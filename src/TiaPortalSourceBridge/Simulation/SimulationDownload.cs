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
            bool dry=!args.ContainsKey("dry_run")||SimulationTools.Flag(args,"dry_run");
            var plan=SimulationTools.Map("project_path",session.GetProjectPath(),"plc",plc.DisplayName,"instance_name",SimulationTools.Text(args,"instance_name"),"dry_run",dry,"ok",false,"saved",false,"download_issued",false,"downloaded",false,"automatic_retry",false,"phase","preflight","compile_status","not_checked_read_only_preflight");
            var timeline=new List<object>();plan["timeline"]=timeline;string id=Guid.NewGuid().ToString("N");plan["id"]=id;
            Action<string> phase=p=>{plan["phase"]=p;timeline.Add(SimulationTools.Map("phase",p,"utc",DateTime.UtcNow.ToString("o")));};
            try {ExecuteCore(session,plc,simulations,args,plan,phase);}
            catch(Exception ex){plan["ok"]=false;if(Convert.ToString(plan["compile_status"])=="in_progress")plan["compile_status"]="unknown";plan["diagnostic"]=DownloadDiagnostics.Failure(ex,Convert.ToString(plan["phase"]),(bool)plan["download_issued"]);plan["state"]=(bool)plan["download_issued"]?"outcome_unknown":"blocked";}
            plan["completed_utc"]=DateTime.UtcNow.ToString("o");
            try{simulations.RecordDownload(id,plan);}catch(Exception ex){plan["receipt_error"]=ex.Message;plan["ok"]=false;}
            return plan;
        }
        static string OnlineState(ReflectionApi api,PlcContext plc,Dictionary<string,object> plan)
        {
            try{var online=api.InvokeGenericNoArguments(plc.DeviceItem,"GetService",api.RequireType("Siemens.Engineering.Online.OnlineProvider"));
                if(online==null)throw new InvalidOperationException("OnlineProvider 不可用。");string state=Convert.ToString(api.Get(online,"State",true));plan["tia_online_state"]=state;return state;}
            catch(Exception ex){plan["tia_online_state"]="unknown";plan["tia_online_state_error"]=ex.GetBaseException().Message;return "unknown";}
        }
        static void ExecuteCore(OpennessSession session,PlcContext plc,SimulationTools simulations,Dictionary<string,object> args,Dictionary<string,object> plan,Action<string> phase)
        {
            phase("preflight");
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
            var detail=(Dictionary<string,object>)simulations.Detail(instance);plan["instance"]=detail;plan["runtime_addresses"]=addresses;
            plan["softbus_interfaces"]=interfaces.Select(pc=>new {name=api.GetString(pc,"Name"),number=api.Get(pc,"Number",true)}).ToArray();
            var targets=interfaces.Count==1?api.Items(api.Get(interfaces[0],"TargetInterfaces",true)).ToArray():new object[0];
            var support=session.Project.GetType().GetProperty("IsSimulationDuringBlockCompilationEnabled");
            bool? enabled=support==null||!support.CanRead?(bool?)null:Convert.ToBoolean(support.GetValue(session.Project,null));
            string onlineState=OnlineState(api,plc,plan);
            var blockers=DownloadDiagnostics.Assess(Convert.ToString(detail["state"]),onlineState,interfaces.Count,addresses.Length,targets.Length,enabled,SimulationTools.Flag(args,"enable_simulation_support")).ToList();
            if(addresses.Length==1){try{simulations.EnsureUniqueAddress(instance,addresses[0]);}catch(Exception ex){blockers.Add(DownloadDiagnostics.Issue("INSTANCE_ADDRESS_UNVERIFIED",ex.Message,"核对受管实例和其他本机实例的 IP，重新预检。"));}}
            plan["blockers"]=blockers.ToArray();plan["preflight_ready"]=blockers.Count==0;plan["simulation_support_enabled"]=enabled;
            if(blockers.Count>0){plan["state"]="blocked";return;}
            if((bool)plan["dry_run"]){plan["ok"]=true;plan["state"]="preflight_ready";return;}
            if (!SimulationTools.Flag(args, "confirm")) throw new ArgumentException("装载到本机仿真必须 confirm=true。");
            if (interfaces.Count != 1) throw new InvalidOperationException("必须唯一匹配 PLCSIM Softbus 接口，实际匹配数：" + interfaces.Count + "；拒绝选择物理网卡。");
            if (addresses.Length != 1) throw new InvalidOperationException("当前仅支持恰好一个有效 IP 的受管本机仿真实例。");
            // Multiple local instances with the same address would make even a Softbus route ambiguous.
            simulations.EnsureUniqueAddress(instance, addresses[0]);
            if (support == null || !support.CanWrite) throw new InvalidOperationException("此 TIA 版本未提供可用的仿真编译开关。");
            phase("compile");
            if (!Convert.ToBoolean(support.GetValue(session.Project, null)))
            {
                if (!SimulationTools.Flag(args, "enable_simulation_support")) throw new InvalidOperationException("项目尚未启用仿真编译。明确传 enable_simulation_support=true 后可在离线项目启用；不会保存。");
                support.SetValue(session.Project, true, null);
                plan["simulation_support_enabled"]=true;plan["simulation_support_changed"]=true;
            }
            plan["compile_status"]="in_progress";
            CompileSummary compile = session.CompilePlc(plc);
            plan["compile_report"]=compile.ToReport();plan["compile_status"]=compile.ErrorCount==0?"passed":"failed";
            if (compile.ErrorCount > 0) {plan["state"]="blocked";plan["diagnostic"]=DownloadDiagnostics.Issue("COMPILE_ERRORS",compile.ToReport(),"先修复离线编译错误，再重新预检；未装载。");return;}
            phase("prepare_download");
            object pcInterface = interfaces[0];
            object target = targets[0];
            if (target == null) throw new InvalidOperationException("PLCSIM 接口没有可用目标接口。");
            object composition = api.Get(target, "Addresses", true);
            object onlineAddress = api.Items(composition).FirstOrDefault(a => api.GetString(a, "Address") == addresses[0]);
            if (onlineAddress == null) onlineAddress = api.Invoke(composition, "Create", addresses[0]);
            var decisions = new List<object>();plan["decisions"]=decisions;
            Action<object> callback = item => Configure(api, item, decisions);
            Type callbackType = api.RequireType("Siemens.Engineering.Download.DownloadConfigurationDelegate");
            var parameter = Expression.Parameter(callbackType.GetMethod("Invoke").GetParameters()[0].ParameterType, "configuration");
            Delegate handler = Expression.Lambda(callbackType, Expression.Invoke(Expression.Constant(callback), Expression.Convert(parameter, typeof(object))), parameter).Compile();
            // Revalidate immediately before issuing the single download. Never retry this call.
            instance = simulations.Owned(args); simulations.EnsureUniqueAddress(instance, addresses[0]);
            if(OnlineState(api,plc,plan)!="Offline")throw new InvalidOperationException("准备期间在线状态变化或不可确认，未发起下载。");
            object result;
            phase("download");plan["state"]="download_intent";plan["download_issued"]=true;
            try{simulations.RecordDownload(Convert.ToString(plan["id"]),plan);}catch{plan["download_issued"]=false;throw;}
            using (OperationMetrics.Measure("simulation_download"))
                result = api.Invoke(provider, "Download", target, onlineAddress, handler, handler,
                    api.EnumValue("Siemens.Engineering.Download.DownloadOptions", "Hardware, Software"));
            int errors = Convert.ToInt32(api.Get(result, "ErrorCount", true));
            plan["dry_run"] = false; plan["downloaded"] = DownloadDiagnostics.Succeeded(errors,api.GetString(result, "State"));
            plan["state"] = api.GetString(result, "State"); plan["error_count"] = errors; plan["warning_count"] = api.Get(result, "WarningCount", true);
            plan["decisions"] = decisions.ToArray(); plan["messages"] = Messages(api, api.Get(result, "Messages", false));
            plan["simulation_support_enabled"] = true;
            plan["ok"]=plan["downloaded"];
            if(!(bool)plan["ok"])plan["diagnostic"]=DownloadDiagnostics.Issue("DOWNLOAD_REPORTED_ERRORS","下载返回错误，请查看 messages。","核对实例实际状态后决定后续处理，不自动重新下载。");
            phase("complete");
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
