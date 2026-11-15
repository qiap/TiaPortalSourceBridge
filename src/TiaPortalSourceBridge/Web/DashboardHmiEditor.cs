using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using TiaPortalSourceBridge.Configuration;
using TiaPortalSourceBridge.Mcp;

namespace TiaPortalSourceBridge.Web
{
    internal sealed partial class WebServer
    {
        private readonly object hmiEditorGate=new object();
        private bool HandleHmiEditor(HttpListenerContext context,string path)
        {
            const string prefix="/api/io/hmi-editor/";if(!path.StartsWith(prefix,StringComparison.Ordinal))return false;
            var response=context.Response;response.Headers["Cache-Control"]="no-store";
            try{
                if(context.Request.HttpMethod!="POST"){SendJson(response,405,new{error="请使用页面编辑器。"});return true;}
                if(context.Request.ContentLength64<0||context.Request.ContentLength64>2*1024*1024)throw new ArgumentException("页面修改请求过大。");
                var json=CylinderWritePipeline.Json();var map=json.Deserialize<Dictionary<string,object>>(ReadBody(context.Request));if(map==null)throw new ArgumentException("缺少页面参数。");
                string project=Text(map,"project_path"),plc=Text(map,"plc_name"),hmi=Text(map,"hmi_name"),action=path.Substring(prefix.Length);
                if(!File.Exists(project)||plc.Length==0||!actionTargets.Verify(project,plc,Text(map,"target_token")))throw new InvalidOperationException("工程或 PLC 选择已变化，请重新读取。");
                if(AppConfig.DetectProjectVersion(project)!="V17")throw new InvalidOperationException("页面编辑器目前支持 V17 经典 WinCC。");
                string folder=Path.Combine(Path.GetDirectoryName(_configPath),"logs","hmi-editor");Directory.CreateDirectory(folder);
                Func<string,string,string> file=(id,suffix)=>{Guid guid;if(!Guid.TryParseExact(id,"N",out guid))throw new ArgumentException("执行编号无效。");return SnapshotDiff.Confined(folder,id+suffix);};
                Func<string,Dictionary<string,object>> load=name=>{if(!File.Exists(name)||new FileInfo(name).Length>32*1024*1024)throw new ArgumentException("记录不存在或过大。");return json.Deserialize<Dictionary<string,object>>(File.ReadAllText(name));};
                Action<string,object> save=(name,value)=>{string temp=name+".tmp";File.WriteAllText(temp,json.Serialize(value),new UTF8Encoding(false));if(File.Exists(name))File.Replace(temp,name,null);else File.Move(temp,name);};
                Action<Dictionary<string,object>> target=record=>{if(!PathsEqual(project,Text(record,"project_path"))||plc!=Text(record,"plc_name")||hmi!=Text(record,"hmi_name"))throw new InvalidOperationException("记录不属于当前工程和 HMI。");};
                if(action=="recent"){
                    var jobs=new List<object>();foreach(string candidate in Directory.EnumerateFiles(folder,"*.job.json").OrderByDescending(File.GetLastWriteTimeUtc).Take(100)){
                        var row=load(candidate);if(PathsEqual(project,Text(row,"project_path"))&&plc==Text(row,"plc_name")&&(hmi.Length==0||hmi==Text(row,"hmi_name")))jobs.Add(row);}
                    SendJson(response,200,new{jobs});return true;
                }
                if(action=="status"){
                    string id=Text(map,"job_id");var job=load(file(id,".job.json"));target(job);
                    if(File.Exists(file(id,".reply.json"))){SendJson(response,200,load(file(id,".reply.json")));return true;}
                    if(File.Exists(file(id,".result.json"))){var data=load(file(id,".result.json"));target(data);string state=Text(data,"state");var active=ActionRunner.FindTask(id);if(!new[]{"completed","failed","needs_review"}.Contains(state)&&(active==null||active.status!="running"))state="unknown";SendJson(response,200,new{job_id=id,state,data,error=state=="unknown"?"任务已中断或结果尚未确认，请核对原记录，不要重复写入。":Text(data,"error")});return true;}
                    var running=ActionRunner.FindTask(id);SendJson(response,200,new{job_id=id,state=running==null?"unknown":running.status,error=running==null?"未取得最终结果；不要重复提交写入。":running.message});return true;
                }
                if(!new[]{"catalog","read","preview","preview_batch","apply"}.Contains(action))throw new ArgumentException("未知页面操作。");
                if(action!="catalog"&&string.IsNullOrWhiteSpace(hmi))throw new ArgumentException("请选择 HMI 设备。");
                lock(hmiEditorGate){
                    if(shutdownRequested||ActionRunner.AnyBlockingRunning)throw new InvalidOperationException("工程通道正在处理其他任务，请等待完成。");
                    bool apply=action=="apply";string id=apply?Text(map,"id"):Guid.NewGuid().ToString("N"),jobFile=file(id,".job.json");
                    var args=new Dictionary<string,object>{{"project_path",project},{"plc_name",plc},{"hmi_name",hmi},{"action",action},{"response_mode","compact"},{"bridge_operation_id",id}};
                    if(apply){
                        if(!CylinderWritePipeline.True(map,"confirm")||!CylinderWritePipeline.True(map,"complete_screen_confirm"))throw new InvalidOperationException("请核对页面修改、特殊控件范围及整个工程保存范围。");
                        string planFile=file(id,".plan.json");var plan=load(planFile);target(plan);
                        if(Text(map,"fingerprint")!=XmlBatch.Hash(File.ReadAllText(planFile)))throw new InvalidOperationException("页面方案已变化。");
                        if(File.Exists(jobFile)||File.Exists(file(id,".claimed")))throw new InvalidOperationException("方案已提交，请打开原执行记录，不能重复写入。");
                        args["id"]=id;args["fingerprint"]=Text(map,"fingerprint");args["confirm"]=true;args["complete_screen_confirm"]=true;
                    }else foreach(string key in new[]{"kind","name","group_path","snapshot_id","changes_json","pages_json"})args[key]=Text(map,key);
                    var job=new Dictionary<string,object>{{"job_id",id},{"project_path",project},{"plc_name",plc},{"hmi_name",hmi},{"action",action},{"name",Text(map,"name")},{"created_utc",DateTime.UtcNow.ToString("o")}};
                    File.WriteAllText(jobFile,json.Serialize(job),new UTF8Encoding(false));
                    bool started=ActionRunner.StartEngineeringTask(_configPath,project,plc,"HMI 页面 · "+action,apply?"正在核对并写入页面":"正在读取页面或校验修改",progress=>{
                        Dictionary<string,object> outcome;
                        try{
                            Dictionary<string,object> reply;lock(DashboardGate)reply=Dashboard(_configPath).DashboardHmiEditorCall(apply,args);
                            bool failed=CylinderWritePipeline.True(reply,"isError");var data=CylinderWritePipeline.Data(reply);
                            bool unknown=apply&&failed&&!new[]{"failed","needs_review"}.Contains(Text(data,"state"));
                            outcome=new Dictionary<string,object>{{"job_id",id},{"state",unknown?"unknown":failed?(Text(data,"state").Length>0?Text(data,"state"):"failed"):"completed"},{"data",data},{"error",failed?CylinderWritePipeline.Error(reply):""}};
                            save(file(id,".reply.json"),outcome);
                            if(unknown)throw new EngineeringOutcomeUnknownException("页面写入结果未确认，请检查原记录和备份。",new InvalidOperationException(CylinderWritePipeline.Error(reply)));
                            if(failed)throw new InvalidOperationException(CylinderWritePipeline.Error(reply));return "页面操作已返回，请查看编辑器中的详细结果。";
                        }catch(Exception ex){
                            if(!File.Exists(file(id,".reply.json"))){outcome=new Dictionary<string,object>{{"job_id",id},{"state",apply?"unknown":"failed"},{"error",ex.Message}};save(file(id,".reply.json"),outcome);if(apply)throw new EngineeringOutcomeUnknownException("页面写入结果未确认，请检查原记录和备份。",ex);}throw;
                        }
                    },id);
                    if(!started){File.WriteAllText(file(id,".reply.json"),json.Serialize(new{job_id=id,state="not_started",error="工程通道被其他任务占用；本次未调用博途。"}));throw new InvalidOperationException("任务未开始，请检查记录。");}
                    SendJson(response,202,new{job_id=id,state="accepted"});return true;
                }
            }catch(Exception ex){SendJson(response,409,new{error=ex.Message});return true;}
        }
    }
}
