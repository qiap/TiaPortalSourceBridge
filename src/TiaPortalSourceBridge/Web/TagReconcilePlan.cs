using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using TiaPortalSourceBridge.Indexing;
using TiaPortalSourceBridge.Mcp;

namespace TiaPortalSourceBridge.Web
{
    internal sealed class TagReconcilePlan
    {
        public string id {get;set;}
        public string project_path {get;set;}
        public string plc_name {get;set;}
        public string table_name {get;set;}
        public string group_path {get;set;}
        public string before_xml {get;set;}
        public string after_xml {get;set;}
        public List<Dictionary<string,object>> dependencies {get;set;}
        public SemanticDiffResult diff {get;set;}
        public int[] rows {get;set;}
        public string source_snapshot {get;set;}
        public string recovery_of {get;set;}
        private static string Field(XmlNode node,string name){var n=node.SelectSingleNode("AttributeList/"+name);return n==null?"":n.InnerText;}
        private static void Set(XmlElement tag,string name,string value){var a=tag.SelectSingleNode("AttributeList");var n=a.SelectSingleNode(name);if(n==null){n=tag.OwnerDocument.CreateElement(name);a.AppendChild(n);}n.InnerText=value;}
        private static readonly Dictionary<string,int> Widths=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase){{"Bool",1},{"Byte",8},{"SInt",8},{"USInt",8},{"Word",16},{"Int",16},{"UInt",16},{"DWord",32},{"DInt",32},{"UDInt",32},{"Real",32},{"Time",32}};
        private static long[] Address(string address,string type)
        {
            int width;if(!Widths.TryGetValue(type??"",out width))throw new InvalidOperationException("点表写回暂不支持类型："+type);
            var m=Regex.Match(IoTable.Address(address),"^([IQM])([BWD]?)([0-9]{1,9})(?:\\.([0-7]))?$");
            if(!m.Success)throw new InvalidOperationException("点位地址无效："+address);
            int declared=m.Groups[2].Value=="B"?8:m.Groups[2].Value=="W"?16:m.Groups[2].Value=="D"?32:1;
            if(width!=declared||(width==1)!=m.Groups[4].Success)throw new InvalidOperationException("地址宽度与类型不一致："+address+" / "+type);
            long start=long.Parse(m.Groups[3].Value,CultureInfo.InvariantCulture)*8+(m.Groups[4].Success?int.Parse(m.Groups[4].Value):0);
            return new[]{(long)m.Groups[1].Value[0],start,start+width-1};
        }
        internal static TagReconcilePlan Build(IoReconcileReport report,SourceArchive archive,int[] selected,string culture)
        {
            if(report==null||report.options.scope!="native"||!string.IsNullOrEmpty(report.options.unit)||string.IsNullOrEmpty(report.options.table))throw new InvalidOperationException("写回前请选择原始变量表、根软件范围和一个明确的变量表，再重新对账。");
            if(report.project_path!=archive.project_path||report.plc_name!=archive.plc_name)throw new InvalidOperationException("对账与源码快照目标不一致。");
            if(selected==null||selected.Length==0||selected.Length>500||selected.Distinct().Count()!=selected.Length)throw new InvalidOperationException("请选择 1–500 条不重复的 Excel 数据行。");
            try{CultureInfo.GetCultureInfo(culture);}catch{throw new InvalidOperationException("注释语言代码无效。");}
            if(string.IsNullOrEmpty(culture))throw new InvalidOperationException("请指定注释语言。");
            var table=archive.entries.SingleOrDefault(e=>e.kind=="plc.tags"&&string.IsNullOrEmpty(e.unit_name)&&e.group+"\n"+e.name==report.options.table);
            if(table==null)throw new InvalidOperationException("目标变量表缺少可用的源码快照。");
            if(archive.warnings.Count>0)throw new InvalidOperationException("源码快照不完整，请重新读取后再生成写回方案。");
            var plan=new TagReconcilePlan{id=Guid.NewGuid().ToString("N"),project_path=archive.project_path,plc_name=archive.plc_name,table_name=table.name,group_path=table.group,before_xml=archive.Sources[table.id],rows=selected,source_snapshot=archive.id,dependencies=new List<Dictionary<string,object>>()};
            var all=new List<IoRow>();
            var tables=archive.entries.Where(e=>e.kind=="plc.tags").ToArray();
            if(report.nativeTableSignatures==null||report.nativeTableSignatures.Count!=tables.Length)throw new InvalidOperationException("对账报告缺少完整的变量表指纹，请重新读取并对账。");
            foreach(var entry in tables){
                if(!string.IsNullOrEmpty(entry.unit_name))throw new InvalidOperationException("点表写回暂不处理软件单元内的变量表。");
                string signature;
                if(!report.nativeTableSignatures.TryGetValue((entry.unit_name??"")+"\n"+(entry.group??"")+"\n"+entry.name,out signature)||signature!=TagTableXml.Signature(archive.Sources[entry.id]))throw new InvalidOperationException("对账后变量表源码已变化，请重新读取并对账。");
                all.AddRange(IoTable.Parse(archive.Sources[entry.id],entry.name,entry.group,entry.unit_name));
                plan.dependencies.Add(new Dictionary<string,object>{{"table_name",entry.name},{"group_path",entry.group},{"signature",TagTableXml.Signature(archive.Sources[entry.id])}});
            }
            var doc=TagTableXml.Parse(plan.before_xml);var root=doc.SelectSingleNode("/Document/SW.Tags.PlcTagTable") as XmlElement;
            if(root==null||Field(root,"Name")!=table.name)throw new InvalidOperationException("变量表源码身份不一致。");
            var objects=root.SelectSingleNode("ObjectList");if(objects==null){objects=doc.CreateElement("ObjectList");root.AppendChild(objects);}
            int nextId=1;foreach(XmlElement n in doc.SelectNodes("//*[@ID]")){int id;if(int.TryParse(n.GetAttribute("ID"),NumberStyles.HexNumber,CultureInfo.InvariantCulture,out id)&&id<100000000)nextId=Math.Max(nextId,id+1);}
            Func<string> idText=()=> (nextId++).ToString("X",CultureInfo.InvariantCulture);
            var touched=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(int number in selected){
                var candidates=report.rows.Where(r=>r.excel_row==number).ToArray();if(candidates.Length!=1)throw new InvalidOperationException("所选 Excel 行不存在或不唯一。");
                var row=candidates[0];if(row.status!="different"&&row.status!="excel_only")throw new InvalidOperationException("冲突、无效、不明确和仅 PLC 行不能写回："+number);
                if(string.IsNullOrWhiteSpace(row.name)||row.name.Length>128||row.name.Any(char.IsControl)||!touched.Add(row.name))throw new InvalidOperationException("变量名称为空、过长或重复。");
                XmlElement tag=null;IoRow old=null;
                if(row.status=="different"){
                    if(row.plc.Count!=1||row.plc[0].group!=table.group||row.plc[0].table!=table.name)throw new InvalidOperationException("PLC 候选不属于目标变量表。");
                    var point=row.plc[0];old=all.SingleOrDefault(r=>r.name==point.name&&r.table==table.name&&r.group==table.group&&r.unit==point.unit);
                    if(old==null||IoTable.Address(old.address)!=IoTable.Address(point.address)||old.data_type!=point.type)throw new InvalidOperationException("变量表已变化，请重新对账。");
                    tag=objects.SelectNodes("SW.Tags.PlcTag").Cast<XmlElement>().SingleOrDefault(t=>Field(t,"Name")==point.name);
                    if(tag==null)throw new InvalidOperationException("不支持修改常量或无法唯一定位变量。");
                }
                string type=report.options.type>=0?row.type:old==null?"":old.data_type;
                var range=Address(row.address,type);
                if(all.Any(r=>r!=old&&string.Equals(r.name,row.name,StringComparison.OrdinalIgnoreCase)))throw new InvalidOperationException("变量名称已被其他点位使用："+row.name);
                if(old==null||IoTable.Address(old.address)!=IoTable.Address(row.address)||!string.Equals(old.data_type,type,StringComparison.OrdinalIgnoreCase)){
                    foreach(var other in all.Where(r=>r!=old&&r.area!="CONST")){
                        long[] span;try{span=Address(other.address,other.data_type);}catch{throw new InvalidOperationException("已有点位地址或类型不支持范围核对，请先处理："+other.name);}
                        if(span[0]==range[0]&&span[1]<=range[2]&&range[1]<=span[2])throw new InvalidOperationException("地址与已有点位重叠："+other.name);
                    }
                }
                if(tag==null){tag=doc.CreateElement("SW.Tags.PlcTag");tag.SetAttribute("ID",idText());tag.SetAttribute("CompositionName","Tags");tag.AppendChild(doc.CreateElement("AttributeList"));objects.AppendChild(tag);}
                Set(tag,"Name",row.name);Set(tag,"DataTypeName",type);Set(tag,"LogicalAddress","%"+IoTable.Address(row.address));
                if(report.options.comment>=0){
                    if(tag.SelectSingleNode("AttributeList/Comment")!=null)throw new InvalidOperationException("此变量使用无语言标记的注释，无法确定写回语言，请先在博途中规范注释："+row.name);
                    var list=tag.SelectSingleNode("ObjectList");if(list==null){list=doc.CreateElement("ObjectList");tag.AppendChild(list);}
                    var text=list.SelectSingleNode("MultilingualText[@CompositionName='Comment']") as XmlElement;
                    if(text==null){text=doc.CreateElement("MultilingualText");text.SetAttribute("ID",idText());text.SetAttribute("CompositionName","Comment");text.AppendChild(doc.CreateElement("ObjectList"));list.AppendChild(text);}
                    var items=text.SelectSingleNode("ObjectList");if(items==null){items=doc.CreateElement("ObjectList");text.AppendChild(items);}
                    var matches=items.SelectNodes("MultilingualTextItem").Cast<XmlElement>().Where(n=>Field(n,"Culture")==culture).ToArray();if(matches.Length>1)throw new InvalidOperationException("注释语言存在重复条目。");
                    var item=matches.FirstOrDefault();if(item==null){item=doc.CreateElement("MultilingualTextItem");item.SetAttribute("ID",idText());item.SetAttribute("CompositionName","Items");item.AppendChild(doc.CreateElement("AttributeList"));items.AppendChild(item);Set(item,"Culture",culture);}Set(item,"Text",row.comment??"");
                }
                if(old!=null)all.Remove(old);all.Add(new IoRow{name=row.name,address=row.address,data_type=type,area=IoTable.Address(row.address).Substring(0,1),table=table.name,group=table.group,unit=""});
            }
            plan.after_xml=doc.OuterXml;plan.diff=SemanticDiff.Compare(plan.before_xml,plan.after_xml);
            if(plan.diff.status!="structural"||plan.diff.changes.Count==0)throw new InvalidOperationException("没有可核实的点表修改。");return plan;
        }
    }
}
