using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace TiaPortalSourceBridge.Mcp
{
    internal static class LadBuilder
    {
        private static readonly Regex ArrayComponentRegex = new Regex(
            @"^([^\[\]]+)\[([^\[\]]+)\]$",
            RegexOptions.Compiled);

        private static string E(string value) { return SecurityElement.Escape(value ?? ""); }
        private static string Text(Dictionary<string, object> data, string key)
        {
            object value;
            return data != null && data.TryGetValue(key, out value) && value != null ? Convert.ToString(value) : "";
        }

        public static string Generate(Dictionary<string, object> args, string portalVersion)
        {
            string name = Text(args, "block_name").Trim();
            int number = Convert.ToInt32(args["block_number"]);
            if (string.IsNullOrWhiteSpace(name) || number < 1 || number > 65535)
                throw new ArgumentException("程序块名称或块号无效（块号需在 1～65535 之间）。");

            string blockType = Text(args, "block_type").Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(blockType)) blockType = "FC";
            if (blockType != "FC" && blockType != "FB")
                throw new ArgumentException("block_type 仅支持 FC 或 FB。");
            bool isFb = blockType == "FB";

            JavaScriptSerializer serializer = new JavaScriptSerializer();
            object[] networks = serializer.DeserializeObject(Text(args, "networks_json")) as object[];
            if (networks == null || networks.Length == 0)
                throw new ArgumentException("至少需要一个网络（networks_json 不能为空）。");

            HashSet<string> localVariables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string interfaceSectionsXml = BuildInterfaceSections(Text(args, "interface_json"), serializer, localVariables, isFb);

            StringBuilder xml = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?><Document><Engineering version=\""
                + E(portalVersion) + "\"/><SW.Blocks." + blockType + " ID=\"0\"><AttributeList><AutoNumber>false</AutoNumber><Interface><Sections xmlns=\"http://www.siemens.com/automation/Openness/SW/Interface/v5\">");
            xml.Append(interfaceSectionsXml);
            xml.Append("</Sections></Interface><MemoryLayout>Optimized</MemoryLayout><Name>")
                .Append(E(name)).Append("</Name>");
            if (ParseMajorVersion(portalVersion) >= 21)
                xml.Append("<Namespace/>");
            xml.Append("<Number>").Append(number)
                .Append("</Number><ProgrammingLanguage>LAD</ProgrammingLanguage><SetENOAutomatically>false</SetENOAutomatically></AttributeList><ObjectList>");

            int documentId = 1;
            foreach (object item in networks)
            {
                Dictionary<string, object> network = item as Dictionary<string, object>;
                if (network == null) throw new ArgumentException("网络必须是 JSON 对象。");
                object rawBranches;
                if (!network.TryGetValue("branches", out rawBranches)) throw new ArgumentException("网络缺少 branches 字段。");
                object[] branches = rawBranches as object[];
                if (branches == null || branches.Length == 0) throw new ArgumentException("branches 至少包含一个支路。");

                string coilSymbol = Text(network, "coil");
                string coilType = Text(network, "coil_type").Trim().ToLowerInvariant();
                bool negateCoil;
                string partName = ResolveCoilPartName(coilType, out negateCoil);

                StringBuilder parts = new StringBuilder();
                StringBuilder wires = new StringBuilder();
                int uid = 21;
                int coilAccess = uid++;
                parts.Append(Access(coilAccess, coilSymbol, localVariables));
                int coil = uid++;
                parts.Append("<Part Name=\"").Append(partName).Append("\" UId=\"").Append(coil).Append("\">");
                if (negateCoil) parts.Append("<Negated Name=\"operand\"/>");
                parts.Append("</Part>");

                List<int?> ends = new List<int?>();
                List<int> starts = new List<int>();
                foreach (object branchItem in branches)
                {
                    object[] contacts = branchItem as object[];
                    if (contacts == null) throw new ArgumentException("每个支路必须是触点数组。");
                    if (contacts.Length == 0 && branches.Length > 1)
                        throw new ArgumentException("多条并联支路中不能包含空支路；仅单支路支持无条件导通 [[]]。");

                    int? previous = null;
                    foreach (object contactItem in contacts)
                    {
                        Dictionary<string, object> contact = contactItem as Dictionary<string, object>;
                        if (contact == null) throw new ArgumentException("触点必须是对象。");

                        string compareOp = Text(contact, "compare").Trim();
                        if (!string.IsNullOrEmpty(compareOp))
                        {
                            string cmpPartName = ResolveComparePartName(compareOp);
                            string srcType = Text(contact, "datatype").Trim();
                            if (string.IsNullOrEmpty(srcType)) srcType = "Int";
                            int leftAccess = uid++;
                            int rightAccess = uid++;
                            int cmpPart = uid++;
                            parts.Append(AccessOrConstant(leftAccess, Text(contact, "left"), srcType, localVariables));
                            parts.Append(AccessOrConstant(rightAccess, Text(contact, "right"), srcType, localVariables));
                            parts.Append("<Part Name=\"").Append(cmpPartName).Append("\" UId=\"").Append(cmpPart)
                                .Append("\"><TemplateValue Name=\"SrcType\" Type=\"Type\">").Append(E(srcType)).Append("</TemplateValue></Part>");
                            Wire(wires, ref uid, "<IdentCon UId=\"" + leftAccess + "\"/>" + Pin(cmpPart, "in1"));
                            Wire(wires, ref uid, "<IdentCon UId=\"" + rightAccess + "\"/>" + Pin(cmpPart, "in2"));
                            if (previous.HasValue) Wire(wires, ref uid, Pin(previous.Value, "out") + Pin(cmpPart, "pre"));
                            else starts.Add(cmpPart);
                            previous = cmpPart;
                            continue;
                        }

                        int access = uid++;
                        parts.Append(Access(access, Text(contact, "symbol"), localVariables));

                        string edge = Text(contact, "edge").Trim().ToLowerInvariant();
                        if (edge == "p" || edge == "pos" || edge == "rising" || edge == "n" || edge == "neg" || edge == "falling")
                        {
                            string edgeMemory = Text(contact, "edge_memory").Trim();
                            if (string.IsNullOrWhiteSpace(edgeMemory))
                                throw new ArgumentException("边沿触点 (edge=" + edge + ") 必须指定 edge_memory 边沿存储位变量。");
                            int bitAccess = uid++;
                            parts.Append(Access(bitAccess, edgeMemory, localVariables));
                            int edgePart = uid++;
                            string edgePartName = (edge.StartsWith("p") || edge == "rising") ? "PContact" : "NContact";
                            parts.Append("<Part Name=\"").Append(edgePartName).Append("\" UId=\"").Append(edgePart).Append("\"/>");
                            Wire(wires, ref uid, "<IdentCon UId=\"" + bitAccess + "\"/>" + Pin(edgePart, "bit"));
                            Wire(wires, ref uid, "<IdentCon UId=\"" + access + "\"/>" + Pin(edgePart, "operand"));
                            if (previous.HasValue) Wire(wires, ref uid, Pin(previous.Value, "out") + Pin(edgePart, "in"));
                            else starts.Add(edgePart);
                            previous = edgePart;
                            continue;
                        }

                        int part = uid++;
                        parts.Append("<Part Name=\"Contact\" UId=\"").Append(part).Append("\">");
                        object negated;
                        if (contact.TryGetValue("negated", out negated) && negated != null && Convert.ToBoolean(negated))
                            parts.Append("<Negated Name=\"operand\"/>");
                        parts.Append("</Part>");
                        Wire(wires, ref uid, "<IdentCon UId=\"" + access + "\"/>" + Pin(part, "operand"));
                        if (previous.HasValue) Wire(wires, ref uid, Pin(previous.Value, "out") + Pin(part, "in"));
                        else starts.Add(part);
                        previous = part;
                    }
                    ends.Add(previous);
                }

                Wire(wires, ref uid, "<IdentCon UId=\"" + coilAccess + "\"/>" + Pin(coil, "operand"));
                if (branches.Length == 1)
                {
                    if (ends[0].HasValue) Wire(wires, ref uid, Pin(ends[0].Value, "out") + Pin(coil, "in"));
                    else starts.Add(coil);
                }
                else
                {
                    int orPart = uid++;
                    parts.Append("<Part Name=\"O\" UId=\"").Append(orPart).Append("\"><TemplateValue Name=\"Card\" Type=\"Cardinality\">")
                        .Append(branches.Length).Append("</TemplateValue></Part>");
                    for (int i = 0; i < ends.Count; i++)
                        Wire(wires, ref uid, Pin(ends[i].Value, "out") + Pin(orPart, "in" + (i + 1)));
                    Wire(wires, ref uid, Pin(orPart, "out") + Pin(coil, "in"));
                }

                if (starts.Count > 0)
                {
                    StringBuilder rail = new StringBuilder("<Powerrail/>");
                    foreach (int start in starts)
                    {
                        // Compare block uses 'pre' instead of 'in' when connected to powerrail if needed, or standard 'in' for Contact/Coil
                        rail.Append(Pin(start, "in"));
                    }
                    Wire(wires, ref uid, rail.ToString());
                }

                xml.Append("<SW.Blocks.CompileUnit ID=\"").Append(documentId++).Append("\" CompositionName=\"CompileUnits\"><AttributeList><NetworkSource><FlgNet xmlns=\"http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v4\"><Parts>")
                    .Append(parts).Append("</Parts><Wires>").Append(wires).Append("</Wires></FlgNet></NetworkSource><ProgrammingLanguage>LAD</ProgrammingLanguage></AttributeList><ObjectList>");
                xml.Append(Multilingual("Title", Text(network, "title"), ref documentId))
                    .Append(Multilingual("Comment", Text(network, "comment"), ref documentId));
                xml.Append("</ObjectList></SW.Blocks.CompileUnit>");
            }

            xml.Append(Multilingual("Title", name, ref documentId))
                .Append(Multilingual("Comment", "Generated by TIA Source Bridge", ref documentId));
            xml.Append("</ObjectList></SW.Blocks.").Append(blockType).Append("></Document>");
            BlockOperations.ParseBlock(xml.ToString());
            return xml.ToString();
        }

        private static string ResolveCoilPartName(string coilType, out bool negateCoil)
        {
            negateCoil = false;
            if (coilType == "set" || coilType == "s" || coilType == "scoil") return "SCoil";
            if (coilType == "reset" || coilType == "r" || coilType == "rcoil") return "RCoil";
            if (coilType == "negated" || coilType == "not") { negateCoil = true; return "Coil"; }
            if (string.IsNullOrEmpty(coilType) || coilType == "coil" || coilType == "normal") return "Coil";
            throw new ArgumentException("不支持的 coil_type: " + coilType + "（支持 coil / set / reset / negated）。");
        }

        private static string ResolveComparePartName(string op)
        {
            switch (op.ToLowerInvariant())
            {
                case "==":
                case "=":
                case "eq": return "Eq";
                case "<>":
                case "!=":
                case "ne": return "Ne";
                case ">":
                case "gt": return "Gt";
                case "<":
                case "lt": return "Lt";
                case ">=":
                case "ge": return "Ge";
                case "<=":
                case "le": return "Le";
                default:
                    throw new ArgumentException("不支持的比较运算符: " + op + "（支持 ==, <>, >, <, >=, <=）。");
            }
        }

        private static string BuildInterfaceSections(string interfaceJson, JavaScriptSerializer serializer, HashSet<string> localVariables, bool isFb)
        {
            Dictionary<string, object> map = null;
            if (!string.IsNullOrWhiteSpace(interfaceJson))
            {
                map = serializer.DeserializeObject(interfaceJson) as Dictionary<string, object>;
                if (map == null) throw new ArgumentException("interface_json 必须是 JSON 对象。");
            }

            string[] sections = isFb
                ? new[] { "Input", "Output", "InOut", "Static", "Temp", "Constant" }
                : new[] { "Input", "Output", "InOut", "Temp", "Constant" };

            StringBuilder sb = new StringBuilder();
            foreach (string section in sections)
            {
                object rawList = FindCaseInsensitive(map, section);
                object[] members = rawList as object[];
                if (members == null || members.Length == 0)
                {
                    sb.Append("<Section Name=\"").Append(section).Append("\"/>");
                    continue;
                }

                sb.Append("<Section Name=\"").Append(section).Append("\">");
                foreach (object item in members)
                {
                    string memberName;
                    string memberType = "Bool";
                    Dictionary<string, object> memberObj = item as Dictionary<string, object>;
                    if (memberObj != null)
                    {
                        memberName = Text(memberObj, "name").Trim().TrimStart('#');
                        string customType = Text(memberObj, "type").Trim();
                        if (!string.IsNullOrEmpty(customType)) memberType = customType;
                    }
                    else
                    {
                        memberName = Convert.ToString(item ?? "").Trim().TrimStart('#');
                    }

                    if (string.IsNullOrWhiteSpace(memberName))
                        throw new ArgumentException("接口变量名称不能为空。");
                    localVariables.Add(memberName);
                    sb.Append("<Member Name=\"").Append(E(memberName))
                        .Append("\" Datatype=\"").Append(E(memberType))
                        .Append("\" Accessibility=\"Public\"/>");
                }
                sb.Append("</Section>");
            }

            if (!isFb)
            {
                sb.Append("<Section Name=\"Return\"><Member Name=\"Ret_Val\" Datatype=\"Void\" Accessibility=\"Public\"/></Section>");
            }
            return sb.ToString();
        }

        private static object FindCaseInsensitive(Dictionary<string, object> map, string key)
        {
            if (map == null) return null;
            foreach (KeyValuePair<string, object> pair in map)
            {
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(pair.Key, key + "s", StringComparison.OrdinalIgnoreCase))
                {
                    return pair.Value;
                }
            }
            return null;
        }

        private static string AccessOrConstant(int uid, string value, string constantType, HashSet<string> localVariables)
        {
            string trimmed = (value ?? "").Trim();
            double numeric;
            if (double.TryParse(trimmed, NumberStyles.Any, CultureInfo.InvariantCulture, out numeric)
                || trimmed.StartsWith("T#", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("16#", StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, "TRUE", StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, "FALSE", StringComparison.OrdinalIgnoreCase))
            {
                return "<Access Scope=\"LiteralConstant\" UId=\"" + uid + "\"><Constant><ConstantType>"
                    + E(constantType) + "</ConstantType><ConstantValue>" + E(trimmed) + "</ConstantValue></Constant></Access>";
            }
            return Access(uid, trimmed, localVariables);
        }

        private static string Access(int uid, string symbol, HashSet<string> localVariables)
        {
            string trimmed = (symbol ?? "").Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("%"))
                throw new ArgumentException("生成器仅接受符号名称（如 Start、DB_Control.Start、Station[1].Sensor 或局部变量 #Start），不支持 % 绝对地址。");

            bool isExplicitLocal = trimmed.StartsWith("#");
            string cleanSymbol = isExplicitLocal ? trimmed.Substring(1).Trim() : trimmed;
            if (string.IsNullOrWhiteSpace(cleanSymbol))
                throw new ArgumentException("变量符号无效。");

            string[] parts = cleanSymbol.Split('.');
            string firstRaw = parts[0].Trim().Trim('"');
            Match firstArrayMatch = ArrayComponentRegex.Match(firstRaw);
            string firstComponent = firstArrayMatch.Success ? firstArrayMatch.Groups[1].Value.Trim().Trim('"') : firstRaw;
            bool isLocal = isExplicitLocal || (localVariables != null && localVariables.Contains(firstComponent));

            StringBuilder xml = new StringBuilder("<Access Scope=\"")
                .Append(isLocal ? "LocalVariable" : "GlobalVariable")
                .Append("\" UId=\"").Append(uid).Append("\"><Symbol>");
            foreach (string raw in parts)
            {
                string partText = raw.Trim();
                Match arrayMatch = ArrayComponentRegex.Match(partText);
                if (arrayMatch.Success)
                {
                    string compName = arrayMatch.Groups[1].Value.Trim().Trim('"');
                    string indicesText = arrayMatch.Groups[2].Value.Trim();
                    if (compName.Length == 0 || indicesText.Length == 0)
                        throw new ArgumentException("数组下标符号无效: " + symbol);
                    xml.Append("<Component Name=\"").Append(E(compName)).Append("\">");
                    foreach (string idxRaw in indicesText.Split(','))
                    {
                        string idx = idxRaw.Trim();
                        long idxVal;
                        if (long.TryParse(idx, NumberStyles.Integer, CultureInfo.InvariantCulture, out idxVal))
                        {
                            xml.Append("<Access Scope=\"LiteralConstant\"><Constant><ConstantType>DInt</ConstantType><ConstantValue>")
                                .Append(idxVal).Append("</ConstantValue></Constant></Access>");
                        }
                        else
                        {
                            bool idxLocal = idx.StartsWith("#") || (localVariables != null && localVariables.Contains(idx.TrimStart('#')));
                            string idxName = idx.TrimStart('#').Trim().Trim('"');
                            xml.Append("<Access Scope=\"").Append(idxLocal ? "LocalVariable" : "GlobalVariable")
                                .Append("\"><Symbol><Component Name=\"").Append(E(idxName)).Append("\"/></Symbol></Access>");
                        }
                    }
                    xml.Append("</Component>");
                }
                else
                {
                    string component = partText.Trim('"');
                    if (component.Length == 0) throw new ArgumentException("变量符号无效: " + symbol);
                    xml.Append("<Component Name=\"").Append(E(component)).Append("\"/>");
                }
            }
            return xml.Append("</Symbol></Access>").ToString();
        }

        private static int ParseMajorVersion(string portalVersion)
        {
            if (string.IsNullOrWhiteSpace(portalVersion)) return 20;
            string cleaned = portalVersion.Trim().TrimStart('V', 'v');
            int dot = cleaned.IndexOfAny(new[] { '.', '_' });
            if (dot > 0) cleaned = cleaned.Substring(0, dot);
            int major;
            return int.TryParse(cleaned, out major) ? major : 20;
        }

        private static string Pin(int uid, string pin) { return "<NameCon UId=\"" + uid + "\" Name=\"" + pin + "\"/>"; }
        private static void Wire(StringBuilder xml, ref int uid, string endpoints)
        {
            xml.Append("<Wire UId=\"").Append(uid++).Append("\">").Append(endpoints).Append("</Wire>");
        }
        private static string Multilingual(string composition, string text, ref int id)
        {
            return "<MultilingualText ID=\"" + id++ + "\" CompositionName=\"" + composition + "\"><ObjectList><MultilingualTextItem ID=\"" + id++
                + "\" CompositionName=\"Items\"><AttributeList><Culture>zh-CN</Culture><Text>" + E(text)
                + "</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText>";
        }
    }
}
