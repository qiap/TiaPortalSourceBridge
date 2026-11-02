using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;
using System.Xml;
using System.Security.Cryptography;
using System.Text;
using TiaPortalSourceBridge.Openness;

namespace TiaPortalSourceBridge.Mcp
{
    // Template-based editing: retain GRAPH options, interface and XML namespaces from TIA.
    internal static class GraphEditor
    {
        internal static XmlDocument Parse(string xml, bool preserveWhitespace = true)
        {
            OperationMetrics.Count("graph_xml_parses");
            XmlDocument doc = BlockOperations.ParseBlock(xml, preserveWhitespace);
            if (doc.SelectSingleNode("/Document/SW.Blocks.FB/AttributeList/ProgrammingLanguage[.='GRAPH']") == null)
                throw new InvalidOperationException("需要 TIA 导出的 GRAPH FB XML。");
            if (doc.SelectNodes("//*[local-name()='Graph']").Count != 1)
                throw new InvalidOperationException("需要一个 GRAPH 流程源。");
            return doc;
        }

        private static XmlNamespaceManager Namespaces(XmlDocument doc)
        {
            XmlNamespaceManager ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("g", doc.SelectSingleNode("//*[local-name()='Graph']").NamespaceURI);
            return ns;
        }

        public static object Inspect(string xml)
        {
            return Inspect(Parse(xml));
        }

        internal static object Inspect(XmlDocument doc, bool includeLogic = true)
        {
            XmlNamespaceManager ns = Namespaces(doc);
            Validate(doc, ns);
            XmlNode nameNode = doc.SelectSingleNode("/Document/SW.Blocks.FB/AttributeList/Name");
            XmlNode numberNode = doc.SelectSingleNode("/Document/SW.Blocks.FB/AttributeList/Number");
            return new
            {
                block_name = nameNode == null ? "" : nameNode.InnerText,
                block_number = numberNode == null ? "" : numberNode.InnerText,
                graph_namespace = ns.LookupNamespace("g"),
                sequences = doc.SelectNodes("//g:Sequence", ns).Cast<XmlElement>().Select((s, index) => new
                {
                    sequence_index = index,
                    steps = s.SelectNodes("g:Steps/g:Step", ns).Cast<XmlElement>().Select(n => new
                    {
                        number = n.GetAttribute("Number"),
                        name = n.GetAttribute("Name"),
                        initial = n.GetAttribute("Init"),
                        maximum_step_time = n.GetAttribute("MaximumStepTime"),
                        warning_time = n.GetAttribute("WarningTime"),
                        actions_xml = includeLogic ? ChildXml(n, "Actions") : "",
                        interlocks_xml = includeLogic ? ChildXml(n, "Interlocks") : "",
                        supervisions_xml = includeLogic ? ChildXml(n, "Supervisions") : ""
                    }).ToArray(),
                    transitions = s.SelectNodes("g:Transitions/g:Transition", ns).Cast<XmlElement>().Select(n => new
                    {
                        number = n.GetAttribute("Number"),
                        name = n.GetAttribute("Name"),
                        language = n.GetAttribute("ProgrammingLanguage"),
                        condition_xml = includeLogic ? n.InnerXml : ""
                    }).ToArray(),
                    branches_xml = ChildXml(s, "Branches"),
                    connections_xml = ChildXml(s, "Connections")
                }).ToArray()
            };
        }

        private static string ChildXml(XmlNode node, string name)
        {
            XmlNode child = node.SelectSingleNode("*[local-name()='" + name + "']");
            return child == null ? "" : child.OuterXml;
        }

        public static string Patch(string xml, string patchesJson, string newName, string newNumber)
        {
            return Patch(Parse(xml), patchesJson, newName, newNumber).OuterXml;
        }

