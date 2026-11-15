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
        readonly object workbenchGate=new object();
        internal static Func<string,Dictionary<string,object>,Dictionary<string,object>> WorkbenchTestCall = null;
        Dictionary<string,object> WorkbenchCall(string operation,Dictionary<string,object> args)
        {
            Dictionary<string,object> reply;
            lock(DashboardGate)reply=WorkbenchTestCall!=null?WorkbenchTestCall(operation,args):Dashboard(_configPath).DashboardWorkbenchCall(operation,args);
            var data=CylinderWritePipeline.Data(reply);
            if(data.Count==0&&CylinderWritePipeline.True(reply,"isError"))throw new InvalidOperationException(CylinderWritePipeline.Error(reply));
            if(CylinderWritePipeline.True(reply,"isError"))data["tool_error"]=true;
            return data;
        }
        string WorkbenchFile(string id,string suffix=".json")
        {
            Guid guid;if(!Guid.TryParseExact(id,"N",out guid))throw new ArgumentException("记录编号无效。");
            string dir=Path.Combine(Path.GetDirectoryName(_configPath),"logs","workbench");Directory.CreateDirectory(dir);
            return SnapshotDiff.Confined(dir,id+suffix);
        }
        void SaveWorkbench(Dictionary<string,object> record)
        {
            record["updated"]=DateTime.UtcNow.ToString("o");string file=WorkbenchFile(Text(record,"id")),temp=file+".tmp";
            File.WriteAllText(temp,CylinderWritePipeline.Json().Serialize(record),new UTF8Encoding(false));if(File.Exists(file))File.Replace(temp,file,null);else File.Move(temp,file);
        }
        Dictionary<string,object> LoadWorkbench(string id,string project,string plc)
        {
            string file=WorkbenchFile(id);if(!File.Exists(file)||new FileInfo(file).Length>32*1024*1024)throw new InvalidOperationException("记录不存在或过大。");
            var record=CylinderWritePipeline.Json().Deserialize<Dictionary<string,object>>(File.ReadAllText(file));
            if(!PathsEqual(Text(record,"project_path"),project)||Text(record,"plc_name")!=plc)throw new InvalidOperationException("记录与当前工程/PLC 不一致。");
            return record;
        }
        static Dictionary<string,object> PickWorkbench(Dictionary<string,object> source,params string[] keys)
        {var result=new Dictionary<string,object>();foreach(string key in keys){object value;if(source.TryGetValue(key,out value))result[key]=value;}return result;}
        private bool HandleWorkbench(HttpListenerContext context,string path)
        {
            const string prefix="/api/io/workbench/";if(!path.StartsWith(prefix,StringComparison.Ordinal))return false;
            var request=context.Request;var response=context.Response;response.Headers["Cache-Control"]="no-store";
            try{
                if(request.HttpMethod!="POST"){SendJson(response,405,new{error="请使用工作区表单提交。"});return true;}
                if(request.ContentLength64<0||request.ContentLength64>256*1024)throw new ArgumentException("请求超过 256 KiB。");
                var map=CylinderWritePipeline.Json().Deserialize<Dictionary<string,object>>(ReadBody(request));if(map==null)throw new ArgumentException("缺少请求。");
                string project=Text(map,"project_path"),plc=Text(map,"plc_name"),operation=path.Substring(prefix.Length);
                if(!File.Exists(project)||plc.Length==0||!actionTargets.Verify(project,plc,Text(map,"target_token")))throw new InvalidOperationException("工程或 PLC 目标已失效，请刷新状态。");
                var args=new Dictionary<string,object>{{"project_path",project},{"plc_name",plc},{"response_mode","compact"}};
                if(operation=="block-read"){
                    foreach(var pair in PickWorkbench(map,"block_name","group_path","unit_name","read_mode","format","network_offset","network_limit"))args[pair.Key]=pair.Value;
                    SendJson(response,200,WorkbenchCall(operation,args));return true;
                }
                if(operation=="compare"){
                    var compare=PickWorkbench(map,"project_path_a","plc_name_a","group_path_a","unit_name_a","project_path_b","plc_name_b","group_path_b","unit_name_b","read_mode","offset","limit","catalog_token");
                    if(!File.Exists(Text(compare,"project_path_a"))||!File.Exists(Text(compare,"project_path_b")))throw new ArgumentException("请选择两侧实际工程文件。");
                    compare["response_mode"]="compact";SendJson(response,200,WorkbenchCall(operation,compare));return true;
                }
                if(operation=="job"){
                    lock(workbenchGate){var record=LoadWorkbench(Text(map,"id"),project,plc);var task=ActionRunner.FindTask(Text(record,"id"));
                        if(new[]{"accepted","running"}.Contains(Text(record,"state"))&&(task==null||!ActionRunner.IsPending(task.status))){record["state"]="outcome_unknown";record["automatic_retry"]=false;record["error"]="未取得最终执行结果，请保留现场并核对凭证。";}
                        SendJson(response,200,record);return true;}
                }
                if(operation=="download-status"){
                    Guid id;if(!Guid.TryParseExact(Text(map,"id"),"N",out id))throw new ArgumentException("下载凭证编号无效。");
                    string file=SnapshotDiff.Confined(Path.Combine(Path.GetDirectoryName(_configPath),"simulation-downloads"),id.ToString("N")+".json");
                    if(new FileInfo(file).Length>32*1024*1024)throw new ArgumentException("凭证过大。");
                    var record=CylinderWritePipeline.Json().Deserialize<Dictionary<string,object>>(File.ReadAllText(file));
                    if(!PathsEqual(Text(record,"project_path"),project)||Text(record,"plc")!=plc)throw new InvalidOperationException("下载凭证目标不一致。");
                    if(Text(record,"state")=="download_intent")record["state"]="outcome_unknown";SendJson(response,200,record);return true;
                }
                if(!new[]{"logic-preview","logic-status","logic-apply","download-preflight","download-apply"}.Contains(operation)){SendJson(response,404,new{error="未知工作区接口。"});return true;}
                if(AppConfig.DetectProjectVersion(project)!="V17")throw new InvalidOperationException("新的预览/写入向导仅支持 V17；V21 保持已有能力。");
                if(operation=="logic-status"){
                    args["plan_id"]=Text(map,"plan_id");SendJson(response,200,WorkbenchCall(operation,args));return true;
                }
                if(operation=="logic-preview"||operation=="download-preflight"){
                    if(operation=="logic-preview")foreach(var pair in PickWorkbench(map,"block_name","group_path","edit_json"))args[pair.Key]=pair.Value;
                    else{args["instance_name"]=Text(map,"instance_name");args["dry_run"]=true;args["enable_simulation_support"]=CylinderWritePipeline.True(map,"enable_simulation_support");}
                    var data=WorkbenchCall(operation,args);string id=Text(data,operation=="logic-preview"?"plan_id":"id");Guid guid;
                    if(!Guid.TryParseExact(id,"N",out guid))throw new InvalidOperationException("预览未返回持久化编号。");
                    var record=new Dictionary<string,object>{{"id",id},{"kind",operation=="logic-preview"?"logic_plan":"download"},{"project_path",project},{"plc_name",plc},{"block_name",Text(args,"block_name")},{"instance_name",Text(args,"instance_name")},{"state","preview"},{"args",args},{"result",data},{"saved",false}};
                    record["fingerprint"]=XmlBatch.Hash(CylinderWritePipeline.Json().Serialize(args)+CylinderWritePipeline.Json().Serialize(data));
                    lock(workbenchGate)SaveWorkbench(record);SendJson(response,200,record);return true;
                }
                if(shutdownRequested)throw new InvalidOperationException("看板正在退出。");
                if(!CylinderWritePipeline.True(map,"confirm"))throw new InvalidOperationException("请核对预览并勾选确认。");
                lock(workbenchGate){
                    var record=LoadWorkbench(Text(map,"id"),project,plc);string id=Text(record,"id");
                    if(Text(record,"kind")!=(operation=="logic-apply"?"logic_plan":"download")||Text(record,"fingerprint")!=Text(map,"fingerprint"))throw new InvalidOperationException("预览与当前提交不一致，请重新核对。");
                    if(Text(record,"state")!="preview"||File.Exists(WorkbenchFile(id,".submitted")))throw new InvalidOperationException("已提交过此方案，请回查记录，不要重复提交。");
                    var preview=(Dictionary<string,object>)record["result"];
                    if(CylinderWritePipeline.True(preview,"tool_error")||operation=="download-apply"&&!CylinderWritePipeline.True(preview,"preflight_ready"))throw new InvalidOperationException("预检未通过，禁止执行。");
                    var savedArgs=(Dictionary<string,object>)record["args"];args=PickWorkbench(savedArgs,"project_path","plc_name","instance_name","enable_simulation_support");args["confirm"]=true;args["response_mode"]="compact";
                    if(operation=="logic-apply"){args["plan_id"]=id;args["bridge_operation_id"]=id;}else args["dry_run"]=false;
                    if(ActionRunner.AnyBlockingRunning)throw new InvalidOperationException("工程通道正忙，请等待当前任务完成。");
                    using(var claim=new FileStream(WorkbenchFile(id,".submitted"),FileMode.CreateNew,FileAccess.Write)){}
                    record["state"]="accepted";record["automatic_retry"]=false;SaveWorkbench(record);
                    bool started=ActionRunner.StartEngineeringTask(_configPath,project,plc,operation,"正在重新核对目标并执行已确认方案",progress=>{
                        try{
                            Dictionary<string,object> value;
                            try{value=WorkbenchCall(operation,args);}
                            catch(Exception ex){lock(workbenchGate){record["state"]="outcome_unknown";record["error"]=ex.Message;SaveWorkbench(record);}throw new EngineeringOutcomeUnknownException("结果待确认，请查询原记录，不要直接重新提交。",ex);}
                            string state=operation=="logic-apply"?Text(value,"state"):CylinderWritePipeline.True(value,"downloaded")?"completed":Text(value,"state");
                            lock(workbenchGate){record["result"]=value;record["state"]=state.Length>0?state:"needs_review";record["error"]=Text(value,"error");SaveWorkbench(record);}
                            if(new[]{"outcome_unknown","needs_review","needs_review_unknown_outcome"}.Contains(state))throw new EngineeringOutcomeUnknownException("执行结果需要核对，请保留现场。",new InvalidOperationException(Text(value,"error")));
                            if(!new[]{"verified_unsaved","completed"}.Contains(state))throw new InvalidOperationException("执行未通过："+state+"，请查看记录中的诊断。");
                            return "执行结束，请核对持久化结果与凭证。";
                        }
                        finally{if(operation=="logic-apply")lock(ioGate)foreach(var job in ioJobs.Values.Where(j=>PathsEqual(j.project,project)&&j.plc==plc))consumedCylinderSnapshots.Add(job.id);}
                    },id);
                    if(!started){record["state"]="not_started";record["error"]="执行通道被占用，本次未执行。请重新预览。";SaveWorkbench(record);}
                    SendJson(response,202,record);return true;
                }
            }catch(Exception ex){SendJson(response,409,new{error=ex.Message});return true;}
        }
    }
}
