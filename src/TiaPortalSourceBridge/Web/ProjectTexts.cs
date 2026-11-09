using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using TiaPortalSourceBridge.Indexing;

namespace TiaPortalSourceBridge.Web
{
    internal sealed class ProjectText
    {
        public string object_id {get;set;} public string object_name {get;set;} public string source_hash {get;set;}
        public string resource_id {get;set;} public string location {get;set;} public string language {get;set;} public string text {get;set;}
        public string resource_group {get;set;}
        internal XmlNode Node;
    }
    internal sealed class TextPatch
    {
        public string object_id {get;set;} public string object_name {get;set;} public string group {get;set;} public string kind {get;set;}
        public string before_hash {get;set;} public string before_xml {get;set;} public string after_xml {get;set;} public SemanticDiffResult diff {get;set;}
    }
    internal static class ProjectTexts
    {
        private static readonly string[] Header={"Snapshot","ObjectId","ObjectName","SourceHash","ResourceId","Location","Language","Original","Translation","Action"};
        internal static XmlDocument Parse(string xml)
        {
            var settings=new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=8*1024*1024};
            using(var reader=XmlReader.Create(new StringReader(xml),settings))while(reader.Read())if(reader.Depth>100)throw new InvalidOperationException("文本资源 XML 嵌套过深。");
            var doc=new XmlDocument{XmlResolver=null,PreserveWhitespace=true};using(var reader=XmlReader.Create(new StringReader(xml),settings))doc.Load(reader);return doc;
        }
        private static string Location(XmlNode node)
        {
            var parts=new List<string>();for(var n=node;n is XmlElement;n=n.ParentNode){var e=(XmlElement)n;int ordinal=1;for(var p=n.PreviousSibling;p!=null;p=p.PreviousSibling)if(p is XmlElement&&p.LocalName==n.LocalName&&p.NamespaceURI==n.NamespaceURI)ordinal++;parts.Add(e.LocalName+"["+ordinal+"]"+(e.HasAttribute("Name")?"("+e.GetAttribute("Name")+")":""));}parts.Reverse();return string.Join("/",parts);
        }
        private static IEnumerable<ProjectText> Read(IndexEntry entry,XmlDocument doc)
        {
            foreach(XmlNode item in doc.SelectNodes("//*[local-name()='MultilingualTextItem']/*[local-name()='AttributeList']/*[local-name()='Text'] | //*[local-name()='MultiLanguageText' and @Lang]")){
                string language=item.LocalName=="MultiLanguageText"?((XmlElement)item).GetAttribute("Lang"):item.ParentNode.SelectSingleNode("*[local-name()='Culture']")==null?"":item.ParentNode.SelectSingleNode("*[local-name()='Culture']").InnerText;
                if(string.IsNullOrEmpty(language)||item.ChildNodes.OfType<XmlElement>().Any())continue;
                string location=Location(item);var container=item.LocalName=="MultiLanguageText"?item.ParentNode:item.ParentNode.ParentNode.ParentNode.ParentNode;
                yield return new ProjectText{object_id=entry.id,object_name=entry.name,source_hash=entry.content_hash,resource_id=IndexJson.Hash(entry.id+"\n"+location),location=location,resource_group=entry.id+"/"+Location(container),language=language,text=item.InnerText,Node=item};
            }
        }
        internal static List<ProjectText> Read(SourceArchive archive)
        {
            var rows=new List<ProjectText>();foreach(var entry in archive.entries){rows.AddRange(Read(entry,Parse(archive.Sources[entry.id])));if(rows.Count>20000)throw new InvalidOperationException("文本资源超过 20000 条，请缩小源码范围。");}return rows;
        }
        internal static byte[] Workbook(SourceArchive archive)
        {
            var resources=Read(archive);var rows=new List<string[]>{Header};rows.AddRange(resources.Select(r=>new[]{archive.id,r.object_id,r.object_name,r.source_hash,r.resource_id,r.location,r.language,r.text,r.text,"keep"}));
            var languages=resources.Select(r=>r.language).Distinct().OrderBy(s=>s,StringComparer.Ordinal).ToArray();if(languages.Length>64)throw new InvalidOperationException("快照语言超过 64 种，请缩小范围。");
            var coverage=new List<string[]>{new[]{"对象","资源组","语言","状态","说明"}};
            foreach(var group in resources.GroupBy(r=>r.resource_group))foreach(var language in languages){var found=group.Where(r=>r.language==language).ToArray();coverage.Add(new[]{group.First().object_name,group.Key,language,found.Length==0?"未声明":found.Length>1?"重复语言":string.IsNullOrWhiteSpace(found[0].text)?"空文本":"已有文本","按本快照出现过的语言对照；未声明不等于工程必须提供该译文。本表只读，不自动新增语言。"});if(coverage.Count>200000)throw new InvalidOperationException("语言覆盖表超过 200000 行。");}
            var notes=new[]{new[]{"项目",archive.project_path},new[]{"PLC",archive.plc_name},new[]{"快照",archive.id},new[]{"编辑方式","只修改 Translation，并把 Action 改为 update；keep 行不会修改。Language、Original 和指纹列必须保持原值。"},new[]{"范围","当前 PLC 快照中已有语言的块/网络/接口/变量表文本；不新增工程语言，不含未读取的 HMI、GRAPH 步名称或外部资源。"},new[]{"空文本","Translation 为空且 Action=update 表示明确清空该语言文本。"}};
            return IoWorkbook.CreateSheets(new[]{"Translations","语言覆盖","说明"},new IEnumerable<string[]>[]{rows,coverage,notes});
        }
        internal static List<TextPatch> Preview(SourceArchive archive,byte[] bytes,string filename)
        {
            var imported=IoSheetImport.Load(bytes,filename);var sheet=imported.Sheets.SingleOrDefault(s=>s.Name=="Translations");if(sheet==null||sheet.Rows.Count==0)throw new InvalidOperationException("缺少 Translations 工作表。");
            var head=sheet.Rows[0];for(int i=0;i<Header.Length;i++)if(sheet.Cell(head,i).Error.Length>0||sheet.Cell(head,i).Value!=Header[i])throw new InvalidOperationException("翻译表表头被修改，请使用本工具导出的表格。");
            var docs=archive.entries.ToDictionary(e=>e.id,e=>Parse(archive.Sources[e.id]));var resources=archive.entries.SelectMany(e=>Read(e,docs[e.id])).ToDictionary(r=>r.resource_id);
            var changed=new HashSet<string>();var used=new HashSet<string>();int updates=0;
            foreach(var row in sheet.Rows.Skip(1)){
                var cells=Enumerable.Range(0,Header.Length).Select(c=>sheet.Cell(row,c)).ToArray();if(cells.All(c=>c.Value.Length==0&&c.Error.Length==0))continue;
                if(cells.Any(c=>c.Error.Length>0))throw new InvalidOperationException("第 "+row.Number+" 行含公式、错误或合并单元格。");
                string action=cells[9].Value;if(action!="keep"&&action!="update")throw new InvalidOperationException("Action 只能为 keep 或 update。");if(action=="keep")continue;
                if(++updates>5000)throw new InvalidOperationException("单批最多修改 5000 条文本。");ProjectText source;
                if(cells[0].Value!=archive.id||!resources.TryGetValue(cells[4].Value,out source)||!used.Add(cells[4].Value)||cells[1].Value!=source.object_id||cells[3].Value!=source.source_hash||cells[6].Value!=source.language||cells[7].Value!=source.text)throw new InvalidOperationException("第 "+row.Number+" 行的快照、语言、原文或资源身份不匹配，拒绝猜测。");
                string text=cells[8].Value;if(text.Length>32000)throw new InvalidOperationException("译文超过 32000 字。");if(text==source.text)continue;
                source.Node.InnerText=text;changed.Add(source.object_id);
            }
            return archive.entries.Where(e=>changed.Contains(e.id)).Select(e=>{string xml=docs[e.id].OuterXml;return new TextPatch{object_id=e.id,object_name=e.name,group=e.group,kind=e.kind,before_hash=e.content_hash,before_xml=archive.Sources[e.id],after_xml=xml,diff=SemanticDiff.Compare(archive.Sources[e.id],xml)};}).ToList();
        }
    }
}
