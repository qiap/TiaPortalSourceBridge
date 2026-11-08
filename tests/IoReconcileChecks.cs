using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Xml;
using TiaPortalSourceBridge.Indexing;
using TiaPortalSourceBridge.Web;

internal static class IoReconcileChecks
{
    static int checks;
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
    static void Reject(Action action, string message) { bool rejected = false; try { action(); } catch (ArgumentException) { rejected = true; } catch (InvalidOperationException) { rejected = true; } catch (XmlException) { rejected = true; } catch (InvalidDataException) { rejected = true; } Check(rejected, message); }
    static byte[] Book(params string[][] rows) { return IoWorkbook.CreateSheets(new[] { "Points" }, new[] { new[] { new[] { "地址", "变量名", "数据类型", "注释" } }.Concat(rows) }); }
    static IoSheetImport Load(params string[][] rows) { return IoSheetImport.Load(Book(rows), "点表.xlsx"); }
    static IoRow Point(string address, string name, string type = "Bool", string comment = "", string unit = "", string table = "Tags")
    { return new IoRow { address = address, name = name, data_type = type, comment = comment, unit = unit, table = table, group = "IO" }; }
    static IoSnapshot Snapshot(params IoRow[] rows) { return new IoSnapshot { project_path = "fixture.ap17", plc_name = "CPU", tables_total = 1, tables_read = 1, read_started_utc = "2026-11-08T01:00:00Z", read_completed_utc = "2026-11-08T01:01:00Z", rows = rows.ToList() }; }
    static IoReconcileOptions Options() { return new IoReconcileOptions { sheet = "Points", header_row = 1, address = 0, name = 1, type = 2, comment = 3 }; }
    static IoRow NativeComment(string text)
    {
        var value = System.Security.SecurityElement.Escape(text);
        return IoTable.Parse("<Document><SW.Tags.PlcTag><AttributeList><Name>Sensor</Name><LogicalAddress>%I0.0</LogicalAddress><DataTypeName>Bool</DataTypeName></AttributeList><ObjectList><MultilingualText CompositionName='Comment'><ObjectList><MultilingualTextItem><AttributeList><Culture>zh-CN</Culture><Text>" + value + "</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText></ObjectList></SW.Tags.PlcTag></Document>", "Tags", "IO", "").Single();
    }
    static byte[] Rewrite(byte[] bytes, Action<Dictionary<string, string>> edit)
    {
        var parts = new Dictionary<string, string>();
        using (var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read)) foreach (var entry in zip.Entries) using (var reader = new StreamReader(entry.Open())) parts.Add(entry.FullName, reader.ReadToEnd());
        edit(parts);
        using (var memory = new MemoryStream()) { using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true)) foreach (var part in parts) using (var writer = new StreamWriter(zip.CreateEntry(part.Key).Open(), new UTF8Encoding(false))) writer.Write(part.Value); return memory.ToArray(); }
    }
    static void EditSheet(Dictionary<string, string> parts, Action<XmlDocument> edit)
    { var doc = new XmlDocument(); doc.LoadXml(parts["xl/worksheets/sheet1.xml"]); edit(doc); parts["xl/worksheets/sheet1.xml"] = doc.OuterXml; }
    static void Import()
    {
        var file = Load(new[] { "%E0002.0", "到位", "BOOL", "中文 & < >" });
        Check(file.Sheets.Count == 1 && file.Sheets[0].Rows[1].Cells[3].Value == "中文 & < >", "inline strings and Unicode round-trip");
        var preview = IndexJson.Map(file.Preview("Points", 0));
        Check(Convert.ToInt32(preview["header_row"]) == 1 && Convert.ToInt32(((Dictionary<string, object>)preview["suggested"])["address"]) == 0, "header suggestions use column identity");
        Check(file.Sheets[0].Rows[1].Number == 2, "original Excel row retained");
        byte[] shared = Rewrite(Book(new[] { "I2.0", "Name", "Bool", "" }), p => {
            p["xl/sharedStrings.xml"] = "<sst><si><r><t>共享</t></r><r><t>变量</t></r><rPh><t>phonetic</t></rPh></si></sst>";
            EditSheet(p, d => { var c = (XmlElement)d.SelectSingleNode("//*[local-name()='c' and @r='B2']"); c.RemoveAll(); c.SetAttribute("r", "B2"); c.SetAttribute("t", "s"); c.InnerXml = "<v>0</v>"; });
        });
        Check(IoSheetImport.Load(shared, "shared.xlsx").Sheets[0].Rows[1].Cells[1].Value == "共享变量", "rich shared strings exclude phonetic annotations");
        byte[] formula = Rewrite(Book(new[] { "I2.0", "Name", "Bool", "" }), p => EditSheet(p, d => { var c = d.SelectSingleNode("//*[local-name()='c' and @r='B2']"); c.InnerXml = "<f>CONCAT(\"Name\")</f><v>Name</v>"; }));
        var formulaFile = IoSheetImport.Load(formula, "formula.xlsx");
        Check(formulaFile.Sheets[0].Rows[1].Cells[1].Error.Contains("公式") && formulaFile.Sheets[0].Rows[1].Cells[1].Value == "", "formula cached results are never trusted");
        var report = IoReconcile.Compare(formulaFile, Snapshot(Point("I2.0", "Name")), Options());
        Check(report.rows[0].status == "invalid", "formula in mapped column becomes explicit invalid row");
        byte[] merged = Rewrite(Book(new[] { "I2.0", "Name", "Bool", "" }), p => EditSheet(p, d => { var m = d.CreateElement("mergeCells", d.DocumentElement.NamespaceURI); var c = d.CreateElement("mergeCell", m.NamespaceURI); c.SetAttribute("ref", "B2:B3"); m.AppendChild(c); d.DocumentElement.AppendChild(m); }));
        Check(IoReconcile.Compare(IoSheetImport.Load(merged, "merged.xlsx"), Snapshot(Point("I2.0", "Name")), Options()).rows[0].status == "invalid", "merged data cells are not propagated or guessed");
        Reject(() => IoSheetImport.Load(Book(), "old.xls"), "old binary xls rejected");
        Reject(() => IoSheetImport.Load(Book(), "../file.xlsx"), "upload accepts names, never paths");
        Reject(() => IoSheetImport.Load(new byte[IoSheetImport.MaxBytes + 1], "large.xlsx"), "compressed size limit");
        Reject(() => IoSheetImport.Load(Rewrite(Book(), p => p["xl/workbook.xml"] = "<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///missing'>]><x>&e;</x>"), "dtd.xlsx"), "DTD entities prohibited");
        Reject(() => IoSheetImport.Load(Rewrite(Book(), p => p["xl/_rels/workbook.xml.rels"] = p["xl/_rels/workbook.xml.rels"].Replace("Target=\"worksheets/sheet1.xml\"", "Target=\"https://external.invalid/sheet.xml\" TargetMode=\"External\"")), "external.xlsx"), "external worksheets rejected");
        Reject(() => IoSheetImport.Load(Rewrite(Book(), p => p["xl/_rels/workbook.xml.rels"] = p["xl/_rels/workbook.xml.rels"].Replace("worksheets/sheet1.xml", "../../secret.xml")), "traversal.xlsx"), "relationship cannot escape workbook worksheet area");
        Reject(() => IoSheetImport.Load(Rewrite(Book(), p => p["xl/vbaProject.bin"] = "macro"), "macro.xlsx"), "disguised macro workbook rejected");
        Reject(() => IoSheetImport.Load(Rewrite(shared, p => p["xl/sharedStrings.xml"] = "<sst/>"), "index.xlsx"), "bad shared string index rejected");
        Reject(() => IoSheetImport.Load(Rewrite(Book(new[] { "I2.0" }), p => EditSheet(p, d => { var c = d.SelectSingleNode("//*[local-name()='c' and @r='A2']"); c.Attributes["r"].Value = "DZ2"; })), "wide.xlsx"), "populated columns beyond limit rejected");
        var headerFile = IoSheetImport.Load(IoWorkbook.CreateSheets(new[] { "title" }, new[] { new[] { new[] { "项目名称" }, new[] { "地址", "名称" }, new[] { "I0.0", "Sensor" } }.AsEnumerable() }), "title.xlsx");
        Check(Convert.ToInt32(IndexJson.Map(headerFile.Preview("title", 0))["header_row"]) == 2, "header below title detected");
        Reject(() => headerFile.Preview("title", 999), "missing header rejected");
        var duplicateHeader = Load(new[] { "I0.0", "A", "Bool", "" }); duplicateHeader.Sheets[0].Rows[0].Cells[3].Value = "地址";
        Check(IoSheetImport.Suggest(duplicateHeader.Sheets[0], duplicateHeader.Sheets[0].Rows[0])["address"] == -1, "duplicate header labels require manual mapping");
    }
    static void CompareAndExport(string directory)
    {
        var file = Load(new[] { "%E0002.0", "Sensor", "BOOL", "same" }, new[] { "%Q3.0", "NewValve", "Bool", "changed" }, new[] { "I5.0", "Moved", "Int", "" }, new[] { "Q8.0", "Added", "Bool", "" });
        var snapshot = Snapshot(Point("I2.0", "Sensor", comment: "same"), Point("Q3.0", "OldValve"), Point("I6.0", "Moved"), Point("Q9.0", "NotListed"), Point("I2.0", "OtherUnit", unit: "U2"));
        string before = IndexJson.Serializer().Serialize(snapshot);
        var report = IoReconcile.Compare(file, snapshot, Options());
        Check(report.rows[0].status == "match" && report.rows[0].address == "%E0002.0", "address normalized for comparison but original preserved");
        Check(report.rows[1].status == "different" && report.rows[1].issues.Count == 2, "address match shows renamed symbol and changed comment");
        Check(report.rows[2].status == "different" && report.rows[2].issues.Contains("同名变量地址不同") && report.rows[2].issues.Contains("数据类型不同"), "name fallback shows address and type differences");
        Check(report.counts["excel_only"] == 1 && report.counts["plc_only"] == 1 && report.plc_rows == 4, "two-sided missing entries stay within selected unit");
        Check(IndexJson.Serializer().Serialize(snapshot) == before, "comparison never mutates PLC snapshot");
        report = IoReconcile.Compare(Load(new[] { "I0.0", "Sensor", "Bool", "到位\n第二行" }), Snapshot(NativeComment("到位\n第二行")), Options());
        Check(report.rows[0].status == "match" && report.rows[0].plc[0].comment.StartsWith("zh-CN:"), "single-culture native comment compares content and retains original language label");
        report = IoReconcile.Compare(Load(new[] { "I0.0", "Sensor", "Bool", "" }), Snapshot(NativeComment("")), Options());
        Check(report.rows[0].status == "match", "empty native comment language label does not manufacture a difference");
        report = IoReconcile.Compare(Load(new[] { "I0.0", "Sensor", "Bool", "到位" }), Snapshot(Point("I0.0", "Sensor", comment: "zh-CN: 到位\nen-US: At position")), Options());
        Check(report.rows[0].status == "different", "multiple translations are not guessed from Excel text");
        report = IoReconcile.Compare(Load(new[] { "I0.0", "Sensor", "Bool", "到位" }), Snapshot(Point("I0.0", "Sensor", comment: "zh-CN: 到位")), Options());
        Check(report.rows[0].status == "different", "a literal culture-like prefix is not removed without native comment metadata");
        var o = Options(); o.comment = -1; o.type = -1;
        report = IoReconcile.Compare(Load(new[] { "I0.0", "Same", "Int", "different" }), Snapshot(Point("I0.0", "Same")), o);
        Check(report.rows[0].status == "match" && report.warnings.Count(w => w.Contains("未选择")) == 2, "optional unmapped fields are not compared and coverage is explicit");
        report = IoReconcile.Compare(Load(new[] { "Q0.0", "A", "Bool", "" }, new[] { "%A000.0", "B", "Bool", "" }), Snapshot(Point("Q0.0", "A")), Options());
        Check(report.rows.Take(2).All(r => r.status == "conflict" && r.issues.Any(i => i.Contains("重复地址"))), "Excel duplicate address normalization");
        report = IoReconcile.Compare(Load(new[] { "Q0.0", "A", "Bool", "" }, new[] { "Q1.0", "a", "Bool", "" }), Snapshot(Point("Q0.0", "A")), Options());
        Check(report.rows.Take(2).All(r => r.status == "conflict"), "duplicate names are explicit");
        report = IoReconcile.Compare(Load(new[] { "Q0.0", "A", "Bool", "" }), Snapshot(Point("QB0", "OutputByte", "Byte")), Options());
        Check(report.rows[0].status == "conflict" && report.rows[0].plc[0].address == "QB0", "bit-byte overlap yields original PLC evidence");
        report = IoReconcile.Compare(Load(new[] { "MW10", "Word", "Word", "" }, new[] { "M11.7", "Bit", "Bool", "" }), Snapshot(Point("MW10", "Word", "Word")), Options());
        Check(report.rows.Take(2).All(r => r.status == "conflict"), "Excel word spans second byte");
        report = IoReconcile.Compare(Load(new[] { "Q0.0", "A", "Bool", "" }), Snapshot(Point("Q0.0", "A"), Point("Q0.0", "Alias")), Options());
        Check(report.rows[0].status == "ambiguous" && report.rows[0].plc.Count == 2, "PLC address aliases remain multiple candidates");
        report = IoReconcile.Compare(Load(new[] { "Q0.0", "A", "Bool", "" }), Snapshot(Point("Q0.0", "B"), Point("Q1.0", "A")), Options());
        Check(report.rows[0].status == "conflict" && report.rows[0].plc.Count == 2, "same name on different occupied address exposes possible swapped assignment");
        report = IoReconcile.Compare(Load(new[] { "52.0", "MissingArea", "Bool", "" }, new[] { "I1.8", "BadBit", "Bool", "" }, new[] { "DB1.DBX0.0", "DB", "Bool", "" }), Snapshot(Point("Q0.0", "A")), Options());
        Check(report.rows.Take(3).All(r => r.status == "invalid"), "no inference for bare numeric, bad bits or DB absolute addresses");
        var mapped = Point("Q0.0", "DB.Output.Valve"); mapped.mapping_fc = "Map";
        o = Options(); o.scope = "mapped";
        report = IoReconcile.Compare(Load(new[] { "Q0.0", "DB.Output.Valve", "Bool", "" }), Snapshot(Point("QB0", "Carrier", "Byte"), mapped), o);
        Check(report.rows[0].status == "match" && report.plc_rows == 1, "mapped view excludes carrier variable from overlap findings");
        o = Options(); o.table = "IO\nSubset";
        report = IoReconcile.Compare(Load(new[] { "Q0.0", "A", "Bool", "" }), Snapshot(Point("Q0.0", "A", table: "Subset"), Point("Q1.0", "B")), o);
        Check(report.plc_rows == 1 && report.rows.Count == 1, "variable table scope affects missing entries");
        o = Options(); o.unit = "Unknown"; Reject(() => IoReconcile.Compare(file, snapshot, o), "unknown unit never falls back to all units");
        o = Options(); o.name = 0; Reject(() => IoReconcile.Compare(file, snapshot, o), "duplicate mapped columns rejected");
        o = Options(); o.address = 99; Reject(() => IoReconcile.Compare(file, snapshot, o), "out of range columns rejected");
        Reject(() => IoReconcile.Compare(Load(), snapshot, Options()), "empty data after header rejected");
        snapshot.tables_read = 0; report = IoReconcile.Compare(file, snapshot, Options());
        Check(report.warnings.Any(w => w.Contains("快照读取不完整")), "incomplete PLC snapshot exposed");
        report.rows[0].comment = "=HYPERLINK(\"bad\")\n<xml>&"; report.source_mode = "local_cache";
        byte[] exported = IoReconcileWorkbook.Create(report); File.WriteAllBytes(Path.Combine(directory, "reconcile-report.xlsx"), exported); File.WriteAllBytes(Path.Combine(directory, "electrical-input.xlsx"), Book(new[] { "I0.0", "Sensor", "Bool", "" }));
        using (var zip = new ZipArchive(new MemoryStream(exported), ZipArchiveMode.Read)) {
            Check(zip.Entries.Count(e => e.FullName.StartsWith("xl/worksheets/")) == 3, "three-sheet report includes overview, complete rows and candidates");
            foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith("xl/worksheets/"))) {
                var doc = new XmlDocument(); using (var stream = entry.Open()) doc.Load(stream);
                Check(doc.SelectNodes("//*[local-name()='f' or local-name()='hyperlink']").Count == 0, "export does not create formulas or hyperlinks");
            }
            var data = new XmlDocument(); using (var stream = zip.GetEntry("xl/worksheets/sheet2.xml").Open()) data.Load(stream);
            Check(data.SelectNodes("//*[local-name()='row']").Count == report.rows.Count + 1, "export contains all rows independent of browser filter");
            Check(data.InnerText.Contains(report.rows[0].comment), "source values including formula-looking text preserved");
        }
        var many = Enumerable.Range(0, 5000).Select(i => new[] { "I0.0", "Same", "Bool", "" }).ToArray();
        var watch = System.Diagnostics.Stopwatch.StartNew(); report = IoReconcile.Compare(Load(many), Snapshot(Point("I0.0", "Same")), Options());
        Check(report.excel_rows == 5000 && report.rows.All(r => r.status == "conflict") && watch.ElapsedMilliseconds < 10000, "dense duplicate spreadsheet remains bounded and fully reported");
    }
    static object Field(object o, string field) { return o.GetType().GetField(field, Private).GetValue(o); }
    static Dictionary<string, object> D(params object[] values) { var d = new Dictionary<string, object>(); for (int i = 0; i < values.Length; i += 2) d[(string)values[i]] = values[i + 1]; return d; }
    static byte[] Request(WebServer server, HttpListener listener, string url, string path, object body, int expected, string origin = null)
    {
        Exception failure = null; var thread = new Thread(() => { try { typeof(WebServer).GetMethod("ProcessRequest", Private).Invoke(server, new object[] { listener.GetContext() }); } catch (Exception ex) { failure = ex; } }); thread.IsBackground = true; thread.Start();
        var request = (HttpWebRequest)WebRequest.Create(url + path); request.Timeout = 10000; request.KeepAlive = false; if (origin != null) request.Headers["Origin"] = origin;
        if (body != null) { request.Method = "POST"; request.ContentType = "application/json"; byte[] bytes = Encoding.UTF8.GetBytes(body is string ? (string)body : IndexJson.Serializer().Serialize(body)); request.ContentLength = bytes.Length; using (var stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length); }
        HttpWebResponse response; try { response = (HttpWebResponse)request.GetResponse(); } catch (WebException ex) { response = (HttpWebResponse)ex.Response; if (response == null) throw; }
        using (response) using (var memory = new MemoryStream()) {
            response.GetResponseStream().CopyTo(memory); var bytes = memory.ToArray();
            Check((int)response.StatusCode == expected, path + " expected " + expected + ", got " + response.StatusCode + ": " + (expected == 200 && path.Contains("export") ? "xlsx" : Encoding.UTF8.GetString(bytes)));
            Check(string.IsNullOrEmpty(response.Headers["Access-Control-Allow-Origin"]), "reconciliation has no wildcard CORS");
            Check(thread.Join(10000) && failure == null, "handler completed without hidden exception"); return bytes;
        }
    }
    static Dictionary<string, object> Json(byte[] bytes) { return IndexJson.Serializer().Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(bytes)); }
    static void Server(string directory)
    {
        string project = Path.Combine(directory, "Fixture.ap17"), config = Path.Combine(directory, "config.json"); File.WriteAllText(project, "offline fixture");
        File.WriteAllText(config, IndexJson.Serializer().Serialize(new { tiaPortalVersion = "V17", projectPath = project, sourceDirectory = directory }));
        var snapshot = Snapshot(Point("I0.0", "Sensor")); snapshot.project_path = project;
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); int port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        var server = new WebServer(config, port); var listener = (HttpListener)Field(server, "_listener"); listener.Start();
        try {
            string url = "http://127.0.0.1:" + port;
            var jobType = typeof(WebServer).GetNestedType("IoJob", BindingFlags.NonPublic); var job = Activator.CreateInstance(jobType, true);
            foreach (var pair in D("id", "snapshot", "state", "completed", "project", project, "plc", "CPU", "result", snapshot, "cached", true)) jobType.GetField(pair.Key).SetValue(job, pair.Value);
            ((IDictionary)Field(server, "ioJobs")).Add("snapshot", job);
            var guard = (ActionTarget)Field(server, "actionTargets");
            var upload = D("project_path", project, "plc_name", "CPU", "target_token", guard.Sign(project, "CPU"), "file_name", "电气.xlsx", "content_base64", Convert.ToBase64String(Book(new[] { "I0.0", "Sensor", "Bool", "" })));
            Request(server, listener, url, "/api/io/reconcile-upload", upload, 403, "https://foreign.invalid");
            Request(server, listener, url, "/api/io/reconcile-upload", "{", 400);
            Request(server, listener, url, "/api/io/reconcile-upload", D("project_path", project, "plc_name", "CPU", "target_token", "wrong"), 409);
            var options = Json(Request(server, listener, url, "/api/io/reconcile-options?id=snapshot", null, 200)); Check((string)options["plc_name"] == "CPU", "options carry exact snapshot target");
            var uploaded = Json(Request(server, listener, url, "/api/io/reconcile-upload", upload, 200)); string id = (string)uploaded["upload_id"];
            Request(server, listener, url, "/api/io/reconcile-preview?upload_id=" + id + "&sheet=Points&header_row=1", null, 200);
            var run = D("project_path", project, "plc_name", "CPU", "target_token", guard.Sign(project, "CPU"), "upload_id", id, "id", "snapshot", "options", Options());
            var report = Json(Request(server, listener, url, "/api/io/reconcile-run", run, 200)); Check((string)report["snapshot_id"] == "snapshot" && (string)report["source_mode"] == "local_cache", "report binds to actual read snapshot");
            run["options"] = D("header_row", 1, "address", 0.5, "name", 1, "type", 2, "comment", 3);
            Request(server, listener, url, "/api/io/reconcile-run", run, 400); run["options"] = Options();
            var exported = Request(server, listener, url, "/api/io/reconcile-export?id=" + report["id"], null, 200); Check(exported[0] == 'P' && exported[1] == 'K', "HTTP export is xlsx");
            run["plc_name"] = "Other"; run["target_token"] = guard.Sign(project, "Other"); Request(server, listener, url, "/api/io/reconcile-run", run, 409);
            run["plc_name"] = "CPU"; run["target_token"] = guard.Sign(project, "CPU"); run["id"] = "missing"; Request(server, listener, url, "/api/io/reconcile-run", run, 409); run["id"] = "snapshot";
            ((HashSet<string>)Field(server, "consumedCylinderSnapshots")).Add("snapshot"); Request(server, listener, url, "/api/io/reconcile-run", run, 409); ((HashSet<string>)Field(server, "consumedCylinderSnapshots")).Clear();
            for (int i = 0; i < 3; i++) Request(server, listener, url, "/api/io/reconcile-upload", upload, 200);
            Request(server, listener, url, "/api/io/reconcile-run", run, 409);
            Request(server, listener, url, "/api/io/reconcile-export?id=missing", null, 409);
            Request(server, listener, url, "/api/io/reconcile-run", null, 405);
            Check(File.ReadAllText(project) == "offline fixture", "read-only HTTP workflow leaves project untouched");
        } finally { listener.Close(); }
    }
    static int Main(string[] args)
    {
        try { string directory = args[0]; Directory.CreateDirectory(directory); Import(); CompareAndExport(directory); Server(directory); Console.WriteLine("PASS: " + checks + " electrical workbook import, reconciliation, export and HTTP checks; no TIA connection or engineering writes."); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
