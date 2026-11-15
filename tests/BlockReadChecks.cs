using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using TiaPortalSourceBridge.Mcp;
using TiaPortalSourceBridge.Indexing;
using TiaPortalSourceBridge.Configuration;
using TiaPortalSourceBridge.Web;
using TiaPortalSourceBridge.Openness;

class BlockReadChecks
{
    static int count;static void Check(bool ok,string message){if(!ok)throw new Exception(message);count++;}
    static Dictionary<string,object> D(params object[] v){var d=new Dictionary<string,object>();for(int i=0;i<v.Length;i+=2)d.Add((string)v[i],v[i+1]);return d;}
    static string J(object o){return IndexJson.Serializer().Serialize(o);}
    static object Invoke(object target,string method,params object[] args){try{return target.GetType().GetMethod(method,BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static).Invoke(target,args);}catch(TargetInvocationException e){throw e.InnerException;}}
    static void Reject(Action a,string message){bool rejected=false;try{a();}catch{rejected=true;}Check(rejected,message);}
    static string Wrap(string name,string logic){return "<Document><Engineering version='V17'/><SW.Blocks.FB ID='0'><AttributeList><Name>"+name+"</Name><ProgrammingLanguage>LAD</ProgrammingLanguage></AttributeList><ObjectList><SW.Blocks.CompileUnit ID='10'><AttributeList><NetworkSource>"+logic+"</NetworkSource></AttributeList><ObjectList><MultilingualText CompositionName='Title'><ObjectList><MultilingualTextItem><AttributeList><Text>急停互锁</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText></ObjectList></SW.Blocks.CompileUnit></ObjectList></SW.Blocks.FB></Document>";}
    static string Flg(string parts,string wires){return "<FlgNet xmlns='urn:test'><Parts>"+parts+"</Parts><Wires>"+wires+"</Wires></FlgNet>";}
    public static int Main(){try{Run();return 0;}catch(Exception ex){Console.Error.WriteLine("CHECKS BEFORE FAILURE: "+count);Console.Error.WriteLine(ex.GetType().Name+": "+ex.Message);Console.Error.WriteLine(ex.StackTrace);return 1;}}
    static void Run(){
        string xml=LadBuilder.Generate(D("block_name","Ladder","block_type","FB","block_number",1,"networks_json","[{\"title\":\"互锁\",\"coil\":\"Motor\",\"coil_type\":\"set\",\"branches\":[[{\"symbol\":\"Permit\",\"negated\":true},{\"symbol\":\"Start\"}],[{\"symbol\":\"Manual\"}]]}]"),"V17");
        var view=BlockReadable.Read(xml,0,5);string text=(string)view["readable_text"];
        foreach(string s in new[]{"互锁","常闭","常开","置位 S","Permit","Manual","NameCon","IdentCon","Powerrail","CompileUnit#"})Check(text.Contains(s),"LAD evidence "+s);
        Check(text.Contains("条件树")&&text.Contains(" AND ")&&text.Contains(" OR ")&&text.Contains("NOT"),"series/parallel/negation condition tree");
        var extra=BlockReadable.Read(xml.Replace("Name=\"Contact\"","Name=\"Contact\" FutureOption=\"true\""),0,5);
        Check(!((string)extra["readable_text"]).Contains("条件树")&&((string[])extra["warnings"]).Any(w=>w.Contains("额外属性")),"unknown attributes never silently enter a Boolean tree");
        extra=BlockReadable.Read(xml.Replace("FlgNet/v4","FlgNet/v999"),0,5);Check(!((string)extra["readable_text"]).Contains("条件树"),"unknown FlgNet versions remain structural");
        Check(!(bool)view["semantic_equivalence_proven"],"no unsafe equivalence claim");Check((string)view["source_hash"]==IndexJson.Hash(xml),"raw source hash");
        var paged=BlockReadable.Read(xml,1,1);Check(((object[])paged["networks"]).Length==0&&!(bool)paged["page_complete"],"empty page not entire program");
        Reject(()=>BlockReadable.Read(xml,0,0),"invalid page rejected");Reject(()=>BlockReadable.Read("<!DOCTYPE x [<!ENTITY a SYSTEM 'file:///C:/private'>]><x>&a;</x>",0,5),"DTD rejected");
        string unusual=Wrap("A",Flg("<Part Name='TON' UId='1'/>","<Wire UId='2'><NameCon UId='999' Name='in'/></Wire>"));view=BlockReadable.Read(unusual,0,5);text=J(view);
        Check(text.Contains("OPAQUE_NODE")&&text.Contains("raw_xml")&&text.Contains("999"),"unknown instruction and dangling reference retained");Check((string)view["coverage"]=="partial","unknown coverage explicit");
        view=BlockReadable.Read(Wrap("A",Flg("<Part Name='Contact' UId='1'/><Part Name='Coil' UId='1'/>","")),0,5);Check(((string[])view["warnings"]).Count(w=>w.Contains("OPAQUE_NODE"))==2,"duplicate part ids never resolved");
        view=BlockReadable.Read(Wrap("A",Flg("<Part Name='PContact' UId='1'/><Part Name='NContact' UId='2'/><Part Name='RCoil' UId='3'/><Call UId='4'><CallInfo Name='Cylinder' BlockType='FB'><Instance Scope='LocalVariable'><Component Name='Z01'/></Instance><Parameter Name='Enable' Section='Input'/></CallInfo></Call>","<Wire UId='5'><OpenCon UId='6'/><NameCon UId='4' Name='Enable'/></Wire>")),0,5);text=J(view);
        foreach(string s in new[]{"上升沿","下降沿","复位 R","Enable","Input","Z01","OpenCon"})Check(text.Contains(s),"special parts "+s);
        string graph=Wrap("Graph", "<Graph><Sequences><Sequence Name='Main'><Steps><Step Number='1' Name='Idle' Init='true'><Actions><Action Qualifier='N' Operand='Run'/></Actions></Step></Steps><Transitions><Transition Number='2' Name='Go'>"+Flg("<Part Name='Contact' UId='1'/>","")+"</Transition></Transitions><Connections><Connection><NodeFrom><StepRef Number='1'/></NodeFrom><NodeTo><TransitionRef Number='2'/></NodeTo></Connection></Connections></Sequence></Sequences></Graph>");
        view=BlockReadable.Read(graph,0,5);text=J(view);foreach(string s in new[]{"Idle","Init","TransitionRef","StepRef","Qualifier","OPAQUE_NODE","NodeFrom","NodeTo"})Check(text.Contains(s),"GRAPH evidence "+s);
        string temp=Path.Combine(Path.GetTempPath(),"block-read-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try {
            string project=Path.Combine(temp,"test.ap17");var store=new ProjectIndexStore(project,"V17");
            store.BuildCatalog(w=>new List<IndexEntry>{IndexEntry.Create("plc.block","PLC","G","Ladder","LAD"),IndexEntry.Create("plc.block","PLC","G","Text","SCL"),IndexEntry.Create("plc.block","PLC","G","Ghost"),IndexEntry.Create("plc.block","PLC","H","Ladder"),IndexEntry.Create("plc.block","PLC","G","Ladder","LAD","","UnitA")});
            store.Hydrate(store.Load().objects.Where(e=>e.group=="G"&&e.unit_name==""&&e.name!="Ghost"),e=>e.name=="Ladder"?xml:ContentIndexer.WrapSource("Text","V17","FUNCTION Text : Void\nBEGIN\nEND_FUNCTION"),3,true);
            var rows=store.CachedBlocks("PLC","","G",new[]{"Ladder","Ghost","Missing"});
            Check((bool)rows[0]["ok"]&&!(bool)rows[1]["ok"]&&!(bool)rows[2]["ok"],"mixed ready/catalog_only/missing: "+J(rows));
            Check((string)rows[0]["project_path"]==project,"cache returns full project identity");Check((string)rows[1]["cache_state"]=="catalog_only","catalog gap explicit");
            Check(rows[0].ContainsKey("indexed_utc")&&rows[0].ContainsKey("catalog_utc"),"collection timestamps present");
            Check((string)rows[0]["freshness"]=="unverified_against_live"&&!(bool)rows[0]["write_baseline"],"cache never live baseline");
            Check(!(bool)store.CachedBlocks("PLC","","",new[]{"Ladder"})[0]["ok"],"same name ambiguity rejected");
            Check(!(bool)store.CachedBlocks("OTHER","","G",new[]{"Ladder"})[0]["ok"],"PLC isolation");
            Check(!(bool)store.CachedBlocks("PLC","UnitA","G",new[]{"Ladder"})[0]["ok"],"software unit isolation");
            Reject(()=>store.CachedBlocks("","","G",new[]{"Ladder"}),"cache PLC identity required");
            store.MarkDirty(new[]{"Ladder"},"changed");var stale=store.CachedBlocks("PLC","","G",new[]{"Ladder"})[0];
            Check((bool)stale["ok"]&&(string)stale["freshness"]=="known_stale","stale cache explicitly usable for analysis only");
            var before=Directory.GetFiles(store.Root,"*",SearchOption.AllDirectories).ToDictionary(p=>p,p=>IndexJson.Hash(File.ReadAllText(p)));
            var server=FormatterServices.GetUninitializedObject(typeof(McpServer));var cfg=new AppConfig{ProjectPath=project,TiaPortalVersion="V17",PlcName="PLC"};
            var args=D("project_path",project,"plc_name","PLC","group_path","G","read_mode","cache","block_names",new object[]{"Ladder","Text","Ghost"});
            var result=(Dictionary<string,object>)Invoke(server,"ReadCachedBlocks","tia_read_blocks_batch",args,cfg);text=J(result);
            Check(text.Contains("FUNCTION Text")&&text.Contains("cached_external_source")&&text.Contains("known_stale"),"mixed cache text/XML with evidence");
            Check((bool)result["isError"],"partial batch signals failures");
            Invoke(server,"AttachIndexHint","tia_read_blocks_batch",args,result);
            Check(before.All(kv=>File.ReadAllText(kv.Key)!=null&&IndexJson.Hash(File.ReadAllText(kv.Key))==kv.Value)&&before.Count==Directory.GetFiles(store.Root,"*",SearchOption.AllDirectories).Length,"cache reads never rewrite index");
            args["format"]="readable";args["block_name"]="Ladder";result=(Dictionary<string,object>)Invoke(server,"ReadCachedBlocks","tia_read_block",args,cfg);Check(J(result).Contains("常闭"),"cached readable view");
            int exports=0;var liveArgs=D("block_names",new object[]{"Text","Ladder","Bad"});
            result=(Dictionary<string,object>)Invoke(server,"ReadLiveBlocks","tia_read_blocks_batch",liveArgs,cfg,new Func<string,string>(n=>{exports++;if(n=="Bad")throw new IOException("locked");return n=="Text"?xml.Replace("Ladder","Text").Replace("<ProgrammingLanguage>LAD</ProgrammingLanguage>","<ProgrammingLanguage>SCL</ProgrammingLanguage>"):xml;}),new Func<string,Dictionary<string,object>>(n=>D("source","SCL source","extension",".scl","group_path","G")));
            text=J(result);Check(exports==3&&text.Contains("SCL source")&&text.Contains("BLOCK_READ_FAILED")&&text.Contains("live_export"),"live mixed export and isolated error");
            Reject(()=>Invoke(server,"BlockReadMode",D("read_mode","silent_fallback")),"unsupported read mode rejected");
            File.WriteAllText(Path.Combine(store.Root,"source",(string)stale["content_hash"]+".xml"),"tampered");Check(!(bool)store.CachedBlocks("PLC","","G",new[]{"Ladder"})[0]["ok"],"hash mismatch refused");
        } finally {Directory.Delete(temp,true);}
        var web=FormatterServices.GetUninitializedObject(typeof(WebServer));
        var readerField=typeof(WebServer).GetField("_portalDiscoveryReader",BindingFlags.NonPublic|BindingFlags.Static);
        var oldReader=readerField.GetValue(null);var entered=new ManualResetEvent(false);var release=new ManualResetEvent(false);int calls=0;
        try {
            readerField.SetValue(null,new Func<AppConfig,IList<PortalProcessInfo>>(c=>{Interlocked.Increment(ref calls);entered.Set();release.WaitOne(5000);return new List<PortalProcessInfo>{new PortalProcessInfo{ProcessId=123,ProjectPath="fixture.ap17"}};}));
            var first=(IList<PortalProcessInfo>)Invoke(web,"GetRunningProjectsCached",new AppConfig(),false);
            Check(first.Count==0&&entered.WaitOne(1000),"first status returns while discovery is blocked");
            Check(J(Invoke(web,"PortalDiscoveryState")).Contains("pending"),"pending is explicit, not no-open-project proof");
            Invoke(web,"GetRunningProjectsCached",new AppConfig(),true);Check(calls==1,"refresh never spawns overlapping scans");release.Set();
            Check(SpinWait.SpinUntil(()=>J(Invoke(web,"PortalDiscoveryState")).Contains("ready"),2000),"background discovery completes");
            Check(((IList<PortalProcessInfo>)Invoke(web,"GetRunningProjectsCached",new AppConfig(),false)).Count==1,"completed snapshot available");
            string collected=(string)IndexJson.Map(Invoke(web,"PortalDiscoveryState"))["snapshot_utc"];
            readerField.SetValue(null,new Func<AppConfig,IList<PortalProcessInfo>>(c=>{throw new IOException("fixture discovery failed");}));
            Invoke(web,"GetRunningProjectsCached",new AppConfig(),true);
            Check(SpinWait.SpinUntil(()=>J(Invoke(web,"PortalDiscoveryState")).Contains("failed"),2000),"discovery failures explicit");
            Check(((IList<PortalProcessInfo>)Invoke(web,"GetRunningProjectsCached",new AppConfig(),false)).Count==1,"failure retains previous project snapshot");
            Check((string)IndexJson.Map(Invoke(web,"PortalDiscoveryState"))["snapshot_utc"]==collected,"failed scan never refreshes the snapshot timestamp");
        }finally{release.Set();readerField.SetValue(null,oldReader);entered.Dispose();release.Dispose();}
        Console.WriteLine("PASS: "+count+" mixed block read, cache provenance, structural view and nonblocking discovery checks; no TIA connection.");
    }
}
