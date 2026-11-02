using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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
        internal static string FindApi()
        {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86), "Siemens", "PLCSIMADV", "API");
            if (!Directory.Exists(root)) throw new InvalidOperationException("未安装 PLCSIM Advanced API；普通 PLCSIM 不提供本接口。");
            // V8 changed the API contract. Do not silently load an unverified newer major version.
            foreach (string version in new[] { "5.0", "7.0", "6.0", "4.1", "4.0", "3.0", "2.1" })
            {
                string path = Path.Combine(root, version, "Siemens.Simatic.Simulation.Runtime.Api.x64.dll");
                if (File.Exists(path)) return path;
            }
            throw new InvalidOperationException("未发现支持的 PLCSIM Advanced API 2.1..7.0。API V8 需要独立适配。");
        }
        internal AdvancedRuntime()
        {
            ApiPath = FindApi(); assembly = Assembly.LoadFrom(ApiPath);
            manager = assembly.GetType("Siemens.Simatic.Simulation.Runtime.SimulationRuntimeManager", true);
            instanceType = assembly.GetType("Siemens.Simatic.Simulation.Runtime.IInstance", true);
        }
        private Type TypeOf(object instance) { return instance == null ? manager : instanceType; }
        public object Get(object instance, string name)
        {
            OperationMetrics.Count("simulation_api_calls");
            return TypeOf(instance).GetProperty(name).GetValue(instance, null);
        }
        private static object ConvertArg(object value, Type target)
        { return target.IsEnum ? Enum.Parse(target, Convert.ToString(value), false) : value; }
        public void Set(object instance, string name, object value)
        {
            OperationMetrics.Count("simulation_api_calls");
            PropertyInfo property = TypeOf(instance).GetProperty(name);
            property.SetValue(instance, ConvertArg(value, property.PropertyType), null);
        }
        public object Call(object instance, string method, params object[] args)
        {
            MethodInfo selected = TypeOf(instance).GetMethods().Where(m => m.Name == method && m.GetParameters().Length == args.Length)
                .FirstOrDefault(m => m.GetParameters().Select((p, i) => p.ParameterType.IsEnum ? args[i] is string : p.ParameterType.IsInstanceOfType(args[i])).All(x => x));
            if (selected == null) throw new MissingMethodException(TypeOf(instance).FullName, method);
            object[] converted = selected.GetParameters().Select((p, i) => ConvertArg(args[i], p.ParameterType)).ToArray();
            OperationMetrics.Count("simulation_api_calls");
            try { return selected.Invoke(instance, converted); }
            catch (TargetInvocationException ex) { throw new InvalidOperationException("PLCSIM Advanced " + method + " 失败：" + ex.InnerException.Message, ex.InnerException); }
        }
        public object[] Instances() { return ((System.Collections.IEnumerable)Get(null, "RegisteredInstanceInfo")).Cast<object>().ToArray(); }
        public object Register(string name, string cpu) { return Call(null, "RegisterInstance", cpu, name); }
        public object Open(string name) { return Call(null, "CreateInterface", name); }
    }
}
