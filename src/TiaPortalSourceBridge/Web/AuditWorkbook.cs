using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using System.Xml;

namespace TiaPortalSourceBridge.Web
{
    // Evidence is always exported as text: even a source name beginning with '='
    // must never become an Excel formula or an external workbook relationship.
    internal static class AuditWorkbook
    {
        private const string Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        internal const int MaxRows = 1048576, MaxCellLength = 32767;
        private static readonly Dictionary<string, string> CoverageLabels = new Dictionary<string, string> {
            { "blocks_total", "程序块总数" }, { "blocks_read", "已读取程序块数" }, { "blocks_parsed", "已解析程序块数" },
            { "programs_total", "PLC 程序总数" }, { "programs_read", "已读取 PLC 程序数" },
            { "tag_tables_total", "变量表总数" }, { "tag_tables_read", "已读取变量表数" },
            { "native_tags", "原生变量声明数" }, { "templates", "有效项目模板数" }, { "supported_write_sites", "已识别写入位置数" },
            { "scl_regions", "SCL 源码区域数" }, { "scl_regions_analyzed", "已分析 SCL 区域数" },
            { "scl_write_sites", "已识别 SCL 写入位置数" }, { "scl_partial_blocks", "SCL 写入识别不完整的块数" },
            { "addresses_checked", "已检查地址数" }, { "template_calls_checked", "已检查模板调用数" },
            { "unsupported_blocks", "部分或全部行为未覆盖的块数" }, { "source_mode", "快照来源" }, { "source_note", "源码读取范围" },
            { "truncated", "报告是否达到结果上限" }, { "limitations", "检查限制" }
        };
        private static string Rule(string value)
        {
            return value == "address_overlap" ? "地址区间重叠" : value == "multiple_writes" ? "多处写入" :
                value == "template_pin" ? "模板要求连接的参数" : value == "instance_reuse" ? "同一实例多处调用" :
                value == "no_ob_path" ? "未发现 OB 调用路径" : value;
        }
        private static string Severity(string value) { return value == "warning" ? "需核对" : value == "info" ? "提示" : value; }
        private static string Confidence(string value) { return value == "confirmed" ? "现象已确认" : value == "review" ? "需人工核对" : value; }
        private static string Text(object value)
        {
            if (value == null) return "";
            if (value is string) return (string)value;
            if (value is bool) return (bool)value ? "是" : "否";
            if (value is IDictionary) return new JavaScriptSerializer().Serialize(value);
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }
        internal static string Clean(string value)
        {
            value = value ?? "";
            if (value.Length > MaxCellLength) throw new InvalidOperationException("体检报告单元格超过 Excel 的 32767 字符限制，未生成截断的报告。");
            var clean = new StringBuilder();
            for (int i = 0; i < value.Length; i++) {
                if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) { clean.Append(value[i]); clean.Append(value[++i]); }
                else if (XmlConvert.IsXmlChar(value[i])) clean.Append(value[i]);
            }
            return clean.ToString();
        }
        internal static void CheckRowCount(long rows)
        { if (rows > MaxRows) throw new InvalidOperationException("体检报告超过 Excel 单表行数上限，未生成不完整的报告。"); }
        private static XmlWriter Writer(ZipArchive zip, string path)
        { return XmlWriter.Create(zip.CreateEntry(path).Open(), new XmlWriterSettings { Encoding = new UTF8Encoding(false), CloseOutput = true, NewLineHandling = NewLineHandling.Entitize }); }
        private static string Column(int value)
        { string name = ""; for (value++; value > 0; value = (value - 1) / 26) name = (char)('A' + (value - 1) % 26) + name; return name; }
        private static void Sheet(ZipArchive zip, int number, IEnumerable<string[]> rows, double[] widths)
        {
            using (var writer = Writer(zip, "xl/worksheets/sheet" + number + ".xml")) {
                writer.WriteStartElement("worksheet", Ns);
                writer.WriteStartElement("sheetViews"); writer.WriteStartElement("sheetView"); writer.WriteAttributeString("workbookViewId", "0");
                writer.WriteStartElement("pane"); writer.WriteAttributeString("ySplit", "1"); writer.WriteAttributeString("topLeftCell", "A2"); writer.WriteAttributeString("activePane", "bottomLeft"); writer.WriteAttributeString("state", "frozen"); writer.WriteEndElement(); writer.WriteEndElement(); writer.WriteEndElement();
                writer.WriteStartElement("cols");
                for (int i = 0; i < widths.Length; i++) {
                    writer.WriteStartElement("col"); writer.WriteAttributeString("min", (i + 1).ToString()); writer.WriteAttributeString("max", (i + 1).ToString());
                    writer.WriteAttributeString("width", widths[i].ToString(CultureInfo.InvariantCulture)); writer.WriteAttributeString("customWidth", "1"); writer.WriteEndElement();
                }
                writer.WriteEndElement(); writer.WriteStartElement("sheetData");
                long count = 0; int columns = 0;
                foreach (var row in rows) {
                    CheckRowCount(++count); columns = Math.Max(columns, row.Length);
                    writer.WriteStartElement("row"); writer.WriteAttributeString("r", count.ToString(CultureInfo.InvariantCulture));
                    for (int i = 0; i < row.Length; i++) {
                        writer.WriteStartElement("c"); writer.WriteAttributeString("r", Column(i) + count.ToString(CultureInfo.InvariantCulture)); writer.WriteAttributeString("t", "inlineStr"); writer.WriteAttributeString("s", count == 1 ? "1" : "0");
                        writer.WriteStartElement("is"); writer.WriteStartElement("t"); writer.WriteAttributeString("xml", "space", null, "preserve"); writer.WriteString(Clean(row[i]));
                        writer.WriteEndElement(); writer.WriteEndElement(); writer.WriteEndElement();
                    }
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();
                if (count > 0 && columns > 0) { writer.WriteStartElement("autoFilter"); writer.WriteAttributeString("ref", "A1:" + Column(columns - 1) + count.ToString(CultureInfo.InvariantCulture)); writer.WriteEndElement(); }
                writer.WriteEndElement();
            }
        }
        private static IEnumerable<string[]> Overview(AuditReport report, IList<AuditFinding> findings)
        {
            yield return new[] { "项目", "内容" };
            yield return new[] { "报告编号", report.id };
            yield return new[] { "报告格式", Text(report.schema) };
            yield return new[] { "工程", report.project_path };
            yield return new[] { "PLC", report.plc_name };
            yield return new[] { "源码快照开始 UTC", report.snapshot_started };
            yield return new[] { "源码快照完成 UTC", report.snapshot_completed };
            yield return new[] { "体检生成时间 UTC", report.generated_at };
            yield return new[] { "发现数量", findings.Count.ToString(CultureInfo.InvariantCulture) };
            yield return new[] { "需核对", findings.Count(f => f.severity == "warning").ToString(CultureInfo.InvariantCulture) };
            yield return new[] { "提示", findings.Count(f => f.severity == "info").ToString(CultureInfo.InvariantCulture) };
            yield return new[] { "导出范围", "本次报告的全部发现和定位证据，不受页面搜索、筛选或分页影响。" };
            yield return new[] { "检查性质", "基于源码快照的只读静态检查；未修改博途工程，未下载或操作 PLC。" };
            yield return new[] { "结论边界", "未发现问题不代表程序逻辑安全或已经完成全部检查。地址重叠、多处写入可能是设计允许的别名或优先级逻辑，请结合实际执行条件核对。" };
            if ((report.coverage != null && report.coverage.ContainsKey("scl_regions")) || findings.Any(f => (f.locations ?? new List<AuditLocation>()).Any(l => l.language == "SCL"))) {
                yield return new[] { "SCL / 定位说明", "行号对应所在源码区域的导出文本或重建 SCL 文本，不是博途编辑器的绝对行号；请结合程序块、网络及语句定位。" };
                yield return new[] { "SCL / 检查边界", "仅识别受支持的显式写入位置，不同分支的写入不代表同时执行；完整 SCL/GRAPH 控制流与 Temp 先写后读尚未检查。" };
            }
            foreach (var item in report.coverage ?? new Dictionary<string, object>()) {
                string label; if (!CoverageLabels.TryGetValue(item.Key, out label)) label = item.Key;
                if (item.Key == "source_mode" && (Text(item.Value) == "local_cache" || Text(item.Value) == "tia_readback")) {
                    yield return new[] { "覆盖 / " + label, Text(item.Value) == "local_cache" ? "已读取的本地源码快照" : "从博途读取的源码快照" };
                    continue;
                }
                var values = item.Value as IEnumerable;
                if (values != null && !(item.Value is string) && !(item.Value is IDictionary)) {
                    bool any = false;
                    foreach (object value in values) { any = true; yield return new[] { "覆盖 / " + label, Text(value) }; }
                    if (!any) yield return new[] { "覆盖 / " + label, "无" };
                } else yield return new[] { "覆盖 / " + label, Text(item.Value) };
            }
            foreach (string warning in report.warnings ?? new List<string>()) yield return new[] { "读取或检查说明", warning };
            if (report.call_graph != null) {
                var graph = report.call_graph;
                yield return new[] { "调用关系 / 程序块数", Text((graph.nodes ?? new List<AuditCallNode>()).Count) };
                yield return new[] { "调用关系 / 已识别调用数", Text(graph.calls_total) };
                yield return new[] { "调用关系 / 已定位目标调用数", Text(graph.calls_resolved) };
                yield return new[] { "调用关系 / 已识别实例调用数", Text(graph.instances_resolved) };
                yield return new[] { "调用关系 / 覆盖不完整", Text(graph.partial) };
                yield return new[] { "调用关系 / 结果达到上限", Text(graph.truncated) };
                yield return new[] { "调用关系 / 导出范围", "报告中保留的全部调用位置与全部节点的已识别 OB 路径，不受页面筛选影响。路径按步骤分行；每个节点仅展示一条已识别路径，不代表唯一调用路径或实际执行顺序。" };
                yield return new[] { "调用关系 / 结论边界", "未找到 OB 路径不等于死代码；共享实例可能是有意设计。未解析调用、源码缺失或截断会使调用关系不完整。" };
                foreach (string limitation in graph.limitations ?? new List<string>()) yield return new[] { "调用关系 / 检查限制", limitation };
            }
        }
        private static IEnumerable<string[]> Findings(IList<AuditFinding> findings)
        {
            yield return new[] { "发现编号", "检查规则", "等级", "证据可信度", "问题", "对象", "说明", "建议", "证据条数" };
            foreach (var finding in findings) yield return new[] { finding.id, Rule(finding.rule), Severity(finding.severity), Confidence(finding.confidence),
                finding.title, finding.subject, finding.explanation, finding.suggestion, (finding.locations == null ? 0 : finding.locations.Count).ToString(CultureInfo.InvariantCulture) };
        }
        private static IEnumerable<string[]> Evidence(IList<AuditFinding> findings)
        {
            yield return new[] { "发现编号", "程序块", "组路径", "软件单元", "网络", "位置", "符号", "地址", "读写或关系", "证据", "语言", "区域内行号", "列号", "行号依据", "语句" };
            foreach (var finding in findings) foreach (var location in finding.locations ?? new List<AuditLocation>())
                yield return new[] { finding.id, location.block, location.group, location.unit, location.network, location.location, location.symbol, location.address, location.access, location.evidence,
                    location.language, location.line > 0 ? Text(location.line) : "", location.column > 0 ? Text(location.column) : "", LineBasis(location.line_basis), location.statement };
        }
        private static string LineBasis(string value)
        { return value == "exported_text" ? "导出文本行" : value == "reconstructed_scl" ? "重建 SCL 行" : value; }
        private static string PathStatus(string value)
        {
            return value == "root" ? "OB 入口" : value == "reachable" ? "存在已识别的 OB 调用路径" :
                value == "no_ob_path" ? "已识别调用中未发现 OB 路径" : value == "unknown" ? "尚未核对" : value;
        }
        private static string SourceStatus(string value)
        { return value == "parsed" ? "已解析" : value == "unsupported" ? "未完整覆盖" : value == "unread" ? "未读取" : value; }
        private static string Resolution(string value)
        { return value == "resolved" ? "已定位" : value == "external" ? "外部或系统目标" : value == "ambiguous" ? "目标存在歧义" : value == "unresolved" ? "未定位" : value; }
        private static string InstanceStatus(string value)
        { return value == "resolved" ? "已识别" : value == "unsupported" ? "实例形式未覆盖" : value == "none" ? "无实例" : value; }
        private static string InstanceScope(string value)
        { return value == "global" || value == "GlobalVariable" ? "全局背景 DB" : value == "local" || value == "LocalVariable" ? "宿主内多重实例" : value; }
        private static AuditCallNode Node(IDictionary<string, AuditCallNode> nodes, string id)
        { AuditCallNode node; return id != null && nodes.TryGetValue(id, out node) ? node : null; }
        private static IEnumerable<string[]> Calls(AuditCallGraph graph, IDictionary<string, AuditCallNode> nodes)
        {
            yield return new[] { "调用编号", "调用方编号", "调用方", "调用方组路径", "调用方软件单元", "被调用编号", "被调用程序块", "调用中目标名称", "类型", "被调用组路径", "被调用软件单元", "目标解析", "实例", "实例作用域", "实例识别", "网络", "位置", "符号", "地址", "关系类型", "证据" };
            foreach (var call in graph.calls ?? new List<AuditCallSite>()) {
                var caller = Node(nodes, call.caller_id); var callee = Node(nodes, call.callee_id); var location = call.location ?? new AuditLocation();
                yield return new[] { call.id, call.caller_id, caller == null ? location.block : caller.name, caller == null ? location.group : caller.group,
                    caller == null ? location.unit : caller.unit, call.callee_id, callee == null ? call.callee_name : callee.name, call.callee_name, call.callee_type,
                    callee == null ? "" : callee.group, callee == null ? "" : callee.unit, Resolution(call.resolution), call.instance, InstanceScope(call.instance_scope),
                    InstanceStatus(call.instance_status), location.network, location.location, location.symbol, location.address, location.access, location.evidence };
            }
        }
        private static IEnumerable<string[]> Paths(AuditCallGraph graph, IDictionary<string, AuditCallNode> nodes)
        {
            yield return new[] { "程序块编号", "程序块", "组路径", "软件单元", "类型", "源码状态", "OB 路径状态", "入口 OB 编号", "入口 OB", "路径步骤", "路径节点编号", "路径程序块", "路径组路径", "路径软件单元", "路径节点类型", "说明" };
            foreach (var node in graph.nodes ?? new List<AuditCallNode>()) {
                var root = Node(nodes, node.root_id); var path = node.path ?? new List<string>();
                for (int i = 0; i < Math.Max(path.Count, 1); i++) {
                    string stepId = path.Count == 0 ? "" : path[i]; var step = Node(nodes, stepId);
                    yield return new[] { node.id, node.name, node.group, node.unit, node.kind, SourceStatus(node.source_status), PathStatus(node.status),
                        node.root_id, root == null ? "" : root.name, path.Count == 0 ? "" : Text(i + 1), stepId,
                        step == null ? (stepId.Length == 0 ? "" : "未在报告节点中定位") : step.name,
                        step == null ? "" : step.group, step == null ? "" : step.unit, step == null ? "" : step.kind, node.note };
                }
            }
        }
        internal static byte[] Create(AuditReport report)
        {
            if (report == null) throw new ArgumentNullException("report");
            var findings = report.findings ?? new List<AuditFinding>();
            if (findings.Any(f => f == null || (f.locations != null && f.locations.Any(l => l == null)))) throw new InvalidOperationException("体检报告包含不完整的发现或证据，无法导出。");
            CheckRowCount((long)findings.Count + 1);
            CheckRowCount(findings.Sum(f => f.locations == null ? 0L : f.locations.Count) + 1);
            var graph = report.call_graph;
            Dictionary<string, AuditCallNode> nodes = null;
            if (graph != null) {
                var graphNodes = graph.nodes ?? new List<AuditCallNode>(); var calls = graph.calls ?? new List<AuditCallSite>();
                if (graphNodes.Any(n => n == null || string.IsNullOrEmpty(n.id) || n.path != null && n.path.Any(p => p == null)) || calls.Any(c => c == null))
                    throw new InvalidOperationException("调用关系包含不完整节点、路径或调用，无法导出。");
                if (graphNodes.Select(n => n.id).Distinct(StringComparer.Ordinal).Count() != graphNodes.Count)
                    throw new InvalidOperationException("调用关系节点编号重复，无法可靠定位路径。");
                nodes = graphNodes.ToDictionary(n => n.id, StringComparer.Ordinal);
                CheckRowCount((long)calls.Count + 1);
                CheckRowCount(graphNodes.Sum(n => Math.Max(n.path == null ? 0L : n.path.Count, 1L)) + 1);
            }
            string[] names = graph == null ? new[] { "概览", "发现清单", "定位证据" } : new[] { "概览", "发现清单", "定位证据", "调用关系", "OB调用路径" };
            using (var memory = new MemoryStream()) {
                using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true)) {
                    using (var writer = Writer(zip, "[Content_Types].xml")) {
                        writer.WriteStartElement("Types", "http://schemas.openxmlformats.org/package/2006/content-types");
                        foreach (var type in new[] { new[] { "rels", "application/vnd.openxmlformats-package.relationships+xml" }, new[] { "xml", "application/xml" } }) {
                            writer.WriteStartElement("Default"); writer.WriteAttributeString("Extension", type[0]); writer.WriteAttributeString("ContentType", type[1]); writer.WriteEndElement();
                        }
                        foreach (var type in new[] { new[] { "/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml" }, new[] { "/xl/styles.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml" } }
                            .Concat(Enumerable.Range(1, names.Length).Select(n => new[] { "/xl/worksheets/sheet" + n + ".xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml" }))) {
                            writer.WriteStartElement("Override"); writer.WriteAttributeString("PartName", type[0]); writer.WriteAttributeString("ContentType", type[1]); writer.WriteEndElement();
                        }
                        writer.WriteEndElement();
                    }
                    using (var writer = Writer(zip, "_rels/.rels")) { writer.WriteStartElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships"); Relationship(writer, "rId1", "officeDocument", "xl/workbook.xml"); writer.WriteEndElement(); }
                    using (var writer = Writer(zip, "xl/workbook.xml")) {
                        writer.WriteStartElement("workbook", Ns); writer.WriteStartElement("sheets");
                        for (int i = 1; i <= names.Length; i++) { writer.WriteStartElement("sheet"); writer.WriteAttributeString("name", names[i - 1]); writer.WriteAttributeString("sheetId", i.ToString()); writer.WriteAttributeString("r", "id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships", "rId" + i); writer.WriteEndElement(); }
                        writer.WriteEndElement(); writer.WriteEndElement();
                    }
                    using (var writer = Writer(zip, "xl/_rels/workbook.xml.rels")) {
                        writer.WriteStartElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships");
                        for (int i = 1; i <= names.Length; i++) Relationship(writer, "rId" + i, "worksheet", "worksheets/sheet" + i + ".xml");
                        Relationship(writer, "rId" + (names.Length + 1), "styles", "styles.xml"); writer.WriteEndElement();
                    }
                    using (var writer = Writer(zip, "xl/styles.xml")) writer.WriteRaw("<styleSheet xmlns=\"" + Ns + "\"><fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><color rgb=\"FFFFFFFF\"/><name val=\"Calibri\"/></font></fonts><fills count=\"3\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF1E293B\"/><bgColor indexed=\"64\"/></patternFill></fill></fills><borders count=\"1\"><border/></borders><cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs><cellXfs count=\"2\"><xf numFmtId=\"49\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyAlignment=\"1\"><alignment vertical=\"top\" wrapText=\"1\"/></xf><xf numFmtId=\"49\" fontId=\"1\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyAlignment=\"1\"><alignment vertical=\"top\" wrapText=\"1\"/></xf></cellXfs><cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>");
                    Sheet(zip, 1, Overview(report, findings), new double[] { 30, 100 });
                    Sheet(zip, 2, Findings(findings), new double[] { 24, 24, 14, 20, 42, 32, 65, 65, 14 });
                    Sheet(zip, 3, Evidence(findings), new double[] { 24, 28, 35, 25, 12, 45, 42, 20, 22, 75, 12, 16, 12, 22, 85 });
                    if (graph != null) {
                        Sheet(zip, 4, Calls(graph, nodes), new double[] { 24, 24, 28, 30, 24, 24, 28, 28, 12, 30, 24, 20, 35, 24, 22, 12, 40, 35, 20, 20, 70 });
                        Sheet(zip, 5, Paths(graph, nodes), new double[] { 24, 28, 30, 24, 12, 18, 35, 24, 28, 12, 24, 28, 30, 24, 12, 70 });
                    }
                }
                return memory.ToArray();
            }
        }
        private static void Relationship(XmlWriter writer, string id, string type, string target)
        { writer.WriteStartElement("Relationship"); writer.WriteAttributeString("Id", id); writer.WriteAttributeString("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/" + type); writer.WriteAttributeString("Target", target); writer.WriteEndElement(); }
    }
}
