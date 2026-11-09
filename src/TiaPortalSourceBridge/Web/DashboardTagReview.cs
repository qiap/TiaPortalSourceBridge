using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using TiaPortalSourceBridge.Configuration;
using TiaPortalSourceBridge.Mcp;
using TiaPortalSourceBridge.Openness;

namespace TiaPortalSourceBridge.Web
{
    internal sealed partial class WebServer
    {
        private readonly object tagReviewGate=new object();
        private string TagReviewFile(string id,string suffix)
        {
            Guid guid;if(!Guid.TryParseExact(id,"N",out guid))throw new ArgumentException("点表操作编号无效。");
            return SnapshotDiff.Confined(Path.Combine(Path.GetDirectoryName(_configPath),"logs","tag-table-jobs"),id+suffix);
        }
        private void WriteTagReview(string id,string suffix,object value)
        {
            string path=TagReviewFile(id,suffix);Directory.CreateDirectory(Path.GetDirectoryName(path));
            string json=CylinderWritePipeline.Json().Serialize(value);if(Encoding.UTF8.GetByteCount(json)>32*1024*1024)throw new InvalidOperationException("点表审查记录超过 32 MiB。");
            File.WriteAllText(path+".tmp",json,new UTF8Encoding(false));if(File.Exists(path))File.Replace(path+".tmp",path,null);else File.Move(path+".tmp",path);
        }
        private T ReadTagReview<T>(string id,string suffix)
        {
            string path=TagReviewFile(id,suffix);if(!File.Exists(path)||new FileInfo(path).Length>32*1024*1024)throw new InvalidOperationException("点表记录不存在或过大。");
            return CylinderWritePipeline.Json().Deserialize<T>(File.ReadAllText(path,Encoding.UTF8));
        }
        private TagReconcilePlan LoadTagPlan(string id,string project,string plc)
        {
            var plan=ReadTagReview<TagReconcilePlan>(id,".plan.json");
            if(plan==null||plan.id!=id||!PathsEqual(plan.project_path,project)||plan.plc_name!=plc)throw new InvalidOperationException("点表方案属于其他工程或 PLC。");return plan;
        }
        private object TagPlanPreview(TagReconcilePlan plan)
        {
            return new{plan.id,plan.table_name,plan.group_path,plan.rows,plan.diff,plan.recovery_of,fingerprint=XmlBatch.Hash(CylinderWritePipeline.Json().Serialize(plan)),
                warning="将更新整张原始变量表并编译、保存整个离线工程。变量重命名不自动重构程序、HMI 或外部引用；请核对预览。不会下载到 PLC。"};
        }
        private Dictionary<string,object> TagJobState(string id,string project,string plc)
        {
            var plan=LoadTagPlan(id,project,plc);Dictionary<string,object> result;
            if(File.Exists(TagReviewFile(id,".json")))result=ReadTagReview<Dictionary<string,object>>(id,".json");
            else if(File.Exists(TagReviewFile(id,".submitted.json")))result=ReadTagReview<Dictionary<string,object>>(id,".submitted.json");
            else result=new Dictionary<string,object>{{"state","preview"}};
            result["id"]=id;result["table_name"]=plan.table_name;result["group_path"]=plan.group_path;result["recovery_of"]=plan.recovery_of;
            var task=ActionRunner.FindTask(id);result["task_status"]=task==null?"":task.status;
            if(Text(result,"state")=="accepted"&&(task==null||!ActionRunner.IsPending(task.status))){result["state"]="needs_review";result["error"]=task==null?"未取得最终执行结果。请核对现场，禁止重复提交。":task.message;}
            return result;
        }
        private Dictionary<string,object> TagReviewCall(TagReconcilePlan plan,Action<string> progress)
        {
            var args=new Dictionary<string,object>{{"project_path",plan.project_path},{"plc_name",plan.plc_name},{"table_name",plan.table_name},{"group_path",plan.group_path},{"xml",plan.after_xml},{"dependencies_json",CylinderWritePipeline.Json().Serialize(plan.dependencies)},{"bridge_operation_id",plan.id},{"confirm",true}};
            try{lock(DashboardGate)return Dashboard(_configPath).DashboardTagReviewCall(args);}
            catch(TimeoutException){
                progress("等待博途最终结果，操作仍可能执行，请勿重复提交。");
                while(true){
                    System.Threading.Thread.Sleep(2000);Dictionary<string,object> reply;
                    try{lock(DashboardGate)reply=Dashboard(_configPath).DashboardCylinderCall("tia_connection_status",new Dictionary<string,object>{{"project_path",plan.project_path}});}
                    catch(Exception ex){throw new EngineeringOutcomeUnknownException("无法核对点表写入结果，请查看执行记录。",ex);}
                    var status=CylinderWritePipeline.Data(reply);if(Text(status,"worker_state")=="busy_outcome_unknown")continue;
                    object late;if(Text(status,"late_tool")=="tia_write_tag_table_review"&&status.TryGetValue("late_result",out late)&&late is Dictionary<string,object>)return(Dictionary<string,object>)late;
                    throw new EngineeringOutcomeUnknownException("未收到点表写入最终结果，请核对工程和备份。",null);
                }
            }
        }
        private bool HandleTagReview(HttpListenerContext context,string path)
        {
            const string prefix="/api/io/tag-review/";if(!path.StartsWith(prefix,StringComparison.Ordinal))return false;
            var request=context.Request;var response=context.Response;response.Headers["Cache-Control"]="no-store";
            try{
                string action=path.Substring(prefix.Length);bool get=action=="history"||action=="status";
                if(!new[]{"preview","apply","history","status","restore-preview"}.Contains(action)){SendJson(response,404,new{error="点表写回接口不存在"});return true;}
                if(request.HttpMethod!=(get?"GET":"POST")){SendJson(response,405,new{error="请求方法无效"});return true;}
                Dictionary<string,object> map;
                if(get)map=new Dictionary<string,object>{{"project_path",GetQueryParam(request,"project_path")},{"plc_name",GetQueryParam(request,"plc_name")},{"target_token",GetQueryParam(request,"target_token")},{"id",GetQueryParam(request,"id")}};
                else{if(request.ContentLength64<0||request.ContentLength64>65536)throw new ArgumentException("写回请求过大。");map=CylinderWritePipeline.Json().Deserialize<Dictionary<string,object>>(ReadBody(request));}
                if(map==null)throw new ArgumentException("缺少点表参数。");string project=Text(map,"project_path"),plc=Text(map,"plc_name"),root=Path.GetDirectoryName(_configPath);
                if(!File.Exists(project)||string.IsNullOrEmpty(plc)||!actionTargets.Verify(project,plc,Text(map,"target_token")))throw new InvalidOperationException("工程或 PLC 已变化，请刷新。");
                if(AppConfig.DetectProjectVersion(project)!="V17")throw new InvalidOperationException("点表写回目前仅支持 V17；其他版本仍可只读对账。");
                lock(tagReviewGate){
                    if(action=="status"){SendJson(response,200,TagJobState(Text(map,"id"),project,plc));return true;}
                    if(action=="history"){
                        string dir=Path.Combine(root,"logs","tag-table-jobs");var records=new List<object>();
                        if(Directory.Exists(dir))foreach(string file in Directory.EnumerateFiles(dir,"*.plan.json").OrderByDescending(File.GetLastWriteTimeUtc).Take(1000)){
                            string id=Path.GetFileName(file).Replace(".plan.json","");TagReconcilePlan plan;
                            try{plan=ReadTagReview<TagReconcilePlan>(id,".plan.json");}catch{continue;}
                            if(!PathsEqual(plan.project_path,project)||plan.plc_name!=plc)continue;
                            if(File.Exists(TagReviewFile(id,".submitted.json")))records.Add(TagJobState(id,project,plc));if(records.Count==50)break;
                        }
                        SendJson(response,200,records);return true;
                    }
                    if(shutdownRequested)throw new InvalidOperationException("看板正在退出。");
                    if(action=="preview"){
                        IoReconcileReport report;lock(reconcileGate)if(!reconcileReports.TryGetValue(Text(map,"report_id"),out report))throw new InvalidOperationException("对账报告已失效。");
                        var snapshot=ReconcileSnapshot(report.snapshot_id);if(!PathsEqual(snapshot.project,project)||snapshot.plc!=plc)throw new InvalidOperationException("对账目标不一致。");
                        object raw;if(!map.TryGetValue("rows",out raw))throw new ArgumentException("请选择差异行。");
                        var rows=CylinderWritePipeline.Json().Deserialize<int[]>(CylinderWritePipeline.Json().Serialize(raw));
                        var archive=SourceReview.Capture(root,project,plc,"V17","点表写回前审查");
                        var plan=TagReconcilePlan.Build(report,archive,rows,Text(map,"culture"));WriteTagReview(plan.id,".plan.json",plan);SendJson(response,200,TagPlanPreview(plan));return true;
                    }
                    if(action=="restore-preview"){
                        var old=LoadTagPlan(Text(map,"id"),project,plc);var result=TagJobState(old.id,project,plc);
                        if(Text(result,"state")!="needs_review"&&Text(result,"state")!="completed")throw new InvalidOperationException("当前记录尚无可恢复的最终结果。");
                        if(ActionRunner.AnyBlockingRunning)throw new InvalidOperationException("工程任务正在执行，请等待完成。");
                        string backup=Text(result,"backup");string safe=SnapshotDiff.Confined(Path.Combine(root,"block-backups"),RelativeBackup(root,backup));
                        string xml=File.ReadAllText(safe,Encoding.UTF8);var metadata=BackupRecord.Read(safe,xml,project,plc);
                        if(metadata==null||metadata.Kind!="TagTable"||metadata.Name!=old.table_name||metadata.GroupPath!=(old.group_path??"").Trim('/'))throw new InvalidOperationException("备份身份不匹配。");
                        var plan=new TagReconcilePlan{id=Guid.NewGuid().ToString("N"),project_path=project,plc_name=plc,table_name=old.table_name,group_path=old.group_path,after_xml=xml,recovery_of=old.id,rows=new int[0],dependencies=new List<Dictionary<string,object>>()};
                        var args=new Dictionary<string,object>{{"project_path",project},{"plc_name",plc}};
                        foreach(var table in IoTable.Maps(ReadDashboard(_configPath,"tia_list_tag_tables",args),"tag_tables")){
                            string name=Text(table,"name"),group=Text(table,"group_path");if(Text(table,"unit_name").Length>0)throw new InvalidOperationException("恢复暂不处理软件单元。");
                            var a=new Dictionary<string,object>(args){{"table_name",name},{"group_path",group.Length==0?"/":group}};string source=Text(ReadDashboard(_configPath,"tia_read_tag_table",a),"xml");
                            plan.dependencies.Add(new Dictionary<string,object>{{"table_name",name},{"group_path",group},{"signature",TagTableXml.Signature(source)}});
                            if(name==plan.table_name&&group==plan.group_path)plan.before_xml=source;
                        }
                        if(plan.before_xml==null||TagTableXml.Signature(plan.before_xml)!=Text(result,"after_signature"))throw new InvalidOperationException("失败后变量表又发生变化或缺少最终回读证据，不能直接覆盖恢复，请先人工核对。");
                        plan.diff=SemanticDiff.Compare(plan.before_xml,plan.after_xml);WriteTagReview(plan.id,".plan.json",plan);SendJson(response,200,TagPlanPreview(plan));return true;
                    }
                    object confirm;if(!map.TryGetValue("confirm",out confirm)||!Equals(confirm,true))throw new InvalidOperationException("请先确认预览以及保存整个离线工程。");
                    var selected=LoadTagPlan(Text(map,"id"),project,plc);
                    if(Text(map,"fingerprint")!=XmlBatch.Hash(CylinderWritePipeline.Json().Serialize(selected)))throw new InvalidOperationException("写回方案与已查看的预览不一致，请重新预览。");
                    if(File.Exists(TagReviewFile(selected.id,".submitted.json"))||File.Exists(TagReviewFile(selected.id,".json")))throw new InvalidOperationException("此方案已经提交，请查看记录，不能重复执行。");
                    if(ActionRunner.AnyBlockingRunning)throw new InvalidOperationException("工程任务正在执行，请稍后重试。");
                    var submitted=new Dictionary<string,object>{{"id",selected.id},{"project_path",project},{"plc_name",plc},{"state","accepted"},{"updated_utc",DateTime.UtcNow.ToString("o")}};WriteTagReview(selected.id,".submitted.json",submitted);
                    bool started=ActionRunner.StartEngineeringTask(_configPath,project,plc,"点表审查写回","正在核对变量表指纹",progress=>{
                        try{
                            var reply=TagReviewCall(selected,progress);var data=CylinderWritePipeline.Data(reply);
                            if(Text(data,"state")!="completed")throw new InvalidOperationException(Text(data,"error").Length>0?Text(data,"error"):CylinderWritePipeline.Error(reply));
                            return "变量表写回、编译、回读和保存完成。";
                        }finally{lock(ioGate)foreach(var job in ioJobs.Values.Where(j=>PathsEqual(j.project,project)&&j.plc==plc))consumedCylinderSnapshots.Add(job.id);}
                    },selected.id);
                    if(!started){submitted["state"]="not_started";submitted["error"]="工程通道被其他任务占用，此方案未执行。";WriteTagReview(selected.id,".submitted.json",submitted);throw new InvalidOperationException("未取得工程执行通道，请重新预览后再提交。");}
                    SendJson(response,202,new{id=selected.id,state="accepted"});return true;
                }
            }catch(Exception ex){SendJson(response,409,new{error=ex.Message});return true;}
        }
        private static string RelativeBackup(string root,string path)
        {
            string folder=Path.GetFullPath(Path.Combine(root,"block-backups"))+Path.DirectorySeparatorChar;
            string full=Path.GetFullPath(path);if(!full.StartsWith(folder,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("备份不在本工具备份目录中。");return full.Substring(folder.Length);
        }
    }
}
