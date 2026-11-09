using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using TiaPortalSourceBridge.Web;
using TiaPortalSourceBridge.Mcp;
using TiaPortalSourceBridge.Online;

internal static class OnlineMonitorChecks
{
    static int count;static void Check(bool b,string m){if(!b)throw new Exception(m);count++;}
    static void Reject(Action a,string m){bool rejected=false;try{a();}catch{rejected=true;}Check(rejected,m);}
    static Dictionary<string,object> D(params object[] pairs){var d=new Dictionary<string,object>();for(int i=0;i<pairs.Length;i+=2)d.Add((string)pairs[i],pairs[i+1]);return d;}
    static void Main(string[] args){try{Run(args);}catch(Exception ex){Console.Error.WriteLine(ex.GetType().Name+": "+ex.Message);Environment.ExitCode=1;}}
    static void Run(string[] args)
    {
        string root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);string project=Path.Combine(root,"Fixture.ap17");File.WriteAllText(project,"fixture");
        bool partial=false,running=false,closed=false;string log=Path.Combine(root,"online-records","sample.jsonl");var calls=new List<string>();Dictionary<string,object> connectArgs=null;
        Func<string,Dictionary<string,object>,object> fake=(name,values)=>{
            calls.Add(name);object data=D();
            if(name=="tia_online_connect"){connectArgs=values;data=D("connected",true);}
            if(name=="tia_online_read")data=D("complete",!partial,"atomic_across_groups",false,"values",D("DB_Process.Value",D("value",partial?null:(object)42,"quality",partial?"bad":"good","address","DB10.DBW0","type","Int")));
            if(name=="tia_online_watch_start"){running=true;data=D("running",true);}
            if(name=="tia_online_watch_stop")running=false;
            if(name=="tia_online_watch_read")data=D("watch",D("file",log,"running",running),"events",new object[0]);
            return D("isError",partial&&name=="tia_online_read","structuredContent",data,"content",new object[0]);
        };
        var monitor=new OnlineMonitor(root,fake,()=>closed=true);var profile=new OnlineProfile{Name="Fixture",Ip="127.0.0.1",Slot=1,Tags=new[]{new OnlineTag{Name="DB_Process.Value",Address="DB10.DBW0",Type="Int"}}};
        Reject(()=>monitor.Execute("read",project,"PLC","bad",D()),"read requires a bound session");
        var connected=CylinderWritePipeline.Json().Deserialize<Dictionary<string,object>>(CylinderWritePipeline.Json().Serialize(monitor.Connect(project,"PLC",profile,"s7tcp","simulation")));string id=(string)connected["session_id"];
        Check(Equals(connectArgs["enable_simulation_writes"],false),"simulation connection remains read only");Check(File.Exists((string)connectArgs["profile_path"]),"profile stored inside owned directory");
        Reject(()=>monitor.Connect(project,"Other",profile,"s7tcp","hardware"),"cannot replace active session");
        Reject(()=>monitor.Execute("read",project,"Other",id,D()),"PLC switch invalidates read");Reject(()=>monitor.Execute("read",project+".other","PLC",id,D()),"project switch invalidates read");Reject(()=>monitor.Execute("read",project,"PLC","other",D()),"session identity required");
        var read=(Dictionary<string,object>)monitor.Execute("read",project,"PLC",id,D());Check((bool)read["complete"],"read returns quality evidence");
        var snapshot=(Dictionary<string,object>)monitor.Execute("snapshot",project,"PLC",id,D());Check(File.Exists(Path.Combine(root,"parameter-snapshots",snapshot["id"]+".json")),"parameter archive durable");Check(Equals(snapshot["atomic"],false),"non atomic scope explicit");
        partial=true;read=(Dictionary<string,object>)monitor.Execute("read",project,"PLC",id,D());Check(!(bool)read["complete"],"bad quality remains visible");Reject(()=>monitor.Execute("snapshot",project,"PLC",id,D()),"incomplete sample not archived");Check(Directory.GetFiles(Path.Combine(root,"parameter-snapshots")).Length==1,"failed sample leaves no archive");partial=false;
        Reject(()=>monitor.Execute("start",project,"PLC",id,D("interval_ms",10,"duration_seconds",60)),"sampling bound enforced");
        monitor.Execute("start",project,"PLC",id,D("interval_ms",50,"duration_seconds",1));Reject(()=>monitor.Export(project,"PLC",id),"no export of changing log");monitor.Execute("stop",project,"PLC",id,D());
        File.WriteAllText(log,"{\"sample\":1}\n");Check(monitor.Export(project,"PLC",id).Length>0,"complete stopped log exported");
        string safe=log;log=project;Reject(()=>monitor.Export(project,"PLC",id),"export path cannot escape records");log=safe;
        Reject(()=>monitor.Execute("tia_online_write_test",project,"PLC",id,D()),"arbitrary tool and writes rejected");
        monitor.Execute("disconnect",project,"PLC",id,D());Reject(()=>monitor.Execute("read",project,"PLC",id,D()),"disconnected session cannot be reused");profile.Tags[0].TestWritable=true;Reject(()=>monitor.Connect(project,"PLC",profile,"s7tcp","simulation"),"write capable profile rejected");
        monitor.Dispose();Check(closed,"owned connection disposed");Check(!calls.Any(n=>n.Contains("write")||n.Contains("force")||n.Contains("download")),"no online writes or CPU operations reachable");Check(File.ReadAllText(project)=="fixture","engineering untouched");
        Console.WriteLine("PASS: "+count+" dashboard read-only monitor and parameter archive checks");
    }
}
