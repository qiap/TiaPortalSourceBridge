using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Web.Script.Serialization;
using TiaPortalSourceBridge.Simulation;
using TiaPortalSourceBridge.Mcp;

class ScenarioChecks
{
    static int count;static void Check(bool ok,string message){if(!ok)throw new Exception(message);count++;}
    static void Reject(Action a,string m){bool rejected=false;try{a();}catch{rejected=true;}Check(rejected,m);}
    static Dictionary<string,object> D(params object[] x){return SimulationTools.Map(x);}
    static ScenarioCondition C(string tag,string type,object value){return new ScenarioCondition{tag=tag,type=type,equals=value};}
    static PeripheralConfiguration Config(){return new PeripheralConfiguration{duration_ms=1000,interval_ms=20,max_lateness_ms=100,steps=new[]{new ScenarioStep{name="等待15步后返回NG",timeout_ms=500,delay_ms=40,wait_until=new[]{C("Step","dint",15)},writes=new[]{new ScenarioWrite{tag="NG",value=true}}},new ScenarioStep{name="等待重测后NG",timeout_ms=300,wait_until=new[]{C("Step","dint",25)},checks=new[]{C("NG","bool",true)},writes=new[]{new ScenarioWrite{tag="NG2",value=true}}},new ScenarioStep{name="确认停站",timeout_ms=300,wait_until=new[]{C("Step","dint",99)},checks=new[]{C("Stopped","bool",true)}}},watch=new[]{new ScenarioTag{tag="Step",type="dint"}}};}
    static int Main(string[] args){try{Run(Path.GetFullPath(args[0]));Console.WriteLine("PASS: "+count+" condition scenario, timeline, failure snapshot and download diagnostic checks; fake runtime only.");return 0;}catch(Exception ex){Console.Error.WriteLine("Checks before failure: "+count+"\n"+ex);return 1;}}
    static void Run(string temp){
        var config=Config();var m=new ScenarioModel(config);var values=D("Step",0,"NG",false,"NG2",false,"Stopped",false);int writes=0;
        Check(ScenarioModel.Parse(new JavaScriptSerializer().Serialize(config)).steps.Length==3,"strict config roundtrip");
        Reject(()=>ScenarioModel.Parse("{\"scenario\":[],\"steps\":[]}"),"unknown config field rejected");
        Reject(()=>ScenarioModel.Parse("{\"steps\":[{\"name\":\"x\",\"writes\":[{\"tag\":\"X\"}]}]}"),"omitted write value rejected");
        Action<long> tick=t=>m.Tick(t,tag=>values[tag.tag],(tag,value)=>{writes++;values[tag]=value;});
        tick(0);Check(writes==0&&m.StepIndex==0,"wait does not trigger early");values["Step"]=15;tick(20);Check(writes==0,"delay after condition match");tick(40);Check(writes==0,"no early feedback");tick(60);Check(writes==1&&(bool)values["NG"]&&m.StepIndex==1,"first NG conditional write");
        tick(80);Check(writes==1,"wait does not replay first action");values["Step"]=25;tick(100);Check(writes==2&&m.StepIndex==2,"second NG conditional write");values["Step"]=99;values["Stopped"]=true;tick(120);Check(m.Complete&&m.Failures==0,"two NG then stop acceptance sequence");
        Check(m.Events.Count(e=>(string)e["kind"]=="step_complete")==3,"complete per-step timeline");
        var closedConfig=Config();closedConfig.rules=new[]{new PeripheralRule{command="Extend",feedback="AtWork",on_delay_ms=40}};closedConfig.steps[0].wait_until=new[]{C("Step","dint",15),C("AtWork","bool",true)};
        var closed=new ScenarioModel(closedConfig);var closedValues=D("Step",15,"NG",false,"NG2",false,"Stopped",false,"Extend",true,"AtWork",false);
        Action<long> closedTick=t=>closed.Tick(t,tag=>closedValues[tag.tag],(tag,value)=>closedValues[tag]=value);
        closedTick(0);closedTick(40);Check((bool)closedValues["AtWork"]&&!(bool)closedValues["NG"],"closed-loop delayed feedback starts scenario condition");closedTick(80);Check((bool)closedValues["NG"],"same runner executes condition after peripheral feedback");
        Check(m.Events.Where(e=>(string)e["kind"]=="value_change"&&(string)e["tag"]=="Step").Select(e=>Convert.ToInt32(e["value"])).SequenceEqual(new[]{0,15,25,99}),"AutoStep transition trace");
        var snapshot=m.Snapshot(tag=>{if(tag.tag=="NG")throw new IOException("read failure");return values[tag.tag];});Check(snapshot.Any(r=>(string)r["tag"]=="NG"&&!(bool)r["ok"])&&snapshot.Length==4,"snapshot retains per-tag failures");
        m=new ScenarioModel(Config());values["Step"]=0;writes=0;tick(0);for(int t=100;t<=600;t+=100)tick(t);Check(m.Failure!=null&&writes==0,"condition timeout prevents writes");
        m=new ScenarioModel(Config());values["Step"]=15;tick(0);tick(150);Check(m.Failure!=null&&writes==0,"late poll stops instead of catching up");
        m=new ScenarioModel(Config());tick(0);Reject(()=>tick(-1),"clock rollback rejected");
        m=new ScenarioModel(Config());tick(0);Reject(()=>m.Tick(40,t=>values[t.tag],(tag,v)=>{throw new IOException("write lost");}),"transport write propagates");
        Check(m.Events.Any(e=>(string)e["kind"]=="write_intent")&&!m.Events.Any(e=>(string)e["kind"]=="step_complete"),"write failure has intent without false completion");
        config=Config();config.steps[0].checks=new[]{C("Stopped","bool",true)};values["Stopped"]=false;m=new ScenarioModel(config);writes=0;tick(0);tick(40);Check(m.Failures>0&&writes==0,"failed check precedes step writes");
        config=Config();config.steps[0].writes=new[]{new ScenarioWrite{tag="X",value=true},new ScenarioWrite{tag="X",value=false}};Reject(()=>new ScenarioModel(config),"duplicate action output");
        config=Config();config.rules=new[]{new PeripheralRule{command="Command",feedback="NG"}};Reject(()=>new ScenarioModel(config),"peripheral feedback and scenario conflict");
        config=Config();config.stimuli=new[]{new SimulationStimulus{at_ms=20,tag="NG",value=false}};Reject(()=>new ScenarioModel(config),"timed stimulus and scenario conflict");
        config=Config();config.watch=new[]{new ScenarioTag{tag="Step",type="bool"}};Reject(()=>new ScenarioModel(config),"inconsistent tag types");
        config=Config();config.steps[1].name=config.steps[0].name;Reject(()=>new ScenarioModel(config),"duplicate step names");
        config=Config();config.steps[0].wait_until[0].equals=1.5;Reject(()=>new ScenarioModel(config),"fractional DInt rejected");
        config=Config();config.steps[0].wait_until[0]=C("B","bool",1);Reject(()=>new ScenarioModel(config),"numeric Bool rejected");
        Reject(()=>ScenarioModel.Value("udint",-1),"unsigned range");Reject(()=>ScenarioModel.Value("real",double.NaN),"NaN rejected");Reject(()=>ScenarioModel.Value("int",1),"unknown SDK type not guessed");
        Check((float)ScenarioModel.Value("real",0.5)==0.5f&&(int)ScenarioModel.Value("time",100)==100,"typed Real and Time");
        config=Config();config.interval_ms=19;Reject(()=>new ScenarioModel(config),"sampling lower bound");
        config=Config();config.steps[0].delay_ms=501;Reject(()=>new ScenarioModel(config),"delay beyond timeout");
        string[] codes=DownloadDiagnostics.Assess("Off","Online",0,0,0,null,false).Select(issue=>(string)issue["code"]).ToArray();
        foreach(var code in new[]{"INSTANCE_NOT_READY","TIA_GUI_ONLINE","SOFTBUS_INTERFACE_NOT_UNIQUE","INSTANCE_ADDRESS_NOT_UNIQUE","SIMULATION_SUPPORT_UNKNOWN"})Check(codes.Contains(code),"preflight diagnostic "+code);
        Check(DownloadDiagnostics.Assess("Stop","Offline",1,1,1,true,false).Length==0,"STOP can receive explicit download");
        Check(DownloadDiagnostics.Assess("Run","Offline",1,1,1,false,true).Length==0,"explicit support opt-in");
        Check(DownloadDiagnostics.Assess("Run","unknown",1,1,2,false,false).Length==3,"unknown online state and target ambiguity block");
        Check(DownloadDiagnostics.Succeeded(0,"Success")&&DownloadDiagnostics.Succeeded(0,"Warning")&&!DownloadDiagnostics.Succeeded(0,"FutureState")&&!DownloadDiagnostics.Succeeded(1,"Success"),"only known successful native states accepted");
        var d=DownloadDiagnostics.Failure(new InvalidOperationException("download",new IOException("Error Code: -15, InterfaceRemoved")),"download",true);
        Check((string)d["code"]=="DOWNLOAD_INTERFACE_REMOVED"&&!(bool)d["root_cause_confirmed"]&&!(bool)d["automatic_retry"],"native symptom without invented NIC cause or retry");
        d=DownloadDiagnostics.Failure(new InvalidOperationException("DownloadProvider"),"compile",false);Check((string)d["code"]=="SIMULATION_COMPILE_FAILED"&&!(bool)d["download_issued"],"stage evidence not guessed online conflict");
        Check(!ProjectOperationGuard.IsMutation("tia_simulation_download",D())&&ProjectOperationGuard.IsMutation("tia_simulation_download",D("dry_run",false)),"dry run read-only guard");
        Adapter(temp);
    }
    sealed class Info{public string Name;}
    sealed class Runtime:ISimulationRuntime
    {
        internal string Name,State="Stop";internal int Id=1,Writes,ReadDelay;internal bool FailWrite,FailRead;
        internal Dictionary<string,object> Values=D("Step",15,"NG",false,"NG2",false,"Stopped",false);
        public string ApiPath{get{return "fake";}}public object[] Instances(){return Name==null?new object[0]:new object[]{new Info{Name=Name}};}
        public object Register(string name,string cpu){Name=name;return this;}public object Open(string name){return this;}
        public object Get(object instance,string name){switch(name){case "Name":return Name;case "ID":return Id;case "OperatingState":return State;case "CommunicationInterface":return "Softbus";default:return new string[0];}}
        public void Set(object i,string n,object v){}
        public object Call(object i,string method,params object[] args){if(method=="UpdateTagList")return null;if(method.StartsWith("Read")){if(ReadDelay>0)Thread.Sleep(ReadDelay);if(FailRead)throw new IOException("gone");return Values[(string)args[0]];}if(method=="WriteBool"){Writes++;if(FailWrite)throw new IOException("ambiguous write");Values[(string)args[0]]=args[1];if((string)args[0]=="NG")Values["Step"]=25;if((string)args[0]=="NG2"){Values["Step"]=99;Values["Stopped"]=true;}return null;}if(method=="Stop")State="Stop";return null;}
    }
    static Dictionary<string,object> Wait(SimulationTools sim,Dictionary<string,object> args){for(int i=0;i<500;i++){var r=(Dictionary<string,object>)sim.PeripheralStatus(args,false);if((string)r["state"]!="running")return r;Thread.Sleep(10);}throw new TimeoutException("Fake scenario didn't finish");}
    static void Adapter(string temp){
        Directory.CreateDirectory(temp);string project=Path.Combine(temp,"Sim.ap17");File.WriteAllText(project,"unchanged");
        var runtime=new Runtime();var sim=new SimulationTools(temp,()=>runtime);var args=D("project_path",project,"plc_name","PLC","instance_name","BridgeSim_Scenario","confirm",true);sim.Start(args);runtime.State="Run";
        var c=Config();c.duration_ms=700;args["configuration_json"]=new JavaScriptSerializer().Serialize(c);
        var started=(Dictionary<string,object>)sim.StartPeripheral(args);args["run_id"]=started["id"];var result=Wait(sim,args);
        Check((string)result["state"]=="completed"&&Convert.ToInt32(result["steps_completed"])==3&&runtime.Writes==2,"owned adapter conditional NG sequence");
        Check((bool)result["snapshot_complete"]&&File.Exists((string)result["file"]+"l"),"durable final snapshot and full timeline");
        args["event_offset"]=0;args["event_limit"]=2;var page=(Dictionary<string,object>)sim.PeripheralStatus(args,false);Check(((object[])page["events"]).Length==2&&Convert.ToInt32(page["next_event_offset"])==2&&(bool)page["events_truncated"],"timeline explicit pagination");
        int total=Convert.ToInt32(result["event_count"]);var events=File.ReadAllLines((string)result["file"]+"l").Select(line=>new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(line)).ToArray();
        Check(events.Length==total&&events.Select(e=>Convert.ToInt32(e["event_index"])).SequenceEqual(Enumerable.Range(0,total)),"timeline indexes complete");
        Check(events.All(e=>e.ContainsKey("recorded_utc")),"wall time on every event");
        Check(events.Count(e=>(string)e["kind"]=="write_intent")==2&&events.Count(e=>(string)e["kind"]=="step_write")==2,"intent and success exactly once");
        var other=new SimulationTools(temp,()=>runtime);page=(Dictionary<string,object>)other.PeripheralStatus(args,false);Check(((object[])page["events"]).Length==2,"history readable after process replacement");
        Reject(()=>sim.StartPeripheral(args),"used run ID never replayed");args.Remove("event_offset");args.Remove("run_id");runtime.Values["Step"]=15;runtime.FailWrite=true;int before=runtime.Writes;
        started=(Dictionary<string,object>)sim.StartPeripheral(args);args["run_id"]=started["id"];result=Wait(sim,args);Check((string)result["state"]=="failed"&&runtime.Writes==before+1&&result.ContainsKey("failure_snapshot")&&!(bool)result["automatic_retry"],"write failure snapshot, no retry");
        args.Remove("run_id");runtime.FailWrite=false;runtime.Values["Step"]=0;c.steps[0].timeout_ms=60;args["configuration_json"]=new JavaScriptSerializer().Serialize(c);before=runtime.Writes;
        started=(Dictionary<string,object>)sim.StartPeripheral(args);args["run_id"]=started["id"];result=Wait(sim,args);Check((string)result["state"]=="failed"&&runtime.Writes==before&&Convert.ToInt32(result["failure_count"])>0,"adapter timeout stops subsequent actions");
        args.Remove("run_id");c.steps[0].timeout_ms=500;args["configuration_json"]=new JavaScriptSerializer().Serialize(c);started=(Dictionary<string,object>)sim.StartPeripheral(args);args["run_id"]=started["id"];runtime.FailRead=true;result=Wait(sim,args);Check((string)result["state"]=="failed"&&!(bool)result["snapshot_complete"],"lost instance values marked failed in snapshot");runtime.FailRead=false;
        args.Remove("run_id");started=(Dictionary<string,object>)sim.StartPeripheral(args);args["run_id"]=started["id"];runtime.ReadDelay=40;result=Wait(sim,args);Check((string)result["state"]=="failed"&&Convert.ToString(result["error"]).Contains("超时"),"slow reads without writes still fail timing guarantee");runtime.ReadDelay=0;
        Check(File.ReadAllText(project)=="unchanged","engineering project never touched");
    }
}
