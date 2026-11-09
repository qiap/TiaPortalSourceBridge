using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace TiaPortalSourceBridge.Mcp
{
    internal static class ReviewedBlockWrite
    {
        internal static Dictionary<string,object> Run(string root,string id,string project,string plc,string itemsJson,Func<string,string,XmlBatchResult> import,Func<string,string,string> read,Action save)
        {
            Guid parsed;if(!Guid.TryParseExact(id,"N",out parsed))throw new ArgumentException("源码写回需要有效操作编号。");
            var items=XmlBatch.Parse(itemsJson);if(items.Any(i=>string.IsNullOrEmpty(i.expected_recovery_signature)))throw new ArgumentException("每个对象均须提供完整的修改前指纹。");
            string folder=Path.Combine(root,"logs","source-write-jobs"),path=Path.Combine(folder,id+".json");Directory.CreateDirectory(folder);
            string hash=XmlBatch.Hash(project+"\n"+plc+"\n"+itemsJson);var json=new System.Web.Script.Serialization.JavaScriptSerializer{MaxJsonLength=32*1024*1024};
            if(File.Exists(path)){var prior=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(path));if(Convert.ToString(prior["request_hash"])!=hash)throw new InvalidOperationException("该操作编号已绑定另一份方案。");return prior;}
            var result=new Dictionary<string,object>{{"id",id},{"project_path",project},{"plc_name",plc},{"request_hash",hash},{"state","checking"},{"saved",false},{"verified",false},{"error",""}};
            Action<string> persist=state=>{result["state"]=state;result["updated_utc"]=DateTime.UtcNow.ToString("o");File.WriteAllText(path+".tmp",json.Serialize(result),new UTF8Encoding(false));if(File.Exists(path))File.Replace(path+".tmp",path,null);else File.Move(path+".tmp",path);};persist("checking");
            try{
                // The batch performs all fresh fingerprint checks and all backups
                // before its first import, under the same project worker lease.
                persist("importing");var batch=import(itemsJson,id);result["batch"]=batch;
                if(batch==null||!batch.ok||!batch.compiled||batch.error_count!=0)throw new InvalidOperationException(batch==null?"未取得批量写入结果。":batch.error??"编译或回读未通过。");
                persist("verifying");var signatures=new Dictionary<string,string>();result["after_signatures"]=signatures;
                foreach(var item in items){string actual=read(item.block_name,item.group_path);string signature=XmlBatch.RecoverySignature(actual);signatures[item.block_name]=signature;if(signature!=XmlBatch.RecoverySignature(item.Xml))throw new InvalidOperationException("完整对象回读不一致（包括注释和初始值）："+item.block_name);}
                result["verified"]=true;persist("saving");save();result["saved"]=true;persist("completed");
            }catch(Exception ex){result["error"]=ex.Message;var signatures=new Dictionary<string,string>();foreach(var item in items)try{signatures[item.block_name]=XmlBatch.RecoverySignature(read(item.block_name,item.group_path));}catch{}result["after_signatures"]=signatures;persist("needs_review");}
            return result;
        }
    }
}
