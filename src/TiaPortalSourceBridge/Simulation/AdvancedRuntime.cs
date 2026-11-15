using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Diagnostics;
using TiaPortalSourceBridge.Openness;

namespace TiaPortalSourceBridge.Simulation
{
    internal interface ISimulationRuntime
    {
        string ApiPath { get; }
        object[] Instances();
        object Register(string name, string cpu);
        object Open(string name);
        object Get(object instance, string name);
        void Set(object instance, string name, object value);
        object Call(object instance, string method, params object[] args);
    }

    internal sealed class AdvancedRuntime : ISimulationRuntime
    {
        private readonly Assembly assembly;
        private readonly Type manager, instanceType;
        public string ApiPath { get; private set; }
        internal string ApiVersion { get { return assembly.GetName().Version.ToString(); } }
        internal static string ManagerPath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86), "Siemens", "PLCSIMADV", "Siemens.Simatic.Simulation.Runtime.Manager.exe"); } }
        internal static bool ManagerRunning { get { return Process.GetProcessesByName("Siemens.Simatic.Simulation.Runtime.Manager").Any() || Process.GetProcessesByName("Siemens.Simatic.Simulation.Runtime.Manager.x64").Any(); } }
        internal static string FindApi()
        {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86), "Siemens", "PLCSIMADV", "API");
            int major=InstalledRuntimeMajor();
            return FindApi(root,major);
        }
        private static int InstalledRuntimeMajor()
        {
            foreach(string path in new[]{@"SOFTWARE\WOW6432Node\Siemens\Shared Tools\PLCSIMADV_SimRT",@"SOFTWARE\Siemens\Shared Tools\PLCSIMADV_SimRT"})
                using(var key=Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path)){Version version;if(key!=null&&Version.TryParse(Convert.ToString(key.GetValue("Version")),out version))return version.Major;}
            int major=File.Exists(ManagerPath)?FileVersionInfo.GetVersionInfo(ManagerPath).FileMajorPart:0;
            // Siemens Runtime binaries use 80.x for product V8; the registry uses 8.x.
            return major>=20&&major%10==0?major/10:major;
        }
        internal static string FindApi(string root,int runtimeMajor)
        {
            if (!Directory.Exists(root)) throw new InvalidOperationException("未安装 PLCSIM Advanced API；普通 PLCSIM 不提供本接口。");
            if(runtimeMajor>8)throw new InvalidOperationException("当前 Runtime V"+runtimeMajor+" 尚未适配；不会静默改用旧版 API。");
            // Match the installed Runtime, not a legacy API directory left by an upgrade.
            foreach (string version in new[] { "8.0", "7.0", "6.0", "5.0", "4.1", "4.0", "3.0", "2.1" })
            {
                if(runtimeMajor>0&&new Version(version).Major!=runtimeMajor)continue;
                string path = Path.Combine(root, version, "Siemens.Simatic.Simulation.Runtime.Api.x64.dll");
                if (File.Exists(path)) return path;
            }
            throw new InvalidOperationException("未发现与 Runtime 匹配的 PLCSIM Advanced API（支持 2.1–8.0）；请修复 Advanced 安装。");
        }
        internal AdvancedRuntime()
            : this(Assembly.LoadFrom(FindApi()),FindApi()) {}
        internal AdvancedRuntime(Assembly sdk,string path)
        {
            ApiPath = path; assembly = sdk;
            manager = assembly.GetType("Siemens.Simatic.Simulation.Runtime.SimulationRuntimeManager", true);
            instanceType = assembly.GetType("Siemens.Simatic.Simulation.Runtime.IInstance", true);
        }
        private Type TypeOf(object instance) { return instance == null ? manager : instanceType; }
        public object Get(object instance, string name)
        {
            OperationMetrics.Count("simulation_api_calls");
            if(instance==null&&name=="IsRuntimeManagerAvailable"&&!ManagerRunning)return false;
            return ReadProperty(instance,name);
        }
        private object ReadProperty(object instance,string name)
        {
            var property=TypeOf(instance).GetProperty(name);
            if(property==null)throw new MissingMemberException(TypeOf(instance).FullName,name);
            try{return property.GetValue(instance, null);}
            catch(TargetInvocationException ex){throw NativeError(name,ex);}
        }
        private static object ConvertArg(object value, Type target)
        { return target.IsEnum ? Enum.Parse(target, Convert.ToString(value), false) : value; }
        public void Set(object instance, string name, object value)
        {
            OperationMetrics.Count("simulation_api_calls");
            PropertyInfo property = TypeOf(instance).GetProperty(name);
            if(property==null)throw new MissingMemberException(TypeOf(instance).FullName,name);
            if(!property.CanWrite&&instance!=null&&name=="CommunicationInterface"&&Convert.ToString(value)=="Softbus"){
                if(Convert.ToString(Get(instance,name))!="Softbus")throw new InvalidOperationException("V8 实例未处于 Softbus；拒绝改变其他实例使用的全局通信模式。");
                return;
            }
            try{property.SetValue(instance, ConvertArg(value, property.PropertyType), null);}
            catch(TargetInvocationException ex){throw NativeError(name,ex);}
        }
        private static Exception NativeError(string operation,TargetInvocationException ex)
        {
            var cause=ex.InnerException??ex;
            string code=cause.Message.Contains("-1073741819")?"0xC0000005":cause.Message.Contains("-1073740791")?"0xC0000409":cause.Message.Contains("-2147483644")?"0x80000004":"";
            string hint=code.Length==0||operation!="IsRuntimeManagerAvailable"?"":"西门子 Runtime Manager 初始化崩溃（"+code+"），尚未进入 PLC 工程加载。请检查官方 Advanced 控制面板和 Windows 应用程序事件日志；";
            return new InvalidOperationException("PLCSIM Advanced "+operation+" 失败（"+cause.GetType().Name+"）："+hint+cause.Message,cause);
        }
        internal static MethodInfo SelectMethod(Type type,string name,object[] args)
        {
            var candidates=type.GetMethods().Where(m=>m.Name==name&&m.GetParameters().Length==args.Length)
                .Where(m=>m.GetParameters().Select((p,i)=>p.ParameterType.IsInstanceOfType(args[i])||p.ParameterType.IsEnum&&args[i] is string&&Enum.GetNames(p.ParameterType).Contains((string)args[i])).All(x=>x)).ToArray();
            if(candidates.Length!=1)throw new MissingMethodException(type.FullName,name+"：未找到唯一匹配的 API 签名。");
            return candidates[0];
        }
        public object Call(object instance, string method, params object[] args)
        {
            MethodInfo selected = SelectMethod(TypeOf(instance),method,args);
            object[] converted = selected.GetParameters().Select((p, i) => ConvertArg(args[i], p.ParameterType)).ToArray();
            OperationMetrics.Count("simulation_api_calls");
            try { return selected.Invoke(instance, converted); }
            catch (TargetInvocationException ex) { throw NativeError(method,ex); }
        }
        public object[] Instances() { return !ManagerRunning?new object[0]:((System.Collections.IEnumerable)Get(null, "RegisteredInstanceInfo")).Cast<object>().ToArray(); }
        internal static object CpuArgument(Assembly sdk,string cpu)
        {var type=sdk.GetType("Siemens.Simatic.Simulation.Runtime.ECPUType",true);if(!Enum.GetNames(type).Contains(cpu))throw new ArgumentException("当前 API 不支持该 CPU 类型："+cpu);return Enum.Parse(type,cpu,false);}
        private void EnsureReady()
        {
            // The Siemens API owns Runtime Manager startup/shutdown. Launching its EXE
            // independently races that lifecycle and can exit before API initialization.
            // Bypass only the read-only status guard, on this explicit start operation.
            OperationMetrics.Count("simulation_api_calls");
            InitializeRuntime(()=>Convert.ToBoolean(ReadProperty(null,"IsRuntimeManagerAvailable")));
        }
        internal static void InitializeRuntime(Func<bool> initialize)
        {
            if(!initialize())throw new InvalidOperationException("PLCSIM Advanced Runtime Manager 未就绪，未创建实例；工具不自动重复启动。请检查官方 Advanced 控制面板和 Windows 应用程序事件日志。");
        }
        internal static void SelectSoftbus(bool isSoftbus,Func<int> existingCount,Action setMode)
        {if(isSoftbus)return;if(existingCount()!=0)throw new InvalidOperationException("V8 使用全局通信模式，已有仿真实例；请先自行核对并切换为 PLCSIM/Softbus，工具不会更改它们的模式。");setMode();}
        public object Register(string name, string cpu) {
            var argument=CpuArgument(assembly,cpu);EnsureReady();
            if(!instanceType.GetProperty("CommunicationInterface").CanWrite){
                var network=manager.GetProperty("NetworkMode");
                string softbus=Enum.GetNames(network.PropertyType).Single(n=>n.Equals("Softbus",StringComparison.OrdinalIgnoreCase));
                SelectSoftbus(Convert.ToString(Get(null,"NetworkMode")).Equals(softbus,StringComparison.OrdinalIgnoreCase),()=>Instances().Length,()=>Set(null,"NetworkMode",softbus));
                if(!Convert.ToString(Get(null,"NetworkMode")).Equals(softbus,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("未确认 V8 Softbus 模式，未创建实例。");
            }
            return Call(null, "RegisterInstance", argument, name);
        }
        public object Open(string name) { return Call(null, "CreateInterface", name); }
    }
}
