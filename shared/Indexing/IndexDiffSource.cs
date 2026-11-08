using System;
using System.IO;
using System.Linq;

namespace TiaPortalSourceBridge.Indexing
{
    internal sealed partial class ProjectIndexStore
    {
        // Read-only: never hydrate, export, rebuild or update derived content.
        internal string DiffSource(string owner, string kind, string group, string name, out IndexEntry entry)
        {
            long stamp = File.GetLastWriteTimeUtc(CatalogFile).Ticks;
            string dirty = DirtyRevision();
            var candidates = Load().objects.Where(e => e.owner == owner && e.kind == kind && e.group == group && e.name == name).ToArray();
            if (candidates.Length != 1) throw new InvalidOperationException("索引中未找到唯一的同 PLC、类型、组路径及名称对象。请重新读取源码；缺失不表示已删除。");
            entry = candidates[0];
            if (!string.IsNullOrEmpty(entry.unit_name)) throw new InvalidOperationException("旧备份未记录软件单元身份，不能自动匹配单元内对象。");
            if (State(entry, Snapshot()) != "ready") throw new InvalidOperationException("源码快照缺失或已标记过期，请在 I/O 页面从博途重新读取后再对比。");
            if (!HashHexRegex.IsMatch(entry.content_hash ?? "")) throw new InvalidOperationException("源码指纹无效。");
            string path = Path.Combine(Root, "source", entry.content_hash + ".xml");
            if (!File.Exists(path) || new FileInfo(path).Length > 8 * 1024 * 1024) throw new IOException("索引源码缺失或超过 8 MiB。");
            string source;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                if (stream.Length > 8 * 1024 * 1024) throw new IOException("索引源码超过 8 MiB。");
                using (var reader = new StreamReader(stream)) source = reader.ReadToEnd();
            }
            if (!string.Equals(IndexJson.Hash(source), entry.content_hash, StringComparison.OrdinalIgnoreCase)) throw new IOException("索引源码与指纹不一致，请重新读取。");
            if (stamp != File.GetLastWriteTimeUtc(CatalogFile).Ticks || dirty != DirtyRevision()) throw new IOException("对比期间源码快照发生变化，请重试。");
            return source;
        }
    }
}
