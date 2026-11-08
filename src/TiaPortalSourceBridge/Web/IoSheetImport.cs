using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;

namespace TiaPortalSourceBridge.Web
{
    internal sealed class IoSheetCell
    {
        internal string Value = "", Error = "";
    }
    internal sealed class IoSheetRow
    {
        internal int Number;
        internal Dictionary<int, IoSheetCell> Cells = new Dictionary<int, IoSheetCell>();
    }
    internal sealed class IoImportSheet
    {
        internal string Name;
        internal bool Hidden;
        internal int Columns;
        internal List<IoSheetRow> Rows = new List<IoSheetRow>();
        internal List<int[]> Merges = new List<int[]>();
        internal IoSheetCell Cell(IoSheetRow row, int column)
        {
            if (column < 0) return new IoSheetCell();
            if (Merges.Any(m => column >= m[0] && column <= m[2] && row.Number >= m[1] && row.Number <= m[3]))
                return new IoSheetCell { Error = "合并单元格，请拆分并明确逐行值" };
            IoSheetCell cell; return row.Cells.TryGetValue(column, out cell) ? cell : new IoSheetCell();
        }
    }
    // Reads uploaded bytes only. No extraction, external relationships, formulas,
    // macros, formatting-based address inference, or file-system paths are used.
    internal sealed class IoSheetImport
    {
        internal const int MaxBytes = 8 * 1024 * 1024, MaxRows = 20000, MaxCells = 200000, MaxColumns = 128;
        internal List<IoImportSheet> Sheets = new List<IoImportSheet>();
        internal string FileName;
        private static string A(XmlNode n, string name) { var e = n as XmlElement; return e == null ? "" : e.GetAttribute(name); }
        private static XmlDocument Read(ZipArchiveEntry entry)
        {
            if (entry == null) throw new ArgumentException("Excel 文件缺少必要的工作簿内容。");
            if (entry.Length > 16 * 1024 * 1024) throw new ArgumentException("Excel 内部单个文件超过 16 MB，请缩小点表。");
            var doc = new XmlDocument { XmlResolver = null };
            using (var stream = entry.Open()) using (var reader = XmlReader.Create(stream, new XmlReaderSettings {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 })) doc.Load(reader);
            return doc;
        }
        internal static string Column(int index)
        { string s = ""; for (index++; index > 0; index = (index - 1) / 26) s = (char)('A' + (index - 1) % 26) + s; return s; }
        private static int[] Position(string value)
        {
            var m = Regex.Match(value ?? "", @"^([A-Z]{1,3})([1-9][0-9]{0,6})$");
            if (!m.Success) throw new ArgumentException("Excel 单元格坐标无效：" + value);
            int col = 0; foreach (char c in m.Groups[1].Value) col = col * 26 + c - 'A' + 1;
            int row = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            if (col > 16384 || row > 1048576) throw new ArgumentException("Excel 单元格坐标超出范围。");
            return new[] { col - 1, row };
        }
        private static string RichText(XmlNode node)
        { return node == null ? "" : string.Concat(node.SelectNodes("*[local-name()='t'] | *[local-name()='r']/*[local-name()='t']").Cast<XmlNode>().Select(n => n.InnerText)); }
        internal static IoSheetImport Load(byte[] bytes, string fileName)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > MaxBytes) throw new ArgumentException("请选择不超过 8 MB 的 .xlsx 文件。");
            if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 255 || fileName.IndexOfAny(new[] { '/', '\\', '\0' }) >= 0 || !fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("第一版支持 .xlsx 点表；请将旧版 .xls 另存为 .xlsx。");
            var result = new IoSheetImport { FileName = fileName };
            using (var memory = new MemoryStream(bytes, false)) using (var zip = new ZipArchive(memory, ZipArchiveMode.Read)) {
                if (zip.Entries.Count > 512 || zip.Entries.Sum(e => e.Length) > 48L * 1024 * 1024) throw new ArgumentException("Excel 解压后内容过大，请缩小点表。");
                if (zip.Entries.GroupBy(e => e.FullName, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1)) throw new ArgumentException("Excel 包含重复内部路径。");
                if (zip.Entries.Any(e => e.FullName.IndexOf("vbaProject", StringComparison.OrdinalIgnoreCase) >= 0)) throw new ArgumentException("请上传不含宏的 .xlsx 文件。");
                var book = Read(zip.GetEntry("xl/workbook.xml")); var rels = Read(zip.GetEntry("xl/_rels/workbook.xml.rels"));
                var relationships = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (XmlNode rel in rels.SelectNodes("/*[local-name()='Relationships']/*[local-name()='Relationship']")) {
                    if (!A(rel, "Type").EndsWith("/worksheet", StringComparison.Ordinal)) continue;
                    string id = A(rel, "Id"), target = A(rel, "Target");
                    if (A(rel, "TargetMode") == "External" || target.Contains(":") || target.Contains("\\") || target.Contains("%")) throw new ArgumentException("不支持外部工作表链接。");
                    var resolved = new Uri(new Uri("https://xlsx.local/xl/workbook.xml"), target);
                    string location = resolved.AbsolutePath.TrimStart('/');
                    if (resolved.Host != "xlsx.local" || !location.StartsWith("xl/worksheets/", StringComparison.Ordinal) || !location.EndsWith(".xml", StringComparison.Ordinal) || resolved.Query.Length > 0 || resolved.Fragment.Length > 0 || relationships.ContainsKey(id))
                        throw new ArgumentException("Excel 工作表链接无效。");
                    relationships.Add(id, location);
                }
                var shared = new List<string>(); var strings = zip.GetEntry("xl/sharedStrings.xml");
                if (strings != null) foreach (XmlNode si in Read(strings).SelectNodes("/*[local-name()='sst']/*[local-name()='si']")) {
                    if (shared.Count >= MaxCells) throw new ArgumentException("Excel 共享文本数量过大。"); shared.Add(RichText(si));
                }
                int rows = 0, cells = 0; long chars = 0;
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (XmlNode sheetNode in book.SelectNodes("/*[local-name()='workbook']/*[local-name()='sheets']/*[local-name()='sheet']")) {
                    if (result.Sheets.Count >= 20) throw new ArgumentException("最多读取 20 张工作表。");
                    string name = A(sheetNode, "name"), id = sheetNode.Attributes.Cast<XmlAttribute>().Where(a => a.LocalName == "id").Select(a => a.Value).FirstOrDefault(), path;
                    if (string.IsNullOrEmpty(name) || !names.Add(name) || id == null || !relationships.TryGetValue(id, out path)) throw new ArgumentException("工作表名称或链接无效。");
                    var sheet = new IoImportSheet { Name = name, Hidden = A(sheetNode, "state") == "hidden" || A(sheetNode, "state") == "veryHidden" };
                    var xml = Read(zip.GetEntry(path)); var numbers = new HashSet<int>(); int nextRow = 1;
                    foreach (XmlNode rowNode in xml.SelectNodes("/*[local-name()='worksheet']/*[local-name()='sheetData']/*[local-name()='row']")) {
                        int number; if (!int.TryParse(A(rowNode, "r"), out number)) number = nextRow;
                        if (number < 1 || number > 1048576 || !numbers.Add(number)) throw new ArgumentException("工作表行号无效或重复。");
                        nextRow = number + 1; var row = new IoSheetRow { Number = number }; int nextCol = 0;
                        foreach (XmlNode cell in rowNode.SelectNodes("*[local-name()='c']")) {
                            var position = A(cell, "r").Length == 0 ? new[] { nextCol, number } : Position(A(cell, "r")); nextCol = position[0] + 1;
                            if (position[1] != number || row.Cells.ContainsKey(position[0])) throw new ArgumentException("单元格行号不一致或重复。");
                            string type = A(cell, "t"); var valueNode = cell.SelectSingleNode("*[local-name()='v']"); string value = valueNode == null ? "" : valueNode.InnerText;
                            var data = new IoSheetCell();
                            if (cell.SelectSingleNode("*[local-name()='f']") != null) data.Error = "公式不参与对账，请复制并粘贴为值";
                            else if (type == "e") data.Error = "Excel 错误单元格：" + value;
                            else if (type == "s") { int index; if (!int.TryParse(value, out index) || index < 0 || index >= shared.Count) throw new ArgumentException("共享字符串索引无效。"); data.Value = shared[index]; }
                            else if (type == "inlineStr") data.Value = RichText(cell.SelectSingleNode("*[local-name()='is']"));
                            else if (new[] { "", "n", "b", "str", "d" }.Contains(type)) data.Value = value;
                            else data.Error = "不支持的单元格类型：" + type;
                            if (data.Value.Length == 0 && data.Error.Length == 0) continue;
                            if (position[0] >= MaxColumns || ++cells > MaxCells || (chars += data.Value.Length + data.Error.Length) > 8L * 1024 * 1024 || data.Value.Length > 32767)
                                throw new ArgumentException("点表超过读取上限（128 列、20 万单元格、8 MB 文本或单元格 32767 字符）。");
                            row.Cells.Add(position[0], data); sheet.Columns = Math.Max(sheet.Columns, position[0] + 1);
                        }
                        if (row.Cells.Count == 0) continue;
                        if (++rows > MaxRows) throw new ArgumentException("点表最多读取 20000 个非空行，请分表导入。"); sheet.Rows.Add(row);
                    }
                    foreach (XmlNode merge in xml.SelectNodes("/*[local-name()='worksheet']/*[local-name()='mergeCells']/*[local-name()='mergeCell']")) {
                        var parts = A(merge, "ref").Split(':'); if (parts.Length != 2 || sheet.Merges.Count >= 1024) throw new ArgumentException("合并单元格范围无效或过多。");
                        var a = Position(parts[0]); var b = Position(parts[1]); if (a[0] > b[0] || a[1] > b[1]) throw new ArgumentException("合并单元格范围无效。");
                        sheet.Merges.Add(new[] { a[0], a[1], b[0], b[1] });
                    }
                    sheet.Rows = sheet.Rows.OrderBy(r => r.Number).ToList(); result.Sheets.Add(sheet);
                }
            }
            if (result.Sheets.Count == 0) throw new ArgumentException("文件没有可读取的工作表。");
            return result;
        }
        internal IoImportSheet GetSheet(string name)
        { var sheet = Sheets.FirstOrDefault(s => s.Name == name); if (sheet == null) throw new ArgumentException("请选择文件中的工作表。"); return sheet; }
        private static readonly Dictionary<string, string[]> Headers = new Dictionary<string, string[]> {
            { "address", new[] { "地址", "点位", "点位地址", "io地址", "i/o地址", "plc地址", "address", "logicaladdress" } },
            { "name", new[] { "名称", "变量名", "变量名称", "点位名称", "信号名称", "符号", "name", "tag", "tagname", "symbol" } },
            { "type", new[] { "类型", "数据类型", "datatype", "type" } }, { "comment", new[] { "注释", "备注", "说明", "描述", "comment", "description" } }
        };
        private static string Header(string value) { return Regex.Replace(value ?? "", @"\s+", "").ToLowerInvariant(); }
        internal static Dictionary<string, int> Suggest(IoImportSheet sheet, IoSheetRow header)
        {
            var map = new Dictionary<string, int>();
            foreach (var field in Headers) {
                var matches = header.Cells.Where(c => sheet.Cell(header, c.Key).Error.Length == 0 && field.Value.Contains(Header(c.Value.Value))).ToArray();
                map[field.Key] = matches.Length == 1 ? matches[0].Key : -1;
            }
            return map;
        }
        internal object Preview(string sheetName, int headerRow)
        {
            var sheet = GetSheet(sheetName);
            var header = headerRow == 0 ? sheet.Rows.Take(50).OrderByDescending(r => Suggest(sheet, r).Count(p => p.Value >= 0)).ThenBy(r => r.Number).FirstOrDefault() : sheet.Rows.FirstOrDefault(r => r.Number == headerRow);
            if (header == null) throw new ArgumentException("该工作表或所选表头行为空。");
            return new { sheet = sheet.Name, header_row = header.Number, columns = Enumerable.Range(0, sheet.Columns).Select(i => new {
                index = i, letter = Column(i), title = Short(sheet.Cell(header, i).Value), error = sheet.Cell(header, i).Error }).ToArray(),
                suggested = Suggest(sheet, header), samples = sheet.Rows.Where(r => r.Number > header.Number).Take(5).Select(r => new {
                    row = r.Number, values = Enumerable.Range(0, sheet.Columns).Select(i => { var c = sheet.Cell(r, i); return c.Error.Length > 0 ? "[" + c.Error + "]" : Short(c.Value); }).ToArray() }).ToArray() };
        }
        private static string Short(string value) { return value.Length <= 160 ? value : value.Substring(0, 160) + "…（预览省略）"; }
    }
}
