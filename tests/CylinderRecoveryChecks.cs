using System;
using System.Collections.Generic;
using System.Collections;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using TiaPortalSourceBridge.Mcp;
using TiaPortalSourceBridge.Web;

internal static class CylinderRecoveryChecks
{
    private static int checks;
    private static Dictionary<string, object> D(params object[] pairs)
    { var result = new Dictionary<string, object>(); for (int i = 0; i < pairs.Length; i += 2) result[(string)pairs[i]] = pairs[i + 1]; return result; }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
    private static string Xml(string name, int number, bool changed)
    { return "<Document><SW.Blocks.FC><AttributeList><Name>" + name + "</Name><Number>" + number + "</Number><ProgrammingLanguage>SCL</ProgrammingLanguage><Interface><Sections><Section Name='Static'><Member Name='Flag' Datatype='Bool'><StartValue>" + (changed ? "true" : "false") + "</StartValue></Member></Section></Sections></Interface></AttributeList></SW.Blocks.FC></Document>"; }
    private static Dictionary<string, object> Item(string xml)
    { return D("xml", xml, "group_path", "Station", "expected_hash", "volatile-export-hash", "expected_content_signature", XmlBatch.ContentSignature(xml)); }
    private static List<Dictionary<string, object>> Snap(params string[] xml)
    {
        var result = xml.Select(s => D("kind", "block", "block_name", CylinderRecoveryPolicy.Name(s), "group_path", "Station", "signature", XmlBatch.RecoverySignature(s))).ToList();
        result.Add(D("kind", "io", "block_name", "@io", "group_path", "", "signature", XmlBatch.Hash("I53.0=spare")));
        return result;
    }
    private static List<Dictionary<string, object>> Clone(List<Dictionary<string, object>> rows)
    { return rows.Select(r => new Dictionary<string, object>(r)).ToList(); }
    private static void Tests()
    {
        string map = Xml("Map", 1, false), mapped = Xml("Map", 1, true), host = Xml("Host", 2, false), generated = Xml("Host", 2, true),
            instance = Xml("Instance", 3, false), compiled = Xml("Instance", 3, true), other = Xml("Other", 4, false);
        var plan = new List<Dictionary<string, object>> { Item(mapped), Item(generated) };
        var backup = new List<Dictionary<string, object>> { Item(map), Item(host), Item(instance) };
        var before = Snap(map, host, instance, other);
        var partial = Snap(mapped, host, instance, other);
        var result = CylinderRecoveryPolicy.Evaluate(plan, before, partial, partial, backup);
        Check(result.CanContinue && result.CanRestore, "partial import offers checked continuation and restoration");
        Check(result.ContinueItems.Count == 1 && CylinderRecoveryPolicy.Name((string)result.ContinueItems[0]["xml"]) == "Host", "continue skips already applied mapping");
        Check(result.RestoreItems.Count == 1 && CylinderRecoveryPolicy.Name((string)result.RestoreItems[0]["xml"]) == "Map", "restore only changes objects altered by original operation");
        Check(!result.ContinueItems[0].ContainsKey("expected_hash") && !result.ContinueItems[0].ContainsKey("expected_content_signature") && (string)result.ContinueItems[0]["expected_recovery_signature"] == XmlBatch.RecoverySignature(host), "writes use fresh recovery concurrency precondition including comments");
        Check(plan[0].ContainsKey("expected_hash"), "planner does not mutate original durable plan");

        var after = Snap(mapped, generated, compiled, other);
        result = CylinderRecoveryPolicy.Evaluate(plan, before, after, after, backup);
        Check(result.CanContinue && result.ContinueItems.Count == 0, "save failure needs no repeated block import");
        Check(result.CanRestore && result.RestoreItems.Count == 3, "restoration includes compiled instance DB");
        Check(CylinderRecoveryPolicy.Name((string)result.RestoreItems.Last()["xml"]) == "Instance", "host precedes instance DB in restoration order");

        var changed = Clone(after); changed[0]["signature"] = XmlBatch.Hash("later operator change");
        result = CylinderRecoveryPolicy.Evaluate(plan, before, after, changed, backup);
        Check(!result.CanContinue && !result.CanRestore && result.Items.Any(i => (string)i["status"] == "conflict"), "later program edit disables both writes with visible difference");
        changed = Clone(after); changed[2]["signature"] = XmlBatch.Hash("later instance value declaration");
        result = CylinderRecoveryPolicy.Evaluate(plan, before, after, changed, backup);
        Check(!result.CanContinue && !result.CanRestore, "later instance DB edit cannot be overwritten");
        changed = Clone(after); changed.Last()["signature"] = XmlBatch.Hash("new address mapping");
        result = CylinderRecoveryPolicy.Evaluate(plan, before, after, changed, backup);
        Check(!result.CanContinue && !result.CanRestore, "later I/O declaration change blocks recovery");
        result = CylinderRecoveryPolicy.Evaluate(plan, before, changed, changed, backup);
        Check(!result.CanContinue && !result.CanRestore, "native I/O declaration change during write cannot become an accepted recovery baseline");
        changed = Clone(after); changed.Add(D("kind", "block", "block_name", "New", "group_path", "Station", "signature", XmlBatch.Hash("new")));
        result = CylinderRecoveryPolicy.Evaluate(plan, before, after, changed, backup);
        Check(!result.CanContinue && !result.CanRestore, "new object after check blocks recovery");

        changed = Clone(after); changed[3]["signature"] = XmlBatch.Hash("change during operation in unrelated block");
        result = CylinderRecoveryPolicy.Evaluate(plan, before, changed, changed, backup);
        Check(!result.CanContinue && !result.CanRestore, "recorded post snapshot cannot legitimize unrelated operation-time change");
        var incompleteBackup = backup.Take(1).ToList();
        result = CylinderRecoveryPolicy.Evaluate(plan, before, partial, partial, incompleteBackup);
        Check(!result.CanContinue && !result.CanRestore && result.Reason.Contains("备份不完整"), "complete backup is required even for continuation");
        var badBackup = new List<Dictionary<string, object>> { Item(mapped), Item(host), Item(instance) };
        result = CylinderRecoveryPolicy.Evaluate(plan, before, partial, partial, badBackup);
        Check(!result.CanRestore && result.Reason.Contains("备份"), "wrong backup fingerprint rejected");
        result = CylinderRecoveryPolicy.Evaluate(plan, before, null, partial, backup);
        Check(!result.CanContinue && !result.CanRestore, "unknown write outcome without post evidence never enables replay");
        result = CylinderRecoveryPolicy.Evaluate(null, before, partial, partial, backup);
        Check(!result.CanContinue && !result.CanRestore && result.Reason.Contains("历史记录"), "legacy job has readable explanation instead of fabricated recoverability");
        result = CylinderRecoveryPolicy.Evaluate(plan, before.Where(r => (string)r["kind"] != "io").ToList(), after.Where(r => (string)r["kind"] != "io").ToList(), after.Where(r => (string)r["kind"] != "io").ToList(), backup);
        Check(!result.CanContinue && !result.CanRestore && result.Reason.Contains("I/O"), "pre-I/O-evidence records fail closed");
        result = CylinderRecoveryPolicy.Evaluate(plan, before, before, before, backup);
        Check(result.CanContinue && !result.CanRestore, "unmodified engineering state needs no restoration");
        var weird = Snap(mapped, Xml("Host", 2, false).Replace("Bool", "Int"), instance, other);
        result = CylinderRecoveryPolicy.Evaluate(plan, before, weird, weird, backup);
        Check(!result.CanContinue && result.CanRestore, "partially changed host cannot be silently replaced with new plan, but complete verified backup can restore it");
        Check(CylinderRecoveryPolicy.Same(after, Clone(after)) && !CylinderRecoveryPolicy.Same(after, partial), "execution rechecks exact same complete snapshot as approved check");
        string withComment = mapped.Replace("</AttributeList>", "</AttributeList><ObjectList><MultilingualText CompositionName='Title'><ObjectList><MultilingualTextItem><AttributeList><Culture>zh-CN</Culture><Text>操作员修改的标题</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText></ObjectList>");
        changed = Snap(withComment, generated, compiled, other);
        result = CylinderRecoveryPolicy.Evaluate(plan, before, after, changed, backup);
        Check(!result.CanContinue && !result.CanRestore, "operator title edits are not overwritten even when code is unchanged");
    }
    private static Dictionary<string, object> Reply(object data, bool error = false)
    { return D("isError", error, "structuredContent", data, "content", new object[] { D("text", "fixture result") }); }
    private static void Pipeline()
    {
        var targets = new List<Dictionary<string, object>> { Item(Xml("Host", 2, true)) };
        var empty = new List<Dictionary<string, object>>();
        var calls = new List<string>(); int readbacks = 0;
        var result = D("saved", false);
        CylinderRecoveryWritePipeline.Run(empty, targets, "fixture.ap17", "CPU", result, D("warning_count", 6), true, (name, args) => {
            calls.Add(name); Check(CylinderWritePipeline.Text(args, "project_path") == "fixture.ap17" && CylinderWritePipeline.True(args, "confirm"), "save is explicitly scoped and confirmed");
            return Reply(D("saved", true));
        }, () => readbacks++, _ => { });
        Check(calls.SequenceEqual(new[] { "tia_save_project" }) && readbacks == 0 && CylinderWritePipeline.True(result, "saved"), "verified save-only retry performs exactly one save and no import or compile");
        Check(CylinderWritePipeline.Text(CylinderWritePipeline.Map(result["import_result"]), "warning_count") == "6", "save-only retry preserves compile warnings");
        calls.Clear(); result = D("saved", false);
        CylinderRecoveryWritePipeline.Run(empty, targets, "fixture.ap17", "CPU", result, null, false, (name, args) => {
            calls.Add(name); return name == "tia_compile_plc" ? Reply(D("error_count", 0, "warning_count", 2)) : Reply(D("saved", true));
        }, () => readbacks++, _ => { });
        Check(calls.SequenceEqual(new[] { "tia_compile_plc", "tia_save_project" }) && readbacks == 1, "when compile evidence is absent, compile and verify precede save");
        foreach (string failure in new[] { "compile", "readback", "save" }) {
            calls.Clear(); result = D("saved", false); bool failed = false;
            try {
                CylinderRecoveryWritePipeline.Run(empty, targets, "fixture.ap17", "CPU", result, null, false, (name, args) => {
                    calls.Add(name); return name == "tia_compile_plc" ? Reply(D("error_count", failure == "compile" ? 1 : 0)) : Reply(D("saved", false), true);
                }, () => { if (failure == "readback") throw new CylinderReviewException("readback mismatch", true); }, _ => { });
            } catch (CylinderReviewException) { failed = true; }
            Check(failed && !CylinderWritePipeline.True(result, "saved"), "failed " + failure + " cannot claim saved");
            if (failure != "save") Check(!calls.Contains("tia_save_project"), "failed " + failure + " stops before save");
        }
    }
    private static Dictionary<string, object> Request(WebServer server, HttpListener listener, string url, string path, string method,
        Dictionary<string, object> body, int expected, string origin = null)
    {
        var accept = listener.GetContextAsync();
        var handled = accept.ContinueWith(task => {
            var context = task.Result;
            try { typeof(WebServer).GetMethod("HandleIo", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(server, new object[] { context, path }); }
            finally { context.Response.Close(); }
        });
        var request = (HttpWebRequest)WebRequest.Create(url + path); request.Method = method; request.Proxy = null;
        if (origin != null) request.Headers["Origin"] = origin;
        if (body != null) {
            byte[] bytes = Encoding.UTF8.GetBytes(CylinderWritePipeline.Json().Serialize(body)); request.ContentType = "application/json";
            request.ContentLength = bytes.Length; using (var stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
        }
        HttpWebResponse response;
        try { response = (HttpWebResponse)request.GetResponse(); } catch (WebException ex) { response = (HttpWebResponse)ex.Response; }
        using (response) using (var reader = new StreamReader(response.GetResponseStream())) {
            string text = reader.ReadToEnd(); handled.GetAwaiter().GetResult();
            Check((int)response.StatusCode == expected, method + " " + path + " => " + response.StatusCode + ": " + text);
            return CylinderWritePipeline.Json().Deserialize<Dictionary<string, object>>(text);
        }
    }
    private static void HttpBoundary()
    {
        string directory = Path.Combine(Path.GetTempPath(), "tia-recovery-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        string project = Path.Combine(directory, "Fixture.ap17"), config = Path.Combine(directory, "config.json");
        File.WriteAllText(project, "fixture"); File.WriteAllText(config, CylinderWritePipeline.Json().Serialize(D("tiaPortalVersion", "V17", "projectPath", project, "sourceDirectory", directory)));
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); int port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        var server = new WebServer(config, port);
        var listener = (HttpListener)typeof(WebServer).GetField("_listener", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(server);
        var lanes = (Dictionary<string, ActionProgress>)typeof(ActionRunner).GetField("_lanes", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        var jobs = (IDictionary)typeof(WebServer).GetField("cylinderJobs", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(server);
        var type = typeof(WebServer).GetNestedType("CylinderJob", BindingFlags.NonPublic);
        string id = Guid.NewGuid().ToString("N"), requestId = Guid.NewGuid().ToString("N"), url = "http://127.0.0.1:" + port;
        var source = Activator.CreateInstance(type, true);
        Action<string, object> set = (key, value) => type.GetField(key).SetValue(source, value);
        set("id", id); set("project", project); set("plc", "Station/CPU"); set("state", "needs_review"); jobs[id] = source;
        listener.Start();
        try {
            Request(server, listener, url, "/api/io/cylinder-recovery-check", "POST", D(), 403, "https://different.invalid");
            Request(server, listener, url, "/api/io/cylinder-recovery-check", "GET", null, 405);
            Request(server, listener, url, "/api/io/cylinder-recovery-check", "POST", D(), 400);
            Request(server, listener, url, "/api/io/cylinder-recovery-check", "POST", D("id", Guid.NewGuid().ToString("N"), "request_id", requestId), 404);
            var body = D("id", id, "request_id", requestId);
            // The occupied lane makes this a routing-only test: no engineering work can start.
            lanes["tia"] = new ActionProgress { status = "running", task_id = "offline-fixture", actionName = "fixture" };
            var busy = Request(server, listener, url, "/api/io/cylinder-recovery-check", "POST", body, 409);
            Check(CylinderWritePipeline.Text(busy, "id") == requestId, "rejected busy check has durable operation receipt");
            var replay = Request(server, listener, url, "/api/io/cylinder-recovery-check", "POST", body, 202);
            Check(CylinderWritePipeline.True(replay, "resumed"), "same request returns original check receipt without execution");
            body["different"] = true; Request(server, listener, url, "/api/io/cylinder-recovery-check", "POST", body, 409);
            var recovery = D("id", id, "request_id", Guid.NewGuid().ToString("N"), "action", "continue");
            Request(server, listener, url, "/api/io/cylinder-recover", "POST", recovery, 400);
            recovery["confirm"] = true; Request(server, listener, url, "/api/io/cylinder-recover", "POST", recovery, 400);
            recovery["confirm_save"] = true; Request(server, listener, url, "/api/io/cylinder-recover", "POST", recovery, 409);
            set("state", "running"); Request(server, listener, url, "/api/io/cylinder-recover", "POST", recovery, 409);
            set("state", "completed"); Request(server, listener, url, "/api/io/cylinder-recover", "POST", recovery, 409);
            set("state", "needs_review"); set("resolution_id", Guid.NewGuid().ToString("N"));
            Request(server, listener, url, "/api/io/cylinder-recovery-check", "POST", D("id", id, "request_id", Guid.NewGuid().ToString("N")), 409);
            Check(lanes["tia"].task_id == "offline-fixture", "no mocked HTTP request entered engineering execution");
        } finally {
            lanes.Remove("tia"); listener.Close();
            if (Path.GetFullPath(directory).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(directory, true);
        }
    }
    public static int Main()
    {
        try { Tests(); Pipeline(); HttpBoundary(); Console.WriteLine("PASS: " + checks + " recovery planning, execution and HTTP checks; no TIA connection or engineering writes."); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
