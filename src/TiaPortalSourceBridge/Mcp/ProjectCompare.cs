using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TiaPortalSourceBridge.Configuration;
using TiaPortalSourceBridge.Indexing;
using TiaPortalSourceBridge.Web;

namespace TiaPortalSourceBridge.Mcp
{
    internal sealed class CompareSide
    {
        public string project_path, plc_name, unit_name, group_path, mode, catalog_utc, error, coverage_warning;
        public bool absence_verified;
        public List<Dictionary<string,object>> blocks = new List<Dictionary<string,object>>();
    }
    internal static class ProjectCompare
    {
        internal static string S(Dictionary<string,object> row,string key) { object value; return row.TryGetValue(key,out value)?Convert.ToString(value):""; }
        internal static bool InGroup(string path,string group) { return group.Length==0 || string.Equals(path,group,StringComparison.OrdinalIgnoreCase) || path.StartsWith(group+"/",StringComparison.OrdinalIgnoreCase); }
        static string Key(Dictionary<string,object> b,CompareSide s) { string group=S(b,"group_path");return (s.group_path.Length==0?group:group.Substring(s.group_path.Length).TrimStart('/'))+"\n"+S(b,"name"); }
        // Keep the internal pair boundary unambiguous; display a compact path in text summaries.
        static string DisplayKey(string key) { int separator=key.IndexOf('\n');return separator==0?key.Substring(1):key.Substring(0,separator)+"/"+key.Substring(separator+1); }
        static Dictionary<string,object> D(params object[] v) { var d=new Dictionary<string,object>();for(int i=0;i<v.Length;i+=2)d.Add((string)v[i],v[i+1]);return d; }
        internal static Dictionary<string,object> Run(CompareSide a,CompareSide b,int offset,int limit,string expectedCatalog,
            Func<CompareSide,Dictionary<string,object>,Dictionary<string,object>> read)
        {
            if(offset<0||limit<1||limit>20)throw new ArgumentException("offset >= 0，limit 为 1..20。");
            if(a.blocks.Count>5000||b.blocks.Count>5000)throw new ArgumentException("目录超过 5000 个块，请缩小组范围。");
            var aa=a.blocks.GroupBy(x=>Key(x,a),StringComparer.OrdinalIgnoreCase).ToDictionary(g=>g.Key,g=>g.ToArray(),StringComparer.OrdinalIgnoreCase);
            var bb=b.blocks.GroupBy(x=>Key(x,b),StringComparer.OrdinalIgnoreCase).ToDictionary(g=>g.Key,g=>g.ToArray(),StringComparer.OrdinalIgnoreCase);
            var keys=aa.Keys.Union(bb.Keys,StringComparer.OrdinalIgnoreCase).OrderBy(k=>k,StringComparer.OrdinalIgnoreCase).ToArray();
            string token=IndexJson.Hash(RestrictedLogic.Json().Serialize(new {a,b}));
            if(!string.IsNullOrEmpty(expectedCatalog)&&token!=expectedCatalog)throw new InvalidOperationException("对比目录已变化，请从第一页重新读取。");
            var rows=new List<Dictionary<string,object>>();long totalBytes=0;
            foreach(string key in keys.Skip(offset).Take(limit)) {
                Dictionary<string,object>[] x,y;aa.TryGetValue(key,out x);bb.TryGetValue(key,out y);
                var row=D("relative_identity",DisplayKey(key),"status","unknown","a",x,"b",y);rows.Add(row);
                if(x!=null&&x.Length!=1||y!=null&&y.Length!=1) {row["reason"]="ambiguous_identity";continue;}
                if(x==null||y==null) { row["status"]=x==null?(a.absence_verified?"added_in_b":"unconfirmed_only_b"):(b.absence_verified?"removed_from_b":"unconfirmed_only_a");row["reason"]="Absence requires a successful complete live catalog; cache absence is not deletion.";continue; }
                try {
                    if(totalBytes>=24*1024*1024)throw new IOException("当前页源码预算已满，请缩小 limit。");
                    var ra=read(a,x[0]);var rb=read(b,y[0]);
                    string ax=S(ra,"xml"),bx=S(rb,"xml");
                    totalBytes+=System.Text.Encoding.UTF8.GetByteCount(ax)+System.Text.Encoding.UTF8.GetByteCount(bx);
                    row["evidence_a"]=ra.Where(kv=>kv.Key!="xml").ToDictionary(kv=>kv.Key,kv=>kv.Value);
                    row["evidence_b"]=rb.Where(kv=>kv.Key!="xml").ToDictionary(kv=>kv.Key,kv=>kv.Value);
                    if(!Equals(ra.ContainsKey("ok")?ra["ok"]:null,true)||!Equals(rb.ContainsKey("ok")?rb["ok"]:null,true)||ax.Length==0||bx.Length==0) {row["reason"]="source_unavailable";continue;}
                    if(totalBytes>24*1024*1024)throw new IOException("当前页源码超过 24 MiB，请缩小 limit。");
                    row["hash_a"]=IndexJson.Hash(ax);row["hash_b"]=IndexJson.Hash(bx);
                    var diff=SemanticDiff.Compare(ax,bx);
                    row["comparison"]=diff.status;row["warnings"]=diff.warnings;
                    row["status"]=diff.status=="structural"?(diff.changes.Count==0?"structurally_equal":"changed"):(ax==bx?"identical_raw":"changed_raw");
                    row["change_count"]=diff.changes.Count;row["changes"]=diff.changes.Take(200).ToArray();row["changes_truncated"]=diff.changes.Count>200;
                    row["interface_change_count"]=diff.changes.Count(c=>c.area=="接口");
                }catch(Exception ex){row["status"]="unknown";row["reason"]=ex.Message;}
            }
            var result=D("schema",1,"read_only",true,"a",D("project_path",a.project_path,"plc_name",a.plc_name,"group_path",a.group_path,"unit_name",a.unit_name,"mode",a.mode,"catalog_utc",a.catalog_utc,"absence_verified",a.absence_verified,"error",a.error,"coverage_warning",a.coverage_warning),
                "b",D("project_path",b.project_path,"plc_name",b.plc_name,"group_path",b.group_path,"unit_name",b.unit_name,"mode",b.mode,"catalog_utc",b.catalog_utc,"absence_verified",b.absence_verified,"error",b.error,"coverage_warning",b.coverage_warning),
                "catalog_token",token,"total_pairs",keys.Length,"offset",offset,"next_offset",offset+rows.Count<keys.Length?(object)(offset+rows.Count):null,"items",rows.ToArray(),
                "unknown_count_in_page",rows.Count(r=>S(r,"status")=="unknown"||S(r,"status").StartsWith("unconfirmed")),
                "scope","PLC program blocks only; exact names and relative groups; rename is remove/add; pages are sequential reads, not an atomic two-project snapshot; no HMI, hardware or download impact proof.");
            if(RestrictedLogic.Json().Serialize(result).Length>24*1024*1024)throw new InvalidOperationException("对比结果超限，请缩小 limit；未返回截断报告。");
            return result;
        }
    }
    internal sealed partial class McpServer
    {
        private CompareSide CompareCatalog(Dictionary<string,object> args,string suffix)
        {
            string project=StringArg(args,"project_path_"+suffix),plc=StringArg(args,"plc_name_"+suffix);
            if(string.IsNullOrWhiteSpace(project)||string.IsNullOrWhiteSpace(plc)||!Path.IsPathRooted(project))throw new ArgumentException("两侧必须明确指定完整工程路径和 PLC 路径。");
            string mode=StringArg(args,"read_mode");if(mode.Length==0)mode="live";if(mode!="live"&&mode!="cache")throw new ArgumentException("read_mode 必须为 live/cache。");
            string group=StringArg(args,"group_path_"+suffix).Replace('\\','/').Trim('/');
            var side=new CompareSide { project_path=Path.GetFullPath(project),plc_name=plc,unit_name=StringArg(args,"unit_name_"+suffix),group_path=group,mode=mode };
            if(AppConfig.DetectProjectVersion(side.project_path)!="V21"&&side.unit_name.Length>0)throw new ArgumentException("当前版本的对比接口不支持 Software Unit，不能忽略 unit_name。");
            try {
                if(mode=="cache") {
                    var store=new ProjectIndexStore(side.project_path,AppConfig.DetectProjectVersion(side.project_path));var catalog=store.Load();side.catalog_utc=catalog.built_utc;
                    side.blocks=catalog.objects.Where(e=>e.kind=="plc.block"&&e.owner==plc&&(e.unit_name??"")==side.unit_name&&ProjectCompare.InGroup(e.group??"",group))
                        .Select(e=>Dict("name",e.name,"group_path",e.group??"","language",e.language,"cache_state",e.state,"content_hash",e.content_hash)).ToList();
                }else {
                    var result=CallTool("tia_list_blocks",Dict("project_path",side.project_path,"plc_name",plc,"unit_name",side.unit_name,"scope_path",group.Length==0?"/":group,"refresh",true,"response_mode","compact"));
                    if(Equals(Value(result,"isError"),true))throw new InvalidOperationException(RestrictedLogic.Json().Serialize(Value(result,"content")));
                    var data=(Dictionary<string,object>)Value(result,"structuredContent");
                    var array=(object[])RestrictedLogic.Json().DeserializeObject(RestrictedLogic.Json().Serialize(data["blocks"]));
                    var catalogRows=array.Cast<Dictionary<string,object>>().ToArray();
                    if(catalogRows.Any(e=>string.IsNullOrWhiteSpace(ProjectCompare.S(e,"name"))||!e.ContainsKey("group_path")))throw new InvalidOperationException("实时目录缺少对象名称或组身份，无法确认完整性。");
                    side.blocks=catalogRows.Where(e=>ProjectCompare.InGroup(ProjectCompare.S(e,"group_path"),group)&&ProjectCompare.S(e,"unit_name")==side.unit_name).ToList();
                    // V21's existing scoped enumerator can suppress unavailable containers.
                    // Keep its current read capability, but never promote absence to deletion.
                    side.absence_verified=PortalVersions.IsClassic(PortalVersions.FromProjectPath(side.project_path));
                    if(!side.absence_verified)side.coverage_warning="当前版本的目录接口未提供容器读取完整度证明；仅单侧出现的块保留为待核对，不确认删除。";
                }
            }catch(Exception ex){side.error=ex.Message;side.absence_verified=false;}
            return side;
        }
        private Dictionary<string,object> CompareProjects(Dictionary<string,object> args)
        {
            var a=CompareCatalog(args,"a");var b=CompareCatalog(args,"b");
            var report=ProjectCompare.Run(a,b,IndexInt(args,"offset",0,0,10000),IndexInt(args,"limit",10,1,20),StringArg(args,"catalog_token"),(side,entry)=> {
                string name=ProjectCompare.S(entry,"name"),group=ProjectCompare.S(entry,"group_path");
                if(side.mode=="cache") {
                    var store=new ProjectIndexStore(side.project_path,AppConfig.DetectProjectVersion(side.project_path));
                    var row=store.CachedBlocks(side.plc_name,side.unit_name,group.Length==0?"/":group,new[]{name})[0];
                    if(row.ContainsKey("payload")){row["xml"]=row["payload"];row.Remove("payload");}return row;
                }
                var response=CallTool("tia_read_block",Dict("project_path",side.project_path,"plc_name",side.plc_name,"unit_name",side.unit_name,"group_path",group.Length==0?"/":group,"block_name",name,"format","xml","response_mode","compact"));
                var data=Value(response,"structuredContent") as Dictionary<string,object>;
                return data??Dict("ok",false,"error",RestrictedLogic.Json().Serialize(Value(response,"content")));
            });
            bool failed=a.error!=null||b.error!=null||(int)report["unknown_count_in_page"]>0;
            return ToolResult("双工程指定组对比完成；读取失败、未缓存和缓存中缺失不代表删除。",failed,report);
        }
        private static Dictionary<string,object> CompareSchema()
        {
            return ObjectSchema(new[]{"project_path_a","plc_name_a","project_path_b","plc_name_b"},
                Property("project_path_a",StringProperty("基准工程绝对路径")),Property("plc_name_a",StringProperty("基准 PLC 完整路径")),Property("group_path_a",StringProperty("基准组及子组；空为全部程序块")),Property("unit_name_a",StringProperty("基准软件单元，V21 可用")),
                Property("project_path_b",StringProperty("对照工程绝对路径")),Property("plc_name_b",StringProperty("对照 PLC 完整路径")),Property("group_path_b",StringProperty("对照组及子组；按相对组路径和同名块匹配")),Property("unit_name_b",StringProperty("对照软件单元，V21 可用")),
                Property("read_mode",StringProperty("live（默认）或 cache；缓存缺失不算删除")),Property("offset",Dict("type","integer","minimum",0)),Property("limit",Dict("type","integer","minimum",1,"maximum",20)),Property("catalog_token",StringProperty("下一页带上上一页目录指纹，目录变化则拒绝混页")));
        }
    }
}
