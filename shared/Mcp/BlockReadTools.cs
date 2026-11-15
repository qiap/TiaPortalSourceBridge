using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TiaPortalSourceBridge.Configuration;
using TiaPortalSourceBridge.Indexing;

namespace TiaPortalSourceBridge.Mcp
{
    internal sealed partial class McpServer
    {
        private static bool IsBlockSnapshotRead(string name){return name=="tia_read_block"||name=="tia_read_blocks_batch";}
        private static string BlockReadMode(Dictionary<string,object> args)
        {
            string mode=StringArg(args,"read_mode");if(mode.Length==0)mode="live";
            if(mode!="live"&&mode!="cache")throw new ArgumentException("read_mode 必须为 live 或 cache；缓存不能作为写入基准。");
            string format=StringArg(args,"format");if(format.Length>0&&!new[]{"auto","xml","readable"}.Contains(format))throw new ArgumentException("format 必须为 auto、xml 或 readable。");
            IndexInt(args,"network_offset",0,0,100000);IndexInt(args,"network_limit",5,1,20);
            return mode;
        }
        private static string[] BlockReadNames(string name,Dictionary<string,object> args)
        {
            var names=name=="tia_read_block"?new[]{StringArg(args,"block_name")}:StringArrayArg(args,"block_names");
            if(names.Length<1||names.Length>30||names.Any(string.IsNullOrWhiteSpace)||names.Any(n=>n.Length>256))throw new ArgumentException("block_names 需要 1..30 个非空名称，单个名称最多 256 字符。");
            if(names.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=names.Length)throw new ArgumentException("block_names 不允许重复名称。");
            return names;
        }
        private static void FormatBlockRead(Dictionary<string,object> row,string payload,Dictionary<string,object> args)
        {
            row["payload_hash"]=IndexJson.Hash(payload);row["payload_complete"]=true;
            bool xml=payload.TrimStart().StartsWith("<");string format=StringArg(args,"format");
            if(format=="readable"&&xml) {
                var view=BlockReadable.Read(payload,IndexInt(args,"network_offset",0,0,100000),IndexInt(args,"network_limit",5,1,20));
                row["readable_text"]=view["readable_text"];view.Remove("readable_text");row["view"]=view;
                row["view_summary"]=Dict("coverage",view["coverage"],"total_networks",view["total_networks"],"network_offset",view["network_offset"],"next_network_offset",view["next_network_offset"],"page_complete",view["page_complete"],"warnings",view["warnings"]);
                row["format"]="readable";row["payload_complete"]=false;
            } else {row[xml?"xml":"source"]=payload;row["format"]=xml?"xml":"source";}
        }
        private Dictionary<string,object> BlockReadReply(string tool,Dictionary<string,object> args,Dictionary<string,object>[] rows)
        {
            if(tool=="tia_read_block") {
                var row=rows[0];string text=StringArg(row,"readable_text");
                if(text.Length==0)text=StringArg(row,"xml");if(text.Length==0)text=StringArg(row,"source");if(text.Length==0)text=StringArg(row,"error");
                return ToolResult(text,!(bool)row["ok"],row);
            }
            return ToolResult("混合语言批量读取完成；失败/未缓存对象不是已删除对象。",rows.Any(r=>!(bool)r["ok"]),
                Dict("project_path",StringArg(args,"project_path"),"read_mode",BlockReadMode(args),"total_requested",rows.Length,
                    "success_count",rows.Count(r=>(bool)r["ok"]),"failure_count",rows.Count(r=>!(bool)r["ok"]),
                    "all_reads_succeeded",rows.All(r=>(bool)r["ok"]),"coverage_scope","requested_names_only; view coverage and pagination are reported per item", "items",rows));
        }
        private Dictionary<string,object> ReadCachedBlocks(string tool,Dictionary<string,object> args,AppConfig config)
        {
            var rows=Index(config).CachedBlocks(StringArg(args,"plc_name"),StringArg(args,"unit_name"),StringArg(args,"group_path"),BlockReadNames(tool,args));
            foreach(var row in rows)if((bool)row["ok"]) {
                string payload=StringArg(row,"payload");row.Remove("payload");
                try{
                    if(tool=="tia_read_blocks_batch" && (StringArg(args,"format")==""||StringArg(args,"format")=="auto")) {
                        var doc=BlockReadable.Parse(payload);var source=doc.SelectSingleNode("/Document/SW.Blocks.Source/SourceText");
                        if(source!=null){payload=source.InnerText;row["representation"]="cached_external_source";}
                    }
                    FormatBlockRead(row,payload,args);
                }catch(Exception ex){row["ok"]=false;row["code"]="READABLE_VIEW_FAILED";row["error"]=ex.Message;}
            }
            return BlockReadReply(tool,args,rows);
        }
        private Dictionary<string,object> ReadLiveBlocks(string tool,Dictionary<string,object> args,AppConfig config,
            Func<string,string> readXml,Func<string,Dictionary<string,object>> readText)
        {
            var rows=new List<Dictionary<string,object>>();long total=0;
            foreach(string name in BlockReadNames(tool,args)) {
                var row=Dict("name",name,"ok",false,"project_path",config.ProjectPath,"plc_name",config.PlcName,
                    "group_path",StringArg(args,"group_path"),"unit_name",StringArg(args,"unit_name"),"source_kind","live_export","freshness","live_at_read_time");
                try {
                    if(total>=16*1024*1024)throw new InvalidOperationException("批次读取已达到 16 MiB，请拆分 block_names。");
                    string xml=readXml(name);var doc=BlockReadable.Parse(xml);
                    var language=doc.SelectSingleNode("/Document/*/AttributeList/ProgrammingLanguage");
                    var block=doc.SelectSingleNode("/Document/*[starts-with(local-name(),'SW.Blocks.')]");
                    string lang=language==null?"":language.InnerText;row["language"]=lang;
                    string payload=xml;
                    if(tool=="tia_read_blocks_batch"&&(StringArg(args,"format")==""||StringArg(args,"format")=="auto")
                        && (lang=="SCL"||lang=="STL"||block!=null&&(block.LocalName=="SW.Blocks.GlobalDB"||block.LocalName=="SW.Blocks.InstanceDB"))) {
                        try {var source=readText(name);payload=StringArg(source,"source");row["extension"]=source["extension"];row["group_path"]=source["group_path"];}
                        catch(Exception ex){row["text_export_warning"]=ex.Message;row["fallback"]="live_xml";}
                    }
                    total+=Encoding.UTF8.GetByteCount(payload);
                    if(total>16*1024*1024)throw new InvalidOperationException("批次读取超过 16 MiB，此项未返回；请缩小范围。");
                    FormatBlockRead(row,payload,args);row["read_utc"]=DateTime.UtcNow.ToString("o");row["ok"]=true;
                }catch(Exception ex){row["code"]="BLOCK_READ_FAILED";row["error"]=ex.Message;row["payload_complete"]=false;}
                rows.Add(row);
            }
            return BlockReadReply(tool,args,rows.ToArray());
        }
        private static Dictionary<string,object> MixedReadSchema()
        {
            return ObjectSchema(new[]{"block_names"},Property("project_path",StringProperty("目标工程路径")),Property("plc_name",StringProperty("目标 PLC 完整路径；缓存读取必填")),
                Property("unit_name",StringProperty("软件单元；V17 实时读取不支持")),Property("group_path",StringProperty("共同组路径；不自动选择同名块")),
                Property("block_names",Dict("type","array","minItems",1,"maxItems",30,"items",Dict("type","string"))),
                Property("read_mode",StringProperty("live（默认）或 cache（只读快照，不连接博途）")),Property("format",StringProperty("auto（SCL/DB 文本，其余 XML）、xml 或 readable")),
                Property("network_offset",Dict("type","integer","minimum",0)),Property("network_limit",Dict("type","integer","minimum",1,"maximum",20)));
        }
    }
}
