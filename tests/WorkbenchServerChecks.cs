using System;
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

class WorkbenchServerChecks
{
    static int checks,calls;static BindingFlags flags=BindingFlags.NonPublic|BindingFlags.Instance;
    static void Check(bool b,string m){if(!b)throw new Exception(m);checks++;}
    static Dictionary<string,object> D(params object[] x){var d=new Dictionary<string,object>();for(int i=0;i<x.Length;i+=2)d.Add((string)x[i],x[i+1]);return d;}
    static object Field(object o,string name){return o.GetType().GetField(name,flags).GetValue(o);}
    static Dictionary<string,object> Request(WebServer server,HttpListener listener,string url,string action,object data,int expected=200,string origin=null,string method="POST")
    {
        Exception error=null;var worker=new Thread(()=>{try{typeof(WebServer).GetMethod("ProcessRequest",flags).Invoke(server,new[]{(object)listener.GetContext()});}catch(Exception ex){error=ex;}});worker.Start();
        var request=(HttpWebRequest)WebRequest.Create(url+action);request.Method=method;request.KeepAlive=false;request.Timeout=10000;if(origin!=null)request.Headers["Origin"]=origin;
        if(data!=null){byte[] bytes=Encoding.UTF8.GetBytes(CylinderWritePipeline.Json().Serialize(data));request.ContentType="application/json";request.ContentLength=bytes.Length;using(var stream=request.GetRequestStream())stream.Write(bytes,0,bytes.Length);}
        HttpWebResponse response;try{response=(HttpWebResponse)request.GetResponse();}catch(WebException ex){response=(HttpWebResponse)ex.Response;if(response==null)throw;}
        string text;using(response)using(var reader=new StreamReader(response.GetResponseStream())){text=reader.ReadToEnd();Check((int)response.StatusCode==expected,action+" HTTP "+(int)response.StatusCode+": "+text);Check(string.IsNullOrEmpty(response.Headers["Access-Control-Allow-Origin"]),"same origin only");}
        Check(worker.Join(5000)&&error==null,"request worker completes");return CylinderWritePipeline.Json().Deserialize<Dictionary<string,object>>(text);
    }
    static int Main(string[] args){try{Run(Path.GetFullPath(args[0]));Console.WriteLine("PASS: "+checks+" fixed-route workbench HTTP, preview binding, replay and record checks; no TIA connection.");return 0;}catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
    static void Run(string root)
    {
        Directory.CreateDirectory(root);string project=Path.Combine(root,"A.ap17"),other=Path.Combine(root,"B.ap17"),config=Path.Combine(root,"config.json");File.WriteAllText(project,"");File.WriteAllText(other,"");File.WriteAllText(config,CylinderWritePipeline.Json().Serialize(D("projectPath",project,"sourceDirectory",root,"tiaPortalVersion","V17")));
        var probe=new TcpListener(IPAddress.Loopback,0);probe.Start();int port=((IPEndPoint)probe.LocalEndpoint).Port;probe.Stop();var server=new WebServer(config,port);var listener=(HttpListener)Field(server,"_listener");listener.Start();string url="http://127.0.0.1:"+port;
        var targets=(ActionTarget)Field(server,"actionTargets");var target=D("project_path",project,"plc_name","PLC","target_token",targets.Sign(project,"PLC"));Dictionary<string,object> received=null;string lastOperation=null;
        WebServer.WorkbenchTestCall=(op,a)=>{Interlocked.Increment(ref calls);lastOperation=op;received=new Dictionary<string,object>(a);object result;
            if(op=="block-read")result=D("ok",true,"source_kind","index_cache","freshness","known_stale");
            else if(op=="compare")result=D("total_pairs",0,"items",new object[0]);
            else if(op=="logic-preview"||op=="logic-status")result=D("plan_id",Guid.NewGuid().ToString("N"),"contact_before",D("negated",false),"contact_after",D("negated",true));
            else if(op=="logic-apply")result=D("state","verified_unsaved","ok",true,"saved",false,"downloaded",false);
            else if(op=="download-preflight")result=D("id",Guid.NewGuid().ToString("N"),"preflight_ready",a.ContainsKey("enable_simulation_support")&&Equals(a["enable_simulation_support"],true),"dry_run",true);
            else if(op=="download-apply")result=D("state","outcome_unknown","downloaded",false,"automatic_retry",false);
            else throw new Exception("unexpected tool route");return D("structuredContent",result);};
        try{
            Func<string,Dictionary<string,object>,int,Dictionary<string,object>> req=(action,payload,status)=>Request(server,listener,url,"/api/io/workbench/"+action,payload,status);
            foreach(string action in new[]{"block-read","compare","logic-preview","logic-apply","download-preflight","download-apply","job","download-status"}){
                Request(server,listener,url,"/api/io/workbench/"+action,target,403,"http://evil.invalid");var wrong=new Dictionary<string,object>(target);wrong["target_token"]="wrong";req(action,wrong,409);
            }Check(calls==0,"rejected requests never reach tool proxy");
            Request(server,listener,url,"/api/io/workbench/block-read",null,405,null,"GET");req("tia_delete_block",target,404);
            var body=new Dictionary<string,object>(target){{"block_name","A"},{"read_mode","cache"},{"format","readable"},{"network_offset",5},{"network_limit",5},{"tool","tia_delete_block"},{"confirm",true}};
            var r=req("block-read",body,200);Check(lastOperation=="block-read"&&!received.ContainsKey("confirm")&&!received.ContainsKey("tool")&&(int)received["network_offset"]==5,"fixed read route strips unrelated arguments");Check((string)r["source_kind"]=="index_cache","cache evidence retained");
            body=new Dictionary<string,object>(target){{"project_path_a",project},{"plc_name_a","PLC"},{"project_path_b",other},{"plc_name_b","PLC2"},{"offset",10},{"limit",10},{"catalog_token","hash"}};
            req("compare",body,200);Check(lastOperation=="compare"&&(string)received["catalog_token"]=="hash"&&!received.ContainsKey("target_token"),"two-project compare explicitly scoped");
            body=new Dictionary<string,object>(target){{"block_name","A"},{"group_path","G"},{"edit_json","{}"}};r=req("logic-preview",body,200);string id=(string)r["id"],fingerprint=(string)r["fingerprint"];
            var apply=new Dictionary<string,object>(target){{"id",id},{"fingerprint",fingerprint}};req("logic-apply",apply,409);apply["confirm"]=true;apply["fingerprint"]="tampered";req("logic-apply",apply,409);apply["fingerprint"]=fingerprint;
            req("logic-apply",apply,202);var query=new Dictionary<string,object>(target){{"id",id}};
            for(int i=0;i<100;i++){r=req("job",query,200);if((string)r["state"]!="accepted")break;Thread.Sleep(10);}
            Check((string)r["state"]=="verified_unsaved"&&lastOperation=="logic-apply"&&(string)received["plan_id"]==id&&Equals(received["confirm"],true),"reviewed plan applied once without arbitrary XML");req("logic-apply",apply,409);
            var mismatch=new Dictionary<string,object>(query);mismatch["plc_name"]="Other";mismatch["target_token"]=targets.Sign(project,"Other");req("job",mismatch,409);
            req("job",new Dictionary<string,object>(target){{"id","../outside"}},409);
            string plans=Path.Combine(root,"logs","logic-plans");Directory.CreateDirectory(plans);File.WriteAllText(Path.Combine(plans,id+".json"),CylinderWritePipeline.Json().Serialize(D("id",id,"project_path",project,"plc_name","PLC","block_name","A")));
            var tasks=Request(server,listener,url,"/api/io/tasks?project_path="+Uri.EscapeDataString(project)+"&plc_name=PLC&target_token="+Uri.EscapeDataString((string)target["target_token"]),null,200,null,"GET");
            var catalog=CylinderWritePipeline.Rows(tasks,"jobs").Where(row=>CylinderWritePipeline.Text(row,"id")==id).ToArray();Check(catalog.Length==1&&CylinderWritePipeline.Text(catalog[0],"state")=="verified_unsaved"&&CylinderWritePipeline.Text(catalog[0],"title").Contains("A"),"task catalog prefers execution receipt and deduplicates native plan");
            var orphanId=Guid.NewGuid().ToString("N");File.WriteAllText(Path.Combine(root,"logs","workbench",orphanId+".json"),CylinderWritePipeline.Json().Serialize(D("id",orphanId,"project_path",project,"plc_name","PLC","state","accepted")));r=req("job",new Dictionary<string,object>(target){{"id",orphanId}},200);Check((string)r["state"]=="outcome_unknown","orphaned accepted job never asserted successful");
            body=new Dictionary<string,object>(target){{"instance_name","BridgeSim_Test"},{"dry_run",false},{"confirm",true}};r=req("download-preflight",body,200);Check(Equals(received["dry_run"],true)&&!received.ContainsKey("confirm"),"preflight cannot be promoted to write");
            apply=new Dictionary<string,object>(target){{"id",r["id"]},{"fingerprint",r["fingerprint"]},{"confirm",true}};req("download-apply",apply,409);
            string scenario="{\"rules\":[{\"command\":\"C\",\"feedback\":\"F\"}]}";var per=new Dictionary<string,object>(target){{"instance_name","BridgeSim_Test"},{"configuration_json",scenario}};
            r=Request(server,listener,url,"/api/io/peripheral/preview",per);Check(r.ContainsKey("preview_token")&&lastOperation=="download-preflight","scenario preview never reaches simulation transport");per["confirm"]=true;per["preview_token"]=r["preview_token"];per["configuration_json"]=scenario.Replace("\"C\"","\"Changed\"");Request(server,listener,url,"/api/io/peripheral/start",per,409);
            body["enable_simulation_support"]=true;r=req("download-preflight",body,200);apply=new Dictionary<string,object>(target){{"id",r["id"]},{"fingerprint",r["fingerprint"]},{"confirm",true},{"instance_name","InjectedOther"}};
            req("download-apply",apply,202);query=new Dictionary<string,object>(target){{"id",r["id"]}};for(int i=0;i<100;i++){r=req("job",query,200);if((string)r["state"]!="accepted")break;Thread.Sleep(10);}
            Check((string)r["state"]=="outcome_unknown"&&Equals(received["dry_run"],false)&&(string)received["instance_name"]=="BridgeSim_Test","download uses bound preflight target and preserves unknown outcome");req("download-apply",apply,409);
            Check(File.ReadAllText(project)==""&&File.ReadAllText(other)=="","fixture projects untouched");
        }finally{WebServer.WorkbenchTestCall=null;listener.Stop();listener.Close();}
    }
}
