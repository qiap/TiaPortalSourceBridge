using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.IO.Compression;
using TiaPortalSourceBridge.Web;
using TiaPortalSourceBridge.Indexing;

internal static class ProjectTextsChecks
{
    static int count;static void Check(bool b,string m){if(!b)throw new Exception(m);count++;}static void Reject(Action a,string m){bool b=false;try{a();}catch{b=true;}Check(b,m);}
    static byte[] Workbook(List<string[]> rows){return IoWorkbook.CreateSheets(new[]{"Translations"},new IEnumerable<string[]>[]{rows});}
    static void Main(string[] args){try{Run();}catch(Exception ex){Console.Error.WriteLine(ex.GetType().Name+": "+ex.Message);Environment.ExitCode=1;}}
    static void Run()
    {
        string xml="<Document><SW.Blocks.GlobalDB ID='0'><AttributeList><Name>Data</Name><Number>1</Number><Interface><Sections><Section Name='Static'><Member Name='A' Datatype='Bool'><Comment><MultiLanguageText Lang='zh-CN'>夹紧</MultiLanguageText><MultiLanguageText Lang='en-US'>Clamp</MultiLanguageText></Comment></Member></Section></Sections></Interface></AttributeList><ObjectList><MultilingualText ID='1' CompositionName='Title'><ObjectList><MultilingualTextItem ID='2'><AttributeList><Culture>en-US</Culture><Text>Title</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText></ObjectList></SW.Blocks.GlobalDB></Document>";
        var e=IndexEntry.Create("plc.block","PLC","","Data","DB");e.content_hash=IndexJson.Hash(xml);var archive=new SourceArchive{id=Guid.NewGuid().ToString("N"),project_path="E:/Fixture.ap17",plc_name="PLC",warnings=new List<string>(),entries=new List<IndexEntry>{e},Sources=new Dictionary<string,string>{{e.id,xml}}};
        var texts=ProjectTexts.Read(archive);Check(texts.Count==3,"interface and block texts extracted");Check(texts.Select(t=>t.resource_id).Distinct().Count()==3,"stable unique resource keys");
        byte[] original=ProjectTexts.Workbook(archive);Check(ProjectTexts.Preview(archive,original,"translations.xlsx").Count==0,"unchanged workbook makes no changes");
        var sheet=IoSheetImport.Load(original,"translations.xlsx").Sheets[0];var rows=sheet.Rows.Select(r=>Enumerable.Range(0,10).Select(i=>sheet.Cell(r,i).Value).ToArray()).ToList();rows[1][8]="=Translated <img>";rows[1][9]="update";
        var patch=ProjectTexts.Preview(archive,Workbook(rows),"translations.xlsx").Single();Check(patch.after_xml.Contains("=Translated &lt;img&gt;")&&patch.after_xml.Contains("Clamp"),"translated value escaped and other language unchanged");Check(patch.before_xml==xml&&archive.Sources[e.id]==xml,"preview leaves snapshot intact");Check(patch.diff.status=="structural"&&patch.diff.changes.Count>0,"structural preview includes text changes");
        rows[1][8]="";patch=ProjectTexts.Preview(archive,Workbook(rows),"translations.xlsx").Single();Check(!patch.after_xml.Contains("夹紧"),"explicit update can clear a translation");
        rows[1][7]="different original";Reject(()=>ProjectTexts.Preview(archive,Workbook(rows),"translations.xlsx"),"old text guard");rows[1][7]="夹紧";
        rows[1][6]="de-DE";Reject(()=>ProjectTexts.Preview(archive,Workbook(rows),"translations.xlsx"),"cannot silently change language");rows[1][6]="zh-CN";
        rows.Add((string[])rows[1].Clone());Reject(()=>ProjectTexts.Preview(archive,Workbook(rows),"translations.xlsx"),"duplicate updates rejected");rows.RemoveAt(rows.Count-1);
        rows[1][0]="other";Reject(()=>ProjectTexts.Preview(archive,Workbook(rows),"translations.xlsx"),"cross snapshot update rejected");rows[1][0]=archive.id;
        rows[1][3]="tampered";Reject(()=>ProjectTexts.Preview(archive,Workbook(rows),"translations.xlsx"),"source hash guard");rows[1][3]=e.content_hash;
        rows[0][0]="wrong";Reject(()=>ProjectTexts.Preview(archive,Workbook(rows),"translations.xlsx"),"header guard");
        Reject(()=>ProjectTexts.Parse("<!DOCTYPE x [<!ENTITY y SYSTEM 'file:///x'>]><Document>&y;</Document>"),"DTD rejected");
        using(var zip=new ZipArchive(new MemoryStream(SourceReview.Documentation(archive)),ZipArchiveMode.Read)){Check(zip.GetEntry("Translations.xlsx")!=null&&zip.GetEntry("IO-and-references.xlsx")!=null&&zip.GetEntry("coverage.txt")!=null,"delivery bundle includes text workbook and IO references");}
        Console.WriteLine("PASS: "+count+" translation roundtrip and delivery document checks");
    }
}
