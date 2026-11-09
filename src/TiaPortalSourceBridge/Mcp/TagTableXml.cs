using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;

namespace TiaPortalSourceBridge.Mcp
{
    internal static class TagTableXml
    {
        internal static XmlDocument Parse(string xml)
        {
            if(string.IsNullOrEmpty(xml)||Encoding.UTF8.GetByteCount(xml)>8*1024*1024)throw new ArgumentException("变量表 XML 为空或超过 8 MiB。");
            var settings=new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=8*1024*1024};
            using(var reader=XmlReader.Create(new StringReader(xml),settings))while(reader.Read())if(reader.Depth>100)throw new ArgumentException("变量表 XML 嵌套过深。");
            var doc=new XmlDocument{XmlResolver=null,PreserveWhitespace=true};using(var reader=XmlReader.Create(new StringReader(xml),settings))doc.Load(reader);
            var objects=doc.SelectNodes("/Document/*[starts-with(local-name(),'SW.')]");
            if(objects.Count!=1||objects[0].Name!="SW.Tags.PlcTagTable"||objects[0].SelectSingleNode("AttributeList/Name")==null)throw new ArgumentException("需要包含一个原始 PLC 变量表的 SimaticML Document。");
            return doc;
        }
        private static void Part(StringBuilder text,string value){text.Append(value.Length).Append(':').Append(value);}
        private static void Canonical(XmlNode node,StringBuilder text)
        {
            var element=node as XmlElement;
            if(element==null){if(node is XmlText||node is XmlCDataSection){text.Append('T');Part(text,node.Value);}return;}
            text.Append('E');Part(text,element.NamespaceURI);Part(text,element.LocalName);
            bool generated=new[]{"SW.Tags.PlcTagTable","SW.Tags.PlcTag","SW.Tags.PlcUserConstant","MultilingualText","MultilingualTextItem"}.Contains(element.Name);
            foreach(XmlAttribute attr in element.Attributes.Cast<XmlAttribute>().Where(a=>!(generated&&a.Name=="ID")&&a.Prefix!="xmlns"&&a.Name!="xmlns").OrderBy(a=>a.NamespaceURI,StringComparer.Ordinal).ThenBy(a=>a.LocalName,StringComparer.Ordinal)){text.Append('A');Part(text,attr.NamespaceURI);Part(text,attr.LocalName);Part(text,attr.Value);}
            foreach(XmlNode child in element.ChildNodes)Canonical(child,text);text.Append('/');
        }
        internal static string Signature(string xml)
        {
            var doc=Parse(xml);var text=new StringBuilder();Canonical(doc.SelectSingleNode("/Document/SW.Tags.PlcTagTable"),text);return XmlBatch.Hash(text.ToString());
        }
    }
}
