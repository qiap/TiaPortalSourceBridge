using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using TiaPortalSourceBridge.Web;
internal static class BrowserLifetimeChecks
{
    private static int checks;
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Check(bool condition, string name) { checks++; if (!condition) throw new Exception(name); }
    private static string Request(WebServer server, HttpListener listener, string url, string path, string body, int expectedStatus)
    {
        Exception failure = null;
        var handler = new Thread(() => {
            try { typeof(WebServer).GetMethod("ProcessRequest", PrivateInstance).Invoke(server, new object[] { listener.GetContext() }); }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        handler.Start();
        var request = (HttpWebRequest)WebRequest.Create(url + path); request.Timeout = 5000;
        if (body != null) {
            request.Method = "POST"; request.ContentType = "application/json";
            byte[] bytes = Encoding.UTF8.GetBytes(body); request.ContentLength = bytes.Length;
            using (var stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
        }
        HttpWebResponse response;
        try { response = (HttpWebResponse)request.GetResponse(); }
        catch (WebException ex) { response = (HttpWebResponse)ex.Response; if (response == null) throw; }
        string result;
        using (response) using (var reader = new StreamReader(response.GetResponseStream())) {
            Check((int)response.StatusCode == expectedStatus, path + " HTTP status"); result = reader.ReadToEnd();
        }
        Check(handler.Join(5000) && failure == null, path + " request finishes");
        return result;
    }
    private static void CheckServerLifetime()
    {
        string root = Path.Combine(Path.GetTempPath(), "tia-browser-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "web"));
        string config = Path.Combine(root, "config.json");
        File.WriteAllText(config, new JavaScriptSerializer().Serialize(new { tiaPortalVersion = "V17", projectPath = Path.Combine(root,"Offline.ap17"), sourceDirectory = root }));
        File.WriteAllText(Path.Combine(root, "web", "index.html"), "refresh-check");
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); int port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        var server = new WebServer(config, port);
        var listener = (HttpListener)typeof(WebServer).GetField("_listener", PrivateInstance).GetValue(server);
        var running = typeof(WebServer).GetField("_running", PrivateInstance);
        var leases = (BrowserLeases)typeof(WebServer).GetField("browserLeases", PrivateInstance).GetValue(server);
        var gate = typeof(WebServer).GetField("browserLifetimeGate", PrivateInstance).GetValue(server);
        var lanes = (Dictionary<string,ActionProgress>)typeof(ActionRunner).GetField("_lanes", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        var laneGate = typeof(ActionRunner).GetField("_sync", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        try {
            listener.Start(); running.SetValue(server, true);
            lock (gate) { leases.Heartbeat("closed-tab", DateTime.UtcNow.AddSeconds(-20)); leases.Release("closed-tab", DateTime.UtcNow.AddSeconds(-10)); }
            typeof(WebServer).GetMethod("StartBrowserLifetime", PrivateInstance).Invoke(server, null);
            Thread.Sleep(1200); // The old four-second exit deadline is already in the past.
            Check(listener.IsListening && (bool)running.GetValue(server), "last page release does not stop service");
            string url = "http://127.0.0.1:" + port;
            var started = new ManualResetEvent(false);
            var release = new ManualResetEvent(false);
            Check(ActionRunner.StartIoRead(config, "offline", "PLC1", report => {
                report("fixture: reading table 2");
                started.Set();
                if (!release.WaitOne(5000)) throw new TimeoutException("fixture timed out");
                return "fixture: one table unavailable";
            }), "start monitored task");
            Check(started.WaitOne(5000), "progress available before completion");
            string live = Request(server, listener, url, "/api/execution-log", null, 200);
            Check(live.Contains("reading table 2") && live.Contains("\"running\""), "live progress visible while task blocked");
            release.Set();
            Check(SpinWait.SpinUntil(() => Directory.GetFiles(Path.Combine(root,"logs","execution"), "*.done").Length == 1, 5000), "result persisted");
            string finished = Request(server, listener, url, "/api/execution-log", null, 200);
            Check(finished.Contains("\"warning\"") && finished.Contains("one table unavailable"), "partial completion has a distinct status");
            var partial = (HttpWebRequest)WebRequest.Create(url + "/api/build");
            partial.Timeout = 5000;
            var pending = partial.BeginGetResponse(null, null);
            var context = listener.GetContext();
            context.Response.SendChunked = true;
            context.Response.OutputStream.WriteByte(32);
            context.Response.OutputStream.Flush();
            using (partial.EndGetResponse(pending)) {
                typeof(WebServer).GetMethod("ProcessRequest", PrivateInstance).Invoke(server, new object[] { context });
            }
            Check(Request(server, listener, url, "/", null, 200) == "refresh-check", "delayed refresh serves dashboard");
            lock (gate) { leases.Heartbeat("sleeping-tab", DateTime.UtcNow.AddSeconds(-120)); }
            Thread.Sleep(1200);
            lock (gate) { Check(leases.Count == 0, "expired page is removed from active count"); }
            Check(listener.IsListening && (bool)running.GetValue(server), "expired heartbeat does not stop service");
            string heartbeat = "{\"client_id\":\"sleeping-tab\",\"page_id\":\"resumed-page\"}";
            Request(server, listener, url, "/api/browser/heartbeat", heartbeat, 200);
            Check(Request(server, listener, url, "/", null, 200) == "refresh-check", "resumed page can reload");
            var task = new ActionProgress { status = "running", actionName = "compile", task_id = "shutdown-wait" };
            lock (laneGate) { lanes["tia"] = task; }
            Request(server, listener, url, "/api/shutdown", "{}", 200);
            Check(listener.IsListening, "explicit shutdown waits for active task");
            Request(server, listener, url, "/api/browser/heartbeat", heartbeat, 503);
            lock (laneGate) { task.status = "completed"; }
            Check(SpinWait.SpinUntil(() => !(bool)running.GetValue(server) && !listener.IsListening, 5000), "explicit shutdown stops after task completes");
        } finally {
            lock (laneGate) { lanes.Clear(); }
            running.SetValue(server, false); listener.Close();
            File.Delete(Path.Combine(root, "web", "index.html")); Directory.Delete(Path.Combine(root, "web"));
            string journal = Path.Combine(root, "logs", "execution");
            if (Directory.Exists(journal)) {
                foreach (string file in Directory.GetFiles(journal)) File.Delete(file);
                Directory.Delete(journal);
                Directory.Delete(Path.Combine(root, "logs"));
            }
            foreach (string file in Directory.GetFiles(root)) File.Delete(file);
            Directory.Delete(root);
        }
    }
    private static int Main()
    {
        try {
            var t = new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc);
            var leases = new BrowserLeases();
            leases.Expire(t.AddHours(1)); Check(leases.Count == 0, "empty tracker");
            leases.Heartbeat("tab-a", t); leases.Heartbeat("tab-b", t);
            leases.Release("tab-a", t.AddSeconds(1)); Check(leases.Count == 1, "other tab remains");
            leases.Release("unknown", t.AddSeconds(2)); Check(leases.Count == 1, "unknown release ignored");
            leases.Release("tab-b", t.AddSeconds(3)); Check(leases.Count == 0, "last release updates count");
            leases.Heartbeat("sleeping", t); leases.Expire(t.AddSeconds(90)); Check(leases.Count == 1, "heartbeat boundary");
            leases.Expire(t.AddSeconds(91)); Check(leases.Count == 0, "stale heartbeat expires");
            leases.Heartbeat("stable", "old-page", t.AddSeconds(120));
            leases.Heartbeat("stable", "new-page", t.AddSeconds(121));
            leases.Release("stable", "old-page", t.AddSeconds(122)); Check(leases.Count == 1, "late old release preserves refreshed page");
            leases.Release("stable", "new-page", t.AddSeconds(123)); Check(leases.Count == 0, "current page release");
            CheckServerLifetime();
            Console.WriteLine("PASS: " + checks + " browser checks (refresh, suspended tabs, explicit shutdown, pending tasks; no TIA connection).");
            return 0;
        } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
