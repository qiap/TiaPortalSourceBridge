using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;
using TiaPortalSourceBridge.Mcp;
class ToolTextSummaryChecks
{
 static int count;
 static void Check(bool b,string message){if(!b)throw new Exception(message);count++;}
 static Dictionary<string,object> D(params object[] v){var d=new Dictionary<string,object>();for(int i=0;i<v.Length;i+=2)d.Add((string)v[i],v[i+1]);return d;}
 static string Text(Dictionary<string,object> r){return string.Join("\n",((object[])r["content"]).Cast<Dictionary<string,object>>().Where(x=>(string)x["type"]=="text").Select(x=>(string)x["text"]));}
 static Dictionary<string,object> Run(object data,bool failed=false){var r=D("content",new object[]{D("type","text","text","结果。")},"isError",failed,"structuredContent",data);ToolTextSummary.Apply("tia_test",D("project_path","P.ap17"),r);return r;}
 public static void Main(){
 var data=new {connections=new[]{new{project_path="A.ap17",state="alive",process_id=12},new{project_path="B.ap17",state="disconnected",process_id=0}}};
 var r=Run(data);string t=Text(r);Check(t.Contains("A.ap17")&&t.Contains("B.ap17"),"both projects visible");Check(((Dictionary<string,object>)((object[])r["content"])[0])["text"].ToString().Contains("B.ap17"),"first text block includes summary");Check(t.Contains("alive")&&t.Contains("disconnected"),"states visible");Check(t.Contains("12"),"pid visible");Check(ReferenceEquals(data,r["structuredContent"]),"structured DTO preserved");
 r=Run(new{instance=new{instance_name="BridgeSim_1",state="Stop",ip_addresses=new[]{"192.168.0.1"},license_status="OK"}});t=Text(r);foreach(string s in new[]{"BridgeSim_1","Stop","192.168.0.1","OK"})Check(t.Contains(s),"simulation "+s);
 r=Run(new{references=new[]{new{source=new{name="FC_A",group="Station"},reference=new{location="NW2",relation="writes",target="Q0.0"}}},total_matches=1,next_offset=(int?)null});t=Text(r);foreach(string s in new[]{"FC_A","NW2","writes","Q0.0","next_offset"})Check(t.Contains(s),"reference evidence "+s);
 r=Run(new{items=Enumerable.Range(0,31).Select(i=>new{name="B"+i}).ToArray()});t=Text(r);Check(t.Contains("31 项")&&t.Contains("省略 1 项"),"list truncation explicit");Check((bool)((Dictionary<string,object>)r["text_summary"])["truncated"],"truncation metadata");
 string source="IF #A THEN\n  #B := TRUE;\nEND_IF;";r=Run(new{source=source,extension=".scl"});Check(Text(r).Contains(source),"small compact source accessible");
 r=Run(new{xml=new string('x',50000)});Check(Text(r).Length<ToolTextSummary.Budget+1000,"bounded text");Check(Text(r).Contains("摘要截断"),"source omission marked");
 var json=new JavaScriptSerializer();string original=json.Serialize(r);ToolTextSummary.Apply("tia_test",D(),r);Check(original==json.Serialize(r),"host worker idempotence");
 r=Run(new{saved=false,items=new[]{new{status="imported",verification="different",backup="backup.xml"}},error_count=0},true);t=Text(r);Check(t.Contains("different")&&t.Contains("backup.xml")&&t.Contains("False"),"partial failure evidence retained");Check(r.ContainsKey("diagnostic"),"structured failure diagnostic");
 foreach(var e in new Exception[]{new TimeoutException("timeout"),new ArgumentException("bad"),new InvalidOperationException("InterfaceRemoved")}){
 var d=ToolTextSummary.Failure("tia_import_block",D("project_path","P.ap17"),e,e.Message);Check(!(bool)d["automatic_retry"],"no implicit retry");Check((string)d["project_path"]=="P.ap17","error target");if(e is InvalidOperationException)Check((string)d["code"]=="OPERATION_FAILED","no guessed cause from message");}
 r=D("content",new object[]{D("type","text","text","failure")},"isError",true,"diagnostic",ToolTextSummary.Failure("x",D(),new TimeoutException(),"timeout"));ToolTextSummary.Apply("x",D(),r);Check(Text(r).Contains("OPERATION_TIMEOUT"),"diagnostic in text without structured data");
 var cycle=D("saved",true);cycle["self"]=cycle;r=Run(cycle);Check(ReferenceEquals(cycle,r["structuredContent"]),"format failure preserves mutation receipt");Check(Text(r).Contains("SUMMARY_UNAVAILABLE")||Text(r).Contains("摘要生成失败"),"format failure explicit");
 Check(!(bool)r["isError"],"format failure does not change engineering outcome");
 string largeSource=string.Join("\n",Enumerable.Range(0,300).Select(i=>"#Result"+i+" := #Input"+i+"; // source evidence"));
 largeSource=largeSource.Substring(0,9000)+"\nEND_FUNCTION";
 r=Run(new{source=largeSource,extension=".scl"});Check(Text(r).Contains(largeSource),"compact 9KB SCL retained in full");Check(!(bool)((Dictionary<string,object>)r["text_summary"])["truncated"],"9KB source not marked truncated");
 foreach(string key in new[]{"source","xml"}) {
   string payload=key=="xml"?"<Document>"+new string('x',9000)+"</Document>":largeSource;
   var structured=D(key,payload);r=D("content",new object[]{D("type","text","text",payload)},"structuredContent",structured);
   ToolTextSummary.Apply("tia_read_block",D(),r);t=Text(r);
   Check(t.IndexOf(payload,StringComparison.Ordinal)==t.LastIndexOf(payload,StringComparison.Ordinal),"full payload appears exactly once: "+key);
   Check(!t.Contains("摘要截断"),"full payload not falsely truncated: "+key);
   Check(ReferenceEquals(structured,r["structuredContent"]),"full structured payload unchanged: "+key);
 }
 r=Run(new{items=Enumerable.Range(0,30).Select(i=>new{name="FC"+i,number=i,group="Station",language="LAD",state="ready"}).ToArray()});t=Text(r);
 Check(t.Contains("name=FC29")&&t.Contains("state=ready"),"all compact list rows retained");Check(t.Split('\n').Length<45,"list scalars use one line per item");
 r=Run(D("padding",Enumerable.Range(0,30).Select(i=>new{name="START"+i+new string('x',1000)+"END"+i}).ToArray(),"source",largeSource));t=Text(r);
 Check(t.Length<ToolTextSummary.Budget+100,"global summary budget");Check(t.Contains("摘要截断"),"global omission explicit");
 Check(t.Split('\n').Where(x=>x.Contains("name=START")).All(x=>x.TrimEnd('\r').EndsWith("END"+x.Substring(x.IndexOf("START")+5).Split('x')[0])),"global budget never slices list row");
 r=Run(new{source=string.Join("\n",Enumerable.Range(0,1500).Select(i=>"#V"+i+" := TRUE;"))});Check(Text(r).Contains("摘要截断"),"large SCL omission explicit");Check(Text(r).Contains("#V700"),"SCL limit larger than XML limit");
 Console.WriteLine("PASS: "+count+" text-only MCP result checks");
 }
}
