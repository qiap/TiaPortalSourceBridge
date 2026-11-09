using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;
using TiaPortalSourceBridge.Indexing;
using TiaPortalSourceBridge.Mcp;

namespace TiaPortalSourceBridge.Web
{
    internal static class SourceDocumentation
    {
        private static void Text(ZipArchive zip,string path,string text){Bytes(zip,path,Encoding.UTF8.GetBytes(text));}
        private static void Bytes(ZipArchive zip,string path,byte[] bytes){using(var stream=zip.CreateEntry(path).Open())stream.Write(bytes,0,bytes.Length);}
        private static string Label(string text){return (text??"").Replace("&","&amp;").Replace("\"","&quot;").Replace("<","&lt;").Replace(">","&gt;").Replace("\r"," ").Replace("\n"," ");}
        internal static void Add(ZipArchive zip,SourceArchive archive)
        {
            var warnings=new List<string>();var io=new IoSnapshot{project_path=archive.project_path,plc_name=archive.plc_name,read_started_utc=archive.created_utc,read_completed_utc=archive.created_utc};var blocks=archive.entries.Where(e=>e.kind=="plc.block").ToArray();var refs=new List<object>();
            foreach(var table in archive.entries.Where(e=>e.kind=="plc.tags")){io.tables_total++;try{io.rows.AddRange(IoTable.Parse(archive.Sources[table.id],table.name,table.group,table.unit_name));io.tables_read++;}catch(Exception ex){warnings.Add(table.name+": "+ex.Message);}}
            var contents=new Dictionary<string,IndexContent>();foreach(var entry in blocks){try{contents[entry.id]=ContentIndexer.Parse(archive.Sources[entry.id]);}catch(Exception ex){warnings.Add(entry.name+": "+ex.Message);}}
            IoMapping.Expand(io,blocks,e=>archive.Sources[e.id]);IoDeclarations.Add(io,blocks,e=>archive.Sources[e.id]);
            foreach(var entry in blocks.Where(e=>contents.ContainsKey(e.id)))refs.AddRange(contents[entry.id].references.Where(r=>new[]{"reads","writes","reads_writes","symbol_reference","token_reference","address"}.Contains(r.relation)).Select(r=>(object)new{block=entry.name,group=entry.group,unit=entry.unit_name,state="snapshot",target=r.target,access=r.relation,location=r.location,evidence=r.evidence}));
            IoTable.AttachIndex(io,IndexJson.Map(new{total=blocks.Length,ready=contents.Count,references=refs,errors=new object[0]}));io.warnings.AddRange(archive.warnings);io.warnings.AddRange(warnings);Bytes(zip,"IO-and-references.xlsx",IoWorkbook.Create(io,io.rows.ToArray()));
            try{Bytes(zip,"Translations.xlsx",ProjectTexts.Workbook(archive));}catch(Exception ex){warnings.Add("文本表："+ex.Message);}
            foreach(var entry in blocks.Where(e=>e.language=="GRAPH")){
                try{
                    var doc=GraphEditor.Parse(archive.Sources[entry.id]);Text(zip,"graph/"+entry.id+".json",IndexJson.Serializer().Serialize(GraphEditor.Inspect(doc)));
                    var graph=new StringBuilder("flowchart TD\n");int sequence=0;
                    foreach(XmlElement seq in doc.SelectNodes("//*[local-name()='Graph']/*[local-name()='Sequence']")){
                        string prefix="s"+(sequence++)+"_";
                        foreach(XmlElement n in seq.SelectNodes("*[local-name()='Steps']/* | *[local-name()='Transitions']/* | *[local-name()='Branches']/*"))graph.Append(prefix).Append(n.LocalName).Append(n.GetAttribute("Number")).Append("[\"").Append(Label(n.LocalName+" "+n.GetAttribute("Number")+" "+n.GetAttribute("Name"))).Append("\"]\n");
                        foreach(XmlElement link in seq.SelectNodes("*[local-name()='Connections']/*")){
                            var a=(XmlElement)link.SelectSingleNode("*[local-name()='NodeFrom']/*");var b=(XmlElement)link.SelectSingleNode("*[local-name()='NodeTo']/*");if(a==null||b==null)throw new InvalidOperationException("无法识别 GRAPH 连线。");
                            graph.Append(prefix).Append(a.LocalName.Replace("Ref","")).Append(a.GetAttribute("Number")).Append(" --> ").Append(prefix).Append(b.LocalName.Replace("Ref","")).Append(b.GetAttribute("Number")).Append("\n");
                        }
                    }Text(zip,"graph/"+entry.id+".mmd",graph.ToString());
                }catch(Exception ex){warnings.Add("GRAPH "+entry.name+": "+ex.Message);}
            }
            Text(zip,"coverage.txt","此包来自离线源码快照；未读取的对象不表示不存在。I/O 引用仅为显式源码证据；GRAPH 图不表示实时状态。HMI 报警与未读取的 HMI 文本不包含在本 PLC 源码包中。\n"+string.Join("\n",archive.warnings.Concat(warnings)));
        }
    }
}
