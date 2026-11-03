using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using TiaPortalSourceBridge.Indexing;
using TiaPortalSourceBridge.Mcp;
using TiaPortalSourceBridge.Configuration;

internal static class IndexChecks
{
    private static int checks;
    private static void Check(bool ok, string text) { if (!ok) throw new Exception(text); checks++; }
    private static Dictionary<string, object> Map(object data) { return IndexJson.Map(data); }
    private static int Num(object data, string key) { return Convert.ToInt32(Map(data)[key]); }
    private static string Xml(string name, string body)
    { return "<Document><Engineering version='V17'/><SW.Blocks.FC ID='0'><AttributeList><Name>" + name + "</Name></AttributeList><ObjectList>" + body + "</ObjectList></SW.Blocks.FC></Document>"; }
    private static string Unit(string body)
    { return "<SW.Blocks.CompileUnit ID='1'><AttributeList><NetworkSource>" + body + "</NetworkSource></AttributeList></SW.Blocks.CompileUnit>"; }
    private static IndexEntry Entry(string name, string stamp = "") { return IndexEntry.Create("plc.block", "PLC", "", name, "LAD", "1", "", "", stamp); }
    public static int Main(string[] args)
    {
        string root = Path.Combine(Path.GetTempPath(), "tia-index-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var store = new ProjectIndexStore(Path.Combine(root, "test.ap17"), "V17");
            store.BuildCatalog(w => new List<IndexEntry> { Entry("A"), Entry("Ghost"), IndexEntry.Create("hardware", "hardware", "rack", "Rack") });
            string first = Xml("A", Unit("<CallInfo Name='Callee' BlockType='FC'/><Access Scope='GlobalVariable'><Symbol><Component Name='DB'/><Component Name='literal.dot'/></Symbol></Access>"));
            store.Hydrate(new[] { store.Load().objects[0] }, e => first, 1, false);
            Check(Num(store.References("Callee", 0, 10), "total_matches") == 1, "initial reverse reference");
            Check(Num(store.References("DB", 0, 10), "total_matches") == 1, "parent symbol reference");
            Check(Num(store.References("DB.literal", 0, 10), "total_matches") == 0, "literal dot is not hierarchy");
            Check(Num(store.References("DB.\"literal.dot\"", 0, 10), "total_matches") == 1, "quoted literal exact lookup");
            Check(File.Exists(Path.Combine(store.Root, "query-cache.json")), "lookup persisted");
            var reopened = new ProjectIndexStore(Path.Combine(root, "test.ap17"), "V17");
            Check(Num(reopened.References("Callee", 0, 10), "total_matches") == 1, "cold process lookup cache");
            bool scanConflict = false;
            try { store.BuildCatalog(w => { reopened.MarkDirty(new[] { "Ghost" }, "during catalog scan"); return new List<IndexEntry> { Entry("A") }; }); }
            catch (InvalidOperationException) { scanConflict = true; }
            Check(scanConflict && store.Load().objects.Any(e => e.name == "Ghost"), "concurrent catalog invalidation prevents false deletion");
            store.BuildCatalog(w => new List<IndexEntry> { Entry("A"), Entry("New") }, false, true, false);
            Check(store.Load().removed == 1 && store.Load().added == 1, "reconcile additions and deletions");
            Check(store.Load().objects.Any(e => e.kind == "hardware"), "omitted hardware is not deleted");
            Check(store.Load().objects.First(e => e.name == "A").state == "ready", "catalog reconciliation retains content coverage");
            Check(store.Load().objects.First(e => e.name == "A").freshness == "unverified_snapshot", "catalog is not proof of freshness");
            store.BuildCatalog(w => new List<IndexEntry> { Entry("A") });
            Check(store.Load().objects[0].state == "stale", "explicit refresh invalidates unstamped content");
            string second = Xml("A", Unit("<CallInfo Name='Other' BlockType='FC'/>"));
            store.Hydrate(store.Load().objects, e => second, 1, false);
            Check(Num(store.References("Callee", 0, 10), "total_matches") == 0, "updated object drops old references");
            Check(Num(store.References("Other", 0, 10), "total_matches") == 1, "updated object adds new references");
            Check(Num(reopened.References("Other", 0, 10), "total_matches") == 1, "other store observes catalog update");
            // Another writer can mark dirty while a slow export is in progress.
            var conflict = store.Hydrate(store.Load().objects, e => { reopened.MarkDirty(new[] { "A" }, "during export"); return first; }, 1, true);
            Check(Num(conflict, "conflicts") == 1, "concurrent invalidation rejects stale export");
            Check(Num(store.Status(), "ready") == 0, "concurrent write stays stale");
            store.Hydrate(store.Load().objects, e => second, 1, false);
            Check(Num(store.Status(), "ready") == 1, "resumes after concurrent conflict");
            var failed = store.Hydrate(store.Load().objects, e => { throw new IOException("export failed"); }, 1, true);
            Check(Num(failed, "failed") == 1, "export failure is checkpointed");
            Check(Num(store.Hydrate(store.Load().objects, e => second, 1, false), "reused") == 1, "resume does not loop on errors");
            store.Hydrate(store.Load().objects, e => second, 1, true);
            Check(Num(store.Status(), "ready") == 1, "forced retry succeeds");
            File.Delete(Path.Combine(store.Root, "content", store.Load().objects[0].content_hash + ".json"));
            Check(Num(store.References("Other", 0, 10), "total_matches") == 0, "missing content does not use in-memory ghost reference");
            store.Hydrate(store.Load().objects, e => second, 1, false);
            // Old parser content can be upgraded from the source snapshot without a TIA call.
            string hash = store.Load().objects[0].content_hash, contentPath = Path.Combine(store.Root, "content", hash + ".json");
            var old = ContentIndexer.Parse(second); old.parser = "structure-v1"; old.references.Clear();
            File.WriteAllText(contentPath, IndexJson.Serializer().Serialize(old)); File.WriteAllText(Path.Combine(store.Root, "query-cache.json"), "invalid cache");
            reopened = new ProjectIndexStore(Path.Combine(root, "test.ap17"), "V17");
            Check(Num(reopened.References("Other", 0, 10), "total_matches") == 1, "local source upgrades old parser and corrupt lookup cache");
            store.BuildCatalog(w => new List<IndexEntry> { Entry("A", "stamp1") });
            store.Hydrate(store.Load().objects, e => second, 1, true);
            store.BuildCatalog(w => new List<IndexEntry> { Entry("A", "stamp1") });
            Check(Num(store.Status(), "ready") == 1, "matching version timestamp reuses snapshot");
            store.BuildCatalog(w => new List<IndexEntry> { Entry("A", "stamp2") });
            Check(Num(store.Status(), "ready") == 0, "changed version timestamp invalidates snapshot");
            store.BuildCatalog(w => new List<IndexEntry> { Entry("A"), Entry("B") });
            var batch = store.Hydrate(store.Load().objects, e => Xml(e.name, ""), 1, false);
            Check(Num(batch, "updated") == 1 && Convert.ToString(Map(batch)["stop_reason"]) == "batch_limit", "bounded build checkpoints");
            Check(Num(store.Hydrate(store.Load().objects, e => Xml(e.name, ""), 1, false), "updated") == 1, "next batch resumes");
            store.BuildCatalog(w => new List<IndexEntry> { Entry("Moved") }, false, true);
            Check(Num(store.References("Other", 0, 10), "total_matches") == 0, "removed identities disappear from reverse index");
            var server = (McpServer)FormatterServices.GetUninitializedObject(typeof(McpServer));
            typeof(McpServer).GetField("projectIndex", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(server, store);
            typeof(McpServer).GetField("indexedProjectPath", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(server, Path.Combine(root, "test.ap17"));
            var request = new Dictionary<string, object> { { "block_name", "Moved" }, { "use_index", false } };
            typeof(McpServer).GetMethod("PrepareIndexContext", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(server,
                new object[] { "tia_read_block", request, new AppConfig { ProjectPath = Path.Combine(root, "test.ap17"), TiaPortalVersion = "V17" } });
            var payload = new Dictionary<string, object>();
            var response = new Dictionary<string, object> { { "structuredContent", payload } };
            typeof(McpServer).GetMethod("AttachIndexHint", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(server, new object[] { "tia_read_block", request, response });
            Check(payload.ContainsKey("index_context"), "direct reader receives index evidence without client cooperation");
            var audit = Map(((Dictionary<string, object>)response["_meta"])["project_index"]);
            Check(Convert.ToBoolean(audit["index_used"]) && Convert.ToString(audit["lookup_phase"]) == "before_live_operation", "server audit proves lookup before live read");

            string ladder = "<FlgNet><Parts><Access UId='1' Scope='GlobalVariable'><Symbol><Component Name='DB'/><Component Name='lamp.Q50.0'/></Symbol></Access><Access UId='2' Scope='Address'><Address Area='Q' Type='Bit' BitOffset='400'/></Access><Part UId='3' Name='Contact'/><Part UId='4' Name='Coil'/></Parts><Wires><Wire><IdentCon UId='1'/><NameCon UId='3' Name='operand'/></Wire><Wire><IdentCon UId='2'/><NameCon UId='4' Name='operand'/></Wire><Wire><Powerrail/><NameCon UId='3' Name='in'/></Wire><Wire><NameCon UId='3' Name='out'/><NameCon UId='4' Name='in'/></Wire></Wires></FlgNet>";
            var c = ContentIndexer.Parse(Xml("Map", Unit(ladder)));
            Check(c.references.Any(r => r.relation == "reads" && r.target == "DB.\"lamp.Q50.0\""), "native contact read with literal dot");
            Check(c.references.Any(r => r.relation == "writes" && r.target == "%Q50.0"), "native coil absolute write");
            Check(c.facts.Any(f => f.kind == "direct_mapping" && f.text.Contains("%Q50.0")), "verified direct LAD mapping");
            c = ContentIndexer.Parse(Xml("Map", Unit(ladder.Replace("Name='Contact'/>", "Name='Contact'><Negated Name='operand'/></Part>"))));
            Check(!c.facts.Any(f => f.kind == "direct_mapping"), "negated logic is not reported as direct mapping");
            c = ContentIndexer.Parse(first);
            Check(c.facts.Any(f => f.kind == "call_order" && f.text.Contains("network=1 source_order=1")), "network and call order evidence");
            string scl = "<StructuredText><Access Scope='GlobalVariable'><Symbol><Component Name='DB'/><Component Name='value'/></Symbol></Access><Blank/><Token Text=':='/><Blank/><Access Scope='LiteralConstant'><Constant><ConstantValue>42</ConstantValue></Constant></Access><Token Text=';'/></StructuredText>";
            c = ContentIndexer.Parse(Xml("Text", Unit(scl)));
            Check(c.facts.Any(f => f.kind == "source_excerpt" && f.text.Contains("DB.value := 42;")), "source reconstruction includes Access and tokens");
            c = ContentIndexer.Parse(ContentIndexer.WrapSource("Text", "V17", "\"DB\".value := \"DB\".\"literal.dot\";"));
            Check(c.references.Any(r => r.target == "DB.value"), "text source normalizes optional identifier quotes");
            Check(c.references.Any(r => r.target == "DB.\"literal.dot\""), "text source preserves literal member dot quotes");
            c = ContentIndexer.Parse(ContentIndexer.WrapSource("UDT", "V17", "TYPE \"UDT\" STRUCT a : Bool; END_STRUCT; END_TYPE"));
            Check(c.object_name == "UDT" && c.source_version == "V17" && c.facts.Any(f => f.kind == "source_excerpt"), "text and UDT readback capture");
            c = ContentIndexer.Parse(Xml("AWL", Unit("<StatementList><StlStatement><StlToken Text='T'/><Access Scope='Address'><Address Area='DB' Type='Byte' BlockNumber='1' BitOffset='8'/></Access></StlStatement></StatementList>")));
            Check(c.references.Any(r => r.relation == "writes" && r.target == "DB1.DBB1"), "STL known instruction direction");
            Check(c.facts.Any(f => f.kind == "source_excerpt" && f.text.Contains("T DB1.DBB1")), "STL reconstruction");
            if (args.Length > 0)
            {
                c = ContentIndexer.Parse(File.ReadAllText(args[0]));
                Check(c.facts.Count(f => f.kind == "direct_mapping") == 32, "live fixture has 32 direct output mappings");
                Check(c.references.Any(r => r.relation == "writes" && r.target == "OP275_Q_53_7"), "live fixture output tag write evidence");
            }
            Console.WriteLine("PASS: " + checks + " indexing checks (reconcile, freshness, concurrency, resume, caches, semantics)."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Directory.Delete(root, true); }
    }
}
