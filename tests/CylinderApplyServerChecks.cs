using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using TiaPortalSourceBridge.Indexing;
using TiaPortalSourceBridge.Mcp;
using TiaPortalSourceBridge.Openness;
using TiaPortalSourceBridge.Web;

internal static class CylinderApplyServerChecks
{
    static int checks;
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
    static Dictionary<string, object> D(params object[] pairs) { var d = new Dictionary<string, object>(); for (int i = 0; i < pairs.Length; i += 2) d[(string)pairs[i]] = pairs[i + 1]; return d; }
    static Dictionary<string, object> Reply(object content, bool error = false) { return D("isError", error, "structuredContent", content, "content", new object[] { D("type", "text", "text", "test outcome") }); }
    static Dictionary<string, object> Imported() { return D("ok", true, "compiled", true, "error_count", 0, "warning_count", 2, "journal", "backup/result.json", "items", new object[] { D("block_name", "Host", "status", "imported", "verification", "matched", "backup", "backup/Host.xml") }); }
    static List<Dictionary<string, object>> Items() { return new List<Dictionary<string, object>> { D("xml", "<fixture/>", "group_path", "Cylinder", "expected_content_signature", new string('A', 64)) }; }
    static void Pipeline()
    {
        int writes = 0, saves = 0;
        var result = D("saved", false);
        CylinderWritePipeline.Run(Items(), "fixture.ap17", "Station/CPU", result, (name, args) => {
            Check(CylinderWritePipeline.Text(args, "project_path") == "fixture.ap17" && CylinderWritePipeline.True(args, "confirm"), "explicit project and confirmation on writes");
            if (name == "tia_import_blocks_batch") { writes++; Check(CylinderWritePipeline.Text(args, "plc_name") == "Station/CPU" && CylinderWritePipeline.Text(args, "compile_mode") == "plc", "exact PLC and complete compile"); return Reply(Imported()); }
            saves++; Check(name == "tia_save_project", "only save follows successful batch"); return Reply(D("saved", true));
        }, _ => { });
        Check(writes == 1 && saves == 1 && CylinderWritePipeline.True(result, "saved"), "successful write verified and saved exactly once");
        Check(result.ContainsKey("import_result") && result.ContainsKey("save_result"), "backup, compile and save evidence retained");
        foreach (string failure in new[] { "compile", "readback", "missing", "isError", "partial", "preflight" }) {
            saves = 0; result = D("saved", false); var imported = Imported(); bool rejected = false;
            if (failure == "compile") imported["error_count"] = 1;
            if (failure == "missing") imported.Remove("compiled");
            var row = ((object[])imported["items"])[0] as Dictionary<string, object>;
            if (failure == "readback") row["verification"] = "different";
            if (failure == "partial") row["status"] = "outcome_unknown";
            if (failure == "preflight") { row["status"] = "failed"; imported["ok"] = false; }
            try { CylinderWritePipeline.Run(Items(), "fixture.ap17", "Station/CPU", result, (name, args) => {
                if (name == "tia_save_project") saves++;
                return Reply(imported, failure == "isError");
            }, _ => { }); }
            catch (CylinderReviewException ex) { rejected = true; Check(ex.MayHaveChanged == (failure != "preflight"), "accurate possible-write result: " + failure); }
            Check(rejected && saves == 0 && !CylinderWritePipeline.True(result, "saved") && result.ContainsKey("import_result"), "failed validation never auto-saves and retains journal: " + failure);
        }
        bool saveFailed = false; result = D("saved", false);
        try { CylinderWritePipeline.Run(Items(), "fixture.ap17", "Station/CPU", result, (name, args) => name == "tia_import_blocks_batch" ? Reply(Imported()) : Reply(D("saved", false), true), _ => { }); }
        catch (CylinderReviewException ex) { saveFailed = ex.MayHaveChanged; }
        Check(saveFailed && !CylinderWritePipeline.True(result, "saved") && result.ContainsKey("import_result"), "save failure remains reviewable after successful write");
        writes = 0; bool missingGuard = false; var unsafeItems = Items(); unsafeItems[0].Remove("expected_content_signature");
        try { CylinderWritePipeline.Run(unsafeItems, "fixture.ap17", "Station/CPU", D(), (name, args) => { writes++; return Reply(Imported()); }, _ => { }); }
        catch (InvalidOperationException) { missingGuard = true; }
        Check(missingGuard && writes == 0, "missing optimistic concurrency guard rejected before mutation");
    }
    static IoRow Row(string address) { return new IoRow { id = "same-tag-id", name = "Spare", table = "Tags", group = "", unit = "", area = "I", address = address, data_type = "Bool" }; }
    static void BatchRequests()
    {
        var common = D("project_path", "fixture.ap17", "host", "same-host", "rules", D("i_Wpos", "input"));
        Check(object.ReferenceEquals(WebServer.CylinderRequests(common)[0], common), "single request remains compatible");
        var first = D("cylinder_name", "Clamp", "instance_name", "Z04", "alarm_index", "4", "bindings", D("i_Wpos", "input-1"));
        var second = D("cylinder_name", "Lift", "instance_name", "Z05", "alarm_index", "5", "bindings", D("i_Wpos", "input-2"));
        common["batch"] = new object[] { first, second };
        var rows = WebServer.CylinderRequests(common);
        Check(rows.Count == 2 && rows.All(r => !r.ContainsKey("batch") && (string)r["host"] == "same-host") && (string)rows[1]["instance_name"] == "Z05", "batch inherits shared target and template with individual names");
        Check(!first.ContainsKey("host") && common.ContainsKey("batch"), "batch normalization does not mutate request data");
        foreach (object invalid in new object[] { "not-array", D(), new object[0], new object[] { null }, new object[] { D("host", "different") }, Enumerable.Repeat((object)first, 21).ToArray() }) {
            common["batch"] = invalid; bool rejected = false;
            try { WebServer.CylinderRequests(common); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "malformed or mixed-context batch rejected before admission");
        }
    }
    static void Snapshot()
    {
        var old = new IoSnapshot(); old.rows.Add(Row("%I53.0"));
        var fresh = new IoSnapshot(); fresh.rows.Add(Row("%I53.0"));
        var bindings = D("i_Wpos", "same-tag-id");
        WebServer.SameCylinderSources(old, fresh, _ => "", bindings); Check(true, "unchanged point identity accepted");
        fresh.rows[0].address = "%I63.0"; bool rejected = false;
        try { WebServer.SameCylinderSources(old, fresh, _ => "", bindings); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "same tag id with changed physical address rejected");
        fresh.rows[0].address = "%I53.0"; fresh.rows[0].data_type = "Byte"; rejected = false;
        try { WebServer.SameCylinderSources(old, fresh, _ => "", bindings); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "changed declaration type rejected");
        string xml = "<Document><DocumentInfo><Created>one</Created></DocumentInfo><SW.Blocks.FC ID='1'><AttributeList><Name>Host</Name><Number>1</Number><ProgrammingLanguage>SCL</ProgrammingLanguage></AttributeList></SW.Blocks.FC></Document>";
        Check(XmlBatch.ContentSignature(xml) == XmlBatch.ContentSignature(xml.Replace("one", "two").Replace("ID='1'", "ID='2'")), "export timestamp and document ID do not make draft stale");
        Check(XmlBatch.ContentSignature(xml) != XmlBatch.ContentSignature(xml.Replace("<Number>1", "<Number>2")), "actual block identity change invalidates signature");
        var parsed = XmlBatch.Parse(CylinderWritePipeline.Json().Serialize(new[] { D("xml", xml, "expected_content_signature", XmlBatch.ContentSignature(xml)) }));
        Check(parsed.Single().expected_content_signature == XmlBatch.ContentSignature(xml), "stable precondition survives batch parsing");
        fresh.cylinderBlocks = new[] { new IndexEntry { name = "Host", group = "Cylinders", language = "SCL" } };
        fresh.cylinderProgramsTotal = fresh.cylinderProgramsRead = 1;
        var guard = WebServer.SnapshotCylinderSignatures(fresh, _ => xml);
        string io = CylinderWritePipeline.Text(guard.Single(x => CylinderWritePipeline.Text(x, "kind") == "io"), "signature");
        fresh.rows[0].references.Add(new IoReference { evidence = "volatile read diagnostic" });
        fresh.read_completed_utc = DateTime.UtcNow.ToString("o");
        Check(CylinderWritePipeline.Text(WebServer.SnapshotCylinderSignatures(fresh, _ => xml).Last(), "signature") == io, "read timestamps and reference evidence do not invalidate recovery");
        fresh.rows[0].address = "%I73.0";
        Check(CylinderWritePipeline.Text(WebServer.SnapshotCylinderSignatures(fresh, _ => xml).Last(), "signature") != io, "physical I/O address edits invalidate recovery");
        var nativeOnly = WebServer.SnapshotCylinderSignatures(fresh, _ => xml);
        fresh.rows.Add(new IoRow { name = "OldCylinderInput", address = "%I53.1", mapping_fc = "Mapping", origin = "mapped" });
        string beforeRename = CylinderWritePipeline.Text(WebServer.SnapshotCylinderSignatures(fresh, _ => xml).Last(), "signature");
        fresh.rows.Last().name = "NewCylinderInput";
        Check(CylinderWritePipeline.Text(WebServer.SnapshotCylinderSignatures(fresh, _ => xml).Last(), "signature") == beforeRename, "controlled DB mapping rename is covered by block evidence, not native tag sentinel");
        string generated = xml.Replace("</AttributeList>", "<Comment><Text>Generated title</Text></Comment></AttributeList>");
        var captured = nativeOnly.Select(r => new Dictionary<string, object>(r)).ToList();
        captured[0]["signature"] = XmlBatch.RecoverySignature(generated);
        var receipt = D("import_result", D("items", new[] { D("block_name", "Host", "group_path", "Cylinders", "status", "imported", "after_recovery_signature", XmlBatch.RecoverySignature(generated)) }));
        WebServer.VerifyCylinderPostEvidence(nativeOnly, receipt, captured); Check(true, "same-batch trusted readback permits changed block snapshot");
        captured[0]["signature"] = XmlBatch.RecoverySignature(generated.Replace("Generated title", "Later manual title"));
        rejected = false;
        try { WebServer.VerifyCylinderPostEvidence(nativeOnly, receipt, captured); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "late manual title edit cannot be absorbed into original operation recovery baseline");
        fresh.cylinderProgramsRead = 0; rejected = false;
        try { WebServer.SnapshotCylinderSignatures(fresh, _ => xml); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "incomplete source coverage cannot produce recovery proof");
    }
    static void CachedSnapshot(string project, string plc, string outputRoot)
    {
        var store = new ProjectIndexStore(project, "V17");
        var snapshot = new IoSnapshot { project_path = project, plc_name = plc };
        var tables = store.Select("plc.tags", plc, "", "").ToArray(); snapshot.tables_total = tables.Length;
        foreach (var table in tables) { snapshot.rows.AddRange(IoTable.Parse(store.IoSource(table), table.name, table.group, table.unit_name)); snapshot.tables_read++; }
        snapshot.cylinderBlocks = store.IoBlocks(plc); var coverage = store.IoProgramCoverage(plc);
        snapshot.cylinderProgramsTotal = coverage[0]; snapshot.cylinderProgramsRead = coverage[1];
        IoMapping.Expand(snapshot, snapshot.cylinderBlocks, store.IoSource); IoDeclarations.Add(snapshot, snapshot.cylinderBlocks, store.IoSource);
        var row = snapshot.rows.First(r => IoTable.Address(r.address) == "I53.0");
        WebServer.SameCylinderSources(snapshot, snapshot, store.IoSource, D("i_Wpos", row.id));
        Check(true, "all cached engineering source formats support stable preflight signatures");
        var host = snapshot.cylinderBlocks.Single(b => b.name == "OP275_Cylinder");
        var backup = WebServer.BackupCylinderInstances(Path.Combine(outputRoot, "config.json"), Guid.NewGuid().ToString("N"), snapshot, host, store.IoSource);
        Check(backup.Any(b => CylinderWritePipeline.Text(b, "block_name") == "InstOP275_Cylinder"), "actual cached host instance DB is explicitly backed up");
        Check(backup.All(b => File.ReadAllText(CylinderWritePipeline.Text(b, "path")) == store.IoSource(snapshot.cylinderBlocks.Single(e => e.name == CylinderWritePipeline.Text(b, "block_name")))), "actual cached instance DB XML is preserved verbatim without engineering write");
    }
    static void InstanceBackups(string root)
    {
        Func<string, string, string> xml = (name, parent) => "<Document><SW.Blocks.InstanceDB ID='0'><AttributeList><Name>" + name + "</Name><Number>20</Number><InstanceOfType>FB</InstanceOfType>" + parent + "<Interface><Sections><Section Name='Static'><Member Name='Existing' Datatype='Bool'><StartValue>true</StartValue></Member></Section></Sections></Interface></AttributeList></SW.Blocks.InstanceDB></Document>";
        var host = new IndexEntry { id = "h", name = "Host", number = "10", unit_name = "", language = "LAD" };
        var byName = new IndexEntry { id = "a", name = "Instance", unit_name = "", language = "DB", group = "Cylinders" };
        var byNumber = new IndexEntry { id = "b", name = "NumberInstance", unit_name = "", language = "DB", group = "Cylinders" };
        var unrelated = new IndexEntry { id = "c", name = "Other", unit_name = "", language = "DB" };
        var otherUnit = new IndexEntry { id = "d", name = "UnitInstance", unit_name = "OtherUnit", language = "DB" };
        var code = new Dictionary<string, string> {
            { "a", xml("Instance", "<InstanceOfName>Host</InstanceOfName>") },
            { "b", xml("NumberInstance", "<InstanceOfNumber>10</InstanceOfNumber>") },
            { "c", xml("Other", "<InstanceOfName>OtherHost</InstanceOfName>") },
            { "d", xml("UnitInstance", "<InstanceOfName>Host</InstanceOfName>") }
        };
        var snapshot = new IoSnapshot { project_path = Path.Combine(root, "Fixture.ap17"), plc_name = "Station/CPU", cylinderBlocks = new[] { byName, byNumber, unrelated, otherUnit } };
        string config = Path.Combine(root, "config.json"), job = Guid.NewGuid().ToString("N");
        var backups = WebServer.BackupCylinderInstances(config, job, snapshot, host, entry => code[entry.id]);
        Check(backups.Length == 2 && backups.Select(b => CylinderWritePipeline.Text(b, "block_name")).SequenceEqual(new[] { "Instance", "NumberInstance" }), "backup selects host instances by name or number in same unit only");
        foreach (var backup in backups) {
            string path = CylinderWritePipeline.Text(backup, "path"), name = CylinderWritePipeline.Text(backup, "block_name"), content = File.ReadAllText(path);
            Check(content == code[snapshot.cylinderBlocks.Single(b => b.name == name).id] && content.Contains("<StartValue>true</StartValue>"), "instance declaration and start values copied verbatim");
            Check(CylinderWritePipeline.Text(backup, "hash") == XmlBatch.Hash(content) && BackupRecord.Read(path, content, snapshot.project_path, snapshot.plc_name).Name == name, "instance backup hash and restore metadata match");
            Check(Path.GetFileName(path).Contains("instance-") && !Path.GetFileName(path).Contains(name), "backup filename does not use project-controlled block names");
        }
        Check(File.Exists(Path.Combine(root, "block-backups", "cylinder-" + job, "manifest.json")), "instance backup manifest persisted before writes");
        string blocked = Path.Combine(root, "backup-failure"); Directory.CreateDirectory(blocked); File.WriteAllText(Path.Combine(blocked, "block-backups"), "file prevents backup directory");
        bool failed = false;
        try { WebServer.BackupCylinderInstances(Path.Combine(blocked, "config.json"), Guid.NewGuid().ToString("N"), snapshot, host, entry => code[entry.id]); } catch (IOException) { failed = true; }
        Check(failed, "backup failure propagates before any import can start");
    }
    static Dictionary<string, object> Request(WebServer server, HttpListener listener, string url, string route, string method, object payload, int expected, string origin = null)
    {
        Exception failure = null;
        var handler = new Thread(() => { try { typeof(WebServer).GetMethod("ProcessRequest", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(server, new object[] { listener.GetContext() }); } catch (Exception ex) { failure = ex; } });
        handler.IsBackground = true; handler.Start();
        var req = (HttpWebRequest)WebRequest.Create(url + route); req.Method = method; req.Timeout = 5000;
        if (origin != null) req.Headers["Origin"] = origin;
        if (payload != null) {
            req.ContentType = "application/json"; byte[] bytes = Encoding.UTF8.GetBytes(CylinderWritePipeline.Json().Serialize(payload)); req.ContentLength = bytes.Length;
            using (var stream = req.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
        }
        HttpWebResponse response;
        try { response = (HttpWebResponse)req.GetResponse(); } catch (WebException ex) { response = ex.Response as HttpWebResponse; if (response == null) throw; }
        string body;
        using (response) using (var reader = new StreamReader(response.GetResponseStream())) { Check((int)response.StatusCode == expected, "HTTP " + expected + ": " + method + " " + route); body = reader.ReadToEnd(); }
        Check(handler.Join(5000) && failure == null, "HTTP handler returns without background engineering work");
        return CylinderWritePipeline.Json().Deserialize<Dictionary<string, object>>(body);
    }
    static void HttpBoundary(string root)
    {
        string project = Path.Combine(root, "Fixture.ap17"), config = Path.Combine(root, "config.json");
        File.WriteAllText(project, "offline fixture"); File.WriteAllText(config, CylinderWritePipeline.Json().Serialize(D("tiaPortalVersion", "V17", "projectPath", project, "sourceDirectory", root)));
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); int port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        var server = new WebServer(config, port);
        var listener = (HttpListener)typeof(WebServer).GetField("_listener", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(server);
        string url = "http://127.0.0.1:" + port, id = Guid.NewGuid().ToString("N");
        listener.Start();
        var lanes = (Dictionary<string, ActionProgress>)typeof(ActionRunner).GetField("_lanes", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        try {
            Request(server, listener, url, "/api/io/cylinder-apply", "POST", D(), 403, "https://different.invalid");
            Request(server, listener, url, "/api/io/cylinder-apply", "GET", null, 405);
            Request(server, listener, url, "/api/io/cylinder-apply", "POST", D("padding", new string('x', 262144)), 413);
            Request(server, listener, url, "/api/io/cylinder-apply", "POST", D("batch", new object[0]), 400);
            Request(server, listener, url, "/api/io/cylinder-batch-preview", "POST", D(), 400);
            Request(server, listener, url, "/api/io/cylinder-batch-preview", "GET", null, 405);
            Request(server, listener, url, "/api/io/cylinder-apply", "POST", D(), 400);
            Request(server, listener, url, "/api/io/cylinder-apply", "POST", D("request_id", id), 409);
            Request(server, listener, url, "/api/io/cylinder-apply-status?id=invalid", "GET", null, 404);
            var guard = (ActionTarget)typeof(WebServer).GetField("actionTargets", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(server);
            var body = D("id", "snapshot", "host", "host", "source_hash", "hash", "project_path", project, "plc_name", "Station/CPU", "target_token", guard.Sign(project, "Station/CPU"), "request_id", id);
            Request(server, listener, url, "/api/io/cylinder-apply", "POST", body, 409);
            var jobType = typeof(WebServer).GetNestedType("IoJob", BindingFlags.NonPublic); var job = Activator.CreateInstance(jobType, true);
            Action<string, object> field = (key, value) => jobType.GetField(key).SetValue(job, value);
            field("id", "snapshot"); field("project", project); field("plc", "Station/CPU"); field("state", "completed");
            field("result", new IoSnapshot { project_path = project, plc_name = "Station/CPU", cylinderBlocks = new[] { new IndexEntry { id = "host", name = "Host", language = "LAD", content_hash = "hash" } } });
            var jobs = (IDictionary)typeof(WebServer).GetField("ioJobs", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(server); jobs["snapshot"] = job;
            // An already occupied lane makes this an offline routing test: no TIA
            // method, source read, import or save can start.
            lanes["tia"] = new ActionProgress { status = "running", actionName = "test-fixture" };
            var busy = Request(server, listener, url, "/api/io/cylinder-apply", "POST", body, 409);
            Check(CylinderWritePipeline.Text(busy, "id") == id, "busy admission has recoverable durable receipt");
            var original = Request(server, listener, url, "/api/io/cylinder-apply", "POST", body, 202);
            Check(CylinderWritePipeline.True(original, "resumed"), "same request id returns original outcome without execution");
            body["cylinder_name"] = "changed"; Request(server, listener, url, "/api/io/cylinder-apply", "POST", body, 409);
            var status = Request(server, listener, url, "/api/io/cylinder-apply-status?request_id=" + id, "GET", null, 200);
            Check(CylinderWritePipeline.Text(status, "state") == "failed" && !CylinderWritePipeline.True(status, "write_started"), "lost response recovers preflight failure by request id");
            // Drop only the in-memory receipt to simulate dashboard restart. GET
            // must reconstruct the same result without replaying the mutation.
            ((IDictionary)typeof(WebServer).GetField("cylinderJobs", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(server)).Clear();
            status = Request(server, listener, url, "/api/io/cylinder-apply-status?id=" + id, "GET", null, 200);
            Check(CylinderWritePipeline.Text(status, "state") == "failed", "durable status survives cleared process memory");
            Check(!status.ContainsKey("plan_items") && !status.ContainsKey("source_signatures"), "polling response omits private source artifacts");
            var list = Request(server, listener, url, "/api/io/cylinder-history?limit=1", "GET", null, 200);
            var listed = CylinderWritePipeline.Rows(list, "jobs");
            Check(Convert.ToInt32(list["total"]) == 1 && listed.Length == 1 && CylinderWritePipeline.Text(listed[0], "id") == id, "completed durable operations are discoverable without browser bookmark");
            Check(CylinderWritePipeline.Text(listed[0], "started").Length > 0 && CylinderWritePipeline.Rows(status, "events").Length >= 2, "operation has durable creation time and stage timeline");
            list = Request(server, listener, url, "/api/io/cylinder-history?offset=1&limit=1", "GET", null, 200);
            Check(CylinderWritePipeline.Rows(list, "jobs").Length == 0 && Convert.ToInt32(list["total"]) == 1, "history pagination preserves total");
            list = Request(server, listener, url, "/api/io/cylinder-history?plc_name=Different", "GET", null, 200);
            Check(Convert.ToInt32(list["total"]) == 0, "history can be scoped to exact PLC");
            Request(server, listener, url, "/api/io/cylinder-history", "POST", D(), 405);
            string damaged = Path.Combine(root, "logs", "cylinder-jobs", Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(damaged, "invalid receipt");
            list = Request(server, listener, url, "/api/io/cylinder-history", "GET", null, 200);
            Check(Convert.ToInt32(list["total"]) == 1 && ((ICollection)list["warnings"]).Count == 1, "one damaged receipt does not hide intact history");
        }
        finally { lanes.Remove("tia"); listener.Close(); }
    }
    static void UnknownOutcome(string root)
    {
        var lanes = (Dictionary<string, ActionProgress>)typeof(ActionRunner).GetField("_lanes", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        try {
            Check(ActionRunner.StartEngineeringTask(Path.Combine(root, "config.json"), "fixture.ap17", "Station/CPU", "test-uncertain-cylinder", "test", report => {
                report("transport interrupted after request"); throw new EngineeringOutcomeUnknownException("final result unknown");
            }), "engineering callback starts on free lane");
            Check(SpinWait.SpinUntil(() => ActionRunner.Running.Any(p => p.status == "outcome_unknown"), 5000), "unknown transport result stays distinguishable from failure and success");
            Check(ActionRunner.AnyBlockingRunning && !ActionRunner.StartIoRead(Path.Combine(root, "config.json"), "fixture.ap17", "Station/CPU", _ => null), "unconfirmed mutation blocks follow-up engineering requests");
            string taskId = ActionRunner.Running.Single(p => p.actionName == "test-uncertain-cylinder").task_id;
            Check(SpinWait.SpinUntil(() => ActionRunner.FindTask(taskId).logs.Any(l => l.Contains("final result unknown")), 5000), "unknown task finishes writing its durable execution record");
        }
        finally { lanes.Remove("tia"); }
    }
    static void BatchProgress(string root)
    {
        var server = new WebServer(Path.Combine(root, "config.json"), 18981);
        var type = typeof(WebServer).GetNestedType("CylinderJob", BindingFlags.NonPublic);
        var job = Activator.CreateInstance(type, true);
        string id = Guid.NewGuid().ToString("N"); type.GetField("id").SetValue(job, id);
        string directory = Path.Combine(root, "block-backups", "batch-operation-" + id); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "result.json");
        int reports = 0;
        var observe = typeof(WebServer).GetMethod("ObserveCylinderBatch", BindingFlags.NonPublic | BindingFlags.Instance);
        using ((IDisposable)observe.Invoke(server, new object[] { job, (Action<string>)(_ => Interlocked.Increment(ref reports)) })) {
            File.WriteAllText(path, CylinderWritePipeline.Json().Serialize(D("phase", "compile", "items", new object[0])));
            Check(SpinWait.SpinUntil(() => (string)type.GetField("stage").GetValue(job) == "compile", 5000), "live batch compile stage reaches operation history without another engineering request");
            File.WriteAllText(path, CylinderWritePipeline.Json().Serialize(D("phase", "verify", "items", new object[0])));
            Check(SpinWait.SpinUntil(() => (string)type.GetField("stage").GetValue(job) == "verify", 5000), "live batch readback stage is distinct from importing");
        }
        int stopped = reports;
        File.WriteAllText(path, CylinderWritePipeline.Json().Serialize(D("phase", "complete", "items", new object[0])));
        Thread.Sleep(650);
        Check(reports == stopped, "disposed observer cannot overwrite terminal task status");
    }
    static int Main(string[] args)
    {
        string root = Path.Combine(Path.GetTempPath(), "tia-cylinder-server-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { Pipeline(); BatchRequests(); Snapshot(); InstanceBackups(root); HttpBoundary(root); UnknownOutcome(root); BatchProgress(root); if (args.Length == 2) CachedSnapshot(args[0], args[1], root); Console.WriteLine("PASS: " + checks + " cylinder apply server checks; no TIA connection or PLC writes."); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message); return 1; }
        finally { if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true); }
    }
}
