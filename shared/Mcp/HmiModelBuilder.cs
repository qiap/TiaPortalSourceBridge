using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using System.Xml;

namespace TiaPortalSourceBridge.Mcp
{
    internal sealed class HmiDocument
    {
        public string kind { get; set; }
        public string group_path { get; set; }
        public string name { get; set; }
        public string xml { get; set; }
        internal string RootOuterXml;
    }

    internal static class HmiModelBuilder
    {
        private static object Val(Dictionary<string, object> m, string key, object fallback = null) { object v; return m.TryGetValue(key, out v) ? v : fallback; }
        private static string Text(Dictionary<string, object> m, string key, string fallback = "") { return Convert.ToString(Val(m, key, fallback)); }
        private static Dictionary<string, object> Map(object value) { var map = value as Dictionary<string, object>; if (map == null) throw new ArgumentException("HMI 模型项必须是对象。"); return map; }
        private static object[] Rows(Dictionary<string, object> m, string key) { return Val(m, key) as object[] ?? new object[0]; }
        private static string Scalar(object value) { return value is bool ? (bool)value ? "true" : "false" : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture); }
        private static XmlElement Add(XmlNode parent, string name) { var e = parent.OwnerDocument.CreateElement(XmlConvert.VerifyName(name)); parent.AppendChild(e); return e; }
        private static void Attribute(XmlNode parent, string key, object value)
        {
            XmlNode attributes = parent.SelectSingleNode("AttributeList") ?? Add(parent, "AttributeList");
            XmlNode attribute = attributes.SelectSingleNode(XmlConvert.VerifyName(key)) ?? Add(attributes, key); attribute.InnerText = Scalar(value);
        }
        private static XmlNode Objects(XmlNode parent) { return parent.SelectSingleNode("ObjectList") ?? Add(parent, "ObjectList"); }
        private static void Link(XmlNode parent, string property, string value)
        {
            XmlNode list = parent.SelectSingleNode("LinkList") ?? Add(parent, "LinkList");
            XmlElement link = (XmlElement)(list.SelectSingleNode(XmlConvert.VerifyName(property)) ?? Add(list, property));
            link.SetAttribute("TargetID", "@OpenLink"); link.RemoveAll(); link.SetAttribute("TargetID", "@OpenLink"); Add(link, "Name").InnerText = value;
        }

