using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using TiaPortalSourceBridge.Configuration;
using TiaPortalSourceBridge.Openness;
using TiaPortalSourceBridge.Web;

namespace TiaPortalSourceBridge.Mcp
{
    internal sealed class HmiCompanionRecord
    {
        public string id,project_path,plc_name,hmi_name,before_hash,before_text,after_text,changes_json,created_utc,alarm_backup;
        public HmiCompanionConfiguration configuration;public object alarm_preview;
    }
    internal sealed partial class McpServer
    {
        private Dictionary<string,object> HmiCompanion(string name,Dictionary<string,object> args,OpennessSession session,string project)
        {
            if(AppConfig.DetectProjectVersion(project)!="V17")throw new InvalidOperationException("新增 HMI 联动仅支持 V17 经典 WinCC。");
            string id=StringArg(args,"bridge_operation_id");Guid guid;if(!Guid.TryParseExact(id,"N",out guid))throw new ArgumentException("缺少 HMI 操作编号。");
            string root=Path.GetDirectoryName(_configPath),folder=Path.Combine(root,"logs","hmi-companion"),file=Path.Combine(folder,id+".plan.json"),resultFile=Path.Combine(folder,id+".json");Directory.CreateDirectory(folder);
            var json=CylinderWritePipeline.Json();string hmiName=StringArg(args,"hmi_name");var hmi=session.SelectHmi(hmiName);if(hmi.DisplayName!=hmiName)throw new InvalidOperationException("请使用完整且唯一的 HMI 名称。");var ops=new HmiOperations(session,hmi);
            Func<HmiCompanionConfiguration,string> tags=config=>{
                var result=ops.Read("tag_tables","","",true,false);if(Convert.ToInt32(result["failure_count"])!=0)throw new InvalidOperationException("HMI 变量读取不完整。");
                var map=json.Deserialize<Dictionary<string,object>>(json.Serialize(result));var definitions=new List<XmlElement>();var tableXml=new List<string>();
                foreach(var item in CylinderWritePipeline.Rows(map,"objects")) {string xml=StringArg(item,"xml");var doc=ProjectTexts.Parse(xml);tableXml.Add(doc.SelectSingleNode("/Document/Hmi.Tag.TagTable").OuterXml);definitions.AddRange(doc.SelectNodes("//Hmi.Tag.Tag").Cast<XmlElement>());}
                foreach(string tag in config.devices.SelectMany(d=>d.alarms).Select(a=>a.trigger_tag).Distinct()){
                    var found=definitions.Where(t=>t.SelectSingleNode("AttributeList/Name")!=null&&t.SelectSingleNode("AttributeList/Name").InnerText==tag).ToArray();
                    if(found.Length!=1)throw new InvalidOperationException("HMI 触发变量不存在或重名："+tag);
                    var type=found[0].SelectSingleNode("LinkList/DataType/Name");if(type==null||!new[]{"Word","UInt","Int"}.Contains(type.InnerText))throw new InvalidOperationException("报警触发变量需为已声明的 16 位整型："+tag);
                }return XmlBatch.Hash(string.Join("\n",tableXml.OrderBy(s=>s,StringComparer.Ordinal)));
            };
            Func<Dictionary<string,object>,Dictionary<string,object>> alarm=parameters=>{
                parameters["project_path"]=project;parameters["hmi_name"]=hmiName;parameters["refresh"]=true;var reply=CallAlarmTool("tia_write_hmi_alarms",parameters);var data=CylinderWritePipeline.Data(reply);if(CylinderWritePipeline.True(reply,"isError"))throw new InvalidOperationException(CylinderWritePipeline.Error(reply));return data;
            };
            if(name=="tia_read_hmi_companion_plan"){
                if(File.Exists(file)||File.Exists(resultFile))throw new InvalidOperationException("编号已使用。");
                var config=json.Deserialize<HmiCompanionConfiguration>(StringArg(args,"configuration_json"));HmiCompanionPlan.Validate(config);string tagHash=tags(config);
                string before=string.IsNullOrWhiteSpace(config.text_list)?"":ops.ReadOneXml("text_lists",config.text_list,"");string after=before.Length==0?"":HmiCompanionPlan.TextList(before,config);
                if(after.Length>0){var preflight=ops.Write(json.Serialize(new[]{new{kind="text_lists",name=config.text_list,xml=after}}),Path.Combine(root,"hmi-companion-backups",id),true,true);if(Convert.ToInt32(preflight["failure_count"])>0)throw new InvalidOperationException("文本列表预检未通过："+json.Serialize(preflight));}
                string changes=HmiCompanionPlan.Changes(config);var preview=alarm(Dict("changes_json",changes,"dry_run",true));
                string backupFolder=Path.Combine(root,"hmi-companion-backups",id);Directory.CreateDirectory(backupFolder);string alarmBackup=Path.Combine(backupFolder,"alarms-before.xlsx");File.Copy(StringArg(preview,"snapshot_file"),alarmBackup,false);if(before.Length>0)File.WriteAllText(Path.Combine(backupFolder,"text-list-before.xml"),before,new UTF8Encoding(false));
                var plan=new HmiCompanionRecord{id=id,project_path=project,plc_name=StringArg(args,"plc_name"),hmi_name=hmiName,before_hash=StringArg(preview,"before_hash"),before_text=before,after_text=after,changes_json=changes,configuration=config,alarm_preview=preview["plan"],created_utc=DateTime.UtcNow.ToString("o"),alarm_backup=alarmBackup};
                File.WriteAllText(file,json.Serialize(plan),new UTF8Encoding(false));File.WriteAllText(Path.Combine(folder,id+".tags"),tagHash);return ToolResult("HMI 联动预览完成，尚未写入。",false,Dict("id",id,"state","preview"));
            }
            RequireConfirmation(args,"写入 HMI 报警及文本列表，全部核验和编译通过后保存整个离线工程");
            if(!File.Exists(file)||new FileInfo(file).Length>16*1024*1024)throw new InvalidOperationException("HMI 预览不存在或过大。");string stored=File.ReadAllText(file);var selected=json.Deserialize<HmiCompanionRecord>(stored);
            if(selected.id!=id||!string.Equals(selected.project_path,project,StringComparison.OrdinalIgnoreCase)||selected.plc_name!=StringArg(args,"plc_name")||selected.hmi_name!=hmiName||XmlBatch.Hash(stored)!=StringArg(args,"fingerprint"))throw new InvalidOperationException("HMI 方案或目标已变化。");
            if(File.Exists(resultFile))return ToolResult("HMI 操作已提交，请查看原记录。",true,json.DeserializeObject(File.ReadAllText(resultFile)));
            var state=Dict("id",id,"project_path",project,"plc_name",selected.plc_name,"hmi_name",hmiName,"state","checking","saved",false,"alarm_backup",selected.alarm_backup,"error","");
            Action<string> persist=s=>{state["state"]=s;state["updated_utc"]=DateTime.UtcNow.ToString("o");File.WriteAllText(resultFile+".tmp",json.Serialize(state),new UTF8Encoding(false));if(File.Exists(resultFile))File.Replace(resultFile+".tmp",resultFile,null);else File.Move(resultFile+".tmp",resultFile);};persist("checking");bool mutation=false;
            try{
                if(tags(selected.configuration)!=File.ReadAllText(Path.Combine(folder,id+".tags")))throw new InvalidOperationException("HMI 变量表已变化，请重新预览。");
                if(selected.before_text.Length>0&&!HmiOperations.SameObjectXml(selected.before_text,ops.ReadOneXml("text_lists",selected.configuration.text_list,"")))throw new InvalidOperationException("HMI 文本列表已变化，请重新预览。");
                if(selected.after_text.Length>0){var preflight=ops.Write(json.Serialize(new[]{new{kind="text_lists",name=selected.configuration.text_list,xml=selected.after_text}}),Path.Combine(root,"hmi-companion-backups",id),true,true);if(Convert.ToInt32(preflight["failure_count"])>0)throw new InvalidOperationException("文本列表写前复核未通过。");}
                // The native alarm operation exports and checks the expected hash again
                // immediately before import. Never retry a partial HMI batch.
                persist("alarms");mutation=true;var applied=alarm(Dict("changes_json",selected.changes_json,"expected_hash",selected.before_hash,"dry_run",false,"confirm",true));state["alarm_result"]=applied;persist("texts");
                if(selected.after_text.Length>0){var written=ops.Write(json.Serialize(new[]{new{kind="text_lists",name=selected.configuration.text_list,xml=selected.after_text}}),Path.Combine(root,"hmi-companion-backups",id),true,false);state["text_result"]=written;if(Convert.ToInt32(written["failure_count"])>0)throw new InvalidOperationException("文本列表写入或回读未通过。");}
                persist("compiling");var compilation=session.CompileHmi(hmi);state["compile_report"]=compilation.ToReport();if(compilation.ErrorCount!=0||!new[]{"Success","Warning"}.Contains(compilation.State))throw new InvalidOperationException("HMI 编译未通过。");
                persist("saving");session.SaveProject();state["saved"]=true;persist("completed");
            }catch(Exception ex){state["error"]=RootMessage(ex);persist(mutation?"needs_review":"failed");}
            return ToolResult("HMI 联动结果："+state["state"],Convert.ToString(state["state"])!="completed",state);
        }
    }
}
