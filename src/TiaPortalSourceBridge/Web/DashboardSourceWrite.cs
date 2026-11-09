using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using TiaPortalSourceBridge.Configuration;
using TiaPortalSourceBridge.Indexing;
using TiaPortalSourceBridge.Mcp;

namespace TiaPortalSourceBridge.Web
{
    internal sealed class ReviewedSourcePlan
    {
        public string id {get;set;} public string project_path {get;set;} public string plc_name {get;set;} public string source_snapshot {get;set;}
        public string purpose {get;set;} public string note {get;set;} public List<TextPatch> patches {get;set;} public List<string> unmatched {get;set;}
        public string recovery_of {get;set;}
    }
    internal sealed partial class WebServer
    {
        private readonly object sourceWriteGate=new object();
        private string SourceWriteFile(string id,string suffix)
        {Guid parsed;if(!Guid.TryParseExact(id,"N",out parsed))throw new ArgumentException("源码写回编号无效。");return SnapshotDiff.Confined(Path.Combine(Path.GetDirectoryName(_configPath),"logs","source-write-jobs"),id+suffix);}
        private void SaveSourceWritePlan(ReviewedSourcePlan plan)
        {string path=SourceWriteFile(plan.id,".plan.json");Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllText(path,CylinderWritePipeline.Json().Serialize(plan),new UTF8Encoding(false));}
        private ReviewedSourcePlan LoadSourceWritePlan(string id,string project,string plc)
        {string file=SourceWriteFile(id,".plan.json");if(!File.Exists(file)||new FileInfo(file).Length>32*1024*1024)throw new InvalidOperationException("源码方案不存在或过大。");var plan=CylinderWritePipeline.Json().Deserialize<ReviewedSourcePlan>(File.ReadAllText(file));if(plan.id!=id||!PathsEqual(plan.project_path,project)||plan.plc_name!=plc)throw new InvalidOperationException("源码方案目标不一致。");return plan;}
        private object SourceWritePreview(ReviewedSourcePlan plan)
        {
            bool supported=plan.patches.Count>0&&plan.patches.Count<=50&&plan.patches.All(p=>p.kind=="plc.block"&&p.diff.status=="structural");
            return new{plan.id,plan.purpose,plan.note,plan.unmatched,fingerprint=XmlBatch.Hash(CylinderWritePipeline.Json().Serialize(plan)),can_apply=supported,
                limitation=supported?"将备份、写入、编译并完整回读所选程序块，全部通过后保存整个离线工程。不会下载到 PLC。":"直接写回仅支持 1–50 个普通 PLC 程序块；变量表、UDT 或未识别对象请单独处理。",
                patches=plan.patches.Select(p=>new{p.object_name,p.group,p.kind,p.diff}).ToArray()};
        }
        private bool HandleSourceWrite(HttpListenerContext context,string path)
        {
            const string prefix="/api/io/source-write/";if(!path.StartsWith(prefix,StringComparison.Ordinal))return false;
            var request=context.Request;var response=context.Response;response.Headers["Cache-Control"]="no-store";
            try{
                if(request.HttpMethod!="POST"){SendJson(response,405,new{error="请使用源码审查按钮"});return true;}
                if(request.ContentLength64<0||request.ContentLength64>12*1024*1024)throw new ArgumentException("源码审查请求过大。");
                var json=CylinderWritePipeline.Json();var map=json.Deserialize<Dictionary<string,object>>(ReadBody(request));if(map==null)throw new ArgumentException("缺少源码审查参数。");
                string project=Text(map,"project_path"),plc=Text(map,"plc_name"),action=path.Substring(prefix.Length),root=Path.GetDirectoryName(_configPath);
                if(!File.Exists(project)||plc.Length==0||!actionTargets.Verify(project,plc,Text(map,"target_token")))throw new InvalidOperationException("工程或 PLC 目标已失效。");
                lock(sourceWriteGate){
                    if(action=="restore-preview"||action=="continue-preview"){
                        if(ActionRunner.AnyBlockingRunning)throw new InvalidOperationException("工程通道仍在执行或结果未决，请先核对原任务。");
                        var old=LoadSourceWritePlan(Text(map,"id"),project,plc);string file=SourceWriteFile(old.id,".json");if(!File.Exists(file)||new FileInfo(file).Length>32*1024*1024)throw new InvalidOperationException("缺少完整执行记录，不能自动恢复或继续。");
                        var state=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(file));if(Text(state,"state")!="needs_review"&&Text(state,"state")!="completed")throw new InvalidOperationException("原任务尚未结束。");
                        object raw;if(!state.TryGetValue("after_signatures",out raw))throw new InvalidOperationException("缺少执行后指纹。");var signatures=CylinderWritePipeline.Map(raw);
                        var next=new ReviewedSourcePlan{id=Guid.NewGuid().ToString("N"),project_path=project,plc_name=plc,source_snapshot=old.source_snapshot,recovery_of=old.id,purpose=action=="restore-preview"?"恢复源码写回前内容":"继续原源码写回目标",note="已重新读取并核对原执行后的对象指纹；仅处理原方案对象，确认后另建任务，原记录保留。",patches=new List<TextPatch>(),unmatched=new List<string>()};
                        foreach(var patch in old.patches){
                            Dictionary<string,object> reply;lock(DashboardGate)reply=Dashboard(_configPath).DashboardCylinderCall("tia_read_block",new Dictionary<string,object>{{"project_path",project},{"plc_name",plc},{"block_name",patch.object_name},{"group_path",string.IsNullOrEmpty(patch.group)?"/":patch.group}});
                            if(CylinderWritePipeline.True(reply,"isError"))throw new InvalidOperationException(CylinderWritePipeline.Error(reply));string current=Text(CylinderWritePipeline.Data(reply),"xml");
                            if(Text(signatures,patch.object_name)!=XmlBatch.RecoverySignature(current))throw new InvalidOperationException("原执行后对象又发生变化或缺少完整回读证据："+patch.object_name+"；请人工核对，不能覆盖。");
                            string desired=action=="restore-preview"?patch.before_xml:patch.after_xml;
                            next.patches.Add(new TextPatch{object_id=patch.object_id,object_name=patch.object_name,group=patch.group,kind=patch.kind,before_hash=XmlBatch.Hash(current),before_xml=current,after_xml=desired,diff=SemanticDiff.Compare(current,desired)});
                        }SaveSourceWritePlan(next);SendJson(response,200,SourceWritePreview(next));return true;
                    }
                    if(action=="status"){
                        var plan=LoadSourceWritePlan(Text(map,"id"),project,plc);string file=SourceWriteFile(plan.id,".json");
                        if(File.Exists(file)){if(new FileInfo(file).Length>32*1024*1024)throw new InvalidOperationException("执行记录过大。");SendJson(response,200,json.DeserializeObject(File.ReadAllText(file)));return true;}
                        var task=ActionRunner.FindTask(plan.id);SendJson(response,200,new{id=plan.id,state=task==null?"unknown":task.status,error=task==null?"未取得最终结果，请人工核对工程和批次备份，禁止重复提交。":task.message});return true;
                    }
                    if(action=="text-export"||action=="text-preview"||action=="parameter-preview"){
                        var archive=SourceReview.Load(root,Text(map,"snapshot_id"),project,plc);
                        if(action=="text-export"){
                            byte[] bytes=ProjectTexts.Workbook(archive);response.ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";response.Headers["Content-Disposition"]="attachment; filename=\"TIA-Translations.xlsx\"";response.ContentLength64=bytes.Length;response.OutputStream.Write(bytes,0,bytes.Length);response.Close();return true;
                        }
                        var plan=new ReviewedSourcePlan{id=Guid.NewGuid().ToString("N"),project_path=project,plc_name=plc,source_snapshot=archive.id,unmatched=new List<string>()};
                        if(action=="text-preview"){
                            string encoded=Text(map,"content_base64");if(encoded.Length>12*1024*1024)throw new InvalidOperationException("翻译表过大。");plan.purpose="多语言文本回填";plan.note="仅更新明确标为 update 的已有语言文本；未读取的 HMI 资源不在本次范围内。";plan.patches=ProjectTexts.Preview(archive,Convert.FromBase64String(encoded),Text(map,"file_name"));
                        }else{
                            Guid id;if(!Guid.TryParseExact(Text(map,"parameter_id"),"N",out id))throw new ArgumentException("请选择本工具归档的参数快照编号。");
                            string file=SnapshotDiff.Confined(Path.Combine(root,"parameter-snapshots"),id.ToString("N")+".json");if(new FileInfo(file).Length>1024*1024)throw new InvalidOperationException("参数快照过大。");
                            var result=ParameterMigration.Preview(archive,json.Deserialize<Dictionary<string,object>>(File.ReadAllText(file)));plan.purpose="参数初始值回填";plan.note=result.note;plan.patches=result.patches;plan.unmatched=result.unmatched;
                        }
                        SaveSourceWritePlan(plan);SendJson(response,200,SourceWritePreview(plan));return true;
                    }
                    if(action!="apply"){SendJson(response,404,new{error="源码写回接口不存在"});return true;}
                    if(shutdownRequested)throw new InvalidOperationException("看板正在退出。");if(AppConfig.DetectProjectVersion(project)!="V17")throw new InvalidOperationException("新增源码写回流程仅支持 V17。");
                    object confirm;if(!map.TryGetValue("confirm",out confirm)||!Equals(confirm,true))throw new InvalidOperationException("请先核对全部对象变化并确认保存整个离线工程。");
                    var selected=LoadSourceWritePlan(Text(map,"id"),project,plc);if(Text(map,"fingerprint")!=XmlBatch.Hash(json.Serialize(selected)))throw new InvalidOperationException("方案与已查看的预览不一致。");
                    if(selected.patches.Count==0||selected.patches.Count>50||selected.patches.Any(p=>p.kind!="plc.block"||p.diff.status!="structural"))throw new InvalidOperationException("当前方案不支持直接写入，请查看预览中的范围说明。");
                    string submitted=SourceWriteFile(selected.id,".submitted");if(File.Exists(submitted)||File.Exists(SourceWriteFile(selected.id,".json")))throw new InvalidOperationException("方案已经提交，请查看执行结果，不能重放。");
                    if(ActionRunner.AnyBlockingRunning)throw new InvalidOperationException("工程通道正在执行其他任务。");
                    string items=json.Serialize(selected.patches.Select(p=>new{block_name=p.object_name,group_path=p.group,xml=p.after_xml,expected_recovery_signature=XmlBatch.RecoverySignature(p.before_xml)}).ToArray());
                    File.WriteAllText(submitted,DateTime.UtcNow.ToString("o"));
                    bool started=ActionRunner.StartEngineeringTask(_configPath,project,plc,selected.purpose,"正在核对源码指纹并准备备份",progress=>{
                        try{
                            Dictionary<string,object> reply;
                            try{lock(DashboardGate)reply=Dashboard(_configPath).DashboardReviewedBlocksCall(new Dictionary<string,object>{{"project_path",project},{"plc_name",plc},{"bridge_operation_id",selected.id},{"items_json",items},{"confirm",true}});}
                            catch(Exception ex){throw new EngineeringOutcomeUnknownException("未收到源码写入最终结果；请查看执行记录和批次备份，不要重复提交。",ex);}
                            var data=CylinderWritePipeline.Data(reply);if(Text(data,"state")!="completed")throw new InvalidOperationException(Text(data,"error").Length>0?Text(data,"error"):CylinderWritePipeline.Error(reply));return "源码写回、完整回读和保存完成。";
                        }finally{lock(ioGate)foreach(var job in ioJobs.Values.Where(j=>PathsEqual(j.project,project)&&j.plc==plc))consumedCylinderSnapshots.Add(job.id);}
                    },selected.id);
                    if(!started)throw new InvalidOperationException("执行通道被占用，此方案未启动，请重新预览。");SendJson(response,202,new{id=selected.id,state="accepted"});return true;
                }
            }catch(Exception ex){SendJson(response,409,new{error=ex.Message});return true;}
        }
    }
}
