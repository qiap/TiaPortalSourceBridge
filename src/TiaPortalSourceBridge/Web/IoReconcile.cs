using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace TiaPortalSourceBridge.Web
{
    internal sealed class IoReconcileOptions
    {
        public string sheet { get; set; }
        public int header_row { get; set; }
        public int address { get; set; }
        public int name { get; set; }
        public int type { get; set; }
        public int comment { get; set; }
        public string scope { get; set; }
        public string unit { get; set; }
        public string table { get; set; }
        public IoReconcileOptions() { address = name = type = comment = -1; scope = "native"; unit = table = ""; }
    }
    internal sealed class IoReconcilePoint
    {
        public string name { get; set; }
        public string address { get; set; }
        public string type { get; set; }
        public string comment { get; set; }
        public string table { get; set; }
        public string group { get; set; }
        public string unit { get; set; }
        public string mapping { get; set; }
        public int references { get; set; }
    }
    internal sealed class IoReconcileRow
    {
        public int excel_row { get; set; }
        public string status { get; set; }
        public string address { get; set; }
        public string name { get; set; }
        public string type { get; set; }
        public string comment { get; set; }
        public List<string> issues { get; set; }
        public List<IoReconcilePoint> plc { get; set; }
        public IoReconcileRow() { issues = new List<string>(); plc = new List<IoReconcilePoint>(); }
    }
    internal sealed class IoReconcileReport
    {
        public string id { get; set; }
        public string snapshot_id { get; set; }
        public string project_path { get; set; }
        public string plc_name { get; set; }
        public string file_name { get; set; }
        public string source_mode { get; set; }
        public string source_note { get; set; }
        public string snapshot_started { get; set; }
        public string snapshot_completed { get; set; }
        public string generated_at { get; set; }
        public IoReconcileOptions options { get; set; }
        public int excel_rows { get; set; }
        public int plc_rows { get; set; }
        public Dictionary<string, int> counts { get; set; }
        public List<string> warnings { get; set; }
        public List<IoReconcileRow> rows { get; set; }
    }
    internal static class IoReconcile
    {
        internal static readonly Dictionary<string, string> Statuses = new Dictionary<string, string> {
            { "match", "所选字段一致" }, { "different", "字段差异" }, { "excel_only", "仅 Excel 列出" },
            { "plc_only", "仅 PLC 列出" }, { "conflict", "地址或名称冲突" }, { "ambiguous", "存在多个匹配" }, { "invalid", "Excel 数据待修正" }
        };
        private sealed class AddressRange { internal string Area, Canonical; internal long Start, End; }
        private sealed class Point { internal IoRow Row; internal AddressRange Range; internal int Id; }
        private sealed class IntervalIndex
        {
            private readonly Point[] points; private readonly long[] ends;
            internal IntervalIndex(IEnumerable<Point> source) {
                points = source.OrderBy(p => p.Range.Start).ToArray(); ends = new long[points.Length]; long end = -1;
                for (int i = 0; i < points.Length; i++) ends[i] = end = Math.Max(end, points[i].Range.End);
            }
            internal IEnumerable<Point> Overlaps(AddressRange range) {
                int low = 0, high = points.Length;
                while (low < high) { int mid = (low + high) / 2; if (points[mid].Range.Start <= range.End) low = mid + 1; else high = mid; }
                for (int i = low - 1; i >= 0 && ends[i] >= range.Start; i--) if (points[i].Range.End >= range.Start) yield return points[i];
            }
        }
        private static string N(string value) { return value ?? ""; }
        private static string Name(string value) { return N(value).Trim(); }
        private static string Type(string value) { return Regex.Replace(N(value), @"\s+", "").ToUpperInvariant(); }
        private static bool Same(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        private static bool SameComment(string excel, IoRow plc)
        {
            string left = Name(excel).Replace("\r\n", "\n"), right = Name(plc.comment).Replace("\r\n", "\n");
            if (left == right) return true;
            return plc.singleLanguageComment != null && left == Name(plc.singleLanguageComment).Replace("\r\n", "\n");
        }
        internal static string TableKey(IoRow row) { return N(row.group) + "\n" + N(row.table); }
        private static bool InScope(IoRow row, string scope) { return scope == "mapped" ? !string.IsNullOrEmpty(row.mapping_fc) : string.IsNullOrEmpty(row.mapping_fc) && string.IsNullOrEmpty(row.origin); }
        private static AddressRange Range(string address)
        {
            var m = Regex.Match(IoTable.Address(address), @"^([IQM])([BWD]?)([0-9]{1,9})(?:\.([0-7]))?$");
            if (!m.Success || (m.Groups[2].Value.Length == 0) != m.Groups[4].Success) return null;
            long start = long.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) * 8 + (m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : 0);
            string width = m.Groups[2].Value;
            return new AddressRange { Area = m.Groups[1].Value, Start = start, End = start + (width == "D" ? 32 : width == "W" ? 16 : width == "B" ? 8 : 1) - 1,
                Canonical = m.Groups[1].Value + width + (start / 8).ToString(CultureInfo.InvariantCulture) + (width.Length == 0 ? "." + start % 8 : "") };
        }
        internal static string NormalizeAddress(string value) { var r = Range(value); return r == null ? "" : r.Canonical; }
        internal static object Choices(IoSnapshot snapshot)
        {
            return new { scopes = new[] { "native", "mapped" }.Select(scope => new {
                scope, units = snapshot.rows.Where(r => InScope(r, scope) && Range(r.address) != null).GroupBy(r => N(r.unit), StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key).Select(g => new {
                    name = g.Key, count = g.Count(), tables = g.GroupBy(TableKey).OrderBy(t => t.Key).Select(t => new { key = t.Key, name = N(t.First().group) + " / " + N(t.First().table), count = t.Count() }).ToArray()
                }).ToArray()
            }).ToArray() };
        }
        private static IoReconcilePoint Model(Point p) { var r = p.Row; return new IoReconcilePoint { name = r.name, address = r.address, type = r.data_type, comment = r.comment, table = r.table, group = r.group, unit = r.unit, mapping = r.mapping_fc, references = r.references.Count }; }
        internal static IoReconcileReport Compare(IoSheetImport file, IoSnapshot snapshot, IoReconcileOptions options)
        {
            if (file == null || snapshot == null || options == null) throw new ArgumentException("缺少点表、PLC 快照或列对应关系。");
            var sheet = file.GetSheet(options.sheet);
            var columns = new[] { options.address, options.name, options.type, options.comment };
            if (options.address < 0 || options.name < 0 || columns.Any(c => c < -1 || c >= sheet.Columns) || columns.Where(c => c >= 0).Distinct().Count() != columns.Count(c => c >= 0))
                throw new ArgumentException("请分别选择地址和名称列；每个字段需使用不同的列。");
            if (!sheet.Rows.Any(r => r.Number == options.header_row)) throw new ArgumentException("表头行不存在。");
            if (options.scope != "native" && options.scope != "mapped") throw new ArgumentException("请选择原始变量表或映射点位。");
            var scope = snapshot.rows.Where(r => InScope(r, options.scope)).Select((r, i) => new Point { Row = r, Range = Range(r.address), Id = i }).Where(p => p.Range != null).ToArray();
            if (!scope.Any(p => Same(N(p.Row.unit), N(options.unit)))) throw new ArgumentException("当前范围没有该软件单元的显式 I/Q/M 地址，请重新选择范围。");
            var points = scope.Where(p => Same(N(p.Row.unit), N(options.unit)) && (string.IsNullOrEmpty(options.table) || TableKey(p.Row) == options.table)).ToArray();
            if (points.Length == 0 || points.Length > 50000) throw new ArgumentException("所选 PLC 范围为空或超过 50000 项，请调整变量表范围。");
            var result = new IoReconcileReport { id = Guid.NewGuid().ToString("N"), project_path = snapshot.project_path, plc_name = snapshot.plc_name, file_name = file.FileName,
                snapshot_started = snapshot.read_started_utc, snapshot_completed = snapshot.read_completed_utc, source_note = snapshot.reference_coverage, generated_at = DateTime.UtcNow.ToString("o"), options = options, plc_rows = points.Length,
                rows = new List<IoReconcileRow>(), warnings = new List<string>(snapshot.warnings ?? new List<string>()) };
            result.warnings.Add("只读对账；仅比较所选软件单元、变量表范围和列。PLC 仅列出项不等于多余或可删除，Excel 仅列出项不等于硬件点位未使用。");
            result.warnings.Add("使用已读取的时间区间快照，不是在线实际值。只比较显式 I/Q/M 位、字节、字和双字地址；DB/局部符号、未分配的硬件通道和外部设备占用不在范围内。");
            result.warnings.Add("包含隐藏行，不应用 Excel 筛选；公式、错误值和数据区合并单元格不参与匹配。数字单元格按原始值读取，不根据显示格式补 I/Q 前缀或前导零。");
            result.warnings.Add("地址范围重叠可能是有意设置的字节/位别名；引用条数仅供核对，不代表完整占用判断。");
            if (sheet.Hidden) result.warnings.Add("所选工作表在原文件中处于隐藏状态。");
            if (snapshot.tables_read < snapshot.tables_total) result.warnings.Add("PLC 变量表快照读取不完整，缺失项不能据此判定为工程中不存在。");
            if (options.type < 0) result.warnings.Add("未选择类型列，不检查类型差异。");
            if (options.comment < 0) result.warnings.Add("未选择注释列，不检查注释差异。");
            else result.warnings.Add("原始变量表的单语言注释可按正文比较，忽略显示用的语言前缀并保留原值；多语言注释不自动选择译文。");
            var ranges = new Dictionary<IoReconcileRow, AddressRange>();
            foreach (var source in sheet.Rows.Where(r => r.Number > options.header_row)) {
                var cells = columns.Select(c => sheet.Cell(source, c)).ToArray();
                if (cells.All(c => c.Value.Length == 0 && c.Error.Length == 0)) continue;
                var row = new IoReconcileRow { excel_row = source.Number, address = cells[0].Value, name = cells[1].Value, type = cells[2].Value, comment = cells[3].Value };
                foreach (var error in cells.Where(c => c.Error.Length > 0).Select(c => c.Error).Distinct()) row.issues.Add(error);
                var range = Range(row.address);
                if (range == null) row.issues.Add("地址无效：请使用 I0.0、Q0.0、IB0、QW0 等带区域的显式地址");
                if (string.IsNullOrWhiteSpace(row.name)) row.issues.Add("名称为空");
                if (row.issues.Count > 0) row.status = "invalid"; else ranges.Add(row, range);
                result.rows.Add(row);
            }
            result.excel_rows = result.rows.Count;
            if (result.excel_rows == 0) throw new ArgumentException("表头之后没有所选列的数据，请核对工作表、表头行和列对应关系。");
            var excelAddresses = ranges.GroupBy(p => p.Value.Canonical).ToDictionary(g => g.Key, g => g.Select(p => p.Key).ToArray());
            var excelNames = ranges.Keys.GroupBy(r => Name(r.name), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
            var duplicateAddresses = excelAddresses.Where(p => p.Value.Length > 1).ToDictionary(p => p.Key, p => Rows(p.Value));
            var duplicateNames = excelNames.Where(p => p.Value.Length > 1).ToDictionary(p => p.Key, p => Rows(p.Value), StringComparer.OrdinalIgnoreCase);
            var byAddress = points.ToLookup(p => p.Range.Canonical); var byName = points.ToLookup(p => Name(p.Row.name), StringComparer.OrdinalIgnoreCase);
            var plcIntervals = points.GroupBy(p => p.Range.Area).ToDictionary(g => g.Key, g => new IntervalIndex(g));
            var excelPoints = ranges.Values.GroupBy(r => r.Canonical).Select((g, i) => new Point { Range = g.First(), Id = i }).ToArray();
            var excelIntervals = excelPoints.GroupBy(p => p.Range.Area).ToDictionary(g => g.Key, g => new IntervalIndex(g));
            var matched = new HashSet<int>();
            foreach (var pair in ranges) {
                var row = pair.Key; var address = pair.Value; bool conflict = false;
                string duplicate;
                if (duplicateAddresses.TryGetValue(address.Canonical, out duplicate)) { row.issues.Add("Excel 中重复地址，涉及行：" + duplicate); conflict = true; }
                if (duplicateNames.TryGetValue(Name(row.name), out duplicate)) { row.issues.Add("Excel 中名称重复，涉及行：" + duplicate); conflict = true; }
                if (excelIntervals[address.Area].Overlaps(address).Any(p => p.Range.Canonical != address.Canonical)) { row.issues.Add("Excel 中存在字节/位地址范围重叠，请核对别名用途"); conflict = true; }
                var exact = byAddress[address.Canonical].ToArray(); var named = byName[Name(row.name)].ToArray();
                var candidates = exact.Length > 0 ? exact : named;
                if (exact.Length > 200 || named.Length > 200) throw new ArgumentException("单个点位关联超过 200 个 PLC 变量，请缩小变量表范围后对账。");
                IntervalIndex interval;
                var overlapping = plcIntervals.TryGetValue(address.Area, out interval) ? interval.Overlaps(address).Where(p => p.Range.Canonical != address.Canonical).Take(201).ToArray() : new Point[0];
                if (overlapping.Length > 0) { row.issues.Add("与 PLC 地址范围重叠，请核对字节/位别名"); conflict = true; }
                if (exact.Length > 0 && named.Any(p => p.Range.Canonical != address.Canonical)) { row.issues.Add("同名 PLC 变量还出现在其他地址，请核对是否串号"); conflict = true; }
                var evidence = candidates.Concat(overlapping).Concat(exact.Length > 0 ? named : new Point[0]).GroupBy(p => p.Id).Select(g => g.First()).ToArray();
                if (evidence.Length > 200) throw new ArgumentException("单个点位关联超过 200 个 PLC 变量，请缩小变量表范围后对账。");
                foreach (var p in evidence) { row.plc.Add(Model(p)); matched.Add(p.Id); }
                if (candidates.Length == 1) {
                    var target = candidates[0];
                    if (address.Canonical != target.Range.Canonical) row.issues.Add("同名变量地址不同");
                    if (!Same(Name(row.name), Name(target.Row.name))) row.issues.Add("同地址变量名称不同");
                    if (options.type >= 0 && Type(row.type) != Type(target.Row.data_type)) row.issues.Add("数据类型不同");
                    if (options.comment >= 0 && !SameComment(row.comment, target.Row)) row.issues.Add("注释不同");
                }
                if (conflict) row.status = "conflict";
                else if (candidates.Length > 1) { row.status = "ambiguous"; row.issues.Add("PLC 中有多个匹配，未自动选定变量"); }
                else if (candidates.Length == 0) { row.status = "excel_only"; row.issues.Add("所选 PLC 范围未找到相同地址或名称"); }
                else row.status = row.issues.Count == 0 ? "match" : "different";
            }
            foreach (var p in points.Where(p => !matched.Contains(p.Id))) result.rows.Add(new IoReconcileRow { status = "plc_only", plc = new List<IoReconcilePoint> { Model(p) }, issues = new List<string> { "所选 Excel 工作表未列出此变量，不代表工程多余" } });
            if (result.rows.Count > 50000 || result.rows.Sum(r => Math.Max(1, r.plc.Count)) > 100000) throw new ArgumentException("对账结果过大，请缩小工作表或 PLC 变量表范围。");
            result.counts = result.rows.GroupBy(r => r.status).ToDictionary(g => g.Key, g => g.Count());
            return result;
        }
        private static string Rows(IEnumerable<IoReconcileRow> rows)
        { var numbers = rows.Select(r => r.excel_row).ToArray(); return string.Join("、", numbers.Take(12)) + (numbers.Length > 12 ? " 等共 " + numbers.Length + " 行" : ""); }
    }
}
