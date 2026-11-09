using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml;
using TiaPortalSourceBridge.Web;

namespace TiaPortalSourceBridge.Mcp
{
    internal sealed class HmiDeviceAlarm
    {
        public int id {get;set;} public int template_id {get;set;} public string trigger_tag {get;set;} public int trigger_bit {get;set;}
        public string text {get;set;} public string operation {get;set;}
    }
    internal sealed class HmiDeviceRow
    {
        public string name {get;set;} public string instance {get;set;} public int index {get;set;} public HmiDeviceAlarm[] alarms {get;set;}
    }
    internal sealed class HmiCompanionConfiguration
    {
        public string culture {get;set;} public string text_list {get;set;} public HmiDeviceRow[] devices {get;set;}
    }
    internal static class HmiCompanionPlan
    {
        internal static void Validate(HmiCompanionConfiguration config)
        {
            if(config==null||config.devices==null||config.devices.Length<1||config.devices.Length>20)throw new ArgumentException("请选择 1–20 个机构。");
            if(string.IsNullOrEmpty(config.culture)||CultureInfo.GetCultureInfo(config.culture).IsNeutralCulture)throw new ArgumentException("请选择明确的项目语言，例如 zh-CN。");
            var instances=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var indexes=new HashSet<int>();var ids=new HashSet<int>();var triggers=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var row in config.devices){
                if(row==null||string.IsNullOrWhiteSpace(row.name)||row.name.Length>64||string.IsNullOrWhiteSpace(row.instance)||row.instance.Length>64||row.name.Any(char.IsControl)||row.instance.Any(char.IsControl)||!instances.Add(row.instance)||!indexes.Add(row.index)||row.index<0)throw new ArgumentException("机构名称、实例名或文本索引无效、重复。");
                if(row.alarms==null||row.alarms.Length<1||row.alarms.Length>8)throw new ArgumentException("每个机构需配置 1–8 条离散报警。");
                foreach(var alarm in row.alarms){
                    if(alarm==null||alarm.id<=0||alarm.template_id<=0||!ids.Add(alarm.id)||string.IsNullOrWhiteSpace(alarm.trigger_tag)||alarm.trigger_tag.Length>256||alarm.trigger_tag.Any(char.IsControl)||alarm.trigger_bit<0||alarm.trigger_bit>15||!triggers.Add(alarm.trigger_tag+"\n"+alarm.trigger_bit)||string.IsNullOrWhiteSpace(alarm.text)||alarm.text.Length>256||alarm.text.Any(char.IsControl)||!new[]{"create","update"}.Contains(alarm.operation))throw new ArgumentException("报警编号/触发位重复或无效；需要模板编号、明确的 Word 变量位 0–15、文本与 create/update 操作。");
                }
            }
        }
        internal static string Changes(HmiCompanionConfiguration config)
        {
            Validate(config);return CylinderWritePipeline.Json().Serialize(config.devices.SelectMany(row=>row.alarms.Select(a=>new{
                id=a.id,op=a.operation,template_id=a.template_id,name=row.instance+"_"+a.id,trigger_tag=a.trigger_tag,trigger_bit=a.trigger_bit,
                texts=new Dictionary<string,string>{{config.culture,a.text.Replace("{name}",row.name).Replace("{instance}",row.instance).Replace("{index}",row.index.ToString(CultureInfo.InvariantCulture))}}
            })).ToArray());
        }
        // Reuse explicitly reserved entries. Creating arbitrary XML shapes or
        // silently replacing other languages/embedded dynamic fields is refused.
        internal static string TextList(string xml,HmiCompanionConfiguration config)
        {
            Validate(config);var doc=ProjectTexts.Parse(xml);var root=doc.SelectSingleNode("/Document/Hmi.TextGraphicList.TextList");
            if(root==null||root.SelectSingleNode("AttributeList/Name")==null||root.SelectSingleNode("AttributeList/Name").InnerText!=config.text_list)throw new InvalidOperationException("文本列表身份不一致。");
            var entries=root.SelectNodes(".//Hmi.TextGraphicList.TextListEntry").Cast<XmlElement>().ToArray();
            foreach(var row in config.devices){
                var matches=entries.Where(e=>{var value=e.SelectSingleNode("AttributeList/Value");int n;return value!=null&&int.TryParse(value.InnerText,out n)&&n==row.index;}).ToArray();
                if(matches.Length!=1)throw new InvalidOperationException("文本索引 "+row.index+" 不是唯一、已声明的单值条目；范围/动态或缺失条目不自动生成。");
                var texts=matches[0].SelectNodes(".//MultilingualText[@CompositionName='Text']/ObjectList/MultilingualTextItem").Cast<XmlElement>().Where(t=>t.SelectSingleNode("AttributeList/Culture")!=null&&t.SelectSingleNode("AttributeList/Culture").InnerText==config.culture).Select(t=>t.SelectSingleNode("AttributeList/Text")).ToArray();
                if(texts.Length!=1||texts[0]==null||texts[0].ChildNodes.OfType<XmlElement>().Any()||texts[0].InnerText.Contains("<field")||matches[0].SelectNodes(".//*[local-name()='FieldInfo']").Count>0)throw new InvalidOperationException("文本索引 "+row.index+" 缺少唯一目标语言纯文本，或含动态字段。");
                texts[0].InnerText=row.name;
            }return doc.OuterXml;
        }
    }
}
