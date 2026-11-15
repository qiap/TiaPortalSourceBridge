using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaPortalSourceBridge.Simulation
{
    internal static class DownloadDiagnostics
    {
        internal static bool Succeeded(int errors,string state){return errors==0&&new[]{"Success","Information","Warning"}.Contains(state);}
        internal static Dictionary<string,object> Issue(string code,string message,string next)
        {return SimulationTools.Map("code",code,"message",message,"next_action",next,"automatic_retry",false);}
        internal static Dictionary<string,object>[] Assess(string instanceState,string onlineState,int interfaces,int addresses,int targets,bool? support,bool allowEnable)
        {
            var issues=new List<Dictionary<string,object>>();
            if(instanceState!="Run"&&instanceState!="Stop")issues.Add(Issue("INSTANCE_NOT_READY","仿真实例未处于稳定的 RUN/STOP："+instanceState,"查询实例状态，等待启动完成后重新预检。"));
            if(onlineState=="Online")issues.Add(Issue("TIA_GUI_ONLINE","目标 PLC 的 OnlineProvider 报告 Online。","由工程师自行核对并退出该目标的在线监控，再重新预检。"));
            else if(onlineState!="Offline")issues.Add(Issue("TIA_ONLINE_STATE_UNKNOWN","无法确认目标 PLC 离线状态。","检查 OnlineProvider 查询证据，恢复状态查询后再装载。"));
            if(interfaces!=1)issues.Add(Issue("SOFTBUS_INTERFACE_NOT_UNIQUE","可用 PLCSIM Softbus 接口数量："+interfaces,"检查本机 PLCSIM 接口配置；不选择物理网卡。"));
            if(addresses!=1)issues.Add(Issue("INSTANCE_ADDRESS_NOT_UNIQUE","受管实例有效 IP 数量："+addresses,"核对仿真实例地址。"));
            if(interfaces==1&&targets!=1)issues.Add(Issue("SOFTBUS_TARGET_NOT_UNIQUE","Softbus 目标接口数量："+targets,"核对目标接口，不自动选择第一个。"));
            if(!support.HasValue)issues.Add(Issue("SIMULATION_SUPPORT_UNKNOWN","无法读取工程仿真编译设置。","检查工程版本及 API。"));
            else if(!support.Value&&!allowEnable)issues.Add(Issue("SIMULATION_SUPPORT_DISABLED","工程未启用仿真编译。","确认需要修改离线设置时，明确传 enable_simulation_support=true。"));
            return issues.ToArray();
        }
        internal static Dictionary<string,object> Failure(Exception ex,string phase,bool issued)
        {
            var parts=new List<string>();for(Exception e=ex;e!=null;e=e.InnerException)parts.Add(e.GetType().Name+": "+e.Message);
            string message=string.Join(" -> ",parts);
            string code=message.IndexOf("InterfaceRemoved",StringComparison.OrdinalIgnoreCase)>=0?"DOWNLOAD_INTERFACE_REMOVED":"SIMULATION_"+phase.ToUpperInvariant()+"_FAILED";
            var result=Issue(code,message,issued?"下载已发起，结果可能不确定；先核对实例与博途诊断，不要直接重下。":"尚未发起下载；核对阶段与原始错误后重新预检。");
            result["phase"]=phase;result["download_issued"]=issued;result["root_cause_confirmed"]=false;return result;
        }
    }
}
