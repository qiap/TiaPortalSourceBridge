using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using TiaPortalSourceBridge.Configuration;
using TiaPortalSourceBridge.Mcp;

namespace TiaPortalSourceBridge.Web
{
    internal sealed partial class WebServer
    {
        private readonly object hmiCompanionGate=new object();
        private bool HandleHmiCompanion(HttpListenerContext context,string path)
        {
            const string prefix="/api/io/hmi-companion/";if(!path.StartsWith(prefix,StringComparison.Ordinal))return false;var response=context.Response;response.Headers["Cache-Control"]="no-store";
            try{
                var request=context.Request;if(request.HttpMethod!="POST"){SendJson(response,405,new{error="请使用 HMI 联动面板"});return true;}if(request.ContentLength64<0||request.ContentLength64>256*1024)throw new ArgumentException("HMI 配置过大。");
                var json=CylinderWritePipeline.Json();var map=json.Deserialize<Dictionary<string,object>>(ReadBody(request));if(map==null)throw new ArgumentException("缺少 HMI 参数。");string project=Text(map,"project_path"),plc=Text(map,"plc_name"),hmi=Text(map,"hmi_name"),action=path.Substring(prefix.Length);
                if(!File.Exists(project)||plc.Length==0||!actionTargets.Verify(project,plc,Text(map,"target_token")))throw new InvalidOperationException("工程和 PLC 目标已失效。");if(AppConfig.DetectProjectVersion(project)!="V17")throw new InvalidOperationException("新增 HMI 联动仅支持 V17 经典 WinCC。");if(string.IsNullOrWhiteSpace(hmi)||hmi.Length>512)throw new ArgumentException("请填写完整的 HMI 名称。");
                lock(hmiCompanionGate){
                    string id=action=="preview"?Guid.NewGuid().ToString("N"):Text(map,"id");Guid guid;if(!Guid.TryParseExact(id,"N",out guid))throw new ArgumentException("HMI 执行编号无效。");
                    string folder=Path.Combine(Path.GetDirectoryName(_configPath),"logs","hmi-companion");Directory.CreateDirectory(folder);string identity=SnapshotDiff.Confined(folder,id+".request.json"),planFile=SnapshotDiff.Confined(folder,id+".plan.json"),resultFile=SnapshotDiff.Confined(folder,id+".json");
                    if(action!="preview"){
                        if(!File.Exists(identity))throw new InvalidOperationException("没有此 HMI 联动记录。");var original=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(identity));if(!PathsEqual(project,Text(original,"project_path"))||plc!=Text(original,"plc_name")||hmi!=Text(original,"hmi_name"))throw new InvalidOperationException("HMI 记录目标不一致。");
                    }
                    if(action=="status"){
                        foreach(string file in new[]{resultFile,planFile})if(File.Exists(file)&&new FileInfo(file).Length>16*1024*1024)throw new InvalidOperationException("HMI 记录过大。");
                        if(File.Exists(resultFile)){SendJson(response,200,json.DeserializeObject(File.ReadAllText(resultFile)));return true;}
                        if(File.Exists(planFile)&&!File.Exists(identity+".submitted")){string text=File.ReadAllText(planFile);var plan=json.Deserialize<HmiCompanionRecord>(text);SendJson(response,200,new{id,state="preview",fingerprint=XmlBatch.Hash(text),plan.configuration,plan.alarm_preview,text_before=plan.before_text,text_after=plan.after_text,note="报警触发位需人工核对与 PLC 报警位的对应关系；文本只回填已有单值索引。确认后保存整个离线工程，不下载。"});return true;}
                        var task=ActionRunner.FindTask(File.Exists(identity+".submitted")?id:id+"-preview");SendJson(response,200,new{id,state=task==null?"unknown":task.status,error=task==null?"没有最终结果，请核对记录和备份，不要重复提交。":task.message});return true;
                    }
                    if(action!="preview"&&action!="apply")throw new ArgumentException("不支持的 HMI 联动操作。");if(shutdownRequested||ActionRunner.AnyBlockingRunning)throw new InvalidOperationException("工程通道忙或正在退出。");
                    var args=new Dictionary<string,object>{{"project_path",project},{"plc_name",plc},{"hmi_name",hmi},{"bridge_operation_id",id}};bool apply=action=="apply";
                    if(apply){if(!CylinderWritePipeline.True(map,"confirm"))throw new InvalidOperationException("请先确认触发位、文本索引和整个工程保存范围。");if(!File.Exists(planFile)||File.Exists(identity+".submitted")||File.Exists(resultFile))throw new InvalidOperationException("方案不存在或已提交，不能重复执行。");if(Text(map,"fingerprint")!=XmlBatch.Hash(File.ReadAllText(planFile)))throw new InvalidOperationException("HMI 方案已变化。");args["fingerprint"]=Text(map,"fingerprint");args["confirm"]=true;File.WriteAllText(identity+".submitted",DateTime.UtcNow.ToString("o"));}
                    else{string config=Text(map,"configuration_json");HmiCompanionPlan.Validate(json.Deserialize<HmiCompanionConfiguration>(config));args["configuration_json"]=config;File.WriteAllText(identity,json.Serialize(args),new UTF8Encoding(false));}
                    bool started=ActionRunner.StartEngineeringTask(_configPath,project,plc,apply?"HMI 联动写入":"HMI 联动预览",apply?"核对并写入 HMI":"读取 HMI 报警和文本列表",progress=>{
                        Dictionary<string,object> reply;try{lock(DashboardGate)reply=Dashboard(_configPath).DashboardHmiCompanionCall(apply,args);}catch(Exception ex){if(apply)throw new EngineeringOutcomeUnknownException("HMI 最终状态未知，请核对执行记录与备份。",ex);throw;}
                        if(CylinderWritePipeline.True(reply,"isError"))throw new InvalidOperationException(CylinderWritePipeline.Error(reply));return apply?"HMI 联动已保存。":"HMI 联动预览已准备，请核对后再写入。";
                    },apply?id:id+"-preview");
                    if(!started)throw new InvalidOperationException("执行通道已占用，请重新预览。");SendJson(response,202,new{id,state="accepted"});return true;
                }
            }catch(Exception ex){SendJson(response,409,new{error=ex.Message});return true;}
        }
    }
}
