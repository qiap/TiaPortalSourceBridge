using System;
using System.Collections.Generic;
using System.Linq;
using TiaPortalSourceBridge.Indexing;
using TiaPortalSourceBridge.Web;

internal static class ProgramAuditChecks
{
    static int checks;
    static void Check(bool ok, string why) { if (!ok) throw new Exception(why); checks++; }
    static string Access(int uid, string symbol, bool local = false) { return "<Access UId='" + uid + "' Scope='" + (local ? "LocalVariable" : "GlobalVariable") + "'><Symbol><Component Name='" + symbol + "'/></Symbol></Access>"; }
    static string Absolute(int uid, string area, int bit) { return "<Access UId='" + uid + "' Scope='GlobalVariable'><Address Area='" + area + "' Type='Bit' BitOffset='" + bit + "'/></Access>"; }
    static string Coil(int uid, string kind = "Coil") { return "<Part UId='" + uid + "' Name='" + kind + "'/>"; }
    static string Wire(int access, int part, string pin = "operand") { return "<Wire><IdentCon UId='" + access + "'/><NameCon UId='" + part + "' Name='" + pin + "'/></Wire>"; }
    static string Net(string parts, string wires) { return "<SW.Blocks.CompileUnit ID='10'><AttributeList><NetworkSource><FlgNet><Parts>" + parts + "</Parts><Wires>" + wires + "</Wires></FlgNet></NetworkSource></AttributeList></SW.Blocks.CompileUnit>"; }
    static string Doc(params string[] networks) { return "<Document><SW.Blocks.FB ID='0'><AttributeList><Name>Host</Name></AttributeList><ObjectList>" + string.Join("", networks) + "</ObjectList></SW.Blocks.FB></Document>"; }
    static IndexEntry Block(string name = "Host", string unit = "", string language = "LAD") { return new IndexEntry { id = name + unit, name = name, group = "Blocks", unit_name = unit, language = language }; }
    static IoRow Tag(string name, string address, string unit = "", string dataType = "Bool") { return new IoRow { id = name + unit, name = name, address = address, unit = unit, table = "Tags", group = "IO", data_type = dataType }; }
    static IoSnapshot Snapshot(params IndexEntry[] blocks) { return new IoSnapshot { project_path = "fixture.ap17", plc_name = "CPU", cylinderBlocks = blocks, cylinderProgramsTotal = blocks.Length, cylinderProgramsRead = blocks.Length, tables_total = 1, tables_read = 1, read_started_utc = "2026-11-08T00:00:00Z", read_completed_utc = "2026-11-08T00:01:00Z" }; }
    static AuditReport Run(IoSnapshot snapshot, string xml, params AuditTemplate[] templates) { return ProgramAudit.Run(snapshot, _ => xml, templates); }
    static List<AuditFinding> Rule(AuditReport report, string rule) { return report.findings.Where(f => f.rule == rule).ToList(); }
    static void Addresses()
    {
        var snapshot = Snapshot(); snapshot.rows.AddRange(new[] { Tag("Word", "%MW10", dataType: "Word"), Tag("Bit", "%M10.3"), Tag("Byte", "%MB11", dataType: "Byte"), Tag("Next", "%MB12", dataType: "Byte"), Tag("Input", "%I10.3"), Tag("Output", "%Q10.3"), Tag("OtherUnit", "%M10.3", "Unit2") });
        var report = Run(snapshot, ""); var overlap = Rule(report, "address_overlap");
        Check(overlap.Count == 2, "MW spans two bytes, excludes adjacent bytes, separate areas and units");
        Check(overlap.All(f => f.severity == "warning" && f.confidence == "confirmed" && f.locations.Count == 2), "overlap evidence states phenomenon, not a fatal error");
        snapshot.rows.Add(Tag("Alias", "%E010.3"));
        Check(Rule(Run(snapshot, ""), "address_overlap").Count == 3, "German alias and leading zero canonicalization");
        var mapped = Tag("Mapped", "%M10.3"); mapped.origin = "mapped"; mapped.mapping_fc = "Map"; snapshot.rows.Add(mapped);
        Check(Rule(Run(snapshot, ""), "address_overlap").Count == 3, "derived mapping rows never inflate native declaration overlap");
        snapshot.rows.Add(Tag("WidthUnknown", "%M10")); snapshot.rows.Add(Tag("Malformed", "%MW10.3"));
        report = Run(snapshot, ""); Check(report.warnings.Count(w => w.Contains("未识别的声明地址")) == 2, "unsupported width is not guessed from datatype");
        snapshot.rows.Add(snapshot.rows[0]); Check(Rule(Run(snapshot, ""), "address_overlap").Count == 3, "duplicate snapshot rows are not distinct declarations");
        var many = Snapshot(); for (int i = 0; i < 100; i++) many.rows.Add(Tag("Alias" + i, "%M0.0"));
        report = Run(many, ""); Check(report.findings.Count == ProgramAudit.MaxFindings && (bool)report.coverage["truncated"], "dense aliases have bounded output and explicit truncation");
        foreach (string dataType in new[] { "String[20]", "Array[0..7] of Byte", "\"CylinderType\"", "LReal", "Bool", "Int", "" }) {
            var invalid = Snapshot(); invalid.rows.AddRange(new[] { Tag("Unsupported", "%MD10", dataType: dataType), Tag("Bit", "%M10.0") });
            report = Run(invalid, "");
            Check(Rule(report, "address_overlap").Count == 0 && (int)report.coverage["addresses_checked"] == 1 && report.warnings.Any(w => w.Contains("声明类型不支持或与地址宽度不一致")), "unsupported or width-mismatched declaration is excluded: " + dataType);
        }
        var scalar = Snapshot(); scalar.rows.AddRange(new[] { Tag("Real", "%MD10", dataType: "Real"), Tag("Int", "%MW12", dataType: "Int"), Tag("Char", "%MB13", dataType: "Char") });
        Check(Rule(Run(scalar, ""), "address_overlap").Count == 3, "supported scalar widths include Real, Int and Char");
    }
    static void Writes()
    {
        var host = Block(); var snapshot = Snapshot(host); snapshot.rows.Add(Tag("Valve", "%Q2.0"));
        string two = Doc(Net(Access(1, "Valve") + Coil(2) + Coil(3), Wire(1, 2) + Wire(1, 3)));
        var report = Run(snapshot, two); var findings = Rule(report, "multiple_writes");
        Check(findings.Count == 1 && findings[0].locations.Count == 2, "two coils in same network retain distinct write locations");
        Check(findings[0].locations.All(l => l.network == "1" && l.address == "Q2.0"), "native symbol resolved to address with network number");
        string repeated = Doc(Net(Access(1, "Valve") + Coil(2), Wire(1, 2) + Wire(1, 2)));
        Check(Rule(Run(snapshot, repeated), "multiple_writes").Count == 0, "same endpoint repeated in XML is not two write sites");
        string alias = Doc(Net(Access(1, "Valve") + Absolute(4, "Q", 16) + Coil(2) + Coil(3), Wire(1, 2) + Wire(4, 3)));
        Check(Rule(Run(snapshot, alias), "multiple_writes").Single().locations.Count == 2, "absolute address and tag alias share write target");
        var badAlias = Snapshot(host); badAlias.rows.Add(Tag("Valve", "%Q2.0", dataType: "String[20]"));
        Check(Rule(Run(badAlias, alias), "multiple_writes").Count == 0, "unknown declaration width does not establish a physical address alias");
        badAlias.rows.Add(Tag("Valve", "%Q2.0"));
        Check(Rule(Run(badAlias, alias), "multiple_writes").Count == 0, "a valid duplicate declaration cannot hide another declaration with unknown width");
        var addressNamed = Snapshot(Block(language: "STL")); addressNamed.rows.Add(Tag("QB5", "%QB6", dataType: "Byte"));
        string namedWrite = "<StlStatement><StlToken Text='T'/>" + Access(1, "QB5") + "</StlStatement>";
        string absoluteWrite = "<StlStatement><StlToken Text='T'/>" + Absolute(2, "Q", 40).Replace("Type='Bit'", "Type='Byte'") + "</StlStatement>";
        report = Run(addressNamed, Doc(namedWrite, absoluteWrite));
        Check(Rule(report, "multiple_writes").Count == 0 && (int)report.coverage["supported_write_sites"] == 2, "tag QB5 declared at QB6 is distinct from absolute QB5");
        report = Run(addressNamed, Doc(namedWrite, absoluteWrite.Replace("BitOffset='40'", "BitOffset='48'")));
        Check(Rule(report, "multiple_writes").Single().subject == "QB6", "an address-like tag name resolves to its declared address");
        Check(Rule(Run(Snapshot(Block(language: "STL")), Doc(namedWrite, absoluteWrite)), "multiple_writes").Count == 0, "undeclared QB5 remains a symbol instead of being guessed as a physical address");
        snapshot.rows.Add(Tag("ValveAlias", "%A02.0"));
        string symbolicAlias = two.Replace("<Part UId='3'", Access(4, "ValveAlias") + "<Part UId='3'").Replace(Wire(1, 3), Wire(4, 3));
        Check(Rule(Run(snapshot, symbolicAlias), "multiple_writes").Count == 1, "distinct native tag names same physical output grouped");
        var a = Block("A"); var b = Block("B"); var locals = Snapshot(a, b);
        string local = Doc(Net(Access(1, "TempFlag", true) + Coil(2), Wire(1, 2)));
        Check(Rule(Run(locals, local), "multiple_writes").Count == 0, "same local name in two blocks is isolated");
        Check(Rule(Run(Snapshot(a), local.Replace(Coil(2), Coil(2) + Coil(3)).Replace(Wire(1, 2), Wire(1, 2) + Wire(1, 3))), "multiple_writes").Count == 1, "local writes within same block remain visible");
        var separated = Snapshot(Block("A", "U1"), Block("B", "U2"));
        Check(Rule(Run(separated, Doc(Net(Access(1, "State") + Coil(2), Wire(1, 2)))), "multiple_writes").Count == 0, "global symbols separate software units");
        string sr = two.Replace("UId='2' Name='Coil'", "UId='2' Name='SCoil'").Replace("UId='3' Name='Coil'", "UId='3' Name='RCoil'");
        Check(Rule(Run(snapshot, sr), "multiple_writes").Single().severity == "info", "set/reset-only sites get explicit review info");
        Check(Rule(Run(snapshot, sr.Replace("Name='RCoil'", "Name='Coil'")), "multiple_writes").Single().severity == "warning", "mixed ordinary/set writes stay warning");
        var ambiguous = Snapshot(host); ambiguous.rows.AddRange(new[] { Tag("Valve", "%Q2.0"), Tag("Valve", "%Q3.0") });
        report = Run(ambiguous, alias); Check(Rule(report, "multiple_writes").Count == 0 && report.warnings.Any(w => w.Contains("多个声明地址")), "ambiguous symbols do not claim definite physical aliases");
        string dynamic = two.Replace("<Component Name='Valve'/>", "<Component Name='Array' AccessModifier='Array'><Access Scope='LocalVariable'><Symbol><Component Name='Index'/></Symbol></Access></Component>");
        report = Run(snapshot, dynamic); Check(Rule(report, "multiple_writes").Count == 0 && report.warnings.Any(w => w.Contains("动态下标")), "dynamic indices are not collapsed to one definite target");
        foreach (bool isLocal in new[] { false, true }) {
            string sliced = Doc(Net(Access(1, "Status", isLocal).Replace("Name='Status'", "Name='Status' SliceAccessModifier='x0'") +
                Access(4, "Status", isLocal).Replace("Name='Status'", "Name='Status' SliceAccessModifier='x1'") + Coil(2) + Coil(3), Wire(1, 2) + Wire(4, 3)));
            report = Run(snapshot, sliced);
            Check(Rule(report, "multiple_writes").Count == 0 && (int)report.coverage["supported_write_sites"] == 0 && report.warnings.Any(w => w.Contains("切片目标")), "distinct " + (isLocal ? "local" : "global") + " slices are explicitly uncovered rather than merged to the base word");
            report = Run(snapshot, sliced.Replace("Name='Coil'", "Name='Contact'"));
            Check(!report.warnings.Any(w => w.Contains("切片目标")), "read-only slice contacts do not add write-coverage warnings");
        }
        string statement = "<StlStatement><StlToken Text='='/><Access Scope='GlobalVariable'><Symbol><Component Name='State'/></Symbol></Access></StlStatement>";
        report = Run(Snapshot(Block(language: "STL")), Doc(statement, statement));
        Check(Rule(report, "multiple_writes").Single().locations.Count == 2, "repeated STL statements retain distinct locations");
        Check((int)report.coverage["supported_write_sites"] == 2, "coverage exposes exact supported write count");
        string call = "<Call UId='3'><CallInfo Name='Drive' BlockType='FB'><Parameter Name='Output' Section='Output'/></CallInfo></Call>";
        report = Run(snapshot, Doc(Net(Access(1, "Valve") + Coil(2) + call, Wire(1, 2) + Wire(1, 3, "Output"))));
        Check(Rule(report, "multiple_writes").Single().locations.Any(l => l.evidence.Contains("调用参数 Drive.Output")), "FB output parameter is an explicit write with evidence");
    }
    static void Pins()
    {
        var host = Block(); var template = new AuditTemplate { host_id = host.id, fb = "Cylinder", sample_id = "Z01", parameters = new[] { "Sense", "Valve" } };
        string call = "<Call UId='10'><CallInfo Name='Cylinder' BlockType='FB'><Instance Scope='LocalVariable'><Component Name='Z02'/></Instance><Parameter Name='Sense' Section='Input'/><Parameter Name='Valve' Section='Output'/></CallInfo></Call>";
        string part = Access(1, "Sensor") + Access(2, "Output") + call;
        string good = Doc(Net(part, Wire(1, 10, "Sense") + Wire(2, 10, "Valve")));
        Check(Rule(Run(Snapshot(host), good, template), "template_pin").Count == 0, "connected matching template is not flagged");
        string open = good.Replace(Wire(1, 10, "Sense"), "<Wire><OpenCon UId='20'/><NameCon UId='10' Name='Sense'/></Wire>");
        var report = Run(Snapshot(host), open, template); var finding = Rule(report, "template_pin").Single();
        Check(finding.subject == "Host/Z02/Sense" && finding.locations[0].network == "1" && finding.explanation.Contains("模板一致性"), "open pin evidence identifies instance and does not infer safety role");
        Check(Rule(Run(Snapshot(host), good.Replace(Wire(2, 10, "Valve"), ""), template), "template_pin").Count == 1, "missing wire is reported");
        Check(Rule(Run(Snapshot(host), good.Replace("<Parameter Name='Valve' Section='Output'/>", ""), template), "template_pin").Count == 1, "missing declared parameter is reported");
        Check(Rule(Run(Snapshot(host), open.Replace("Name='Cylinder'", "Name='OtherFB'"), template), "template_pin").Count == 0, "template rules do not leak across FB definitions");
        Check(Rule(Run(Snapshot(Block("OtherHost")), open, template), "template_pin").Count == 0, "template rules are scoped to host");
        Check(Rule(Run(Snapshot(host), open), "template_pin").Count == 0 && Run(Snapshot(host), open).warnings.Any(w => w.Contains("没有可用")), "without templates explicitly no pin coverage");
        string expression = good.Replace(call, "<Part UId='99' Name='O'/>" + call).Replace(Wire(1, 10, "Sense"), "<Wire><NameCon UId='99' Name='out'/><NameCon UId='10' Name='Sense'/></Wire>");
        Check(Rule(Run(Snapshot(host), expression, template), "template_pin").Count == 0, "an expression connection is not mistaken for open pin");
        report = Run(Snapshot(host), expression.Replace("<Part UId='99' Name='O'/>", ""), template);
        Check(Rule(report, "template_pin").Count == 0 && report.warnings.Any(w => w.Contains("对端无法核对")), "dangling connection is explicitly unverified, not accepted or mistaken for open pin");
        report = Run(Snapshot(Block("OtherHost")), good, template);
        Check(report.warnings.Any(w => w.Contains("模板宿主不在")), "absent template host is explicit coverage gap");
    }
    static void SclIntegration()
    {
        var lad = Block("Ladder"); var scl = Block("Structured", language: "SCL"); var stl = Block("Statement", language: "STL");
        var snapshot = Snapshot(lad, scl, stl); snapshot.rows.Add(Tag("Valve", "%Q2.0"));
        string ladder = Doc(Net(Access(1, "Valve") + Coil(2), Wire(1, 2)));
        string structured = Doc("<SourceText>// Valve := FALSE;\nValve := TRUE;\n%Q2.0 := FALSE;</SourceText>");
        string statement = Doc("<StlStatement><StlToken Text='='/>", Absolute(1, "Q", 16), "</StlStatement>");
        var report = ProgramAudit.Run(snapshot, b => b == lad ? ladder : b == scl ? structured : statement, null);
        var write = Rule(report, "multiple_writes").Single();
        Check(write.subject == "Q2.0" && write.locations.Count == 4, "SCL, LAD and STL sites share declared physical alias identity");
        var locations = write.locations.Where(l => l.language == "SCL").ToArray();
        Check(locations.Length == 2 && locations.All(l => l.line > 0 && l.column > 0 && !string.IsNullOrEmpty(l.statement) && l.line_basis == "exported_text"), "SCL evidence retains statement and source location through grouping");
        Check((int)report.coverage["scl_regions"] == 1 && (int)report.coverage["scl_regions_analyzed"] == 1 && (int)report.coverage["scl_write_sites"] == 2 && (int)report.coverage["scl_partial_blocks"] == 0, "SCL coverage measures explicit write analysis separately from full control flow");
        Check(IndexJson.Serializer().Serialize(report).Contains("line_basis"), "source evidence is available in serialized reports");
        structured = Doc("<SourceText>#Same := TRUE;\n#Same := FALSE;</SourceText>");
        var localOther = Block("Other", language: "SCL");
        report = ProgramAudit.Run(Snapshot(scl, localOther), b => structured, null);
        Check(Rule(report, "multiple_writes").Count == 2 && Rule(report, "multiple_writes").All(f => f.locations.Count == 2), "SCL local write groups stay isolated by host");
        var otherUnit = Block("OtherUnit", "Unit2", "SCL");
        report = ProgramAudit.Run(Snapshot(scl, otherUnit), b => Doc("<SourceText>Valve := TRUE;</SourceText>"), null);
        Check(Rule(report, "multiple_writes").Count == 0, "SCL global writes remain isolated by software unit");
    }
    static void Coverage()
    {
        var snapshot = Snapshot(Block("Good"), Block("Bad"), Block("Text", language: "SCL")); snapshot.cylinderProgramsTotal = 5; snapshot.tables_total = 2;
        var report = ProgramAudit.Run(snapshot, b => b.name == "Bad" ? "<broken>" : b.name == "Text" ? Doc("<SourceText>#x := 1;</SourceText>") : Doc(Net(Access(1, "X") + Coil(2), Wire(1, 2))), new List<AuditTemplate>());
        Check((int)report.coverage["blocks_parsed"] == 2 && report.warnings.Any(w => w.Contains("Bad")), "malformed source is isolated and visible");
        Check(report.warnings.Any(w => w.Contains("读取不完整")) && (int)report.coverage["unsupported_blocks"] == 1, "incomplete source and SCL coverage explicitly reported");
        Check(report.snapshot_started == snapshot.read_started_utc && report.snapshot_completed == snapshot.read_completed_utc && report.project_path == snapshot.project_path && report.plc_name == snapshot.plc_name, "source snapshot target and timestamps retained");
        Check(report.findings.Count == 0 && ((string[])report.coverage["limitations"]).Any(s => s.Contains("无发现不代表")), "empty findings never claims safety");
        Check(snapshot.rows.Count == 0 && snapshot.cylinderProgramsTotal == 5, "analysis leaves input snapshot unchanged");
        string json = IndexJson.Serializer().Serialize(report); Check(json.Contains("\"coverage\"") && json.Contains("\"findings\""), "report DTO round-trips through project serializer");
    }
    static int Main()
    {
        try { Addresses(); Writes(); Pins(); SclIntegration(); Coverage(); Console.WriteLine("PASS: " + checks + " program audit checks; offline evidence only, no TIA connection or project writes."); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message); return 1; }
    }
}
