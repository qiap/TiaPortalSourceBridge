using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using TiaPortalSourceBridge.Indexing;

namespace TiaPortalSourceBridge.Web
{
    internal sealed class SourceArchive
    {
        public int schema { get; set; }
        public string id { get; set; }
        public string label { get; set; }
        public string project_path { get; set; }
        public string plc_name { get; set; }
        public string version { get; set; }
        public string created_utc { get; set; }
        public List<string> warnings { get; set; }
        public List<IndexEntry> entries { get; set; }
        internal Dictionary<string, string> Sources = new Dictionary<string, string>();
    }
    internal sealed class ReviewObjectChange
    {
        public string id { get; set; }
        public string name { get; set; }
        public string group { get; set; }
        public string unit { get; set; }
        public string kind { get; set; }
        public string type { get; set; }
        public int changes { get; set; }
        public string coverage { get; set; }
    }
    internal sealed class ReviewImpact
    {
        public string changed { get; set; }
        public string caller { get; set; }
        public string side { get; set; }
        public string caller_name { get; set; }
        public string changed_name { get; set; }
        public List<string> path { get; set; }
    }
    internal sealed class SourceReviewResult
    {
        public List<ReviewObjectChange> objects { get; set; }
        public List<ReviewImpact> impact { get; set; }
        public List<string> warnings { get; set; }
        public int export_noise_only { get; set; }
        public bool partial { get; set; }
        public string before_id { get; set; }
        public string after_id { get; set; }
    }
    internal static class SourceReview
    {
        internal static string Identity(IndexEntry e) { return IndexJson.Hash(string.Join("\n", new[] {e.kind, e.owner, e.unit_name, e.object_namespace, e.group, e.name})); }
        private static string Folder(string root, string id)
        {
            Guid guid; if (!Guid.TryParseExact(id, "N", out guid)) throw new ArgumentException("源码快照编号无效。");
            return SnapshotDiff.Confined(Path.Combine(root, "source-reviews"), id);
        }
        internal static SourceArchive Capture(string root, string project, string plc, string version, string label)
        {
            if (label == null || label.Trim().Length == 0 || label.Length > 120) throw new ArgumentException("请填写 1–120 字的快照名称。");
            var archive = new SourceArchive { schema=1,id=Guid.NewGuid().ToString("N"),label=label.Trim(),project_path=Path.GetFullPath(project),plc_name=plc,version=version,created_utc=DateTime.UtcNow.ToString("o"),warnings=new List<string>(),entries=new List<IndexEntry>() };
            var sources = new ProjectIndexStore(project,version).ArchiveSources(plc,archive.warnings);
            foreach (var pair in sources) {
                var e=pair.Key;e.id=Identity(e);archive.entries.Add(e);
                if (archive.Sources.ContainsKey(e.id)) throw new IOException("源码身份重复："+e.name);
                archive.Sources.Add(e.id,pair.Value);
            }
            string folder=Folder(root,archive.id);Directory.CreateDirectory(folder);
            foreach(var entry in archive.entries) File.WriteAllText(Path.Combine(folder,entry.id+".xml"),archive.Sources[entry.id],new UTF8Encoding(false));
            // Manifest is the commit marker. Interrupted captures remain unlisted.
            File.WriteAllText(Path.Combine(folder,"manifest.tmp"),IndexJson.Serializer().Serialize(archive),new UTF8Encoding(false));
            File.Move(Path.Combine(folder,"manifest.tmp"),Path.Combine(folder,"manifest.json"));
            return archive;
        }
        internal static SourceArchive Load(string root,string id,string project,string plc,bool source=true)
        {
            string folder=Folder(root,id);
            var value=IndexJson.Serializer().Deserialize<SourceArchive>(SnapshotDiff.ReadBounded(SnapshotDiff.Confined(folder,"manifest.json"),8*1024*1024));
            if(value==null||value.schema!=1||value.id!=id||value.plc_name!=plc||!string.Equals(Path.GetFullPath(value.project_path),Path.GetFullPath(project),StringComparison.OrdinalIgnoreCase)||value.entries==null||value.entries.Count>5000||value.warnings==null) throw new IOException("源码快照格式或工程身份不匹配。");
            if(value.entries.Any(e=>e==null||e.id!=Identity(e)||e.owner!=plc)||value.entries.Select(e=>e.id).Distinct().Count()!=value.entries.Count)throw new IOException("源码快照对象身份无效或重复。");
            if(source){long size=0;foreach(var e in value.entries){string xml=SnapshotDiff.ReadBounded(SnapshotDiff.Confined(folder,e.id+".xml"),8*1024*1024);size+=Encoding.UTF8.GetByteCount(xml);if(size>64*1024*1024||IndexJson.Hash(xml)!=e.content_hash)throw new IOException("源码快照文件过大或指纹不符。");value.Sources.Add(e.id,xml);}}
            return value;
        }
        internal static object[] List(string root,string project,string plc)
        {
            string folder=Path.Combine(root,"source-reviews");if(!Directory.Exists(folder))return new object[0];
            var list=new List<SourceArchive>();
            var directories=Directory.EnumerateDirectories(folder).Take(1001).ToArray();
            if(directories.Length>1000)throw new IOException("快照数量过多，请先归档。");
            foreach(string dir in directories){
                try{list.Add(Load(root,Path.GetFileName(dir),project,plc,false));}catch(IOException){}catch(ArgumentException){}catch(InvalidOperationException){}
            }
            return list.OrderByDescending(a=>a.created_utc,StringComparer.Ordinal).Take(100).Select(a=>(object)new {a.id,a.label,a.created_utc,count=a.entries.Count,warnings=a.warnings.Count}).ToArray();
        }
        internal static AuditCallGraph Graph(SourceArchive archive,List<string> warnings)
        {
            var blocks=archive.entries.Where(e=>e.kind=="plc.block").ToArray();
            return CallGraphAudit.Run(new IoSnapshot {project_path=archive.project_path,plc_name=archive.plc_name,cylinderBlocks=blocks,cylinderProgramsRead=blocks.Count(e=>e.language!="DB"),cylinderProgramsTotal=blocks.Count(e=>e.language!="DB")},e=>archive.Sources[e.id],f=>{},w=>warnings.Add(w));
        }
        internal static SourceReviewResult Compare(SourceArchive before,SourceArchive after)
        {
            if(!string.Equals(before.project_path,after.project_path,StringComparison.OrdinalIgnoreCase)||before.plc_name!=after.plc_name)throw new IOException("请对比同一工程与 PLC 的两份快照。");
            var result=new SourceReviewResult {before_id=before.id,after_id=after.id,objects=new List<ReviewObjectChange>(),impact=new List<ReviewImpact>(),warnings=new List<string>(),partial=true};
            result.warnings.Add("影响范围仅追踪两份快照中已解析的显式调用方。数据/UDT 引用、HMI/外部访问、间接调用、DB 偏移和下载重新初始化不在判断范围内；未列出不代表不受影响。");
            result.warnings.AddRange(before.warnings.Concat(after.warnings));
            bool incomplete=before.warnings.Count>0||after.warnings.Count>0;
            var old=before.entries.ToDictionary(e=>e.id);var current=after.entries.ToDictionary(e=>e.id);
            foreach(string id in old.Keys.Union(current.Keys).OrderBy(x=>x,StringComparer.Ordinal)){
                IndexEntry a,b;old.TryGetValue(id,out a);current.TryGetValue(id,out b);var e=b??a;
                if(a!=null&&b!=null&&a.content_hash==b.content_hash)continue;
                var item=new ReviewObjectChange {id=id,name=e.name,group=e.group,unit=e.unit_name,kind=e.kind,type=a==null?"added":b==null?"removed":"modified",coverage="structural"};
                if(a!=null&&b!=null){var diff=SemanticDiff.Compare(before.Sources[id],after.Sources[id]);item.coverage=diff.status;item.changes=diff.changes.Count;if(diff.status=="structural"&&diff.changes.Count==0){result.export_noise_only++;continue;}}
                else if(incomplete){item.type=a==null?"only_after":"only_before";item.coverage="incomplete_snapshot";}
                result.objects.Add(item);
            }
            AddImpact(result,Graph(before,result.warnings),"before");AddImpact(result,Graph(after,result.warnings),"after");
            result.warnings=result.warnings.Distinct().Take(1000).ToList();return result;
        }
        private static void AddImpact(SourceReviewResult result,AuditCallGraph graph,string side)
        {
            var nodes=graph.nodes.ToDictionary(n=>n.id);var incoming=graph.calls.Where(c=>c.resolution=="resolved"&&!string.IsNullOrEmpty(c.callee_id)).GroupBy(c=>c.callee_id).ToDictionary(g=>g.Key,g=>g.Select(c=>c.caller_id).Distinct().ToArray());
            foreach(var change in result.objects){
                var visited=new HashSet<string>{change.id};var queue=new Queue<List<string>>();queue.Enqueue(new List<string>{change.id});
                while(queue.Count>0){var path=queue.Dequeue();string[] callers;if(!incoming.TryGetValue(path[path.Count-1],out callers))continue;
                    foreach(var caller in callers){if(!visited.Add(caller))continue;if(result.impact.Count>=20000||path.Count>=64){result.warnings.Add("调用影响路径达到 20000 项 / 64 层上限；结果不完整。");return;}
                        var next=new List<string>(path);next.Add(caller);queue.Enqueue(next);result.impact.Add(new ReviewImpact {changed=change.id,caller=caller,side=side,path=next,changed_name=change.name,caller_name=nodes.ContainsKey(caller)?nodes[caller].name:caller});}
                }
            }
        }
        internal static byte[] Documentation(SourceArchive archive)
        {
            var warnings=new List<string>(archive.warnings);var graph=Graph(archive,warnings);
            Func<string,string> h=s=>WebUtility.HtmlEncode(s??"");
            var html=new StringBuilder("<!doctype html><meta charset='utf-8'><title>PLC source documentation</title><style>body{font:15px/1.6 sans-serif;margin:32px}table{border-collapse:collapse;width:100%}th,td{border:1px solid #ccc;padding:8px;text-align:left}pre{white-space:pre-wrap}</style>");
            html.Append("<h1>PLC 源码交付资料</h1><p>").Append(h(archive.label)).Append(" · ").Append(h(archive.created_utc)).Append("</p><p>").Append(h(archive.project_path)).Append(" / ").Append(h(archive.plc_name)).Append("</p><p>基于离线源码快照，不代表在线状态。调用图仅包含已解析调用；参见覆盖说明。</p><h2>源码目录</h2><table><tr><th>对象</th><th>软件单元 / 组</th><th>类型</th><th>SHA256</th></tr>");
            foreach(var e in archive.entries)html.Append("<tr><td>").Append(h(e.name)).Append("</td><td>").Append(h(e.unit_name+" / "+e.group)).Append("</td><td>").Append(h(e.kind)).Append("</td><td>").Append(h(e.content_hash)).Append("</td></tr>");
            html.Append("</table><h2>调用关系</h2><table><tr><th>调用方</th><th>目标</th><th>解析状态</th></tr>");
            var names=archive.entries.ToDictionary(e=>e.id,e=>e.name);
            foreach(var c in graph.calls)html.Append("<tr><td>").Append(h(names.ContainsKey(c.caller_id)?names[c.caller_id]:c.caller_id)).Append("</td><td>").Append(h(c.callee_name)).Append("</td><td>").Append(h(c.resolution)).Append("</td></tr>");
            html.Append("</table><h2>覆盖说明</h2><pre>").Append(h(string.Join("\n",warnings.Concat(graph.limitations)))).Append("</pre>");
            using(var output=new MemoryStream()){
                using(var zip=new ZipArchive(output,ZipArchiveMode.Create,true)){
                    WriteZip(zip,"index.html",html.ToString());WriteZip(zip,"manifest.json",IndexJson.Serializer().Serialize(archive));WriteZip(zip,"call-graph.json",IndexJson.Serializer().Serialize(graph));
                    var mermaid=new StringBuilder("flowchart TD\n");foreach(var node in graph.nodes)mermaid.Append("n").Append(node.id).Append("[\"").Append(node.name.Replace("&","&amp;").Replace("\"","&quot;").Replace("<","&lt;").Replace(">","&gt;").Replace("\r"," ").Replace("\n"," ")).Append("\"]\n");
                    foreach(var call in graph.calls.Where(c=>c.resolution=="resolved"&&graph.nodes.Any(n=>n.id==c.callee_id)))mermaid.Append("n").Append(call.caller_id).Append(" --> n").Append(call.callee_id).Append("\n");
                    WriteZip(zip,"calls.mmd",mermaid.ToString());
                    foreach(var e in archive.entries)WriteZip(zip,"sources/"+e.id+".xml",archive.Sources[e.id]);
                    SourceDocumentation.Add(zip,archive);
                }return output.ToArray();
            }
        }
        private static void WriteZip(ZipArchive zip,string path,string text){using(var writer=new StreamWriter(zip.CreateEntry(path).Open(),new UTF8Encoding(false)))writer.Write(text);}
    }
}
