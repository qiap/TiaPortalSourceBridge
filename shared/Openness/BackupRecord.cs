using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace TiaPortalSourceBridge.Openness
{
    internal sealed class BackupRecord
    {
        public int Version { get; set; }
        public string ProjectPath { get; set; }
        public string Plc { get; set; }
        public string GroupPath { get; set; }
        public string Kind { get; set; }
        public string Name { get; set; }
        public string CreatedAtUtc { get; set; }
        public string Sha256 { get; set; }

        public static string Write(string root, string label, string xml, string project, string plc,
            string group, string kind, string name)
        {
            Directory.CreateDirectory(root);
            string path = Path.Combine(root, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-"
                + PathTools.SafeName(label) + "-" + Guid.NewGuid().ToString("N") + ".xml");
            File.WriteAllText(path, xml, new UTF8Encoding(false));
            var record = new BackupRecord
            {
                Version = 1, ProjectPath = Path.GetFullPath(project), Plc = plc,
                GroupPath = PathTools.NormalizeGroupPath(group), Kind = kind, Name = name,
                CreatedAtUtc = DateTime.UtcNow.ToString("o"), Sha256 = Hash(xml)
            };
            // Both files must be durable before the caller changes the engineering object.
            File.WriteAllText(path + ".meta.json", new JavaScriptSerializer().Serialize(record), new UTF8Encoding(false));
            return path;
        }

        public static BackupRecord Read(string path, string xml, string project, string plc)
        {
            string metadataPath = path + ".meta.json";
            if (!File.Exists(metadataPath)) return null; // Legacy XML backups remain usable.
            BackupRecord record = new JavaScriptSerializer().Deserialize<BackupRecord>(File.ReadAllText(metadataPath, Encoding.UTF8));
            if (record == null || record.Version != 1 || record.GroupPath == null
                || string.IsNullOrWhiteSpace(record.ProjectPath) || string.IsNullOrWhiteSpace(record.Plc)
                || string.IsNullOrWhiteSpace(record.Kind) || string.IsNullOrWhiteSpace(record.Name))
                throw new InvalidOperationException("备份元数据无效。");
            if (!string.Equals(record.Sha256, Hash(xml), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("备份 XML 内容与指纹不一致，拒绝恢复。");
            if (!string.Equals(Path.GetFullPath(record.ProjectPath), Path.GetFullPath(project), StringComparison.OrdinalIgnoreCase)
                || !string.Equals(record.Plc, plc, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("备份所属工程或 PLC 与当前目标不一致，拒绝恢复。");
            return record;
        }

        private static string Hash(string xml)
        {
            using (SHA256 hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(xml))).Replace("-", "");
        }
    }
}
