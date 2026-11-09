using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using TiaPortalSourceBridge.Openness;

namespace TiaPortalSourceBridge.Mcp
{
    internal sealed class TagTableDependency
    {
        public string table_name {get;set;}
        public string group_path {get;set;}
        public string signature {get;set;}
    }
    // Runs inside one project-worker call and its ProjectOperationGuard lease.
    // There is no client-side read/write gap between dependency validation and import.
    internal static class TagTableTransaction
    {
        private static JavaScriptSerializer Json(){return new JavaScriptSerializer{MaxJsonLength=32*1024*1024};}
        internal static string Key(string group,string name){return (group??"").Trim('/')+"\n"+name;}
        internal static Dictionary<string,object> Run(string root,string operationId,string project,string plc,string table,string group,string xml,string dependencies,
            Func<TagTableDependency[]> list,Func<string,string,string> read,Func<string,string,string> import,Func<CompileSummary> compile,Action save)
        {
            Guid id;if(!Guid.TryParseExact(operationId,"N",out id))throw new ArgumentException("点表写回需要有效操作编号。");
            if(xml==null||Encoding.UTF8.GetByteCount(xml)>8*1024*1024||dependencies==null||dependencies.Length>1024*1024)throw new ArgumentException("写回方案过大。");
            var doc=TagTableXml.Parse(xml);var objectNode=doc.SelectSingleNode("/Document/SW.Tags.PlcTagTable/AttributeList/Name");
            if(objectNode==null||objectNode.InnerText!=table)throw new ArgumentException("变量表身份与写回方案不一致。");
            var expected=Json().Deserialize<TagTableDependency[]>(dependencies);
            if(expected==null||expected.Length==0||expected.Length>1000||expected.Any(e=>e==null||string.IsNullOrEmpty(e.table_name)||e.signature==null||!System.Text.RegularExpressions.Regex.IsMatch(e.signature,"^[a-fA-F0-9]{64}$"))||expected.Select(e=>Key(e.group_path,e.table_name)).Distinct().Count()!=expected.Length)throw new ArgumentException("变量表依赖清单无效。");
            if(!expected.Any(e=>Key(e.group_path,e.table_name)==Key(group,table)))throw new ArgumentException("依赖清单没有目标变量表。");
            string fingerprint=XmlBatch.Hash(string.Join("\n",new[]{Path.GetFullPath(project),plc,table,group,xml,dependencies}));
            string dir=Path.Combine(root,"logs","tag-table-jobs"),path=Path.Combine(dir,operationId+".json");Directory.CreateDirectory(dir);
            if(File.Exists(path)){
                var prior=Json().Deserialize<Dictionary<string,object>>(File.ReadAllText(path));
                if(Convert.ToString(prior["request_hash"])!=fingerprint)throw new InvalidOperationException("操作编号已用于另一份方案。");
                return prior; // Includes unresolved and failed jobs: never replay imports.
            }
            var result=new Dictionary<string,object>{{"id",operationId},{"request_hash",fingerprint},{"project_path",project},{"plc_name",plc},{"table_name",table},{"group_path",group},{"state","checking"},{"imported",false},{"compiled",false},{"verified",false},{"saved",false},{"write_started",false},{"backup",""},{"error",""}};
            Action<string> persist=state=>{result["state"]=state;result["updated_utc"]=DateTime.UtcNow.ToString("o");File.WriteAllText(path+".tmp",Json().Serialize(result),new UTF8Encoding(false));if(File.Exists(path))File.Replace(path+".tmp",path,null);else File.Move(path+".tmp",path);};
            persist("checking");
            try {
                var actual=list();
                if(actual==null||!actual.Select(e=>Key(e.group_path,e.table_name)).OrderBy(s=>s,StringComparer.Ordinal).SequenceEqual(expected.Select(e=>Key(e.group_path,e.table_name)).OrderBy(s=>s,StringComparer.Ordinal)))throw new InvalidOperationException("变量表目录已变化，请重新对账。");
                string before=null;
                foreach(var dependency in expected){string current=read(dependency.table_name,dependency.group_path);
                    if(TagTableXml.Signature(current)!=dependency.signature)throw new InvalidOperationException("变量表已变化："+dependency.table_name+"。本次尚未写入。");
                    if(Key(dependency.group_path,dependency.table_name)==Key(group,table))before=current;
                }
                result["backup"]=BackupRecord.Write(Path.Combine(root,"block-backups","tag-"+operationId),table,before,project,plc,group,"TagTable",table);
                persist("backed_up");result["write_started"]=true;persist("importing");
                result["import_backup"]=import(xml,group);result["imported"]=true;persist("compiling");
                var compiled=compile();if(compiled==null)throw new InvalidOperationException("未收到编译结果。");
                result["compiled"]=true;result["error_count"]=compiled.ErrorCount;result["warning_count"]=compiled.WarningCount;result["compile_report"]=compiled.ToReport();
                if(compiled.ErrorCount!=0||!new[]{"Success","Warning"}.Contains(compiled.State))throw new InvalidOperationException("点表已写入，但编译存在错误或状态未确认；未自动保存，请核对执行记录。");
                persist("verifying");string after=read(table,group);result["after_signature"]=TagTableXml.Signature(after);
                if(TagTableXml.Signature(xml)!=Convert.ToString(result["after_signature"]))throw new InvalidOperationException("写回后的变量表与预览不一致，未自动保存。");
                result["verified"]=true;persist("saving");save();result["saved"]=true;persist("completed");
            }catch(Exception ex){
                result["error"]=ex.Message;
                if((bool)result["write_started"])try{result["after_signature"]=TagTableXml.Signature(read(table,group));}catch(Exception readError){result["readback_error"]=readError.Message;}
                persist((bool)result["write_started"]?"needs_review":"failed");
            }
            return result;
        }
    }
}
