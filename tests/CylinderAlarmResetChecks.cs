using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using TiaPortalSourceBridge.Indexing;
using TiaPortalSourceBridge.Web;
internal static class CylinderAlarmResetChecks
{
    static int count;
    static string Xml(string value) {return System.Security.SecurityElement.Escape(value);}
    static string Access(string value)
    {
        if(!value.StartsWith("#"))return "<Access Scope='LiteralConstant'><Constant><ConstantValue>"+value+"</ConstantValue></Constant></Access>";
        var array=Regex.Match(value,@"^#(\w+)\.(\w+)\[([^\]]+)\]$");
        string symbol=array.Success?"<Component Name='"+array.Groups[1].Value+"'/><Token Text='.'/><Component Name='"+array.Groups[2].Value+"'><Token Text='['/>"+string.Join("<Token Text=','/>",array.Groups[3].Value.Split(',').Select(Access))+"<Token Text=']'/></Component>":"<Component Name='"+value.Substring(1)+"'/>";
        return "<Access Scope='LocalVariable'><Symbol>"+symbol+"</Symbol></Access>";
    }
    static string Source(string value)
    {return string.Join("",Regex.Matches(value,@"#\w+(?:\.\w+\[(?:#\w+|\d+)(?:,\d+)?\])?|:=|[;]|[A-Za-z_]+|\d+").Cast<Match>().Select(m=>m.Value.StartsWith("#") || Regex.IsMatch(m.Value,@"^\d+$") || m.Value=="FALSE" || m.Value=="TRUE"?Access(m.Value):"<Token Text='"+Xml(m.Value)+"'/>"));}
    static string Fields="<Member Name='Timeout' Datatype='Array[1..4,1..2] of Bool'/>";
    static string Callee(string source)
    {return "<Document><SW.Blocks.FC><AttributeList><Name>Aggregate</Name><Interface><Sections><Section Name='Input'><Member Name='Reset' Datatype='Bool'/></Section><Section Name='Output'><Member Name='Any' Datatype='Bool'/></Section><Section Name='InOut'><Member Name='Alarm' Datatype='Struct'>"+Fields+"</Member></Section><Section Name='Temp'><Member Name='i' Datatype='Int'/></Section></Sections></Interface></AttributeList><ObjectList><SW.Blocks.CompileUnit><AttributeList><NetworkSource><StructuredText>"+Source(source)+"</StructuredText></NetworkSource></AttributeList></SW.Blocks.CompileUnit></ObjectList></SW.Blocks.FC></Document>";}
    static void Check(bool actual,bool expected,string note){if(actual!=expected)throw new Exception(note);count++;}
    static int Main()
    {
        try {
            var db=IndexEntry.Create("plc.block","PLC","","Errors","DB","1");var caller=IndexEntry.Create("plc.block","PLC","","Caller","LAD","2");var fc=IndexEntry.Create("plc.block","PLC","","Aggregate","SCL","3");
            var blocks=new[]{db,caller,fc};var code=new Dictionary<string,string>();
            code[db.id]="<Document><SW.Blocks.GlobalDB><AttributeList><Interface><Sections><Section Name='Static'>"+Fields+"</Section></Sections></Interface></AttributeList></SW.Blocks.GlobalDB></Document>";
            code[caller.id]="<Document><SW.Blocks.FC><ObjectList><SW.Blocks.CompileUnit><AttributeList><NetworkSource><FlgNet><Parts><Access Scope='GlobalVariable' UId='1'><Symbol><Component Name='Errors'/></Symbol></Access><Call UId='2'><CallInfo Name='Aggregate' BlockType='FC'><Parameter Name='Alarm' Section='InOut' Type='Struct'/></CallInfo></Call></Parts><Wires><Wire><IdentCon UId='1'/><NameCon UId='2' Name='Alarm'/></Wire></Wires></FlgNet></NetworkSource></AttributeList></SW.Blocks.CompileUnit></ObjectList></SW.Blocks.FC></Document>";
            const string good="IF #Reset THEN FOR #i := 1 TO 4 BY 1 DO #Alarm.Timeout[#i,1] := FALSE; #Alarm.Timeout[#i,2] := FALSE; END_FOR; END_IF; FOR #i := 1 TO 4 BY 1 DO IF #Alarm.Timeout[#i,1] OR #Alarm.Timeout[#i,2] THEN #Any := TRUE; EXIT; END_IF; END_FOR;";
            Func<string,bool> run=s=>{code[fc.id]=Callee(s);return CylinderAlarmReset.IsSharedReset("Errors","Timeout",blocks,b=>code[b.id]);};
            Check(run(good),true,"verified aggregate reset accepted with arbitrary DB/function names");
            Check(run(good.Replace(":= FALSE",":= TRUE")),false,"nonreset writes rejected");
            Check(run(good.Replace("IF #Reset THEN","IF TRUE THEN")),false,"unconditional clear rejected");
            Check(run(good.Replace("TO 4","TO 3")),false,"partial range rejected");
            Check(run(good.Replace("#Alarm.Timeout[#i,2] := FALSE;","")),false,"unverified remaining column rejected");
            Check(run(good.Replace("#Alarm.Timeout[#i,1] := FALSE;","#Alarm.Timeout[1,1] := FALSE;")),false,"fixed element overwriter rejected");
            Check(run(good+"#Alarm.Timeout[#i,1] := FALSE;"),false,"unguarded write rejected");
            Check(run(good+"DangerousCall;"),false,"arbitrary called routine rejected");
            Check(run(good.Replace("IF #Reset THEN","IF #Any THEN")),false,"output flag is not a reset input");
            Check(run(good.Replace("BY 1","BY 2")),false,"nonunit step rejected");
            Check(run(good.Replace("#Alarm.Timeout[#i,2] := FALSE;","EXIT; #Alarm.Timeout[#i,2] := FALSE;")),false,"early exit in reset rejected");
            code[caller.id]=code[caller.id].Replace("Section='InOut'","Section='Output'");Check(run(good),false,"wholeDB output pin not reset transport");
            Console.WriteLine("PASS: "+count+" shared alarm reset checks.");return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
