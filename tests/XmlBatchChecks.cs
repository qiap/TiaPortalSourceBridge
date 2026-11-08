using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Web.Script.Serialization;
using TiaPortalSourceBridge.Mcp;
using TiaPortalSourceBridge.Openness;

internal static class XmlBatchChecks
{
    internal static int Run(string fixture)
    {
        int count = 0;
        Action<bool,string> check = (ok,msg) => { if(!ok) throw new Exception(msg); count++; };
        foreach (string fail in new[] { "", "preflight", "backup", "import", "compile", "verify", "different", "compile_errors" })
        {
            var log = new List<string>();
            var items = new[] { new XmlBatchItem { block_name="A", status="pending" }, new XmlBatchItem { block_name="B", status="pending" }, new XmlBatchItem { block_name="C", status="pending" } };
            Action<string,XmlBatchItem> stage = (phase,item) => { log.Add(phase+item.block_name); if (fail==phase && item.block_name=="B") throw new Exception("injected"); };
            var result = XmlBatch.Run(items,
                i=>stage("preflight",i), i=>{stage("backup",i);i.backup="backup-"+i.block_name;}, i=>stage("import",i),
                ()=>{log.Add("compile");if(fail=="compile")throw new Exception("injected");return new CompileSummary { ErrorCount=fail=="compile_errors"?1:0 };},
                i=>{stage("verify",i);i.verification=fail=="different"?"different":"matched";},
                r=>{log.Add("phase:"+r.phase);if(r.phase=="verify")log.Add("readback:"+r.current_block+":"+string.Join(",",r.items.Select(i=>i.verification)));});
            check(result.ok==(fail==""), "failure must not report success: "+fail);
            check(log.IndexOf("phase:preflight") < log.IndexOf("preflightA"), "preflight announced before work");
            check(log.Count(x=>x=="compile") <= 1,"one compile maximum");
            if(fail=="preflight" || fail=="backup")
                check(!log.Any(x=>x.StartsWith("import") || x=="compile"),"no write/compile before all backups");
            else
            {
                check(log.IndexOf("backupC") < log.IndexOf("importA"),"all backups before imports");
                check(log.Count(x=>x=="compile")==1,"one final compile");
                check(log.IndexOf("phase:compile") < log.IndexOf("compile"),"compile progress visible while compile runs");
                check(log.IndexOf("phase:verify") < log.IndexOf("verifyA"),"readback phase visible before readback runs");
                check(log.Any(x=>x.StartsWith("readback:A:") && x.Contains(fail=="different"?"different":"matched")),"per-block readback outcome persisted");
            }
            if(fail=="import")
            {
                check(!log.Contains("importC"),"stop after failure");
                check(items[1].status=="outcome_unknown" && items[2].status=="backed_up", "accurate partial result");
                check(log.Contains("verifyB"),"read back ambiguous import outcome");
            }
        }
        string journalRoot=Path.Combine(Path.GetTempPath(),"tia-batch-journal-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(journalRoot);
        try {
            string operation=Guid.NewGuid().ToString("N");
            string first=XmlBatch.CreateJournalDirectory(journalRoot,operation);
            check(first==Path.Combine(journalRoot,"batch-operation-"+operation),"operation id locates predictable journal directory");
            File.WriteAllText(Path.Combine(first,"result.json"),"original");
            bool duplicateRejected=false;
            try{XmlBatch.CreateJournalDirectory(journalRoot,operation);}catch(InvalidOperationException){duplicateRejected=true;}
            check(duplicateRejected && File.ReadAllText(Path.Combine(first,"result.json"))=="original","duplicate operation cannot overwrite evidence");
            check(XmlBatch.CreateJournalDirectory(journalRoot,null)!=XmlBatch.CreateJournalDirectory(journalRoot,null),"ordinary batches retain independent directories");
            bool invalidIdRejected=false;try{XmlBatch.CreateJournalDirectory(journalRoot,"../invalid");}catch(ArgumentException){invalidIdRejected=true;}
            check(invalidIdRejected,"invalid operation id cannot determine journal path");
            string concurrent=Guid.NewGuid().ToString("N");int winners=0;Exception unexpected=null;
            using(var ready=new ManualResetEvent(false)) {
                var threads=Enumerable.Range(0,8).Select(_=>new Thread(()=>{ready.WaitOne();try{XmlBatch.CreateJournalDirectory(journalRoot,concurrent);Interlocked.Increment(ref winners);}catch(InvalidOperationException){}catch(IOException){}catch(Exception ex){unexpected=ex;}})).ToArray();
                foreach(var thread in threads)thread.Start();ready.Set();foreach(var thread in threads)thread.Join();
            }
            check(winners==1 && unexpected==null,"atomic manifest claim permits only one concurrent owner");
        } finally {
            if(Path.GetFullPath(journalRoot).StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase))Directory.Delete(journalRoot,true);
        }
        string minimal="<Document><SW.Blocks.FC><AttributeList><Name>A</Name><Number>1</Number><ProgrammingLanguage>SCL</ProgrammingLanguage></AttributeList></SW.Blocks.FC></Document>";
        var serializer=new JavaScriptSerializer();
        check(XmlBatch.Parse(serializer.Serialize(new[]{new{xml=minimal}})).Length==1,"parse XML batch");
        var importItems = XmlBatch.Parse(serializer.Serialize(new[]{new{xml=minimal}}));
        string relatedXml = minimal.Replace("<Name>A</Name>", "<Name>Related</Name>");
        string relatedSignature = XmlBatch.RecoverySignature(relatedXml);
        string relatedJson = serializer.Serialize(new[]{new{block_name="Related",group_path="Group",expected_recovery_signature=relatedSignature}});
        var related = XmlBatch.ParseRelated(relatedJson, importItems);
        check(related.Length==1 && related[0].status=="pending", "parse unique related existing block evidence");
        XmlBatch.ValidateRelatedBefore(related[0], relatedXml);
        check(related[0].status=="validated" && related[0].before_signature==relatedSignature, "related FC supports read-only preflight evidence");
        check(XmlBatch.ParseRelated(null, importItems).Length==0 && XmlBatch.ParseRelated("[]", importItems).Length==0, "related evidence is optional");
        foreach (string badRelated in new[]{"null", "{}", "[null]", serializer.Serialize(new[]{new{block_name="A",expected_recovery_signature=relatedSignature}}),
            serializer.Serialize(new[]{new{block_name="Related",expected_recovery_signature=relatedSignature},new{block_name="related",expected_recovery_signature=relatedSignature}}),
            serializer.Serialize(new[]{new{block_name="Related",expected_recovery_signature=""}}), serializer.Serialize(new[]{new{block_name="Related",expected_recovery_signature=new string('Z',64)}})}) {
            bool rejected=false; try { XmlBatch.ParseRelated(badRelated,importItems); } catch(ArgumentException) {rejected=true;}
            check(rejected,"reject duplicate, overlapping or malformed related evidence");
        }
        string titledRelated = relatedXml.Replace("</SW.Blocks.FC>", "<ObjectList><MultilingualText CompositionName='Title'><ObjectList><MultilingualTextItem><AttributeList><Culture>zh-CN</Culture><Text>原始标题</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText></ObjectList></SW.Blocks.FC>");
        var titledItem = XmlBatch.ParseRelated(serializer.Serialize(new[]{new{block_name="Related",expected_recovery_signature=XmlBatch.RecoverySignature(titledRelated)}}),importItems)[0];
        bool titleRejected=false;
        try { XmlBatch.ValidateRelatedBefore(titledItem,titledRelated.Replace("原始标题","后续人工修改")); } catch(InvalidOperationException) {titleRejected=true;}
        check(titleRejected, "related preflight detects later title/comment edits before mutation");
        bool identityRejected=false;
        try { XmlBatch.ValidateRelatedBefore(related[0], minimal); } catch(InvalidOperationException) {identityRejected=true;}
        check(identityRejected,"related readback identity must match requested object");
        string compileUnit = "<SW.Blocks.CompileUnit CompositionName='CompileUnits'><ObjectList><MultilingualText CompositionName='Title'/></ObjectList></SW.Blocks.CompileUnit>";
        string title = "<MultilingualText CompositionName='Title'/>";
        string contiguous = minimal.Replace("</SW.Blocks.FC>", "<ObjectList><MultilingualText CompositionName='Comment'/>" + compileUnit + "<!-- separator -->" + compileUnit + title + "</ObjectList></SW.Blocks.FC>");
        check(XmlBatch.Parse(serializer.Serialize(new[]{new{xml=contiguous}})).Length==1,"contiguous compile units accept footer title and nested network titles");
        string split = minimal.Replace("<Name>A</Name>","<Name>B</Name>").Replace("</SW.Blocks.FC>","<ObjectList>"+compileUnit+title+compileUnit+"</ObjectList></SW.Blocks.FC>");
        bool splitRejected = false, mutated = false;
        try {
            var parsed = XmlBatch.Parse(serializer.Serialize(new[]{new{xml=minimal},new{xml=split}}));
            XmlBatch.Run(parsed, i=>{}, i=>{mutated=true;}, i=>{mutated=true;}, ()=>new CompileSummary(), i=>{}, r=>{});
        } catch(InvalidOperationException ex) { splitRejected=ex.Message.Contains("CompileUnits"); }
        check(splitRejected && !mutated,"reject separated CompileUnits in any batch item before all backups and writes");
        splitRejected=false;
        try { BlockOperations.ValidateCompileUnitOrder(BlockOperations.ParseBlock(split)); } catch(InvalidOperationException) { splitRejected=true; }
        check(splitRejected,"direct XML import rejects separated CompileUnits before engine operations");
        foreach(string invalid in new[]{"[]", "[null]", serializer.Serialize(new[]{new{xml=minimal},new{xml=minimal}})})
        {
            bool rejected=false;try{XmlBatch.Parse(invalid);}catch{rejected=true;}check(rejected,"reject invalid/duplicate batch");
        }
        check(XmlBatch.ContentSignature(minimal)!=XmlBatch.ContentSignature(minimal.Replace("<Number>1", "<Number>2")),"block identity differences remain visible");
        string network="<Document><SW.Blocks.FC><AttributeList><Name>A</Name></AttributeList><ObjectList><SW.Blocks.CompileUnit><AttributeList><NetworkSource><FlgNet><Parts><Part UId='1' Name='Contact'/></Parts><Wires><Wire UId='3'><NameCon UId='1' Name='in'/></Wire></Wires></FlgNet></NetworkSource></AttributeList></SW.Blocks.CompileUnit></ObjectList></SW.Blocks.FC></Document>";
        check(XmlBatch.ContentSignature(network)!=XmlBatch.ContentSignature(network.Replace("NameCon UId='1'", "NameCon UId='2'")),"rewiring must be detected");
        check(XmlBatch.ContentSignature(network)==XmlBatch.ContentSignature(network.Replace("UId='1'", "UId='21'").Replace("UId='3'", "UId='24'")),"consistent wire ID renumbering is harmless");
        check(XmlBatch.ContentSignature(network)!=XmlBatch.ContentSignature(network.Replace("Name='Contact'", "Name='Coil'")),"instruction changes remain visible");
        string lineXml = minimal.Replace("<Name>A</Name>", "<Name>A</Name><Token Text='\n'/>");
        check(((System.Xml.XmlElement)BlockOperations.ParseBlock(lineXml).SelectSingleNode("//Token")).GetAttribute("Text")=="\n","full block parser preserves literal action newlines");
        bool dtdRejected=false;try{BlockOperations.ParseBlock("<!DOCTYPE Document [<!ENTITY x 'x'>]>"+minimal);}catch{dtdRejected=true;}check(dtdRejected,"DTD remains prohibited");
        if(!string.IsNullOrEmpty(fixture))
        {
            string root=Directory.GetParent(Path.GetDirectoryName(fixture)).FullName;
            foreach(string name in new[]{"DB_OP275Product","OP275_ProductInfo","OP275_BranchControl","OP275_Alarm","OP275_自动流程"})
            {
                string candidate=File.ReadAllText(Path.Combine(root,"candidate",name+".xml"));
                string readback=File.ReadAllText(Path.Combine(root,"apply",name+".after.xml"));
                var differences = XmlBatch.Differences(candidate, readback);
                if (name == "OP275_自动流程")
                    check(differences.Length == 1 && differences[0] == "Interface[0]", "GRAPH source matches; generated interface changes remain visible: " + string.Join(",",differences));
                else
                    check(differences.Length == 0,"actual V17 readback matches "+name);
            }
        }
        return count;
    }
}
