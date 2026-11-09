using System;
using System.Xml;
using System.Collections.Generic;
using TiaPortalSourceBridge.Web;
internal static class GraphMonitorChecks
{
 static int n;static void Check(bool ok,string m){if(!ok)throw new Exception(m);n++;}
 static GraphExpression Parse(string x){var d=new XmlDocument();d.LoadXml(x);return GraphMonitor.ParseCondition(d.DocumentElement);}
 static void Main(){try{
  string source="<Transition ProgrammingLanguage='LAD'><FlgNet><Parts><Access Scope='GlobalVariable' UId='1'><Symbol><Component Name='DB'/><Component Name='Ready'/></Symbol></Access><Part Name='Contact' UId='2'/><Part Name='TrCoil' UId='3'/></Parts><Wires><Wire><Powerrail/><NameCon UId='2' Name='in'/></Wire><Wire><IdentCon UId='1'/><NameCon UId='2' Name='operand'/></Wire><Wire><NameCon UId='2' Name='out'/><NameCon UId='3' Name='in'/></Wire></Wires></FlgNet></Transition>";
  var tree=Parse(source);Check(tree.op=="and"&&tree.children[1].symbol=="\"DB\".\"Ready\"","serial contact keeps exact symbol");
  tree=Parse(source.Replace("Name='Contact' UId='2'/>","Name='Contact' UId='2'><Negated Name='operand'/></Part>"));Check(tree.children[1].op=="not","normally closed contact inverted");
  Check(Parse(source.Replace("Name='Contact'","Name='PContact'")).op=="unknown","edge memory not treated as Boolean contact");
  Check(Parse(source.Replace("GlobalVariable","LocalVariable")).op=="unknown","local instance needs explicit runtime context");
  Check(Parse(source.Replace("<Component Name='Ready'/>","<Component Name='Ready'><Access/></Component>")).op=="unknown","array/structured access refused");
  Check(Parse(source.Replace("ProgrammingLanguage='LAD'","ProgrammingLanguage='SCL'")).op=="unknown","SCL not guessed");
  Check(Parse(source.Replace("<Powerrail/>","<OpenCon/>")).op=="unknown","open input not true");
  Check(Parse(source.Replace("<Powerrail/>","<NameCon UId='2' Name='out'/>")).op=="unknown","wire cycle rejected");
  Check(Parse(source.Replace("</Parts>","<Part Name='Contact' UId='99'/></Parts>")).op=="unknown","disconnected instruction not ignored");
  Check(Parse(source.Replace("UId='1'/><NameCon","UId='77'/><NameCon")).op=="unknown","dangling operand unknown");
  Check(Parse(source.Replace("UId='2'/>","UId='1'/>")).op=="unknown","duplicate definitions rejected");
  var rules=new Dictionary<string,object>{{"Start","output"},{"Run","input"},{"Permit","shared"}};var suffix=new Dictionary<string,object>{{"Start","启动输出"},{"Run","运行反馈"}};ActuatorNaming.Validate("motor",suffix,rules);Check(true,"generic Bool actuator explicit naming accepted");
  Action<Action> reject=work=>{bool bad=false;try{work();}catch{bad=true;}Check(bad,"invalid actuator naming rejected");};
  reject(()=>ActuatorNaming.Validate("motor",new Dictionary<string,object>(),rules));suffix["Run"]="I3.2";reject(()=>ActuatorNaming.Validate("motor",suffix,rules));suffix["Run"]="运行反馈";reject(()=>ActuatorNaming.Validate("cylinder",suffix,rules));reject(()=>ActuatorNaming.Validate("safety",suffix,rules));
  Console.WriteLine("PASS: "+n+" GRAPH Boolean subset and actuator naming checks");
 }catch(Exception e){Console.Error.WriteLine(e);Environment.ExitCode=1;}}
}
