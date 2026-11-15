using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using TiaPortalSourceBridge.Mcp;

namespace TiaPortalSourceBridge.Web
{
    internal sealed partial class WebServer
    {
        // Read-only catalog. Existing per-operation handlers remain the authority for
        // querying, confirming, continuing and restoring a particular operation.
        private bool HandleTaskCatalog(HttpListenerContext context,string path)
        {
            if(path!="/api/io/tasks")return false;
            try {
                var request=context.Request;var response=context.Response;response.Headers["Cache-Control"]="no-store";
                if(request.HttpMethod!="GET"){SendJson(response,405,new{error="仅支持读取任务列表。"});return true;}
                string project=GetQueryParam(request,"project_path"),plc=GetQueryParam(request,"plc_name");
                if(!File.Exists(project)||plc.Length==0||!actionTargets.Verify(project,plc,GetQueryParam(request,"target_token")))throw new InvalidOperationException("请先选择当前工程与 PLC。");
                string root=Path.GetDirectoryName(_configPath);var rows=new List<Dictionary<string,object>>();var warnings=new List<string>();long budget=64L*1024*1024;
                foreach(var spec in new[]{new[]{"workbench","logs/workbench","*.json",".json"},new[]{"logic_plan","logs/logic-plans","*.json",".json"},new[]{"download","simulation-downloads","*.json",".json"},new[]{"source","logs/source-write-jobs","*.plan.json",".plan.json"},new[]{"hmi","logs/hmi-companion","*.request.json",".request.json"},new[]{"tag","logs/tag-table-jobs","*.plan.json",".plan.json"},new[]{"peripheral","simulation-runs","*.json",".json"},new[]{"parameter","parameter-snapshots","*.json",".json"}}){
                    string directory=Path.Combine(root,spec[1]);if(!Directory.Exists(directory))continue;
                    var files=Directory.EnumerateFiles(directory,spec[2]).Take(2001).ToArray();if(files.Length>2000)warnings.Add("部分历史记录未扫描，请在原功能页核对更早记录。");
                    foreach(string candidate in files.Take(2000).OrderByDescending(File.GetLastWriteTimeUtc).Take(100))try{
                        string name=Path.GetFileName(candidate),id=name.Substring(0,name.Length-spec[3].Length);Guid guid;if(!Guid.TryParseExact(id,"N",out guid))continue;
                        string file=SnapshotDiff.Confined(directory,name);var info=new FileInfo(file);if(info.Length>32*1024*1024||info.Length>budget){warnings.Add("部分记录过大，未列入本次列表。");continue;}budget-=info.Length;
                        var identity=CylinderWritePipeline.Json().Deserialize<Dictionary<string,object>>(File.ReadAllText(file));
                        if(!PathsEqual(Text(identity,"project_path"),project)||(spec[0]=="download"?Text(identity,"plc"):Text(identity,"plc_name"))!=plc){
                            // Older simulation runs have project and instance identity, but no PLC identity.
                            // Do not guess the PLC for these records.
                            continue;
                        }
                        var result=identity;string state="preview",resultFile=SnapshotDiff.Confined(directory,id+(spec[0]=="logic_plan"?".result.json":".json"));
                        bool submitted=File.Exists(SnapshotDiff.Confined(directory,id+".submitted"))||File.Exists(SnapshotDiff.Confined(directory,id+".claim"))||File.Exists(SnapshotDiff.Confined(directory,id+".submitted.json"))||File.Exists(file+".submitted");
                        if(spec[0]=="tag"&&!submitted&&!File.Exists(resultFile))continue;
                        if(File.Exists(resultFile)){
                            long size=new FileInfo(resultFile).Length;if(size>32*1024*1024||size>budget){warnings.Add("执行结果过大，请到原功能页核对。");continue;}budget-=size;
                            result=CylinderWritePipeline.Json().Deserialize<Dictionary<string,object>>(File.ReadAllText(resultFile));state=Text(result,"state");
                        }else if(submitted||spec[0]=="hmi"&&!File.Exists(SnapshotDiff.Confined(directory,id+".plan.json")))state="unknown";
                        if(spec[0]=="parameter")state="archived";
                        if(state.Length==0)state="preview";
                        if(state=="download_intent")state="outcome_unknown";
                        var task=ActionRunner.FindTask(id)??(spec[0]=="hmi"?ActionRunner.FindTask(id+"-preview"):null);
                        if(task!=null&&ActionRunner.IsPending(task.status))state=task.status;
                        else if(state=="unknown"&&task!=null&&task.status=="failed")state=submitted?"unknown":"failed";
                        else if(new[]{"accepted","running","checking","importing","verifying","saving","alarms","texts","compiling"}.Contains(state))state="unknown";
                        string title=spec[0]=="source"?Text(identity,"purpose"):spec[0]=="hmi"?"HMI 联动 · "+Text(identity,"hmi_name"):spec[0]=="tag"?"点表写回 · "+Text(identity,"table_name"):spec[0]=="parameter"?"参数归档":"外设仿真 · "+Text(identity,"instance_name");
                        string kind=spec[0]=="workbench"?Text(identity,"kind"):spec[0];
                        if(kind=="logic_plan")title="受限触点修改 · "+Text(identity,"block_name");if(kind=="download")title="仿真预检 / 装载 · "+Text(identity,"instance_name");
                        object saved;result.TryGetValue("saved",out saved);
                        rows.Add(new Dictionary<string,object>{{"id",id},{"kind",kind},{"record_source",spec[0]=="workbench"?"workbench":"native"},{"title",title},{"state",state},{"saved",saved},{"updated",File.GetLastWriteTimeUtc(File.Exists(resultFile)?resultFile:file).ToString("o")},{"message",task!=null&&ActionRunner.IsPending(task.status)?task.message:Text(result,"error")},{"hmi_name",Text(identity,"hmi_name")},{"instance_name",Text(identity,"instance_name")},{"snapshot_id",Text(identity,"source_snapshot")}});
                    }catch(Exception){warnings.Add("一条历史记录无法读取，已跳过；不会推断其执行成功。");}
                }
                SendJson(context.Response,200,new{jobs=rows.GroupBy(r=>Text(r,"kind")+":"+Text(r,"id")).Select(g=>g.First()).OrderByDescending(r=>Convert.ToString(r["updated"]),StringComparer.Ordinal).Take(300).ToArray(),warnings=warnings.Distinct().ToArray(),note="当前工程与 PLC 的近期记录；未确认状态请打开原任务核对，不自动重放。"});
            }catch(Exception ex){SendJson(context.Response,409,new{error=ex.Message});}return true;
        }
    }
}
