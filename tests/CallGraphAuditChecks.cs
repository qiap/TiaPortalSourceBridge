using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using TiaPortalSourceBridge.Indexing;
using TiaPortalSourceBridge.Web;

internal static class CallGraphAuditChecks
{
    private static int checks;
    private static void Check(bool value, string why) { if (!value) throw new Exception(why); checks++; }
    private static string X(string value) { return SecurityElement.Escape(value ?? ""); }
    private static string Doc(string kind, string name, string content = "", string members = "", string instanceOf = "")
    {
        return "<Document><SW.Blocks." + kind + "><AttributeList><Name>" + X(name) + "</Name>" +
            (instanceOf.Length == 0 ? "" : "<InstanceOfName>" + X(instanceOf) + "</InstanceOfName><InstanceOfType>FB</InstanceOfType>") +
            "<Interface><Sections><Section Name='Static'>" + members + "</Section></Sections></Interface></AttributeList><ObjectList>" + content + "</ObjectList></SW.Blocks." + kind + "></Document>";
    }
    private static string Net(params string[] calls) { return "<SW.Blocks.CompileUnit><AttributeList><NetworkSource><FlgNet><Parts>" + string.Join("", calls) + "</Parts><Wires/></FlgNet></NetworkSource></AttributeList></SW.Blocks.CompileUnit>"; }
    private static string Instance(string name, string scope = "LocalVariable", string extra = "") { return "<Instance Scope='" + scope + "'><Component Name='" + X(name) + "'" + extra + "/></Instance>"; }
    private static string Call(string name, string type = "FC", string instance = "", string ns = "") { return "<Call><CallInfo Name='" + X(name) + "' BlockType='" + type + "'" + (ns.Length == 0 ? "" : " Namespace='" + X(ns) + "'") + ">" + instance + "</CallInfo></Call>"; }
    private static string Part(string name, string instance) { return "<Part Name='" + name + "'>" + instance + "</Part>"; }
    private static string Member(string name, string type) { return "<Member Name='" + X(name) + "' Datatype='" + X(type) + "'/>"; }
    private sealed class Fixture
    {
        internal readonly List<IndexEntry> Blocks = new List<IndexEntry>();
        internal readonly Dictionary<string, string> Sources = new Dictionary<string, string>();
        internal readonly List<AuditFinding> Findings = new List<AuditFinding>();
        internal readonly List<string> Warnings = new List<string>();
        internal IndexEntry Add(string kind, string name, string body = "", string members = "", string unit = "", string instanceOf = "", string language = "LAD", string ns = "")
        {
            var block = new IndexEntry { id = "id" + Blocks.Count, name = name, kind = "plc.block", group = "Group" + Blocks.Count, unit_name = unit, language = kind.EndsWith("DB") ? "DB" : language, object_namespace = ns };
            Blocks.Add(block); Sources[block.id] = Doc(kind, name, body, members, instanceOf); return block;
        }
        internal AuditCallGraph Run(bool incomplete = false)
        {
            Findings.Clear(); Warnings.Clear();
            var snapshot = new IoSnapshot { project_path = "offline.ap17", plc_name = "Station/CPU", cylinderBlocks = Blocks.ToArray(), cylinderProgramsRead = Blocks.Count, cylinderProgramsTotal = Blocks.Count + (incomplete ? 1 : 0) };
            return CallGraphAudit.Run(snapshot, b => Sources[b.id], Findings.Add, Warnings.Add);
        }
        internal AuditCallNode Node(AuditCallGraph graph, IndexEntry entry) { return graph.nodes.Single(n => n.id == entry.id); }
    }
    private static void Paths()
    {
        var f = new Fixture();
        IndexEntry root = f.Add("OB", "Cycle", Net(Call("A"))), startup = f.Add("OB", "PowerOn", Net(Call("C")));
        IndexEntry a = f.Add("FC", "A", Net(Call("B"))), b = f.Add("FC", "B", Net(Call("A"))), c = f.Add("FC", "C"), orphan = f.Add("FC", "Main");
        f.Add("GlobalDB", "Data");
        var graph = f.Run();
        Check(graph.nodes.Count == 6 && graph.nodes.Count(n => n.status == "root") == 2, "all native OBs, including non-OB1 names, become roots; DBs excluded");
        Check(f.Node(graph, a).path.SequenceEqual(new[] { root.id, a.id }) && f.Node(graph, b).path.SequenceEqual(new[] { root.id, a.id, b.id }), "cycle-safe forward call paths preserve explicit chain");
        Check(f.Node(graph, c).root_id == startup.id && f.Node(graph, c).status == "reachable", "second OB entry reaches its own branch");
        Check(f.Node(graph, orphan).status == "no_ob_path" && f.Findings.Single().rule == "no_ob_path" && f.Findings.Single().confidence == "review", "FC named Main is not guessed to be root or declared dead");
        Check(graph.calls_total == 4 && graph.calls_resolved == 4 && !graph.partial && !graph.truncated, "complete simple native call graph has accurate counts");
        Check(graph.calls.All(s => s.location.network == "1" && s.location.access == "call") && graph.calls.Select(s => s.id).Distinct().Count() == 4, "each call retains distinct site and network evidence");

        var noRoot = new Fixture(); noRoot.Add("FB", "Main", Net(Call("Leaf"))); noRoot.Add("FC", "Leaf");
        graph = noRoot.Run();
        Check(graph.partial && graph.nodes.All(n => n.status == "unknown") && !noRoot.Findings.Any(r => r.rule == "no_ob_path"), "no native OB means no unreachable claims");
        Check(noRoot.Warnings.Any(w => w.Contains("没有可确认的 OB")), "no-root coverage is explicit");
    }
    private static void ScopeAndResolution()
    {
        var f = new Fixture();
        IndexEntry rootA = f.Add("OB", "Cycle", Net(Call("Worker")), unit: "A"), rootB = f.Add("OB", "Cycle", Net(Call("Worker")), unit: "B");
        IndexEntry workerA = f.Add("FC", "Worker", unit: "A"), workerB = f.Add("FC", "Worker", unit: "B");
        var graph = f.Run();
        Check(graph.calls.Single(c => c.caller_id == rootA.id).callee_id == workerA.id && graph.calls.Single(c => c.caller_id == rootB.id).callee_id == workerB.id, "same-named blocks remain within software unit");
        f.Sources[workerA.id] = null; graph = f.Run();
        Check(graph.calls.Single(c => c.caller_id == rootA.id).resolution == "unresolved" && graph.calls.Single(c => c.caller_id == rootB.id).resolution == "resolved", "missing local source never resolves to foreign unit");
        Check(f.Node(graph, workerA).source_status == "unread" && graph.partial, "source failure is visible and does not fabricate reachability");

        var ambiguous = new Fixture(); ambiguous.Add("OB", "Cycle", Net(Call("Same"))); ambiguous.Add("FC", "Same", ns: "N1"); ambiguous.Add("FC", "Same", ns: "N2");
        graph = ambiguous.Run(); Check(graph.calls.Single().resolution == "ambiguous" && graph.partial, "same-name namespace ambiguity is not guessed");
        ambiguous.Sources[ambiguous.Blocks[0].id] = Doc("OB", "Cycle", Net(Call("Same", ns: "N2")));
        graph = ambiguous.Run(); Check(graph.calls.Single().callee_id == ambiguous.Blocks[2].id, "explicit namespace selects the corresponding declaration");

        var typed = new Fixture(); typed.Add("OB", "Cycle", Net(Call("Wrong", "FB"), Call("SystemClock", "SFC"), Call("Indirect", "Unknown"))); typed.Add("FC", "Wrong");
        graph = typed.Run(); Check(graph.calls[0].resolution == "unresolved" && graph.calls[1].resolution == "external" && graph.calls[2].resolution == "unresolved", "block type mismatch and unknown type never become graph edges; system calls external");
    }
    private static void Coverage()
    {
        var f = new Fixture(); var root = f.Add("OB", "Cycle", Net(Call("Text")));
        var scl = f.Add("FC", "Text", "<SW.Blocks.CompileUnit><AttributeList><NetworkSource><StructuredText>" + Call("Leaf") + "<Token Text='Foo();'/></StructuredText></NetworkSource></AttributeList></SW.Blocks.CompileUnit>", language: "SCL");
        IndexEntry leaf = f.Add("FC", "Leaf"), hidden = f.Add("FC", "Hidden", "<SW.Blocks.CompileUnit><AttributeList><NetworkSource><Graph/></NetworkSource></AttributeList></SW.Blocks.CompileUnit>", language: "GRAPH");
        var graph = f.Run(true);
        Check(graph.partial && f.Node(graph, scl).source_status == "unsupported" && f.Node(graph, scl).status == "reachable", "native SCL call metadata can be retained without claiming full SCL coverage");
        Check(f.Node(graph, leaf).path.SequenceEqual(new[] { root.id, scl.id, leaf.id }) && f.Node(graph, hidden).status == "unknown", "explicit SCL chain is evidence, unreachable unparsed GRAPH is unknown");
        Check(f.Warnings.Any(w => w.Contains("读取不完整")) && graph.limitations.Any(l => l.Contains("SCL/GRAPH")), "incomplete snapshot and language boundary remain explicit");
        f.Sources[hidden.id] = "<!DOCTYPE Document [<!ENTITY x SYSTEM 'file:///C:/Windows/win.ini'>]><Document>&x;</Document>";
        graph = f.Run(); Check(f.Node(graph, hidden).source_status == "unread", "external entity documents cannot be read");
        f.Sources[root.id] = Doc("OB", "Different"); graph = f.Run();
        Check(f.Node(graph, root).status == "unknown" && !graph.nodes.Any(n => n.status == "root"), "mismatched source identity cannot provide an OB root");
        var stl = new Fixture(); stl.Add("OB", "Cycle", "<SW.Blocks.CompileUnit><AttributeList><NetworkSource><StatementList><StlStatement><StlToken Text='CALL'/></StlStatement></StatementList></NetworkSource></AttributeList></SW.Blocks.CompileUnit>", language: "STL");
        graph = stl.Run(); Check(graph.partial && stl.Warnings.Any(w => w.Contains("STL 调用缺少")), "unresolved STL call is explicitly uncovered");
    }
    private static void LocalInstances()
    {
        var f = new Fixture(); f.Add("OB", "Cycle", Net(Call("Host", "FB", Instance("HostDB", "GlobalVariable"))));
        f.Add("InstanceDB", "HostDB", instanceOf: "Host");
        var host = f.Add("FB", "Host", Net(Call("Pump", "FB", Instance("Z1")), Call("Pump", "FB", Instance("Z1")), Call("Pump", "FB", Instance("Z2"))), Member("Z1", "\"Pump\"") + Member("Z2", "Pump"));
        f.Add("FB", "Another", Net(Call("Pump", "FB", Instance("Z1"))), Member("Z1", "Pump"));
        f.Add("FB", "Pump");
        var graph = f.Run(); var reuse = f.Findings.Where(r => r.rule == "instance_reuse").ToArray();
        Check(reuse.Length == 1 && reuse[0].locations.Count == 2 && reuse[0].locations.All(l => l.block == "Host"), "same local member across two sites reuses state; same name in different host isolated");
        Check(reuse[0].severity == "info" && reuse[0].confidence == "review" && reuse[0].explanation.Contains("不直接认定为冲突"), "reuse warning states physical sharing but leaves runtime interpretation to review");
        Check(graph.instances_resolved == 5 && graph.calls.Where(c => c.caller_id == host.id).All(c => c.instance_status == "resolved"), "local declaration and global InstanceDB identities verified");
        f.Sources[host.id] = Doc("FB", "Host", Net(Call("Pump", "FB", Instance("Z1")), Call("Pump", "FB", Instance("Z1"))), Member("Z1", "Other"));
        graph = f.Run(); Check(!f.Findings.Any(r => r.rule == "instance_reuse") && graph.calls.Where(c => c.caller_id == host.id).All(c => c.instance_status == "unsupported"), "wrong static datatype cannot establish shared FB state");
        f.Sources[host.id] = Doc("FB", "Host", Net(Call("Pump", "FB", Instance("Z1"))), Member("Z1", "Pump") + Member("Z1", "Pump"));
        graph = f.Run(); Check(graph.calls.Single(c => c.caller_id == host.id).instance_status == "unsupported", "duplicate local declarations are ambiguous");
        var fc = new Fixture(); fc.Add("FC", "Host", Net(Call("Pump", "FB", Instance("Z1"))), Member("Z1", "Pump")); fc.Add("FB", "Pump");
        Check(fc.Run().calls.Single().instance_status == "unsupported", "local multi-instance must belong to a native FB");
    }
    private static void GlobalInstances()
    {
        var f = new Fixture(); f.Add("OB", "Cycle", Net(Call("Pump", "FB", Instance("Shared", "GlobalVariable"))));
        f.Add("OB", "Startup", Net(Call("Pump", "FB", Instance("Shared", "GlobalVariable")), Call("Pump", "FB", Instance("Separate", "GlobalVariable"))));
        f.Add("FB", "Pump"); var db = f.Add("InstanceDB", "Shared", instanceOf: "Pump"); f.Add("InstanceDB", "Separate", instanceOf: "Pump");
        var graph = f.Run(); var reuse = f.Findings.Where(r => r.rule == "instance_reuse").ToArray();
        Check(reuse.Length == 1 && reuse[0].locations.Count == 2 && graph.instances_resolved == 3, "same global DB in separate callers groups, separate DB of same FB does not");
        Check(reuse[0].locations.Select(l => l.block).Distinct().Count() == 2, "global reuse retains both caller identities");
        f.Sources[db.id] = Doc("GlobalDB", "Shared"); graph = f.Run();
        Check(!f.Findings.Any(r => r.rule == "instance_reuse") && graph.instances_resolved == 1, "ordinary global DB is not guessed to be background instance");
        f.Sources[db.id] = Doc("InstanceDB", "Shared", instanceOf: "OtherFB"); graph = f.Run();
        Check(graph.calls.Count(c => c.instance_status == "unsupported") == 2, "mismatched InstanceOfName does not establish shared state");
        f.Sources[db.id] = Doc("InstanceDB", "Shared", instanceOf: "Pump"); f.Add("InstanceDB", "Shared", instanceOf: "Pump"); graph = f.Run();
        Check(!f.Findings.Any(r => r.rule == "instance_reuse"), "duplicate background DB names are not arbitrarily selected");
        var units = new Fixture();
        foreach (string unit in new[] { "A", "B" }) { units.Add("OB", "Cycle", Net(Call("Pump", "FB", Instance("Shared", "GlobalVariable"))), unit: unit); units.Add("FB", "Pump", unit: unit); units.Add("InstanceDB", "Shared", unit: unit, instanceOf: "Pump"); }
        graph = units.Run(); Check(graph.instances_resolved == 2 && !units.Findings.Any(r => r.rule == "instance_reuse"), "same DB name in different software units does not share identity");
        var wrong = new Fixture(); wrong.Add("OB", "Entry", Net(Call("Driver", "FB", Instance("DriverDB", "GlobalVariable")), Call("Driver", "FB", Instance("DriverDB", "GlobalVariable"))));
        var driver = wrong.Add("FC", "Driver"); wrong.Add("InstanceDB", "DriverDB", instanceOf: "Driver");
        graph = wrong.Run(); Check(graph.instances_resolved == 0 && !wrong.Findings.Any(r => r.rule == "instance_reuse"), "InstanceOfName alone cannot validate FB when native target is FC");
        wrong.Sources[driver.id] = null; graph = wrong.Run(); Check(graph.instances_resolved == 0, "unread FB target never validates background instance");
        wrong.Sources[driver.id] = Doc("FB", "Driver"); wrong.Add("FB", "Driver"); graph = wrong.Run();
        Check(graph.instances_resolved == 0 && graph.calls.All(c => c.resolution == "ambiguous"), "ambiguous FB target never validates background instance");
        var namespaces = new Fixture(); var nsHost = namespaces.Add("FB", "Host", Net(Call("Driver", "FB", Instance("Local"), "N1")), Member("Local", "Driver"), ns: "N2");
        namespaces.Add("FB", "Driver", ns: "N1"); graph = namespaces.Run();
        Check(graph.calls.Single().resolution == "resolved" && graph.instances_resolved == 0, "unqualified local declaration in different namespace cannot match explicit callee");
        namespaces.Sources[nsHost.id] = Doc("FB", "Host", Net(Call("Driver", "FB", Instance("Local"), "N1")), Member("Local", "N1.Driver")); graph = namespaces.Run();
        Check(graph.instances_resolved == 1, "qualified local declaration matches explicitly resolved FB namespace");
        namespaces.Sources[nsHost.id] = Doc("FB", "Host", Net(Call("Driver", "FB", Instance("Remote", "GlobalVariable"), "N1")));
        var nsDb = namespaces.Add("InstanceDB", "Remote", instanceOf: "Driver", ns: "N2"); graph = namespaces.Run();
        Check(graph.instances_resolved == 0, "background DB InstanceOfName is resolved relative to its own namespace");
        namespaces.Sources[nsDb.id] = Doc("InstanceDB", "Remote", instanceOf: "N1.Driver"); graph = namespaces.Run();
        Check(graph.instances_resolved == 1, "qualified background DB InstanceOfName is checked against actual target");
        namespaces.Add("FB", "Driver", ns: "N2"); nsDb.object_namespace = "N1";
        namespaces.Sources[nsDb.id] = Doc("InstanceDB", "Remote", instanceOf: "Driver"); graph = namespaces.Run();
        Check(graph.instances_resolved == 0, "duplicate FB short type is not inferred from background DB namespace");
        nsHost.object_namespace = "N1";
        namespaces.Sources[nsHost.id] = Doc("FB", "Host", Net(Call("Driver", "FB", Instance("Local"), "N1")), Member("Local", "Driver")); graph = namespaces.Run();
        Check(graph.instances_resolved == 0, "duplicate FB short type is not inferred from host namespace");
        namespaces.Sources[nsHost.id] = Doc("FB", "Host", Net(Call("Driver", "FB", Instance("Local"), "N1")), Member("Local", "N1.Driver")); graph = namespaces.Run();
        Check(graph.instances_resolved == 1, "fully qualified local type disambiguates same-named FBs");
    }
    private static void InstructionsAndUnsupportedInstances()
    {
        var f = new Fixture(); var host = f.Add("FB", "Host", Net(Part("TON", Instance("Timer")), Part("TOF", Instance("Timer")), Part("R_TRIG", Instance("Edge")), Part("R_TRIG", Instance("Edge"))), Member("Timer", "IEC_TIMER") + Member("Edge", "R_TRIG"));
        var graph = f.Run();
        Check(graph.calls.Count == 4 && graph.calls.All(c => c.resolution == "external") && graph.instances_resolved == 4, "native timer and edge instructions keep explicit state identities without project target edges");
        Check(f.Findings.Count(r => r.rule == "instance_reuse") == 2, "timer and verified edge instance reuse produce separate findings");
        foreach (string instance in new[] { Instance("Timers", extra: " AccessModifier='Array'"), Instance("Timer", extra: " SliceAccessModifier='x0'"), "<Instance Scope='LocalVariable'><Component Name='Group'/><Component Name='Timer'/></Instance>", Instance("Timer", "Indirect") }) {
            f.Sources[host.id] = Doc("FB", "Host", Net(Part("TON", instance), Part("TON", instance)), Member("Timer", "IEC_TIMER"));
            graph = f.Run(); Check(graph.instances_resolved == 0 && graph.calls.All(c => c.instance_status == "unsupported") && !f.Findings.Any(r => r.rule == "instance_reuse"), "array/struct/slice/indirect state identity never collapsed to bare member: " + instance);
        }
        f.Sources[host.id] = Doc("FB", "Host", Net(Part("UnknownStateful", Instance("Timer"))), Member("Timer", "IEC_TIMER"));
        graph = f.Run(); Check(graph.partial && graph.calls.Single().instance_status == "unsupported", "unknown stateful instruction is uncovered rather than assumed timer");
        f.Sources[host.id] = Doc("FB", "Host", Net(Call("Pump", "FB"))); graph = f.Run();
        Check(graph.calls.Single().instance_status == "unsupported", "FB call with no instance is explicitly uncovered");
        var sfb = new Fixture(); sfb.Add("FB", "Host", Net(Call("Invented", "SFB", Instance("State")), Call("Invented", "SFB", Instance("State"))), Member("State", "Invented"));
        graph = sfb.Run(); Check(graph.instances_resolved == 0 && !sfb.Findings.Any(r => r.rule == "instance_reuse"), "unknown system FB cannot prove state identity from matching arbitrary name");
    }
    private static void Bounds()
    {
        var chain = new Fixture(); var root = chain.Add("OB", "Entry", Net(Call("F0")));
        for (int i = 0; i < CallGraphAudit.MaxPath + 3; i++) chain.Add("FC", "F" + i, i == CallGraphAudit.MaxPath + 2 ? "" : Net(Call("F" + (i + 1))));
        var graph = chain.Run();
        Check(graph.truncated && graph.partial && graph.nodes.Last().status == "reachable" && graph.nodes.Last().root_id == root.id, "path display truncation never turns reachable node into orphan");
        Check(graph.nodes.All(n => n.path.Count <= CallGraphAudit.MaxPath) && graph.nodes.Sum(n => n.path.Count) <= CallGraphAudit.MaxPathSteps && graph.nodes.Last().note.Contains("上限"), "path output stays bounded with explicit node note");
        var calls = new Fixture(); calls.Add("OB", "Entry", Net(Enumerable.Repeat(Call("Leaf"), CallGraphAudit.MaxCalls + 1).ToArray())); calls.Add("FC", "Leaf");
        graph = calls.Run(); Check(graph.calls.Count == CallGraphAudit.MaxCalls && graph.calls_total == CallGraphAudit.MaxCalls + 1 && graph.truncated, "call-site cap retains total and truncation marker");
        var repeated = new Fixture(); repeated.Add("FB", "Host", Net(Enumerable.Repeat(Call("Pump", "FB", Instance("Shared")), CallGraphAudit.MaxEvidence + 1).ToArray()), Member("Shared", "Pump")); repeated.Add("FB", "Pump");
        graph = repeated.Run(); Check(repeated.Findings.Single(r => r.rule == "instance_reuse").locations.Count == CallGraphAudit.MaxEvidence && graph.truncated, "instance evidence truncation is visible");
        var many = new Fixture(); for (int i = 0; i < CallGraphAudit.MaxBlocks + 1; i++) many.Add("FC", "F" + i);
        graph = many.Run(); Check(graph.nodes.Count == CallGraphAudit.MaxBlocks && graph.truncated && graph.partial, "snapshot object count bounded before source reads");
    }
    private static int Main()
    {
        try { Paths(); ScopeAndResolution(); Coverage(); LocalInstances(); GlobalInstances(); InstructionsAndUnsupportedInstances(); Bounds(); Console.WriteLine("PASS: " + checks + " call graph and instance identity checks; source snapshots only, no TIA connection or project changes."); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
