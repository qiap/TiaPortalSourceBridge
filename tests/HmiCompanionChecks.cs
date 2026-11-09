using System;
using System.Linq;
using TiaPortalSourceBridge.Mcp;
using TiaPortalSourceBridge.Web;
internal static class HmiCompanionChecks
{
 static int n;static void Check(bool ok,string why){if(!ok)throw new Exception(why);n++;}static void Reject(Action f){bool bad=false;try{f();}catch{bad=true;}Check(bad,"invalid or ambiguous HMI mapping rejected");}
 static void Main(){try{
  var alarm=new HmiDeviceAlarm{id=100,template_id=1,trigger_tag="Alarms",trigger_bit=3,text="{name} / {instance} / {index}",operation="create"};var row=new HmiDeviceRow{name="夹紧",instance="Z04",index=4,alarms=new[]{alarm}};var config=new HmiCompanionConfiguration{culture="zh-CN",text_list="Devices",devices=new[]{row}};
  string changes=HmiCompanionPlan.Changes(config);Check(changes.Contains("夹紧 / Z04 / 4")&&changes.Contains("\"trigger_bit\":3")&&changes.Contains("\"op\":\"create\""),"explicit identity, translated label and trigger preserved");
  string xml="<Document><Engineering version='V17'/><Hmi.TextGraphicList.TextList ID='0'><AttributeList><Name>Devices</Name></AttributeList><ObjectList><Hmi.TextGraphicList.TextListEntry ID='1'><AttributeList><Value>4</Value></AttributeList><ObjectList><MultilingualText CompositionName='Text'><ObjectList><MultilingualTextItem><AttributeList><Culture>zh-CN</Culture><Text>备用</Text></AttributeList></MultilingualTextItem><MultilingualTextItem><AttributeList><Culture>en-US</Culture><Text>Spare</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText></ObjectList></Hmi.TextGraphicList.TextListEntry></ObjectList></Hmi.TextGraphicList.TextList></Document>";
  string updated=HmiCompanionPlan.TextList(xml,config);Check(updated.Contains("夹紧")&&updated.Contains("Spare")&&!updated.Contains("备用"),"only explicit existing language and index updated");
  Reject(()=>HmiCompanionPlan.TextList(xml.Replace("<Value>4</Value>","<Value>4-8</Value>"),config));
  Reject(()=>HmiCompanionPlan.TextList(xml.Replace("zh-CN","de-DE"),config));
  Reject(()=>HmiCompanionPlan.TextList(xml.Replace("备用","&lt;field ref='0'/&gt;"),config));
  config.devices=new[]{row,row};Reject(()=>HmiCompanionPlan.Validate(config));config.devices=new[]{row};
  alarm.trigger_bit=16;Reject(()=>HmiCompanionPlan.Validate(config));alarm.trigger_bit=3;alarm.id=0;Reject(()=>HmiCompanionPlan.Validate(config));alarm.id=100;
  alarm.operation="upsert";Reject(()=>HmiCompanionPlan.Validate(config));alarm.operation="create";
  config.culture="zh";Reject(()=>HmiCompanionPlan.Validate(config));config.culture="zh-CN";
  config.text_list="Other";Reject(()=>HmiCompanionPlan.TextList(xml,config));
  Console.WriteLine("PASS: "+n+" explicit Classic HMI companion planning checks; no engineering calls");
 }catch(Exception ex){Console.Error.WriteLine(ex);Environment.ExitCode=1;}}
}
