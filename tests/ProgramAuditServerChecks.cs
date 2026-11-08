using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Xml;
using TiaPortalSourceBridge.Indexing;
using TiaPortalSourceBridge.Web;

internal static class ProgramAuditServerChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int checks;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
    private static Dictionary<string, object> D(params object[] pairs) { var value = new Dictionary<string, object>(); for (int i = 0; i < pairs.Length; i += 2) value[(string)pairs[i]] = pairs[i + 1]; return value; }
    private static string Text(Dictionary<string, object> value, string name) { return CylinderWritePipeline.Text(value, name); }
    private static object Field(object instance, string name) { return instance.GetType().GetField(name, Private).GetValue(instance); }
    private static void Set(object instance, string name, object value) { instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).SetValue(instance, value); }
    private static object Job(string type, params object[] fields) { var result = Activator.CreateInstance(typeof(WebServer).GetNestedType(type, BindingFlags.NonPublic), true); for (int i = 0; i < fields.Length; i += 2) Set(result, (string)fields[i], fields[i + 1]); return result; }
    private static string Network(string sample) { return "<SW.Blocks.CompileUnit ID='10'><AttributeList><NetworkSource><FlgNet xmlns='http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v4'><Parts><Access UId='1' Scope='GlobalVariable'><Symbol><Component Name='Sensor'/></Symbol></Access><Call UId='2'><CallInfo Name='Cylinder.FB' BlockType='FB'><Instance Scope='LocalVariable'><Component Name='" + sample + "'/></Instance><Parameter Name='Sense' Section='Input' Type='Bool'/><Parameter Name='Valve' Section='Output' Type='Bool'/></CallInfo></Call></Parts><Wires><Wire UId='3'><IdentCon UId='1'/><NameCon UId='2' Name='Sense'/></Wire></Wires></FlgNet></NetworkSource></AttributeList></SW.Blocks.CompileUnit>"; }
    private static string Host() { return "<Document><SW.Blocks.FB ID='0'><AttributeList><Name>Host</Name><Interface><Sections xmlns='http://www.siemens.com/automation/Openness/SW/Interface/v5'><Section Name='Static'><Member Name='Z01' Datatype='&quot;Cylinder.FB&quot;'/><Member Name='Z02' Datatype='&quot;Cylinder.FB&quot;'/></Section></Sections></Interface></AttributeList><ObjectList>" + Network("Z01") + Network("Z02") + "</ObjectList></SW.Blocks.FB></Document>"; }
    private static string Definition() { return "<Document><SW.Blocks.FB><AttributeList><Name>Cylinder.FB</Name><Interface><Sections xmlns='http://www.siemens.com/automation/Openness/SW/Interface/v5'><Section Name='Input'><Member Name='Sense' Datatype='Bool'/></Section><Section Name='Output'><Member Name='Valve' Datatype='Bool'/></Section></Sections></Interface></AttributeList></SW.Blocks.FB></Document>"; }
    private sealed class Fixture
    {
        internal string Root, Project, Config, HostXml = Host(), FbXml = Definition();
        internal IoSnapshot Snapshot;
        internal IndexEntry HostEntry, FbEntry;
        internal Fixture(string root)
        {
            Root = root; Directory.CreateDirectory(root); Project = Path.Combine(root, "Fixture.ap17"); Config = Path.Combine(root, "config.json");
            File.WriteAllText(Project, "offline audit fixture");
            File.WriteAllText(Config, CylinderWritePipeline.Json().Serialize(D("tiaPortalVersion", "V17", "projectPath", Project, "sourceDirectory", root)));
            HostEntry = new IndexEntry { id = "host", name = "Host", group = "Cylinder", language = "LAD", content_hash = IndexJson.Hash(HostXml) };
            FbEntry = new IndexEntry { id = "fb", name = "Cylinder.FB", group = "Library", language = "LAD", content_hash = IndexJson.Hash(FbXml) };
            Snapshot = new IoSnapshot { project_path = Project, plc_name = "Station/CPU", cylinderBlocks = new[] { HostEntry, FbEntry },
                read_started_utc = "2026-11-08T04:00:00Z", read_completed_utc = "2026-11-08T04:01:00Z", cylinderProgramsTotal = 2, cylinderProgramsRead = 2,
                tables_total = 1, tables_read = 1, reference_coverage = "固定的本地测试源码范围" };
            Snapshot.rows.Add(new IoRow { id = "word", name = "MemoryWord", table = "Tags", group = "", unit = "", area = "M", address = "%MW10", data_type = "Word" });
            Snapshot.rows.Add(new IoRow { id = "bit", name = "MemoryBit", table = "Tags", group = "", unit = "", area = "M", address = "%M10.3", data_type = "Bool" });
            var store = new ProjectIndexStore(Project, "V17"); string source = Path.Combine(store.Root, "source"); Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, HostEntry.content_hash + ".xml"), HostXml); File.WriteAllText(Path.Combine(source, FbEntry.content_hash + ".xml"), FbXml);
        }
        internal string Source(IndexEntry entry) { return entry.id == "host" ? HostXml : FbXml; }
        internal CylinderTemplateContext Context(string sample = "Z01", int network = 1) { return CylinderTemplateStore.Context(Snapshot, HostEntry, Source, sample, network); }
        internal Dictionary<string, object> Save(string sample = "Z01", int network = 1, string senseRole = "input") { var c = Context(sample, network); return CylinderTemplateStore.Save(Root, c, c.sample_fingerprint, D("Sense", senseRole, "Valve", "output"), true); }
    }
    private static void Templates(string root)
    {
        var f = new Fixture(root); var warnings = new List<string>();
        Func<IList<AuditTemplate>> load = () => { warnings.Clear(); return WebServer.AuditTemplates(root, f.Snapshot, f.Source, warnings); };
        Check(load().Count == 0 && warnings.Count == 0, "missing templates do not invent required parameters");
        var record = f.Save(); string path = CylinderTemplateStore.PathFor(root, f.Context()); string original = File.ReadAllText(path);
        var valid = load();
        Check(valid.Count == 1 && valid[0].host_id == "host" && valid[0].fb == "Cylinder.FB" && valid[0].parameters.SequenceEqual(new[] { "Sense", "Valve" }) && warnings.Count == 0, "reviewed exact-project template resolves its current FB and required pins");
        f.Save(senseRole: "open"); Check(load().Single().parameters.SequenceEqual(new[] { "Valve" }), "deliberately open pin is excluded from required connections");
        File.WriteAllText(path, original);
        foreach (string key in new[] { "project_path", "plc_name" }) {
            var changed = new Dictionary<string, object>(record); changed[key] = key == "project_path" ? Path.Combine(root, "Foreign.ap17") : "Other/CPU";
            File.WriteAllText(path, CylinderWritePipeline.Json().Serialize(changed));
            Check(load().Count == 0, "foreign target template excluded: " + key); File.WriteAllText(path, original);
        }
        f.HostXml = f.HostXml.Replace("Name='Sensor'", "Name='ChangedSensor'");
        Check(load().Count == 0 && warnings.Any(w => w.Contains("已变化")), "stale sample binding skipped with actionable warning"); f.HostXml = Host();
        f.FbXml = f.FbXml.Replace("Name='Sense' Datatype='Bool'", "Name='Sense' Datatype='Int'");
        Check(load().Count == 0 && warnings.Any(w => w.Contains("已变化")), "changed FB interface skipped with warning"); f.FbXml = Definition();
        record["rules"] = D("Valve", "output"); File.WriteAllText(path, CylinderWritePipeline.Json().Serialize(record));
        Check(load().Count == 0 && warnings.Any(w => w.Contains("规则不完整")), "incomplete saved role map cannot create audit requirements"); File.WriteAllText(path, original);
        string extra = Path.Combine(Path.GetDirectoryName(path), "invalid.json");
        File.WriteAllText(extra, original); Check(load().Count == 1 && warnings.Any(w => w.Contains("存储身份不一致")), "misnamed copied template is rejected while intact template remains");
        File.WriteAllText(extra, "{"); Check(load().Count == 1 && warnings.Count == 1, "malformed template does not discard other valid templates");
        File.WriteAllText(extra, new string('x', 65537)); Check(load().Count == 1 && warnings.Any(w => w.Contains("大小上限")), "oversized template is bounded before parsing");
        File.Delete(extra);
        f.Save("Z02", 2, "open"); Check(load().Count == 0 && warnings.Any(w => w.Contains("不同连接要求")), "conflicting requirements for same host and FB are not guessed");
        f.Save("Z02", 2); Check(load().Count == 1 && warnings.Count == 0, "matching templates consolidate without duplicate checks");
        Check(File.ReadAllText(f.Project) == "offline audit fixture", "template loading never changes engineering project file");
    }
    private sealed class HttpReply
    {
        internal byte[] Bytes; internal string ContentType, Disposition, Cache, Cors;
        internal Dictionary<string, object> Json { get { return CylinderWritePipeline.Json().Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(Bytes)); } }
    }
    private static HttpReply Request(WebServer server, HttpListener listener, string url, string route, string method, object payload, int expected, string origin = null, bool raw = false, bool chunked = false)
    {
        Exception failure = null;
        var handler = new Thread(() => { try { typeof(WebServer).GetMethod("ProcessRequest", Private).Invoke(server, new object[] { listener.GetContext() }); } catch (Exception ex) { failure = ex; } });
        handler.IsBackground = true; handler.Start();
        var request = (HttpWebRequest)WebRequest.Create(url + route); request.Method = method; request.Timeout = 5000; request.KeepAlive = false;
        if (origin != null) request.Headers["Origin"] = origin;
        if (payload != null) {
            byte[] body = Encoding.UTF8.GetBytes(raw ? (string)payload : CylinderWritePipeline.Json().Serialize(payload)); request.ContentType = "application/json";
            if (chunked) request.SendChunked = true; else request.ContentLength = body.Length;
            using (var stream = request.GetRequestStream()) stream.Write(body, 0, body.Length);
        }
        HttpWebResponse response;
        try { response = (HttpWebResponse)request.GetResponse(); } catch (WebException ex) { response = ex.Response as HttpWebResponse; if (response == null) throw; }
        HttpReply reply;
        using (response) using (var memory = new MemoryStream()) {
            response.GetResponseStream().CopyTo(memory); byte[] bytes = memory.ToArray();
            Check((int)response.StatusCode == expected, "HTTP " + expected + " for " + method + " " + route + "; actual " + (int)response.StatusCode + " " + (expected == 200 && route.Contains("audit-export") ? "xlsx" : Encoding.UTF8.GetString(bytes)));
            reply = new HttpReply { Bytes = bytes, ContentType = response.ContentType, Disposition = response.Headers["Content-Disposition"], Cache = response.Headers["Cache-Control"], Cors = response.Headers["Access-Control-Allow-Origin"] };
        }
        Check(handler.Join(5000) && failure == null, "HTTP handler completes: " + route);
        Check(string.IsNullOrEmpty(reply.Cors), "audit endpoints never expose wildcard CORS");
        return reply;
    }
    private static Dictionary<string, object> Await(WebServer server, HttpListener listener, string url, string id)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew(); Dictionary<string, object> status;
        do {
            status = Request(server, listener, url, "/api/io/audit-status?id=" + id, "GET", null, 200).Json;
            if (Text(status, "state") != "running") return status;
            Thread.Sleep(10);
        } while (watch.ElapsedMilliseconds < 5000);
        throw new Exception("asynchronous audit did not finish within test deadline");
    }
    private static bool LogsFinished(string root, int count)
    {
        string directory = Path.Combine(root, "logs", "execution");
        return count == 0 || (Directory.Exists(directory) && Directory.GetFiles(directory, "*.done").Length == count &&
            Directory.GetFiles(directory, "*.active").Length == 0 && Directory.GetFiles(directory, "*.tmp").Length == 0);
    }
    private static void Drain(WebServer server, string root, IList<string> ids)
    {
        if (ids.Count == 0) return;
        var watch = System.Diagnostics.Stopwatch.StartNew(); bool pending;
        do {
            lock (Field(server, "auditGate")) {
                var jobs = (IDictionary)Field(server, "auditJobs");
                pending = ids.Any(id => jobs.Contains(id) && (string)jobs[id].GetType().GetField("state").GetValue(jobs[id]) == "running");
            }
            if (!pending && LogsFinished(root, ids.Count)) return;
            Thread.Sleep(10);
        } while (watch.ElapsedMilliseconds < 5000);
        throw new Exception("real audit workers or execution-log finalization still pending; retaining fixtures");
    }
    private static void Http(string root)
    {
        var f = new Fixture(root); f.Save();
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); int port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        var server = new WebServer(f.Config, port); var listener = (HttpListener)Field(server, "_listener"); string url = "http://127.0.0.1:" + port;
        var guard = (ActionTarget)Field(server, "actionTargets"); var ioJobs = (IDictionary)Field(server, "ioJobs"); var auditJobs = (IDictionary)Field(server, "auditJobs");
        var consumed = (HashSet<string>)Field(server, "consumedCylinderSnapshots"); string snapshotId = "offline-snapshot";
        object snapshotJob = Job("IoJob", "id", snapshotId, "project", f.Project, "plc", f.Snapshot.plc_name, "state", "completed", "cached", true, "result", f.Snapshot);
        ioJobs[snapshotId] = snapshotJob;
        var body = D("id", snapshotId, "project_path", f.Project, "plc_name", f.Snapshot.plc_name, "target_token", guard.Sign(f.Project, f.Snapshot.plc_name));
        var lanes = (Dictionary<string, ActionProgress>)typeof(ActionRunner).GetField("_lanes", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        var occupied = new ActionProgress { status = "running", actionName = "offline-sentinel" }; lanes["tia"] = occupied;
        string before = CylinderWritePipeline.Json().Serialize(f.Snapshot); var sourceBefore = Directory.GetFiles(new ProjectIndexStore(f.Project, "V17").Root, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllText);
        var admitted = new List<string>(); listener.Start();
        try {
            Request(server, listener, url, "/api/io/audit-run", "POST", body, 403, "https://foreign.invalid");
            Request(server, listener, url, "/api/io/audit-status?id=invalid", "GET", null, 403, "null");
            Request(server, listener, url, "/api/io/audit-export?id=invalid", "GET", null, 403, "http://127.0.0.1:" + (port + 1));
            Request(server, listener, url, "/api/io/audit-run", "GET", null, 405);
            Request(server, listener, url, "/api/io/audit-status", "POST", D(), 405);
            Request(server, listener, url, "/api/io/audit-export", "POST", D(), 405);
            Request(server, listener, url, "/api/io/audit-run", "POST", D("padding", new string('x', 16385)), 413);
            Request(server, listener, url, "/api/io/audit-run", "POST", "{}", 413, raw: true, chunked: true);
            foreach (string invalid in new[] { "", "{", "null", "[]", "123", "true", "\"text\"" }) Request(server, listener, url, "/api/io/audit-run", "POST", invalid, 400, raw: true);
            Request(server, listener, url, "/api/io/audit-run", "POST", D(), 409);
            foreach (string key in new[] { "target_token", "id", "plc_name", "project_path" }) {
                var invalid = new Dictionary<string, object>(body); invalid[key] = "invalid";
                Request(server, listener, url, "/api/io/audit-run", "POST", invalid, 409);
            }
            var foreignPlc = new Dictionary<string, object>(body); foreignPlc["plc_name"] = "Other/CPU"; foreignPlc["target_token"] = guard.Sign(f.Project, "Other/CPU");
            Request(server, listener, url, "/api/io/audit-run", "POST", foreignPlc, 409);
            string otherProject = Path.Combine(root, "Other.ap17"); File.WriteAllText(otherProject, "second offline fixture");
            var foreignProject = new Dictionary<string, object>(body); foreignProject["project_path"] = otherProject; foreignProject["target_token"] = guard.Sign(otherProject, f.Snapshot.plc_name);
            Request(server, listener, url, "/api/io/audit-run", "POST", foreignProject, 409);
            Set(snapshotJob, "state", "running"); Request(server, listener, url, "/api/io/audit-run", "POST", body, 409); Set(snapshotJob, "state", "completed");
            Set(snapshotJob, "result", null); Request(server, listener, url, "/api/io/audit-run", "POST", body, 409); Set(snapshotJob, "result", f.Snapshot);
            consumed.Add(snapshotId); Request(server, listener, url, "/api/io/audit-run", "POST", body, 409); consumed.Remove(snapshotId);
            Set(server, "shutdownRequested", true); Request(server, listener, url, "/api/io/audit-run", "POST", body, 503); Set(server, "shutdownRequested", false);
            Request(server, listener, url, "/api/io/audit-status?id=invalid", "GET", null, 404);
            Request(server, listener, url, "/api/io/audit-export?id=" + Guid.NewGuid().ToString("N"), "GET", null, 404);
            string injected = Guid.NewGuid().ToString("N"); object pending = Job("AuditJob", "id", injected, "state", "running"); auditJobs[injected] = pending;
            Request(server, listener, url, "/api/io/audit-export?id=" + injected, "GET", null, 409);
            Request(server, listener, url, "/api/io/audit-run", "POST", body, 409);
            Set(pending, "state", "failed"); Request(server, listener, url, "/api/io/audit-export?id=" + injected, "GET", null, 409); auditJobs.Remove(injected);
            Check(auditJobs.Count == 0 && consumed.Count == 0 && object.ReferenceEquals(lanes["tia"], occupied), "rejected requests create no jobs and consume neither snapshot nor engineering lane");
            var started = Request(server, listener, url, "/api/io/audit-run", "POST", body, 202, url); string id = Text(started.Json, "id");
            admitted.Add(id);
            Check(id.Length == 32 && started.Cache == "no-store", "same-origin request returns asynchronous private report id");
            var finished = Await(server, listener, url, id);
            Check(Text(finished, "state") == "completed" && Text(finished, "project_path") == f.Project && Text(finished, "plc_name") == f.Snapshot.plc_name && Text(finished, "snapshot_id") == snapshotId, "audit completes using exact admitted snapshot identity");
            Check(LogsFinished(root, admitted.Count), "completed audit has already finalized its execution log without active or temporary files");
            var report = (Dictionary<string, object>)finished["result"]; var findings = CylinderWritePipeline.Rows(report, "findings"); var coverage = (Dictionary<string, object>)report["coverage"];
            Check(findings.Any(r => Text(r, "rule") == "address_overlap") && findings.Any(r => Text(r, "rule") == "template_pin"), "HTTP analysis uses both supplied native tags and local template/source content");
            Check(Text(coverage, "source_mode") == "local_cache" && Text(coverage, "source_note") == f.Snapshot.reference_coverage && Convert.ToInt32(coverage["templates"]) == 1, "report preserves cache origin and exact template coverage");
            var graph = (Dictionary<string, object>)report["call_graph"];
            var calls = CylinderWritePipeline.Rows(graph, "calls");
            Check(Convert.ToInt32(report["schema"]) == 2 && CylinderWritePipeline.Rows(graph, "nodes").Length == 2 && calls.Length == 2,
                "HTTP report exposes full call graph for the same source snapshot");
            Check(calls.All(c => Text(c, "caller_id") == "host" && Text(c, "callee_id") == "fb" && Text(c, "resolution") == "resolved"),
                "call graph keeps exact caller and target identities in API results");
            Check(Convert.ToBoolean(graph["partial"]) && CylinderWritePipeline.Rows(graph, "nodes").All(n => Text(n, "status") != "no_ob_path"),
                "snapshot without an OB is explicitly partial and does not classify unreachable code");
            var exported = Request(server, listener, url, "/api/io/audit-export?id=" + id + "&limit=1&rule=missing", "GET", null, 200, url);
            Check(exported.ContentType == "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" && exported.Disposition.Contains(id + ".xlsx") && exported.Cache == "no-store", "download has Excel MIME, stable report filename and no-cache policy");
            using (var zip = new ZipArchive(new MemoryStream(exported.Bytes), ZipArchiveMode.Read)) {
                var doc = new XmlDocument { XmlResolver = null }; using (var reader = zip.GetEntry("xl/worksheets/sheet2.xml").Open()) doc.Load(reader);
                Check(doc.SelectNodes("//*[local-name()='row']").Count == findings.Length + 1, "download includes whole immutable report despite page/filter query fields");
                Check(zip.GetEntry("xl/worksheets/sheet3.xml") != null, "download contains detailed evidence worksheet");
                Check(zip.GetEntry("xl/worksheets/sheet4.xml") != null && zip.GetEntry("xl/worksheets/sheet5.xml") != null,
                    "new report download includes call relationships and OB path sheets");
                using (var reader = zip.GetEntry("xl/worksheets/sheet4.xml").Open()) doc.Load(reader);
                Check(doc.SelectNodes("//*[local-name()='row']").Count == calls.Length + 1,
                    "export includes all call sites independently of UI filtering");
            }
            string secondId = Text(Request(server, listener, url, "/api/io/audit-run", "POST", body, 202).Json, "id");
            admitted.Add(secondId);
            Check(secondId != id && Text(Await(server, listener, url, secondId), "state") == "completed", "completed audit can be repeated on same still-valid snapshot");
            Check(LogsFinished(root, admitted.Count), "repeated completion also waits for all execution-log file operations");
            Check(consumed.Count == 0 && object.ReferenceEquals(ioJobs[snapshotId], snapshotJob) && CylinderWritePipeline.Json().Serialize(f.Snapshot) == before, "read-only audit does not consume or mutate source snapshot");
            Check(lanes.Count == 1 && object.ReferenceEquals(lanes["tia"], occupied), "audit runs while engineering lane is busy and leaves it untouched");
            Check(File.ReadAllText(f.Project) == "offline audit fixture" && sourceBefore.All(p => File.ReadAllText(p.Key) == p.Value), "audit never imports, saves or rewrites engineering/source files");
            Check(!Directory.Exists(Path.Combine(root, "block-backups")) && !Directory.Exists(Path.Combine(root, "logs", "cylinder-jobs")), "audit creates no write receipts or block-backup workflow");
        } finally { try { Drain(server, root, admitted); } finally { lanes.Remove("tia"); listener.Close(); } }
    }
    private static int Main(string[] args)
    {
        string basePath = Path.GetFullPath(args.Length == 0 ? Path.GetTempPath() : args[0]);
        string root = Path.GetFullPath(Path.Combine(basePath, "pa-" + Guid.NewGuid().ToString("N").Substring(0, 12)));
        try {
            var sizeReport = new AuditReport { call_graph = new AuditCallGraph() };
            WebServer.ValidateAuditResponse(sizeReport, 4096);
            sizeReport.call_graph.calls.Add(new AuditCallSite { callee_name = new string('x', 8192) });
            bool oversized = false;
            try { WebServer.ValidateAuditResponse(sizeReport, 4096); }
            catch (InvalidOperationException ex) { oversized = ex.Message.Contains("无法完整展示"); }
            Check(oversized && sizeReport.call_graph.calls.Single().callee_name.Length == 8192,
                "oversized call graph cannot be marked completed with an unreadable status or silently shortened evidence");
            Templates(Path.Combine(root, "templates")); Http(Path.Combine(root, "http"));
            if (!root.StartsWith(basePath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(root).StartsWith("pa-", StringComparison.Ordinal)) throw new Exception("unexpected fixture cleanup path");
            if (Directory.Exists(root)) Directory.Delete(root, true);
            Console.WriteLine("PASS: " + checks + " program audit server/template checks; no TIA connection or engineering changes."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.ToString()); Console.Error.WriteLine("Retained offline fixtures: " + root); return 1; }
    }
}
