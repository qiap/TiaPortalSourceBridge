using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using TiaPortalSourceBridge.Mcp;
using TiaPortalSourceBridge.Simulation;

internal static class PeripheralChecks
{
    static int count;static void Check(bool b,string m){if(!b)throw new Exception(m);count++;}static void Reject(Action a,string m){bool b=false;try{a();}catch{b=true;}Check(b,m);}
    static Dictionary<string,object> D(params object[] pairs){var d=new Dictionary<string,object>();for(int i=0;i<pairs.Length;i+=2)d.Add((string)pairs[i],pairs[i+1]);return d;}
    sealed class Info {public string Name;}
    sealed class Runtime:ISimulationRuntime
    {
        internal string State="Stop",Communication="Softbus",Name;internal int Id=1,Writes;internal bool FailWrite;
        internal Dictionary<string,bool> Bits=new Dictionary<string,bool>{{"Command",false},{"Feedback",false}};
        public string ApiPath{get{return "fake";}}public object[] Instances(){return Name==null?new object[0]:new object[]{new Info{Name=Name}};}
        public object Register(string name,string cpu){Name=name;return this;}public object Open(string name){return this;}
        public object Get(object instance,string name){switch(name){case "Name":return Name;case "ID":return Id;case "OperatingState":return State;case "CommunicationInterface":return Communication;case "ControllerIP":return new string[0];default:return "fake";}}
        public void Set(object instance,string name,object value){if(name=="CommunicationInterface")Communication=(string)value;}
        public object Call(object instance,string method,params object[] args){lock(Bits){if(method=="ReadBool")return Bits[(string)args[0]];if(method=="WriteBool"){Writes++;if(FailWrite)throw new Exception("write failed");Bits[(string)args[0]]=(bool)args[1];}if(method=="Run")State="Run";if(method=="Stop")State="Stop";return null;}}
    }
    static void Main(string[] args){try{Run(args);}catch(Exception ex){Console.Error.WriteLine(ex.GetType().Name+": "+ex.Message+" after "+count);Environment.ExitCode=1;}}
    static void Run(string[] args)
    {
        var config=new PeripheralConfiguration{duration_ms=1000,interval_ms=20,rules=new[]{new PeripheralRule{command="C",feedback="F",on_delay_ms=100,off_delay_ms=50}}};var bits=new Dictionary<string,bool>{{"C",false},{"F",false}};int writes=0;var model=new PeripheralModel(config);Action<long> tick=t=>model.Tick(t,n=>bits[n],(n,v)=>{writes++;bits[n]=v;});
        tick(0);Check(writes==0,"initial off delay honored");tick(50);Check(writes==1&&!bits["F"],"off feedback after delay");bits["C"]=true;tick(60);tick(159);Check(!bits["F"],"on feedback not early");tick(160);Check(bits["F"],"on feedback at deadline");bits["C"]=false;tick(170);bits["C"]=true;tick(180);tick(220);Check(bits["F"]&&writes==2,"reversing command cancels pending transition");Reject(()=>tick(1),"clock regression rejected");
        config.rules[0].stuck=true;config.rules[0].stuck_value=false;model=new PeripheralModel(config);model.Tick(0,n=>true,(n,v)=>bits[n]=v);Check(!bits["F"],"stuck simulation holds configured value");config.rules[0].stuck=false;
        config.stimuli=new[]{new SimulationStimulus{at_ms=10,tag="C",value=true}};config.assertions=new[]{new SimulationAssertion{at_ms=110,tag="F",expected=true}};bits["C"]=bits["F"]=false;model=new PeripheralModel(config);model.Tick(10,n=>bits[n],(n,v)=>bits[n]=v);model.Tick(110,n=>bits[n],(n,v)=>bits[n]=v);Check(model.Failures==0&&model.AssertionsCompleted==1,"stimulus and delayed feedback satisfy assertion");
        model=new PeripheralModel(config);model.Tick(900,n=>false,(n,v)=>{});Check(model.Failures==2,"missed stimulus and assertion deadlines not reported as pass");
        config.rules=new[]{new PeripheralRule{command="C",feedback="F"},new PeripheralRule{command="X",feedback="F"}};Reject(()=>new PeripheralModel(config),"duplicate feedback writes rejected");config.rules=new[]{new PeripheralRule{command="F",feedback="F"}};Reject(()=>new PeripheralModel(config),"feedback loops rejected");
        string root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);string project=Path.Combine(root,"Fixture.ap17");File.WriteAllText(project,"fixture");var runtime=new Runtime();var sim=new SimulationTools(root,()=>runtime);var request=D("project_path",project,"instance_name","BridgeSim_Fixture","confirm",true);
        sim.Start(request);config=new PeripheralConfiguration{duration_ms=100,interval_ms=20,rules=new[]{new PeripheralRule{command="Command",feedback="Feedback"}},assertions=new[]{new SimulationAssertion{at_ms=50,tag="Feedback",expected=false}}};request["configuration_json"]=new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(config);
        Reject(()=>sim.StartPeripheral(request),"STOP CPU cannot run response rules");runtime.State="Run";runtime.Communication="TCPIP";Reject(()=>sim.StartPeripheral(request),"non Softbus runtime rejected");runtime.Communication="Softbus";
        request["confirm"]=false;Reject(()=>sim.StartPeripheral(request),"explicit confirmation required");request["confirm"]=true;
        var started=(Dictionary<string,object>)sim.StartPeripheral(request);request["run_id"]=started["id"];Dictionary<string,object> result=null;
        for(int i=0;i<300;i++){result=(Dictionary<string,object>)sim.PeripheralStatus(request,false);if((string)result["state"]!="running")break;Thread.Sleep(10);}Check((string)result["state"]=="completed"&&Convert.ToInt32(result["assertions_completed"])==1,"owned adapter completes fake runtime assertion");
        Check(File.Exists((string)result["file"])&&File.Exists((string)result["file"]+"l"),"durable summary and write intent log");
        request["instance_name"]="BridgeSim_Other";Reject(()=>sim.PeripheralStatus(request,true),"cannot stop another instance task");request["instance_name"]="BridgeSim_Fixture";
        runtime.FailWrite=true;request.Remove("run_id");started=(Dictionary<string,object>)sim.StartPeripheral(request);request["run_id"]=started["id"];int initial=runtime.Writes;
        for(int i=0;i<300;i++){result=(Dictionary<string,object>)sim.PeripheralStatus(request,false);if((string)result["state"]!="running")break;Thread.Sleep(10);}Check((string)result["state"]=="failed"&&runtime.Writes<=initial+1,"write failure stops without retry");runtime.FailWrite=false;
        config.duration_ms=10000;request["configuration_json"]=new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(config);request.Remove("run_id");started=(Dictionary<string,object>)sim.StartPeripheral(request);request["run_id"]=started["id"];Reject(()=>sim.Control(D("project_path",project,"instance_name","BridgeSim_Fixture","action","stop","confirm",true)),"in-process controls blocked while rules active");
        var other=new SimulationTools(root,()=>runtime);
        Reject(()=>other.Control(D("project_path",project,"instance_name","BridgeSim_Fixture","action","stop","confirm",true)),"other server controls blocked by shared lease");
        Reject(()=>other.StartPeripheral(request),"other server rules blocked by shared lease");
        sim.PeripheralStatus(request,true);sim.StopPeripheral();result=(Dictionary<string,object>)sim.PeripheralStatus(request,false);Check((string)result["state"]=="stopped","cancel stops future simulated writes");Check(File.ReadAllText(project)=="fixture","no engineering writes");
        Reject(()=>sim.StartPeripheral(request),"persisted run identifier cannot replay");
        other.Control(D("project_path",project,"instance_name","BridgeSim_Fixture","action","stop","confirm",true));Check(runtime.State=="Stop","shared lease released after stopped task");
        Console.WriteLine("PASS: "+count+" peripheral model and owned simulation adapter checks");
    }
}
