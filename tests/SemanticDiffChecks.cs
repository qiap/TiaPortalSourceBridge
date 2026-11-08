using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using TiaPortalSourceBridge.Web;
using TiaPortalSourceBridge.Indexing;
using TiaPortalSourceBridge.Openness;

internal static class SemanticDiffChecks
{
    static int count;
    static void Check(bool ok, string message) { count++; if (!ok) throw new Exception(message); }
    static void Reject(Action action, string message) { bool rejected = false; try { action(); } catch (Exception) { rejected = true; } Check(rejected, message); }
    static string Xml(string body) { return "<Document><Engineering version='V17'/><DocumentInfo><Created>yesterday</Created></DocumentInfo><SW.Blocks.FB ID='0'><AttributeList><Name>Host</Name><ProgrammingLanguage>LAD</ProgrammingLanguage><Interface><Sections xmlns='urn:interface'><Section Name='Static'><Member Name='Z01' Datatype='Int'><StartValue>1</StartValue></Member><Member Name='Z02' Datatype='Bool'/></Section></Sections></Interface></AttributeList><ObjectList>" + body + "</ObjectList></SW.Blocks.FB></Document>"; }
    static string Net = "<SW.Blocks.CompileUnit ID='3'><AttributeList><NetworkSource><FlgNet><Parts><Access UId='1' Scope='GlobalVariable'><Symbol><Component Name='Sensor'/></Symbol></Access><Part UId='2' Name='Contact'/><Part UId='4' Name='Coil'/></Parts><Wires><Wire UId='5'><IdentCon UId='1'/><NameCon UId='2' Name='operand'/></Wire><Wire UId='6'><NameCon UId='2' Name='out'/><NameCon UId='4' Name='in'/></Wire></Wires></FlgNet></NetworkSource></AttributeList></SW.Blocks.CompileUnit>";
    static void Changed(string a, string b, string evidence) { var d = SemanticDiff.Compare(a,b); Check(d.status == "structural" && d.changes.Any(c => c.path.Contains(evidence)), "Expected change: " + evidence); }
    static void HttpChecks(string root, string project, string backup)
    {
        string config=Path.Combine(root,"config.json");
        File.WriteAllText(config,IndexJson.Serializer().Serialize(new { tiaPortalVersion="V17", projectPath=project, sourceDirectory=root }));
        var probe=new TcpListener(IPAddress.Loopback,0);probe.Start();int port=((IPEndPoint)probe.LocalEndpoint).Port;probe.Stop();
        var server=new WebServer(config,port); var flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var listener=(HttpListener)typeof(WebServer).GetField("_listener",flags).GetValue(server);
        var target=(ActionTarget)typeof(WebServer).GetField("actionTargets",flags).GetValue(server);
        string url="http://127.0.0.1:"+port;
        string query="?project_path="+Uri.EscapeDataString(project)+"&plc_name=PLC&target_token="+Uri.EscapeDataString(target.Sign(project,"PLC"));
        listener.Start();
        try {
            Func<string,string,string,int,string> request=(route,method,origin,expected)=>{
                Exception failure=null;
                var handler=new Thread(()=>{try { typeof(WebServer).GetMethod("ProcessRequest",flags).Invoke(server,new object[]{listener.GetContext()}); } catch(Exception ex){failure=ex;} });
                handler.IsBackground=true;handler.Start();
                var req=(HttpWebRequest)WebRequest.Create(url+route);req.Timeout=5000;req.Method=method;req.KeepAlive=false;
                if(origin!=null)req.Headers["Origin"]=origin;
                if(method=="POST")req.ContentLength=0;
                HttpWebResponse response;
                try{response=(HttpWebResponse)req.GetResponse();}catch(WebException ex){response=(HttpWebResponse)ex.Response;if(response==null)throw;}
                string body;
                using(response)using(var reader=new StreamReader(response.GetResponseStream())) {
                    body=reader.ReadToEnd();
                    Check((int)response.StatusCode==expected,"HTTP status "+expected+": "+body);
                    Check(string.IsNullOrEmpty(response.Headers["Access-Control-Allow-Origin"]),"diff CORS removed");
                    Check(response.Headers["Cache-Control"]=="no-store","diff not cached");
                }
                Check(handler.Join(5000)&&failure==null,"HTTP handler completes");return body;
            };
            Check(request("/api/backups"+query,"GET",url,200).Contains("Host"),"nested backup via HTTP");
            Check(request("/api/diff"+query+"&backup="+Uri.EscapeDataString(backup),"GET",url,200).Contains("snapshot_sha256"),"HTTP comparison evidence");
            request("/api/diff"+query+"&backup=missing.xml","GET",url,409);
            request("/api/backups","GET",url,409);
            request("/api/diff"+query,"POST",url,405);
            request("/api/backups"+query,"GET","https://foreign.example",403);
            request("/api/diff"+query+"&backup=..%2Fescape.xml","GET",url,409);
            request("/api/backups"+query.Replace("plc_name=PLC","plc_name=Other"),"GET",url,409);
        } finally { listener.Close(); }
    }
    static void Main(string[] args)
    {
        string xml = Xml(Net);
        Check(SemanticDiff.Compare(xml,xml).changes.Count == 0, "same");
        Check(SemanticDiff.Compare(xml,xml.Replace("yesterday","today").Replace("ID='0'","ID='20'").Replace("ID='3'","ID='30'")).changes.Count == 0,"export metadata noise");
        string renumber = xml;
        for (int i=1;i<=6;i++) renumber = renumber.Replace("UId='"+i+"'", "UId='"+(100+i)+"'");
        Check(SemanticDiff.Compare(xml,renumber).changes.Count==0,"consistent UId renumber");
        Changed(xml,xml.Replace("NameCon UId='2' Name='out'","NameCon UId='4' Name='out'"),"网络");
        Changed(xml,xml.Replace("Name='Sensor'","Name='Other'"),"网络");
        Changed(xml,xml.Replace("Name='Contact'","Name='NotContact'"),"网络");
        Changed(xml,xml.Replace("Datatype='Int'","Datatype='DInt'"),"接口");
        Changed(xml,xml.Replace("<StartValue>1","<StartValue>2"),"Z01");
        Changed(xml,xml.Replace("Name='Z02'","Name='Z03'"),"声明顺序");
        Changed(xml,xml.Replace("<Member Name='Z02' Datatype='Bool'/>",""),"Z02");
        Changed(xml,xml.Replace("urn:interface","urn:newinterface"),"XML命名空间");
        string namespaced=xml.Replace("<Member Name='Z01'", "<Member xmlns:x='urn:one' x:Custom='value' Name='Z01'");
        Changed(namespaced,namespaced.Replace("urn:one","urn:two"),"Z01");
        Changed(xml,xml.Replace("<Interface>","<Interface Custom='x'>"),"容器");
        Changed(xml,xml.Replace("<AttributeList><Name>","<AttributeList Custom='x'><Name>"),"属性容器");
        Changed(xml,xml.Replace("<ObjectList>","<ObjectList Custom='x'>"),"子对象容器");
        Changed(xml,xml.Replace("<Document>","<Document Custom='x'>"),"文档容器");
        Check(SemanticDiff.Compare(xml,xml.Replace("<Name>Host","<Name>Other")).status=="unsupported","identity mismatch");
        Check(SemanticDiff.Compare("x","x").status=="unsupported","non XML");
        Check(SemanticDiff.Compare("<!DOCTYPE x [<!ENTITY a SYSTEM 'file:///C:/secret'>]><Document/>",xml).status=="unsupported","DTD disabled");
        string deep = string.Concat(Enumerable.Repeat("<x>",102))+string.Concat(Enumerable.Repeat("</x>",102));
        Check(SemanticDiff.Compare(Xml(deep),xml).status=="unsupported","depth bound");
        Check(SemanticDiff.Compare(new string('x',8*1024*1024+1),xml).status=="unsupported","size bound");
        string duplicate = xml.Replace("UId='4' Name='Coil'","UId='2' Name='Coil'");
        Check(SemanticDiff.Compare(duplicate,duplicate).warnings.Any(w=>w.Contains("重复 UId")),"duplicate id fallback");
        string dangling = xml.Replace("IdentCon UId='1'","IdentCon UId='99'");
        Check(SemanticDiff.Compare(dangling,dangling).warnings.Any(w=>w.Contains("未解析")),"dangling wire fallback");
        string unknown = xml.Replace("<Part UId='4'", "<Unknown UId='4'");
        Check(SemanticDiff.Compare(unknown,unknown.Replace("UId='4'","UId='104'")).changes.Count>0,"unknown UID retained");
        string graph=xml.Replace("FlgNet","Graph");
        Check(SemanticDiff.Compare(graph,graph.Replace("UId='1'","UId='101'")).changes.Count>0,"GRAPH IDs retained");
        string text=Xml("<SW.Blocks.CompileUnit ID='1'><AttributeList><NetworkSource><StructuredText><Token UId='1' Text='a'/><Token UId='2'><Text> </Text></Token></StructuredText></NetworkSource></AttributeList></SW.Blocks.CompileUnit>");
        Changed(text,text.Replace("<Text> </Text>","<Text>  </Text>"),"网络");
        Check(SemanticDiff.Compare(text,text.Replace("UId='1'","UId='101'").Replace("UId='2'","UId='102'")).changes.Count==0,"SCL token IDs");
        string scl=Xml("<SW.Blocks.CompileUnit><AttributeList><NetworkSource><StructuredText><Access UId='1'><Symbol UId='2'><Component UId='3' Name='Value'/></Symbol></Access><Token UId='4' Text=':='/><Constant UId='5'><ConstantValue UId='6'>1</ConstantValue></Constant></StructuredText></NetworkSource></AttributeList></SW.Blocks.CompileUnit>");
        string sclNew=scl;for(int i=1;i<=6;i++)sclNew=sclNew.Replace("UId='"+i+"'","UId='"+(i+100)+"'");
        Check(SemanticDiff.Compare(scl,sclNew).changes.Count==0,"SCL nested access and constant IDs");
        Changed(scl,scl.Replace(">1</ConstantValue>",">2</ConstantValue>"),"网络");
        string tags="<Document><SW.Tags.PlcTagTable ID='1'><AttributeList><Name>Tags</Name></AttributeList><ObjectList><SW.Tags.PlcTag ID='2'><AttributeList><Name>Valve</Name><LogicalAddress>%Q1.0</LogicalAddress><DataTypeName>Bool</DataTypeName></AttributeList><ObjectList><MultilingualText ID='4' CompositionName='Comment'><ObjectList><MultilingualTextItem ID='5'><AttributeList><Culture>zh-CN</Culture><Text>阀</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText></ObjectList></SW.Tags.PlcTag></ObjectList></SW.Tags.PlcTagTable></Document>";
        Changed(tags,tags.Replace("%Q1.0","%Q1.1"),"LogicalAddress");
        Changed(tags,tags.Replace("<Text>阀","<Text>新阀"),"文本");
        Changed(tags,tags.Replace("<Name>Valve","<Name>NewValve"),"变量");
        Check(SemanticDiff.Compare(tags,tags.Replace("ID='2'","ID='20'").Replace("ID='5'","ID='50'")).changes.Count==0,"tag/text export IDs");
        string duptags=tags.Replace("</ObjectList></SW.Tags.PlcTagTable>",tags.Substring(tags.IndexOf("<SW.Tags.PlcTag ID="),tags.IndexOf("</SW.Tags.PlcTag>")+16-tags.IndexOf("<SW.Tags.PlcTag ID="))+"</ObjectList></SW.Tags.PlcTagTable>");
        Check(SemanticDiff.Compare(duptags,duptags).status=="unsupported","duplicate tag rejected");
        Check(DiffHelper.CompareFiles("missing","missing").error!=null,"missing not deleted");
        var raw=DiffHelper.CompareText("a\nb\nc", "a\nx\nc", "x");
        Check(raw.additions==1 && raw.deletions==1 && raw.lines.Last().text=="c","raw and suffix order");
        string largeA=string.Join("\n",Enumerable.Repeat("a",66000)),largeB=string.Join("\n",Enumerable.Repeat("b",66000));
        Check(DiffHelper.CompareText(largeA,largeB,"big").additions==66000,"LCS product overflow avoided");

        Check(DiffHelper.CompareText(new string('\n',100001),"x","large").error!=null,"raw line count bound before allocation");
        string root=Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
        string project=Path.Combine(root,"Test.ap17"); File.WriteAllText(project,"");
        string backupRoot=Path.Combine(root,"block-backups"); string backup=BackupRecord.Write(Path.Combine(backupRoot,"batch"),"Host",xml,project,"PLC","Group","Block","Host");
        string relative=backup.Substring(backupRoot.Length+1);
        var entry=IndexEntry.Create("plc.block","PLC","Group","Host"); entry.state="ready"; entry.content_hash=IndexJson.Hash(xml); entry.indexed_utc="2026-11-08T00:00:00Z";
        var catalog=new IndexCatalog { project_file="Test.ap17", portal_version="V17", objects=new List<IndexEntry>{entry} };
        var store=new ProjectIndexStore(project,"V17"); Directory.CreateDirectory(store.Root);
        Directory.CreateDirectory(Path.Combine(store.Root,"source")); Directory.CreateDirectory(Path.Combine(store.Root,"content"));
        string catalogFile=Path.Combine(store.Root,"catalog.json"), sourceFile=Path.Combine(store.Root,"source",entry.content_hash+".xml");
        File.WriteAllText(sourceFile,xml);File.WriteAllText(Path.Combine(store.Root,"content",entry.content_hash+".json"),"{}");
        Action save=()=>File.WriteAllText(catalogFile,IndexJson.Serializer().Serialize(catalog)); save();
        Check(SnapshotDiff.List(backupRoot,project,"PLC").Length==1,"nested backup listed");
        Check(SnapshotDiff.List(backupRoot,project,"Other").Length==0,"PLC list isolation");
        Check(SnapshotDiff.Compare(backupRoot,relative,project,"PLC","V17").semantic.changes.Count==0,"exact snapshot matched");
        var stamps=Directory.GetFiles(root,"*",SearchOption.AllDirectories).ToDictionary(f=>f,f=>File.GetLastWriteTimeUtc(f));
        SnapshotDiff.Compare(backupRoot,relative,project,"PLC","V17");
        Check(stamps.All(p=>File.GetLastWriteTimeUtc(p.Key)==p.Value),"comparison remains read only");
        Reject(()=>SnapshotDiff.Compare(backupRoot,"../escape.xml",project,"PLC","V17"),"traversal");
        Reject(()=>SnapshotDiff.Confined(backupRoot,backup),"absolute path");
        Reject(()=>SnapshotDiff.Confined(backupRoot,"file.xml:stream"),"alternate stream");
        Reject(()=>SnapshotDiff.Compare(backupRoot,relative,project,"Other","V17"),"PLC target mismatch");
        Reject(()=>SnapshotDiff.Compare(backupRoot,relative,Path.Combine(root,"Other.ap17"),"PLC","V17"),"project mismatch");
        File.WriteAllText(sourceFile,xml+" ");Reject(()=>SnapshotDiff.Compare(backupRoot,relative,project,"PLC","V17"),"source hash mismatch");File.WriteAllText(sourceFile,xml);
        File.AppendAllText(backup," ");Reject(()=>SnapshotDiff.Compare(backupRoot,relative,project,"PLC","V17"),"backup hash mismatch");File.WriteAllText(backup,xml);
        entry.group="Elsewhere";save();Reject(()=>SnapshotDiff.Compare(backupRoot,relative,project,"PLC","V17"),"group exact match");entry.group="Group";
        entry.unit_name="Unit";save();Reject(()=>SnapshotDiff.Compare(backupRoot,relative,project,"PLC","V17"),"unit identity absent in backup");entry.unit_name="";
        entry.state="stale";save();Reject(()=>SnapshotDiff.Compare(backupRoot,relative,project,"PLC","V17"),"stale snapshot");entry.state="ready";save();
        File.WriteAllText(Path.Combine(store.Root,"dirty.json"),IndexJson.Serializer().Serialize(new IndexDirty { all=true }));
        Reject(()=>SnapshotDiff.Compare(backupRoot,relative,project,"PLC","V17"),"pending invalidation");File.Delete(Path.Combine(store.Root,"dirty.json"));
        catalog.objects.Add(entry);save();Reject(()=>SnapshotDiff.Compare(backupRoot,relative,project,"PLC","V17"),"ambiguous match");
        catalog.objects.RemoveAt(1);save();
        HttpChecks(root,project,relative);
        Console.WriteLine("PASS: "+count+" semantic diff and snapshot identity checks");
    }
}
