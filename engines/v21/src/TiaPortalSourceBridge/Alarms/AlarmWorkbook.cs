using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Xml;

namespace TiaPortalSourceBridge.Alarms
{
    // Read and patch the native Siemens package, rather than round-tripping through an Excel writer.
    // This preserves TIA_Version, relationships, other sheets, rich text and unknown columns.
    internal sealed class AlarmWorkbook
    {
        private const string Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private readonly byte[] package;
        private readonly XmlDocument sheet;
        private readonly XmlDocument strings;
        private readonly string sheetPath;
        private readonly List<string> shared;
        private readonly Dictionary<string, int> stringIds;
        private readonly Dictionary<string, int> columns;
        private readonly List<AlarmRow> rows;
        private readonly XmlElement data;
        private int nextRow;
        internal string Hash { get; private set; }
        internal int Count { get { return rows.Count; } }
        internal static readonly Dictionary<string, string> Fields = new Dictionary<string, string>(StringComparer.Ordinal) {
            {"id", "ID"}, {"name", "Name"}, {"alarm_class", "Class"}, {"trigger_tag", "Trigger tag"},
            {"trigger_bit", "Trigger bit"}, {"acknowledgement_tag", "Acknowledgement tag"}, {"acknowledgement_bit", "Acknowledgement bit"},
            {"plc_acknowledgement_tag", "PLC acknowledgement tag"}, {"plc_acknowledgement_bit", "PLC acknowledgement bit"},
            {"group", "Group"}, {"report", "Report"}
        };
        internal sealed class AlarmRow { internal XmlElement Element; internal Dictionary<string, string> Values; }
        private static XmlDocument Parse(byte[] bytes)
        {
            var doc = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
            using (var stream = new MemoryStream(bytes))
            using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64L * 1024 * 1024 })) doc.Load(reader);
            return doc;
        }
        private static byte[] Entry(ZipArchive archive, string name)
        {
            var entry = archive.GetEntry(name); if (entry == null) throw new ArgumentException("原生报警文件缺少：" + name);
            if (entry.Length > 64L * 1024 * 1024) throw new ArgumentException("报警文件条目过大。");
            using (var stream = entry.Open()) using (var buffer = new MemoryStream()) { stream.CopyTo(buffer); return buffer.ToArray(); }
        }
        private static string Text(XmlNode node) { return node == null ? "" : node.InnerText; }
        private static string Col(string address) { return new string(address.TakeWhile(char.IsLetter).ToArray()); }
        private static int ColNumber(string address) { int n = 0; foreach (char c in Col(address)) n = n * 26 + c - 'A' + 1; return n; }
        private static string ColName(int index) { string name = ""; while (index > 0) { index--; name = (char)('A' + index % 26) + name; index /= 26; } return name; }
        internal AlarmWorkbook(string path) : this(File.ReadAllBytes(path)) { }
        internal AlarmWorkbook(byte[] bytes)
        {
            package = bytes;
            using (var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read))
            {
                var properties = Parse(Entry(zip, "docProps/custom.xml"));
                if (properties.SelectSingleNode("//*[local-name()='property' and @name='TIA_Version']") == null)
                    throw new ArgumentException("不是博途原生报警导出文件：缺少 TIA_Version。请先从博途导出报警作为模板。");
                var workbook = Parse(Entry(zip, "xl/workbook.xml"));
                var selected = workbook.SelectSingleNode("//*[local-name()='sheet' and @name='DiscreteAlarms']") as XmlElement;
                if (selected == null) throw new ArgumentException("文件不含 DiscreteAlarms 离散报警表。");
                string relation = selected.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
                var rels = Parse(Entry(zip, "xl/_rels/workbook.xml.rels"));
                var rel = rels.DocumentElement.ChildNodes.OfType<XmlElement>().SingleOrDefault(e => e.GetAttribute("Id") == relation);
                if (rel == null) throw new ArgumentException("报警表关系缺失。");
                var resolved = new Uri(new Uri("http://package/xl/workbook.xml"), rel.GetAttribute("Target"));
                sheetPath = Uri.UnescapeDataString(resolved.AbsolutePath.TrimStart('/'));
                sheet = Parse(Entry(zip, sheetPath));
                strings = Parse(Entry(zip, "xl/sharedStrings.xml"));
                shared = strings.DocumentElement.ChildNodes.OfType<XmlElement>().Select(e => string.Concat(e.SelectNodes(".//*[local-name()='t']").Cast<XmlNode>().Select(n => n.InnerText))).ToList();
                stringIds = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int i = 0; i < shared.Count; i++) if (!stringIds.ContainsKey(shared[i])) stringIds.Add(shared[i], i);
                data = sheet.SelectSingleNode("/*[local-name()='worksheet']/*[local-name()='sheetData']") as XmlElement;
                if (data == null) throw new ArgumentException("报警表缺少 sheetData。");
                var xmlRows = data.ChildNodes.OfType<XmlElement>().Where(e => e.LocalName == "row").ToArray();
                var header = xmlRows.SingleOrDefault(r => r.GetAttribute("r") == "1");
                if (header == null) throw new ArgumentException("报警表缺少表头。");
                columns = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var cell in header.ChildNodes.OfType<XmlElement>())
                {
                    string title = CellValue(cell); if (columns.ContainsKey(title)) throw new ArgumentException("报警表含重复列：" + title);
                    columns.Add(title, ColNumber(cell.GetAttribute("r")));
                }
                foreach (string title in new[] { "ID", "Name", "Class", "Trigger tag", "Trigger bit" })
                    if (!columns.ContainsKey(title)) throw new ArgumentException("报警表缺少列：" + title);
                rows = new List<AlarmRow>();
                foreach (var row in xmlRows.Where(r => r != header))
                {
                    var cells = row.ChildNodes.OfType<XmlElement>().ToDictionary(c => ColNumber(c.GetAttribute("r")), c => CellValue(c));
                    var values = columns.ToDictionary(c => c.Key, c => cells.ContainsKey(c.Value) ? cells[c.Value] : "", StringComparer.Ordinal);
                    if (values["ID"].Length > 0) rows.Add(new AlarmRow { Element = row, Values = values });
                }
                nextRow = xmlRows.Select(r => int.Parse(r.GetAttribute("r"))).DefaultIfEmpty(1).Max() + 1;
                // ZIP timestamps, string indexes and row order may differ on a fresh TIA export.
                string canonical = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 }.Serialize(rows.OrderBy(r => r.Values["ID"], StringComparer.Ordinal)
                    .Select(r => columns.OrderBy(c => c.Value).Select(c => new[] { c.Key, r.Values[c.Key] }).ToArray()).ToArray());
                using (var sha = SHA256.Create()) Hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical))).Replace("-", "").ToLowerInvariant();
            }
        }
        private string CellValue(XmlElement cell)
        {
            string value = Text(cell.ChildNodes.OfType<XmlElement>().FirstOrDefault(e => e.LocalName == "v"));
            if (cell.GetAttribute("t") == "s") { int index; if (!int.TryParse(value, out index) || index < 0 || index >= shared.Count) throw new ArgumentException("报警文件共享字符串下标无效。"); return shared[index]; }
            if (cell.GetAttribute("t") == "inlineStr") return string.Concat(cell.SelectNodes(".//*[local-name()='t']").Cast<XmlNode>().Select(n => n.InnerText));
            return value;
        }
        private int StringId(string value)
        {
            int id; if (stringIds.TryGetValue(value, out id)) return id;
            id = shared.Count; shared.Add(value); stringIds[value] = id;
            var si = strings.CreateElement("x", "si", Ns); var t = strings.CreateElement("x", "t", Ns);
            t.SetAttribute("space", "http://www.w3.org/XML/1998/namespace", "preserve"); t.InnerText = value; si.AppendChild(t); strings.DocumentElement.AppendChild(si);
            return id;
        }
        private void Set(AlarmRow row, string title, string value)
        {
            if (!columns.ContainsKey(title)) throw new ArgumentException("原生文件不含列：" + title);
            int col = columns[title]; string address = ColName(col) + row.Element.GetAttribute("r");
            var cell = row.Element.ChildNodes.OfType<XmlElement>().FirstOrDefault(c => c.GetAttribute("r") == address);
            if (cell == null)
            {
                cell = sheet.CreateElement("x", "c", Ns); cell.SetAttribute("r", address);
                var next = row.Element.ChildNodes.OfType<XmlElement>().FirstOrDefault(c => ColNumber(c.GetAttribute("r")) > col);
                row.Element.InsertBefore(cell, next);
            }
            // Changing the value must also remove a possible inline string/formula from that cell.
            while (cell.HasChildNodes) cell.RemoveChild(cell.FirstChild);
            bool numeric = title == "ID" || title.EndsWith(" bit", StringComparison.Ordinal);
            cell.SetAttribute("t", numeric ? "n" : "s");
            var v = sheet.CreateElement("x", "v", Ns); v.InnerText = numeric ? value : StringId(value).ToString(CultureInfo.InvariantCulture); cell.AppendChild(v);
            row.Values[title] = value;
        }
        internal object Read(string filter, string tag, int offset, int limit, bool includeFields)
        {
            var hits = rows.Where(r => (string.IsNullOrEmpty(filter) || r.Values.Values.Any(v => v.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0))
                && (string.IsNullOrEmpty(tag) || string.Equals(r.Values["Trigger tag"], tag, StringComparison.OrdinalIgnoreCase))).ToArray();
            return new { file_hash = Hash, total_count = rows.Count, matched_count = hits.Length, offset = offset, next_offset = offset + limit < hits.Length ? (object)(offset + limit) : null,
                alarms = hits.Skip(offset).Take(limit).Select(r => Model(r, includeFields)).ToArray() };
        }
        private object Model(AlarmRow row, bool all)
        {
            var result = new Dictionary<string, object>();
            foreach (var field in Fields) if (row.Values.ContainsKey(field.Value)) result[field.Key] = row.Values[field.Value];
            result["texts"] = row.Values.Where(p => p.Key.StartsWith("Alarm text [", StringComparison.Ordinal)).ToDictionary(p => p.Key.Substring(12, p.Key.IndexOf(']') - 12), p => p.Value);
            if (all) result["fields"] = new Dictionary<string, string>(row.Values);
            return result;
        }
        private static int Integer(object raw, string label, int min, int max)
        {
            int value; if (raw is bool || !int.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) || value < min || value > max)
                throw new ArgumentException(label + " 必须在 " + min + ".." + max);
            return value;
        }
        internal object Patch(string changesJson)
        {
            var input = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 }.DeserializeObject(changesJson) as object[];
            if (input == null || input.Length < 1 || input.Length > 10000) throw new ArgumentException("changes_json 必须为 1..10000 项报警操作数组。");
            var byId = new Dictionary<int, AlarmRow>();
            foreach (var row in rows)
            {
                int id = Integer(row.Values["ID"], "ID", 1, int.MaxValue);
                if (byId.ContainsKey(id)) throw new ArgumentException("原生文件含重复报警 ID：" + id); byId[id] = row;
            }
            var seen = new HashSet<int>(); int added = 0, updated = 0, unchanged = 0;
            var changed = new List<object>();
            foreach (object raw in input)
            {
                var m = raw as Dictionary<string, object>;
                if (m == null || !m.ContainsKey("id")) throw new ArgumentException("每项操作必须包含 id。");
                int id = Integer(m["id"], "id", 1, int.MaxValue);
                if (!seen.Add(id)) throw new ArgumentException("批次含重复 ID：" + id);
                string op = m.ContainsKey("op") ? Convert.ToString(m["op"]) : "upsert";
                if (op != "upsert" && op != "update" && op != "create") throw new ArgumentException("op 仅支持 create/update/upsert。");
                AlarmRow row; bool exists = byId.TryGetValue(id, out row);
                if (exists && op == "create" || !exists && op == "update") throw new ArgumentException("报警 ID 状态不符合 " + op + "：" + id);
                var updates = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var pair in m)
                {
                    if (pair.Key == "op" || pair.Key == "id" || pair.Key == "template_id") continue;
                    if (pair.Key == "texts" || pair.Key == "fields")
                    {
                        var values = pair.Value as Dictionary<string, object>; if (values == null) throw new ArgumentException(pair.Key + " 必须为对象。");
                        foreach (var value in values)
                        {
                            string title = pair.Key == "fields" ? value.Key : "Alarm text [" + value.Key + "], Alarm text";
                            if (title == "ID") throw new ArgumentException("fields 不得修改 ID。");
                            if (!columns.ContainsKey(title)) throw new ArgumentException("原生文件中不存在列：" + title);
                            if (updates.ContainsKey(title)) throw new ArgumentException("同一字段重复设置：" + title);
                            updates.Add(title, Convert.ToString(value.Value, CultureInfo.InvariantCulture));
                        }
                    }
                    else
                    {
                        string title; if (!Fields.TryGetValue(pair.Key, out title)) throw new ArgumentException("未知报警字段：" + pair.Key);
                        if (updates.ContainsKey(title)) throw new ArgumentException("同一字段重复设置：" + title);
                        updates.Add(title, Convert.ToString(pair.Value, CultureInfo.InvariantCulture));
                    }
                }
                if (!exists)
                {
                    AlarmRow template = null;
                    if (m.ContainsKey("template_id") && !byId.TryGetValue(Integer(m["template_id"], "template_id", 1, int.MaxValue), out template)) throw new ArgumentException("template_id 不存在。");
                    int rn = nextRow++;
                    var node = template == null ? sheet.CreateElement("x", "row", Ns) : (XmlElement)template.Element.CloneNode(true);
                    node.SetAttribute("r", rn.ToString());
                    foreach (var c in node.ChildNodes.OfType<XmlElement>()) c.SetAttribute("r", Col(c.GetAttribute("r")) + rn);
                    row = new AlarmRow { Element = node, Values = columns.ToDictionary(c => c.Key, c => template == null ? "" : template.Values[c.Key]) };
                    data.AppendChild(node); rows.Add(row); byId[id] = row;
                    foreach (var pair in columns) if (template == null) Set(row, pair.Key, pair.Key == "ID" || pair.Key.EndsWith(" bit", StringComparison.Ordinal) ? "0" : pair.Key.EndsWith(" tag", StringComparison.Ordinal) || pair.Key == "Group" ? "<No value>" : "");
                    Set(row, "ID", id.ToString());
                    if (template == null) { Set(row, "Class", "Errors"); if (columns.ContainsKey("Report")) Set(row, "Report", "True"); }
                    if (!updates.ContainsKey("Name")) throw new ArgumentException("新增报警必须提供 name。");
                    if (!updates.ContainsKey("Trigger tag")) throw new ArgumentException("新增报警必须提供 trigger_tag。");
                    if (!updates.ContainsKey("Trigger bit")) throw new ArgumentException("新增报警必须提供 trigger_bit。");
                    if (!updates.Keys.Any(k => k.StartsWith("Alarm text [", StringComparison.Ordinal))) throw new ArgumentException("新增报警必须提供 texts。");
                }
                bool differs = !exists;
                foreach (var pair in updates)
                {
                    if (pair.Key == "Trigger bit" || pair.Key.EndsWith(" bit", StringComparison.Ordinal)) Integer(pair.Value, pair.Key, 0, 31);
                    if (pair.Key == "Name" && string.IsNullOrWhiteSpace(pair.Value) || pair.Key == "Trigger tag" && (string.IsNullOrWhiteSpace(pair.Value) || pair.Value == "<No value>")) throw new ArgumentException(pair.Key + " 不得为空。");
                    if (row.Values[pair.Key] != pair.Value) { Set(row, pair.Key, pair.Value); differs = true; }
                }
                if (!differs) unchanged++; else { if (exists) updated++; else added++; changed.Add(Model(row, false)); }
            }
            var duplicateName = rows.GroupBy(r => r.Values["Name"], StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            if (duplicateName != null) throw new ArgumentException("报警名称冲突：" + duplicateName.Key);
            var duplicateTrigger = rows.GroupBy(r => r.Values["Trigger tag"].ToUpperInvariant() + "|" + r.Values["Trigger bit"]).Where(g => g.Count() > 1 && g.Any(r => seen.Contains(Integer(r.Values["ID"], "ID", 1, int.MaxValue)))).FirstOrDefault();
            if (duplicateTrigger != null) throw new ArgumentException("触发位冲突：" + duplicateTrigger.Key);
            return new { added_count = added, updated_count = updated, unchanged_count = unchanged, changed_count = added + updated, total_count = rows.Count, changed = changed.ToArray() };
        }
        internal object Verify(string changesJson)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 };
            var changes = json.DeserializeObject(changesJson) as object[];
            if (changes == null) throw new ArgumentException("changes_json 必须为操作数组。");
            foreach (var item in changes.Cast<Dictionary<string, object>>()) { item["op"] = "update"; item.Remove("template_id"); }
            return Patch(json.Serialize(changes));
        }
        internal void Write(string outputPath)
        {
            string path = Path.GetFullPath(outputPath);
            if (File.Exists(path)) throw new IOException("输出文件已存在，未覆盖：" + path);
            var dim = sheet.DocumentElement.ChildNodes.OfType<XmlElement>().FirstOrDefault(e => e.LocalName == "dimension");
            if (dim != null) dim.SetAttribute("ref", "A1:" + ColName(columns.Values.Max()) + data.ChildNodes.OfType<XmlElement>().Select(r => int.Parse(r.GetAttribute("r"))).Max());
            strings.DocumentElement.RemoveAttribute("count"); strings.DocumentElement.RemoveAttribute("uniqueCount");
            using (var input = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read))
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            using (var output = new ZipArchive(file, ZipArchiveMode.Create))
            {
                foreach (var entry in input.Entries)
                {
                    var copy = output.CreateEntry(entry.FullName, CompressionLevel.Fastest); copy.LastWriteTime = entry.LastWriteTime;
                    using (var dest = copy.Open())
                    {
                        if (entry.FullName == sheetPath || entry.FullName == "xl/sharedStrings.xml")
                        {
                            var doc = entry.FullName == sheetPath ? sheet : strings;
                            using (var writer = XmlWriter.Create(dest, new XmlWriterSettings { Encoding = new UTF8Encoding(false), CloseOutput = false })) doc.Save(writer);
                        }
                        else using (var source = entry.Open()) source.CopyTo(dest);
                    }
                }
            }
            // Validate the exact saved package before it can be submitted to TIA.
            new AlarmWorkbook(path);
        }
    }
}
