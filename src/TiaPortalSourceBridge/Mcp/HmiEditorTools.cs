using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using TiaPortalSourceBridge.Configuration;
using TiaPortalSourceBridge.Openness;
using TiaPortalSourceBridge.Web;

namespace TiaPortalSourceBridge.Mcp
{
    internal sealed class HmiEditorRecord
    {
        public string id,project_path,plc_name,hmi_name,kind,name,group_path,before,after,changes_json,tags_hash,created_utc;
        public Dictionary<string,string> tags;
        public string[] screens,text_lists;
        public object[] differences;
        public object preflight;
        public HmiEditorRecord[] pages;
    }
    internal sealed partial class McpServer
    {
        internal Dictionary<string,object> DashboardHmiEditorCall(bool apply,Dictionary<string,object> arguments)
        {return CallTool(apply?"tia_write_hmi_editor":"tia_read_hmi_editor",arguments);}
        private Dictionary<string,object> HmiEditorTool(bool apply,Dictionary<string,object> args,OpennessSession session,string project)
        {
            if(AppConfig.DetectProjectVersion(project)!="V17")throw new InvalidOperationException("页面编辑器目前支持 V17 经典 WinCC。");
            string plc=StringArg(args,"plc_name"),hmiName=StringArg(args,"hmi_name"),action=StringArg(args,"action");
            session.SelectPlc(plc);
            if(!apply&&action=="catalog"&&hmiName.Length==0)return ToolResult("HMI 设备列表。",false,Dict("hmis",session.GetHmis().Select(h=>h.DisplayName).ToArray()));
            var hmi=session.SelectHmi(hmiName);if(hmi.DisplayName!=hmiName)throw new ArgumentException("请选择完整的 HMI 名称。");var ops=new HmiOperations(session,hmi);
            using(ops.BeginReadScope()) {
            string folder=Path.Combine(Path.GetDirectoryName(_configPath),"logs","hmi-editor");Directory.CreateDirectory(folder);var json=CylinderWritePipeline.Json();
            Func<string,string,string> file=(recordId,suffix)=>{Guid guid;if(!Guid.TryParseExact(recordId,"N",out guid))throw new ArgumentException("页面记录编号无效。");return SnapshotDiff.Confined(folder,recordId+suffix);};
            Func<string,HmiEditorRecord> read=path=>{if(!File.Exists(path)||new FileInfo(path).Length>32*1024*1024)throw new ArgumentException("页面记录不存在或过大。");var r=json.Deserialize<HmiEditorRecord>(File.ReadAllText(path));if(!string.Equals(r.project_path,project,StringComparison.OrdinalIgnoreCase)||r.plc_name!=plc||r.hmi_name!=hmiName)throw new InvalidOperationException("记录的工程、PLC 或 HMI 与当前选择不一致。");return r;};
            var nameCache=new Dictionary<string,string[]>();
            Func<string,string[]> names=kind=>{string[] cached;if(nameCache.TryGetValue(kind,out cached))return cached;var data=ops.Read(kind,"","",false,false);if(Convert.ToInt32(data["failure_count"])>0)throw new InvalidOperationException("页面依赖目录读取失败。");return nameCache[kind]=CylinderWritePipeline.Rows(json.Deserialize<Dictionary<string,object>>(json.Serialize(data)),"objects").Select(o=>StringArg(o,"name")).ToArray();};
            Dictionary<string,string> liveTags=null;string liveTagsHash=null;
            Func<HmiEditorRecord,string> tags=record=>{
                if(liveTags!=null){record.tags=new Dictionary<string,string>(liveTags,StringComparer.OrdinalIgnoreCase);return liveTagsHash;}
                var data=ops.Read("tag_tables","","",true,false);if(Convert.ToInt32(data["failure_count"])>0)throw new InvalidOperationException("变量表读取不完整。");
                var xml=CylinderWritePipeline.Rows(json.Deserialize<Dictionary<string,object>>(json.Serialize(data)),"objects").Select(o=>StringArg(o,"xml")).ToArray();
                record.tags=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
                foreach(var entry in xml.SelectMany(HmiValidation.TagPaths))record.tags.Add(entry.Key,HmiValidation.Value(entry.Value,"LinkList/DataType/Name").Length==0?"":HmiValidation.TypePair(entry.Value));
                liveTags=record.tags;return liveTagsHash=XmlBatch.Hash(string.Join("\n",xml.Select(HmiXmlIdentity.Canonical).OrderBy(x=>x,StringComparer.Ordinal)));
            };
            if(!apply&&action=="catalog")
            {
                var objects=new List<object>();foreach(string kind in new[]{"screens","screen_templates"}){
                    var data=ops.Read(kind,"","",false,false);if(Convert.ToInt32(data["failure_count"])>0)throw new InvalidOperationException("页面目录读取失败。");
                    objects.AddRange(CylinderWritePipeline.Rows(json.Deserialize<Dictionary<string,object>>(json.Serialize(data)),"objects").Select(o=>(object)new{kind,name=StringArg(o,"name"),group_path=StringArg(o,"group_path"),protected_page=HmiPageEditor.ProtectedName(StringArg(o,"name"))}));}
                return ToolResult("页面目录已读取。",false,Dict("objects",objects));
            }
            if(!apply&&action=="read")
            {
                string kind=StringArg(args,"kind");if(kind!="screens"&&kind!="screen_templates")throw new ArgumentException("不支持此页面类别。");
                var record=new HmiEditorRecord{id=Guid.NewGuid().ToString("N"),project_path=project,plc_name=plc,hmi_name=hmiName,kind=kind,name=StringArg(args,"name"),group_path=StringArg(args,"group_path"),created_utc=DateTime.UtcNow.ToString("o")};
                record.before=ops.ReadOneXml(kind,record.name,record.group_path);record.tags_hash=tags(record);record.screens=names("screens");record.text_lists=names("text_lists");
                var model=new HmiPageEditor(record.before).Model();File.WriteAllText(file(record.id,".snapshot.json"),json.Serialize(record),new UTF8Encoding(false));
                return ToolResult("页面已读取，尚未修改。",false,Dict("snapshot_id",record.id,"model",model,"tags",record.tags.Select(t=>new{name=t.Key,type=t.Value}).ToArray(),"screens",record.screens,"text_lists",record.text_lists));
            }
            if(!apply&&(action=="preview"||action=="preview_batch"))
            {
                var inputs=action=="preview_batch" ? json.Deserialize<Dictionary<string,string>[]>(StringArg(args,"pages_json")) : new[]{new Dictionary<string,string>{{"snapshot_id",StringArg(args,"snapshot_id")},{"changes_json",StringArg(args,"changes_json")}}};
                if(inputs==null||inputs.Length<1||inputs.Length>20)throw new ArgumentException("每批需要 1–20 个页面。");
                var pages=new List<HmiEditorRecord>();
                foreach(var input in inputs) {
                    if(input==null||!input.ContainsKey("snapshot_id")||!input.ContainsKey("changes_json"))throw new ArgumentException("缺少页面快照或字段修改。");
                    var record=read(file(input["snapshot_id"],".snapshot.json"));
                    if(record.pages!=null)throw new ArgumentException("请使用单页读取快照。");
                    var editor=new HmiPageEditor(record.before);record.changes_json=input["changes_json"];
                    record.differences=editor.Patch(record.changes_json,record.tags,new HashSet<string>(record.screens,StringComparer.Ordinal),new HashSet<string>(record.text_lists,StringComparer.Ordinal));record.after=editor.Document.OuterXml;
                    record.preflight=new{passed=true,scope="snapshot_only",live_validation_on_apply=true,warnings=new[]{"预览使用读取快照；写入前将重新核对页面及依赖。"}};
                    pages.Add(record);
                }
                HmiEditorBatch.Validate(pages.ToArray());
                var planned=pages.Count==1 ? pages[0] : new HmiEditorRecord{project_path=project,plc_name=plc,hmi_name=hmiName,name=pages.Count+" 个页面",pages=pages.ToArray(),preflight=pages[0].preflight};
                planned.id=Guid.NewGuid().ToString("N");string stored=json.Serialize(planned);File.WriteAllText(file(planned.id,".plan.json"),stored,new UTF8Encoding(false));
                return ToolResult("快照预览完成；写入前仍会核对当前工程。",false,Dict("id",planned.id,"state","preview","fingerprint",XmlBatch.Hash(stored),"differences",pages.SelectMany(page=>page.differences.Select(d=>new{page=page.name,change=d})).ToArray(),"preflight",planned.preflight,"page_count",pages.Count,"saved",false));
            }

            if(!apply)throw new ArgumentException("不支持的页面读取操作。");
            RequireConfirmation(args,"写入已核对的 HMI 普通页面，编译回读成功后保存整个离线工程");
            if(!BoolArg(args,"complete_screen_confirm",false))throw new InvalidOperationException("需确认此普通页面不含报警视图、诊断视图等不能完整导出的控件。");
            string id=StringArg(args,"id"),planFile=file(id,".plan.json"),resultFile=file(id,".result.json");var plan=read(planFile);
            if(XmlBatch.Hash(File.ReadAllText(planFile))!=StringArg(args,"fingerprint"))throw new InvalidOperationException("页面方案已变化，请重新预览。");
            // Durable single-use claim, before any native write. A dropped connection is never replayed.
            using(var claim=new FileStream(file(id,".claimed"),FileMode.CreateNew,FileAccess.Write,FileShare.None)){}
            var result=Dict("id",id,"state","checking","saved",false,"verified",false,"compiled",false,"project_path",project,"plc_name",plc,"hmi_name",hmiName,"error","");
            var clock=new HmiStageTimer();
            Action<string> persist=state=>{clock.Move(state);result["state"]=state;result["stage_ms"]=clock.Snapshot();result["phase_started_utc"]=DateTime.UtcNow.ToString("o");File.WriteAllText(resultFile+".tmp",json.Serialize(result),new UTF8Encoding(false));if(File.Exists(resultFile))File.Replace(resultFile+".tmp",resultFile,null);else File.Move(resultFile+".tmp",resultFile);};
            persist("checking");
            try{
                var pages=plan.pages??new[]{plan};HmiEditorBatch.Validate(pages);
                foreach(var page in pages)if(page.project_path!=project||page.plc_name!=plc||page.hmi_name!=hmiName)throw new InvalidOperationException("页面不属于当前目标。");
                var batch=json.Serialize(pages.Select(page=>new{kind=page.kind,group_path=page.group_path,xml=page.after}).ToArray());
                HmiEditorBatch.Execute(pages,result,page=>{
                    using(OperationMetrics.Measure("hmi_page_read"))if(!HmiOperations.SameObjectXml(page.before,ops.ReadOneXml(page.kind,page.name,page.group_path)))throw new InvalidOperationException(page.name+" 页面已变化，未执行覆盖，请重新读取。");
                    string expected=page.tags_hash;
                    using(OperationMetrics.Measure("hmi_dependencies"))if(tags(page)!=expected)throw new InvalidOperationException("HMI 变量表已变化，未执行覆盖。");
                    var editor=new HmiPageEditor(page.before);editor.Patch(page.changes_json,page.tags,new HashSet<string>(names("screens"),StringComparer.Ordinal),new HashSet<string>(names("text_lists"),StringComparer.Ordinal));
                    if(!HmiOperations.SameObjectXml(editor.Document.OuterXml,page.after))throw new InvalidOperationException("存储方案与字段修改不一致。");
                },()=>ops.Write(batch,Path.Combine(folder,"backups",id),true,false),()=>{
                    var compilation=session.CompileHmi(hmi);return Dict("compile_report",compilation.ToReport(),"error_count",compilation.ErrorCount,"warning_count",compilation.WarningCount,"compile_state",compilation.State);
                },page=>{
                    ops.InvalidateReadCache(); // Post-compile readback must never use the pre-compile export.
                    var changes=json.Deserialize<Dictionary<string,string>[]>(page.changes_json);
                    using(OperationMetrics.Measure("hmi_final_readback"))return HmiPageEditor.Verify(page.after,ops.ReadOneXml(page.kind,page.name,page.group_path),changes.Select(c=>c["key"]));
                },()=>session.SaveProject(),persist);
                persist("completed");
            }catch(Exception ex){result["error"]=ex.ToString();object mutation;persist(result.TryGetValue("mutation_started",out mutation)&&Convert.ToBoolean(mutation)?"needs_review":"failed");}
            return ToolResult("页面写入结果："+result["state"],Convert.ToString(result["state"])!="completed",result);
            }
        }

