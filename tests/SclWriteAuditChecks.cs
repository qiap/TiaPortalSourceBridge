using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Xml;
using TiaPortalSourceBridge.Indexing;
using TiaPortalSourceBridge.Web;

internal static class SclWriteAuditChecks
{
    private static int checks;
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
    private static string X(string value) { return SecurityElement.Escape(value ?? ""); }
    private static XmlDocument Parse(string xml) { var document = new XmlDocument { XmlResolver = null }; document.LoadXml(xml); return document; }
    private static string Doc(string kind, string name, string source = "", string declarations = "", string instanceOf = "")
    {
        return "<Document><SW.Blocks." + kind + "><AttributeList><Name>" + X(name) + "</Name><Interface><Sections>" + declarations + "</Sections></Interface>" +
            (instanceOf.Length == 0 ? "" : "<InstanceOfName>" + X(instanceOf) + "</InstanceOfName><InstanceOfType>FB</InstanceOfType>") +
            "</AttributeList><ObjectList>" + source + "</ObjectList></SW.Blocks." + kind + "></Document>";
    }
    private static string Raw(string text) { return "<SourceText>" + X(text) + "</SourceText>"; }
    private static string Net(string xml) { return "<SW.Blocks.CompileUnit><AttributeList><NetworkSource><StructuredText xmlns='http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v3'>" + xml + "</StructuredText></NetworkSource></AttributeList></SW.Blocks.CompileUnit>"; }
    private static string Access(string name, string scope = "GlobalVariable") { return "<Access Scope='" + scope + "'><Symbol><Component Name='" + X(name) + "'/></Symbol></Access>"; }
    private static string Token(string text) { return "<Token Text='" + X(text) + "'/>"; }
    private static string Section(string name, params string[] members) { return "<Section Name='" + name + "'>" + string.Join("", members) + "</Section>"; }
    private static string Member(string name, string type = "Bool") { return "<Member Name='" + X(name) + "' Datatype='" + X(type) + "'/>"; }
    private sealed class Fixture
    {
        internal readonly List<IndexEntry> Blocks = new List<IndexEntry>();
        internal readonly Dictionary<string, string> Sources = new Dictionary<string, string>();
        internal IndexEntry Add(string name, string source = "", string kind = "FC", string declarations = "", string instanceOf = "", string unit = "", string ns = "", string language = "SCL")
        {
            var entry = new IndexEntry { id = "block" + Blocks.Count, kind = "plc.block", name = name, language = language, group = "Fixture", unit_name = unit, object_namespace = ns };
            Blocks.Add(entry); Sources[entry.id] = Doc(kind, name, source, declarations, instanceOf); return entry;
        }
        internal SclAuditResult Run(IndexEntry block) { return new SclWriteAudit(Blocks, b => Sources[b.id]).Analyze(block, Parse(Sources[block.id])); }
    }
    private static void Lexical()
    {
        var f = new Fixture(); var block = f.Add("Main", Raw("// bogus := 1;\n(* misleading := 2; (* nested := 3; *) *)\n\"DB Data\".\"中文 点位\" := 'a$' fake := 9;';\n#local := 'can''t := 4;';\n%q0005.1 := TRUE;\n\"QB5\" := FALSE;"));
        var r = f.Run(block);
        Check(r.writes.Count == 4, "comments and single-quoted literals never fabricate assignments");
        Check(r.writes[0].symbol == "\"DB Data\".\"中文 点位\"", "quoted symbols preserve literal member identity");
        Check(r.writes[1].symbol == "#local", "local scope marker retained");
        Check(r.writes[2].symbol == "%Q5.1", "explicit absolute address normalized");
        Check(r.writes[3].symbol == "QB5", "address-looking quoted name remains a symbolic tag");
        Check(r.writes[0].line == 3 && r.writes[0].column == 1 && r.writes[0].line_basis == "exported_text", "raw text line basis and coordinates retained");
        Check(r.writes.All(w => w.access == "writes" && w.language == "SCL" && w.statement.Contains(":=")), "writes carry source language, direction and original statement");
        Check(!r.partial && r.present && r.regions == 1 && r.regions_analyzed == 1, "fully handled literal body has complete explicit-write coverage");
        f.Sources[block.id] = Doc("FC", "Main", Raw(new string(' ', 15000) + "\n%AW0004 := 5;\narr[0001,-02].field := 2;")); r = f.Run(block);
        Check(r.writes.Count == 2 && r.writes[0].symbol == "%QW4", "source beyond 12k is read in full and German area normalized");
        Check(r.writes[1].symbol == "arr[1,-2].field", "static numeric array indices normalized without truncating the symbol");
        f.Sources[block.id] = Doc("FC", "Main", Raw("IF #condition THEN #x := 1; ELSE #x := 2; END_IF;\nFOR #i := 1 TO 5 DO #x := #i; END_FOR;")); r = f.Run(block);
        Check(r.writes.Count == 4 && r.writes.Count(w => w.symbol == "#x") == 3, "branch and loop writes retained as distinct static sites");
        Check(r.writes.Select(w => w.location).Distinct().Count() == 4, "same target branch sites retain distinct positions");
        f.Sources[block.id] = Doc("FC", "Main", Raw("FUNCTION \"Main\" : Void\nVAR_TEMP\n  local : Int := 0;\nEND_VAR\nBEGIN\n#local := 1;\nEND_FUNCTION")); r = f.Run(block);
        Check(r.writes.Count == 1 && r.writes[0].line == 6, "declaration initializer excluded from executable writes");
        f.Sources[block.id] = Doc("FC", "Main", Raw("FUNCTION Main : Void BEGIN #x := 1; END_FUNCTION FUNCTION Other : Void BEGIN #x := 2; END_FUNCTION")); r = f.Run(block);
        Check(r.partial && r.writes.Count == 0 && r.regions_analyzed == 0, "multiple raw program definitions never share a false block identity");
        f.Sources[block.id] = Doc("FC", "Main", Raw("FUNCTION Other : Void BEGIN #x := 2; END_FUNCTION")); r = f.Run(block);
        Check(r.partial && !r.writes.Any(), "raw definition name must match snapshot identity");
        f.Sources[block.id] = Doc("FC", "Main", Raw("VAR_Count := 1;")); r = f.Run(block);
        Check(r.writes.Count == 1 && !r.partial, "identifier with VAR_ prefix is not a declaration keyword");
        f.Sources[block.id] = Doc("FC", "Main", Raw("FUNCTION Main : Void BEGIN #x := 2;")); r = f.Run(block);
        Check(r.partial && !r.writes.Any(), "full source needs a matching program terminator");
        f.Sources[block.id] = Doc("FC", "Main", Raw("DATA_BLOCK Data VAR x : Int := 1; END_VAR BEGIN x := 2; END_DATA_BLOCK")); r = f.Run(block);
        Check(r.partial && !r.writes.Any(), "DB start values are not runtime assignments");
    }
    private static void Structured()
    {
        var f = new Fixture();
        string member = "<Access Scope='GlobalVariable'><Symbol><Component Name='Data'/><Token Text='.'/><Component Name='Value'/></Symbol></Access>";
        var block = f.Add("Native", Net(Token("// comment") + "<NewLine Num='3'/><Blank Num='9'/>" + member + Token(":=") + "<Access Scope='TypedConstant'><Constant><ConstantValue>UDINT#1</ConstantValue></Constant></Access>" + Token(";")) + Net(Access("local", "LocalVariable") + Token(":=") + "<Access Scope='LocalConstant'><Constant Name='TIMES'/></Access>" + Token(";")));
        var r = f.Run(block);
        Check(r.writes.Count == 2 && r.writes[0].symbol == "Data.Value", "native member separator Token is supported");
        Check(r.writes[0].line == 4 && r.writes[0].column == 10 && r.writes[0].network == "1", "Num counts in native Blank and NewLine determine precise location");
        Check(r.writes[1].line == 1 && r.writes[1].network == "2" && r.writes[1].statement.Contains("#TIMES"), "native network line reset and named constant retained");
        Check(r.writes.All(w => w.line_basis == "reconstructed_scl") && !r.partial, "native token reconstruction is honestly labeled");
        f.Sources[block.id] = Doc("FC", block.name, Net(Access("result", "LocalVariable") + Token(":=") + "<Access Scope='Call'><Instruction Name='ROL'><TemplateValue Name='value_type' Type='Type'>Int</TemplateValue>" + Token("(") + "<Parameter Name='IN'>" + Token(":=") + Access("value", "LocalVariable") + "</Parameter>" + Token(")") + "</Instruction></Access>" + Token(";")));
        r = f.Run(block);
        Check(r.writes.Count == 1 && r.writes[0].symbol == "#result", "generic instruction metadata does not erase a known assignment");
        Check(r.partial && r.writes[0].statement.Contains("ROL(IN:="), "unknown builtin call is a coverage gap while named input never becomes a write");
        f.Sources[block.id] = Doc("FC", block.name, Net("<LineComment><Text>fake := 1;</Text></LineComment><NewLine Num='1'/>" + Access("real") + Token(":=") + Token("TRUE;") + "<UnknownWrite/>") + Net("")); r = f.Run(block);
        Check(r.writes.Count == 1 && r.writes[0].symbol == "real", "native line comments cannot create a write");
        Check(r.partial && r.regions == 2 && r.regions_analyzed == 2, "unknown native element visible and empty network is analyzed");
        f.Sources[block.id] = Doc("FC", block.name, Net("<Access Scope='GlobalVariable'><Symbol><Component Name='Data'><Access Scope='LocalVariable'><Symbol><Component Name='i'/></Symbol></Access></Component></Symbol></Access>" + Token(":=1;")));
        r = f.Run(block); Check(r.partial && r.writes.Count == 0, "unsupported native dynamic access never degrades to Data prefix");
        f.Sources[block.id] = Doc("FC", block.name, "<Graph>" + Net(Access("graphWrite") + Token(":=1;")) + "</Graph>"); r = f.Run(block);
        Check(!r.present && r.partial && r.writes.Count == 0, "GRAPH inner SCL is explicitly excluded");
        var lad = f.Add("Lad", Raw("comment := 'text';"), language: "LAD"); r = f.Run(lad);
        Check(!r.present && r.writes.Count == 0 && r.partial, "untyped SourceText from LAD is never guessed as SCL");
    }
    private static void Calls()
    {
        string pins = Section("Input", Member("input")) + Section("Output", Member("output")) + Section("InOut", Member("shared")) + Section("Return", Member("result"));
        var f = new Fixture(); var caller = f.Add("Caller", Raw("Calc(input := #source, output => \"Data\".result, shared := #state, result => %Q0.0);"));
        f.Add("Calc", declarations: pins); var r = f.Run(caller);
        Check(r.writes.Count == 3 && !r.writes.Any(w => w.symbol == "input" || w.symbol == "output" || w.symbol == "shared"), "named parameters never counted as ordinary variable writes");
        Check(r.writes.Single(w => w.symbol == "#state").access == "reads_writes", "verified InOut actual contributes reads_writes");
        Check(r.writes.Single(w => w.symbol == "Data.result").evidence.Contains("Output"), "verified Output actual contributes write evidence");
        Check(r.writes.Single(w => w.symbol == "%Q0.0").evidence.Contains("Return"), "verified Return actual contributes write evidence");
        Check(r.writes.All(w => w.statement.StartsWith("Calc(") && w.statement.EndsWith(")")) && !r.partial, "full original call is retained as evidence");
        f.Sources[caller.id] = Doc("FC", caller.name, Raw("Unknown(input := #a, output => #b);\n#known := 1;")); r = f.Run(caller);
        Check(r.writes.Count == 1 && r.writes[0].symbol == "#known" && r.partial, "unknown callee cannot fabricate parameter writes but later assignments survive");
        f.Sources[caller.id] = Doc("FC", caller.name, Raw("Calc(input := Calc(input := #a, output => #b, shared := #c), output => #d, shared := #e);")); r = f.Run(caller);
        Check(r.writes.Select(w => w.symbol).OrderBy(s => s).SequenceEqual(new[] { "#b", "#c", "#d", "#e" }), "nested calls have independent verified parameter direction");
        f.Sources[caller.id] = Doc("FC", caller.name, Raw("Calc(input => #a, output := #b, shared => #c);")); r = f.Run(caller);
        Check(r.writes.Count == 0 && r.partial, "connection operator must agree with declared direction");
        f.Sources[caller.id] = Doc("FC", caller.name, Raw("Calc(output => Data[#i].value, shared := #pointer^);")); r = f.Run(caller);
        Check(r.writes.Count == 0 && r.partial, "dynamic and pointer call actuals do not report a symbol prefix");
        f.Sources[caller.id] = Doc("FC", caller.name, Raw("#instances[#i](output => #x);")); r = f.Run(caller);
        Check(r.writes.Count == 0 && r.partial, "unsupported dynamic call with only output arrow is visible");
        f.Sources[caller.id] = Doc("FC", caller.name, Raw("Calc(#a, #b);")); r = f.Run(caller);
        Check(r.writes.Count == 0 && r.partial, "positional call binding explicitly outside coverage");
        f.Sources[caller.id] = Doc("FC", caller.name, Raw("Calc(shared := #x, shared := #y);")); r = f.Run(caller);
        Check(r.writes.Count == 0 && r.partial, "duplicate connections do not invent two parameter writes");

        var scoped = new Fixture(); var host = scoped.Add("Host", Raw("Calc(shared := #x);"), unit: "A", ns: "Line");
        scoped.Add("Calc", declarations: pins, unit: "A", ns: "Line"); scoped.Add("Calc", declarations: Section("Input", Member("shared")), unit: "B", ns: "Line");
        scoped.Add("Calc", declarations: Section("Input", Member("shared")), unit: "A", ns: "Other"); r = scoped.Run(host);
        Check(r.writes.Count == 1 && !r.partial, "verified interface stays within unit and namespace");
        scoped.Sources[host.id] = Doc("FC", "Host", Raw("Other.Calc(shared := #x);")); r = scoped.Run(host);
        Check(r.writes.Count == 0 && !r.partial, "explicit qualified callee selects exact namespace input direction");
        scoped.Add("Calc", declarations: pins, unit: "A", ns: "Line"); scoped.Sources[host.id] = Doc("FC", "Host", Raw("Calc(shared := #x);")); r = scoped.Run(host);
        Check(r.writes.Count == 0 && r.partial, "ambiguous callee never resolves by first match");
        var unread = new Fixture(); var u = unread.Add("Caller", Raw("Calc(shared := #x);")); unread.Add("Calc", declarations: pins); var dupe = unread.Add("Calc", declarations: pins); unread.Sources[dupe.id] = null; r = unread.Run(u);
        Check(r.writes.Count == 0 && r.partial, "unread duplicate catalog declaration preserves ambiguity");
    }
    private static void Instances()
    {
        string pins = Section("InOut", Member("state"));
        var f = new Fixture(); var host = f.Add("Host", Raw("#one(state := #local); Instance(state := Data.state);"), kind: "FB", declarations: Section("Static", Member("one", "\"Pump\"")));
        f.Add("Pump", kind: "FB", declarations: pins); f.Add("Instance", kind: "InstanceDB", instanceOf: "Pump"); var r = f.Run(host);
        Check(r.writes.Count == 2 && !r.partial, "local static FB instance and global InstanceDB resolve through declarations");
        f.Sources[host.id] = Doc("FB", "Host", Raw("#missing(state := #x); Pump(state := #y);"), Section("Static", Member("one", "\"Pump\""))); r = f.Run(host);
        Check(r.writes.Count == 0 && r.partial, "undeclared local and direct FB type are not guessed as callable instances");
        f.Sources[host.id] = Doc("FB", "Host", Raw("#one(state := #x);"), Section("Static", Member("one", "Bool"))); r = f.Run(host);
        Check(r.writes.Count == 0 && r.partial, "static scalar is not mistaken for FB instance");
        f.Sources[host.id] = Doc("FB", "Host", Raw("#one(state := #x);"), Section("Static", Member("one", "Pump"), Member("one", "Pump"))); r = f.Run(host);
        Check(r.writes.Count == 0 && r.partial, "duplicate static declaration invalidates instance identity");
        var g = new Fixture(); var gHost = g.Add("Main", Raw("Instance(state := #x);")); g.Add("Pump", kind: "FB", declarations: pins); g.Add("Instance", kind: "GlobalDB", instanceOf: "Pump"); r = g.Run(gHost);
        Check(r.writes.Count == 0 && r.partial, "GlobalDB never accepted as InstanceDB even if it contains misleading metadata");
    }
    private static void RejectionAndLimits()
    {
        var f = new Fixture(); var block = f.Add("Bad");
        foreach (var text in new[] { "arr[#i] := 1;", "pointer^ := 1;", "data.%X0 := 1;", "%I0.8 := 1;", "%DB1.DBX0.0 := TRUE;", "broken := ;", "broken := 1", "x := y := 1;", "x := 'unterminated", "(* missing", "FC(x := 1;", "x := 1; )" }) {
            f.Sources[block.id] = Doc("FC", block.name, Raw(text)); var r = f.Run(block);
            Check(r.writes.Count == 0 && r.partial, "unsupported/malformed form is explicit and never shortened into a write: " + text);
        }
        f.Sources[block.id] = Doc("FC", block.name, Raw(new string(' ', SclWriteAudit.MaxRegionCharacters + 1) + "x := 1;")); var limit = f.Run(block);
        Check(limit.truncated && limit.partial && limit.writes.Count == 0, "oversize region rejected before a partial tail can look complete");
        f.Sources[block.id] = Doc("FC", block.name, Raw("x := '" + new string('a', SclWriteAudit.MaxStatementCharacters + 10) + "';")); limit = f.Run(block);
        Check(limit.writes.Count == 1 && limit.truncated && limit.writes[0].statement.Contains("截断"), "long evidence carries explicit truncation marker");
        f.Sources[block.id] = Doc("FC", block.name, string.Concat(Enumerable.Repeat(Net(""), SclWriteAudit.MaxRegions + 1))); limit = f.Run(block);
        Check(limit.regions == SclWriteAudit.MaxRegions + 1 && limit.regions_analyzed == SclWriteAudit.MaxRegions && limit.truncated, "region cap reports full region count and bounded analyzed count");
        var many = new Fixture(); var host = many.Add("Caller", Raw("Calc(shared := #unknown); #certain := 1;"));
        many.Add("Calc", declarations: Section("InOut", Member("shared")));
        for (int i = 2; i < SclWriteAudit.MaxBlocks; i++) many.Add("Empty" + i);
        many.Add("Calc", declarations: Section("Input", Member("shared")));
        limit = many.Run(host);
        Check(limit.truncated && limit.writes.Count == 1 && limit.writes[0].symbol == "#certain", "truncated identity catalog forbids call direction claims but retains direct writes");
    }
    public static int Main()
    {
        try { Lexical(); Structured(); Calls(); Instances(); RejectionAndLimits(); Console.WriteLine("PASS: " + checks + " SCL explicit write and coverage checks"); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
