using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using TiaPortalSourceBridge.Mcp;
using TiaPortalSourceBridge.Web;
using TiaPortalSourceBridge.Openness;

internal static class HmiEditorChecks
{
    static int count;
    static void Check(bool condition,string name){if(!condition)throw new Exception(name);count++;}
    static void Reject(Action action,string name){try{action();}catch(ArgumentException){count++;return;}catch(InvalidOperationException){count++;return;}throw new Exception("Accepted: "+name);}
    static readonly Dictionary<string,string> Tags=new Dictionary<string,string>{{"Old","Bool"},{"New","Bool"},{"Wrong","Int"},{"Unknown",""}};
    static readonly HashSet<string> Screens=new HashSet<string>{"Home","Next"},Lists=new HashSet<string>{"States"};
    static string Button="<Hmi.Screen.Button ID='2' CompositionName='ScreenItems'><AttributeList><ObjectName>Start</ObjectName><Left>10</Left><Top>20</Top><Width>100</Width><Height>40</Height><BackColor>1, 2, 3</BackColor><InvisibleFlag>preserve</InvisibleFlag></AttributeList><ObjectList><MultilingualText CompositionName='TextOff'><ObjectList><MultilingualTextItem><AttributeList><Culture>zh-CN</Culture><Text><body><p>Start</p><p>Cycle</p></body></Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText><Hmi.Event.FunctionListEntry><AttributeList><Name>ActivateScreen</Name></AttributeList><ObjectList><Hmi.Event.FunctionListEntryParameter><AttributeList><Name>Screen name</Name></AttributeList><LinkList><Value><Name>Home</Name></Value></LinkList></Hmi.Event.FunctionListEntryParameter></ObjectList></Hmi.Event.FunctionListEntry></ObjectList><LinkList><Tag><Name>Old</Name></Tag><TextList><Name>States</Name></TextList></LinkList></Hmi.Screen.Button>";
    static string Page(string content,string name="Process"){return "<Document><Hmi.Screen.Screen ID='1'><AttributeList><Name>"+name+"</Name><Width>800</Width><Height>480</Height></AttributeList><ObjectList>"+content+"</ObjectList></Hmi.Screen.Screen></Document>";}
    static HmiPageEditor Fresh(){return new HmiPageEditor(Page(Button));}
    static string Key(HmiPageEditor e,string kind){return e.Fields.Values.First(f=>f.kind==kind).key;}
    static object[] Patch(HmiPageEditor e,string key,string value){return e.Patch(CylinderWritePipeline.Json().Serialize(new[]{new{key,value}}),Tags,Screens,Lists);}
    static void PerformanceChecks()
    {
        var cache=new HmiReadCache();var token=new object();int reads=0;
        Func<string> read=()=>{reads++;return "xml";};cache.Get(token,read);cache.Get(token,read);Check(reads==1,"one export per scoped object");cache.Clear();cache.Get(token,read);Check(reads==2,"cache invalidated before readback");
        token=new object();Reject(()=>cache.Get(token,()=>{throw new InvalidOperationException();}),"failed export");cache.Get(token,read);Check(reads==3,"failed exports not cached");
        string original=Page(Button), renumbered=original.Replace("ID='1'","ID='8'").Replace("ID='2'","ID='9'");
        Check(HmiXmlIdentity.Equal(original,renumbered.Replace("<ObjectList>","\n<ObjectList>\n")),"generated IDs and structural whitespace ignored");
        Check(!HmiXmlIdentity.Equal(original,original.Replace("Start</p>"," Start </p>")),"user text whitespace significant");
        Check(!HmiXmlIdentity.Equal(original,original.Replace("preserve","changed")),"unexposed attributes significant");
        string linked=Page("<A ID='2'/><B ID='3'/><Link TargetID='2'/>");
        Check(HmiXmlIdentity.Equal(linked,linked.Replace("ID='2'","ID='12'").Replace("ID='3'","ID='13'")),"references renumbered consistently");
        Check(!HmiXmlIdentity.Equal(linked,linked.Replace("TargetID='2'","TargetID='3'")),"rewired references significant");
        var e=Fresh();Check(Patch(e,Key(e,"text"),e.Fields[Key(e,"text")].value).Length==0,"unchanged field is no-op");
        foreach(bool reverse in new[]{false,true}) {
            string line="<Hmi.Screen.Line CompositionName='ScreenItems'><AttributeList><ObjectName>Line</ObjectName><Left>10</Left><Top>20</Top><Width>30</Width><Height>40</Height><StartLeft>"+(reverse?40:10)+"</StartLeft><EndLeft>"+(reverse?10:40)+"</EndLeft><StartTop>20</StartTop><EndTop>60</EndTop></AttributeList></Hmi.Screen.Line>";
            e=new HmiPageEditor(Page(line));string key=e.Fields.Values.Single(f=>f.label=="Left").key;Patch(e,key,"100");
            Check(e.Document.SelectSingleNode("//StartLeft").InnerText==(reverse?"130":"100"),"line start updated with direction");
            Check(e.Document.SelectSingleNode("//EndLeft").InnerText==(reverse?"100":"130"),"line end updated with direction");
            string expected=e.Document.OuterXml;var bad=HmiValidation.Parse(expected);bad.SelectSingleNode("//StartLeft").InnerText=reverse?"100":"130";bad.SelectSingleNode("//EndLeft").InnerText=reverse?"130":"100";
            Check(HmiPageEditor.Verify(expected,bad.OuterXml,new[]{key}).Length==2,"line direction verified after compile");
            Check(HmiValidation.Validate(Page(line.Replace("<Width>30","<Width>31"))).Any(x=>x.Contains("line_geometry_conflict")),"invalid line rejected before import");
        }
        Check(HmiValidation.Validate(Page(Button+Button)).Any(x=>x.Contains("screen_object_name_conflict")),"duplicate control names rejected");
        var pages=new[]{new HmiEditorRecord{project_path="p",plc_name="plc",hmi_name="h",kind="screens",name="a"},new HmiEditorRecord{project_path="p",plc_name="plc",hmi_name="h",kind="screens",name="b"}};
        Reject(()=>HmiEditorBatch.Validate(new[]{pages[0],pages[0]}),"duplicate batch page");
        Reject(()=>HmiEditorBatch.Validate(new[]{pages[0],new HmiEditorRecord{project_path="other"}}),"mixed target");
        Reject(()=>HmiEditorBatch.Validate(new HmiEditorRecord[21]),"batch size limit");
        foreach(string scenario in new[]{"changed","unchanged","stale","import_failure","compile_failure","verify_failure"}) {
            var events=new List<string>();var result=new Dictionary<string,object>();int writes=0,compiles=0,saves=0;
            Action run=()=>HmiEditorBatch.Execute(pages,result,page=>{events.Add("check "+page.name);if(scenario=="stale"&&page.name=="b")throw new InvalidOperationException("stale");},()=>{writes++;events.Add("write");return new Dictionary<string,object>{{"failure_count",scenario=="import_failure"?1:0},{"changed_count",scenario=="unchanged"?0:2}};},()=>{compiles++;return new Dictionary<string,object>{{"compile_state","Success"},{"error_count",scenario=="compile_failure"?1:0}};},page=>scenario=="verify_failure"?new object[]{"mismatch"}:new object[0],()=>saves++,stage=>{});
            bool success=scenario=="changed"||scenario=="unchanged";if(success)run();else Reject(run,scenario);
            Check(writes==(scenario=="stale"?0:1),scenario+" batch import count");
            Check(compiles==(new[]{"changed","compile_failure","verify_failure"}.Contains(scenario)?1:0),scenario+" compile count");
            Check(saves==(scenario=="changed"?1:0),scenario+" save count");
            Check(events.Take(2).SequenceEqual(new[]{"check a","check b"}),scenario+" all pages checked first");
        }
    }
    static void Main(string[] args){try{
        var e=Fresh();Check(e.Controls.Count==1&&e.Reasons.Count==0,"ordinary page editable");Check(e.Fields.Values.First(f=>f.kind=="text").value=="Start\nCycle","paragraphs retained");
        string textKey=Key(e,"text"),expectedText="<img src=x> & 中文\nsecond line";
        Patch(e,textKey,expectedText);Check(e.Document.OuterXml.Contains("&lt;img"),"XML metacharacters escaped");Check(e.Document.SelectSingleNode("//InvisibleFlag").InnerText=="preserve","unexposed attributes retained");Check(e.Document.SelectSingleNode("//Hmi.Event.FunctionListEntry/AttributeList/Name").InnerText=="ActivateScreen","events retained");
        Check(new HmiPageEditor(e.Document.OuterXml).Fields[textKey].value==expectedText,"multiline round trip");
        Check(HmiPageEditor.Verify(e.Document.OuterXml,e.Document.OuterXml.Replace("ID=\"2\"","ID=\"999\""),new[]{textKey}).Length==0,"IDs do not affect readback identity");
        Check(HmiPageEditor.Verify(e.Document.OuterXml,Page(Button),new[]{textKey}).Length==1,"readback mismatch detected");
        var grouped=new HmiPageEditor(Page("<Hmi.Screen.Group CompositionName='ScreenItems'><AttributeList><ObjectName>Group1</ObjectName></AttributeList><ObjectList>"+Button+"</ObjectList></Hmi.Screen.Group>"));Check(grouped.Fields.Count==Fresh().Fields.Count,"nested controls not duplicated");
        foreach(string kind in new[]{"tag","screen"}){e=Fresh();Patch(e,Key(e,kind),kind=="tag"?"New":kind=="screen"?"Next":"States");if(kind=="text_list")break;Check(true,"valid dependency "+kind);}
    }catch(Exception ex){Console.Error.WriteLine(ex);Environment.ExitCode=1;return;}
    try{
        foreach(string tag in new[]{"Missing","Wrong","Unknown"}){var e=Fresh();Reject(()=>Patch(e,Key(e,"tag"),tag),"invalid tag "+tag);}
        foreach(string kind in new[]{"screen","text_list"}){var e=Fresh();Reject(()=>Patch(e,Key(e,kind),"Missing"),"missing dependency "+kind);}
        foreach(string n in new[]{"-1","4097","1.2","1e2"}){var e=Fresh();Reject(()=>Patch(e,Key(e,"number"),n),"bad geometry");}
        {var e=Fresh();Reject(()=>Patch(e,e.Fields.Values.First(f=>f.label=="Width").key,"0"),"zero width");Reject(()=>Patch(e,Key(e,"color"),"256,0,0"),"bad color");Reject(()=>Patch(e,"../xml","x"),"unexposed field");Reject(()=>Patch(e,Key(e,"text"),new string('x',2049)),"oversized value");}
        {var e=Fresh();var change=new{key=Key(e,"text"),value="new"};Reject(()=>e.Patch(CylinderWritePipeline.Json().Serialize(new[]{change,change}),Tags,Screens,Lists),"duplicate field");}
        foreach(string name in new[]{"Alarm","诊断页","HistoricalAlarms"}){var e=new HmiPageEditor(Page(Button,name));Reject(()=>Patch(e,Key(e,"text"),"Changed"),"protected page");}
        {var e=new HmiPageEditor(Page(Button+"<Hmi.Screen.AlarmView CompositionName='ScreenItems'><AttributeList><ObjectName>View</ObjectName></AttributeList></Hmi.Screen.AlarmView>"));Reject(()=>Patch(e,Key(e,"text"),"Changed"),"unsupported compound control");}
        if(args.Length>0&&Directory.Exists(args[0]))foreach(string file in Directory.GetFiles(args[0],"screens__*.xml")){
            var editor=new HmiPageEditor(File.ReadAllText(file));Check(editor.Controls.Count>0,"exported page controls");
            if(editor.Reasons.Count==0){var text=editor.Fields.Values.FirstOrDefault(f=>f.kind=="text");if(text!=null){Patch(editor,text.key,text.value+" test");Check(HmiPageEditor.Verify(editor.Document.OuterXml,editor.Document.OuterXml,new[]{text.key}).Length==0,"exported page patch");}}
            Console.WriteLine(Path.GetFileName(file)+": "+editor.Controls.Count+" controls, "+editor.Fields.Count+" fields, "+(editor.Reasons.Count>0?"read-only":"editable"));
        }
        PerformanceChecks();
        Console.WriteLine("PASS: "+count+" HMI page, cache, batch, no-op and readback checks; no TIA writes");
    }catch(Exception ex){Console.Error.WriteLine(ex);Environment.ExitCode=1;}}
}
