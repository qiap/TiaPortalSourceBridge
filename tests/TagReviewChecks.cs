using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using TiaPortalSourceBridge.Web;
using TiaPortalSourceBridge.Mcp;
using TiaPortalSourceBridge.Indexing;
using TiaPortalSourceBridge.Openness;

internal static class TagReviewChecks
{
    static int count;
    static void Check(bool value,string text){if(!value)throw new Exception(text);count++;}
    static void Reject(Action action,string text){bool failed=false;try{action();}catch{failed=true;}Check(failed,text);}
    static string Tag(string name,string address,string type="Bool",string comment="") {return "<SW.Tags.PlcTag ID='2' CompositionName='Tags'><AttributeList><Name>"+name+"</Name><LogicalAddress>"+address+"</LogicalAddress><DataTypeName>"+type+"</DataTypeName></AttributeList>"+comment+"</SW.Tags.PlcTag>";}
    static string Table(string tags){return "<Document><SW.Tags.PlcTagTable ID='0'><AttributeList><Name>Tags</Name></AttributeList><ObjectList>"+tags+"</ObjectList></SW.Tags.PlcTagTable></Document>";}
    static SourceArchive Archive(string xml){var entry=IndexEntry.Create("plc.tags","PLC","","Tags","");return new SourceArchive{id=Guid.NewGuid().ToString("N"),project_path="E:/Fixture.ap17",plc_name="PLC",warnings=new List<string>(),entries=new List<IndexEntry>{entry},Sources=new Dictionary<string,string>{{entry.id,xml}}};}
    static IoReconcileReport Report(SourceArchive archive){return new IoReconcileReport{project_path=archive.project_path,plc_name=archive.plc_name,options=new IoReconcileOptions{scope="native",table="\nTags",type=2,comment=3},nativeTableSignatures=new Dictionary<string,string>{{"\n\nTags",TagTableXml.Signature(archive.Sources.Values.Single())}},rows=new List<IoReconcileRow>{new IoReconcileRow{excel_row=2,status="excel_only",name="NewPoint",address="I0.1",type="Bool",comment="新增点"}}};}
    static void Main(string[] args){try{Run(args);}catch(Exception ex){Console.Error.WriteLine(ex.GetType().Name+": "+ex.Message+" after "+count+" checks");Environment.ExitCode=1;}}
    static void Run(string[] args)
    {
        string root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
        var archive=Archive(Table(Tag("Existing","%I0.0")));var report=Report(archive);
        var plan=TagReconcilePlan.Build(report,archive,new[]{2},"zh-CN");
        var parsed=IoTable.Parse(plan.after_xml,"Tags","","").ToArray();Check(parsed.Length==2&&parsed.Single(p=>p.name=="NewPoint").singleLanguageComment=="新增点","new tag and selected comment language");
        Check(plan.dependencies.Count==1&&plan.diff.changes.Count>0,"preview and full table dependencies");
        Check(plan.before_xml==archive.Sources.Values.Single(),"original preserved");
        Reject(()=>TagReconcilePlan.Build(report,archive,new[]{2,2},"zh-CN"),"duplicate selection rejected");
        Reject(()=>TagReconcilePlan.Build(report,archive,new[]{3},"zh-CN"),"unknown row rejected");
        report.rows[0].address="I0.0";Reject(()=>TagReconcilePlan.Build(report,archive,new[]{2},"zh-CN"),"occupied bit rejected");
        report.rows[0].address="IB0";report.rows[0].type="Byte";Reject(()=>TagReconcilePlan.Build(report,archive,new[]{2},"zh-CN"),"byte overlap rejected");
        report.rows[0].address="I0.1";Reject(()=>TagReconcilePlan.Build(report,archive,new[]{2},"zh-CN"),"address type width rejected");report.rows[0].type="Bool";
        report.rows[0].name="existing";Reject(()=>TagReconcilePlan.Build(report,archive,new[]{2},"zh-CN"),"duplicate name case insensitive");report.rows[0].name="NewPoint";
        report.rows[0].status="conflict";Reject(()=>TagReconcilePlan.Build(report,archive,new[]{2},"zh-CN"),"conflict selection rejected");report.rows[0].status="excel_only";
        report.options.scope="mapped";Reject(()=>TagReconcilePlan.Build(report,archive,new[]{2},"zh-CN"),"mapping writes rejected");report.options.scope="native";
        report.options.table="";Reject(()=>TagReconcilePlan.Build(report,archive,new[]{2},"zh-CN"),"all table scope rejected");report.options.table="\nTags";
        var original=archive.Sources.Values.Single();archive.Sources[archive.entries[0].id]=original.Replace("Existing","Changed");Reject(()=>TagReconcilePlan.Build(report,archive,new[]{2},"zh-CN"),"stale report table hash rejected");archive.Sources[archive.entries[0].id]=original;
        var r=report.rows[0];r.status="different";r.name="Renamed";r.address="I0.0";r.plc.Add(new IoReconcilePoint{name="Existing",address="%I0.0",type="Bool",table="Tags",group="",unit=""});
        plan=TagReconcilePlan.Build(report,archive,new[]{2},"zh-CN");Check(IoTable.Parse(plan.after_xml,"Tags","","").Single().name=="Renamed","matched row renamed without duplicate");
        string multilingual="<ObjectList><MultilingualText ID='3' CompositionName='Comment'><ObjectList><MultilingualTextItem ID='4'><AttributeList><Culture>en-US</Culture><Text>Original</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText></ObjectList>";
        archive.Sources[archive.entries[0].id]=Table(Tag("Existing","%I0.0","Bool",multilingual));report.nativeTableSignatures["\n\nTags"]=TagTableXml.Signature(archive.Sources.Values.Single());
        plan=TagReconcilePlan.Build(report,archive,new[]{2},"zh-CN");Check(plan.after_xml.Contains("Original")&&plan.after_xml.Contains("zh-CN"),"other comment languages preserved");
        string project=Path.Combine(root,"Fixture.ap17");File.WriteAllText(project,"");
        string before=Table(Tag("Existing","%I0.0")),after=Table(Tag("Changed","%I0.0")),current=before;
        var deps=new[]{new TagTableDependency{table_name="Tags",group_path="",signature=TagTableXml.Signature(before)}};string json=CylinderWritePipeline.Json().Serialize(deps);
        int imports=0,saves=0,compiles=0;bool failCompile=false,failImport=false,mismatch=false;
        Func<string,Dictionary<string,object>> run=id=>TagTableTransaction.Run(root,id,project,"PLC","Tags","",after,json,()=>deps,(n,g)=>current,(x,g)=>{imports++;if(failImport)throw new Exception("import failed");if(!mismatch)current=x;return "backup";},()=>{compiles++;return new CompileSummary{State=failCompile?"Error":"Success",ErrorCount=failCompile?1:0};},()=>saves++);
        string first=Guid.NewGuid().ToString("N");var result=run(first);
        Check((string)result["state"]=="completed"&&imports==1&&saves==1&&compiles==1,"import compile verify save success");
        Check(File.Exists((string)result["backup"]),"backup created before mutation");Check(File.ReadAllText((string)result["backup"])==before,"backup contains old table");
        result=run(first);Check(imports==1&&saves==1&&(bool)result["saved"],"completed job never replayed");
        string oldJson=json;json=json.Replace(deps[0].signature,new string('a',64));Reject(()=>run(first),"operation id cannot be reused for another plan");json=oldJson;
        result=run(Guid.NewGuid().ToString("N"));Check((string)result["state"]=="failed"&&imports==1,"fresh stale plan rejected before mutation");
        current=before;failCompile=true;string failed=Guid.NewGuid().ToString("N");result=run(failed);
        Check((string)result["state"]=="needs_review"&&!(bool)result["saved"]&&saves==1,"compile failure preserves unsaved scene");
        run(failed);Check(imports==2,"failed job never replayed");
        current=before;failCompile=false;mismatch=true;result=run(Guid.NewGuid().ToString("N"));Check((string)result["state"]=="needs_review"&&saves==1,"readback mismatch blocks save");
        mismatch=false;failImport=true;result=run(Guid.NewGuid().ToString("N"));Check((string)result["state"]=="needs_review"&&saves==1,"import exception retains backup and unknown write result");
        Reject(()=>run("../bad"),"operation path guard");
        Check(File.ReadAllText(project)=="","offline test does not write engineering file");
        Console.WriteLine("PASS: "+count+" tag review plan and transaction checks");
    }
}
