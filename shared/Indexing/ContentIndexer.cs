using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;

namespace TiaPortalSourceBridge.Indexing
{
    // Extract evidence, not guessed machine behavior. No project-specific names or addresses.
    internal static class ContentIndexer
    {
        private static readonly Regex TokenRefRegex = new Regex(
            "(?:\"[^\"]+\"|#?[\\p{L}_][\\p{L}\\p{N}_]*)(?:\\.(?:\"[^\"]+\"|[\\p{L}_][\\p{L}\\p{N}_]*)(?:\\[[^\\]\\r\\n]+\\])?)+",
            RegexOptions.Compiled);
        private static readonly Regex IndirectParamRegex = new Regex(
            @"\[([\p{L}_][\p{L}\p{N}_]*)\]",
            RegexOptions.Compiled);

        private static string Attr(XmlNode node, string name) { var e = node as XmlElement; return e == null ? "" : e.GetAttribute(name); }
        private static string Child(XmlNode node, string name) { var n = node == null ? null : node.SelectSingleNode("*[local-name()='" + name + "']"); return n == null ? "" : n.InnerText; }
        private static string Location(XmlNode node)
        {
            var parts = new List<string>();
            for (XmlNode n = node; n is XmlElement && parts.Count < 8; n = n.ParentNode)
            {
                string name = Attr(n, "Name"); if (name.Length == 0) name = Attr(n, "Number"); if (name.Length == 0) name = Attr(n, "ID");
                parts.Add(n.LocalName + (name.Length == 0 ? "" : "[" + name + "]"));
            }
            parts.Reverse(); return string.Join("/", parts);
        }
        private static void Fact(IndexContent c, string kind, XmlNode n, string text)
        { if (!string.IsNullOrWhiteSpace(text)) c.facts.Add(new IndexFact { kind = kind, location = Location(n), text = text }); }
        private static void Ref(IndexContent c, string relation, string target, XmlNode n, string evidence)
        { if (!string.IsNullOrWhiteSpace(target)) c.references.Add(new IndexReference { relation = relation, target = target, location = Location(n), evidence = evidence }); }
        private static string Symbol(XmlNode node)
        {
            return string.Join(".", node.SelectNodes("*[local-name()='Component']").Cast<XmlElement>().Select(e => {
                string indices = string.Join(",", e.SelectNodes(".//*[local-name()='ConstantValue']").Cast<XmlNode>().Select(n => n.InnerText));
                return e.GetAttribute("Name") + (indices.Length == 0 ? "" : "[" + indices + "]");
            }));
        }
        internal static IndexContent Parse(string xml)
        {
            var doc = new XmlDocument { XmlResolver = null, PreserveWhitespace = true };
            using (var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32 * 1024 * 1024 })) doc.Load(reader);
            var objectRoot = doc.SelectSingleNode("/Document/*[starts-with(local-name(),'SW.') or starts-with(local-name(),'Hmi.')]");
            var c = new IndexContent { hash = IndexJson.Hash(objectRoot == null ? doc.OuterXml : objectRoot.OuterXml) };
            c.object_name = objectRoot == null ? "" : Child(objectRoot.SelectSingleNode("AttributeList"), "Name");
            var engineering = doc.SelectSingleNode("/Document/Engineering/@version"); c.source_version = engineering == null ? "" : engineering.Value;
            var ids = doc.SelectNodes("//*[@ID]").Cast<XmlElement>().GroupBy(e => e.GetAttribute("ID")).ToDictionary(g => "#" + g.Key, g => g.First());
            Func<XmlNode, string> value = p => {
                var literal = p.SelectSingleNode("AttributeList/Value"); if (literal != null) return literal.InnerText;
                var link = p.SelectSingleNode("LinkList/Value"); if (link == null) return "";
                string name = Child(link, "Name"); XmlElement target;
                return name.Length > 0 ? name : ids.TryGetValue(Attr(link, "TargetID"), out target) ? Child(target.SelectSingleNode("AttributeList"), "Name") : Attr(link, "TargetID");
            };
            foreach (XmlElement m in doc.SelectNodes("/Document/*[starts-with(local-name(),'SW.')]/AttributeList/Interface//*[local-name()='Member']"))
            {
                string path = string.Join(".", m.SelectNodes("ancestor-or-self::*[local-name()='Member']").Cast<XmlElement>().Select(e => e.GetAttribute("Name")));
                string type = m.GetAttribute("Datatype"), comment = string.Join("; ", m.SelectNodes("*[local-name()='Comment']/*").Cast<XmlNode>().Select(n => n.InnerText));
                Fact(c, "member", m, path + " : " + type + " = " + Child(m, "StartValue") + (comment.Length > 0 ? " // " + comment : ""));
                var fields = m.SelectNodes("*[local-name()='Sections']/*[local-name()='Section']/*[local-name()='Member']").Cast<XmlElement>().ToArray();
                if (fields.Length > 0) Fact(c, "member_structure", m, path + " : " + type + " {" + string.Join(", ", fields.Select(f => f.GetAttribute("Name") + ":" + f.GetAttribute("Datatype"))) + "}");
                if (type.Contains("\"")) Ref(c, "uses_type", type.Trim('"'), m, "declared datatype");
            }
            foreach (XmlNode unit in doc.SelectNodes("//SW.Blocks.CompileUnit"))
            {
                var title = unit.SelectSingleNode("ObjectList/MultilingualText[@CompositionName='Title']//Text");
                Fact(c, "network", unit, title == null ? "network " + Attr(unit, "ID") : title.InnerText);
            }
            foreach (XmlElement call in doc.SelectNodes("//*[local-name()='CallInfo']"))
                Ref(c, "calls", call.GetAttribute("Name"), call, call.GetAttribute("BlockType"));
            foreach (XmlNode tag in doc.SelectNodes("//SW.Tags.PlcTag | //SW.Tags.PlcUserConstant"))
            {
                var a = tag.SelectSingleNode("AttributeList");
                Fact(c, "plc_tag", tag, Child(a, "Name") + " : " + Child(a, "DataTypeName") + " address=" + Child(a, "LogicalAddress") + " value=" + Child(a, "Value"));
                c.definitions.Add(Child(a, "Name"));
                Ref(c, "address", Child(a, "LogicalAddress"), tag, "declared PLC tag address");
            }
            foreach (XmlNode access in doc.SelectNodes("//*[local-name()='Access']/*[local-name()='Symbol']"))
                Ref(c, "symbol_reference", Symbol(access), access, "scope=" + Attr(access.ParentNode, "Scope") + "; read/write direction not inferred");
            foreach (XmlElement token in doc.SelectNodes("//*[local-name()='Token' and @Text]"))
            {
                string text = token.GetAttribute("Text");
                foreach (Match m in TokenRefRegex.Matches(text))
                    Ref(c, "token_reference", m.Value.Replace("\"", ""), token, "literal source token");
            }
            foreach (XmlNode sequence in doc.SelectNodes("//*[local-name()='Graph']/*[local-name()='Sequence']"))
            {
                foreach (XmlElement s in sequence.SelectNodes("*[local-name()='Steps']/*"))
                {
                    Fact(c, "graph_step", s, "S" + s.GetAttribute("Number") + " " + s.GetAttribute("Name") + " initial=" + s.GetAttribute("Init") + " max=" + s.GetAttribute("MaximumStepTime") + " warning=" + s.GetAttribute("WarningTime"));
                    foreach (XmlNode a in s.SelectNodes("*[local-name()='Actions']/*[local-name()='Action']"))
                        Fact(c, "graph_action", a, Attr(a, "Qualifier") + " " + string.Join(" ", a.SelectNodes("*[local-name()='Token']").Cast<XmlElement>().Select(t => t.GetAttribute("Text"))));
                }
                foreach (XmlElement t in sequence.SelectNodes("*[local-name()='Transitions']/*"))
                    Fact(c, "graph_transition", t, "T" + t.GetAttribute("Number") + " " + t.GetAttribute("Name") + " " + t.GetAttribute("ProgrammingLanguage") + " " + string.Join(";", t.SelectNodes(".//*[local-name()='Symbol']").Cast<XmlNode>().Select(Symbol)));
                foreach (XmlElement b in sequence.SelectNodes("*[local-name()='Branches']/*")) Fact(c, "graph_branch", b, b.OuterXml);
                foreach (XmlNode connection in sequence.SelectNodes("*[local-name()='Connections']/*"))
                {
                    Func<string, string> endpoint = direction => {
                        var n = connection.SelectSingleNode("*[local-name()='" + direction + "']/*");
                        return n == null ? "?" : n.LocalName + Attr(n, "Number") + (Attr(n, "In") + Attr(n, "Out")).Insert(0, ":");
                    };
                    Fact(c, "graph_connection", connection, endpoint("NodeFrom") + " -> " + endpoint("NodeTo") + " " + Child(connection, "LinkType"));
                }
            }
            foreach (XmlNode root in doc.SelectNodes("/Document/*[starts-with(local-name(),'Hmi.')]") )
            {
                foreach (XmlNode item in root.SelectNodes("descendant-or-self::*[starts-with(local-name(),'Hmi.Screen.') and AttributeList/ObjectName]"))
                {
                    var a = item.SelectSingleNode("AttributeList");
                    Fact(c, "hmi_control", item, item.LocalName + " " + Child(a, "ObjectName") + " left=" + Child(a, "Left") + " top=" + Child(a, "Top"));
                }
                foreach (XmlNode link in root.SelectNodes(".//*[local-name()='LinkList']/*[*[local-name()='Name']]"))
                    Ref(c, "hmi_" + link.LocalName.ToLowerInvariant(), Child(link, "Name"), link, "explicit link");
                foreach (XmlNode tag in root.SelectNodes("descendant-or-self::Hmi.Tag.Tag"))
                {
                    var a = tag.SelectSingleNode("AttributeList");
                    Fact(c, "hmi_tag", tag, Child(a, "Name") + " address=" + Child(a, "LogicalAddress") + " start=" + Child(a, "StartValue") + " type=" + Child(tag.SelectSingleNode("LinkList/DataType") ?? tag, "Name"));
                    c.definitions.Add(Child(a, "Name"));
                    Ref(c, "address", Child(a, "LogicalAddress"), tag, "declared HMI address; indirect expressions preserved");
                    foreach (Match match in IndirectParamRegex.Matches(Child(a, "LogicalAddress")))
                        Ref(c, "hmi_indirect_parameter", match.Groups[1].Value, tag, "explicit indirect-address parameter; result not evaluated");
                }
                foreach (XmlNode fn in root.SelectNodes(".//Hmi.Event.FunctionListEntry"))
                {
                    string parameters = string.Join("; ", fn.SelectNodes("ObjectList/Hmi.Event.FunctionListEntryParameter").Cast<XmlNode>().Select(p => Child(p.SelectSingleNode("AttributeList"), "Name") + "=" + value(p)));
                    string function = Child(fn.SelectSingleNode("AttributeList"), "Name");
                    Fact(c, "hmi_event", fn, function + "(" + parameters + ")");
                    if (function == "LinearScaling") Fact(c, "hmi_mapping", fn, "mapping: " + function + "(" + parameters + ")");
                }
                foreach (XmlNode entry in root.SelectNodes(".//Hmi.TextGraphicList.TextListEntry"))
                    foreach (XmlNode text in entry.SelectNodes(".//MultilingualTextItem"))
                    {
                        var a = entry.SelectSingleNode("AttributeList"); var ta = text.SelectSingleNode("AttributeList");
                        Fact(c, "hmi_text_entry", entry, "index=" + Child(a, "From") + ".." + Child(a, "To") + " locale=" + Child(ta, "Culture") + " " + Child(ta, "Text"));
                    }
            }
            // Retain source snippets from SCL/STL XML without claiming a full language interpreter.
            foreach (XmlNode source in doc.SelectNodes("//*[local-name()='StructuredText' or local-name()='SourceText']"))
            {
                string text = source.InnerText;
                if (string.IsNullOrWhiteSpace(text)) text = string.Join(" ", source.SelectNodes(".//*[local-name()='Token' and @Text]").Cast<XmlElement>().Select(e => e.GetAttribute("Text")));
                Fact(c, "source_excerpt", source, text.Length > 12000 ? text.Substring(0, 12000) : text);
                c.warnings.Add("SCL/STL source excerpts and native Access/CallInfo nodes are indexed; no complete control-flow or read/write analysis is claimed.");
            }
            if (c.facts.Count == 0 && c.references.Count == 0) c.warnings.Add("No supported content structures found; use the live source reader for this object.");
            c.references = c.references.GroupBy(r => r.relation + "\n" + r.target + "\n" + r.location).Select(g => g.First()).ToList();
            c.warnings.Add("Structural index snapshot; live-read target before writing. Symbol direction and dynamic-address results are not inferred.");
            c.search_text = string.Join("\n", c.facts.Select(f => f.text).Concat(c.references.Select(r => r.target)));
            return c;
        }
    }
}
