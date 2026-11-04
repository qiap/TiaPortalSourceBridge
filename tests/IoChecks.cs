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
using TiaPortalSourceBridge.Web;
using TiaPortalSourceBridge.Indexing;
internal static class IoChecks
{
    static int count;
    static void Check(bool ok, string name) { if (!ok) throw new Exception(name); count++; }
    static Dictionary<string, object> Map(object x) { return IndexJson.Map(x); }
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    static byte[] Request(WebServer server, HttpListener listener, string url, string path, string body, int expected, string origin = null)
    {
        Exception error = null;
        var thread = new Thread(() => { try { typeof(WebServer).GetMethod("ProcessRequest", Flags).Invoke(server, new object[] { listener.GetContext() }); } catch(Exception ex) { error = ex; } }) { IsBackground = true };
        thread.Start();
        var r = (HttpWebRequest)WebRequest.Create(url + path); r.Timeout = 5000;
        if (origin != null) r.Headers["Origin"] = origin;
        if (body != null) { r.Method = "POST"; r.ContentType = "application/json"; byte[] data = Encoding.UTF8.GetBytes(body); r.ContentLength = data.Length; using(var s = r.GetRequestStream()) s.Write(data, 0, data.Length); }
        HttpWebResponse response;
        try { response = (HttpWebResponse)r.GetResponse(); } catch(WebException ex) { response = (HttpWebResponse)ex.Response; if(response == null) throw; }
        byte[] bytes;
        using(response) using(var memory = new MemoryStream()) { Check((int)response.StatusCode == expected, path + " status"); response.GetResponseStream().CopyTo(memory); bytes = memory.ToArray(); }
        Check(thread.Join(5000) && error == null, "HTTP response completes"); return bytes;
    }
    static void VerifyByteMapping()
    {
        var fc = IndexEntry.Create("plc.block","PLC","Area/Station","Map","LAD","1");
        var db = IndexEntry.Create("plc.block","PLC","Data","Signals","DB","12");
        string fields = "<Member Name='Input' Datatype='Struct'><Member Name='Stop' Datatype='Bool'><Comment><Text>stop</Text></Comment></Member></Member><Member Name='Output' Datatype='Struct'><Member Name='Lamp' Datatype='Bool'/></Member>";
        string dbXml = "<Document><SW.Blocks.GlobalDB><AttributeList><Name>Signals</Name><Number>12</Number><MemoryLayout>Standard</MemoryLayout><Interface><Sections><Section Name='Static'>" + fields + "</Section></Sections></Interface></AttributeList></SW.Blocks.GlobalDB></Document>";
        Func<string,string,string> op = (name,access) => "<StlStatement><StlToken Text='" + name + "'/><Access Scope='" + (access.StartsWith("<Symbol") ? "GlobalVariable" : "Address") + "'>" + access + "</Access></StlStatement>";
        string input = "<Symbol><Component Name='InByte'/></Symbol>", output = "<Symbol><Component Name='OutByte'/></Symbol>";
        string db0 = "<Address Area='DB' Type='Byte' BlockNumber='12' BitOffset='0'/>", db2 = db0.Replace("Offset='0'", "Offset='16'");
        string xml = "<StatementList>" + op("L",input) + op("T",db0) + op("L",db2) + op("T",output) + "</StatementList>";
        Func<string,string,IoSnapshot> expand = (code,layout) => {
            var snap = new IoSnapshot();
            snap.rows.Add(new IoRow { id="i", name="InByte", data_type="Byte", address="%IB50", unit="", area="I" });
            snap.rows.Add(new IoRow { id="q", name="OutByte", data_type="Byte", address="%QB60", unit="", area="Q" });
            IoMapping.Expand(snap,new[] { fc,db },e => e == fc ? code : layout); return snap;
        };
        var rows = expand(xml,dbXml).rows.Where(r => r.mapping_fc != null).ToArray();
        Check(rows.Length == 2 && rows[0].address == "%I50.0" && rows[0].name == "Signals.Input.Stop" && rows[0].comment == "stop", "byte input to DB bool");
        Check(rows[1].address == "%Q60.0" && rows[1].name == "Signals.Output.Lamp" && rows[1].mapping_group == "Area/Station", "aligned DB output and project folder");
        Check(expand(xml,dbXml.Replace("Standard","Optimized")).rows.Count == 2, "no absolute layout inferred for optimized DB");
        Check(expand(xml.Replace("</StlStatement>","</StlStatement><StlStatement><StlToken Text='NOP'/></StlStatement>"),dbXml).rows.Count == 2,"no mapping across intervening instruction");
        Check(expand(xml,dbXml.Replace("Datatype='Struct'","Datatype='Unknown'")).rows.Count == 2, "unknown layout cannot generate points");
    }
    static void VerifyArrayDeclarations()
    {
        var db = IndexEntry.Create("plc.block","PLC","Station/Alarm","Flags","DB","12");
        string xml = "<Document><SW.Blocks.GlobalDB><AttributeList><Name>Flags</Name><Interface><Sections><Section Name='Static'><Member Name='Alarm' Datatype='Array[-1..0, 2..3] of Bool'><Subelement Path='-1,3'><Comment><Text>door open</Text></Comment></Subelement></Member><Member Name='Packed' Datatype='Array[20..21] of Byte'/><Member Name='Sub' Datatype='Struct'><Member Name='Ok' Datatype='Bool'/></Member></Section></Sections></Interface></AttributeList></SW.Blocks.GlobalDB></Document>";
        var snapshot = new IoSnapshot();
        IoDeclarations.Add(snapshot,new[] { db },e => xml);
        Check(snapshot.rows.Count == 21 && snapshot.rows.All(r => r.area=="DB" && r.group=="Station/Alarm" && r.address==""),"DB group and symbolic addresses");
        Check(snapshot.rows.Single(r => r.name=="Flags.Alarm[-1,3]").comment=="door open" && snapshot.rows.Any(r=>r.name=="Flags.Alarm[0,2]"),"two dimensional signed bounds and per-element comments");
        Check(snapshot.rows.Count(r => r.origin.Contains("Byte")) == 16 && snapshot.rows.Any(r=>r.name=="Flags.Packed[21].%X7"),"each packed byte contributes eight labelled bits");
        Check(snapshot.rows.Any(r=>r.name=="Flags.Sub.Ok") && snapshot.rows.Select(r=>r.id).Distinct().Count()==21,"struct Bool and distinct element identities");
    }
    static int Main(string[] args)
    {
        string root = Path.Combine(Path.GetTempPath(), "tia-io-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try {
            VerifyByteMapping();
            VerifyArrayDeclarations();
            string xml = "<Document><SW.Tags.PlcTagTable><ObjectList><SW.Tags.PlcTag><AttributeList><Name>Motor1</Name><DataTypeName>Bool</DataTypeName><LogicalAddress>%I10.2</LogicalAddress></AttributeList><ObjectList><MultilingualText CompositionName='Comment'><ObjectList><MultilingualTextItem><AttributeList><Culture>zh-CN</Culture><Text>启动 &amp; 复位</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText></ObjectList></SW.Tags.PlcTag><SW.Tags.PlcUserConstant><AttributeList><Name>Limit</Name><DataTypeName>Int</DataTypeName><Value>10</Value></AttributeList></SW.Tags.PlcUserConstant></ObjectList></SW.Tags.PlcTagTable></Document>";
            var rows = IoTable.Parse(xml, "默认变量表", "", "").ToList();
            Check(rows.Count == 2 && rows[0].area == "I" && rows[1].area == "CONST", "tags and constants");
            Check(rows[0].comment.Contains("启动 & 复位"), "multilingual comment");
            Check(IoTable.Address("%E10.2") == "I10.2" && IoTable.Address("%AW12") == "QW12", "localized addresses");
            Check(IoTable.SortAddress(new IoRow { address = "%I2.0" }) < IoTable.SortAddress(rows[0]), "numeric address order");
            Check(IoTable.Filter(rows, "IO", "", "").Count() == 1, "I/Q filter");
            Check(IoTable.Filter(rows, "ALL", "启动", "").Count() == 1, "comment search");
            bool rejected = false; try { IoTable.Parse("<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///missing'>]><x>&e;</x>", "", "", "").ToArray(); } catch(XmlException) { rejected = true; }
            Check(rejected, "DTD prohibited");
            var snapshot = new IoSnapshot { project_path = Path.Combine(root,"A.ap17"), plc_name = "PLC1", tables_read = 1, tables_total = 2,
                read_started_utc = DateTime.UtcNow.ToString("o"), read_completed_utc = DateTime.UtcNow.ToString("o"), rows = rows };
            var refs = Map(new { total = 3, ready = 2, errors = new[] { new { name = "Missing", state = "error", error = "export failed" } }, references = new[] {
                new { block="A", group="Main", unit="", target="\"Motor1\"", access="reads", location="network 1", evidence="native", state="ready" },
                new { block="B", group="Main", unit="", target="Motor10", access="reads", location="network 2", evidence="native", state="ready" },
                new { block="C", group="Main", unit="Unit1", target="Motor1", access="writes", location="network 1", evidence="native", state="ready" },
                new { block="D", group="Main", unit="", target="Motor1", access="reads", location="network 1", evidence="scope=LocalVariable", state="ready" },
                new { block="E", group="Main", unit="", target="%I10.2", access="writes", location="network 3", evidence="native", state="stale" }
            }});
            IoTable.AttachIndex(snapshot, refs);
            Check(rows[0].references.Count == 2, "exact symbol/address, unit and local scope isolation");
            Check(snapshot.warnings.Count == 1 && rows[0].references[1].state == "stale", "partial/stale evidence visible");
            var special = new IoSnapshot { rows = new List<IoRow> { new IoRow { name = "Motor start", address = "", unit = "" } } };
            IoTable.AttachIndex(special, Map(new { references = new[] { new { target = "\"Motor start\"", unit = "", evidence = "scope=GlobalVariable" } } }));
            Check(special.rows[0].references.Count == 1, "quoted tag name with spaces");
            var native = Map(new { truncated=true, items=new[] { new { name="Motor1", address="%I10.2", references=new[] { new { name="OfficialBlock", path="Main", locations=new[] { new { access="Read", reference_location="Network 8", referenced_as_name="Motor1" } } } } } } });
            IoTable.AttachNative(snapshot, native, "", "默认变量表", "");
            Check(rows[0].references.Count == 3 && snapshot.warnings.Count == 2, "native nesting and truncation");
            IoTable.AttachNative(snapshot, Map(new { items = native["items"] }), "", "OtherTable", "");
            Check(rows[0].references.Count == 3, "native references cannot cross tag tables");
            rows[0].comment = "=HYPERLINK(\"https://example.invalid\")\n中文 & < >";
            byte[] workbook = IoWorkbook.Create(snapshot, rows.ToArray());
            File.WriteAllBytes(args[0], workbook);
            using(var archive = new ZipArchive(new MemoryStream(workbook), ZipArchiveMode.Read)) {
                foreach (var entry in archive.Entries) {
                    var part = new XmlDocument(); using (var stream = entry.Open()) part.Load(stream);
                    Check(part.DocumentElement != null, "valid XML package part: " + entry.FullName);
                }
                Check(archive.GetEntry("xl/worksheets/sheet3.xml") != null, "three worksheet package");
                var doc = new XmlDocument(); using(var s = archive.GetEntry("xl/worksheets/sheet1.xml").Open()) doc.Load(s);
                Check(doc.SelectNodes("//*[local-name()='f']").Count == 0, "no formulas from comments");
                Check(doc.SelectSingleNode("//*[local-name()='c' and @r='E2']").Attributes["t"].Value == "inlineStr", "formula-like comment is text");
                Check(doc.SelectSingleNode("//*[local-name()='c' and @r='E2']").InnerText == rows[0].comment, "Excel text roundtrip");
            }
            // The real index must exclude another PLC with a similar name.
            var store = new ProjectIndexStore(snapshot.project_path, "V17");
            store.BuildCatalog(w => new List<IndexEntry> { IndexEntry.Create("plc.block","PLC1","","A"), IndexEntry.Create("plc.block","PLC10","","B") });
            Check(Convert.ToInt32(Map(store.IoReferenceSnapshot("PLC1"))["total"]) == 1, "index exact PLC scope");
            store.Hydrate(store.Load().objects, e => xml, 100, false);
            int rereads = 0;
            store.RefreshIoContent("PLC1");
            store.Hydrate(store.Load().objects, e => { rereads++; return xml; }, 100, false);
            Check(rereads == 1, "refresh rereads selected PLC even without changed metadata");
            string config = Path.Combine(root,"config.json"); File.WriteAllText(config, IndexJson.Serializer().Serialize(new { tiaPortalVersion="V17", projectPath=snapshot.project_path, sourceDirectory=root })); File.WriteAllText(snapshot.project_path,"fixture");
            var probe = new TcpListener(IPAddress.Loopback,0); probe.Start(); int port=((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            var server=new WebServer(config,port); var listener=(HttpListener)typeof(WebServer).GetField("_listener",Flags).GetValue(server); listener.Start();
            try {
                string url="http://127.0.0.1:"+port;
                Request(server,listener,url,"/api/io/read","{}",409);
                Request(server,listener,url,"/api/io/read","{}",403,"https://foreign.example");
                Request(server,listener,url,"/api/io/export?id=unknown",null,404);
                var jobType=typeof(WebServer).GetNestedType("IoJob",BindingFlags.NonPublic); var job=Activator.CreateInstance(jobType,true);
                jobType.GetField("id").SetValue(job,"fixture"); jobType.GetField("state").SetValue(job,"completed"); jobType.GetField("result").SetValue(job,snapshot);
                ((IDictionary)typeof(WebServer).GetField("ioJobs",Flags).GetValue(server)).Add("fixture",job);
                var data=IndexJson.Serializer().Deserialize<Dictionary<string,object>>(Encoding.UTF8.GetString(Request(server,listener,url,"/api/io/data?id=fixture&area=CONST",null,200)));
                Check(Convert.ToInt32(data["total"])==1 && Convert.ToString(data["plc_name"])=="PLC1", "snapshot target and filter");
                Request(server,listener,url,"/api/io/references?id=fixture&row="+rows[0].id,null,200);
                var exported=Request(server,listener,url,"/api/io/export?id=fixture&area=CONST",null,200);
                using(var zip=new ZipArchive(new MemoryStream(exported),ZipArchiveMode.Read)) { var doc=new XmlDocument();using(var s=zip.GetEntry("xl/worksheets/sheet1.xml").Open())doc.Load(s);Check(doc.SelectNodes("//*[local-name()='row']").Count==2,"export uses full filter"); }
                var guard=(ActionTarget)typeof(WebServer).GetField("actionTargets",Flags).GetValue(server);
                var lanes=(Dictionary<string,ActionProgress>)typeof(ActionRunner).GetField("_lanes",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);
                lanes["tia"]=new ActionProgress {status="running"};
                try { Request(server,listener,url,"/api/io/read",IndexJson.Serializer().Serialize(new {project_path=snapshot.project_path,plc_name="PLC1",target_token=guard.Sign(snapshot.project_path,"PLC1")}),409); }
                finally { lanes.Clear(); }
            } finally { listener.Close(); }
            Console.WriteLine("PASS: "+count+" I/O checks (parsing, scoped references, workbook, HTTP, busy target; no TIA writes).");return 0;
        } catch(Exception ex) { Console.Error.WriteLine(ex);return 1; }
        // Temporary fixtures contain no engineering data and are retained for diagnosis.
    }
}
