using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace TiaPortalSourceBridge.Indexing
{
    internal static partial class ContentIndexer
    {
        internal const string ParserVersion = "structure-v2.1";
        internal static string NormalizeSymbol(string symbol)
        { return Regex.Replace(symbol, @"""([\p{L}_][\p{L}\p{N}_]*)""", "$1"); }
        internal static string ComponentName(string name)
        { return Regex.IsMatch(name, @"^[\p{L}_][\p{L}\p{N}_]*$") ? name : "\"" + name.Replace("\"", "\"\"") + "\""; }
        private static string Address(XmlNode n)
        {
            int bits; if (!int.TryParse(Attr(n, "BitOffset"), out bits)) return "address(" + n.OuterXml + ")";
            string area = Attr(n, "Area"), type = Attr(n, "Type"), size = type == "Bit" ? "X" : type == "Byte" ? "B" : type == "Word" ? "W" : type == "DWord" ? "D" : "";
            if (size.Length == 0) return "address(" + n.OuterXml + ")";
            return (area == "DB" ? "DB" + Attr(n, "BlockNumber") + ".DB" + size : "%" + area + (size == "X" ? "" : size))
                + (bits / 8) + (size == "X" ? "." + (bits % 8) : "");
        }
        internal static string Operand(XmlNode access)
        {
            var symbol = access.SelectSingleNode("*[local-name()='Symbol']");
            if (symbol != null) return (Attr(access, "Scope") == "LocalVariable" ? "#" : "") + Symbol(symbol);
            var address = access.SelectSingleNode("*[local-name()='Address']");
            if (address != null) return Address(address);
            var constant = access.SelectSingleNode(".//*[local-name()='ConstantValue']");
            return constant == null ? "" : constant.InnerText;
        }
        private static string SourceText(XmlNode node)
        {
            if (node.NodeType == XmlNodeType.Text || node.NodeType == XmlNodeType.CDATA) return node.Value;
            if (!(node is XmlElement)) return "";
            if (node.LocalName == "Token" || node.LocalName == "StlToken") return Attr(node, "Text");
            if (node.LocalName == "NewLine") return "\n";
            if (node.LocalName == "Blank") return " ";
            if (node.LocalName == "Access" && Operand(node).Length > 0) return Operand(node);
            if (node.LocalName == "LineComment") return "// " + node.InnerText;
            if (node.LocalName == "Text" || node.LocalName == "SourceText") return node.InnerText;
            string separator = node.LocalName == "StlStatement" ? " " : "";
            string result = string.Join(separator, node.ChildNodes.Cast<XmlNode>().Where(n => n is XmlElement || !string.IsNullOrWhiteSpace(n.Value)).Select(SourceText));
            if (node.LocalName == "Instruction" || node.LocalName == "Parameter") result = Attr(node, "Name") + result;
            return result + (node.LocalName == "StlStatement" ? "\n" : "");
        }
        // Plain source readbacks are captured without a second Openness export.
        internal static string WrapSource(string name, string version, string source)
        {
            var doc = new XmlDocument(); doc.LoadXml("<Document><Engineering/><SW.Blocks.Source><AttributeList><Name/></AttributeList><SourceText/></SW.Blocks.Source></Document>");
            ((XmlElement)doc.SelectSingleNode("/Document/Engineering")).SetAttribute("version", version);
            doc.SelectSingleNode("//Name").InnerText = name; doc.SelectSingleNode("//SourceText").InnerText = source;
            return doc.OuterXml;
        }
        private static void Semantics(IndexContent c, XmlDocument doc)
        {
            int network = 0;
            foreach (XmlNode unit in doc.SelectNodes("//SW.Blocks.CompileUnit"))
            {
                network++; int order = 0;
                foreach (XmlNode call in unit.SelectNodes(".//*[local-name()='CallInfo']"))
                    Fact(c, "call_order", call, "network=" + network + " source_order=" + (++order) + " calls " + Attr(call, "Name") + "; conditional execution depends on network logic");
            }
            foreach (XmlNode net in doc.SelectNodes("//*[local-name()='FlgNet']"))
            {
                var parts = net.SelectNodes("*[local-name()='Parts']/*[@UId]").Cast<XmlElement>().ToDictionary(n => Attr(n, "UId"));
                var wires = net.SelectNodes("*[local-name()='Wires']/*").Cast<XmlNode>().ToArray();
                foreach (XmlNode wire in wires)
                    foreach (XmlNode ident in wire.SelectNodes("*[local-name()='IdentCon']"))
                    {
                        XmlElement access; if (!parts.TryGetValue(Attr(ident, "UId"), out access) || access.LocalName != "Access") continue;
                        string target = Operand(access); if (target.Length == 0) continue;
                        foreach (XmlNode con in wire.SelectNodes("*[local-name()='NameCon']"))
                        {
                            XmlElement part; if (!parts.TryGetValue(Attr(con, "UId"), out part)) continue;
                            string pin = Attr(con, "Name"), op = Attr(part, "Name"), direction = "";
                            if (pin == "operand")
                            {
                                if (new[] { "Coil", "SCoil", "RCoil" }.Contains(op)) direction = "writes";
                                else if (op == "Contact") direction = "reads";
                            }
                            var parameter = part.SelectNodes("*[local-name()='CallInfo']/*[local-name()='Parameter']").Cast<XmlNode>().FirstOrDefault(p => Attr(p, "Name") == pin);
                            if (parameter != null)
                            {
                                string section = Attr(parameter, "Section");
                                direction = section == "Input" ? "reads" : section == "Output" || section == "Return" ? "writes" : section == "InOut" ? "reads_writes" : "";
                            }
                            if (direction.Length > 0) Ref(c, direction, target, wire, "native pin " + op + "." + pin + (parameter == null ? "" : " section=" + Attr(parameter, "Section")));
                        }
                    }
                var logic = parts.Values.Where(p => p.LocalName != "Access").ToArray();
                var contact = logic.FirstOrDefault(p => Attr(p, "Name") == "Contact"); var coil = logic.FirstOrDefault(p => Attr(p, "Name") == "Coil");
                if (logic.Length == 2 && contact != null && coil != null && !contact.HasChildNodes && !coil.HasChildNodes)
                {
                    Func<XmlNode, string, string, bool> pin = (w, uid, name) => w.SelectNodes("*[local-name()='NameCon']").Cast<XmlNode>().Any(n => Attr(n, "UId") == uid && Attr(n, "Name") == name);
                    string ci = Attr(contact, "UId"), co = Attr(coil, "UId");
                    Func<string, string> operand = uid => {
                        var w = wires.FirstOrDefault(n => pin(n, uid, "operand")); if (w == null) return "";
                        var ident = w.SelectSingleNode("*[local-name()='IdentCon']"); XmlElement access;
                        return ident != null && parts.TryGetValue(Attr(ident, "UId"), out access) ? Operand(access) : "";
                    };
                    if (wires.Any(w => w.SelectSingleNode("*[local-name()='Powerrail']") != null && pin(w, ci, "in")) && wires.Any(w => pin(w, ci, "out") && pin(w, co, "in")))
                    {
                        string from = operand(ci), to = operand(co);
                        if (from.Length > 0 && to.Length > 0) {
                            Fact(c, "direct_mapping", net, from + " -> " + to + " (unnegated contact to coil)");
                            c.mappings.Add(new IndexMapping { source = from, target = to, location = Location(net) });
                        }
                    }
                }
            }
            foreach (XmlNode statement in doc.SelectNodes("//*[local-name()='StlStatement']"))
            {
                var token = statement.SelectSingleNode("*[local-name()='StlToken']"); string op = Attr(token, "Text");
                string relation = new[] { "L", "A", "AN", "O", "ON", "U", "UN", "X", "XN" }.Contains(op) ? "reads" : new[] { "T", "=", "S", "R" }.Contains(op) ? "writes" : "";
                if (relation.Length > 0) foreach (XmlNode access in statement.SelectNodes("*[local-name()='Access']"))
                    if (Attr(access, "Scope") != "LiteralConstant") Ref(c, relation, Operand(access), statement, "STL opcode=" + op);
            }
            foreach (XmlNode access in doc.SelectNodes("//*[local-name()='Access'][*[local-name()='Address']]")) Ref(c, "address", Operand(access), access, "native absolute address");
        }
    }
}
