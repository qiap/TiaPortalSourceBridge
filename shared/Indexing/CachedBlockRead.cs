using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TiaPortalSourceBridge.Indexing
{
    internal sealed partial class ProjectIndexStore
    {
        // Index hashes cover the parsed object, while archive/backup hashes cover the full export.
        private void ValidateIndexedSource(string source, IndexEntry entry)
        {
            using(var reader=System.Xml.XmlReader.Create(new StringReader(source),new System.Xml.XmlReaderSettings { DtdProcessing=System.Xml.DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=8*1024*1024 }))
                while(reader.Read())if(reader.Depth>80)throw new IOException("缓存 XML 深度超限。");
            var parsed=ContentIndexer.Parse(source);
            if(!string.Equals(parsed.hash,entry.content_hash,StringComparison.OrdinalIgnoreCase)
                || parsed.object_name!=entry.name || !string.Equals(parsed.source_version,version,StringComparison.OrdinalIgnoreCase))
                throw new IOException("缓存源码指纹或对象身份不一致："+entry.name);
        }

        // Analysis only: no hydration, catalog rebuild, dirty consumption or engineering connection.
        internal Dictionary<string,object>[] CachedBlocks(string owner, string unit, string group, string[] names)
        {
            if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("缓存读取必须指定完整 plc_name；不会猜测目标 PLC。");
            var catalog = Load(); long stamp = File.GetLastWriteTimeUtc(CatalogFile).Ticks;
            string revision = DirtyRevision(); var snapshot = Snapshot();
            var candidates = catalog.objects.Where(e => e.kind == "plc.block" && e.owner == owner && (e.unit_name ?? "") == unit
                && (group.Length == 0 || string.Equals(e.group, group.Trim('/'),StringComparison.OrdinalIgnoreCase))).ToArray();
            var result = new List<Dictionary<string,object>>(); long total = 0;
            foreach (string name in names) {
                var row = new Dictionary<string,object> { {"name",name},{"ok",false},{"source_kind","index_cache"},
                    {"project_path",currentProjectPath},{"plc_name",owner},{"unit_name",unit},{"catalog_utc",catalog.built_utc},
                    {"freshness","unverified_against_live"},{"write_baseline",false} };
                var matches = candidates.Where(e => string.Equals(e.name,name,StringComparison.OrdinalIgnoreCase)).ToArray();
                if(matches.Length!=1) { row["code"]=matches.Length==0?"NOT_INDEXED":"AMBIGUOUS_OBJECT";row["error"]="缓存中没有唯一匹配；不表示工程对象已删除。";result.Add(row);continue; }
                var entry=matches[0];string state=State(entry,snapshot);
                row["group_path"]=entry.group;row["language"]=entry.language;row["indexed_utc"]=entry.indexed_utc;
                row["cache_state"]=state;row["content_hash"]=entry.content_hash;row["catalog_warnings"]=catalog.warnings;
                row["last_index_error"]=entry.error;
                row["freshness"]=state=="stale"?"known_stale":"unverified_against_live";
                try {
                    if (!HashHexRegex.IsMatch(entry.content_hash??"")) throw new IOException("未缓存源码（"+state+"）。");
                    string sourceRoot=Path.Combine(Root,"source"),path=Path.Combine(sourceRoot,entry.content_hash+".xml");
                    foreach(string check in new[]{Root,sourceRoot,path})
                        if((File.GetAttributes(check)&FileAttributes.ReparsePoint)!=0)throw new IOException("缓存路径不允许链接或重解析点。");
                    string source;
                    using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                        if(stream.Length>8*1024*1024 || total+stream.Length>16*1024*1024)throw new IOException("缓存读取超过单块 8 MiB / 批次 16 MiB 限制，请缩小范围。");
                        total+=stream.Length;using(var reader=new StreamReader(stream))source=reader.ReadToEnd();
                    }
                    ValidateIndexedSource(source,entry);
                    row["payload"]=source;row["ok"]=true;row["payload_complete"]=true;
                } catch(IOException ex) { row["code"]="CACHE_SOURCE_UNAVAILABLE";row["error"]=ex.Message;row["payload_complete"]=false; }
                catch(System.Xml.XmlException ex) { row["code"]="CACHE_SOURCE_INVALID";row["error"]=ex.Message;row["payload_complete"]=false; }
                catch(UnauthorizedAccessException ex) { row["code"]="CACHE_ACCESS_DENIED";row["error"]=ex.Message;row["payload_complete"]=false; }
                result.Add(row);
            }
            if(stamp!=File.GetLastWriteTimeUtc(CatalogFile).Ticks || revision!=DirtyRevision())throw new IOException("读取期间缓存发生变化，请重新读取。");
            return result.ToArray();
        }
    }
}
