using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TiaPortalSourceBridge.Indexing
{
    internal sealed partial class ProjectIndexStore
    {
        internal Dictionary<IndexEntry, string> ArchiveSources(string owner, List<string> warnings)
        {
            long stamp = File.GetLastWriteTimeUtc(CatalogFile).Ticks;
            string dirty = DirtyRevision(); var state = Snapshot();
            var entries = Load().objects.Where(e => e.owner == owner && new[] { "plc.block", "plc.type", "plc.tags" }.Contains(e.kind)).ToArray();
            if (entries.Length == 0 || entries.Length > 5000) throw new InvalidOperationException("源码目录为空或超过 5000 个对象，请检查当前 PLC 索引。");
            var output = new Dictionary<IndexEntry, string>(); long length = 0;
            foreach (var entry in entries) {
                if (State(entry, state) != "ready") { warnings.Add(entry.name + "：" + State(entry, state) + "，未收入快照。"); continue; }
                if (!HashHexRegex.IsMatch(entry.content_hash ?? "")) throw new IOException("源码指纹无效：" + entry.name);
                string path = Path.Combine(Root, "source", entry.content_hash + ".xml"), text;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                    if (stream.Length > 8 * 1024 * 1024 || (length += stream.Length) > 64 * 1024 * 1024) throw new IOException("源码快照超过单对象 8 MiB 或总计 64 MiB 限制。");
                    using (var reader = new StreamReader(stream)) text = reader.ReadToEnd();
                }
                if (!string.Equals(IndexJson.Hash(text), entry.content_hash, StringComparison.OrdinalIgnoreCase)) throw new IOException("源码指纹不一致：" + entry.name);
                output.Add(IndexJson.Serializer().Deserialize<IndexEntry>(IndexJson.Serializer().Serialize(entry)), text);
            }
            if (stamp != File.GetLastWriteTimeUtc(CatalogFile).Ticks || dirty != DirtyRevision()) throw new IOException("读取期间源码索引变化，请重试。");
            if (output.Count == 0) throw new IOException("没有可用源码，请先从博途重新读取。");
            return output;
        }
    }
}
