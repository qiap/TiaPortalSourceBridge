using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace TiaPortalSourceBridge.Mcp
{
    internal sealed class ExecutionRecord
    {
        public string id { get; set; }
        public string operation_id { get; set; }
        public string source { get; set; }
        public string action { get; set; }
        public string target { get; set; }
        public string status { get; set; }
        public string message { get; set; }
        public string started { get; set; }
        public string updated { get; set; }
        public int pid { get; set; }
        public long processStart { get; set; }
    }

    // One atomic snapshot per invocation. Different processes never write the same task.
    internal sealed class ExecutionLog
    {
        private const int KeepFinished = 100;
        private readonly string directory;
        private readonly ExecutionRecord record;
        internal string Id { get { return record.id; } }

        private static string DirectoryFor(string config)
        {
            var root = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(config)));
            if (root.Name.Equals("v21", StringComparison.OrdinalIgnoreCase) && root.Parent != null &&
                root.Parent.Name.Equals("engines", StringComparison.OrdinalIgnoreCase) && root.Parent.Parent != null)
                root = root.Parent.Parent;
            return Path.Combine(root.FullName, "logs", "execution");
        }

        private ExecutionLog(string config, string source, string action, string target, string operationId)
        {
            directory = DirectoryFor(config);
            using (var process = Process.GetCurrentProcess())
                record = new ExecutionRecord { id = Guid.NewGuid().ToString("N"), operation_id = operationId, source = source, action = Limit(action, 160),
                    target = Limit(target, 600), started = DateTime.UtcNow.ToString("o"), pid = process.Id,
                    processStart = process.StartTime.ToUniversalTime().Ticks };
        }

        private static string Limit(string value, int max)
        { return string.IsNullOrEmpty(value) || value.Length <= max ? value : value.Substring(0, max) + "…"; }

        internal static ExecutionLog Begin(string config, string source, string action, string target, string operationId = null)
        {
            var log = new ExecutionLog(config, source, action, target, operationId);
            log.Update("running", "已开始", false);
            return log;
        }

        internal void Update(string status, string message, bool finished)
        {
            try {
                record.status = status;
                record.message = Limit(message, 600);
                record.updated = DateTime.UtcNow.ToString("o");
                Directory.CreateDirectory(directory);
                string active = Path.Combine(directory, record.id + ".active");
                string temp = active + ".tmp";
                File.WriteAllText(temp, new JavaScriptSerializer().Serialize(record), new UTF8Encoding(false));
                if (File.Exists(active)) File.Replace(temp, active, null); else File.Move(temp, active);
                if (finished) {
                    File.Move(active, Path.ChangeExtension(active, ".done"));
                    foreach (var old in Recent(directory, "*.done").Skip(KeepFinished)) File.Delete(old.FullName);
                }
            } catch (Exception ex) { Console.Error.WriteLine("执行日志写入失败：" + ex.Message); }
        }

        private static IEnumerable<FileInfo> Recent(string dir, string pattern)
        { return new DirectoryInfo(dir).GetFiles(pattern).OrderByDescending(f => f.LastWriteTimeUtc); }

        internal static ExecutionRecord[] Read(string config)
        {
            string dir = DirectoryFor(config);
            if (!Directory.Exists(dir)) return new ExecutionRecord[0];
            var rows = new Dictionary<string, ExecutionRecord>();
            var json = new JavaScriptSerializer();
            foreach (var file in Recent(dir, "*.active").Concat(Recent(dir, "*.done").Take(KeepFinished))) {
                try {
                    ExecutionRecord row;
                    using (var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(stream)) row = json.Deserialize<ExecutionRecord>(reader.ReadToEnd());
                    if ((row.status == "running" || row.status == "waiting") &&
                        (file.Extension == ".done" || !OwnerIsAlive(row))) {
                        row.status = "unknown";
                        row.message = "进程已退出，未收到最终结果";
                        if (file.Extension == ".active") File.Move(file.FullName, Path.ChangeExtension(file.FullName, ".done"));
                    }
                    rows[row.id] = row;
                } catch (IOException) { /* File rotated or completed during this read. */ }
            }
            var ordered = rows.Values.OrderByDescending(r => r.updated, StringComparer.Ordinal);
            return ordered.Where(r => r.status == "running" || r.status == "waiting").Concat(
                ordered.Where(r => r.status != "running" && r.status != "waiting").Take(KeepFinished)).ToArray();
        }

        private static bool OwnerIsAlive(ExecutionRecord row)
        {
            try { using (var p = Process.GetProcessById(row.pid)) return p.StartTime.ToUniversalTime().Ticks == row.processStart; }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (System.ComponentModel.Win32Exception) { return true; }
        }
    }
}
