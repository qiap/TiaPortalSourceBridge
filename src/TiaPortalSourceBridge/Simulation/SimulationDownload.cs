using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using TiaPortalSourceBridge.Openness;

namespace TiaPortalSourceBridge.Simulation
{
    internal static class SimulationDownload
    {
        internal static object Execute(OpennessSession session, PlcContext plc, SimulationTools simulations, Dictionary<string, object> args)
        {
            object instance = simulations.Owned(args);
            var api = session.Api;
            if (plc.DeviceItem == null) throw new InvalidOperationException("当前 PLC 缺少可下载的 DeviceItem。");
            object provider = api.InvokeGenericNoArguments(plc.DeviceItem, "GetService", api.RequireType("Siemens.Engineering.Download.DownloadProvider"));
            if (provider == null) throw new InvalidOperationException("该 PLC 不提供 DownloadProvider。");
            object configuration = api.Get(provider, "Configuration", true);
            var interfaces = new List<object>();
            foreach (object mode in api.Items(api.Get(configuration, "Modes", true)))
                foreach (object pc in api.Items(api.Get(mode, "PcInterfaces", true)))
                    if (IsSoftbusInterface(api.GetString(pc, "Name"))) interfaces.Add(pc);
            string[] addresses = simulations.Addresses(instance).Where(a => a.Length > 0 && a != "0.0.0.0").Distinct().ToArray();
            var plan = SimulationTools.Map("project_path", session.GetProjectPath(), "plc", plc.DisplayName, "instance", simulations.Detail(instance),
                "softbus_interfaces", interfaces.Select(pc => new { name = api.GetString(pc, "Name"), number = api.Get(pc, "Number", true) }).ToArray(), "runtime_addresses", addresses, "saved", false);
            if (!args.ContainsKey("dry_run") || SimulationTools.Flag(args, "dry_run")) { plan["dry_run"] = true; return plan; }
            if (!SimulationTools.Flag(args, "confirm")) throw new ArgumentException("装载到本机仿真必须 confirm=true。");
            if (interfaces.Count != 1) throw new InvalidOperationException("必须唯一匹配 PLCSIM Softbus 接口，实际匹配数：" + interfaces.Count + "；拒绝选择物理网卡。");
            if (addresses.Length != 1) throw new InvalidOperationException("当前仅支持恰好一个有效 IP 的受管本机仿真实例。");
            // Multiple local instances with the same address would make even a Softbus route ambiguous.
            simulations.EnsureUniqueAddress(instance, addresses[0]);
            var support = session.Project.GetType().GetProperty("IsSimulationDuringBlockCompilationEnabled");
            if (support == null || !support.CanWrite) throw new InvalidOperationException("此 TIA 版本未提供可用的仿真编译开关。");
            if (!Convert.ToBoolean(support.GetValue(session.Project, null)))
            {
                if (!SimulationTools.Flag(args, "enable_simulation_support")) throw new InvalidOperationException("项目尚未启用仿真编译。明确传 enable_simulation_support=true 后可在离线项目启用；不会保存。");
                support.SetValue(session.Project, true, null);
            }
            CompileSummary compile = session.CompilePlc(plc);
            if (compile.ErrorCount > 0) throw new InvalidOperationException("仿真装载前编译失败，未装载：" + compile.ToReport());
            object pcInterface = interfaces[0];
            object target = api.Items(api.Get(pcInterface, "TargetInterfaces", true)).FirstOrDefault();
            if (target == null) throw new InvalidOperationException("PLCSIM 接口没有可用目标接口。");
            object composition = api.Get(target, "Addresses", true);
            object onlineAddress = api.Items(composition).FirstOrDefault(a => api.GetString(a, "Address") == addresses[0]);
            if (onlineAddress == null) onlineAddress = api.Invoke(composition, "Create", addresses[0]);
            var decisions = new List<object>();
            Action<object> callback = item => Configure(api, item, decisions);
            Type callbackType = api.RequireType("Siemens.Engineering.Download.DownloadConfigurationDelegate");
            var parameter = Expression.Parameter(callbackType.GetMethod("Invoke").GetParameters()[0].ParameterType, "configuration");
            Delegate handler = Expression.Lambda(callbackType, Expression.Invoke(Expression.Constant(callback), Expression.Convert(parameter, typeof(object))), parameter).Compile();
            // Revalidate immediately before issuing the single download. Never retry this call.
            instance = simulations.Owned(args); simulations.EnsureUniqueAddress(instance, addresses[0]);
            object result;
            using (OperationMetrics.Measure("simulation_download"))
                result = api.Invoke(provider, "Download", target, onlineAddress, handler, handler,
                    api.EnumValue("Siemens.Engineering.Download.DownloadOptions", "Hardware, Software"));
            int errors = Convert.ToInt32(api.Get(result, "ErrorCount", true));
            plan["dry_run"] = false; plan["downloaded"] = errors == 0 && api.GetString(result, "State") != "Error";
            plan["state"] = api.GetString(result, "State"); plan["error_count"] = errors; plan["warning_count"] = api.Get(result, "WarningCount", true);
            plan["decisions"] = decisions.ToArray(); plan["messages"] = Messages(api, api.Get(result, "Messages", false));
            plan["simulation_support_enabled"] = true;
            return plan;
        }
        internal static bool IsSoftbusInterface(string name)
        { return string.Equals(name, "PLCSIM", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "PLCSIM S7-1200/S7-1500", StringComparison.OrdinalIgnoreCase); }
        private static object[] Messages(ReflectionApi api, object composition)
        { return api.Items(composition).Select(m => (object)new { state = api.GetString(m, "State"), message = api.GetString(m, "Message"), children = Messages(api, api.Get(m, "Messages", false)) }).ToArray(); }
        private static void Configure(ReflectionApi api, object item, List<object> decisions)
        {
            string type = item.GetType().Name, selection;
            var options = new Dictionary<string, string> {
                { "StopModules", "StopAll" }, { "StartModules", "NoAction" }, { "AllBlocksDownload", "DownloadAllBlocks" },
                { "OverwriteSystemData", "Overwrite" }, { "ConsistentBlocksDownload", "ConsistentDownload" },
                { "AlarmTextLibrariesDownload", "ConsistentDownload" }, { "InitializeMemory", "AcceptAll" },
                { "DifferentTargetConfiguration", "AcceptAll" }, { "LoadIdentificationData", "LoadNothing" }
            };
            if (options.TryGetValue(type, out selection))
            {
                var property = item.GetType().GetProperty("CurrentSelection");
                property.SetValue(item, Enum.Parse(property.PropertyType, selection, false), null);
                decisions.Add(new { type = type, selection = selection, message = api.GetString(item, "Message") });
            }
            else if (item.GetType().GetProperty("CurrentSelection") != null || item.GetType().GetMethod("SetPassword") != null || item.GetType().GetProperty("Checked") != null)
                throw new InvalidOperationException("仿真装载需要尚未支持的配置决策，已停止：" + type + " " + api.GetString(item, "Message"));
            else decisions.Add(new { type = type, message = api.GetString(item, "Message") });
        }
    }
}
