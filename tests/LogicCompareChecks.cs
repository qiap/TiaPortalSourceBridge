using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using TiaPortalSourceBridge.Mcp;
using TiaPortalSourceBridge.Web;

class LogicCompareChecks
{
    static int count;
    static void Check(bool ok,string message){if(!ok)throw new Exception(message);count++;}
    static void Reject(Action action,string message){bool failed=false;try{action();}catch{failed=true;}Check(failed,message);}
    static Dictionary<string,object> D(params object[] v){var d=new Dictionary<string,object>();for(int i=0;i<v.Length;i+=2)d.Add((string)v[i],v[i+1]);return d;}
    static string J(object o){return RestrictedLogic.Json().Serialize(o);}
    static ContactEdit Edit(string xml){var d=BlockReadable.Parse(xml);return new ContactEdit{compile_unit_id=((XmlElement)d.SelectSingleNode("//SW.Blocks.CompileUnit")).GetAttribute("ID"),contact_uid=((XmlElement)d.SelectSingleNode("//*[local-name()='Part' and @Name='Contact']")).GetAttribute("UId"),scope="GlobalVariable",components=new[]{"NewPermit"},negated=true};}
    static CompareSide Side(string project,params string[] names){return new CompareSide{project_path=project,plc_name="PLC",unit_name="",group_path="G",mode="live",absence_verified=true,blocks=names.Select(n=>D("name",n,"group_path","G")).ToList()};}
    static Dictionary<string,object>[] Rows(Dictionary<string,object> r){return (Dictionary<string,object>[])r["items"];}
    public static int Main(string[] args){try{Run(Path.GetFullPath(args[0]));Console.WriteLine("PASS: "+count+" restricted LAD/GRAPH edit, no-replay write and dual-project comparison checks; no TIA connection.");return 0;}catch(Exception ex){Console.Error.WriteLine("Checks before failure: "+count+"\n"+ex);return 1;}}

