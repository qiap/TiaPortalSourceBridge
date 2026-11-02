using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Xml;

namespace TiaPortalSourceBridge.Mcp
{
    // A complete flow model; original XML fragments preserve features outside the shorthand builders.
    internal static class GraphBuilder
    {
        private static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 }; }
        private static object Val(Dictionary<string, object> m, string k, object fallback = null) { object v; return m.TryGetValue(k, out v) ? v : fallback; }
        private static string Text(Dictionary<string, object> m, string k, string fallback = "") { return Convert.ToString(Val(m, k, fallback)); }
        private static Dictionary<string, object> Map(object value) { return value as Dictionary<string, object> ?? throwMap(); }
        private static Dictionary<string, object> throwMap() { throw new ArgumentException("GRAPH 模型项必须是对象。"); }
        private static object[] Rows(Dictionary<string, object> m, string k) { return Val(m, k) as object[] ?? new object[0]; }
        private static int Number(Dictionary<string, object> m, string k) { int n = Convert.ToInt32(Val(m, k, 0)); if (n < 1 || n > 65535) throw new ArgumentException(k + " 应为 1..65535。"); return n; }
        private static XmlElement Add(XmlNode parent, string name) { var e = parent.OwnerDocument.CreateElement(name, parent.NamespaceURI); parent.AppendChild(e); return e; }
        private static void Attr(XmlElement e, string key, object value) { e.SetAttribute(key, Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)); }
        private static XmlNode Child(XmlNode n, string local) { return n.SelectSingleNode("*[local-name()='" + local + "']"); }

        public static string Generate(string modelJson, string baseXml, string version, string toolRoot)
        {
            return GenerateDocument(modelJson, baseXml, version, toolRoot).OuterXml;
        }

        internal static XmlDocument GenerateDocument(string modelJson, string baseXml, string version, string toolRoot)
        {
            if (string.IsNullOrWhiteSpace(version)) version = "V21";
            var model = Map(Json().DeserializeObject(modelJson));
            bool usingTemplate = string.IsNullOrWhiteSpace(baseXml);
            if (usingTemplate)
            {
                string path = Path.Combine(toolRoot, "templates", "graph", version.ToLowerInvariant() + ".xml");
                if (!File.Exists(path)) path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "templates", "graph", version.ToLowerInvariant() + ".xml");
                if (!File.Exists(path)) throw new InvalidOperationException("当前 TIA V21 工具仅内置 V21 GRAPH 模板，请使用 V21 或提供同版本 base_xml。版本：" + version);
                baseXml = File.ReadAllText(path);
            }
            XmlDocument doc = GraphEditor.Parse(baseXml, !usingTemplate);
            XmlNode engineering = doc.SelectSingleNode("/Document/Engineering/@version");
            if (engineering == null || !string.Equals(engineering.Value, version, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("GRAPH 模板必须与目标 TIA 版本一致。");
            GraphEditor.ValidateDocument(doc);
            XmlNode block = doc.SelectSingleNode("/Document/SW.Blocks.FB/AttributeList");
            string name = Text(model, "block_name").Trim();
            if (name.Length == 0) throw new ArgumentException("block_name 不能为空。");
            block.SelectSingleNode("Name").InnerText = name;
            string nsValue = Text(model, "namespace").Trim();
            if (nsValue.Length > 0 && block.SelectSingleNode("Namespace") != null)
                block.SelectSingleNode("Namespace").InnerText = nsValue;
            block.SelectSingleNode("Number").InnerText = Number(model, "block_number").ToString();
            if (block.SelectSingleNode("AutoNumber") != null) block.SelectSingleNode("AutoNumber").InnerText = "false";
            if (!string.IsNullOrWhiteSpace(Text(model, "interface_xml")))
            {
                var interfaceDoc = new XmlDocument { XmlResolver = null };
                using (XmlReader reader = XmlReader.Create(new StringReader(Text(model, "interface_xml")), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) interfaceDoc.Load(reader);
                if (interfaceDoc.DocumentElement.Name != "Interface") throw new ArgumentException("interface_xml 必须是 Interface 元素。");
                block.ReplaceChild(doc.ImportNode(interfaceDoc.DocumentElement, true), block.SelectSingleNode("Interface"));
            }
            var settings = Val(model, "block_attributes") as Dictionary<string, object>;
            if (settings != null) foreach (var setting in settings)
            {
                if (new[] { "Name", "Number", "AutoNumber", "Interface", "ProgrammingLanguage", "GraphVersion" }.Contains(setting.Key)) continue;
                XmlNode target = block.SelectSingleNode("*[local-name()='" + XmlConvert.VerifyNCName(setting.Key) + "']");
                if (target == null || target.ChildNodes.OfType<XmlElement>().Any()) throw new ArgumentException("不支持的 GRAPH 块属性：" + setting.Key);
                target.InnerText = Convert.ToString(setting.Value);
            }
            var graph = (XmlElement)doc.SelectSingleNode("//*[local-name()='Graph']");
            string ns = graph.NamespaceURI;
            XmlNode staticSection = doc.SelectSingleNode("/Document/SW.Blocks.FB/AttributeList/Interface/*/*[@Name='Static']");
            if (staticSection == null) throw new InvalidOperationException("GRAPH 模板缺少 Static 接口段。");
            XmlNode stepMember = staticSection.SelectSingleNode("*[local-name()='Member' and starts-with(@Datatype,'G7_Step')]");
            XmlNode transitionMember = staticSection.SelectSingleNode("*[local-name()='Member' and starts-with(@Datatype,'G7_Transition')]");
            if (stepMember == null || transitionMember == null) throw new InvalidOperationException("GRAPH 模板需要至少一个步和转换的接口定义。");
            stepMember = stepMember.CloneNode(true); transitionMember = transitionMember.CloneNode(true);
            var originalMembers = staticSection.SelectNodes("*[local-name()='Member']").Cast<XmlElement>().ToDictionary(e => e.GetAttribute("Name"), e => e.CloneNode(true), StringComparer.Ordinal);
            var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
            var automaticLinks = new HashSet<XmlNode>();
            foreach (XmlNode member in staticSection.SelectNodes("*[local-name()='Member' and (starts-with(@Datatype,'G7_Step') or starts-with(@Datatype,'G7_Transition'))]").Cast<XmlNode>().ToArray()) staticSection.RemoveChild(member);
            foreach (XmlNode seq in graph.SelectNodes("*[local-name()='Sequence']").Cast<XmlNode>().ToArray()) graph.RemoveChild(seq);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (XmlElement member in staticSection.SelectNodes("*[local-name()='Member']")) names.Add(member.GetAttribute("Name"));
            object[] sequences = Rows(model, "sequences");
            if (sequences.Length == 0) throw new ArgumentException("至少需要一个 sequence。");
            foreach (object row in sequences)
            {
                var sequenceModel = Map(row);
                XmlElement sequence = doc.CreateElement("Sequence", ns);
                XmlNode post = Child(graph, "PostOperations"); graph.InsertBefore(sequence, post);
                if (!string.IsNullOrEmpty(Text(sequenceModel, "title")))
                {
                    var title = Add(sequence, "Title"); var ml = Add(title, "MultiLanguageText"); Attr(ml, "Lang", Text(sequenceModel, "language", "zh-CN")); ml.InnerText = Text(sequenceModel, "title");
                }
                else if (!string.IsNullOrWhiteSpace(Text(sequenceModel, "title_xml"))) sequence.AppendChild(Fragment(doc, Text(sequenceModel, "title_xml"), ns, "Title"));
                if (!string.IsNullOrWhiteSpace(Text(sequenceModel, "comment_xml"))) sequence.AppendChild(Fragment(doc, Text(sequenceModel, "comment_xml"), ns, "Comment"));
                XmlElement steps = Add(sequence, "Steps");
                foreach (object stepRow in Rows(sequenceModel, "steps"))
                {
                    var s = Map(stepRow); string stepName = Text(s, "name").Trim();
                    XmlElement step = Element(doc, s, "Step", ns);
                    string oldStepName = step.GetAttribute("Name");
                    if (oldStepName.Length > 0 && oldStepName != stepName) renamed[oldStepName] = stepName;
                    Attr(step, "Number", Number(s, "number")); Attr(step, "Name", stepName); Attr(step, "Init", Convert.ToBoolean(Val(s, "initial", false)) ? "true" : "false");
                    if (s.ContainsKey("maximum_step_time")) Attr(step, "MaximumStepTime", Text(s, "maximum_step_time"));
                    if (s.ContainsKey("warning_time")) Attr(step, "WarningTime", Text(s, "warning_time"));
                    XmlElement actions = doc.CreateElement("Actions", ns);
                    if (s.ContainsKey("actions"))
                    {
                        foreach (object actionRow in Rows(s, "actions"))
                        {
                            var a = Map(actionRow); var action = Add(actions, "Action");
                            foreach (string key in new[] { "qualifier", "event", "interlock" }) if (a.ContainsKey(key)) Attr(action, char.ToUpperInvariant(key[0]) + key.Substring(1), key == "interlock" ? Text(a, key).ToLowerInvariant() : Text(a, key));
                            string expression = Text(a, "text", Text(a, "operand"));
                            if (a.ContainsKey("tokens")) foreach (object token in Rows(a, "tokens")) Attr(Add(action, "Token"), "Text", Convert.ToString(token));
                            else foreach (Match token in Regex.Matches(expression, @"'(?:(?:'')|[^'])*'|[\p{L}][\p{L}\p{N}_]*\#[\p{L}\p{N}_#+.\-:]+|\#?(?:""[^""]+""|[\p{L}_][\p{L}\p{N}_]*)(?:\[[^\]\r\n]+\])?(?:\.(?:""[^""]+""|[\p{L}_][\p{L}\p{N}_]*)(?:\[[^\]\r\n]+\])?)*|\d+(?:\.\d+)?|:=|<=|>=|<>|\S")) Attr(Add(action, "Token"), "Text", token.Value);
                            Attr(Add(action, "Token"), "Text", "\n");
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(Text(s, "actions_xml"))) actions = (XmlElement)Fragment(doc, Text(s, "actions_xml"), ns, "Actions");
                    ReplaceChild(step, "Actions", actions);
                    ReplaceChild(step, "Supervisions", Module(doc, s, "supervisions", "Supervisions", "Supervision", "SvCoil", ns));
                    ReplaceChild(step, "Interlocks", Module(doc, s, "interlocks", "Interlocks", "Interlock", "IlCoil", ns));
                    XmlNode existingStepMember; originalMembers.TryGetValue(oldStepName.Length > 0 ? oldStepName : stepName, out existingStepMember);
                    steps.AppendChild(step); AddMember(staticSection, existingStepMember ?? stepMember, stepName, names);
                }
                XmlElement transitions = Add(sequence, "Transitions");
                foreach (object transitionRow in Rows(sequenceModel, "transitions"))
                {
                    var t = Map(transitionRow); string transitionName = Text(t, "name").Trim();
                    XmlElement transition = Element(doc, t, "Transition", ns);
                    string oldTransitionName = transition.GetAttribute("Name");
                    if (oldTransitionName.Length > 0 && oldTransitionName != transitionName) renamed[oldTransitionName] = transitionName;
                    Attr(transition, "Number", Number(t, "number")); Attr(transition, "Name", transitionName); Attr(transition, "ProgrammingLanguage", Text(t, "language", "LAD"));
                    ReplaceChild(transition, "FlgNet", t.ContainsKey("condition") ? Condition(doc, Val(t, "condition"), "TrCoil", ns)
                        : !string.IsNullOrWhiteSpace(Text(t, "condition_xml")) ? Fragment(doc, Text(t, "condition_xml"), ns, "FlgNet") : Condition(doc, true, "TrCoil", ns));
                    XmlNode existingTransitionMember; originalMembers.TryGetValue(oldTransitionName.Length > 0 ? oldTransitionName : transitionName, out existingTransitionMember);
                    transitions.AppendChild(transition); AddMember(staticSection, existingTransitionMember ?? transitionMember, transitionName, names);
                }
                XmlElement branches = Add(sequence, "Branches");
                foreach (object branchRow in Rows(sequenceModel, "branches"))
                {
                    var b = Map(branchRow); var branch = Element(doc, b, "Branch", ns);
                    string type = Text(b, "type"); if (!new[] { "AltBegin", "AltEnd", "SimBegin", "SimEnd" }.Contains(type)) throw new ArgumentException("分支类型无效。");
                    int cardinality = Number(b, "cardinality"); if (cardinality < 2) throw new ArgumentException("分支至少需要两个出口或入口。");
                    Attr(branch, "Number", Number(b, "number")); Attr(branch, "Type", type); Attr(branch, "Cardinality", cardinality); branches.AppendChild(branch);
                }
                XmlElement connections = Add(sequence, "Connections");
                foreach (object connectionRow in Rows(sequenceModel, "connections"))
                {
                    var c = Map(connectionRow); var connection = Add(connections, "Connection");
                    Reference(Add(connection, "NodeFrom"), Map(Val(c, "from")), true);
                    Reference(Add(connection, "NodeTo"), Map(Val(c, "to")), false);
                    string link = Text(c, "link_type", "Direct"); if (link != "Direct" && link != "Jump") throw new ArgumentException("link_type 仅支持 Direct 或 Jump。");
                    Add(connection, "LinkType").InnerText = link;
                    if (!c.ContainsKey("link_type")) automaticLinks.Add(connection);
                }
            }
            foreach (var pair in new[] { new[] { "pre_operations_xml", "PreOperations" }, new[] { "post_operations_xml", "PostOperations" }, new[] { "alarms_settings_xml", "AlarmsSettings" } })
                if (!string.IsNullOrWhiteSpace(Text(model, pair[0]))) ReplaceChild(graph, pair[1], Fragment(doc, Text(model, pair[0]), ns, pair[1]));
            NormalizeLoops(graph, automaticLinks);
            XmlNode sections = staticSection.ParentNode;
            foreach (object row in Rows(model, "interface_members"))
            {
                var memberModel = Map(row); string sectionName = Text(memberModel, "section", "Static"); string memberName = Text(memberModel, "name");
                if (!new[] { "Input", "Output", "InOut", "Static", "Temp", "Constant" }.Contains(sectionName)) throw new ArgumentException("接口段无效。");
                if (sections.SelectNodes("*[local-name()='Section']/*[local-name()='Member']").Cast<XmlElement>().Any(e => string.Equals(e.GetAttribute("Name"), memberName, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("接口变量重复：" + memberName);
                if (string.IsNullOrWhiteSpace(memberName) || string.IsNullOrWhiteSpace(Text(memberModel, "datatype"))) throw new ArgumentException("接口变量需要 name 和 datatype。");
                XmlNode section = sections.SelectSingleNode("*[@Name='" + sectionName + "']"); if (section == null) { section = Add(sections, "Section"); Attr((XmlElement)section, "Name", sectionName); }
                var member = Add(section, "Member"); Attr(member, "Name", memberName); Attr(member, "Datatype", Text(memberModel, "datatype"));
                if (memberModel.ContainsKey("start_value")) Add(member, "StartValue").InnerText = Text(memberModel, "start_value");
            }
            GraphEditor.ValidateDocument(doc);
            foreach (XmlElement component in doc.SelectNodes("//*[local-name()='Access' and @Scope='LocalVariable']/*[local-name()='Symbol']/*[local-name()='Component'][1]"))
            {
                string replacement; if (renamed.TryGetValue(component.GetAttribute("Name"), out replacement)) component.SetAttribute("Name", replacement);
            }
            foreach (XmlElement token in graph.SelectNodes(".//*[local-name()='Token' and @Text]"))
            {
                string text = token.GetAttribute("Text"); string prefix = text.StartsWith("#") ? "#" : ""; string symbol = text.Substring(prefix.Length);
                int dot = symbol.IndexOf('.'); string first = dot < 0 ? symbol : symbol.Substring(0, dot); string replacement;
                if (renamed.TryGetValue(first, out replacement)) token.SetAttribute("Text", prefix + replacement + (dot < 0 ? "" : symbol.Substring(dot)));
            }
            ValidateConnections(graph);
            return doc;
        }

        private static void AddMember(XmlNode parent, XmlNode prototype, string name, HashSet<string> names)
        {
            if (string.IsNullOrWhiteSpace(name) || !names.Add(name)) throw new ArgumentException("GRAPH 步、转换名称不能为空或与接口变量重复：" + name);
            var member = (XmlElement)prototype.CloneNode(true); member.SetAttribute("Name", name); parent.AppendChild(member);
        }
        private static XmlElement Element(XmlDocument doc, Dictionary<string, object> m, string name, string ns)
        { return string.IsNullOrWhiteSpace(Text(m, "xml")) ? doc.CreateElement(name, ns) : (XmlElement)Fragment(doc, Text(m, "xml"), ns, name); }
        private static void ReplaceChild(XmlNode parent, string name, XmlNode value)
        {
            XmlNode old = Child(parent, name);
            if (old == null) parent.AppendChild(value); else parent.ReplaceChild(value, old);
        }
        private static XmlNode Fragment(XmlDocument doc, string xml, string ns, string name)
        {
            var fragment = new XmlDocument { XmlResolver = null };
            using (XmlReader reader = XmlReader.Create(new StringReader("<Root xmlns=\"" + ns + "\">" + xml + "</Root>"), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) fragment.Load(reader);
            XmlElement[] children = fragment.DocumentElement.ChildNodes.OfType<XmlElement>().ToArray();
            if (children.Length != 1 || children[0].LocalName != name || children[0].NamespaceURI != ns) throw new ArgumentException("XML 片段需要同命名空间的 " + name + " 元素。");
            return doc.ImportNode(children[0], true);
        }
        private static XmlNode Module(XmlDocument doc, Dictionary<string, object> step, string key, string container, string module, string coil, string ns)
        {
            if (step.ContainsKey(key)) { var c = doc.CreateElement(container, ns); var m = Add(c, module); Attr(m, "ProgrammingLanguage", "LAD"); m.AppendChild(Condition(doc, Val(step, key), coil, ns)); return c; }
            if (!string.IsNullOrWhiteSpace(Text(step, key + "_xml"))) return Fragment(doc, Text(step, key + "_xml"), ns, container);
            var result = doc.CreateElement(container, ns); var child = Add(result, module); Attr(child, "ProgrammingLanguage", "LAD"); child.AppendChild(Condition(doc, true, coil, ns)); return result;
        }
        private static XmlNode Condition(XmlDocument doc, object condition, string coilName, string ns)
        {
            object branches = new object[] { new object[0] };
            if (condition is bool && !(bool)condition)
                return Fragment(doc, "<FlgNet><Parts><Access Scope=\"LiteralConstant\" UId=\"1\"><Constant><ConstantType>Bool</ConstantType><ConstantValue>false</ConstantValue></Constant></Access><Part Name=\"Contact\" UId=\"2\"/><Part Name=\"" + coilName + "\" UId=\"3\"/></Parts><Wires><Wire UId=\"4\"><Powerrail/><NameCon UId=\"2\" Name=\"in\"/></Wire><Wire UId=\"5\"><IdentCon UId=\"1\"/><NameCon UId=\"2\" Name=\"operand\"/></Wire><Wire UId=\"6\"><NameCon UId=\"2\" Name=\"out\"/><NameCon UId=\"3\" Name=\"in\"/></Wire></Wires></FlgNet>", ns, "FlgNet");
            if (!(condition is bool)) branches = Val(Map(condition), "branches") ?? throwMap();
            var args = new Dictionary<string, object> { { "block_name", "GraphCondition" }, { "block_number", 1 },
                { "networks_json", Json().Serialize(new[] { new Dictionary<string, object> { { "coil", "__GraphResult" }, { "branches", branches } } }) } };
            var ladder = new XmlDocument(); ladder.LoadXml(LadBuilder.Generate(args, "V21"));
            XmlNode net = ladder.SelectSingleNode("//*[local-name()='FlgNet']");
            XmlElement coil = (XmlElement)net.SelectSingleNode("*[local-name()='Parts']/*[local-name()='Part' and @Name='Coil']");
            XmlNode access = net.SelectSingleNode("*[local-name()='Parts']/*[local-name()='Access'][*[local-name()='Symbol']/*[@Name='__GraphResult']]");
            string accessId = ((XmlElement)access).GetAttribute("UId");
            foreach (XmlNode wire in net.SelectNodes("*[local-name()='Wires']/*[*[local-name()='IdentCon' and @UId='" + accessId + "']]").Cast<XmlNode>().ToArray()) wire.ParentNode.RemoveChild(wire);
            access.ParentNode.RemoveChild(access); coil.SetAttribute("Name", coilName);
            return CopyNamespace(doc, net, ns);
        }
        private static XmlNode CopyNamespace(XmlDocument doc, XmlNode source, string ns)
        {
            if (!(source is XmlElement)) return doc.ImportNode(source, true);
            var result = doc.CreateElement(source.LocalName, ns);
            foreach (XmlAttribute attr in source.Attributes) if (attr.Prefix != "xmlns" && attr.Name != "xmlns") result.SetAttribute(attr.Name, attr.Value);
            foreach (XmlNode child in source.ChildNodes) result.AppendChild(CopyNamespace(doc, child, ns));
            return result;
        }
        private static void Reference(XmlNode parent, Dictionary<string, object> reference, bool from)
        {
            string kind = Text(reference, "kind");
            if (!new[] { "Step", "Transition", "Branch", "End" }.Contains(kind) || from && kind == "End") throw new ArgumentException("连线节点类型无效。");
            var node = Add(parent, kind == "End" ? "EndConnection" : kind + "Ref");
            if (kind != "End") Attr(node, "Number", Number(reference, "number"));
            if (kind == "Branch") { int port = Convert.ToInt32(Val(reference, "port", 0)); if (port < 0) throw new ArgumentException("分支端口不能为负。"); Attr(node, from ? "Out" : "In", port); }
        }
        internal static void ValidateConnections(XmlElement graph)
        {
            foreach (XmlNode seq in graph.SelectNodes("*[local-name()='Sequence']"))
            {
                var branches = seq.SelectNodes("*[local-name()='Branches']/*").Cast<XmlElement>().ToDictionary(b => b.GetAttribute("Number"));
                var referenced = new HashSet<string>();
                foreach (XmlElement branch in branches.Values)
                {
                    int count;
                    if (!new[] { "AltBegin", "AltEnd", "SimBegin", "SimEnd" }.Contains(branch.GetAttribute("Type"))
                        || !int.TryParse(branch.GetAttribute("Cardinality"), out count) || count < 2)
                        throw new ArgumentException("分支类型或端口数量无效。");
                }
                foreach (XmlElement connection in seq.SelectNodes("*[local-name()='Connections']/*"))
                {
                    foreach (string direction in new[] { "NodeFrom", "NodeTo" })
                    {
                        XmlNode endpoint = Child(connection, direction);
                        XmlElement[] nodes = endpoint == null ? new XmlElement[0] : endpoint.ChildNodes.OfType<XmlElement>().ToArray();
                        if (nodes.Length != 1 || !new[] { "StepRef", "TransitionRef", "BranchRef", "EndConnection" }.Contains(nodes[0].LocalName)
                            || direction == "NodeFrom" && nodes[0].LocalName == "EndConnection") throw new ArgumentException("GRAPH 连线端点无效。");
                        referenced.Add(nodes[0].LocalName + ":" + nodes[0].GetAttribute("Number"));
                    }
                    XmlNode link = Child(connection, "LinkType");
                    if (link == null || link.InnerText != "Direct" && link.InnerText != "Jump"
                        || link.InnerText == "Jump" && Child(connection, "NodeTo").ChildNodes.OfType<XmlElement>().Single().LocalName != "StepRef")
                        throw new ArgumentException("GRAPH 连线类型无效，Jump 必须指向步骤。");
                }
                foreach (XmlElement reference in seq.SelectNodes("*[local-name()='Connections']//*[local-name()='BranchRef']"))
                {
                    XmlElement branch;
                    if (!branches.TryGetValue(reference.GetAttribute("Number"), out branch)) throw new ArgumentException("分支不存在。");
                    int cardinality = int.Parse(branch.GetAttribute("Cardinality")); bool from = reference.ParentNode.LocalName == "NodeFrom";
                    bool wide = from ? branch.GetAttribute("Type").EndsWith("Begin") : branch.GetAttribute("Type").EndsWith("End");
                    int port;
                    if (!int.TryParse(reference.GetAttribute(from ? "Out" : "In"), out port) || port < 0 || port >= (wide ? cardinality : 1)) throw new ArgumentException("分支端口越界。");
                }
                foreach (XmlElement node in seq.SelectNodes("*[local-name()='Steps']/* | *[local-name()='Transitions']/* | *[local-name()='Branches']/*"))
                {
                    string kind = node.LocalName + "Ref", number = node.GetAttribute("Number");
                    if (!referenced.Contains(kind + ":" + number))
                        throw new ArgumentException("GRAPH 节点没有连线：" + node.GetAttribute("Name"));
                }
            }
        }

        private static string Key(XmlNode parent)
        {
            var node = parent.ChildNodes.OfType<XmlElement>().Single();
            return node.LocalName + ":" + node.GetAttribute("Number");
        }
        private sealed class GraphEdge
        {
            internal XmlNode Node; internal string From, To; internal bool Direct;
        }
        private static bool Reachable(string start, string end, Dictionary<string, List<GraphEdge>> outgoing, GraphEdge excluded)
        {
            var pending = new Stack<string>(); var visited = new HashSet<string>(); pending.Push(start);
            while (pending.Count > 0)
            {
                string key = pending.Pop(); if (key == end) return true; if (!visited.Add(key)) continue;
                List<GraphEdge> edges;
                if (outgoing.TryGetValue(key, out edges)) foreach (GraphEdge edge in edges)
                    if (edge != excluded && edge.Direct) pending.Push(edge.To);
            }
            return false;
        }
        internal static void NormalizeLoops(XmlElement graph, HashSet<XmlNode> automatic)
        {
            foreach (XmlNode sequence in graph.SelectNodes("*[local-name()='Sequence']"))
            {
                GraphEdge[] connections = Child(sequence, "Connections").ChildNodes.OfType<XmlElement>()
                    .Select(c => new GraphEdge { Node = c, From = Key(Child(c, "NodeFrom")), To = Key(Child(c, "NodeTo")), Direct = Child(c, "LinkType").InnerText == "Direct" }).ToArray();
                var outgoing = connections.GroupBy(c => c.From).ToDictionary(g => g.Key, g => g.ToList());
                var initial = new HashSet<string>(Child(sequence, "Steps").ChildNodes.OfType<XmlElement>().Where(s => s.GetAttribute("Init") == "true").Select(s => "StepRef:" + s.GetAttribute("Number")));
                foreach (GraphEdge connection in connections.OrderByDescending(c => initial.Contains(c.To)))
                {
                    if (automatic.Contains(connection.Node) && connection.To.StartsWith("StepRef:") && Reachable(connection.To, connection.From, outgoing, connection))
                    { Child(connection.Node, "LinkType").InnerText = "Jump"; connection.Direct = false; }
                }
                var indegree = connections.SelectMany(c => new[] { c.From, c.To }).Distinct().ToDictionary(k => k, k => 0);
                foreach (GraphEdge edge in connections) if (edge.Direct) indegree[edge.To]++;
                var ready = new Queue<string>(indegree.Where(p => p.Value == 0).Select(p => p.Key)); int visited = 0;
                while (ready.Count > 0)
                {
                    string key = ready.Dequeue(); visited++; List<GraphEdge> edges;
                    if (outgoing.TryGetValue(key, out edges)) foreach (GraphEdge edge in edges)
                        if (edge.Direct && --indegree[edge.To] == 0) ready.Enqueue(edge.To);
                }
                if (visited != indegree.Count) throw new ArgumentException("GRAPH 循环回边必须使用 Jump，并指向步骤。");
            }
        }

        public static object ReadModel(string xml)
        {
            return ReadModel(GraphEditor.Parse(xml));
        }
        internal static object ReadModel(XmlDocument doc)
        {
            GraphEditor.ValidateDocument(doc);
            var graph = doc.SelectSingleNode("//*[local-name()='Graph']");
            if (graph == null) throw new ArgumentException("需要 GRAPH XML。");
            return new
            {
                block_name = doc.SelectSingleNode("/Document/SW.Blocks.FB/AttributeList/Name").InnerText,
                block_number = int.Parse(doc.SelectSingleNode("/Document/SW.Blocks.FB/AttributeList/Number").InnerText),
                interface_xml = doc.SelectSingleNode("/Document/SW.Blocks.FB/AttributeList/Interface").OuterXml,
                block_attributes = doc.SelectSingleNode("/Document/SW.Blocks.FB/AttributeList").ChildNodes.OfType<XmlElement>().Where(e => !e.ChildNodes.OfType<XmlElement>().Any() && e.Name != "Interface").ToDictionary(e => e.Name, e => e.InnerText),
                pre_operations_xml = Child(graph, "PreOperations").OuterXml,
                post_operations_xml = Child(graph, "PostOperations").OuterXml,
                alarms_settings_xml = Child(graph, "AlarmsSettings").OuterXml,
                sequences = graph.SelectNodes("*[local-name()='Sequence']").Cast<XmlElement>().Select(seq => new
                {
                    title_xml = Child(seq, "Title") == null ? "" : Child(seq, "Title").OuterXml,
                    language = "zh-CN", comment_xml = Child(seq, "Comment") == null ? "" : Child(seq, "Comment").OuterXml,
                    steps = seq.SelectNodes("*[local-name()='Steps']/*").Cast<XmlElement>().Select(s => new
                    {
                        number = int.Parse(s.GetAttribute("Number")), name = s.GetAttribute("Name"), initial = s.GetAttribute("Init") == "true",
                        xml = s.OuterXml, actions_xml = Child(s, "Actions").OuterXml,
                        supervisions_xml = Child(s, "Supervisions").OuterXml, interlocks_xml = Child(s, "Interlocks").OuterXml
                    }).ToArray(),
                    transitions = seq.SelectNodes("*[local-name()='Transitions']/*").Cast<XmlElement>().Select(t => new
                    {
                        number = int.Parse(t.GetAttribute("Number")), name = t.GetAttribute("Name"), language = t.GetAttribute("ProgrammingLanguage"),
                        xml = t.OuterXml, condition_xml = Child(t, "FlgNet").OuterXml
                    }).ToArray(),
                    branches = seq.SelectNodes("*[local-name()='Branches']/*").Cast<XmlElement>().Select(b => new
                    { number = int.Parse(b.GetAttribute("Number")), type = b.GetAttribute("Type"), cardinality = int.Parse(b.GetAttribute("Cardinality")), xml = b.OuterXml }).ToArray(),
                    connections = seq.SelectNodes("*[local-name()='Connections']/*").Cast<XmlElement>().Select(c => new
                    { from = ReadReference(Child(c, "NodeFrom")), to = ReadReference(Child(c, "NodeTo")), link_type = Child(c, "LinkType").InnerText }).ToArray()
                }).ToArray()
            };
        }
        private static object ReadReference(XmlNode parent)
        {
            var node = parent.ChildNodes.OfType<XmlElement>().Single();
            return new { kind = node.LocalName == "EndConnection" ? "End" : node.LocalName.Replace("Ref", ""),
                number = node.HasAttribute("Number") ? int.Parse(node.GetAttribute("Number")) : 0,
                port = node.HasAttribute("Out") ? int.Parse(node.GetAttribute("Out")) : node.HasAttribute("In") ? int.Parse(node.GetAttribute("In")) : 0 };
        }
    }
}
