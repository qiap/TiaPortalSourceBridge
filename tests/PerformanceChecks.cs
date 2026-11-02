using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using TiaPortalSourceBridge.Indexing;
using TiaPortalSourceBridge.Online;
using TiaPortalSourceBridge.Openness;
using TiaPortalSourceBridge.Mcp;
using System.Xml;
using System.Web.Script.Serialization;

internal static class PerformanceChecks
{
    static int checks;
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
    static object Call(object target, string name, params object[] args)
    { return target.GetType().GetMethod(name, BindingFlags.Instance|BindingFlags.NonPublic).Invoke(target,args); }
    static void Set(object target, string name, object value)
    { target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).SetValue(target,value); }
    static double Median(Action action, int repeats)
    {
        action(); var times=new List<double>();
        for(int i=0;i<repeats;i++){var sw=Stopwatch.StartNew();action();times.Add(sw.Elapsed.TotalMilliseconds);}
        return times.OrderBy(x=>x).ElementAt(times.Count/2);
    }
    static Dictionary<string,object> Map(object value){return IndexJson.Map(value);}
    static int Number(Dictionary<string,object> map,string key){return Convert.ToInt32(map[key]);}
    public static int Main(string[] args)
    {
        string root=Path.Combine(Path.GetTempPath(),"tia-performance-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            bool optimized=args.Length==0||args[0]!="baseline";
            if (optimized) checks += XmlBatchChecks.Run(args.Length > 1 ? args[1] : null);
            if (optimized && args.Length > 1)
            {
                string graphXml = File.ReadAllText(args[1]);
                foreach (string operation in new[] { "replace_xml", "append_xml", "insert_before", "insert_after" })
                foreach (string terminator in new[] { "\n", "&#xA;" })
                {
                    string xpath = "(//g:Step/g:Actions/g:Action)[1]";
                    if (operation == "append_xml") xpath = "(//g:Step/g:Actions)[1]";
                    string patch = new JavaScriptSerializer().Serialize(new[] { new { op=operation, xpath=xpath,
                        xml="<Action><Token Text=\"RegressionMarker\"/><Token Text=\"" + terminator + "\"/></Action>" } });
                    var patched = new XmlDocument();
                    patched.LoadXml(GraphEditor.Patch(graphXml, patch, "", ""));
                    var token = (XmlElement)patched.SelectSingleNode("//*[local-name()='Action']/*[@Text='RegressionMarker']/following-sibling::*[1]");
                    Check(token != null && token.GetAttribute("Text") == "\n", operation + " preserves GRAPH action newline " + terminator);
                }
            }
            var store=new ProjectIndexStore(Path.Combine(root,"test.ap17"),"V17");
            store.BuildCatalog(w=>Enumerable.Range(0,1000).Select(i=>IndexEntry.Create("plc.block","PLC","","Block"+i,"SCL")).ToList());
            var catalog=store.Load(); string hash=new string('a',64);
            Directory.CreateDirectory(Path.Combine(store.Root,"content"));
            File.WriteAllText(Path.Combine(store.Root,"content",hash+".json"),IndexJson.Serializer().Serialize(new IndexContent {hash=hash}));
            foreach(var e in catalog.objects)
            {e.state="ready";e.content_hash=IndexJson.Hash(e.id);File.WriteAllText(Path.Combine(store.Root,"content",e.content_hash+".json"),IndexJson.Serializer().Serialize(new IndexContent {hash=e.content_hash}));}
            store.MarkDirty(new[]{"bLoCk0"},"test");
            var status=Map(store.Status());Check(Number(status,"ready")==999,"case insensitive dirty state");Check(Number(status,"pending")==1,"pending count");
            var metricsResult=new Dictionary<string,object>();
            using(var metrics=OperationMetrics.Begin()){store.Tree("","","","",0,25);metrics.Attach(metricsResult);}
            if(optimized)
            {
                var meta=(Dictionary<string,object>)metricsResult["_meta"];
                var perf=(Dictionary<string,object>)meta["performance"];
                var counts=(Dictionary<string,long>)perf["counts"];
                Check(counts["index_dirty_reads"]==1,"one dirty read per tree query");
                Check(counts["index_content_file_checks"]==999,"one file check per clean object across status and locators");
            }
            double indexMs=Median(()=>store.Status(),7);
            store.MarkDirty(new[]{"Block1"},"external mutation");
            Check(Number(Map(store.Status()),"ready")==998,"next request sees dirty changes");
            File.Delete(Path.Combine(store.Root,"content",catalog.objects[2].content_hash+".json"));
            Check(Number(Map(store.Status()),"ready")==997,"next request sees deleted content");
            store.MarkDirty(null,"all");Check(Number(Map(store.Status()),"pending")==1000,"whole catalog invalidation");
            Check(Map(store.Context("Block",2,2)).ContainsKey("objects"),"context query works");
            Check(Number(Map(store.References("Unknown",0,10)),"total_matches")==0,"reference query works");
            if(optimized)
            {
                string apiDir=Path.Combine(root,"PublicAPI","V17");Directory.CreateDirectory(apiDir);
                string apiFile=Path.Combine(apiDir,"Siemens.Engineering.dll");File.WriteAllText(apiFile,"");
                var has=typeof(ReflectionApi).GetMethod("HasExactCandidate",BindingFlags.Static|BindingFlags.NonPublic);
                Check((bool)has.Invoke(null,new object[]{new[]{apiFile},"V17"}),"exact API version accepted");
                Check(!(bool)has.Invoke(null,new object[]{new[]{apiFile},"V20"}),"wrong API version not accepted as fast path");
                File.Delete(apiFile);
                Check(!(bool)has.Invoke(null,new object[]{new[]{apiFile},"V17"}),"missing API not accepted");
            }
            var transport=new FakeTransport();var online=new OnlineTools(root);
            Set(online,"profile",new OnlineProfile {Tags=new[]{new OnlineTag{Name="word",Address="DB1.DBW0",Type="Word"},new OnlineTag{Name="far",Address="DB1.DBB200",Type="Byte"},new OnlineTag{Name="other",Address="DB2.DBB0",Type="Byte"}}});
            Set(online,"connection",new S7Connection(transport));
            string[] names={"other","far","word","DB1.DBX1.0"};
            var first=Map(Call(online,"Read",(object)names));
            Check((bool)first["complete"],"sample complete");Check(transport.Reads==3,"200 byte and DB grouping preserved");
            var values=(Dictionary<string,object>)first["values"];
            Check(Number((Dictionary<string,object>)values["word"],"value")==1,"word decoding");
            Check((bool)((Dictionary<string,object>)values["DB1.DBX1.0"])["value"],"bit decoding");
            if(optimized)
            {
                object p1=Call(online,"Plan",(object)names);object p2=Call(online,"Plan",(object)(string[])names.Clone());
                Check(object.ReferenceEquals(p1,p2),"plan reused");
                Check(!object.ReferenceEquals(p1,Call(online,"Plan",(object)new[]{"WORD"})),"requested spelling preserved");
                for(int i=0;i<100;i++)Call(online,"Plan",(object)new[]{"DB1.DBB"+i});
                var cache=(System.Collections.IDictionary)online.GetType().GetField("readPlans",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(online);
                Check(cache.Count<=32,"bounded plan cache");
                Call(online,"Disconnect");Check(cache.Count==0,"disconnect clears plans");
                Set(online,"profile",new OnlineProfile{Tags=new[]{new OnlineTag{Name="word",Address="DB1.DBW2",Type="Word"}}});
                Set(online,"connection",new S7Connection(transport));
                var changed=Map(Call(online,"Read",(object)new[]{"word"}));
                Check(Number((Dictionary<string,object>)((Dictionary<string,object>)changed["values"])["word"],"value")==515,"new profile does not reuse stale address");
            }
            string[] batch=Enumerable.Range(0,128).Select(i=>"DB1.DBB"+i).ToArray();
            double sampleMs=Median(()=>{for(int i=0;i<100;i++)Call(online,"Read",(object)batch);},7);
            transport.FailItem=true;
            Check(!(bool)Map(Call(online,"Read",(object)new[]{"DB1.DBB0"}))["complete"],"item error quality preserved");
            online.Dispose();
            Console.WriteLine(IndexJson.Serializer().Serialize(new {mode=optimized?"optimized":"baseline",checks=checks,index_status_1000_objects_median_ms=indexMs,sampling_100x128_tags_fake_transport_median_ms=sampleMs}));
            return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
        finally { Directory.Delete(root,true); }
    }
    sealed class FakeTransport:IPduTransport
    {
        internal int Reads; internal bool FailItem;
        public byte[] Exchange(byte[] pdu)
        {
            if(pdu[10]==0xF0){var r=Reply(pdu,8,0);r[12]=0xF0;r[18]=1;r[19]=0xE0;return r;}
            Reads++;int len=S7Connection.Get16(pdu,16);int start=((pdu[21]<<16)|(pdu[22]<<8)|pdu[23])/8;
            var result=Reply(pdu,2,4+len);result[12]=4;result[13]=1;result[14]=(byte)(FailItem?5:255);result[15]=4;
            result[16]=(byte)((len*8)>>8);result[17]=(byte)(len*8);
            for(int i=0;i<len;i++)result[18+i]=(byte)(start+i);
            return result;
        }
        static byte[] Reply(byte[] request,int parameters,int data)
        {var r=new byte[12+parameters+data];r[0]=0x32;r[1]=3;r[4]=request[4];r[5]=request[5];r[7]=(byte)parameters;r[8]=(byte)(data>>8);r[9]=(byte)data;return r;}
        public void Dispose(){}
    }
}
