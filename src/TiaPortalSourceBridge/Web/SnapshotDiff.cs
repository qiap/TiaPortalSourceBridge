using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using TiaPortalSourceBridge.Indexing;
using TiaPortalSourceBridge.Openness;

namespace TiaPortalSourceBridge.Web
{
    internal static class SnapshotDiff
    {
        internal static string Confined(string root, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(":")) throw new IOException("备份路径无效。");
            root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(Path.Combine(root, relative));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("备份路径越界。");
            for (string part = full; part != null && part.Length >= root.Length - 1; part = Path.GetDirectoryName(part))
                if ((File.Exists(part) || Directory.Exists(part)) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0) throw new IOException("不读取链接备份路径。");
            return full;
        }
        internal static string ReadBounded(string path, int max)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                if (stream.Length > max) throw new IOException("文件超过对比大小限制。");
                using (var reader = new StreamReader(stream, Encoding.UTF8, true)) return reader.ReadToEnd();
            }
        }
        private static BackupRecord Metadata(string path)
        {
            var meta = IndexJson.Serializer().Deserialize<BackupRecord>(ReadBounded(path + ".meta.json", 65536));
            if (meta == null || meta.Version != 1 || meta.GroupPath == null || string.IsNullOrWhiteSpace(meta.Name) || string.IsNullOrWhiteSpace(meta.ProjectPath) || string.IsNullOrWhiteSpace(meta.Plc)) throw new IOException("备份身份信息不完整。");
            return meta;
        }
        private static bool Target(BackupRecord meta, string project, string plc)
        {
            return string.Equals(Path.GetFullPath(meta.ProjectPath), Path.GetFullPath(project), StringComparison.OrdinalIgnoreCase) && meta.Plc == plc;
        }
        internal static object[] List(string root, string project, string plc)
        {
            if (!Directory.Exists(root)) return new object[0];
            var files = new List<FileInfo>(); var pending = new Stack<string>(); pending.Push(root);
            int visited = 0;
            while (pending.Count > 0) {
                string dir = pending.Pop();
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue;
                foreach (string item in Directory.EnumerateFileSystemEntries(dir)) {
                    if (++visited > 20000) throw new IOException("备份目录超过 20000 项，请归档旧备份后重试。");
                    var attrs = File.GetAttributes(item);
                    if ((attrs & FileAttributes.ReparsePoint) != 0) continue;
                    if ((attrs & FileAttributes.Directory) != 0) pending.Push(item);
                    else if (item.EndsWith(".xml.meta.json", StringComparison.OrdinalIgnoreCase)) files.Add(new FileInfo(item));
                }
            }
            var output = new List<object>();
            foreach (var file in files.OrderByDescending(f => f.LastWriteTimeUtc)) {
                try {
                    string xml = file.FullName.Substring(0, file.FullName.Length - 10);
                    var meta = Metadata(xml);
                    if (!Target(meta, project, plc) || !File.Exists(xml)) continue;
                    output.Add(new { xml_file = xml.Substring(Path.GetFullPath(root).TrimEnd('\\').Length + 1), name = meta.Name, kind = meta.Kind,
                        group = meta.GroupPath, plc = meta.Plc, created_at = meta.CreatedAtUtc, xml_size = new FileInfo(xml).Length });
                    if (output.Count == 100) break;
                } catch (IOException) { } catch (ArgumentException) { } catch (InvalidOperationException) { }
            }
            return output.ToArray();
        }
        internal static FileDiffResult Compare(string root, string relative, string project, string plc, string version)
        {
            string path = Confined(root, relative);
            if (!path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) throw new IOException("仅支持 XML 备份。");
            Confined(root, relative + ".meta.json");
            var meta = Metadata(path);
            if (!Target(meta, project, plc)) throw new IOException("备份所属工程或 PLC 与当前选择不一致。");
            string oldText = ReadBounded(path, 8 * 1024 * 1024);
            if (!string.Equals(IndexJson.Hash(oldText), meta.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("备份 XML 与指纹不一致。");
            string kind = meta.Kind == "Block" ? "plc.block" : meta.Kind == "Type" ? "plc.type" : meta.Kind == "TagTable" ? "plc.tags" : "";
            if (kind.Length == 0) throw new IOException("当前只支持 PLC 块、数据类型与变量表备份。");
            IndexEntry entry;
            var store = new ProjectIndexStore(project, version);
            string current = store.DiffSource(plc, kind, meta.GroupPath, meta.Name, out entry);
            var result = DiffHelper.CompareText(oldText, current, meta.Name);
            if (result.error != null) throw new IOException(result.error);
            result.evidence = new { project_path = project, plc_name = plc, group = meta.GroupPath, backup_created_utc = meta.CreatedAtUtc,
                backup_sha256 = meta.Sha256, snapshot_indexed_utc = entry.indexed_utc, snapshot_sha256 = entry.content_hash,
                note = "比较历史备份与已读取的索引源码快照，不代表博途实时内容。需要最新状态时，请先从博途重新读取。" };
            return result;
        }
    }
}
