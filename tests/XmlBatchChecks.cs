using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
                i=>{stage("verify",i);i.verification=fail=="different"?"different":"matched";}, r=>{});
            check(result.ok==(fail==""), "failure must not report success: "+fail);
            check(log.Count(x=>x=="compile") <= 1,"one compile maximum");
            if(fail=="preflight" || fail=="backup")
                check(!log.Any(x=>x.StartsWith("import") || x=="compile"),"no write/compile before all backups");
            else
            {
                check(log.IndexOf("backupC") < log.IndexOf("importA"),"all backups before imports");
                check(log.Count(x=>x=="compile")==1,"one final compile");
            }
            if(fail=="import")
            {
                check(!log.Contains("importC"),"stop after failure");
                check(items[1].status=="outcome_unknown" && items[2].status=="backed_up", "accurate partial result");
                check(log.Contains("verifyB"),"read back ambiguous import outcome");
            }
        }
        string minimal="<Document><SW.Blocks.FC><AttributeList><Name>A</Name><Number>1</Number><ProgrammingLanguage>SCL</ProgrammingLanguage></AttributeList></SW.Blocks.FC></Document>";
        var serializer=new JavaScriptSerializer();
        check(XmlBatch.Parse(serializer.Serialize(new[]{new{xml=minimal}})).Length==1,"parse XML batch");
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
