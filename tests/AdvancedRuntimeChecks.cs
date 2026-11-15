using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using TiaPortalSourceBridge.Simulation;

internal static class AdvancedRuntimeChecks
{
    static int count;
    static void Check(bool condition,string name){if(!condition)throw new Exception(name);count++;}
    static void Reject(Action action,string name){try{action();}catch{count++;return;}throw new Exception("Accepted: "+name);}
    public enum Cpu{CPU1513}
    public sealed class Overloads{
        public static string RegisterInstance(string article,string name){return "article";}
        public static string RegisterInstance(Cpu cpu,string name){return "cpu";}
        public static bool ReadBool(string name){return true;}
        public static void WriteBool(string name,bool value){}
    }
    static void Offline(string root)
    {
        Directory.CreateDirectory(root);
        foreach(string version in new[]{"5.0","7.0","8.0"}){Directory.CreateDirectory(Path.Combine(root,version));File.WriteAllText(Path.Combine(root,version,"Siemens.Simatic.Simulation.Runtime.Api.x64.dll"),"");}
        foreach(int major in new[]{5,7,8})Check(AdvancedRuntime.FindApi(root,major).Contains(major+".0"),"match installed runtime "+major);
        Check(AdvancedRuntime.FindApi(root,0).Contains("8.0"),"newest verified fallback");
        Reject(()=>AdvancedRuntime.FindApi(root,9),"unverified runtime rejected");Reject(()=>AdvancedRuntime.FindApi(root,6),"missing matching API rejects legacy fallback");
        var method=AdvancedRuntime.SelectMethod(typeof(Overloads),"RegisterInstance",new object[]{Cpu.CPU1513,"test"});
        Check((string)method.Invoke(null,new object[]{Cpu.CPU1513,"test"})=="cpu","V8 CPU overload never article overload");
        Reject(()=>AdvancedRuntime.SelectMethod(typeof(Overloads),"RegisterInstance",new object[]{"CPU1513","test"}),"ambiguous string overload rejected");
        Check(AdvancedRuntime.SelectMethod(typeof(Overloads),"ReadBool",new object[]{"tag"}).ReturnType==typeof(bool),"symbolic Bool read signature");
        Check(AdvancedRuntime.SelectMethod(typeof(Overloads),"WriteBool",new object[]{"tag",true}).ReturnType==typeof(void),"typed Bool write signature");
        Reject(()=>AdvancedRuntime.SelectMethod(typeof(Overloads),"WriteBool",new object[]{"tag","true"}),"wrong value type rejected");
        int changes=0;AdvancedRuntime.SelectSoftbus(true,()=>4,()=>changes++);Check(changes==0,"existing Softbus needs no global change");
        int initializations=0;
        AdvancedRuntime.InitializeRuntime(()=>{initializations++;return true;});
        Check(initializations==1,"explicit start initializes through SDK once");
        initializations=0;
        Reject(()=>AdvancedRuntime.InitializeRuntime(()=>{initializations++;return false;}),"unavailable runtime blocks registration");
        Check(initializations==1,"unavailable runtime is not retried");
        initializations=0;var failure=new InvalidOperationException("native startup failure");
        try {AdvancedRuntime.InitializeRuntime(()=>{initializations++;throw failure;});throw new Exception("startup failure lost");}
        catch(InvalidOperationException ex){Check(object.ReferenceEquals(ex,failure),"native startup failure retained");}
        Check(initializations==1,"crashed runtime is not retried");
        Reject(()=>AdvancedRuntime.SelectSoftbus(false,()=>1,()=>changes++),"other instances block global mode changes");Check(changes==0,"blocked mode untouched");
        AdvancedRuntime.SelectSoftbus(false,()=>0,()=>changes++);Check(changes==1,"empty runtime may select Softbus once");
        var runtime=new AdvancedRuntime(Assembly.GetExecutingAssembly(),"fake-sdk");
        var fake=runtime.Call(null,"RegisterInstance",AdvancedRuntime.CpuArgument(Assembly.GetExecutingAssembly(),"CPU1513"),"fixture");
        Check(Convert.ToString(runtime.Get(fake,"Name"))=="fixture","adapter selects typed registration");
        runtime.Set(fake,"CommunicationInterface","Softbus");Check(true,"read-only V8 Softbus accepted");
        runtime.Call(fake,"WriteBool","tag",true);Check((bool)runtime.Call(fake,"ReadBool","tag"),"symbolic Bool write/read through adapter");
        Check((uint)runtime.Call(fake,"ReadUInt32","tag")==42U,"UDInt adapter read");Check((int)runtime.Call(fake,"ReadInt32","tag")==-4,"DInt/time adapter read");Check((float)runtime.Call(fake,"ReadFloat","tag")==1.5f,"Real adapter read");
        ((Siemens.Simatic.Simulation.Runtime.FakeInstance)fake).Mode="TCPIP";
        Reject(()=>runtime.Set(fake,"CommunicationInterface","Softbus"),"read-only TCPIP cannot silently switch global mode");
        try{runtime.Call(fake,"ReadBool","missing");throw new Exception("missing error not reported");}catch(InvalidOperationException ex){Check(ex.InnerException!=null&&ex.Message.Contains("native failure"),"native error retained");}
    }
    static void Contract(string path)
    {
        var assembly=Assembly.LoadFrom(path);var manager=assembly.GetType("Siemens.Simatic.Simulation.Runtime.SimulationRuntimeManager",true);var instance=assembly.GetType("Siemens.Simatic.Simulation.Runtime.IInstance",true);
        var info=assembly.GetType("Siemens.Simatic.Simulation.Runtime.SInstanceInfo",true);Check(info.GetField("Name")!=null&&info.GetField("ID")!=null,"native instance identity fields");
        Check(AdvancedRuntime.SelectMethod(manager,"RegisterInstance",new[]{AdvancedRuntime.CpuArgument(assembly,"CPU1513"),(object)"BridgeSim_Test"}).GetParameters()[0].ParameterType.IsEnum,"native SDK CPU overload");
        Reject(()=>AdvancedRuntime.CpuArgument(assembly,"InvalidCPU"),"unknown CPU rejected");
        foreach(string name in new[]{"Name","ID","OperatingState","CommunicationInterface","ControllerIP","LicenseStatus"})Check(instance.GetProperty(name)!=null,"native property "+name);
        if(instance.GetProperty("CommunicationInterface").CanWrite)Check(true,"legacy per-instance Softbus");
        else{var mode=manager.GetProperty("NetworkMode");Check(mode!=null&&mode.CanWrite,"V8 global network selection");Check(Enum.GetNames(mode.PropertyType).Any(n=>n.Equals("Softbus",StringComparison.OrdinalIgnoreCase)),"V8 Softbus enum value");}
        foreach(string name in new[]{"PowerOn","PowerOff","Run","Stop"})Check(AdvancedRuntime.SelectMethod(instance,name,new object[]{(uint)30000})!=null,"native lifecycle "+name);
        Check(AdvancedRuntime.SelectMethod(instance,"UnregisterInstance",new object[0])!=null,"native cleanup");
        Check(AdvancedRuntime.SelectMethod(instance,"UpdateTagList",new object[0])!=null,"native symbol refresh");
        foreach(string name in new[]{"ReadBool","ReadUInt32","ReadInt32","ReadFloat"})Check(AdvancedRuntime.SelectMethod(instance,name,new object[]{"tag"})!=null,"native read "+name);
        Check(AdvancedRuntime.SelectMethod(instance,"WriteBool",new object[]{"tag",true})!=null,"native stimulus write");
        Console.WriteLine("SDK contract: "+assembly.GetName().Version+" (no native calls)");
    }
    static void Live(string root)
    {
        Directory.CreateDirectory(root);string project=Path.Combine(root,"ApiLifecycleFixture.ap17");File.WriteAllText(project,"Test identity only; no engineering project and no download.");
        var runtime=new AdvancedRuntime();var sim=new SimulationTools(root,()=>runtime);
        string name="BridgeSim_V8Check_"+Guid.NewGuid().ToString("N").Substring(0,12);
        var args=new Dictionary<string,object>{{"project_path",project},{"instance_name",name},{"cpu_type","CPU1513"},{"confirm",true}};
        var json=new System.Web.Script.Serialization.JavaScriptSerializer();
        Console.WriteLine("BEFORE "+json.Serialize(sim.Status()));
        try {
            Console.WriteLine("START "+json.Serialize(sim.Start(args)));
            object owned=sim.Owned(args);Check((string)runtime.Get(owned,"Name")==name,"live ownership");
            Check(Convert.ToString(runtime.Get(owned,"CommunicationInterface"))=="Softbus","live Softbus");
            foreach(string action in new[]{"run","stop"}){args["action"]=action;Console.WriteLine(action.ToUpperInvariant()+" "+json.Serialize(sim.Control(args)));Check(Convert.ToString(runtime.Get(owned,"OperatingState"))==(action=="run"?"Run":"Stop"),"live "+action);}
            args["tags"]=new object[]{new Dictionary<string,object>{{"name","DB_NotDownloaded.Value"},{"type","bool"}}};
            var missing=(Dictionary<string,object>)sim.Read(args);Console.WriteLine("READ_UNLOADED_SYMBOL "+json.Serialize(missing));Check(Convert.ToInt32(missing["failure_count"])==1,"unloaded symbol reports native failure");
        } finally {
            // Only dispose of the new, name/ID/project-matched instance created by this check.
            object instance=null;
            try{instance=sim.Owned(args);}catch(Exception ex){Console.WriteLine("CLEANUP_OWNERSHIP "+ex.Message);}
            if(instance!=null){if(Convert.ToString(runtime.Get(instance,"OperatingState"))!="Off")runtime.Call(instance,"PowerOff",(uint)30000);runtime.Call(instance,"UnregisterInstance");Console.WriteLine("CLEANUP unregistered "+name);}
        }
        Console.WriteLine("AFTER "+json.Serialize(sim.Status()));
        Console.WriteLine("Live lifecycle only; no PLC program download, no successful symbol/data-flow claim.");
    }
    static void Main(string[] args){try{if(args[0]=="live")Live(args[1]);else if(args[0]=="contract")Contract(args[1]);else Offline(args[1]);Console.WriteLine("PASS: "+count+" Advanced runtime checks");}catch(Exception ex){Console.Error.WriteLine(ex);Environment.ExitCode=1;}}
}

// Minimal V8-shaped SDK: exercises reflection dispatch without loading Siemens native code.
namespace Siemens.Simatic.Simulation.Runtime
{
    public enum ECPUType { CPU1513 }
    public interface IInstance { string Name{get;} string CommunicationInterface{get;} bool ReadBool(string tag);void WriteBool(string tag,bool value);uint ReadUInt32(string tag);int ReadInt32(string tag);float ReadFloat(string tag); }
    public sealed class FakeInstance:IInstance {
        public string Mode="Softbus";public string Name{get;set;}public string CommunicationInterface{get{return Mode;}}private bool bit;
        public bool ReadBool(string tag){if(tag=="missing")throw new InvalidOperationException("native failure");return bit;}
        public void WriteBool(string tag,bool value){bit=value;}public uint ReadUInt32(string tag){return 42;}public int ReadInt32(string tag){return -4;}public float ReadFloat(string tag){return 1.5f;}
    }
    public static class SimulationRuntimeManager {
        public static IInstance RegisterInstance(string article,string name){throw new Exception("wrong overload");}
        public static IInstance RegisterInstance(ECPUType cpu,string name){return new FakeInstance{Name=name};}
    }
}
