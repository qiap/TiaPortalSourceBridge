using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using TiaPortalSourceBridge.Web;
using TiaPortalSourceBridge.Mcp;
using TiaPortalSourceBridge.Indexing;
using TiaPortalSourceBridge.Online;

internal static class SourceWriteChecks
{
    static int count;static void Check(bool b,string m){if(!b)throw new Exception(m);count++;}static void Reject(Action a,string m){bool b=false;try{a();}catch{b=true;}Check(b,m);}
    static Dictionary<string,object> D(params object[] values){var d=new Dictionary<string,object>();for(int i=0;i<values.Length;i+=2)d.Add((string)values[i],values[i+1]);return d;}
    static string Db(string initial,string comment="old"){return "<Document><SW.Blocks.GlobalDB ID='0'><AttributeList><Name>Process</Name><Number>10</Number><MemoryLayout>Standard</MemoryLayout><Interface><Sections><Section Name='Static'><Member Name='Value' Datatype='Int'><StartValue>"+initial+"</StartValue><Comment><MultiLanguageText Lang='en-US'>"+comment+"</MultiLanguageText></Comment></Member><Member Name='NewField' Datatype='Bool'><StartValue>false</StartValue></Member></Section></Sections></Interface></AttributeList></SW.Blocks.GlobalDB></Document>";}
    static void Main(string[] args){try{Run(args);}catch(Exception ex){Console.Error.WriteLine(ex.GetType().Name+": "+ex.Message+" after "+count);Environment.ExitCode=1;}}
    static void Run(string[] args)
    {
        string root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);string project=Path.Combine(root,"Fixture.ap17");File.WriteAllText(project,"fixture");string xml=Db("1");var entry=IndexEntry.Create("plc.block","PLC","","Process","DB");entry.content_hash=IndexJson.Hash(xml);
        var archive=new SourceArchive{id=Guid.NewGuid().ToString("N"),project_path=project,plc_name="PLC",entries=new List<IndexEntry>{entry},warnings=new List<string>(),Sources=new Dictionary<string,string>{{entry.id,xml}}};
        var profile=new OnlineProfile{Tags=new[]{new OnlineTag{Name="Process.Value",Address="DB10.DBW0",Type="Int"}}};var value=D("value",42,"quality","good","address","DB10.DBW0");var snapshot=D("project_path",project,"plc_name","PLC","profile",profile,"sample",D("complete",true,"values",D("Process.Value",value)));
        var preview=ParameterMigration.Preview(archive,snapshot);Check(preview.patches.Count==1&&preview.matched.Count==1,"parameter mapped to one DB member");Check(preview.patches[0].after_xml.Contains("INT#42")&&preview.patches[0].after_xml.Contains("<StartValue>false</StartValue>"),"typed value restored and new unrelated field unchanged");Check(archive.Sources[entry.id]==xml,"preview does not modify baseline");
        value["quality"]="bad";Reject(()=>ParameterMigration.Preview(archive,snapshot),"bad quality rejected");value["quality"]="good";
        value["value"]=40000;Reject(()=>ParameterMigration.Preview(archive,snapshot),"range overflow rejected");value["value"]=1.5;Reject(()=>ParameterMigration.Preview(archive,snapshot),"fractional integer rejected");value["value"]="INT#42";Reject(()=>ParameterMigration.Preview(archive,snapshot),"text is not evaluated as numeric expression");value["value"]=42;
        value["address"]="DB11.DBW0";Reject(()=>ParameterMigration.Preview(archive,snapshot),"address evidence cannot be changed");value["address"]="DB10.DBW0";
        profile.Tags[0].Type="Word";Check(ParameterMigration.Preview(archive,snapshot).unmatched.Count==1,"type change not coerced");profile.Tags[0].Type="Int";
        archive.Sources[entry.id]=xml.Replace("Standard","Optimized");Check(ParameterMigration.Preview(archive,snapshot).patches.Count==0,"optimized DB not migrated by absolute driver");archive.Sources[entry.id]=xml;
        snapshot["plc_name"]="Other";Reject(()=>ParameterMigration.Preview(archive,snapshot),"cross PLC archive rejected");snapshot["plc_name"]="PLC";
        var items=new[]{D("block_name","Process","group_path","","xml",Db("2","new"),"expected_recovery_signature",XmlBatch.RecoverySignature(xml))};string json=CylinderWritePipeline.Json().Serialize(items),current=xml;int imports=0,saves=0;bool compileError=false,mismatch=false,saveError=false;
        Func<string,Dictionary<string,object>> run=id=>ReviewedBlockWrite.Run(root,id,project,"PLC",json,(payload,operation)=>{imports++;if(!mismatch)current=Db("2","new");else current=Db("2","old");return new XmlBatchResult{ok=!compileError,compiled=true,error_count=compileError?1:0,error=compileError?"compile failed":null};},(name,group)=>current,()=>{if(saveError)throw new Exception("save failed");saves++;});
        string first=Guid.NewGuid().ToString("N");var result=run(first);Check((string)result["state"]=="completed"&&saves==1,"batch import and full verification before save");run(first);Check(imports==1&&saves==1,"completed write not replayed");
        string original=json;json=json.Replace("new","changed");Reject(()=>run(first),"operation id bound to plan fingerprint");json=original;
        mismatch=true;result=run(Guid.NewGuid().ToString("N"));Check((string)result["state"]=="needs_review"&&saves==1,"comment mismatch blocks save even when logic signature matches");mismatch=false;
        compileError=true;string failed=Guid.NewGuid().ToString("N");result=run(failed);Check((string)result["state"]=="needs_review"&&saves==1,"compile failure leaves scene for review");int before=imports;run(failed);Check(imports==before,"failed write not replayed");compileError=false;
        saveError=true;result=run(Guid.NewGuid().ToString("N"));Check((string)result["state"]=="needs_review"&&Equals(result["saved"],false),"save exception not labeled success");
        items[0].Remove("expected_recovery_signature");json=CylinderWritePipeline.Json().Serialize(items);Reject(()=>run(Guid.NewGuid().ToString("N")),"full before signature mandatory");
        Check(File.ReadAllText(project)=="fixture","offline tests do not change engineering files");Console.WriteLine("PASS: "+count+" parameter migration and reviewed source write checks");
    }
}