        internal static XmlDocument Patch(XmlDocument doc, string patchesJson, string newName = "", string newNumber = "")
        {
            XmlNamespaceManager ns = Namespaces(doc);
            JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 };
            object[] patches = json.DeserializeObject(patchesJson) as object[];
            if (patches == null || patches.Length > 2000) throw new InvalidOperationException("patches_json 必须是最多2000项的数组。");
            foreach (object item in patches)
            {
                Dictionary<string, object> patch = item as Dictionary<string, object>;
                if (patch == null) throw new InvalidOperationException("每项补丁必须是对象。");
                string xpath = Get(patch, "xpath");
                string op = Get(patch, "op");
                XmlNodeList targets = doc.SelectNodes(xpath, ns);
                if (targets == null || targets.Count != 1)
                    throw new InvalidOperationException("补丁 XPath 必须准确匹配一个节点：" + xpath);
                XmlNode target = targets[0];
                XmlNode ancestor = target is XmlAttribute ? ((XmlAttribute)target).OwnerElement : target;
                while (ancestor != null && ancestor.LocalName != "Graph") ancestor = ancestor.ParentNode;
                if (ancestor == null) throw new InvalidOperationException("补丁只能修改 Graph 内的节点。");

                if (op == "set_attribute")
                {
                    XmlElement element = target as XmlElement;
                    if (element == null) throw new InvalidOperationException("set_attribute 目标必须是元素。");
                    string attr = Get(patch, "attribute");
                    string value = Get(patch, "value");
                    if (attr == "Name" && (element.LocalName == "Step" || element.LocalName == "Transition"))
                        RenameNode(doc, element.LocalName, element.GetAttribute("Name"), value);
                    if (attr == "Number" && new[] { "Step", "Transition", "Branch" }.Contains(element.LocalName))
                        Renumber(element, value);
                    element.SetAttribute(attr, value);
                }
                else if (op == "set_text")
                {
                    if (!(target is XmlElement) || target.ChildNodes.OfType<XmlElement>().Any()) throw new InvalidOperationException("set_text 只能修改叶元素；属性请使用 set_attribute。");
                    target.InnerText = Get(patch, "value");
                }
                else if (op == "replace_xml" || op == "append_xml" || op == "insert_before" || op == "insert_after")
                {
                    XmlDocument fragment = new XmlDocument { XmlResolver = null };
                    string escapedNs = System.Security.SecurityElement.Escape(ns.LookupNamespace("g"));
                    using (XmlReader reader = XmlReader.Create(new System.IO.StringReader("<Root xmlns=\"" + escapedNs + "\">" + Get(patch, "xml") + "</Root>"),
                        new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                    {
                        fragment.Load(reader);
                    }
                    XmlElement[] children = fragment.DocumentElement.ChildNodes.Cast<XmlNode>().OfType<XmlElement>().ToArray();
                    if (children.Length != 1) throw new InvalidOperationException("xml 必须包含一个完整元素。");
                    XmlNode replacement = doc.ImportNode(children[0], true);
                    if (op == "replace_xml")
                    {
                        if (target.LocalName != replacement.LocalName || target.NamespaceURI != replacement.NamespaceURI)
                            throw new InvalidOperationException("替换元素必须与原元素同名、同命名空间。");
                        if (target.LocalName == "Step" || target.LocalName == "Transition")
                            RenameNode(doc, target.LocalName, ((XmlElement)target).GetAttribute("Name"), ((XmlElement)replacement).GetAttribute("Name"));
                        if (new[] { "Step", "Transition", "Branch" }.Contains(target.LocalName)) Renumber((XmlElement)target, ((XmlElement)replacement).GetAttribute("Number"));
                        target.ParentNode.ReplaceChild(replacement, target);
                    }
                    else if (op == "append_xml") target.AppendChild(replacement);
                    else if (op == "insert_before") target.ParentNode.InsertBefore(replacement, target);
                    else target.ParentNode.InsertAfter(replacement, target);
                }
                else if (op == "remove")
                {
                    XmlAttribute attrTarget = target as XmlAttribute;
                    if (attrTarget != null)
                    {
                        if (attrTarget.OwnerElement != null) attrTarget.OwnerElement.RemoveAttributeNode(attrTarget);
                    }
                    else if (target.ParentNode != null)
                    {
                        target.ParentNode.RemoveChild(target);
                    }
                }
                else
                {
                    throw new InvalidOperationException("支持: set_attribute / set_text / replace_xml / append_xml / insert_before / insert_after / remove。");
                }
            }

            XmlNode attrs = doc.SelectSingleNode("/Document/SW.Blocks.FB/AttributeList");
            if (!string.IsNullOrWhiteSpace(newName))
            {
                XmlNode nameNode = attrs.SelectSingleNode("Name");
                if (nameNode != null) nameNode.InnerText = newName.Trim();
            }
            if (!string.IsNullOrWhiteSpace(newNumber))
            {
                int number;
                if (!int.TryParse(newNumber, out number) || number < 1 || number > 65535)
                    throw new InvalidOperationException("块号应为 1～65535。");
                XmlNode numberNode = attrs.SelectSingleNode("Number");
                if (numberNode != null) numberNode.InnerText = number.ToString();
                XmlNode auto = attrs.SelectSingleNode("AutoNumber");
                if (auto != null) auto.InnerText = "false";
            }
            SynchronizeInterface(doc, ns);
            ValidateDocument(doc);
            GraphBuilder.ValidateConnections((XmlElement)doc.SelectSingleNode("//g:Graph", ns));
            GraphBuilder.NormalizeLoops((XmlElement)doc.SelectSingleNode("//g:Graph", ns), new HashSet<XmlNode>());
            return doc;
        }

        internal static string Fingerprint(XmlDocument doc)
        {
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(doc.SelectSingleNode("/Document/SW.Blocks.FB").OuterXml))).Replace("-", "");
        }

