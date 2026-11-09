using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using TiaPortalSourceBridge.Configuration;
using TiaPortalSourceBridge.Mcp;

namespace TiaPortalSourceBridge.Web
{
    internal sealed partial class WebServer
    {
        private bool HandlePeripheral(HttpListenerContext context,string path)
        {
            const string prefix="/api/io/peripheral/";if(!path.StartsWith(prefix,StringComparison.Ordinal))return false;var response=context.Response;response.Headers["Cache-Control"]="no-store";
            try{
                var request=context.Request;if(request.HttpMethod!="POST"){SendJson(response,405,new{error="请使用仿真规则面板"});return true;}if(request.ContentLength64<0||request.ContentLength64>256*1024)throw new ArgumentException("仿真配置过大。");
                var map=CylinderWritePipeline.Json().Deserialize<Dictionary<string,object>>(ReadBody(request));if(map==null)throw new ArgumentException("缺少仿真配置。");string project=Text(map,"project_path"),plc=Text(map,"plc_name"),action=path.Substring(prefix.Length);
                if(!File.Exists(project)||plc.Length==0||!actionTargets.Verify(project,plc,Text(map,"target_token")))throw new InvalidOperationException("工程或 PLC 目标已失效。");
                if(AppConfig.DetectProjectVersion(project)!="V17")throw new InvalidOperationException("新增外设规则仅支持 V17；V21 仿真能力保持原有范围。");
                if(action=="start"&&shutdownRequested)throw new InvalidOperationException("看板正在退出。");
                var args=new Dictionary<string,object>{{"project_path",project},{"instance_name",Text(map,"instance_name")},{"portal_version","V17"},{"run_id",Text(map,"run_id")}};
                if(action=="export"){
                    Guid id;if(!Guid.TryParseExact(Text(map,"run_id"),"N",out id))throw new ArgumentException("仿真编号无效。");string root=Path.Combine(Path.GetDirectoryName(_configPath),"simulation-runs"),file=SnapshotDiff.Confined(root,id.ToString("N")+".json");if(!File.Exists(file)||new FileInfo(file).Length>16*1024*1024)throw new InvalidOperationException("仿真记录缺失或过大。");
                    var record=CylinderWritePipeline.Json().Deserialize<Dictionary<string,object>>(File.ReadAllText(file));if(!PathsEqual(Text(record,"project_path"),project)||Text(record,"instance_name")!=Text(map,"instance_name"))throw new InvalidOperationException("仿真记录目标不一致。");
                    if(Text(record,"state")=="running")throw new InvalidOperationException("请等本次运行结束后导出完整记录。");string events=SnapshotDiff.Confined(root,id.ToString("N")+".jsonl");if(!File.Exists(events)||new FileInfo(events).Length>64*1024*1024)throw new InvalidOperationException("完整记录缺失或超过 64 MiB。");byte[] bytes=File.ReadAllBytes(events);response.ContentType="application/x-ndjson";response.Headers["Content-Disposition"]="attachment; filename=\"TIA-Simulation-"+id.ToString("N")+".jsonl\"";response.ContentLength64=bytes.Length;response.OutputStream.Write(bytes,0,bytes.Length);response.Close();return true;
                }
                if(action=="start"){object confirmed;if(!map.TryGetValue("confirm",out confirmed)||!Equals(confirmed,true))throw new InvalidOperationException("请确认只在受管本机仿真实例内执行规则。");args["confirm"]=true;args["configuration_json"]=Text(map,"configuration_json");}
                Dictionary<string,object> reply;lock(DashboardGate)reply=Dashboard(_configPath).DashboardPeripheralCall(action,args);
                var data=CylinderWritePipeline.Data(reply);if(data.Count==0&&CylinderWritePipeline.True(reply,"isError"))throw new InvalidOperationException(CylinderWritePipeline.Error(reply));SendJson(response,200,data);return true;
            }catch(Exception ex){SendJson(response,409,new{error=ex.Message});return true;}
        }
    }
}
