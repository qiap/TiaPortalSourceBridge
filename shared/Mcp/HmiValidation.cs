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
        // Names may themselves contain dots, slashes or braces. Build complete paths
        // from the exported member tree instead of splitting a reference at a dot.
        internal static IEnumerable<KeyValuePair<string, XmlNode>> TagPaths(string xml)
        {
            foreach (var tag in Tags(xml))
                foreach (var entry in MemberPaths(tag, Value(tag, "AttributeList/Name"))) yield return entry;
        }
        private static IEnumerable<KeyValuePair<string, XmlNode>> MemberPaths(XmlNode node, string path)
        {
            yield return new KeyValuePair<string, XmlNode>(path, node);
            foreach (XmlNode member in node.SelectNodes("ObjectList/Hmi.Tag.TagStructureMember"))
                foreach (var entry in MemberPaths(member, path + "." + Value(member, "AttributeList/Name"))) yield return entry;
        }
        internal static string Binding(XmlNode tag)
        { return Value(tag, "AttributeList/AddressAccessMode") + "|" + Value(tag, "LinkList/Connection/Name") + "|" + Value(tag, "LinkList/ControllerTag/Name") + "|" + Value(tag, "AttributeList/LogicalAddress"); }
        internal static string TypePair(XmlNode tag)
        { return Value(tag, "LinkList/DataType/Name") + "|" + Value(tag, "LinkList/HmiDataType/Name"); }
        private static string BindingEvidence(XmlNode tag)
        {
            var parts = new List<string> { Binding(tag), TypePair(tag) };
            foreach (var entry in MemberPaths(tag, "").Skip(1))
            { parts.Add(entry.Key); parts.Add(Binding(entry.Value)); parts.Add(TypePair(entry.Value)); }
            return string.Join("", parts.Select(p => p.Length + ":" + p));
        }
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
                if (mode == "Symbolic" && connection.Length > 0 && Value(tag, "LinkList/ControllerTag/Name").Length == 0) errors.Add("symbolic_binding_required: " + name);
                if (connection.Length == 0 && Value(tag, "LinkList/ControllerTag/Name").Length > 0) errors.Add("connection_required: " + name);
                if (type.IndexOf("string", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    int length; if (!int.TryParse(Value(tag, "AttributeList/Length"), out length) || length <= 0) errors.Add("string_length_required: " + name);
                }
            }
            foreach (var group in TagPaths(xml).GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                errors.Add("tag_path_conflict: " + group.Key);
            var page=Parse(xml);
            var controls=page.SelectNodes("//*[@CompositionName='ScreenItems']").Cast<XmlNode>().ToArray();
            foreach(var group in controls.GroupBy(c=>Value(c,"AttributeList/ObjectName"),StringComparer.OrdinalIgnoreCase).Where(g=>g.Key.Length>0&&g.Count()>1))
                errors.Add("screen_object_name_conflict: "+group.Key);
            foreach(var control in controls.Where(c=>c.Name=="Hmi.Screen.Line")) {
                var values=new Dictionary<string,int>();
                foreach(string field in new[]{"Left","Top","Width","Height","StartLeft","StartTop","EndLeft","EndTop"}) {
                    int number; if(!int.TryParse(Value(control,"AttributeList/"+field),out number))break; values[field]=number;
                }
                // Only validate layouts whose full endpoint representation is available.
                if(values.Count==8&&(values["Left"]!=Math.Min(values["StartLeft"],values["EndLeft"])||values["Top"]!=Math.Min(values["StartTop"],values["EndTop"])||
                    (long)values["Width"]!=Math.Abs((long)values["EndLeft"]-values["StartLeft"])||(long)values["Height"]!=Math.Abs((long)values["EndTop"]-values["StartTop"])))
                    errors.Add("line_geometry_conflict: "+Value(control,"AttributeList/ObjectName")+"；线段起终点与位置尺寸不一致。");
            }
            return errors;
        }
        internal static void CheckTagEvidence(HmiDocument[] documents, Dictionary<string, string> existing, Action<string, string, string> issue, List<string> warnings, Func<XmlNode, string> resolveBinding = null)
        {
            var oldTags = existing.Values.SelectMany(Tags).ToArray();
            var types = new HashSet<string>(oldTags.Select(TypePair), StringComparer.Ordinal);
            var bindings = new HashSet<string>(oldTags.Select(BindingEvidence), StringComparer.Ordinal);
            var finalTables = new Dictionary<string, string>(existing, StringComparer.OrdinalIgnoreCase);
            foreach (var d in documents.Where(d => d.kind == "tag_tables")) finalTables[d.name] = d.xml;
            var finalTags = finalTables.Values.SelectMany(Tags).ToArray();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in finalTables.Values.SelectMany(TagPaths))
                if (!names.Add(entry.Key)) issue("tag_name_conflict", entry.Key, "HMI 变量或结构体成员路径重复。");
            foreach (var d in documents.Where(d => d.kind == "tag_tables")) foreach (var tag in Tags(d.xml))
            {
                string name = Value(tag, "AttributeList/Name");
                if (!types.Contains(TypePair(tag))) issue("type_unverified", name, "目标 HMI 的当前导出未证明此类型组合受支持；不能将 PLC 类型名直接作为 HMI 类型名，也不能省略类型重试。");
                if (Value(tag, "LinkList/Connection/Name").Length > 0 && !bindings.Contains(BindingEvidence(tag)))
                {
                    string error = resolveBinding == null ? "未提供可核验的 PLC 连接对应关系。" : resolveBinding(tag);
                    if (!string.IsNullOrEmpty(error)) issue("plc_binding_unverified", name, error);
                }
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
            foreach (var entry in TagPaths(xml))
            {
                XmlNode tag = entry.Value;
                string key = "tag:" + entry.Key.Length + ":" + entry.Key + "/";
                result.Add(key + "node_type", tag.Name);
                foreach (string field in new[] { "DataType", "HmiDataType", "Connection", "ControllerTag" })
                    result.Add(key + field, Value(tag, "LinkList/" + field + "/Name"));
                foreach (string field in new[] { "Length", "AddressAccessMode", "LogicalAddress", "AcquisitionTriggerMode" })
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
