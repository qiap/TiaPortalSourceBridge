using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using System.Xml;

namespace TiaPortalSourceBridge.Web
{
    internal sealed class SemanticChange
    {
        public string area { get; set; }
        public string path { get; set; }
        public string type { get; set; }
        public string before { get; set; }
        public string after { get; set; }
    }
    internal sealed class SemanticDiffResult
    {
        public string status { get; set; }
        public string object_name { get; set; }
        public List<string> warnings { get; set; }
        public List<SemanticChange> changes { get; set; }
        public SemanticDiffResult() { status = "partial"; warnings = new List<string>(); changes = new List<SemanticChange>(); }
    }

    // Structural comparison, not a proof of program equivalence. Unknown XML is
    // retained as evidence; never discard identifiers in an unknown network grammar.
    internal static class SemanticDiff
    {
        private sealed class Model
        {
            internal string Identity;
            internal Dictionary<string, string> Fields = new Dictionary<string, string>(StringComparer.Ordinal);
            internal List<string> Warnings = new List<string>();
        }
        internal static SemanticDiffResult Compare(string before, string after)
        {
            var result = new SemanticDiffResult();
            result.warnings.Add("只读结构化对比，不判断逻辑等价、DB 实际偏移、下载重新初始化或 HMI 影响。网络按原始顺序对应，插入或移动可能产生多条变化。改名按删除与新增展示，不猜测身份。");
            try {
                Model a = Parse(before), b = Parse(after);
                result.object_name = b.Identity;
                if (a.Identity != b.Identity) throw new InvalidOperationException("两份 XML 的对象类型、名称或命名空间不一致，无法确认为同一个对象。");
                result.warnings.AddRange(a.Warnings.Concat(b.Warnings).Distinct());
                foreach (string key in a.Fields.Keys.Union(b.Fields.Keys).OrderBy(k => k, StringComparer.Ordinal)) {
                    string x, y; bool hasA = a.Fields.TryGetValue(key, out x), hasB = b.Fields.TryGetValue(key, out y);
                    if (hasA && hasB && x == y) continue;
                    if (result.changes.Count >= 10000) throw new InvalidOperationException("结构化变化超过 10000 项，请缩小对比对象。");
                    result.changes.Add(new SemanticChange { area = key.Split('/')[0], path = key, type = !hasA ? "add" : !hasB ? "delete" : "change", before = x, after = y });
                }
                result.status = "structural";
            } catch (Exception ex) {
                result.status = "unsupported"; result.changes.Clear(); result.warnings.Add("结构化对比不可用：" + ex.Message + " 请查看原始文本。");
            }
            return result;
        }
        private static IEnumerable<XmlElement> Children(XmlNode node) { return node.ChildNodes.OfType<XmlElement>(); }
        private static IEnumerable<XmlElement> Walk(XmlNode node)
        {
            foreach (XmlElement child in Children(node)) { yield return child; foreach (var sub in Walk(child)) yield return sub; }
        }
        private static XmlElement Child(XmlNode node, string name) { return Children(node).FirstOrDefault(n => n.LocalName == name); }
        private static string Value(XmlNode node, string name) { var child = node == null ? null : Child(node, name); return child == null ? "" : child.InnerText; }
        private static string Key(string value) { return (value ?? "").Replace("%", "%25").Replace("/", "%2F"); }
        private static string AttributeKey(XmlAttribute attr) { return Key(attr.NamespaceURI.Length == 0 ? attr.LocalName : "{" + attr.NamespaceURI + "}" + attr.LocalName); }
        private static void Put(Model model, string key, string value)
        {
            if (model.Fields.ContainsKey(key)) throw new InvalidOperationException("对象中存在重复结构路径：" + key);
            if (model.Fields.Count >= 50000) throw new InvalidOperationException("结构字段超过 50000 项。");
            model.Fields.Add(key, value);
        }
        private static Model Parse(string xml)
        {
            if (xml == null || xml.Length > 8 * 1024 * 1024) throw new InvalidOperationException("XML 超过 8 MiB 字符限制。");
            using (var guard = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                while (guard.Read()) if (guard.Depth > 100) throw new InvalidOperationException("XML 嵌套过深。");
            var doc = new XmlDocument { XmlResolver = null, PreserveWhitespace = true };
            using (var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 8 * 1024 * 1024 })) doc.Load(reader);
            if (doc.DocumentElement == null || doc.DocumentElement.LocalName != "Document") throw new InvalidOperationException("仅支持单对象 SimaticML XML；SCL 文本、SimaticSD 和其他格式保留文本对比。");
            var objects = Children(doc.DocumentElement).Where(e => e.LocalName.StartsWith("SW.", StringComparison.Ordinal)).ToArray();
            if (objects.Length != 1) throw new InvalidOperationException("需要单个 PLC 块、数据类型或变量表 XML。");
            XmlElement obj = objects[0];
            if (!new[] { "SW.Blocks.OB", "SW.Blocks.FB", "SW.Blocks.FC", "SW.Blocks.GlobalDB", "SW.Blocks.InstanceDB", "SW.Types.PlcStruct", "SW.Tags.PlcTagTable" }.Contains(obj.LocalName)) throw new InvalidOperationException("尚未覆盖对象类型：" + obj.LocalName);
            var attrs = Child(obj, "AttributeList");
            if (attrs == null || string.IsNullOrEmpty(Value(attrs, "Name"))) throw new InvalidOperationException("缺少对象名称。");
            var model = new Model { Identity = obj.LocalName + " / " + Value(attrs, "Namespace") + " / " + Value(attrs, "Name") };
            Put(model, "其他/文档容器", Canonical(Shallow(doc.DocumentElement), false));
            // Export envelope alone is ignored; timestamps inside engineering objects are retained.
            foreach (var extra in Children(doc.DocumentElement).Where(e => e != obj && e.LocalName != "Engineering" && e.LocalName != "DocumentInfo"))
                Put(model, "其他/文档/" + Key(extra.LocalName), Canonical(extra, false));
            ReadObject(model, obj, "对象");
            return model;
        }
        private static void ReadObject(Model model, XmlElement obj, string path)
        {
            Put(model, path + "/XML命名空间", obj.NamespaceURI);
            // Preserve non-ID attributes, including unknown attributes.
            foreach (XmlAttribute attr in obj.Attributes)
                if (attr.Name != "ID" && attr.Prefix != "xmlns" && attr.Name != "xmlns") Put(model, path + "/@" + AttributeKey(attr), attr.Value);
            int network = 0, other = 0;
            foreach (XmlElement child in Children(obj)) {
                if (child.LocalName == "AttributeList") {
                    Put(model, path + "/属性容器", Canonical(Shallow(child), false));
                    foreach (var field in Children(child)) {
                        if (field.LocalName == "Interface") {
                            var sections = Child(field, "Sections");
                            if (sections == null) Put(model, "接口/未识别", Canonical(field, false));
                            else {
                                var remainder = (XmlElement)field.CloneNode(true);
                                remainder.RemoveChild(Child(remainder, "Sections"));
                                Put(model, "接口/容器", Canonical(remainder, false));
                                ReadInterface(model, sections, "接口");
                            }
                        } else Put(model, path + "/" + Key(field.LocalName), Canonical(field, false));
                    }
                } else if (child.LocalName == "ObjectList") {
                    Put(model, path + "/子对象容器", Canonical(Shallow(child), false));
                    var tagOrder = new List<string>();
                    foreach (var entry in Children(child)) {
                        if (entry.LocalName == "SW.Blocks.CompileUnit") {
                            network++;
                            string p = "网络/" + network.ToString("D4");
                            var ns = entry.SelectSingleNode("*[local-name()='AttributeList']/*[local-name()='NetworkSource']") as XmlElement;
                            var copy = (XmlElement)entry.CloneNode(true);
                            var copySource = copy.SelectSingleNode("*[local-name()='AttributeList']/*[local-name()='NetworkSource']");
                            if (ns != null) { Put(model, p + "/程序", Network(ns, model)); copySource.ParentNode.RemoveChild(copySource); }
                            Put(model, p + "/属性与注释", Canonical(copy, true));
                        } else if (entry.LocalName == "SW.Tags.PlcTag" || entry.LocalName == "SW.Tags.PlcUserConstant") {
                            string name = Value(Child(entry, "AttributeList"), "Name");
                            if (name.Length == 0) throw new InvalidOperationException("变量缺少名称。");
                            tagOrder.Add(entry.LocalName + ":" + name);
                            ReadObject(model, entry, "变量/" + Key(entry.LocalName) + "/" + Key(name));
                        } else if (entry.LocalName == "MultilingualText") {
                            ReadText(model, entry, path + "/文本/" + Key(entry.GetAttribute("CompositionName")));
                        } else {
                            Put(model, path + "/其他/" + (++other) + "/" + Key(entry.LocalName), Canonical(entry, false));
                            model.Warnings.Add("存在未细分的 XML 内容，已完整保留在“其他”项中；相关编号变化可能仍显示差异。");
                        }
                    }
                    if (tagOrder.Count > 0) Put(model, "变量/声明顺序", string.Join("\n", tagOrder));
                } else Put(model, path + "/其他/" + (++other) + "/" + Key(child.LocalName), Canonical(child, false));
            }
        }
        private static void ReadText(Model model, XmlElement text, string path)
        {
            // Preserve everything except export object IDs, including language keys.
            Put(model, path, Canonical(text, true));
        }
        private static void ReadInterface(Model model, XmlElement parent, string path)
        {
            Put(model, path + "/XML命名空间", parent.NamespaceURI);
            foreach (XmlAttribute attr in parent.Attributes)
                if (attr.Prefix != "xmlns" && attr.Name != "xmlns") Put(model, path + "/@" + AttributeKey(attr), attr.Value);
            var order = new List<string>(); int other = 0;
            foreach (XmlElement entry in Children(parent)) {
                if (entry.LocalName == "Section" || entry.LocalName == "Member") {
                    string name = entry.GetAttribute("Name");
                    if (name.Length == 0) throw new InvalidOperationException("接口成员缺少名称。");
                    order.Add(entry.LocalName + ":" + name);
                    ReadInterface(model, entry, path + "/" + Key(name));
                } else {
                    Put(model, path + "/" + Key(entry.LocalName) + "/" + (++other), Canonical(entry, false));
                }
            }
            if (order.Count > 0) Put(model, path + "/声明顺序", string.Join("\n", order));
        }
        private static string Network(XmlElement source, Model model)
        {
            var copy = (XmlElement)source.CloneNode(true);
            var roots = Children(copy).ToArray();
            if (roots.Length != 1 || (roots[0].LocalName != "FlgNet" && roots[0].LocalName != "StructuredText")) {
                model.Warnings.Add("GRAPH、STL 及未知网络使用保守 XML 对比，保留原编号；未分析分支控制流。");
                return Canonical(copy, false);
            }
            var nodes = Walk(copy).ToArray();
            var known = new[] { "Access", "Part", "Call", "Wire", "OpenCon", "Token", "Blank", "NewLine", "NameCon", "IdentCon", "Component", "Constant", "ConstantValue", "Instance", "Instruction", "LineComment", "NamelessParameter", "Parameter", "Symbol", "TemplateValue", "Text" };
            if (nodes.Any(n => n.HasAttribute("UId") && !known.Contains(n.LocalName)) ||
                nodes.Any(n => n.Attributes.Cast<XmlAttribute>().Any(a => a.LocalName != "UId" && (a.LocalName.EndsWith("Id", StringComparison.OrdinalIgnoreCase) || a.LocalName.EndsWith("Ref", StringComparison.OrdinalIgnoreCase))))) {
                model.Warnings.Add("网络含未知编号或引用属性，保留原编号进行保守对比。");
                return Canonical(source, false);
            }
            var map = new Dictionary<string,string>(StringComparer.Ordinal);
            foreach (var node in nodes.Where(n => n.HasAttribute("UId") && n.LocalName != "NameCon" && n.LocalName != "IdentCon")) {
                string id = node.GetAttribute("UId");
                if (map.ContainsKey(id)) { model.Warnings.Add("网络存在重复 UId，保留原编号进行保守对比。"); return Canonical(source, false); }
                map.Add(id, "n" + map.Count);
            }
            foreach (var node in nodes.Where(n => n.HasAttribute("UId"))) {
                string value;
                if (!map.TryGetValue(node.GetAttribute("UId"), out value)) { model.Warnings.Add("网络含未解析的连接引用，保留原编号进行保守对比。"); return Canonical(source, false); }
                node.SetAttribute("UId", value);
            }
            return Canonical(copy, false);
        }
        private static XmlElement Shallow(XmlElement node) { return (XmlElement)node.CloneNode(false); }
        private static string Canonical(XmlElement node, bool ignoreObjectIds)
        {
            var output = new StringBuilder();
            using (var writer = XmlWriter.Create(output, new XmlWriterSettings { OmitXmlDeclaration = true, Indent = true, NewLineChars = "\n" })) Write(writer, node, ignoreObjectIds, 0);
            return output.ToString();
        }
        private static void Write(XmlWriter writer, XmlElement node, bool ignoreObjectIds, int depth)
        {
            if (depth > 100) throw new InvalidOperationException("XML 嵌套过深。");
            writer.WriteStartElement(node.LocalName, node.NamespaceURI);
            foreach (XmlAttribute attr in node.Attributes.Cast<XmlAttribute>().OrderBy(a => a.NamespaceURI, StringComparer.Ordinal).ThenBy(a => a.LocalName, StringComparer.Ordinal)) {
                if (attr.Prefix == "xmlns" || attr.Name == "xmlns" || (ignoreObjectIds && attr.Name == "ID" && (node.LocalName == "SW.Blocks.CompileUnit" || node.LocalName == "MultilingualText" || node.LocalName == "MultilingualTextItem"))) continue;
                writer.WriteAttributeString(attr.NamespaceURI == "http://www.w3.org/XML/1998/namespace" ? "xml" : null, attr.LocalName, attr.NamespaceURI, attr.Value);
            }
            foreach (XmlNode child in node.ChildNodes) {
                var element = child as XmlElement;
                if (element != null) Write(writer, element, ignoreObjectIds, depth + 1);
                else if (child.NodeType == XmlNodeType.Text || child.NodeType == XmlNodeType.CDATA || child.NodeType == XmlNodeType.SignificantWhitespace || (child.NodeType == XmlNodeType.Whitespace && !Children(node).Any())) writer.WriteString(child.Value);
                else if (child.NodeType == XmlNodeType.Comment) writer.WriteComment(child.Value);
                else if (child.NodeType == XmlNodeType.ProcessingInstruction) writer.WriteProcessingInstruction(child.Name, child.Value);
            }
            writer.WriteEndElement();
        }
    }
}
