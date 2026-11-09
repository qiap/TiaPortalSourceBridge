using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Collections.Generic;
using TiaPortalSourceBridge.Indexing;
using TiaPortalSourceBridge.Web;

internal static class SourceReviewChecks
{
    static int count;
    static void Check(bool value,string message){if(!value)throw new Exception(message);count++;}
    static void Reject(Action action,string message){bool failed=false;try{action();}catch(Exception){failed=true;}Check(failed,message);}
    static string Block(string kind,string name,string call="",string extra="")
    {
        return "<Document><SW.Blocks."+kind+"><AttributeList><Name>"+name+"</Name><ProgrammingLanguage>LAD</ProgrammingLanguage>"+extra+"</AttributeList><ObjectList>"+(call.Length==0?"":"<SW.Blocks.CompileUnit><AttributeList><NetworkSource><FlgNet><Parts><Call UId='1'><CallInfo Name='"+call+"' BlockType='FC'/></Call></Parts><Wires/></FlgNet></NetworkSource></AttributeList></SW.Blocks.CompileUnit>")+"</ObjectList></SW.Blocks."+kind+"></Document>";
    }
    static void Main(string[] args)
    {
        string root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);string project=Path.Combine(root,"Fixture.ap17");File.WriteAllText(project,"");
        var store=new ProjectIndexStore(project,"V17");Directory.CreateDirectory(store.Root);Directory.CreateDirectory(Path.Combine(store.Root,"source"));Directory.CreateDirectory(Path.Combine(store.Root,"content"));
        var sources=new Dictionary<string,string>{{"Main",Block("OB","Main","Middle")},{"Middle",Block("FC","Middle","Leaf")},{"Leaf",Block("FC","Leaf","","<Number>1</Number>")}};
        var entries=sources.Keys.Select(name=>IndexEntry.Create("plc.block","PLC","Group",name,"LAD")).ToList();
        Action save=()=>{foreach(var e in entries){e.content_hash=IndexJson.Hash(sources[e.name]);e.state="ready";File.WriteAllText(Path.Combine(store.Root,"source",e.content_hash+".xml"),sources[e.name]);File.WriteAllText(Path.Combine(store.Root,"content",e.content_hash+".json"),"{}");}File.WriteAllText(Path.Combine(store.Root,"catalog.json"),IndexJson.Serializer().Serialize(new IndexCatalog{project_file="Fixture.ap17",portal_version="V17",objects=entries}));};save();
        var before=SourceReview.Capture(root,project,"PLC","V17","Baseline");
        Check(before.entries.Count==3,"capture all ready blocks");
        Check(SourceReview.List(root,project,"PLC").Length==1,"persisted snapshot listed");
        Check(SourceReview.List(root,project,"Other").Length==0,"foreign PLC excluded");
        var loaded=SourceReview.Load(root,before.id,project,"PLC");Check(loaded.Sources.Count==3,"reopen durable source evidence");
        Check(SourceReview.Compare(before,loaded).objects.Count==0,"same snapshot");
        sources["Leaf"]=sources["Leaf"].Replace("<Number>1","<Number>2");save();
        var after=SourceReview.Capture(root,project,"PLC","V17","Changed");var report=SourceReview.Compare(before,after);
        Check(report.objects.Count==1&&report.objects[0].name=="Leaf","detect changed object");
        Check(report.impact.Count(i=>i.caller_name=="Middle")==2,"direct caller before and after");
        Check(report.impact.Count(i=>i.caller_name=="Main")==2,"transitive caller before and after");
        Check(report.impact.Where(i=>i.caller_name=="Main").All(i=>i.path.Count==3),"evidence path ordered leaf to root");
        Check(report.partial&&report.warnings.Any(w=>w.Contains("HMI")),"coverage limits disclosed");
        sources["Middle"]=Block("FC","Middle");save();var disconnected=SourceReview.Capture(root,project,"PLC","V17","Disconnected");
        report=SourceReview.Compare(before,disconnected);string leafId=before.entries.Single(e=>e.name=="Leaf").id;
        Check(report.impact.Any(i=>i.changed==leafId&&i.side=="before"&&i.caller_name=="Main"),"removed edge still has before impact");
        Check(!report.impact.Any(i=>i.changed==leafId&&i.side=="after"),"removed edge does not invent after path");
        sources["Middle"]=Block("FC","Middle","Leaf");sources["Leaf"]=Block("FC","Leaf","Middle");save();var cycle=SourceReview.Capture(root,project,"PLC","V17","Cycle");
        Check(SourceReview.Compare(before,cycle).impact.Count<20,"call cycles terminate");
        File.WriteAllText(Path.Combine(store.Root,"dirty.json"),IndexJson.Serializer().Serialize(new IndexDirty{names=new List<string>{"Leaf"}}));
        var partial=SourceReview.Capture(root,project,"PLC","V17","Partial");
        Check(partial.entries.Count==2&&partial.warnings.Count==1,"dirty source excluded with evidence");
        Check(SourceReview.Compare(before,partial).objects.Single(o=>o.name=="Leaf").type=="only_before","missing read is not deletion");
        File.Delete(Path.Combine(store.Root,"dirty.json"));
        sources["Middle"]=Block("FC","Middle","Leaf");sources["Leaf"]=sources["Leaf"].Replace("<Document>","<Document><DocumentInfo><Created>today</Created></DocumentInfo>");save();
        var noise=SourceReview.Capture(root,project,"PLC","V17","Noise");Check(SourceReview.Compare(cycle,noise).export_noise_only==1,"export envelope noise counted separately");
        Reject(()=>SourceReview.Load(root,"../outside",project,"PLC"),"path guard");
        Reject(()=>SourceReview.Load(root,before.id,project,"Other"),"foreign target guard");
        Reject(()=>SourceReview.Capture(root,project,"PLC","V17",""),"missing label");
        Reject(()=>SourceReview.Capture(root,project,"PLC","V17",new string('a',121)),"label bound");
        string sourceFile=Path.Combine(root,"source-reviews",before.id,before.entries[0].id+".xml");File.AppendAllText(sourceFile," ");
        Reject(()=>SourceReview.Load(root,before.id,project,"PLC"),"source tampering detected");
        File.WriteAllText(sourceFile,before.Sources[before.entries[0].id]);
        string manifest=Path.Combine(root,"source-reviews",before.id,"manifest.json"),original=File.ReadAllText(manifest);File.WriteAllText(manifest,original.Replace("Baseline","<script>bad()</script>"));
        var escaped=SourceReview.Load(root,before.id,project,"PLC");byte[] docs=SourceReview.Documentation(escaped);
        using(var zip=new ZipArchive(new MemoryStream(docs),ZipArchiveMode.Read)){
            Check(zip.GetEntry("manifest.json")!=null&&zip.GetEntry("call-graph.json")!=null&&zip.GetEntry("calls.mmd")!=null,"documentation artifacts");
            Check(zip.Entries.Count(e=>e.FullName.StartsWith("sources/"))==3,"documentation includes all snapshot sources");
            using(var reader=new StreamReader(zip.GetEntry("index.html").Open())){string html=reader.ReadToEnd();Check(html.Contains("&lt;script&gt;")&&!html.Contains("<script>"),"HTML text encoded");}
            using(var reader=new StreamReader(zip.GetEntry("calls.mmd").Open()))Check(reader.ReadToEnd().Contains(" --> "),"call diagram exported");
        }
        File.WriteAllText(manifest,original);Check(File.ReadAllText(project)=="","never changed engineering project");
        Console.WriteLine("PASS: "+count+" source archive, comparison, impact and documentation checks");
    }
}
