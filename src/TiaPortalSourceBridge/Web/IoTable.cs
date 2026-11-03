using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using TiaPortalSourceBridge.Indexing;

namespace TiaPortalSourceBridge.Web
{
    internal sealed class IoReference
    {
        public string block { get; set; }
        public string group { get; set; }
        public string access { get; set; }
        public string location { get; set; }
        public string evidence { get; set; }
        public string source { get; set; }
        public string state { get; set; }
    }
    internal sealed class IoRow
    {
        public string id { get; set; }
        public string table { get; set; }
        public string group { get; set; }
        public string unit { get; set; }
        public string name { get; set; }
        public string address { get; set; }
        public string area { get; set; }
        public string data_type { get; set; }
        public string comment { get; set; }
        public string value { get; set; }
        public List<IoReference> references { get; set; }
        public IoRow() { references = new List<IoReference>(); }
    }
    internal sealed class IoSnapshot
    {
        public string project_path { get; set; }
        public string plc_name { get; set; }
        public string read_started_utc { get; set; }
        public string read_completed_utc { get; set; }
        public int tables_total { get; set; }
        public int tables_read { get; set; }
        public string reference_source { get; set; }
        public string reference_coverage { get; set; }
        public List<string> warnings { get; set; }
        public List<IoRow> rows { get; set; }
        public IoSnapshot() { warnings = new List<string>(); rows = new List<IoRow>(); }
    }
    internal static class IoTable
    {
        internal static string Text(Dictionary<string, object> map, string key)
        { object v; return map != null && map.TryGetValue(key, out v) ? Convert.ToString(v) : ""; }
        internal static IEnumerable<Dictionary<string, object>> Maps(Dictionary<string, object> map, string key)
        { object v; return map.TryGetValue(key, out v) && v is IEnumerable ? ((IEnumerable)v).OfType<Dictionary<string, object>>() : Enumerable.Empty<Dictionary<string, object>>(); }
        private static string Child(XmlNode node, string name)
        { var child = node.SelectSingleNode("*[local-name()='" + name + "']"); return child == null ? "" : child.InnerText; }
        internal static string Address(string text)
        {
            string a = (text ?? "").Trim().Replace(" ", "").ToUpperInvariant();
            if (a.StartsWith("%")) a = a.Substring(1);
            a = Regex.Replace(a, "^E(?=[BWD]?\\d)", "I"); a = Regex.Replace(a, "^A(?=[BWD]?\\d)", "Q");
            return a;
        }
        internal static string Symbol(string text)
        { return ContentIndexer.NormalizeSymbol((text ?? "").Trim()); }
        private static string TagSymbol(string name)
        { return Symbol(ContentIndexer.ComponentName(name ?? "")); }
        internal static IEnumerable<IoRow> Parse(string xml, string table, string group, string unit)
        {
            var doc = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) doc.Load(reader);
            foreach (XmlNode tag in doc.SelectNodes("//*[local-name()='SW.Tags.PlcTag' or local-name()='SW.Tags.PlcUserConstant']")) {
                var a = tag.SelectSingleNode("*[local-name()='AttributeList']"); if (a == null) continue;
                string name = Child(a, "Name"), address = Child(a, "LogicalAddress"), normalized = Address(address);
                string area = tag.LocalName == "SW.Tags.PlcUserConstant" ? "CONST" : Regex.IsMatch(normalized, "^[IQM][BWD]?\\d+(\\.[0-7])?$") ? normalized.Substring(0, 1) : "OTHER";
                var comments = tag.SelectNodes("./*[local-name()='ObjectList']/*[local-name()='MultilingualText' and @CompositionName='Comment']//*[local-name()='MultilingualTextItem']/*[local-name()='AttributeList']")
                    .Cast<XmlNode>().Select(n => (Child(n, "Culture").Length == 0 ? "" : Child(n, "Culture") + ": ") + Child(n, "Text")).ToArray();
                yield return new IoRow { id = IndexJson.Hash(unit + "\n" + group + "\n" + table + "\n" + name), table = table, group = group, unit = unit,
                    name = name, address = address, area = area, data_type = Child(a, "DataTypeName"), value = Child(a, "Value"),
                    comment = comments.Length == 0 ? Child(a, "Comment") : string.Join("\n", comments) };
            }
        }
        internal static long SortAddress(IoRow row)
        { var m = Regex.Match(Address(row.address), "^[IQM][BWD]?(\\d+)(?:\\.([0-7]))?$"); long n; return m.Success && long.TryParse(m.Groups[1].Value, out n) ? n * 8 + (m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0) : long.MaxValue; }
        internal static IEnumerable<IoRow> Filter(IEnumerable<IoRow> rows, string area, string query, string table)
        {
            query = (query ?? "").Trim();
            return rows.Where(r => (string.IsNullOrEmpty(area) || area == "ALL" || (area == "IO" ? r.area == "I" || r.area == "Q" : r.area == area))
                && (string.IsNullOrEmpty(table) || r.unit + "/" + r.group + "/" + r.table == table)
                && (query.Length == 0 || string.Join(" ", new[] { r.name, r.address, r.data_type, r.comment, r.table, r.group, r.unit }).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0));
        }
        // Matching is scoped by software unit. No substring matches such as Motor1 -> Motor10.
        internal static void AttachIndex(IoSnapshot result, Dictionary<string, object> data)
        {
            var bySymbol = result.rows.ToLookup(r => r.unit + "\n" + TagSymbol(r.name), StringComparer.OrdinalIgnoreCase);
            var byAddress = result.rows.Where(r => r.address.Length > 0).ToLookup(r => r.unit + "\n" + Address(r.address), StringComparer.OrdinalIgnoreCase);
            foreach (var reference in Maps(data, "references")) {
                string target = Text(reference, "target"), unit = Text(reference, "unit"), evidence = Text(reference, "evidence");
                if (target.StartsWith("#") || evidence.Contains("scope=LocalVariable")) continue;
                foreach (var row in bySymbol[unit + "\n" + Symbol(target)].Concat(byAddress[unit + "\n" + Address(target)]).Distinct())
                    row.references.Add(new IoReference { block = Text(reference, "block"), group = Text(reference, "group"), access = Text(reference, "access"), location = Text(reference, "location"), evidence = evidence, source = "索引显式引用", state = Text(reference, "state") });
            }
            result.reference_source = "索引显式引用";
            result.reference_coverage = "PLC 程序块内容 " + Text(data, "ready") + "/" + Text(data, "total") + " 已读取；不推断间接地址、跨软件单元绑定或 HMI 引用。零条证据不等于未使用。";
            foreach (var error in Maps(data, "errors")) result.warnings.Add(Text(error, "name") + ": " + Text(error, "state") + " " + Text(error, "error"));
        }
        internal static void AttachNative(IoSnapshot result, Dictionary<string, object> data, string unit)
        {
            var bySymbol = result.rows.Where(r => r.unit == unit).ToLookup(r => TagSymbol(r.name), StringComparer.OrdinalIgnoreCase);
            var byAddress = result.rows.Where(r => r.unit == unit && r.address.Length > 0).ToLookup(r => Address(r.address), StringComparer.OrdinalIgnoreCase);
            foreach (var source in Maps(data, "items")) {
                var matches = bySymbol[TagSymbol(Text(source, "name"))].Concat(byAddress[Address(Text(source, "address"))]).Distinct();
                foreach (var row in matches) foreach (var reference in Maps(source, "references")) foreach (var location in Maps(reference, "locations"))
                    row.references.Add(new IoReference { block = Text(reference, "name"), group = Text(reference, "path"), access = Text(location, "access"),
                        location = Text(location, "reference_location"), evidence = Text(location, "referenced_as_name"), source = "V21 官方交叉引用", state = "live" });
            }
            if (Text(data, "truncated").Equals("True", StringComparison.OrdinalIgnoreCase)) result.warnings.Add("软件单元 " + (unit.Length == 0 ? "默认" : unit) + " 的官方引用返回达到上限，引用信息不完整。");
        }
    }
}