        private static object[] HmiEditorDefinitions()
        {
            var common=new[]{Property("project_path",StringProperty("V17 工程完整路径")),Property("plc_name",StringProperty("PLC 精确名称")),Property("hmi_name",StringProperty("HMI 完整名称"))};
            return new[]{
                Tool("tia_read_hmi_editor","V17 页面编辑器：读取目录、页面和可编辑字段，或对字段修改生成校验后的预览。不写工程。",ObjectSchema(common.Concat(new[]{Property("action",StringProperty("catalog/read/preview/preview_batch")),Property("kind",StringProperty("screens/screen_templates")),Property("name",StringProperty("页面名")),Property("group_path",StringProperty("精确组路径")),Property("snapshot_id",StringProperty("页面读取快照编号")),Property("changes_json",StringProperty("字段修改数组，每项 key/value；同页全部修改一次提交")),Property("pages_json",StringProperty("preview_batch: 1–20 项 {snapshot_id,changes_json}；同一 HMI，每页一次"))}).ToArray()),true,false,true),
                Tool("tia_write_hmi_editor","V17 页面编辑器：核对方案与最新页面，备份、导入、编译、回读修改字段并保存整个离线工程。不下载。",ObjectSchema(common.Concat(new[]{Property("id",StringProperty("已校验方案编号")),Property("fingerprint",StringProperty("方案指纹")),Property("confirm",BooleanProperty("确认页面修改及整个工程保存")),Property("complete_screen_confirm",BooleanProperty("已核对此普通页面不含不能导出的复合控件"))}).ToArray()),false,true,false)
            };
        }
    }
}
