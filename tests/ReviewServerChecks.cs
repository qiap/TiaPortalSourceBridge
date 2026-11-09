using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using TiaPortalSourceBridge.Web;
using TiaPortalSourceBridge.Mcp;
using TiaPortalSourceBridge.Indexing;

internal static class ReviewServerChecks
{
    static int count;static BindingFlags flags=BindingFlags.NonPublic|BindingFlags.Instance;
    static void Check(bool b,string m){if(!b)throw new Exception(m);count++;}
    static object Field(object o,string n){return o.GetType().GetField(n,flags).GetValue(o);}
    static Dictionary<string,object> D(params object[] args){var d=new Dictionary<string,object>();for(int i=0;i<args.Length;i+=2)d.Add((string)args[i],args[i+1]);return d;}
    static string Text(Dictionary<string,object> d,string key){return CylinderWritePipeline.Text(d,key);}
    static Dictionary<string,object> Request(WebServer server,HttpListener listener,string url,string route,string method,object body,int code,string origin=null,string site=null)
    {
        Exception error=null;var thread=new Thread(()=>{try{typeof(WebServer).GetMethod("ProcessRequest",flags).Invoke(server,new[]{(object)listener.GetContext()});}catch(Exception ex){error=ex;}});thread.Start();
        var request=(HttpWebRequest)WebRequest.Create(url+route);request.Method=method;request.KeepAlive=false;request.Timeout=10000;
        if(origin!=null)request.Headers["Origin"]=origin;if(site!=null)request.Headers["Sec-Fetch-Site"]=site;
        if(body!=null){byte[] bytes=Encoding.UTF8.GetBytes(CylinderWritePipeline.Json().Serialize(body));request.ContentType="application/json";request.ContentLength=bytes.Length;using(var stream=request.GetRequestStream())stream.Write(bytes,0,bytes.Length);}
        HttpWebResponse response;try{response=(HttpWebResponse)request.GetResponse();}catch(WebException ex){response=(HttpWebResponse)ex.Response;if(response==null)throw;}
        string text;using(response)using(var reader=new StreamReader(response.GetResponseStream())){text=reader.ReadToEnd();Check((int)response.StatusCode==code,"expected "+code+" "+route+": "+text);Check(string.IsNullOrEmpty(response.Headers["Access-Control-Allow-Origin"]),"no wildcard CORS");}
        Check(thread.Join(5000)&&error==null,"handler completes");return text.StartsWith("{")?CylinderWritePipeline.Json().Deserialize<Dictionary<string,object>>(text):D("body",text);
    }
    static void Main(string[] args){try{Run(args);}catch(Exception ex){Console.Error.WriteLine(ex.GetType().Name+": "+ex.Message);Environment.ExitCode=1;}}
    static void Run(string[] args)
    {
        string root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);string project=Path.Combine(root,"Fixture.ap17"),config=Path.Combine(root,"config.json");File.WriteAllText(project,"fixture");File.WriteAllText(config,CylinderWritePipeline.Json().Serialize(D("projectPath",project,"sourceDirectory",root,"tiaPortalVersion","V17")));
        string xml="<Document><SW.Tags.PlcTagTable><AttributeList><Name>Tags</Name></AttributeList><ObjectList><SW.Tags.PlcTag><AttributeList><Name>Existing</Name><DataTypeName>Bool</DataTypeName><LogicalAddress>%I0.0</LogicalAddress></AttributeList></SW.Tags.PlcTag></ObjectList></SW.Tags.PlcTagTable></Document>";
        var entry=IndexEntry.Create("plc.tags","PLC","","Tags","");entry.content_hash=IndexJson.Hash(xml);entry.state="ready";var store=new ProjectIndexStore(project,"V17");Directory.CreateDirectory(Path.Combine(store.Root,"source"));Directory.CreateDirectory(Path.Combine(store.Root,"content"));
        File.WriteAllText(Path.Combine(store.Root,"source",entry.content_hash+".xml"),xml);File.WriteAllText(Path.Combine(store.Root,"content",entry.content_hash+".json"),"{}");File.WriteAllText(Path.Combine(store.Root,"catalog.json"),IndexJson.Serializer().Serialize(new IndexCatalog{project_file="Fixture.ap17",portal_version="V17",objects=new List<IndexEntry>{entry}}));
        var probe=new TcpListener(IPAddress.Loopback,0);probe.Start();int port=((IPEndPoint)probe.LocalEndpoint).Port;probe.Stop();var server=new WebServer(config,port);var listener=(HttpListener)Field(server,"_listener");listener.Start();string url="http://127.0.0.1:"+port;
        var targets=(ActionTarget)Field(server,"actionTargets");var target=D("project_path",project,"plc_name","PLC","target_token",targets.Sign(project,"PLC"));
        var report=new IoReconcileReport{id="report",snapshot_id="snapshot",project_path=project,plc_name="PLC",options=new IoReconcileOptions{scope="native",table="\nTags",type=2,comment=3},nativeTableSignatures=new Dictionary<string,string>{{"\n\nTags",TagTableXml.Signature(xml)}},rows=new List<IoReconcileRow>{new IoReconcileRow{excel_row=2,status="excel_only",name="New",type="Bool",address="I0.1",comment="new point"}}};
        ((IDictionary)Field(server,"reconcileReports")).Add(report.id,report);
        var job=Activator.CreateInstance(typeof(WebServer).GetNestedType("IoJob",BindingFlags.NonPublic),true);foreach(var pair in D("id","snapshot","project",project,"plc","PLC","state","completed","result",new IoSnapshot{project_path=project,plc_name="PLC"}))job.GetType().GetField(pair.Key,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).SetValue(job,pair.Value);((IDictionary)Field(server,"ioJobs")).Add("snapshot",job);
        var lanes=(Dictionary<string,ActionProgress>)typeof(ActionRunner).GetField("_lanes",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);lanes["tia"]=new ActionProgress{status="running",actionName="offline-test-sentinel"};
        try{
            var body=new Dictionary<string,object>(target){{"report_id","report"},{"rows",new[]{2}},{"culture","zh-CN"}};
            Request(server,listener,url,"/api/io/tag-review/preview","POST",body,403,"http://evil.invalid");Request(server,listener,url,"/api/io/tag-review/preview","POST",body,403,null,"cross-site");
            var invalid=new Dictionary<string,object>(body);invalid["target_token"]="wrong";Request(server,listener,url,"/api/io/tag-review/preview","POST",invalid,409);
            var preview=Request(server,listener,url,"/api/io/tag-review/preview","POST",body,200);Check(Text(preview,"table_name")=="Tags"&&Text(preview,"fingerprint").Length==64,"preview bound to plan fingerprint");
            var apply=new Dictionary<string,object>(target){{"id",Text(preview,"id")},{"fingerprint",Text(preview,"fingerprint")},{"confirm",false}};
            Request(server,listener,url,"/api/io/tag-review/apply","POST",apply,409);apply["confirm"]=true;apply["fingerprint"]="changed";Request(server,listener,url,"/api/io/tag-review/apply","POST",apply,409);
            apply["fingerprint"]=preview["fingerprint"];Request(server,listener,url,"/api/io/tag-review/apply","POST",apply,409);Check(!File.Exists(Path.Combine(root,"logs","tag-table-jobs",preview["id"]+".submitted.json")),"busy lane rejects before submission");
            string query="?project_path="+Uri.EscapeDataString(project)+"&plc_name=PLC&target_token="+Uri.EscapeDataString(Text(target,"target_token"))+"&id="+preview["id"];
            var status=Request(server,listener,url,"/api/io/tag-review/status"+query,"GET",null,200);Check(Text(status,"state")=="preview","preview survives disk round trip");
            Request(server,listener,url,"/api/io/tag-review/restore-preview","POST",apply,409);
            var capture=new Dictionary<string,object>(target){{"label","Baseline"}};var saved=Request(server,listener,url,"/api/io/review/capture","POST",capture,200);
            var compare=new Dictionary<string,object>(target){{"before_id",saved["id"]},{"after_id",saved["id"]}};var diff=Request(server,listener,url,"/api/io/review/compare","POST",compare,200);Check(((ArrayList)diff["objects"]).Count==0,"identical independently persisted snapshots compare empty");
            compare["before_id"]="../bad";Request(server,listener,url,"/api/io/review/compare","POST",compare,409);
            foreach(string route in new[]{"monitor/connect","source-write/apply","peripheral/start","hmi-companion/preview"}){
                Request(server,listener,url,"/api/io/"+route,"POST",invalid,409);
                Request(server,listener,url,"/api/io/"+route,"POST",body,403,"http://evil.invalid");
            }
            var sourcePlan=new ReviewedSourcePlan{id=Guid.NewGuid().ToString("N"),project_path=project,plc_name="PLC",purpose="fixture",note="fixture",patches=new List<TextPatch>(),unmatched=new List<string>()};
            string sourceDir=Path.Combine(root,"logs","source-write-jobs");Directory.CreateDirectory(sourceDir);File.WriteAllText(Path.Combine(sourceDir,sourcePlan.id+".plan.json"),CylinderWritePipeline.Json().Serialize(sourcePlan));
            var sourceRequest=new Dictionary<string,object>(target){{"id",sourcePlan.id},{"confirm",false},{"fingerprint",XmlBatch.Hash(CylinderWritePipeline.Json().Serialize(sourcePlan))}};
            Request(server,listener,url,"/api/io/source-write/apply","POST",sourceRequest,409);Request(server,listener,url,"/api/io/source-write/restore-preview","POST",sourceRequest,409);
            sourceRequest["confirm"]=true;Request(server,listener,url,"/api/io/source-write/apply","POST",sourceRequest,409);Check(!File.Exists(Path.Combine(sourceDir,sourcePlan.id+".submitted")),"unsupported source plan not submitted");
            var graphRequest=new Dictionary<string,object>(target){{"id",saved["id"]}};Request(server,listener,url,"/api/io/review/graph-list","POST",graphRequest,200);
            graphRequest["object_id"]="missing";Request(server,listener,url,"/api/io/review/graph","POST",graphRequest,409);
            string v21=Path.Combine(root,"Fixture.ap21");File.WriteAllText(v21,"");var unsupported=new Dictionary<string,object>(body);unsupported["project_path"]=v21;unsupported["target_token"]=targets.Sign(v21,"PLC");Request(server,listener,url,"/api/io/tag-review/preview","POST",unsupported,409);
            Check(File.ReadAllText(project)=="fixture"&&!Directory.Exists(Path.Combine(root,"block-backups")),"HTTP tests perform no engineering writes or backups");
            Check(lanes["tia"].actionName=="offline-test-sentinel","existing engineering task untouched");
        }finally{lanes.Remove("tia");listener.Close();}
        Console.WriteLine("PASS: "+count+" source and tag review HTTP checks; no TIA connection");
    }
}
