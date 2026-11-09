using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using TiaPortalSourceBridge.Configuration;
using TiaPortalSourceBridge.Indexing;

namespace TiaPortalSourceBridge.Web
{
    internal sealed partial class WebServer
    {
        private readonly object sourceReviewGate = new object();
        private bool sourceReviewBusy;
        private bool HandleSourceReview(HttpListenerContext context,string path)
        {
            const string prefix="/api/io/review/";
            if(!path.StartsWith(prefix,StringComparison.Ordinal))return false;
            var request=context.Request;var response=context.Response;response.Headers["Cache-Control"]="no-store";
            bool acquired=false;
            try {
                string action=path.Substring(prefix.Length);bool read=action=="list"||action=="document";
                if(request.HttpMethod!=(read?"GET":"POST")){SendJson(response,405,new{error="请求方法无效"});return true;}
                if(!new[]{"list","capture","compare","object","document","graph-list","graph"}.Contains(action)){SendJson(response,404,new{error="源码审查接口不存在"});return true;}
                Dictionary<string,object> map;
                if(read)map=new Dictionary<string,object>{{"project_path",GetQueryParam(request,"project_path")},{"plc_name",GetQueryParam(request,"plc_name")},{"target_token",GetQueryParam(request,"target_token")},{"id",GetQueryParam(request,"id")}};
                else {if(request.ContentLength64<0||request.ContentLength64>65536)throw new ArgumentException("审查请求过大。");map=_json.Deserialize<Dictionary<string,object>>(ReadBody(request));}
                if(map==null)throw new ArgumentException("缺少审查参数。");
                string project=Text(map,"project_path"),plc=Text(map,"plc_name"),root=Path.GetDirectoryName(_configPath);
                if(!File.Exists(project)||string.IsNullOrWhiteSpace(plc)||!actionTargets.Verify(project,plc,Text(map,"target_token")))throw new InvalidOperationException("工程或 PLC 目标已失效，请刷新看板。");
                lock(sourceReviewGate){if(sourceReviewBusy)throw new InvalidOperationException("正在处理另一份源码审查，请稍后重试。");sourceReviewBusy=acquired=true;}
                object result;
                if(action=="list")result=SourceReview.List(root,project,plc);
                else if(action=="capture"){
                    var archive=SourceReview.Capture(root,project,plc,AppConfig.DetectProjectVersion(project),Text(map,"label"));
                    result=new{archive.id,archive.label,archive.created_utc,count=archive.entries.Count,archive.warnings};
                } else if(action=="graph-list"||action=="graph"){
                    var archive=SourceReview.Load(root,Text(map,"id"),project,plc);
                    if(action=="graph-list")result=archive.entries.Where(e=>e.kind=="plc.block"&&e.language=="GRAPH").Select(e=>new{e.id,e.name,e.group,e.unit_name}).ToArray();
                    else{var entry=archive.entries.SingleOrDefault(e=>e.id==Text(map,"object_id")&&e.kind=="plc.block"&&e.language=="GRAPH");if(entry==null)throw new ArgumentException("GRAPH 对象不在当前快照中。");result=new{entry.name,entry.content_hash,archive.created_utc,sequences=GraphMonitor.Read(archive.Sources[entry.id])};}
                } else if(action=="document"){
                    var archive=SourceReview.Load(root,Text(map,"id"),project,plc);byte[] bytes=SourceReview.Documentation(archive);
                    response.ContentType="application/zip";response.Headers["Content-Disposition"]="attachment; filename=\"TIA-Documentation-"+archive.id+".zip\"";
                    response.ContentLength64=bytes.Length;response.OutputStream.Write(bytes,0,bytes.Length);response.Close();return true;
                } else {
                    var before=SourceReview.Load(root,Text(map,"before_id"),project,plc);var after=SourceReview.Load(root,Text(map,"after_id"),project,plc);
                    if(action=="compare")result=SourceReview.Compare(before,after);
                    else {
                        string id=Text(map,"object_id");var a=before.entries.SingleOrDefault(e=>e.id==id);var b=after.entries.SingleOrDefault(e=>e.id==id);
                        if(a==null&&b==null)throw new ArgumentException("对象不在所选快照中。");
                        var diff=DiffHelper.CompareText(a==null?"":before.Sources[id],b==null?"":after.Sources[id],(b??a).name);
                        if(diff.error!=null)throw new InvalidOperationException(diff.error);result=diff;
                    }
                }
                if(Encoding.UTF8.GetByteCount(_json.Serialize(result))>32*1024*1024)throw new InvalidOperationException("审查结果超过 32 MiB，请缩小范围。");
                SendJson(response,200,result);
            } catch(Exception ex){SendJson(response,409,new{error=ex.Message});}
            finally{if(acquired)lock(sourceReviewGate)sourceReviewBusy=false;}
            return true;
        }
    }
}