    static void CheckGenerationVersions(string root)
    {
        var args = D("block_name","FormatCheck","block_number",1,"networks_json","[{\"coil\":\"Motor\",\"branches\":[[{\"symbol\":\"Start\"}]]}]");
        string templateDirectory = Path.Combine(Path.GetTempPath(), "tia-xml-templates-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(templateDirectory);
        try {
            for (int major = 16; major <= 21; major++) {
                string version = "V" + major;
                var lad = BlockReadable.Parse(LadBuilder.Generate(args, version));
                Check(lad.SelectSingleNode("/Document/Engineering/@version").Value == version, version+" LAD Engineering");
                Check(lad.SelectSingleNode("//*[local-name()='Sections']").NamespaceURI.EndsWith(major <= 17 ? "/v4" : "/v5"), version+" interface format");
                Check(lad.SelectSingleNode("//*[local-name()='FlgNet']").NamespaceURI.EndsWith(major >= 19 ? "/v5" : "/v4"), version+" LAD format");
                Check((lad.SelectSingleNode("/Document/SW.Blocks.FC/AttributeList/Namespace") != null) == (major >= 18), version+" PLC namespace");
                var hmi = BlockReadable.Parse("<Document><Engineering version='"+version+"'/><Hmi.Screen.Screen><AttributeList><Name>Sample</Name></AttributeList></Hmi.Screen.Screen></Document>");
                var tree = HmiModelBuilder.ReadTree((XmlElement)hmi.DocumentElement.LastChild);
                string model = J(D("objects",new[]{D("kind","screens","model",tree)}));
                var screen = HmiModelBuilder.Generate(model,version,root).Single();
                Check(screen.name == "Sample" && BlockReadable.Parse(screen.xml).SelectSingleNode("/Document/Engineering/@version").Value == version,version+" HMI same-version object");
                Reject(()=>HmiModelBuilder.Generate(model,major == 21 ? "V20" : "V21",root),version+" HMI wrong-version object");
            }
            var argsWithNamespace = new Dictionary<string,object>(args) {{"namespace","Station.Tools"}};
            Check(BlockReadable.Parse(LadBuilder.Generate(argsWithNamespace,"V18")).SelectSingleNode("/Document/SW.Blocks.FC/AttributeList/Namespace").InnerText == "Station.Tools","V18 LAD namespace retained");
            Reject(()=>LadBuilder.Generate(argsWithNamespace,"V17"),"no block namespace before V18");
            Reject(()=>LadBuilder.Generate(args,"V22"),"unknown LAD format rejected");
            string v17Graph = File.ReadAllText(Path.Combine(root,"templates","graph","v17.xml"));
            var preserved = BlockOperations.ParseBlock(v17Graph, true);
            Check(preserved.SelectSingleNode("/Document/Engineering/@version").Value == "V17" &&
                preserved.SelectSingleNode("//*[local-name()='Sections']").NamespaceURI.EndsWith("/v5"),
                "full V17 exported XML retains its own v5 interface on the block import path");
            foreach (string version in new[]{"V16","V18","V19","V20"}) {
                Reject(()=>GraphBuilder.Generate("{}",v17Graph,version,root),version+" cannot relabel V17 GRAPH donor");
            }
            string buttons = J(D("screens",new[]{D("name","Panel","items",new[]{D("name","B","type","Button")})}));
            string location = Path.Combine(templateDirectory,"templates","hmi","v18");
            Directory.CreateDirectory(location);
            string template = "<Document><Engineering version='V18'/><Hmi.Screen.Screen><ObjectList><Hmi.Screen.Button><AttributeList><Width>81</Width></AttributeList></Hmi.Screen.Button></ObjectList></Hmi.Screen.Screen></Document>";
            string file = Path.Combine(location,"Button.xml");
            File.WriteAllText(file, template);
            Check(HmiModelBuilder.Generate(buttons,"V18",templateDirectory).Single().xml.Contains("<Width>81</Width>"),"same-version HMI template selected");
            File.WriteAllText(file,template.Replace("V18","V17"));
            Reject(()=>HmiModelBuilder.Generate(buttons,"V18",templateDirectory),"mismatching HMI document template rejected");
        } finally { Directory.Delete(templateDirectory,true); }
    }
    static void Run(string root){
        CheckGenerationVersions(root);
        string xml=LadBuilder.Generate(D("block_name","Ladder","block_type","FB","block_number",1,"networks_json","[{\"title\":\"互锁\",\"coil\":\"Motor\",\"branches\":[[{\"symbol\":\"Permit\"},{\"symbol\":\"Start\"}]]},{\"title\":\"保持原样\",\"coil\":\"Other\",\"branches\":[[{\"symbol\":\"Keep\"}]]}]"),"V17");
        var edit=Edit(xml);string after=RestrictedLogic.Patch(xml,edit);var a=BlockReadable.Parse(xml);var b=BlockReadable.Parse(after);
        Reject(()=>RestrictedLogic.ParseEdit("{\"contact_uid\":\"1\",\"transition_expr\":\"false\",\"negated\":true}"),"unsupported requested edit not silently ignored");
        Reject(()=>RestrictedLogic.ParseEdit("{\"negated\":\"false\"}"),"polarity type strict");
        Check(RestrictedLogic.ParseEdit(J(edit)).negated==true,"edit serialization roundtrip");
        Check(after.Contains("NewPermit")&&after.Contains("Negated"),"operand and polarity changed");
        Check(J(RestrictedLogic.Describe(xml,edit)).Contains("Permit")&&J(RestrictedLogic.Describe(after,edit)).Contains("NewPermit"),"concise before/after contact review");
        Check(a.SelectNodes("//SW.Blocks.CompileUnit")[1].OuterXml==b.SelectNodes("//SW.Blocks.CompileUnit")[1].OuterXml,"other network untouched");
        Check(a.SelectSingleNode("/Document/SW.Blocks.FB/AttributeList").OuterXml==b.SelectSingleNode("/Document/SW.Blocks.FB/AttributeList").OuterXml,"block interface untouched");
        Check(a.SelectSingleNode("//*[local-name()='Wires']").OuterXml==b.SelectSingleNode("//*[local-name()='Wires']").OuterXml,"wire topology untouched");
        Reject(()=>RestrictedLogic.Patch(xml,new ContactEdit{compile_unit_id=edit.compile_unit_id,contact_uid="missing",negated=true}),"missing contact");
        Reject(()=>RestrictedLogic.Patch(xml,new ContactEdit{compile_unit_id="missing",contact_uid=edit.contact_uid,negated=true}),"missing network");
        Reject(()=>RestrictedLogic.Patch(xml,new ContactEdit{compile_unit_id=edit.compile_unit_id,contact_uid=edit.contact_uid}),"empty edit");
        Reject(()=>RestrictedLogic.Patch(xml.Replace("Name=\"Contact\"","Name=\"PContact\""),edit),"edge contact rejected");
        Reject(()=>RestrictedLogic.Patch(xml.Replace("Name=\"Contact\"","Name=\"Contact\" NewSemantic=\"x\""),edit),"unknown contact semantics rejected");
        Reject(()=>RestrictedLogic.Patch(xml.Replace("FlgNet/v4","FlgNet/v999"),edit),"unknown namespace rejected");
        var bad=Edit(xml);bad.components=new[]{"A[2]"};Reject(()=>RestrictedLogic.Patch(xml,bad),"array rejected");
        bad=Edit(xml);bad.scope="LiteralConstant";Reject(()=>RestrictedLogic.Patch(xml,bad),"literal scope rejected");
        bad=Edit(xml);bad.sequence_index=0;Reject(()=>RestrictedLogic.Patch(xml,bad),"mixed selectors rejected");
        var shared=BlockReadable.Parse(xml);var wire=shared.SelectSingleNode("//*[local-name()='Wire'][*[local-name()='IdentCon']]");wire.ParentNode.AppendChild(wire.CloneNode(true));Reject(()=>RestrictedLogic.Patch(shared.OuterXml,edit),"shared or duplicate operand wire rejected");
        var incomplete=BlockReadable.Parse(xml);var pin=incomplete.SelectSingleNode("//*[local-name()='NameCon' and @UId='"+edit.contact_uid+"' and @Name='in']");pin.ParentNode.RemoveChild(pin);Reject(()=>RestrictedLogic.Patch(incomplete.OuterXml,edit),"disconnected contact rejected");
        var graph=BlockReadable.Parse(File.ReadAllText(Path.Combine(root,"templates","graph","v17.xml")));
        var transition=graph.SelectSingleNode("//*[local-name()='Transition']");
        string ns=transition.NamespaceURI;
        transition.SelectSingleNode("*[local-name()='FlgNet']").InnerXml="<Parts xmlns='"+ns+"'><Access Scope='GlobalVariable' UId='1'><Symbol><Component Name='Permit'/></Symbol></Access><Part Name='Contact' UId='2'/><Part Name='TrCoil' UId='3'/></Parts><Wires xmlns='"+ns+"'><Wire UId='4'><Powerrail/><NameCon UId='2' Name='in'/></Wire><Wire UId='5'><IdentCon UId='1'/><NameCon UId='2' Name='operand'/></Wire><Wire UId='6'><NameCon UId='2' Name='out'/><NameCon UId='3' Name='in'/></Wire></Wires>";
        var ge=new ContactEdit{sequence_index=0,transition=((XmlElement)transition).GetAttribute("Number"),contact_uid="2",negated=true};
        string ga=RestrictedLogic.Patch(graph.OuterXml,ge);Check(ga.Contains("Negated"),"real GRAPH template transition patched");
        Check(J(RestrictedLogic.Describe(ga,ge)).Contains("\"negated\":true"),"GRAPH concise polarity review");
        var gd=BlockReadable.Parse(ga);Check(graph.SelectSingleNode("//*[local-name()='Steps']").OuterXml==gd.SelectSingleNode("//*[local-name()='Steps']").OuterXml,"GRAPH steps/actions unchanged");
        Check(graph.SelectSingleNode("//*[local-name()='Connections']").OuterXml==gd.SelectSingleNode("//*[local-name()='Connections']").OuterXml,"GRAPH connections unchanged");
        ge.sequence_index=1;Reject(()=>RestrictedLogic.Patch(graph.OuterXml,ge),"wrong GRAPH sequence rejected");
        ge.sequence_index=0;ge.transition="999";Reject(()=>RestrictedLogic.Patch(graph.OuterXml,ge),"missing transition rejected");
        string temp=Path.Combine(Path.GetTempPath(),"tia-logic-check-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try {
            string project=Path.Combine(temp,"P.ap17");var plan=RestrictedLogic.Create(temp,project,"PLC","Ladder","G",xml,edit);
            Check(File.Exists(Path.Combine(temp,"logs","logic-plans",plan.id+".json")),"plan durable");
            Check(RestrictedLogic.Load(temp,plan.id,project,"PLC").after_xml==after,"plan revalidation");
            Reject(()=>RestrictedLogic.Load(temp,plan.id,project,"Other"),"wrong PLC");
            Reject(()=>RestrictedLogic.Load(temp,plan.id,Path.Combine(temp,"Q.ap17"),"PLC"),"wrong project");
            Reject(()=>RestrictedLogic.Load(temp,"../outside",project,"PLC"),"plan path traversal");
            Reject(()=>RestrictedLogic.Create(temp,project,"PLC","Wrong","G",xml,edit),"wrong block identity");
            Reject(()=>RestrictedLogic.Create(temp,project,"PLC","Ladder","G",after,edit),"no-op refused");
            int imports=0;Func<string,string,XmlBatchResult> success=(items,id)=>{imports++;Check(items.Contains(plan.before_signature),"write retains live precondition");return new XmlBatchResult{ok=true,compiled=true,error_count=0};};
            Reject(()=>RestrictedLogic.Apply(temp,plan,()=>after,success),"stale plan does not import");Check(imports==0,"no import on stale baseline");
            int reads=0;var result=(Dictionary<string,object>)RestrictedLogic.Apply(temp,plan,()=>++reads==1?xml:after,success);
            Check((bool)result["ok"]&&(string)result["state"]=="verified_unsaved"&&!(bool)result["saved"]&&!(bool)result["downloaded"],"compile/readback success, no save/download");
            RestrictedLogic.Apply(temp,plan,()=>{throw new Exception("must not reread/reapply");},success);Check(imports==1,"durable idempotence");
            var fail=RestrictedLogic.Create(temp,project,"PLC","Ladder","G",xml,edit);
            result=(Dictionary<string,object>)RestrictedLogic.Apply(temp,fail,()=>xml,(items,id)=>{throw new IOException("partial import");});Check(!(bool)result["ok"]&&(string)result["state"]=="needs_review","partial import retained");
            Check(File.Exists(Path.Combine(temp,"logs","logic-plans",fail.id+".claim")),"uncertain writes claimed");
            var unknown=RestrictedLogic.Create(temp,project,"PLC","Ladder","G",xml,edit);File.WriteAllText(Path.Combine(temp,"logs","logic-plans",unknown.id+".claim"),"");Reject(()=>RestrictedLogic.Apply(temp,unknown,()=>xml,success),"interrupted write not replayed");
            var different=RestrictedLogic.Create(temp,project,"PLC","Ladder","G",xml,edit);result=(Dictionary<string,object>)RestrictedLogic.Apply(temp,different,()=>xml,(items,id)=>new XmlBatchResult{ok=true,compiled=true,error_count=0});Check(!(bool)result["ok"],"full readback mismatch fails");
            var compileFail=RestrictedLogic.Create(temp,project,"PLC","Ladder","G",xml,edit);reads=0;result=(Dictionary<string,object>)RestrictedLogic.Apply(temp,compileFail,()=>++reads==1?xml:after,(items,id)=>new XmlBatchResult{ok=false,compiled=true,error_count=1});Check(!(bool)result["ok"],"compile errors fail even with matching readback");
            var tampered=RestrictedLogic.Create(temp,project,"PLC","Ladder","G",xml,edit);tampered.after_xml=tampered.after_xml.Replace("Motor","HiddenMutation");File.WriteAllText(Path.Combine(temp,"logs","logic-plans",tampered.id+".json"),J(tampered));Reject(()=>RestrictedLogic.Load(temp,tampered.id,project,"PLC"),"plan tamper refused");
        }finally{Directory.Delete(temp,true);}
        var left=Side("A","Ladder","OnlyA");var right=Side("B","Ladder","OnlyB");
        Func<CompareSide,Dictionary<string,object>,Dictionary<string,object>> read=(s,e)=>D("ok",true,"xml",s==left?xml:after,"source_kind","live_export");
        var report=ProjectCompare.Run(left,right,0,20,"",read);var rows=Rows(report);
        Check(rows.All(r=>!((string)r["relative_identity"]).Contains("\n"))&&rows.Any(r=>(string)r["relative_identity"]=="Ladder"),"compact root block identity");
        Check(rows.Any(r=>(string)r["status"]=="changed"),"changed logic detected");Check(rows.Any(r=>(string)r["status"]=="removed_from_b"),"confirmed live removal");Check(rows.Any(r=>(string)r["status"]=="added_in_b"),"confirmed live addition");
        left.absence_verified=false;right.absence_verified=false;left.mode=right.mode="cache";
        report=ProjectCompare.Run(left,right,0,20,"",read);Check(Rows(report).Count(r=>((string)r["status"]).StartsWith("unconfirmed"))==2,"cache absence never deletion");
        var error=Side("B");error.error="read failed";error.absence_verified=false;
        report=ProjectCompare.Run(left,error,0,20,"",read);Check(Rows(report).All(r=>(string)r["status"]=="unconfirmed_only_a"),"failed catalog not deletion");
        report=ProjectCompare.Run(left,right,0,20,"",(s,e)=>D("ok",false,"code","catalog_only"));Check(Rows(report).Any(r=>(string)r["status"]=="unknown"&&J(r).Contains("catalog_only")),"read failures preserved");
        report=ProjectCompare.Run(left,right,0,1,"",read);Check((int)report["next_offset"]==1&&(int)report["total_pairs"]==3,"pagination");
        var next=ProjectCompare.Run(left,right,1,1,(string)report["catalog_token"],read);Check((int)next["offset"]==1,"stable catalog continuation");
        right.blocks.Add(D("name","New","group_path","G"));Reject(()=>ProjectCompare.Run(left,right,1,1,(string)report["catalog_token"],read),"changed catalog rejects mixed paging");
        right=Side("B","Ladder");right.blocks.Add(D("name","Ladder","group_path","G"));report=ProjectCompare.Run(left,right,0,20,"",read);Check(Rows(report).Any(r=>r.ContainsKey("reason")&&(string)r["reason"]=="ambiguous_identity"),"ambiguous block not arbitrarily paired");
        left=Side("A","Ladder");right=Side("B","Ladder");report=ProjectCompare.Run(left,right,0,20,"",(s,e)=>D("ok",true,"xml",xml));Check((string)Rows(report)[0]["status"]=="structurally_equal","equal source");
        report=ProjectCompare.Run(left,right,0,20,"",(s,e)=>D("ok",true,"xml",s==left?"text before":"text after"));Check((string)Rows(report)[0]["status"]=="changed_raw","unsupported structure preserves difference");
        Check(ProjectCompare.InGroup("G/Sub","G")&&!ProjectCompare.InGroup("G2/Sub","G"),"group boundary exact");
        Check(!ProjectOperationGuard.IsMutation("tia_compare_projects",D())&&!ProjectOperationGuard.IsMutation("tia_read_logic_patch_plan",D())&&ProjectOperationGuard.IsMutation("tia_write_logic_patch",D()),"correct operation guards");
    }
}
