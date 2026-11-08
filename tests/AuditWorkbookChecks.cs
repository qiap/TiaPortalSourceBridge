using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml;
using TiaPortalSourceBridge.Web;

internal static class AuditWorkbookChecks
{
    private static int checks;
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
    private static void Reject(Action action, string message) { bool rejected = false; try { action(); } catch (ArgumentException) { rejected = true; } catch (InvalidOperationException) { rejected = true; } Check(rejected, message); }
    private static XmlDocument Read(ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(path); Check(entry != null, "package entry exists: " + path);
        var doc = new XmlDocument { XmlResolver = null };
        using (var stream = entry.Open()) using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) doc.Load(reader);
        return doc;
    }
    private static string[] CellTexts(XmlDocument doc) { return doc.SelectNodes("//*[local-name()='c']/*[local-name()='is']/*[local-name()='t']").Cast<XmlNode>().Select(n => n.InnerText).ToArray(); }
    private static AuditReport Fixture()
    {
        return new AuditReport { schema = 1, id = "report-fixture", project_path = "E:\\生产 & 备份\\Project.ap17", plc_name = "CPU<1513>",
            snapshot_started = "2026-11-08T04:00:00Z", snapshot_completed = "2026-11-08T04:01:00Z", generated_at = "2026-11-08T04:02:00Z",
            warnings = new List<string> { "SCL 读写和动态地址未完整覆盖", "报告达到上限，结果不完整" },
            coverage = new Dictionary<string, object> { { "blocks_total", 12 }, { "blocks_read", 11 }, { "blocks_parsed", 10 }, { "native_tags", 20 }, { "templates", 1 },
                { "source_mode", "local_cache" }, { "truncated", true }, { "limitations", new[] { "不检查硬件通道", "仅报告支持的显式写入" } } },
            findings = new List<AuditFinding> {
                new AuditFinding { id = "finding-1", rule = "address_overlap", severity = "warning", confidence = "confirmed", title = "地址区间重叠", subject = "=HYPERLINK(\"https://example.invalid\",\"not a formula\")",
                    explanation = "MW10 与 M10.3 重叠，可能是允许的别名", suggestion = "核对设计意图", locations = new List<AuditLocation> {
                        new AuditLocation { block = "Tags", group = "IO & 设备", unit = "", network = "", location = "变量表", symbol = "+阀名", address = "%MW10", access = "declaration", evidence = "<tag>\"阀\" & '线'\n第二行\t保留\u0001\ud800" },
                        new AuditLocation { block = "Tags", group = "IO", unit = "", network = "", location = "变量表", symbol = "@输入", address = "%M10.3", access = "declaration", evidence = "-10" } } },
                new AuditFinding { id = "finding-2", rule = "multiple_writes", severity = "info", confidence = "review", title = "置位与复位位置", subject = "Q52.0",
                    explanation = "S/R 仍需核对执行条件", suggestion = "查看调用顺序", locations = new List<AuditLocation> { new AuditLocation { block = "Cylinder", group = "Station", unit = "", network = "3", location = "网络 3", symbol = "Valve", address = "%Q52.0", access = "writes", evidence = "native pin SCoil.operand" } } }
            }
        };
    }
    private static void Workbook(string output)
    {
        var report = Fixture(); byte[] bytes = AuditWorkbook.Create(report);
        if (!string.IsNullOrEmpty(output)) File.WriteAllBytes(output, bytes);
        using (var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read)) {
            Check(zip.Entries.Count == 8, "complete three-sheet minimal OOXML package");
            foreach (var entry in zip.Entries) if (entry.FullName.EndsWith(".xml") || entry.FullName.EndsWith(".rels")) Read(zip, entry.FullName);
            var workbook = Read(zip, "xl/workbook.xml");
            Check(workbook.SelectNodes("//*[local-name()='sheet']").Cast<XmlNode>().Select(n => n.Attributes["name"].Value).SequenceEqual(new[] { "概览", "发现清单", "定位证据" }), "sheets have review-oriented names");
            foreach (var rel in Read(zip, "xl/_rels/workbook.xml.rels").SelectNodes("//*[local-name()='Relationship']").Cast<XmlNode>())
                Check(rel.Attributes["TargetMode"] == null && zip.GetEntry("xl/" + rel.Attributes["Target"].Value) != null, "all workbook relationships stay internal and resolve");
            var overview = Read(zip, "xl/worksheets/sheet1.xml"); var overviewText = CellTexts(overview);
            Check(overviewText.Contains(report.project_path) && overviewText.Contains(report.plc_name) && overviewText.Contains(report.snapshot_completed), "target identity and exact snapshot included");
            Check(overviewText.Contains("覆盖 / 快照来源") && overviewText.Contains("已读取的本地源码快照"), "snapshot origin is understandable without internal field names");
            Check(overviewText.Contains("不检查硬件通道") && overviewText.Contains("仅报告支持的显式写入") && overviewText.Contains("报告达到上限，结果不完整"), "coverage arrays and report warnings exported without loss");
            Check(overviewText.Any(s => s.Contains("未发现问题不代表")) && overviewText.Any(s => s.Contains("不受页面搜索")), "scope and zero-finding interpretation explicitly stated");
            var findings = Read(zip, "xl/worksheets/sheet2.xml"); var evidence = Read(zip, "xl/worksheets/sheet3.xml");
            Check(findings.SelectNodes("//*[local-name()='row']").Count == 3 && evidence.SelectNodes("//*[local-name()='row']").Count == 4, "all findings and evidence exported, not just a page");
            Check(CellTexts(findings).Contains(report.findings[0].subject) && CellTexts(evidence).Contains("+阀名") && CellTexts(evidence).Contains("@输入") && CellTexts(evidence).Contains("-10"), "formula-looking strings preserve exact visible content");
            Check(CellTexts(evidence).Contains("<tag>\"阀\" & '线'\n第二行\t保留"), "XML escaping, multiline text and illegal character cleanup");
            Check(CellTexts(evidence).Count(s => s == "finding-1") == 2 && CellTexts(evidence).Count(s => s == "finding-2") == 1, "evidence rows remain joined to their discovery IDs");
            foreach (var doc in new[] { overview, findings, evidence }) {
                Check(doc.SelectNodes("//*[local-name()='f' or local-name()='hyperlink']").Count == 0 && doc.SelectNodes("//*[local-name()='c']").Cast<XmlNode>().All(c => c.Attributes["t"].Value == "inlineStr"), "all cells are explicit text with no formulas or links");
                Check(doc.SelectSingleNode("//*[local-name()='pane' and @state='frozen']") != null && doc.SelectSingleNode("//*[local-name()='autoFilter']") != null, "large report navigation has frozen header and filters");
            }
        }
    }
    private static void EmptyAndLimits()
    {
        var empty = new AuditReport { id = "empty", warnings = new List<string>(), coverage = new Dictionary<string, object> { { "limitations", new[] { "没有可用模板" } } } };
        using (var zip = new ZipArchive(new MemoryStream(AuditWorkbook.Create(empty)), ZipArchiveMode.Read)) {
            Check(CellTexts(Read(zip, "xl/worksheets/sheet1.xml")).Contains("没有可用模板"), "zero findings retain missing-coverage evidence");
            Check(Read(zip, "xl/worksheets/sheet2.xml").SelectNodes("//*[local-name()='row']").Count == 1, "zero-finding sheet has a usable header");
        }
        Check(AuditWorkbook.Clean(new string('中', AuditWorkbook.MaxCellLength)).Length == AuditWorkbook.MaxCellLength, "maximum legal Excel cell remains complete");
        Reject(() => AuditWorkbook.Clean(new string('x', AuditWorkbook.MaxCellLength + 1)), "overlong cells rejected rather than silently truncating evidence");
        Check(AuditWorkbook.Clean("气缸\ud83d\ude80\ud800\u0000").Equals("气缸\ud83d\ude80"), "valid surrogate pair preserved while invalid XML chars removed");
        AuditWorkbook.CheckRowCount(AuditWorkbook.MaxRows); Check(true, "maximum Excel row count accepted");
        Reject(() => AuditWorkbook.CheckRowCount((long)AuditWorkbook.MaxRows + 1), "Excel row limit overflow rejected");
        Reject(() => AuditWorkbook.Create(null), "missing report rejected");
        var report = Fixture(); report.findings[0].explanation = new string('x', AuditWorkbook.MaxCellLength + 1);
        Reject(() => AuditWorkbook.Create(report), "whole export fails if evidence cannot fit without truncation");
        report = Fixture(); report.findings[0].locations.Add(null);
        Reject(() => AuditWorkbook.Create(report), "missing evidence object rejected");
    }
    private static AuditReport GraphFixture()
    {
        var report = Fixture(); report.schema = 2;
        report.findings.Add(new AuditFinding { id = "reuse", rule = "instance_reuse", severity = "info", confidence = "review", title = "同一实例多处调用" });
        report.findings.Add(new AuditFinding { id = "path", rule = "no_ob_path", severity = "info", confidence = "review", title = "未发现调用路径" });
        report.call_graph = new AuditCallGraph { partial = true, truncated = true, calls_total = 4, calls_resolved = 2, instances_resolved = 1,
            limitations = new List<string> { "SCL 调用未完整识别", "调用关系达到上限，结果不完整" },
            nodes = new List<AuditCallNode> {
                new AuditCallNode { id = "ob", name = "Main", kind = "OB", unit = "A", group = "循环", status = "root", source_status = "parsed", root_id = "ob", path = new List<string> { "ob" }, note = "循环任务入口" },
                new AuditCallNode { id = "fb-a", name = "Cylinder", kind = "FB", unit = "A", group = "设备", status = "reachable", source_status = "parsed", root_id = "ob", path = new List<string> { "ob", "fb-a" }, note = "展示一条已识别路径" },
                new AuditCallNode { id = "fb-b", name = "Cylinder", kind = "FB", unit = "B", group = "设备", status = "no_ob_path", source_status = "parsed", root_id = "", path = new List<string>(), note = "缺少路径不等于死代码" },
                new AuditCallNode { id = "unknown", name = "=Unknown", kind = "FC", unit = "A", group = "外部 & 库", status = "unknown", source_status = "unread", path = null, note = "未读取源码 <请核对>" },
                new AuditCallNode { id = "unknown-path", name = "NoTarget", kind = "FC", unit = "A", status = "unknown", source_status = "unsupported", root_id = "not-in-report", path = new List<string> { "not-in-report" }, note = "路径节点未在报告内" }
            },
            calls = new List<AuditCallSite> {
                new AuditCallSite { id = "call-1", caller_id = "ob", callee_id = "fb-a", callee_name = "Cylinder", callee_type = "FB", instance = "=HYPERLINK(\"x\")", instance_scope = "global", instance_status = "resolved", resolution = "resolved", location = new AuditLocation { network = "2", location = "网络 2 / 调用 1", symbol = "@DB & x", evidence = "<CallInfo>\n重复实例待核对" } },
                new AuditCallSite { id = "call-2", caller_id = "fb-a", callee_id = "ob", callee_name = "Main", callee_type = "FC", instance = "", instance_scope = "", instance_status = "none", resolution = "resolved", location = new AuditLocation { network = "3", location = "循环回边", evidence = "不递归展开" } },
                new AuditCallSite { id = "call-3", caller_id = "unknown", callee_id = "", callee_name = "Missing<&>", callee_type = "FB", instance = "#Timer", instance_scope = "local", instance_status = "unsupported", resolution = "ambiguous", location = new AuditLocation { network = "4", location = "网络 4", evidence = "同名目标，未自动选择" } },
                new AuditCallSite { id = "call-4", caller_id = "missing-caller", callee_id = "", callee_name = "External", callee_type = "FC", instance_status = "none", resolution = "external", location = new AuditLocation { block = "CallerFallback", group = "FallbackGroup", unit = "FallbackUnit", evidence = "未在快照内解析" } }
            } };
        return report;
    }
    private static void CallGraphWorkbook(string output)
    {
        var report = GraphFixture(); var bytes = AuditWorkbook.Create(report);
        if (!string.IsNullOrEmpty(output)) File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(output), Path.GetFileNameWithoutExtension(output) + "-calls.xlsx"), bytes);
        using (var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read)) {
            Check(zip.Entries.Count == 10, "new report contains complete five-sheet package");
            var workbook = Read(zip, "xl/workbook.xml");
            Check(workbook.SelectNodes("//*[local-name()='sheet']").Cast<XmlNode>().Select(n => n.Attributes["name"].Value).SequenceEqual(new[] { "概览", "发现清单", "定位证据", "调用关系", "OB调用路径" }), "new sheets coexist with legacy findings and evidence");
            var rels = Read(zip, "xl/_rels/workbook.xml.rels").SelectNodes("//*[local-name()='Relationship']").Cast<XmlNode>().ToArray();
            Check(rels.Length == 6 && rels.Select(r => r.Attributes["Id"].Value).Distinct().Count() == 6, "five sheet and style relationships use unique IDs");
            Check(rels.All(r => r.Attributes["TargetMode"] == null && zip.GetEntry("xl/" + r.Attributes["Target"].Value) != null), "new workbook relationships all remain internal and resolve");
            var types = Read(zip, "[Content_Types].xml");
            Check(types.SelectNodes("//*[local-name()='Override']").Cast<XmlNode>().Count(n => n.Attributes["ContentType"].Value.EndsWith("worksheet+xml")) == 5, "content types include all five sheets");
            var overview = CellTexts(Read(zip, "xl/worksheets/sheet1.xml"));
            Check(overview.Contains("调用关系 / 覆盖不完整") && overview.Contains("调用关系 / 结果达到上限") && overview.Contains("SCL 调用未完整识别") && overview.Contains("调用关系达到上限，结果不完整"), "call coverage and truncation are explicit and complete");
            Check(overview.Any(s => s.Contains("每个节点仅展示一条")) && overview.Any(s => s.Contains("未找到 OB 路径不等于死代码")), "path semantics and incomplete-graph meaning are exported");
            var findings = CellTexts(Read(zip, "xl/worksheets/sheet2.xml"));
            Check(findings.Contains("同一实例多处调用") && findings.Contains("未发现 OB 调用路径"), "new rules have readable Chinese labels");
            var calls = Read(zip, "xl/worksheets/sheet4.xml"); var paths = Read(zip, "xl/worksheets/sheet5.xml");
            var callTexts = CellTexts(calls); var pathTexts = CellTexts(paths);
            Check(calls.SelectNodes("//*[local-name()='row']").Count == report.call_graph.calls.Count + 1, "all known and unresolved calls exported");
            Check(paths.SelectNodes("//*[local-name()='row']").Count == 7, "path steps expand into rows and pathless nodes retain one row");
            Check(callTexts.Contains("=HYPERLINK(\"x\")") && callTexts.Contains("@DB & x") && callTexts.Contains("Missing<&>") && callTexts.Contains("<CallInfo>\n重复实例待核对"), "call names and evidence preserve formula-like and XML-sensitive text");
            Check(callTexts.Contains("全局背景 DB") && callTexts.Contains("宿主内多重实例") && callTexts.Contains("目标存在歧义") && callTexts.Contains("外部或系统目标") && callTexts.Contains("实例形式未覆盖"), "instance scope and unresolved statuses remain readable");
            Check(callTexts.Contains("FallbackGroup") && callTexts.Contains("FallbackUnit") && callTexts.Contains("CallerFallback"), "call location identity retained when caller node absent");
            Check(pathTexts.Contains("fb-a") && pathTexts.Contains("fb-b") && pathTexts.Contains("A") && pathTexts.Contains("B"), "same-name nodes preserve separate unit and ID identities");
            Check(pathTexts.Contains("OB 入口") && pathTexts.Contains("存在已识别的 OB 调用路径") && pathTexts.Contains("已识别调用中未发现 OB 路径") && pathTexts.Contains("尚未核对"), "root, reachable, not-found and unknown nodes retain exact meaning");
            Check(pathTexts.Contains("未读取") && pathTexts.Contains("未完整覆盖") && pathTexts.Contains("未在报告节点中定位") && pathTexts.Contains("not-in-report"), "missing sources and path-node references are never silently dropped");
            foreach (var doc in new[] { calls, paths }) {
                Check(doc.SelectNodes("//*[local-name()='f' or local-name()='hyperlink']").Count == 0 && doc.SelectNodes("//*[local-name()='c']").Cast<XmlNode>().All(c => c.Attributes["t"].Value == "inlineStr"), "call and path cells are explicit text without formulas or links");
                Check(doc.SelectSingleNode("//*[local-name()='pane' and @state='frozen']") != null && doc.SelectSingleNode("//*[local-name()='autoFilter']") != null, "call and path sheets retain frozen headers and filtering");
            }
        }
        var empty = new AuditReport { call_graph = new AuditCallGraph() };
        using (var zip = new ZipArchive(new MemoryStream(AuditWorkbook.Create(empty)), ZipArchiveMode.Read)) {
            Check(Read(zip, "xl/worksheets/sheet4.xml").SelectNodes("//*[local-name()='row']").Count == 1 && Read(zip, "xl/worksheets/sheet5.xml").SelectNodes("//*[local-name()='row']").Count == 1, "empty current graph still has two usable sheets");
        }
        report = GraphFixture(); report.call_graph.nodes[0].name = new string('中', 500); report.call_graph.nodes[1].path = Enumerable.Repeat("ob", 100).ToList();
        using (var zip = new ZipArchive(new MemoryStream(AuditWorkbook.Create(report)), ZipArchiveMode.Read))
            Check(Read(zip, "xl/worksheets/sheet5.xml").SelectNodes("//*[local-name()='row']").Count == 105, "long paths split across rows instead of overflowing a joined cell");
        report = GraphFixture(); report.call_graph.nodes[0].note = new string('x', AuditWorkbook.MaxCellLength + 1);
        Reject(() => AuditWorkbook.Create(report), "overlong graph evidence fails instead of disappearing");
        report = GraphFixture(); report.call_graph.nodes.Add(report.call_graph.nodes[0]);
        Reject(() => AuditWorkbook.Create(report), "duplicate graph node identities rejected");
        report = GraphFixture(); report.call_graph.calls.Add(null);
        Reject(() => AuditWorkbook.Create(report), "missing call object rejected");
        report = GraphFixture(); report.call_graph.nodes[0].path.Add(null);
        Reject(() => AuditWorkbook.Create(report), "missing path step rejected");
        report = GraphFixture(); var manySteps = Enumerable.Repeat("n0", 1024).ToList();
        report.call_graph.nodes = Enumerable.Range(0, 1024).Select(i => new AuditCallNode { id = "n" + i, path = manySteps }).ToList();
        Reject(() => AuditWorkbook.Create(report), "expanded path rows enforce Excel sheet row limit before writing");
    }
    private static int Main(string[] args)
    {
        try { Workbook(args.Length == 0 ? null : args[0]); EmptyAndLimits(); CallGraphWorkbook(args.Length == 0 ? null : args[0]); Console.WriteLine("PASS: " + checks + " audit workbook checks; no TIA connection or project changes."); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message); return 1; }
    }
}
