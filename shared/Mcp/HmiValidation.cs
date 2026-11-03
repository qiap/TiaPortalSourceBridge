using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using System.IO;

namespace TiaPortalSourceBridge.Mcp
{
    internal sealed class HmiVerificationException : InvalidOperationException
    {
        internal readonly object[] Differences;
        internal HmiVerificationException(object[] differences) : base("HMI 回读核验失败；详见 verification_differences 中的预期值与实际值。") { Differences = differences; }
    }
    // Verification compares requested semantic fields, never generated XML IDs.
    internal static class HmiValidation
    {
        internal static XmlDocument Parse(string xml)
        {
            var doc = new XmlDocument { XmlResolver = null };
            using (var r = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) doc.Load(r);
            return doc;
        }
        internal static string Value(XmlNode node, string path)
        { var found = node.SelectSingleNode(path); return found == null ? "" : found.InnerText; }
        internal static XmlNode[] Tags(string xml) { return Parse(xml).SelectNodes("//Hmi.Tag.Tag").Cast<XmlNode>().ToArray(); }
        internal static string Binding(XmlNode tag)
        { return Value(tag, "AttributeList/AddressAccessMode") + "|" + Value(tag, "LinkList/Connection/Name") + "|" + Value(tag, "LinkList/ControllerTag/Name") + "|" + Value(tag, "AttributeList/LogicalAddress"); }
        internal static string TypePair(XmlNode tag)
        { return Value(tag, "LinkList/DataType/Name") + "|" + Value(tag, "LinkList/HmiDataType/Name"); }
        internal static List<string> Validate(string xml)
        {
            var errors = new List<string>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tag in Tags(xml))
            {
                string name = Value(tag, "AttributeList/Name"), type = Value(tag, "LinkList/HmiDataType/Name");
                if (name.Length == 0 || !names.Add(name)) errors.Add("tag_name_conflict: " + name);
                if (type.Length == 0 || Value(tag, "LinkList/DataType/Name").Length == 0) errors.Add("explicit_type_required: " + name + "; 不允许省略类型后使用默认 Int。");
                if (tag.SelectSingleNode("LinkList/PlcTag") != null) errors.Add("invalid_symbol_link: " + name + "; 经典 HMI XML 使用 ControllerTag，不能使用 PlcTag。");
                string mode = Value(tag, "AttributeList/AddressAccessMode"), connection = Value(tag, "LinkList/Connection/Name");
                if (mode == "Symbolic" && (connection.Length == 0 || Value(tag, "LinkList/ControllerTag/Name").Length == 0)) errors.Add("symbolic_binding_required: " + name);
                if (type.IndexOf("string", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    int length; if (!int.TryParse(Value(tag, "AttributeList/Length"), out length) || length <= 0) errors.Add("string_length_required: " + name);
                }
            }
            return errors;
        }
        internal static void CheckTagEvidence(HmiDocument[] documents, Dictionary<string, string> existing, Action<string, string, string> issue, List<string> warnings)
        {
            var oldTags = existing.Values.SelectMany(Tags).ToArray();
            var types = new HashSet<string>(oldTags.Select(TypePair), StringComparer.Ordinal);
            var bindings = new HashSet<string>(oldTags.Select(t => Binding(t) + "|" + TypePair(t)), StringComparer.Ordinal);
            var finalTables = new Dictionary<string, string>(existing, StringComparer.OrdinalIgnoreCase);
            foreach (var d in documents.Where(d => d.kind == "tag_tables")) finalTables[d.name] = d.xml;
            var finalTags = finalTables.Values.SelectMany(Tags).ToArray();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tag in finalTags)
                if (!names.Add(Value(tag, "AttributeList/Name"))) issue("tag_name_conflict", Value(tag, "AttributeList/Name"), "HMI 变量名在不同表中重复。");
            foreach (var d in documents.Where(d => d.kind == "tag_tables")) foreach (var tag in Tags(d.xml))
            {
                string name = Value(tag, "AttributeList/Name");
                if (!types.Contains(TypePair(tag))) issue("type_unverified", name, "目标 HMI 的当前导出未证明此类型组合受支持；不能将 PLC 类型名直接作为 HMI 类型名，也不能省略类型重试。");
                if (Value(tag, "LinkList/Connection/Name").Length > 0 && !bindings.Contains(Binding(tag) + "|" + TypePair(tag)))
                    issue("plc_binding_unverified", name, "新的连接/PLC 符号/类型组合尚不能原生核验，已阻止写入。" + "本结果不包含集成连接；空列表不证明连接不存在。");
            }
            if (oldTags.Any(t => Value(t, "LinkList/Connection/Name").Length > 0))
                warnings.Add("保留的既有 PLC 绑定仅与当前 HMI 导出核对；不证明 PLC 端符号仍存在或运行时通信正常。");
        }
        private static string Identity(XmlNode node)
        {
            if (node == null || node is XmlDocument) return "";
            string name = Value(node, "AttributeList/Name");
            if (name.Length == 0) name = Value(node, "AttributeList/ObjectName");
            if (name.Length == 0) name = Value(node, "AttributeList/Index");
            // Length-prefixed identities prevent names containing '/' from colliding.
            return Identity(node.ParentNode) + "/" + node.Name + "[" + name.Length + ":" + name + "]";
        }
        internal static Dictionary<string, string> Fields(string xml)
        {
            var doc = Parse(xml); var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (XmlNode tag in doc.SelectNodes("//Hmi.Tag.Tag"))
            {
                string key = "tag:" + Value(tag, "AttributeList/Name") + "/";
                foreach (string field in new[] { "DataType", "HmiDataType", "Connection", "ControllerTag" })
                    result.Add(key + field, Value(tag, "LinkList/" + field + "/Name"));
                foreach (string field in new[] { "Length", "AddressAccessMode", "LogicalAddress" })
                    if (tag.SelectSingleNode("AttributeList/" + field) != null) result.Add(key + field, Value(tag, "AttributeList/" + field));
            }
            foreach (XmlNode link in doc.SelectNodes("//LinkList/Tag | //LinkList/TextList"))
                result.Add(Identity(link), Value(link, "Name"));
            return result;
        }
        internal static object[] Compare(string expected, string actual)
        {
            var wanted = Fields(expected); var got = Fields(actual); var differences = new List<object>();
            foreach (var item in wanted)
            {
                string value; bool present = got.TryGetValue(item.Key, out value);
                if (!present || item.Value != value) differences.Add(new { field = item.Key, expected = item.Value, actual = present ? value : null, state = present ? "mismatch" : "unverified" });
            }
            return differences.ToArray();
        }
    }
}
