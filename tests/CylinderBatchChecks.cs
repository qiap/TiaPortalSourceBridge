using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml;
using TiaPortalSourceBridge.Indexing;
using TiaPortalSourceBridge.Mcp;
using TiaPortalSourceBridge.Web;

internal static class CylinderBatchChecks
{
    private static int count;
    private static Dictionary<string, object> D(params object[] pairs) { var d = new Dictionary<string, object>(); for (int i = 0; i < pairs.Length; i += 2) d[(string)pairs[i]] = pairs[i + 1]; return d; }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); count++; }
    private static void Reject(Action action, string text) { try { action(); } catch (InvalidOperationException ex) { Check(ex.Message.Contains(text), "expected rejection containing " + text + ": " + ex.Message); return; } throw new Exception("unexpected acceptance: " + text); }
    private static string Fixture(string method, params object[] values)
    { return (string)typeof(CylinderApplyChecks).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, values); }
    private static string Access(params string[] names) { return Fixture("Access", new object[] { names }); }
    private static string Db(string name, int number, string fields) { return Fixture("Db", name, number, fields); }
    private static Dictionary<string, object> Request(int n)
    {
        return D("sample_id", "Z01", "instance_name", "Z0" + (n + 3), "cylinder_name", n == 1 ? "夹紧气缸" : "支撑气缸", "alarm_index", (n + 3).ToString(),
            "rules", D("i_Wpos", "input", "oOutput_W", "output", "i_Interlock_Wpos", "shared", "HMI_Output_W", "device", "Timeout_ToWpos", "index", "Unused", "open"),
            "bindings", D("i_Wpos", "i" + n, "oOutput_W", "q" + n));
    }
    private static XmlDocument Parse(string value) { var document = new XmlDocument(); document.LoadXml(value); return document; }
    private static void Tests()
    {
        var host = IndexEntry.Create("plc.block", "PLC", "Area", "Host", "LAD", "1");
        var db = IndexEntry.Create("plc.block", "PLC", "Area", "Signals", "DB", "2");
        var data = IndexEntry.Create("plc.block", "PLC", "Area", "Data", "DB", "3");
        var alarm = IndexEntry.Create("plc.block", "PLC", "Area", "Alarm", "DB", "4");
        var mapper = IndexEntry.Create("plc.block", "PLC", "Area/Mapping", "Map", "STL", "5");
        var code = new Dictionary<string, string>(); code[host.id] = Fixture("Host");
        code[db.id] = Db("Signals", 2, "<Member Name='Input' Datatype='Struct'><Member Name='OldI' Datatype='Bool'/><Member Name='Stop' Datatype='Bool'/><Member Name='SpareI1' Datatype='Bool'/><Member Name='SpareI2' Datatype='Bool'/></Member><Member Name='Output' Datatype='Struct'><Member Name='OldQ' Datatype='Bool'/><Member Name='SpareQ1' Datatype='Bool'/><Member Name='SpareQ2' Datatype='Bool'/></Member>");
        code[data.id] = Db("Data", 3, "<Member Name='Z01' Datatype='&quot;CylinderData&quot;'/>");
        code[alarm.id] = Db("Alarm", 4, "<Member Name='Timeout' Datatype='Array[1..8,1..2] of Bool'/>");
        var statements = new List<string>();
        for (int i = 1; i <= 2; i++) {
            statements.Add("<StlStatement><StlToken Text='A'/>" + Access("InputTag" + i) + "</StlStatement><StlStatement><StlToken Text='='/>") ;
            statements.Add(Access("Signals", "Input", "SpareI" + i) + "</StlStatement><StlStatement><StlToken Text='A'/>" + Access("Signals", "Output", "SpareQ" + i) + "</StlStatement><StlStatement><StlToken Text='='/>");
            statements.Add(Access("OutputTag" + i) + "</StlStatement>");
        }
        code[mapper.id] = "<Document><SW.Blocks.FC ID='0'><AttributeList><Name>Map</Name><Number>5</Number></AttributeList><ObjectList><SW.Blocks.CompileUnit ID='1'><AttributeList><NetworkSource><StatementList>" + string.Join("", statements) + "</StatementList></NetworkSource></AttributeList></SW.Blocks.CompileUnit></ObjectList></SW.Blocks.FC></Document>";
        var snapshot = new IoSnapshot { project_path = "fixture.ap17", plc_name = "PLC", cylinderBlocks = new[] { host, db, data, alarm, mapper }, cylinderProgramsTotal = 2, cylinderProgramsRead = 2, tables_read = 1, tables_total = 1 };
        for (int i = 1; i <= 2; i++) {
            snapshot.rows.Add(new IoRow { id = "i" + i, unit = "", name = "InputTag" + i, address = "%I3." + (i - 1), area = "I", data_type = "Bool" });
            snapshot.rows.Add(new IoRow { id = "q" + i, unit = "", name = "OutputTag" + i, address = "%Q3." + (i - 1), area = "Q", data_type = "Bool" });
        }
        Func<IndexEntry, string> source = entry => code[entry.id];
        IoDeclarations.Add(snapshot, snapshot.cylinderBlocks, source);
        var originalCode = new Dictionary<string, string>(code); string originalRows = CylinderWritePipeline.Json().Serialize(snapshot.rows);
        var requests = new List<Dictionary<string, object>> { Request(1), Request(2) };
        var plan = CylinderBatchPlan.Build(snapshot, host, source, requests);
        Check(plan.Items.Count == 4, "two cylinders merge into mapping DB, data DB, mapper and host exactly once");
        Check(plan.Items.Select(i => CylinderRecoveryPolicy.Name((string)i["xml"])).Distinct().Count() == plan.Items.Count, "final batch contains no duplicate blocks");
        string combinedHost = (string)plan.Items.Last()["xml"];
        var doc = Parse(combinedHost); var units = doc.SelectNodes("//SW.Blocks.CompileUnit").Cast<XmlElement>().ToArray();
        Check(units.Length == 3, "both requested cylinders appended to the same final host");
        Check(units[0].OuterXml == Parse(originalCode[host.id]).SelectSingleNode("//SW.Blocks.CompileUnit").OuterXml, "original donor network remains unchanged");
        Check(units[1].OuterXml.Contains("Name=\"Z04\"") && units[1].OuterXml.Contains("夹紧气缸伸出到位I3.0") && units[1].OuterXml.Contains("夹紧气缸伸出阀Q3.0"), "first cylinder keeps its chosen name and physical points");
        Check(units[2].OuterXml.Contains("Name=\"Z05\"") && units[2].OuterXml.Contains("支撑气缸伸出到位I3.1") && units[2].OuterXml.Contains("支撑气缸伸出阀Q3.1") && !units[2].OuterXml.Contains("夹紧气缸"), "second cylinder never inherits the first new cylinder's bindings");
        Check(units[1].SelectSingleNode(".//Component[@Name='Timeout']//ConstantValue").InnerText == "4" && units[2].SelectSingleNode(".//Component[@Name='Timeout']//ConstantValue").InnerText == "5", "different alarm indices preserved");
        Check(units.Skip(1).All(u => u.OuterXml.Contains("Name=\"Stop\"")), "both cylinders preserve sample emergency-stop interlock");
        Check(doc.SelectNodes("//@ID").Cast<XmlAttribute>().Select(a => a.Value).Distinct().Count() == doc.SelectNodes("//@ID").Count, "all generated document IDs remain unique across the batch");
        string signals = (string)plan.Items.Single(i => CylinderRecoveryPolicy.Name((string)i["xml"]) == "Signals")["xml"];
        string map = (string)plan.Items.Single(i => CylinderRecoveryPolicy.Name((string)i["xml"]) == "Map")["xml"];
        Check(!signals.Contains("SpareI") && !signals.Contains("SpareQ") && !map.Contains("SpareI") && !map.Contains("SpareQ"), "both rows' mapping declarations and structured uses renamed");
        string dataXml = (string)plan.Items.Single(i => CylinderRecoveryPolicy.Name((string)i["xml"]) == "Data")["xml"];
        Check(dataXml.Contains("Name=\"Z04\"") && dataXml.Contains("Name=\"Z05\""), "new data members accumulate instead of replacing prior row");
        foreach (var item in plan.Items) {
            string name = CylinderRecoveryPolicy.Name((string)item["xml"]), original = source(snapshot.cylinderBlocks.Single(b => b.name == name));
            Check((string)item["expected_content_signature"] == XmlBatch.ContentSignature(original), "batch precondition refers to real original snapshot: " + name);
            Check((string)item["expected_recovery_signature"] == XmlBatch.RecoverySignature(original), "batch strict guard refers to real original snapshot: " + name);
        }
        PipelineAndRecovery(plan, snapshot, source);
        var summary = CylinderWritePipeline.Map(plan.Summary);
        Check(CylinderWritePipeline.Text(summary, "count") == "2" && CylinderWritePipeline.Rows(summary, "renames").Length == 4, "summary lists all cylinders and all four point renames");
        Check(code.All(p => originalCode[p.Key] == p.Value) && originalRows == CylinderWritePipeline.Json().Serialize(snapshot.rows), "planning leaves original code and rows unchanged");
        Check(!((Dictionary<string, object>)requests[1]["bindings"]).Values.Any(v => Convert.ToString(v).Length > 3), "planning does not replace request bindings with virtual IDs");
        foreach (string field in new[] { "instance_name", "cylinder_name", "alarm_index" }) {
            var duplicates = new List<Dictionary<string, object>> { Request(1), Request(2) }; duplicates[1][field] = duplicates[0][field];
            Reject(() => CylinderBatchPlan.Build(snapshot, host, source, duplicates), "重复");
        }
        var conflict = new List<Dictionary<string, object>> { Request(1), Request(2) }; ((Dictionary<string, object>)conflict[1]["bindings"])["i_Wpos"] = "i1";
        Reject(() => CylinderBatchPlan.Build(snapshot, host, source, conflict), "重复占用");
        conflict = new List<Dictionary<string, object>> { Request(1), Request(2) }; conflict[1]["sample_id"] = "Z02";
        Reject(() => CylinderBatchPlan.Build(snapshot, host, source, conflict), "同一工程");
        conflict = new List<Dictionary<string, object>> { Request(1), Request(2) }; ((Dictionary<string, object>)conflict[1]["rules"])["i_Interlock_Wpos"] = "input";
        Reject(() => CylinderBatchPlan.Build(snapshot, host, source, conflict), "同一工程");
        conflict = new List<Dictionary<string, object>> { Request(1), Request(2) }; conflict[1]["alarm_index"] = "9";
        Reject(() => CylinderBatchPlan.Build(snapshot, host, source, conflict), "第 2 行");
        Check(code.All(p => originalCode[p.Key] == p.Value), "late row failure cannot mutate original engineering source");
        Reject(() => CylinderBatchPlan.Build(snapshot, host, source, new List<Dictionary<string, object>>()), "1～20");
        Reject(() => CylinderBatchPlan.Build(snapshot, host, source, Enumerable.Range(0, 21).Select(_ => Request(1)).ToList()), "1～20");
        var alias = new IoRow { id = "i-alias", unit = "", name = "Alias", address = "%I3.0", area = "I", data_type = "Bool" }; snapshot.rows.Add(alias);
        conflict = new List<Dictionary<string, object>> { Request(1), Request(2) }; ((Dictionary<string, object>)conflict[1]["bindings"])["i_Wpos"] = alias.id;
        Reject(() => CylinderBatchPlan.Build(snapshot, host, source, conflict), "重复占用"); snapshot.rows.Remove(alias);
        SharedReset(snapshot, host, source, code, requests);
    }
    private static void PipelineAndRecovery(CylinderApplyPlan plan, IoSnapshot snapshot, Func<IndexEntry, string> source)
    {
        int imports = 0, saves = 0; var result = D("saved", false);
        CylinderWritePipeline.Run(plan.Items, snapshot.project_path, snapshot.plc_name, result, (name, args) => {
            if (name == "tia_import_blocks_batch") {
                imports++;
                var parsed = XmlBatch.Parse(CylinderWritePipeline.Text(args, "items_json"));
                Check(parsed.Length == plan.Items.Count && parsed.Select(i => i.block_name).Distinct().Count() == plan.Items.Count,
                    "one merged import contains every shared block exactly once");
                Check(parsed.All(i => i.expected_recovery_signature.Length == 64), "strict block guards survive serialization to worker");
                Check(CylinderWritePipeline.Text(args, "compile_mode") == "plc", "all rows request one whole-PLC compile in the merged batch");
                return D("isError", false, "structuredContent", D("ok", true, "compiled", true, "error_count", 0,
                    "items", parsed.Select(i => D("block_name", i.block_name, "status", "imported", "verification", "matched")).ToArray()));
            }
            Check(name == "tia_save_project", "only one explicit save follows the merged batch"); saves++;
            return D("isError", false, "structuredContent", D("saved", true));
        }, _ => { });
        Check(imports == 1 && saves == 1 && CylinderWritePipeline.True(result, "saved"), "two-cylinder batch imports and saves exactly once");
        var backups = plan.Items.Select(i => {
            var entry = snapshot.cylinderBlocks.Single(b => b.name == CylinderRecoveryPolicy.Name((string)i["xml"]));
            return D("xml", source(entry), "group_path", entry.group);
        }).ToList();
        string instanceBefore = Db("HostInstance", 20, "<Member Name='Z01' Datatype='Bool'/>");
        string instanceAfter = Db("HostInstance", 20, "<Member Name='Z01' Datatype='Bool'/><Member Name='Z04' Datatype='Bool'/><Member Name='Z05' Datatype='Bool'/>");
        backups.Add(D("xml", instanceBefore, "group_path", "Area"));
        var before = snapshot.cylinderBlocks.Select(b => D("kind", "block", "block_name", b.name, "group_path", b.group,
            "signature", XmlBatch.RecoverySignature(source(b)))).ToList();
        before.Add(D("kind", "block", "block_name", "HostInstance", "group_path", "Area", "signature", XmlBatch.RecoverySignature(instanceBefore)));
        before.Add(D("kind", "io", "block_name", "@io", "group_path", "", "signature", XmlBatch.Hash("unchanged native IO")));
        var after = before.Select(r => new Dictionary<string, object>(r)).ToList();
        foreach (var item in plan.Items) after.Single(r => CylinderRecoveryPolicy.Key(r) == CylinderRecoveryPolicy.Key(CylinderRecoveryPolicy.Name((string)item["xml"]), (string)item["group_path"]))["signature"] = XmlBatch.RecoverySignature((string)item["xml"]);
        after.Single(r => (string)r["block_name"] == "HostInstance")["signature"] = XmlBatch.RecoverySignature(instanceAfter);
        var recovery = CylinderRecoveryPolicy.Evaluate(plan.Items, before, after, after, backups);
        Check(recovery.CanContinue && recovery.ContinueItems.Count == 0, "fully applied batch continues at verification/save without regenerating cylinders");
        Check(recovery.CanRestore && recovery.RestoreItems.Count == plan.Items.Count + 1 && CylinderRecoveryPolicy.Name((string)recovery.RestoreItems.Last()["xml"]) == "HostInstance",
            "batch restoration includes every shared changed block and associated instance DB after the host");
        var partial = before.Select(r => new Dictionary<string, object>(r)).ToList();
        var first = plan.Items.First(); string firstKey = CylinderRecoveryPolicy.Key(CylinderRecoveryPolicy.Name((string)first["xml"]), (string)first["group_path"]);
        partial.Single(r => CylinderRecoveryPolicy.Key(r) == firstKey)["signature"] = XmlBatch.RecoverySignature((string)first["xml"]);
        recovery = CylinderRecoveryPolicy.Evaluate(plan.Items, before, partial, partial, backups);
        Check(recovery.CanContinue && recovery.ContinueItems.Count == plan.Items.Count - 1 && recovery.RestoreItems.Count == 1,
            "partial batch continues only remaining merged blocks, or restores only the confirmed change");
        string oldHost = source(snapshot.cylinderBlocks.Single(b => b.name == "Host"));
        string commented = oldHost.Replace("</SW.Blocks.FB>", "<ObjectList><MultilingualText CompositionName='Comment'><ObjectList><MultilingualTextItem><AttributeList><Text>operator note</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText></ObjectList></SW.Blocks.FB>");
        Check(XmlBatch.ContentSignature(oldHost) == XmlBatch.ContentSignature(commented) && XmlBatch.RecoverySignature(oldHost) != XmlBatch.RecoverySignature(commented),
            "strict source guard detects operator comments missed by ordinary content signatures");
    }
    private static void SharedReset(IoSnapshot snapshot, IndexEntry host, Func<IndexEntry, string> source, Dictionary<string, string> code, IList<Dictionary<string, object>> requests)
    {
        var caller = IndexEntry.Create("plc.block", "PLC", "Area", "AlarmCaller", "LAD", "6");
        var aggregate = IndexEntry.Create("plc.block", "PLC", "Area", "Aggregate", "SCL", "7");
        code[caller.id] = "<Document><SW.Blocks.FC><AttributeList><Name>AlarmCaller</Name><Number>6</Number></AttributeList><ObjectList><SW.Blocks.CompileUnit><AttributeList><NetworkSource><FlgNet><Parts><Access Scope='GlobalVariable' UId='1'><Symbol><Component Name='Alarm'/></Symbol></Access><Call UId='2'><CallInfo Name='Aggregate' BlockType='FC'><Parameter Name='Alarm' Section='InOut' Type='Struct'/></CallInfo></Call></Parts><Wires><Wire><IdentCon UId='1'/><NameCon UId='2' Name='Alarm'/></Wire></Wires></FlgNet></NetworkSource></AttributeList></SW.Blocks.CompileUnit></ObjectList></SW.Blocks.FC></Document>";
        const string logic = "IF #Reset THEN FOR #i := 1 TO 8 BY 1 DO #Alarm.Timeout[#i,1] := FALSE; #Alarm.Timeout[#i,2] := FALSE; END_FOR; END_IF; FOR #i := 1 TO 8 BY 1 DO IF #Alarm.Timeout[#i,1] OR #Alarm.Timeout[#i,2] THEN #Any := TRUE; EXIT; END_IF; END_FOR;";
        code[aggregate.id] = ((string)typeof(CylinderAlarmResetChecks).GetMethod("Callee", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { logic })).Replace("Array[1..4,1..2]", "Array[1..8,1..2]");
        snapshot.cylinderBlocks = snapshot.cylinderBlocks.Concat(new[] { caller, aggregate }).ToArray(); snapshot.cylinderProgramsTotal += 2; snapshot.cylinderProgramsRead += 2;
        var plan = CylinderBatchPlan.Build(snapshot, host, source, requests);
        Check(CylinderWritePipeline.True(CylinderWritePipeline.Map(plan.Summary), "shared_alarm_reset"), "batch verifies and preserves existing shared alarm reset for both new indices");
        Check(plan.Items.All(i => CylinderRecoveryPolicy.Name((string)i["xml"]) != "Aggregate" && CylinderRecoveryPolicy.Name((string)i["xml"]) != "AlarmCaller"), "batch does not rewrite verified common reset logic");
    }
    private static void CachedProject(string project, string plc)
    {
        var store = new ProjectIndexStore(project, "V17"); var snapshot = new IoSnapshot { project_path = project, plc_name = plc };
        var tables = store.Select("plc.tags", plc, "", "").ToArray(); snapshot.tables_total = tables.Length;
        foreach (var table in tables) { snapshot.rows.AddRange(IoTable.Parse(store.IoSource(table), table.name, table.group, table.unit_name)); snapshot.tables_read++; }
        snapshot.cylinderBlocks = store.IoBlocks(plc); var coverage = store.IoProgramCoverage(plc);
        snapshot.cylinderProgramsTotal = coverage[0]; snapshot.cylinderProgramsRead = coverage[1];
        IoMapping.Expand(snapshot, snapshot.cylinderBlocks, store.IoSource); IoDeclarations.Add(snapshot, snapshot.cylinderBlocks, store.IoSource);
        var host = snapshot.cylinderBlocks.Single(b => b.name == "OP275_Cylinder"); string originalHost = store.IoSource(host);
        var original = Parse(originalHost); var calls = original.SelectNodes("//*[local-name()='CallInfo']").Cast<XmlElement>().ToArray();
        var donor = calls.First(c => c.SelectSingleNode("*[local-name()='Instance']/*[local-name()='Component']/@Name").Value == "Z03");
        int next = calls.Select(c => c.SelectSingleNode("*[local-name()='Instance']/*[local-name()='Component']/@Name").Value)
            .Where(s => System.Text.RegularExpressions.Regex.IsMatch(s, "^Z[0-9]+$")).Select(s => int.Parse(s.Substring(1))).Max() + 1;
        var usage = new CylinderIoUsage(snapshot, store.IoSource);
        var available = snapshot.rows.Where(r => r.data_type == "Bool" && (r.area == "I" || r.area == "Q") && usage.For(r).Availability == "unused" && usage.PreferredSymbol(r).Contains("DB_OP275IOMapping"))
            .GroupBy(r => IoTable.Address(r.address)).Select(g => g.First()).ToArray();
        var inputs = available.Where(r => r.area == "I").OrderBy(IoTable.SortAddress).Take(4).ToArray();
        var outputs = available.Where(r => r.area == "Q").OrderBy(IoTable.SortAddress).Take(4).ToArray();
        Check(inputs.Length == 4 && outputs.Length == 4, "cached real project has enough verified free points for two cylinders");
        var requests = new List<Dictionary<string, object>>();
        for (int i = 0; i < 2; i++) {
            var rules = new Dictionary<string, object>(); var bindings = new Dictionary<string, object>();
            foreach (XmlElement pin in donor.SelectNodes("*[local-name()='Parameter']")) {
                string name = pin.GetAttribute("Name");
                if (name == "i_Wpos" || name == "i_Hpos" || name == "oOutput_W" || name == "oOutput_H") {
                    bool input = name.StartsWith("i_"); rules[name] = input ? "input" : "output";
                    bindings[name] = (input ? inputs : outputs)[2 * i + (name.EndsWith("Hpos") || name.EndsWith("_H") ? 1 : 0)].id;
                } else if (name.StartsWith("Timeout_")) rules[name] = "index";
                else if (name.StartsWith("HMI") || name.StartsWith("i_Trigger_") || name == "TimeOut_Set") rules[name] = "device";
                else rules[name] = "shared";
            }
            requests.Add(D("sample_id", "Z03", "instance_name", "Z" + (next + i).ToString("D2"), "cylinder_name", "OP275离线批量校验" + (i + 1), "alarm_index", (next + i).ToString(), "rules", rules, "bindings", bindings));
        }
        var plan = CylinderBatchPlan.Build(snapshot, host, store.IoSource, requests);
        var combined = Parse((string)plan.Items.Last()["xml"]);
        var beforeUnits = original.SelectNodes("//SW.Blocks.CompileUnit").Cast<XmlElement>().ToArray(); var afterUnits = combined.SelectNodes("//SW.Blocks.CompileUnit").Cast<XmlElement>().ToArray();
        Check(afterUnits.Length == beforeUnits.Length + 2 && beforeUnits.Select(u => u.OuterXml).SequenceEqual(afterUnits.Take(beforeUnits.Length).Select(u => u.OuterXml)), "cached real host preserves every existing network and adds exactly two");
        Check(afterUnits[beforeUnits.Length].OuterXml.Contains("OP275离线批量校验1") && !afterUnits[beforeUnits.Length].OuterXml.Contains("OP275离线批量校验2") && afterUnits.Last().OuterXml.Contains("OP275离线批量校验2") && !afterUnits.Last().OuterXml.Contains("OP275离线批量校验1"), "real generated networks have independent endpoint names");
        Check(CylinderWritePipeline.Rows(CylinderWritePipeline.Map(plan.Summary), "renames").Length == 8, "real two-cylinder plan contains eight IO renames");
        Check(originalHost == store.IoSource(host), "cached real source is unchanged after planning");
        Console.WriteLine("Read-only cached project: " + plan.Items.Count + " unique merged blocks for two cylinders; no imports, compiles or saves.");
    }
    public static int Main(string[] args)
    {
        try { Tests(); if (args.Length == 2) CachedProject(args[0], args[1]); Console.WriteLine("PASS: " + count + " batch cylinder planning checks; no TIA connection or writes."); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