        internal static void ValidateDocument(XmlDocument doc)
        {
            if (doc.SelectNodes("//*[local-name()='Graph']").Count != 1 || doc.SelectNodes("//*[local-name()='Graph']/*[local-name()='Sequence']").Count == 0)
                throw new InvalidOperationException("GRAPH 必须保留一个流程源及至少一个顺序。");
            Validate(doc, Namespaces(doc));
        }

        private static void Renumber(XmlElement node, string value)
        {
            int number;
            if (!int.TryParse(value, out number) || number < 1 || number > 65535) throw new InvalidOperationException("GRAPH 编号必须在1..65535。");
            XmlNode sequence = node.ParentNode.ParentNode;
            foreach (XmlElement reference in sequence.SelectNodes("*[local-name()='Connections']//*[local-name()='" + node.LocalName + "Ref']"))
                if (reference.GetAttribute("Number") == node.GetAttribute("Number")) reference.SetAttribute("Number", value);
        }

        private static void SynchronizeInterface(XmlDocument doc, XmlNamespaceManager ns)
        {
            XmlNode section = doc.SelectSingleNode("/Document/SW.Blocks.FB/AttributeList/Interface/*/*[@Name='Static']");
            if (section == null) throw new InvalidOperationException("GRAPH 缺少 Static 接口。");
            var members = section.ChildNodes.OfType<XmlElement>().Where(e => e.LocalName == "Member").ToArray();
            var existing = members.ToDictionary(e => e.GetAttribute("Name"), StringComparer.OrdinalIgnoreCase);
            var desired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (XmlElement node in doc.SelectNodes("//g:Sequence/g:Steps/g:Step | //g:Sequence/g:Transitions/g:Transition", ns))
            {
                string name = node.GetAttribute("Name"), kind = "G7_" + node.LocalName;
                if (name.Length == 0 || !desired.Add(name)) throw new InvalidOperationException("GRAPH 步/转换名称为空或与其他顺序重复：" + name);
                XmlElement member;
                if (existing.TryGetValue(name, out member))
                {
                    if (!member.GetAttribute("Datatype").StartsWith(kind, StringComparison.Ordinal)) throw new InvalidOperationException("GRAPH 名称与其他接口变量冲突：" + name);
                }
                else
                {
                    XmlElement prototype = members.FirstOrDefault(m => m.GetAttribute("Datatype").StartsWith(kind, StringComparison.Ordinal));
                    if (prototype == null) throw new InvalidOperationException("缺少 " + kind + " 接口模板。");
                    member = (XmlElement)prototype.CloneNode(true); member.SetAttribute("Name", name); section.AppendChild(member);
                }
            }
            foreach (XmlElement member in members)
                if ((member.GetAttribute("Datatype").StartsWith("G7_Step", StringComparison.Ordinal) || member.GetAttribute("Datatype").StartsWith("G7_Transition", StringComparison.Ordinal)) && !desired.Contains(member.GetAttribute("Name"))) section.RemoveChild(member);
        }