        public static HmiDocument[] Generate(string modelJson, string version, string root)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 };
            var model = Map(json.DeserializeObject(modelJson)); var result = new List<HmiDocument>();
            foreach (object row in Rows(model, "objects"))
            {
                var m = Map(row); var doc = NewDocument(version); int count = 0;
                doc.DocumentElement.AppendChild(BuildTree(doc, Map(Val(m, "model")), 0, ref count));
                XmlNode name = doc.SelectSingleNode("/Document/*/AttributeList/Name");
                result.Add(new HmiDocument { kind = Text(m, "kind"), group_path = Text(m, "group_path"), name = name == null ? "" : name.InnerText, xml = doc.OuterXml });
            }
            foreach (object row in Rows(model, "screens"))
            {
                var m = Map(row); string name = RequiredName(m); var doc = NewDocument(version); var screen = Add(doc.DocumentElement, "Hmi.Screen.Screen");
                Attribute(screen, "Name", name); Attribute(screen, "Number", Val(m, "number", 1)); Attribute(screen, "Width", Val(m, "width", 800)); Attribute(screen, "Height", Val(m, "height", 480));
                Attribute(screen, "BackColor", Val(m, "back_color", "192, 192, 192")); Attribute(screen, "Visible", true); Attribute(screen, "ActiveLayer", 0);
                ApplyAttributes(screen, Val(m, "attributes"));
                var layers = new Dictionary<int, XmlNode>(); var itemNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (object itemRow in Rows(m, "items"))
                {
                    var itemModel = Map(itemRow); string type = Text(itemModel, "type"); int layerIndex = Convert.ToInt32(Val(itemModel, "layer", 0));
                    if (layerIndex < 0 || layerIndex > 31) throw new ArgumentException("HMI 层号应为 0..31。");
                    XmlNode layer;
                    if (!layers.TryGetValue(layerIndex, out layer))
                    {
                        layer = Add(Objects(screen), "Hmi.Screen.ScreenLayer"); ((XmlElement)layer).SetAttribute("CompositionName", "Layers");
                        Attribute(layer, "Index", layerIndex); Attribute(layer, "Name", ""); Attribute(layer, "VisibleES", true); layers[layerIndex] = layer;
                    }
                    XmlElement item = LoadTemplate(doc, root, version, type);
                    string objectName = RequiredName(itemModel);
                    if (!itemNames.Add(objectName)) throw new ArgumentException("控件名称重复：" + objectName);
                    item.SetAttribute("CompositionName", "ScreenItems"); Attribute(item, "ObjectName", objectName);
                    foreach (var pair in new[] { new[] { "left", "Left" }, new[] { "top", "Top" }, new[] { "width", "Width" }, new[] { "height", "Height" } })
                        if (itemModel.ContainsKey(pair[0])) Attribute(item, pair[1], itemModel[pair[0]]);
                    ApplyAttributes(item, Val(itemModel, "attributes"));
                    if (itemModel.ContainsKey("text")) SetText(item, Text(itemModel, "text"), Text(itemModel, "language", "zh-CN"));
                    if (!string.IsNullOrWhiteSpace(Text(itemModel, "tag")))
                    {
                        XmlElement property = Add(Objects(item), "Hmi.Screen.Property"); property.SetAttribute("CompositionName", "Properties"); Attribute(property, "Name", Text(itemModel, "tag_property", "ProcessValue"));
                        var dynamic = Add(Objects(property), "Hmi.Dynamic.TagConnectionDynamic"); dynamic.SetAttribute("CompositionName", "Dynamic"); Attribute(dynamic, "Indirect", false); Link(dynamic, "Tag", Text(itemModel, "tag"));
                    }
                    if (!string.IsNullOrWhiteSpace(Text(itemModel, "text_list"))) Link(item, "TextList", Text(itemModel, "text_list"));
                    if (!string.IsNullOrWhiteSpace(Text(itemModel, "events_xml")))
                    {
                        var fragment = doc.CreateDocumentFragment(); fragment.InnerXml = Text(itemModel, "events_xml"); Objects(item).AppendChild(fragment);
                    }
                    Objects(layer).AppendChild(item);
                }
                if (layers.Count == 0)
                {
                    var layer = Add(Objects(screen), "Hmi.Screen.ScreenLayer"); layer.SetAttribute("CompositionName", "Layers"); Attribute(layer, "Index", 0); Attribute(layer, "Name", ""); Attribute(layer, "VisibleES", true);
                }
                AssignIds(screen);
                result.Add(new HmiDocument { kind = "screens", name = name, group_path = Text(m, "group_path"), xml = doc.OuterXml });
            }
            foreach (object row in Rows(model, "tag_tables"))
            {
                var m = Map(row); string name = RequiredName(m); var doc = NewDocument(version); var table = Add(doc.DocumentElement, "Hmi.Tag.TagTable"); Attribute(table, "Name", name);
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (object tagRow in Rows(m, "tags"))
                {
                    var t = Map(tagRow); string tagName = RequiredName(t); if (!names.Add(tagName)) throw new ArgumentException("变量名重复：" + tagName);
                    var tag = LoadTemplate(doc, root, version, "Tag"); tag.SetAttribute("CompositionName", "Tags"); Attribute(tag, "Name", tagName);
                    Link(tag, "DataType", Text(t, "data_type", "Bool")); Link(tag, "HmiDataType", Text(t, "data_type", "Bool"));
                    if (t.ContainsKey("connection")) Link(tag, "Connection", Text(t, "connection"));
                    if (t.ContainsKey("address")) { Attribute(tag, "AddressAccessMode", "Absolute"); Attribute(tag, "LogicalAddress", Text(t, "address")); }
                    if (t.ContainsKey("plc_tag")) { Attribute(tag, "AddressAccessMode", "Symbolic"); Link(tag, "PlcTag", Text(t, "plc_tag")); }
                    if (t.ContainsKey("acquisition_cycle")) Link(tag, "AcquisitionCycle", Text(t, "acquisition_cycle"));
                    ApplyAttributes(tag, Val(t, "attributes")); Objects(table).AppendChild(tag);
                }
                AssignIds(table); result.Add(new HmiDocument { kind = "tag_tables", name = name, group_path = Text(m, "group_path"), xml = doc.OuterXml });
            }
            if (result.Count == 0) throw new ArgumentException("HMI 模型需要 screens、tag_tables 或 objects。");
            return result.ToArray();
        }
        private static XmlDocument NewDocument(string version)
        {
            var doc = new XmlDocument { XmlResolver = null }; doc.LoadXml("<Document><Engineering/></Document>"); ((XmlElement)doc.SelectSingleNode("/Document/Engineering")).SetAttribute("version", version); return doc;
        }
        private static string RequiredName(Dictionary<string, object> m) { string name = Text(m, "name").Trim(); if (name.Length == 0) throw new ArgumentException("HMI 对象 name 不能为空。"); return name; }
        private static void ApplyAttributes(XmlNode target, object value)
        { var attributes = value as Dictionary<string, object>; if (attributes != null) foreach (var pair in attributes) Attribute(target, pair.Key, pair.Value); }
        private static XmlElement LoadTemplate(XmlDocument doc, string root, string version, string type)
        {
            if (!new[] { "Button", "TextField", "IOField", "Rectangle", "DateTimeField", "GraphicIOField", "SymbolicIOField", "Tag" }.Contains(type))
                throw new ArgumentException("没有内置控件模板：" + type + "。可使用 objects 通用节点模型构建任意已知 HMI XML 对象。");
            // Common fragments contain only Classic WinCC object defaults, not an Engineering document/version.
            string path = Path.Combine(root, "templates", "hmi", version.ToLowerInvariant(), type + ".xml");
            if (!File.Exists(path)) path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "templates", "hmi", version.ToLowerInvariant(), type + ".xml");
            if (!File.Exists(path) && (version == "V17" || version == "V21")) path = Path.Combine(root, "templates", "hmi", "common", type + ".xml");
            if (!File.Exists(path) && (version == "V17" || version == "V21")) path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "templates", "hmi", "common", type + ".xml");
            if (!File.Exists(path)) throw new InvalidOperationException("当前版本没有内置 HMI 控件模板。可使用同版本读取到的 objects 通用模型。版本：" + version);
            var template = new XmlDocument { XmlResolver = null };
            using (XmlReader reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) template.Load(reader);
            return (XmlElement)doc.ImportNode(template.DocumentElement, true);
        }
        private static void SetText(XmlNode item, string text, string culture)
        {
            XmlNode multilingual = item.SelectSingleNode("ObjectList/MultilingualText[@CompositionName='Text' or @CompositionName='TextOff']");
            if (multilingual == null) { multilingual = Add(Objects(item), "MultilingualText"); ((XmlElement)multilingual).SetAttribute("CompositionName", "Text"); }
            var mlItem = Add(Objects(multilingual), "MultilingualTextItem"); mlItem.SetAttribute("CompositionName", "Items");
            foreach (XmlNode old in multilingual.SelectNodes("ObjectList/MultilingualTextItem").Cast<XmlNode>().Where(n => n != mlItem).ToArray()) old.ParentNode.RemoveChild(old);
            Attribute(mlItem, "Culture", culture); var attr = (XmlElement)mlItem.SelectSingleNode("AttributeList");
            var value = Add(attr, "Text"); var body = Add(value, "body"); Add(body, "p").InnerText = text;
        }
        private static void AssignIds(XmlNode root)
        {
            int id = 0;
            foreach (XmlElement node in new[] { (XmlElement)root }.Concat(root.SelectNodes(".//*[@ID or @CompositionName]").Cast<XmlElement>())) node.SetAttribute("ID", (id++).ToString("X"));
        }
        public static object ReadTree(XmlElement root)
        {
            return new { element = root.Name, namespace_uri = root.NamespaceURI, attributes = root.Attributes.Cast<XmlAttribute>().ToDictionary(a => a.Name, a => a.Value),
                content = root.ChildNodes.Cast<XmlNode>().Where(n => n is XmlElement || n is XmlCharacterData).Select(n => n is XmlElement ? ReadTree((XmlElement)n) : (object)n.Value).ToArray() };
        }
        private static XmlElement BuildTree(XmlDocument doc, Dictionary<string, object> model, int depth, ref int count)
        {
            if (depth > 64 || ++count > 20000) throw new ArgumentException("HMI 模型过大或嵌套过深。");
            var element = doc.CreateElement(XmlConvert.VerifyName(Text(model, "element")), Text(model, "namespace_uri"));
            var attributes = Val(model, "attributes") as Dictionary<string, object>;
            if (attributes != null) foreach (var pair in attributes) element.SetAttribute(XmlConvert.VerifyName(pair.Key), Scalar(pair.Value));
            foreach (object part in Rows(model, "content"))
                if (part is string) element.AppendChild(doc.CreateTextNode((string)part)); else element.AppendChild(BuildTree(doc, Map(part), depth + 1, ref count));
            return element;
        }
    }
}
