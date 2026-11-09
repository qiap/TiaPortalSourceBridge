using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml;
using TiaPortalSourceBridge.Indexing;
using TiaPortalSourceBridge.Mcp;
using TiaPortalSourceBridge.Online;

namespace TiaPortalSourceBridge.Web
{
    internal sealed class ParameterMigrationResult
    {
        public List<TextPatch> patches {get;set;}
        public List<string> matched {get;set;}
        public List<string> unmatched {get;set;}
        public string note {get;set;}
    }
    internal static class ParameterMigration
    {
        private sealed class Member {internal IndexEntry Entry;internal XmlElement Node;internal string Type;internal int Db;}
        private static string Literal(string type,object value)
        {
            if(value==null)throw new InvalidOperationException("参数值为空。");
            string name=type.ToLowerInvariant();if(name=="bool"){if(!(value is bool))throw new InvalidOperationException("Bool 参数值类型不符。");return(bool)value?"TRUE":"FALSE";}
            if(value is string||value is bool)throw new InvalidOperationException("数值参数不能用文本或布尔值代替。");
            if(name=="real"){double number=Convert.ToDouble(value,CultureInfo.InvariantCulture);if(double.IsNaN(number)||double.IsInfinity(number)||number>float.MaxValue||number< -float.MaxValue)throw new InvalidOperationException("Real 参数超出范围。");return "REAL#"+((float)number).ToString("R",CultureInfo.InvariantCulture);}
            decimal n=Convert.ToDecimal(value,CultureInfo.InvariantCulture);if(decimal.Truncate(n)!=n)throw new InvalidOperationException("整数参数不能含小数。");
            decimal min=0,max=0;switch(name){case "sint":min=-128;max=127;break;case "byte":case "usint":max=255;break;case "int":min=-32768;max=32767;break;case "word":case "uint":max=65535;break;case "dint":min=int.MinValue;max=int.MaxValue;break;case "time":max=int.MaxValue;break;case "dword":case "udint":max=uint.MaxValue;break;default:throw new InvalidOperationException("暂不支持参数类型："+type);}
            if(n<min||n>max)throw new InvalidOperationException("参数超出 "+type+" 范围。");
            if(name=="time")return "T#"+n.ToString(CultureInfo.InvariantCulture)+"ms";
            if(name=="byte"||name=="word"||name=="dword")return type.ToUpperInvariant()+"#16#"+((uint)n).ToString("X",CultureInfo.InvariantCulture);
            return type.ToUpperInvariant()+"#"+n.ToString(CultureInfo.InvariantCulture);
        }
        private static void Walk(Dictionary<string,Member> members,IndexEntry entry,XmlElement node,string parent,int db)
        {
            string path=parent+"."+ContentIndexer.ComponentName(node.GetAttribute("Name"));string type=node.GetAttribute("Datatype");
            if(type.StartsWith("Array",StringComparison.OrdinalIgnoreCase)||node.HasAttribute("At"))return;
            var children=node.SelectNodes("*[local-name()='Member'] | *[local-name()='Sections']/*[local-name()='Section']/*[local-name()='Member']").OfType<XmlElement>().ToArray();
            if(children.Length>0){foreach(var child in children)Walk(members,entry,child,path,db);return;}
            string key=ContentIndexer.NormalizeSymbol(path);if(members.ContainsKey(key))throw new InvalidOperationException("参数符号存在重复或命名空间歧义："+path);
            members.Add(key,new Member{Entry=entry,Node=node,Type=type,Db=db});
        }
        internal static ParameterMigrationResult Preview(SourceArchive archive,Dictionary<string,object> snapshot)
        {
            if(CylinderWritePipeline.Text(snapshot,"project_path")!=archive.project_path||CylinderWritePipeline.Text(snapshot,"plc_name")!=archive.plc_name)throw new InvalidOperationException("参数快照属于其他工程或 PLC。");
            var profile=CylinderWritePipeline.Json().Deserialize<OnlineProfile>(CylinderWritePipeline.Json().Serialize(snapshot["profile"]));OnlineTools.ValidateProfile(profile);
            var sample=CylinderWritePipeline.Map(snapshot["sample"]);if(!CylinderWritePipeline.True(sample,"complete"))throw new InvalidOperationException("参数快照读取不完整。");var values=CylinderWritePipeline.Map(sample["values"]);
            var docs=new Dictionary<string,XmlDocument>();var members=new Dictionary<string,Member>(StringComparer.OrdinalIgnoreCase);
            foreach(var entry in archive.entries.Where(e=>e.kind=="plc.block"&&string.IsNullOrEmpty(e.unit_name)&&string.IsNullOrEmpty(e.object_namespace))){
                var doc=ProjectTexts.Parse(archive.Sources[entry.id]);var db=doc.SelectSingleNode("/Document/SW.Blocks.GlobalDB");if(db==null||db.SelectSingleNode("AttributeList/MemoryLayout[.='Standard']")==null)continue;
                int number;var n=db.SelectSingleNode("AttributeList/Number");if(n==null||!int.TryParse(n.InnerText,out number))continue;docs[entry.id]=doc;
                foreach(XmlElement field in db.SelectNodes("AttributeList/Interface/*[local-name()='Sections']/*[local-name()='Section' and @Name='Static']/*[local-name()='Member']"))Walk(members,entry,field,ContentIndexer.ComponentName(entry.name),number);
            }
            var result=new ParameterMigrationResult{patches=new List<TextPatch>(),matched=new List<string>(),unmatched=new List<string>(),note="按用户配置的符号名匹配相同 DB 编号、相同标量类型的标准全局 DB。不会猜测改名、转换类型、展开数组、修改实例 DB 或优化 DB。在线名称与物理偏移的对应仍需人工核对；仅修改离线初始值，不恢复在线实际值、不下载。"};var changed=new HashSet<string>();
            foreach(var tag in profile.Tags){Member field;if(!members.TryGetValue(ContentIndexer.NormalizeSymbol(tag.Name),out field)){result.unmatched.Add(tag.Name+"：未找到唯一可支持的标准 DB 成员");continue;}
                var address=PlcAddress.Parse(tag.Address,tag.Type);if(field.Db!=address.Db||!string.Equals(field.Type,tag.Type,StringComparison.OrdinalIgnoreCase)){result.unmatched.Add(tag.Name+"：DB 编号或类型不一致");continue;}
                object raw;if(!values.TryGetValue(tag.Name,out raw))throw new InvalidOperationException("参数快照缺少变量值："+tag.Name);var row=CylinderWritePipeline.Map(raw);
                if(CylinderWritePipeline.Text(row,"quality")!="good"||CylinderWritePipeline.Text(row,"address")!=tag.Address)throw new InvalidOperationException("参数质量或地址证据不一致："+tag.Name);
                string literal=Literal(field.Type,row["value"]);var start=field.Node.SelectSingleNode("*[local-name()='StartValue']");if(start==null){start=field.Node.OwnerDocument.CreateElement("StartValue",field.Node.NamespaceURI);field.Node.AppendChild(start);}
                if(start.InnerText!=literal){start.InnerText=literal;changed.Add(field.Entry.id);}result.matched.Add(tag.Name+" = "+literal);
            }
            foreach(var entry in archive.entries.Where(e=>changed.Contains(e.id))){string xml=docs[entry.id].OuterXml;result.patches.Add(new TextPatch{object_id=entry.id,object_name=entry.name,group=entry.group,kind=entry.kind,before_hash=entry.content_hash,before_xml=archive.Sources[entry.id],after_xml=xml,diff=SemanticDiff.Compare(archive.Sources[entry.id],xml)});}return result;
        }
    }
}
