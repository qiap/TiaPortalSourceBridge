using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using TiaPortalSourceBridge.Online;

namespace TiaPortalSourceBridge.Web
{
    internal sealed partial class WebServer
    {
        private readonly object monitorGate=new object();
        private OnlineMonitor onlineMonitor;
        private void CloseOnlineMonitor(){lock(monitorGate){if(onlineMonitor!=null){onlineMonitor.Dispose();onlineMonitor=null;}}}
        private bool HandleOnlineMonitor(HttpListenerContext context,string path)
        {
            const string prefix="/api/io/monitor/";if(!path.StartsWith(prefix,StringComparison.Ordinal))return false;
            var request=context.Request;var response=context.Response;response.Headers["Cache-Control"]="no-store";
            try{
                string action=path.Substring(prefix.Length);if(request.HttpMethod!="POST"){SendJson(response,405,new{error="请使用监控面板按钮"});return true;}
                if(shutdownRequested)throw new InvalidOperationException("看板正在退出。");
                if(request.ContentLength64<0||request.ContentLength64>128*1024)throw new ArgumentException("监控配置过大。");
                var map=_json.Deserialize<Dictionary<string,object>>(ReadBody(request));if(map==null)throw new ArgumentException("缺少监控配置。");
                string project=Text(map,"project_path"),plc=Text(map,"plc_name");if(!File.Exists(project)||plc.Length==0||!actionTargets.Verify(project,plc,Text(map,"target_token")))throw new InvalidOperationException("当前工程与 PLC 目标已失效。");
                lock(monitorGate){
                    if(onlineMonitor==null)onlineMonitor=new OnlineMonitor(Path.GetDirectoryName(_configPath));object result;
                    if(action=="connect"){
                        object profile;if(!map.TryGetValue("profile",out profile))throw new ArgumentException("请配置需要读取的变量。");
                        result=onlineMonitor.Connect(project,plc,_json.Deserialize<OnlineProfile>(_json.Serialize(profile)),Text(map,"transport"),Text(map,"target_mode"));
                    }else if(action=="export"){
                        byte[] bytes=onlineMonitor.Export(project,plc,Text(map,"session_id"));response.ContentType="application/x-ndjson";response.Headers["Content-Disposition"]="attachment; filename=\"TIA-Trace.jsonl\"";response.ContentLength64=bytes.Length;response.OutputStream.Write(bytes,0,bytes.Length);response.Close();return true;
                    }else result=onlineMonitor.Execute(action,project,plc,Text(map,"session_id"),map);
                    SendJson(response,200,result);return true;
                }
            }catch(Exception ex){SendJson(response,409,new{error=ex.Message});return true;}
        }
    }
}
