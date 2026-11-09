using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using TiaPortalSourceBridge.Web;
using TiaPortalSourceBridge.Indexing;
internal static class CylinderApplyChecks
{
    static int count;
    static void Check(bool ok,string message) { if(!ok)throw new Exception(message); count++; }
    static void Reject(Action work,string message) { try {work();} catch(InvalidOperationException) {count++;return;} throw new Exception(message); }
    static string Attr(string value) { return System.Security.SecurityElement.Escape(value); }
    static string Access(params string[] components) {return "<Access Scope='GlobalVariable'><Symbol>"+string.Join("",components.Select(c=>"<Component Name='"+Attr(c)+"'/>"))+"</Symbol></Access>";}
    static string Db(string name,int number,string fields) {return "<Document><SW.Blocks.GlobalDB ID='0'><AttributeList><Name>"+name+"</Name><Number>"+number+"</Number><MemoryLayout>Standard</MemoryLayout><Interface><Sections><Section Name='Static'>"+fields+"</Section></Sections></Interface></AttributeList></SW.Blocks.GlobalDB></Document>";}
    static string Host()
    {
        var parameters=new[] {"i_Wpos","oOutput_W","i_Interlock_Wpos","HMI_Output_W","Timeout_ToWpos","Unused"};
        var dirs=new[] {"Input","Output","Input","Output","Output","Output"};
        var operands=new[] {Access("Signals","Input","OldI"),Access("Signals","Output","OldQ"),Access("Signals","Input","Stop"),Access("Data","Z01","Valve"),Access("Alarm","Timeout").Replace("Name='Timeout'/>","Name='Timeout' AccessModifier='Array'><Access Scope='LiteralConstant'><Constant><ConstantValue>1</ConstantValue></Constant></Access><Access Scope='LiteralConstant'><Constant><ConstantValue>1</ConstantValue></Constant></Access></Component>")};
        string parts=string.Join("",operands.Select((s,i)=>s.Replace("<Access Scope='GlobalVariable'", "<Access UId='"+(i+1)+"' Scope='GlobalVariable'")));
        parts+="<Call UId='20'><CallInfo Name='Cylinder' BlockType='FB'><Instance Scope='LocalVariable'><Component Name='Z01'/></Instance>"+string.Join("",parameters.Select((p,i)=>"<Parameter Name='"+p+"' Section='"+dirs[i]+"' Type='Bool'/>"))+"</CallInfo></Call>";
        string wires="<Wire UId='30'><Powerrail/><NameCon UId='20' Name='en'/></Wire>"+string.Join("",parameters.Select((p,i)=>"<Wire UId='"+(31+i)+"'>"+(i==5?"<OpenCon UId='90'/>":"<IdentCon UId='"+(i+1)+"'/>")+"<NameCon UId='20' Name='"+p+"'/></Wire>"));
        return "<Document><SW.Blocks.FB ID='0'><AttributeList><Name>Host</Name><Number>1</Number><Interface><Sections><Section Name='Static'><Member Name='Z01' Datatype='&quot;Cylinder&quot;'/></Section></Sections></Interface></AttributeList><ObjectList><SW.Blocks.CompileUnit ID='1' CompositionName='CompileUnits'><AttributeList><NetworkSource><FlgNet><Parts>"+parts+"</Parts><Wires>"+wires+"</Wires></FlgNet></NetworkSource><ProgrammingLanguage>LAD</ProgrammingLanguage></AttributeList><ObjectList><MultilingualText ID='2' CompositionName='Title'><ObjectList><MultilingualTextItem ID='3'><AttributeList><Text>old</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText></ObjectList></SW.Blocks.CompileUnit></ObjectList></SW.Blocks.FB></Document>";
    }
    static Dictionary<string,object> Request()
    { return new Dictionary<string,object> { {"sample_id","Z01"},{"instance_name","Z02"},{"cylinder_name","夹紧气缸"},{"alarm_index","2"},
        {"rules",new Dictionary<string,object>{{"i_Wpos","input"},{"oOutput_W","output"},{"i_Interlock_Wpos","shared"},{"HMI_Output_W","device"},{"Timeout_ToWpos","index"},{"Unused","open"}}},
        {"bindings",new Dictionary<string,object>{{"i_Wpos","i"},{"oOutput_W","q"}}} }; }
    static void Synthetic()
    {
        var host=IndexEntry.Create("plc.block","PLC","Area","Host","LAD","1");
        var db=IndexEntry.Create("plc.block","PLC","Area","Signals","DB","2");
        var data=IndexEntry.Create("plc.block","PLC","Area","Data","DB","3");
        var alarm=IndexEntry.Create("plc.block","PLC","Area","Alarm","DB","4");
        var mapper=IndexEntry.Create("plc.block","PLC","Area/Mapping","Map","STL","5");
        var code=new Dictionary<string,string>();code[host.id]=Host();
        code[db.id]=Db("Signals",2,"<Member Name='Input' Datatype='Struct'><Member Name='OldI' Datatype='Bool'/><Member Name='Stop' Datatype='Bool'/><Member Name='SpareI' Datatype='Bool'/></Member><Member Name='Output' Datatype='Struct'><Member Name='OldQ' Datatype='Bool'/><Member Name='SpareQ' Datatype='Bool'/></Member>");
        code[data.id]=Db("Data",3,"<Member Name='Z01' Datatype='&quot;CylinderData&quot;'/><Member Name='Z02' Datatype='&quot;CylinderData&quot;'/>");
        code[alarm.id]=Db("Alarm",4,"<Member Name='Timeout' Datatype='Array[1..8,1..2] of Bool'/>");
        code[mapper.id]="<Document><SW.Blocks.FC ID='0'><AttributeList><Name>Map</Name><Number>5</Number></AttributeList><ObjectList><SW.Blocks.CompileUnit ID='1'><AttributeList><NetworkSource><StatementList><StlStatement><StlToken Text='A'/>"+Access("InputTag")+"</StlStatement><StlStatement><StlToken Text='='/>"+Access("Signals","Input","SpareI")+"</StlStatement><StlStatement><StlToken Text='A'/>"+Access("Signals","Output","SpareQ")+"</StlStatement><StlStatement><StlToken Text='='/>"+Access("OutputTag")+"</StlStatement></StatementList></NetworkSource></AttributeList></SW.Blocks.CompileUnit></ObjectList></SW.Blocks.FC></Document>";
        var snap=new IoSnapshot {cylinderBlocks=new[]{host,db,data,alarm,mapper},cylinderProgramsTotal=2,cylinderProgramsRead=2,tables_read=1,tables_total=1};
        snap.rows.Add(new IoRow{id="i",unit="",name="InputTag",address="%I3.0",area="I",data_type="Bool"});
        snap.rows.Add(new IoRow{id="q",unit="",name="OutputTag",address="%Q3.0",area="Q",data_type="Bool"});
        snap.rows.Add(new IoRow{id="di",unit="",name="Signals.Input.SpareI",area="DB",data_type="Bool"});
        snap.rows.Add(new IoRow{id="dq",unit="",name="Signals.Output.SpareQ",area="DB",data_type="Bool"});
        Func<IndexEntry,string> source=b=>code[b.id];
        var plan=CylinderApplyPlan.Build(snap,host,source,Request());
        snap.project_path="E:/Fixture.ap17";var motorRequest=Request();motorRequest["device_kind"]="motor";motorRequest["cylinder_name"]="输送电机";motorRequest["io_suffixes"]=new Dictionary<string,object>{{"i_Wpos","运行反馈"},{"oOutput_W","启动输出"}};
        var motorPlan=CylinderApplyPlan.Build(snap,host,source,motorRequest);Check(motorPlan.Items.Any(i=>Convert.ToString(i["xml"]).Contains("输送电机运行反馈I3.0"))&&motorPlan.Items.Any(i=>Convert.ToString(i["xml"]).Contains("输送电机启动输出Q3.0")),"generic device uses explicitly reviewed per-pin suffixes");
        Check(Convert.ToString(motorPlan.Items.Last()["xml"]).Contains("Name=\"Stop\""),"generic reuse preserves donor interlock binding");
        snap.project_path="E:/Fixture.ap21";Reject(()=>CylinderApplyPlan.Build(snap,host,source,motorRequest),"new actuator write capability limited to V17");snap.project_path="E:/Fixture.ap17";
        Check(plan.Items.Count==3,"mapping DB + map + host only, existing data member reused");
        Check(Convert.ToString(plan.Items.Last()["xml"]).Contains("<Name>Host</Name>"),"host imported last");
        Check(plan.Items.All(i=>Convert.ToString(i["expected_content_signature"]).Length==64),"stable import preconditions included");
        string hostXml=Convert.ToString(plan.Items.Last()["xml"]); var doc=new XmlDocument();doc.LoadXml(hostXml);
        var units=doc.SelectNodes("//SW.Blocks.CompileUnit").Cast<XmlElement>().ToArray();
        Check(units.Length==2 && units[0].OuterXml==Parse(Host()).SelectSingleNode("//SW.Blocks.CompileUnit").OuterXml,"existing network remains byte-equivalent XML");
        Check(units[1].OuterXml.Contains("Name=\"Z02\"") && units[1].OuterXml.Contains("夹紧气缸伸出到位I3.0") && units[1].OuterXml.Contains("夹紧气缸伸出阀Q3.0"),"new instance and physical endpoint names generated");
        Check(units[1].OuterXml.Contains("Name=\"Stop\"") && units[1].OuterXml.Contains("<OpenCon"),"shared interlock and explicit open pin preserved");
        Check(doc.SelectNodes("//@ID").Cast<XmlAttribute>().Select(a=>a.Value).Distinct().Count()==doc.SelectNodes("//@ID").Count,"global XML IDs unique");
        Check(units[1].SelectNodes(".//Access[@Scope='GlobalVariable']").Count==5,"unused source operand clones removed");
        Check(units[1].SelectSingleNode(".//Component[@Name='Timeout']//ConstantValue").InnerText=="2","new alarm index");
        Check(Convert.ToString(plan.Items[0]["xml"]).Contains("SpareI")==false && Convert.ToString(plan.Items[1]["xml"]).Contains("SpareQ")==false,"declaration and mapping references renamed together");
        Check(code[host.id]==Host() && code[db.id].Contains("SpareI"),"planning does not mutate source snapshot");
        var request=Request();request["instance_name"]="Z01";Reject(()=>CylinderApplyPlan.Build(snap,host,source,request),"existing instance rejected");
        request=Request();request["alarm_index"]="9";Reject(()=>CylinderApplyPlan.Build(snap,host,source,request),"out of range alarm rejected");
        request=Request();request["alarm_index"]="1";Reject(()=>CylinderApplyPlan.Build(snap,host,source,request),"existing alarm output rejected");
        request=Request();((Dictionary<string,object>)request["rules"])["HMI_Output_W"]="shared";Reject(()=>CylinderApplyPlan.Build(snap,host,source,request),"untrusted shared output rejected");
        request=Request();request["cylinder_name"]="bad\nname";Reject(()=>CylinderApplyPlan.Build(snap,host,source,request),"unsafe display name rejected");
        request=Request();((Dictionary<string,object>)request["bindings"])["i_Wpos"]="q";Reject(()=>CylinderApplyPlan.Build(snap,host,source,request),"wrong I/O direction rejected");
        snap.cylinderProgramsTotal=3;Reject(()=>CylinderApplyPlan.Build(snap,host,source,Request()),"incomplete reference coverage rejected");snap.cylinderProgramsTotal=2;
        code[host.id]=Host().Replace("<Powerrail/>","<OpenCon/>");Reject(()=>CylinderApplyPlan.Build(snap,host,source,Request()),"conditional or open EN rejected");code[host.id]=Host();
        code[host.id]=Host().Replace("</ObjectList></SW.Blocks.FB>",Access("Signals","Input","SpareI")+"</ObjectList></SW.Blocks.FB>");Reject(()=>CylinderApplyPlan.Build(snap,host,source,Request()),"newly occupied point rejected");code[host.id]=Host();
        code[host.id]=Host().Replace("</ObjectList></SW.Blocks.FB>",Access("Data")+"</ObjectList></SW.Blocks.FB>");Reject(()=>CylinderApplyPlan.Build(snap,host,source,Request()),"whole data DB use prevents member reuse");code[host.id]=Host();
        Func<string,string> withScl=expression=>Host().Replace("</ObjectList></SW.Blocks.FB>","<SW.Blocks.CompileUnit ID='A'><AttributeList><NetworkSource><StructuredText><Token Text='"+Attr(expression)+"'/></StructuredText></NetworkSource></AttributeList></SW.Blocks.CompileUnit></ObjectList></SW.Blocks.FB>");
        code[host.id]=withScl("\"Alarm\".Timeout[2,2] := TRUE;");
        Check(!ContentIndexer.Parse(code[host.id]).references.Any(r=>(r.relation=="writes" || r.relation=="reads_writes") && r.target.Contains("[2,2]")),"SCL fixture has unknown write direction");
        Reject(()=>CylinderApplyPlan.Build(snap,host,source,Request()),"unknown-direction reference in another column reserves first dimension");
        code[host.id]=withScl("\"Alarm\".Timeout[3,1] := TRUE;");Check(CylinderApplyPlan.Build(snap,host,source,Request()).Items.Count==3,"other fixed first dimension does not reserve chosen index");
        code[host.id]=withScl("\"Alarm\".Timeout[#Index,1] := TRUE;");Reject(()=>CylinderApplyPlan.Build(snap,host,source,Request()),"dynamic global array reference blocks automatic index allocation");
        code[host.id]=withScl("\"Alarm\".Timeout := other;");Reject(()=>CylinderApplyPlan.Build(snap,host,source,Request()),"whole-array unknown-direction reference rejected");
        code[host.id]=Host().Replace("</ObjectList></SW.Blocks.FB>","<Access Scope='Address'><Address Area='DB' Type='Bit' BlockNumber='4' BitOffset='16'/></Access></ObjectList></SW.Blocks.FB>");
        Reject(()=>CylinderApplyPlan.Build(snap,host,source,Request()),"absolute address in alarm DB requires manual verification");code[host.id]=Host();
        code[host.id]=Host().Replace("Name='Unused' Section='Output'","Name='Feedback' Section='Input'").Replace("Name='Unused'","Name='Feedback'").Replace("<OpenCon UId='90'/>","<IdentCon UId='2'/>");
        request=Request();var feedbackRules=(Dictionary<string,object>)request["rules"];feedbackRules.Remove("Unused");feedbackRules["Feedback"]="feedback";
        var feedbackPlan=CylinderApplyPlan.Build(snap,host,source,request);var feedbackDoc=Parse(Convert.ToString(feedbackPlan.Items.Last()["xml"]));
        var feedbackUnit=feedbackDoc.SelectNodes("//SW.Blocks.CompileUnit").Cast<XmlElement>().Last();
        string feedbackId=feedbackUnit.SelectSingleNode(".//Wire[NameCon/@Name='Feedback']/IdentCon/@UId").Value;
        Check(feedbackUnit.SelectSingleNode(".//Access[@UId='"+feedbackId+"']/Symbol/Component[last()]/@Name").Value=="夹紧气缸伸出阀Q3.0","feedback follows new output even when parameter order is reversed");
        code[host.id]=Host().Replace("Name='Z01' Datatype='&quot;Cylinder&quot;'/>","Name='Z01' Datatype='&quot;Cylinder&quot;'><Sections><Section Name='Input'><Member Name='Configured' Datatype='Bool'><StartValue>true</StartValue></Member></Section></Sections><Comment><MultiLanguageText Lang='zh-CN'>old cylinder</MultiLanguageText></Comment></Member>");
        var configured=CylinderApplyPlan.Build(snap,host,source,Request());var configuredDoc=Parse(Convert.ToString(configured.Items.Last()["xml"]));
        Check(configuredDoc.SelectSingleNode("//Member[@Name='Z02']//StartValue").InnerText=="true" && configuredDoc.SelectSingleNode("//Member[@Name='Z02']/Comment").InnerText=="Z02 夹紧气缸","typed nested configuration preserved and copied member comment updated");code[host.id]=Host();
        code[data.id]=code[data.id].Replace("Name='Z02' Datatype='&quot;CylinderData&quot;'","Name='Z02' Datatype='Bool'");Reject(()=>CylinderApplyPlan.Build(snap,host,source,Request()),"conflicting data type rejected");
        code[data.id]=Db("Data",3,"<Member Name='Z01' Datatype='&quot;CylinderData&quot;'/>");
        plan=CylinderApplyPlan.Build(snap,host,source,Request());Check(plan.Items.Count==4 && plan.Items.Any(i=>Convert.ToString(i["xml"]).Contains("<Name>Data</Name>")),"new typed data member is added when absent");
        // Real TIA exports place block Comment before the ordered CompileUnits and
        // block Title after them. Appending behind Title re-enters IOrdered and fails.
        string footerHost=Host().Replace("<ObjectList><SW.Blocks.CompileUnit ID='1'","<ObjectList><MultilingualText ID='4' CompositionName='Comment'/><SW.Blocks.CompileUnit ID='1'")
            .Replace("</SW.Blocks.CompileUnit></ObjectList></SW.Blocks.FB>","</SW.Blocks.CompileUnit><MultilingualText ID='5' CompositionName='Title'><ObjectList><MultilingualTextItem ID='6'><AttributeList><Text>Host title</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText></ObjectList></SW.Blocks.FB>");
        code[host.id]=footerHost;
        var orderedPlan=CylinderApplyPlan.Build(snap,host,source,Request());var orderedDoc=Parse(Convert.ToString(orderedPlan.Items.Last()["xml"]));
        var objectItems=orderedDoc.SelectSingleNode("/Document/SW.Blocks.FB/ObjectList").ChildNodes.OfType<XmlElement>().ToArray();
        Check(objectItems.Select(e=>e.LocalName=="SW.Blocks.CompileUnit"?"Network":e.GetAttribute("CompositionName")).SequenceEqual(new[]{"Comment","Network","Network","Title"}),"new network inserted before block title; ordered composition remains contiguous");
        var sourceItems=Parse(footerHost).SelectSingleNode("/Document/SW.Blocks.FB/ObjectList").ChildNodes.OfType<XmlElement>().ToArray();
        Check(objectItems[0].OuterXml==sourceItems[0].OuterXml && objectItems.Last().OuterXml==sourceItems.Last().OuterXml && objectItems[1].OuterXml==sourceItems[1].OuterXml,"block header/footer and original network remain unchanged");
        Check(orderedDoc.SelectNodes("//@ID").Cast<XmlAttribute>().Select(a=>a.Value).Distinct().Count()==orderedDoc.SelectNodes("//@ID").Count,"new network IDs also avoid trailing block-title IDs");
        var broken=Parse(footerHost);var stray=(XmlElement)broken.SelectSingleNode("//SW.Blocks.CompileUnit").CloneNode(true);
        stray.SelectSingleNode(".//CallInfo/Instance/Component").Attributes["Name"].Value="Z09";
        broken.SelectSingleNode("/Document/SW.Blocks.FB/ObjectList").AppendChild(stray);code[host.id]=broken.OuterXml;
        try {CylinderApplyPlan.Build(snap,host,source,Request());throw new Exception("interleaved source must fail preflight");}
        catch(InvalidOperationException ex) {Check(ex.Message.Contains("CompileUnits"),"interleaved source ordered-composition preflight gives explicit reason");}
        code[host.id]=Host();
    }
    static XmlDocument Parse(string xml) {var d=new XmlDocument();d.LoadXml(xml);return d;}
    static void CachedProject(string project,string plc,string output)
    {
        // Optional local integration check: reads already exported files only, never connects to TIA.
        var store=new ProjectIndexStore(project,"V17");var snap=new IoSnapshot {project_path=project,plc_name=plc};
        var tables=store.Select("plc.tags",plc,"","").ToArray();snap.tables_total=tables.Length;
        foreach(var table in tables) {snap.rows.AddRange(IoTable.Parse(store.IoSource(table),table.name,table.group,table.unit_name));snap.tables_read++;}
        snap.cylinderBlocks=store.IoBlocks(plc);var coverage=store.IoProgramCoverage(plc);snap.cylinderProgramsTotal=coverage[0];snap.cylinderProgramsRead=coverage[1];
        IoMapping.Expand(snap,snap.cylinderBlocks,store.IoSource);IoDeclarations.Add(snap,snap.cylinderBlocks,store.IoSource);
        var host=snap.cylinderBlocks.Single(b=>b.name=="OP275_Cylinder");
        var doc=Parse(store.IoSource(host));var info=doc.SelectNodes("//*[local-name()='CallInfo']").Cast<XmlElement>().First(c=>c.SelectSingleNode("*[local-name()='Instance']/*[local-name()='Component']").Attributes["Name"].Value=="Z03");
        var rules=new Dictionary<string,object>();var bindings=new Dictionary<string,object>();
        foreach(XmlElement pin in info.SelectNodes("*[local-name()='Parameter']")) {
            string name=pin.GetAttribute("Name");
            if(name=="i_Wpos" || name=="i_Hpos" || name=="oOutput_W" || name=="oOutput_H") {
                rules[name]=name.StartsWith("i_")?"input":"output";
                string address=name=="i_Wpos"?"I53.0":name=="i_Hpos"?"I53.1":name=="oOutput_W"?"Q53.1":"Q53.2";
                bindings[name]=snap.rows.First(r=>IoTable.Address(r.address)==address && r.data_type=="Bool").id;
            } else if(name.StartsWith("Timeout_"))rules[name]="index";
            else if(name.StartsWith("HMI") || name.StartsWith("i_Trigger_") || name=="TimeOut_Set")rules[name]="device";
            else rules[name]="shared";
        }
        var request=new Dictionary<string,object>{{"sample_id","Z03"},{"instance_name","Z04"},{"cylinder_name","OP275测试气缸"},{"alarm_index","4"},{"rules",rules},{"bindings",bindings}};
        var plan=CylinderApplyPlan.Build(snap,host,store.IoSource,request);
        Check(plan.Items.Count>=2,"cached actual project yields applicable XML plan");
        var candidate=Parse(Convert.ToString(plan.Items.Last()["xml"]));
        var oldUnits=doc.SelectNodes("//SW.Blocks.CompileUnit").Cast<XmlElement>().ToArray();var newUnits=candidate.SelectNodes("//SW.Blocks.CompileUnit").Cast<XmlElement>().ToArray();
        Check(newUnits.Length==oldUnits.Length+1 && oldUnits.Select(u=>u.OuterXml).SequenceEqual(newUnits.Take(oldUnits.Length).Select(u=>u.OuterXml)),"all real original networks remain unchanged");
        var generated=newUnits.Last();
        Check(generated.SelectSingleNode(".//*[local-name()='Instance']/*[local-name()='Component']/@Name").Value=="Z04","real new network calls Z04");
        Func<XmlElement,string,XmlElement> pinAccess=(unit,pin)=> {
            var wire=unit.SelectNodes(".//*[local-name()='Wire']").Cast<XmlElement>().Single(w=>w.SelectNodes("*[local-name()='NameCon']").Cast<XmlElement>().Any(n=>n.GetAttribute("Name")==pin));
            string id=wire.SelectSingleNode("*[local-name()='IdentCon']/@UId").Value;
            return unit.SelectNodes(".//*[local-name()='Access']").Cast<XmlElement>().Single(a=>a.GetAttribute("UId")==id);
        };
        Check(pinAccess(generated,"i_Wpos").InnerXml.Contains("OP275测试气缸伸出到位I53.0") && pinAccess(generated,"i_Hpos").InnerXml.Contains("OP275测试气缸缩回到位I53.1") &&
            pinAccess(generated,"oOutput_W").InnerXml.Contains("OP275测试气缸伸出阀Q53.1") && pinAccess(generated,"oOutput_H").InnerXml.Contains("OP275测试气缸缩回阀Q53.2"),"actual four new directional IO bindings");
        Check(pinAccess(generated,"i_Interlock_Wpos").InnerXml==pinAccess(oldUnits.Last(),"i_Interlock_Wpos").InnerXml && pinAccess(generated,"i_Interlock_Hpos").InnerXml==pinAccess(oldUnits.Last(),"i_Interlock_Hpos").InnerXml,"both real emergency-stop interlock conditions preserved");
        Check(candidate.SelectSingleNode("//*[local-name()='Section' and @Name='Static']/*[local-name()='Member' and @Name='Z04']//*[local-name()='Sections']")!=null,"real nested FB declaration retained for TIA readback");
        Directory.CreateDirectory(output);for(int i=0;i<plan.Items.Count;i++)File.WriteAllText(Path.Combine(output,"candidate-"+i+".xml"),Convert.ToString(plan.Items[i]["xml"]));
        Console.WriteLine("Read-only cached engineering fixture: "+plan.Items.Count+" planned blocks. No imports performed.");
    }
    static int Main(string[] args) {try {Synthetic();if(args.Length==3)CachedProject(args[0],args[1],args[2]);Console.WriteLine("PASS: "+count+" cylinder apply checks; no engineering writes.");return 0;}catch(Exception ex){Console.Error.WriteLine(ex);return 1;} }
}