        private static string Get(Dictionary<string, object> values, string key)
        {
            object value;
            if (!values.TryGetValue(key, out value)) throw new InvalidOperationException("补丁缺少字段: " + key);
            return Convert.ToString(value);
        }

        private static void RenameNode(XmlDocument doc, string kind, string oldName, string newName)
        {
            if (oldName == newName) return;
            if (string.IsNullOrWhiteSpace(newName)) throw new InvalidOperationException("步名称不能为空。");
            foreach (XmlElement member in doc.SelectNodes("/Document/SW.Blocks.FB/AttributeList/Interface//*[local-name()='Member']"))
                if (member.GetAttribute("Name") == oldName && member.GetAttribute("Datatype").StartsWith("G7_" + kind, StringComparison.Ordinal))
                    member.SetAttribute("Name", newName);
            foreach (XmlElement component in doc.SelectNodes("//*[local-name()='Access' and @Scope='LocalVariable']/*[local-name()='Symbol']/*[local-name()='Component'][1]"))
                if (component.GetAttribute("Name") == oldName) component.SetAttribute("Name", newName);
            foreach (XmlElement token in doc.SelectNodes("//*[local-name()='Graph']//*[local-name()='Token' and @Text]"))
            {
                string value = token.GetAttribute("Text"), prefix = value.StartsWith("#") ? "#" : "", symbol = value.Substring(prefix.Length);
                if (symbol == oldName || symbol.StartsWith(oldName + ".", StringComparison.Ordinal)) token.SetAttribute("Text", prefix + newName + symbol.Substring(oldName.Length));
            }
        }

        private static void Validate(XmlDocument doc, XmlNamespaceManager ns)
        {
            foreach (XmlNode sequence in doc.SelectNodes("//g:Sequence", ns))
            {
                Dictionary<string, HashSet<string>> numbers = new Dictionary<string, HashSet<string>>();
                foreach (string kind in new[] { "Step", "Transition", "Branch" })
                {
                    HashSet<string> found = new HashSet<string>();
                    HashSet<string> names = new HashSet<string>();
                    string container = kind == "Branch" ? "Branches" : kind + "s";
                    foreach (XmlElement node in sequence.SelectNodes("g:" + container + "/g:" + kind, ns))
                    {
                        int number;
                        string value = node.GetAttribute("Number");
                        if (!int.TryParse(value, out number) || number < 1 || number > 65535 || !found.Add(number.ToString()))
                            throw new InvalidOperationException("GRAPH " + kind + " 编号无效或重复：" + value);
                        if (kind != "Branch" && (string.IsNullOrWhiteSpace(node.GetAttribute("Name")) || !names.Add(node.GetAttribute("Name"))))
                            throw new InvalidOperationException("GRAPH " + kind + " 名称为空或重复。");
                    }
                    numbers[kind] = found;
                }
                if (numbers["Step"].Count == 0 || sequence.SelectNodes("g:Steps/g:Step[@Init='true']", ns).Count == 0)
                    throw new InvalidOperationException("GRAPH 顺序需要步和初始步。");
                foreach (XmlElement reference in sequence.SelectNodes("g:Connections//*[self::g:StepRef or self::g:TransitionRef or self::g:BranchRef]", ns))
                {
                    string kind = reference.LocalName.Replace("Ref", "");
                    if (!numbers[kind].Contains(reference.GetAttribute("Number")))
                        throw new InvalidOperationException("GRAPH 连线引用不存在的 " + kind + "：" + reference.GetAttribute("Number"));
                }
            }
        }
    }
}
