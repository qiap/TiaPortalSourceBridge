using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Web.Script.Serialization;
using TiaPortalSourceBridge.Web;
using TiaPortalSourceBridge.Openness;
internal static class ReliabilityChecks
{
    static int checks;
    static void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks++; }
    static void Reject(Action call, string name) { bool rejected = false; try { call(); } catch (InvalidOperationException) { rejected = true; } Check(rejected, name); }
    static string Post(WebServer server, HttpListener listener, string url, object payload)
    {
        Exception failure = null;
        var handler = new Thread(() => { try { typeof(WebServer).GetMethod("ProcessRequest",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(server,new object[]{listener.GetContext()}); } catch(Exception ex) { failure=ex; } }); handler.IsBackground=true; handler.Start();
        var request = (HttpWebRequest)WebRequest.Create(url + "/api/action/run"); request.Method="POST"; request.ContentType="application/json"; request.Timeout=5000;
        byte[] body=Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(payload)); request.ContentLength=body.Length;
        using(var stream=request.GetRequestStream()) stream.Write(body,0,body.Length);
        HttpWebResponse response;
        try { response=(HttpWebResponse)request.GetResponse(); } catch(WebException ex) { response=(HttpWebResponse)ex.Response; if(response==null)throw; }
        string text; using(response) using(var reader=new StreamReader(response.GetResponseStream())) { Check((int)response.StatusCode==409,"API rejects unsafe or busy action"); text=reader.ReadToEnd(); }
        Check(handler.Join(5000) && failure==null,"API response completes"); return text;
    }
    static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "tia-reliability-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try {
            string a = Path.Combine(root, "A.ap17"), b = Path.Combine(root, "B.ap21");
            var guard = new ActionTarget(); string token = guard.Sign(a, "Station/CPU");
            Check(guard.Verify(a, "Station/CPU", token), "snapshot remains valid after another tab selects B");
            Check(!guard.Verify(b, "Station/CPU", token), "reject changed project/version");
            Check(!guard.Verify(a, "Other/CPU", token), "reject same CPU name in another station");
            Check(!guard.Verify(a, "Station/CPU", ""), "reject missing snapshot");
            Check(!new ActionTarget().Verify(a, "Station/CPU", token), "reject previous server snapshot");
            string backup = BackupRecord.Write(root, "block", "<xml/>", a, "Station/CPU", "Group", "Block", "A");
            Check(BackupRecord.Read(backup, "<xml/>", a, "Station/CPU").GroupPath == "Group", "restore original group");
            Reject(() => BackupRecord.Read(backup, "changed", a, "Station/CPU"), "reject altered backup");
            Reject(() => BackupRecord.Read(backup, "<xml/>", b, "Station/CPU"), "reject wrong project backup");
            Reject(() => BackupRecord.Read(backup, "<xml/>", a, "Other/CPU"), "reject wrong PLC backup");
            var lane = (Dictionary<string,ActionProgress>)typeof(ActionRunner).GetField("_lanes", BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);
            var progress = new ActionProgress { status = "running", actionName = "compile", task_id = "timeout-test", startTime = DateTime.Now };
            lane["tia"] = progress;
            File.WriteAllText(a,"offline fixture");
            string config=Path.Combine(root,"config.json");
            var json=new JavaScriptSerializer(); File.WriteAllText(config,json.Serialize(new { tiaPortalVersion="V17", projectPath=b, sourceDirectory=root }));
            File.WriteAllText(Path.Combine(root,"recent.json"),json.Serialize(new { LastProjectPath=b, LastPlcName="Other/CPU" }));
            var probe=new TcpListener(IPAddress.Loopback,0); probe.Start(); int port=((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            var server=new WebServer(config,port);
            var listener=(HttpListener)typeof(WebServer).GetField("_listener",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(server);
            var apiGuard=(ActionTarget)typeof(WebServer).GetField("actionTargets",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(server);
            listener.Start();
            try {
                string url="http://127.0.0.1:"+port;
                Check(Post(server,listener,url,new { action="import" }).Contains("目标信息无效"),"legacy action cannot use global selection");
                Check(Post(server,listener,url,new { action="import", project_path=a, plc_name="Other/CPU", target_token=apiGuard.Sign(a,"Station/CPU") }).Contains("目标信息无效"),"API rejects target substitution");
                Check(Post(server,listener,url,new { action="import", project_path=a, plc_name="Station/CPU", target_token=apiGuard.Sign(a,"Station/CPU") }).Contains("已有其他任务"),"API preserves A snapshot after selection switches to B");
            } finally { listener.Close(); }
            var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -Command \"Start-Sleep -Milliseconds 1800; exit 0\"") { UseShellExecute=false, CreateNoWindow=true, RedirectStandardInput=true, RedirectStandardOutput=true, RedirectStandardError=true };
            object[] args = { psi, "compile", TimeSpan.FromMilliseconds(50), false }; Exception failure = null; int code = -99;
            var thread = new Thread(() => { try { code = (int)typeof(ActionRunner).GetMethod("RunProcess",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,args); } catch(Exception ex) { failure=ex; } }); thread.Start();
            var clock = Stopwatch.StartNew(); while (ActionRunner.Current.status != "outcome_unknown" && clock.ElapsedMilliseconds < 5000) Thread.Sleep(10);
            Check(ActionRunner.Current.status == "outcome_unknown" && ActionRunner.AnyBlockingRunning, "timeout retains busy state");
            string busy; Check(!ActionRunner.StartAction("import", "unused", a, "Station/CPU", out busy), "prevent duplicate writes after timeout");
            Check(!ActionRunner.StartAction("build-index", "unused", a, "Station/CPU", out busy), "index cannot race engineering writes");
            Check(thread.Join(10000) && failure == null && code == 0 && (bool)args[3], "late process completes without kill");
            var finish = typeof(ActionRunner).GetMethod("Finish", BindingFlags.Static|BindingFlags.NonPublic);
            finish.Invoke(null,new object[]{progress,code,null}); Check(ActionRunner.Current.status == "completed" && !ActionRunner.AnyBlockingRunning, "late success releases lane");
            finish.Invoke(null,new object[]{progress,3,null}); Check(ActionRunner.Current.status == "cancelled", "cancellation is not success");
            Console.WriteLine("PASS: " + checks + " reliability checks (target snapshots, backup identity, timeout, late result, cancellation)."); return 0;
        } catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { foreach (var f in Directory.GetFiles(root)) File.Delete(f); Directory.Delete(root); }
    }
}
