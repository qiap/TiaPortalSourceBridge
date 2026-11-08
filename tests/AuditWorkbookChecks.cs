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
    private static int Main(string[] args)
    {
        try { Workbook(args.Length == 0 ? null : args[0]); EmptyAndLimits(); Console.WriteLine("PASS: " + checks + " audit workbook checks; no TIA connection or project changes."); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message); return 1; }
    }
}
