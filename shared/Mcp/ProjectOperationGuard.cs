using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace TiaPortalSourceBridge.Mcp
{
    internal sealed class ProjectActivityState
    {
        public bool Busy { get; set; }
        public bool Unresolved { get; set; }
        public bool Unavailable { get; set; }
        public string Reason { get; set; }
        public string[] OperationIds { get; set; }
        public string[] ActiveOperationIds { get; set; }
    }

    // The actual worker owns this handle for the duration of a call. A parent
    // dashboard exiting cannot release it; Windows releases it if the worker dies.
    // Receipts live outside an installation so two installed copies share the guard.
    internal sealed class ProjectOperationGuard : IDisposable
    {
        private sealed class Receipt
        {
            public int version = 1, pid;
            public long process_start;
            public string id, operation_id, project, tool, state, started, updated;
            public bool mutation;
        }
        private readonly FileStream handle;
        private readonly string directory;
        private readonly Receipt receipt;
        private bool completed, disposed;
        private static JavaScriptSerializer Json() { return new JavaScriptSerializer(); }
        internal static string StorageDirectory(string project)
        {
            string normalized = Path.GetFullPath(project).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
            string key;
            using (var sha = SHA256.Create()) key = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(normalized))).Replace("-", "");
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TiaPortalSourceBridge", "project-activity", key);
        }
        private static bool SharingViolation(IOException ex)
        { int code = ex.HResult & 0xffff; return code == 32 || code == 33; }
        private static FileStream Acquire(string directory, bool create)
        {
            if (create) Directory.CreateDirectory(directory);
            return new FileStream(Path.Combine(directory, "activity.lock"), create ? FileMode.OpenOrCreate : FileMode.Open,
                FileAccess.ReadWrite, FileShare.Read);
        }
        private static IEnumerable<Receipt> ReadReceipts(string directory, string project)
        {
            foreach (string file in Directory.GetFiles(directory, "*.json")) {
                Receipt row;
                using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream)) row = Json().Deserialize<Receipt>(reader.ReadToEnd());
                Guid id, operation;
                if (row == null || row.version != 1 || !Guid.TryParse(row.id, out id) || id == Guid.Empty ||
                    !Guid.TryParse(row.operation_id, out operation) || operation == Guid.Empty || row.pid <= 0 || row.process_start <= 0 ||
                    !string.Equals(Path.GetFullPath(row.project), Path.GetFullPath(project), StringComparison.OrdinalIgnoreCase) ||
                    !new[] { "running", "completed", "failed", "resolved" }.Contains(row.state) ||
                    !string.Equals(Path.GetFileNameWithoutExtension(file), row.id, StringComparison.Ordinal))
                    throw new InvalidOperationException("工程执行凭证损坏，无法确认是否仍在执行。");
                yield return row;
            }
        }
        // null is deliberately distinct from stopped (for example access denied).
        private static bool? OwnerAlive(Receipt row)
        {
            try { using (var process = Process.GetProcessById(row.pid)) return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == row.process_start; }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (System.ComponentModel.Win32Exception) { return null; }
        }
        private static void Write(string directory, Receipt row)
        {
            row.updated = DateTime.UtcNow.ToString("o");
            string path = Path.Combine(directory, row.id + ".json"), temp = path + ".tmp";
            File.WriteAllText(temp, Json().Serialize(row), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }
        internal static ProjectActivityState Inspect(string project)
        {
            var result = new ProjectActivityState { Reason = "未发现正在执行的工程调用", OperationIds = new string[0], ActiveOperationIds = new string[0] };
            try {
                string directory = StorageDirectory(project);
                if (!Directory.Exists(directory)) return result;
                FileStream probe = null;
                try { if (File.Exists(Path.Combine(directory, "activity.lock"))) probe = Acquire(directory, false); }
                catch (IOException ex) { if (SharingViolation(ex)) result.Busy = true; else throw; }
                try {
                    var running = ReadReceipts(directory, project).Where(r => r.state == "running").ToArray();
                    result.ActiveOperationIds = result.Busy ? running.Where(r => OwnerAlive(r) != false).Select(r => r.operation_id).Distinct().ToArray() : new string[0];
                    result.OperationIds = running.Where(r => r.mutation).Select(r => r.operation_id).Distinct().ToArray();
                    result.Unresolved = result.OperationIds.Length > 0;
                    if (result.Busy) result.Reason = "该工程的后台调用仍在执行；请等待最终结果，不要重新提交。";
                    else if (result.Unresolved) result.Reason = "存在没有最终结果的工程写入，须先核对当前工程；不会自动重放。";
                    if (running.Any(r => OwnerAlive(r) == null)) {
                        result.Unavailable = true;
                        result.Reason = "无法核实旧后台进程身份；请先恢复进程状态查询。";
                    }
                } finally { if (probe != null) probe.Dispose(); }
            } catch (Exception ex) {
                result.Unavailable = true; result.Reason = "无法读取工程执行凭证：" + ex.Message;
            }
            return result;
        }
        internal static ProjectOperationGuard Enter(string project, string tool, string operationId, bool mutation)
        {
            string directory = StorageDirectory(project); FileStream handle;
            try { handle = Acquire(directory, true); }
            catch (IOException ex) {
                if (SharingViolation(ex)) throw new InvalidOperationException("另一个后台正在操作此工程；本次请求未执行，请等待并核对结果。", ex);
                throw;
            }
            try {
                var rows = ReadReceipts(directory, project).ToArray();
                if (mutation && rows.Any(r => r.state == "running" && r.mutation))
                    throw new InvalidOperationException("该工程存在未确认结果的旧写入；先核对并明确继续或恢复，不能重复提交。");
                Guid parsed;
                string id = Guid.NewGuid().ToString("N");
                string operation = Guid.TryParse(operationId, out parsed) && parsed != Guid.Empty ? parsed.ToString("N") : id;
                Receipt row;
                using (var process = Process.GetCurrentProcess()) row = new Receipt { id = id, operation_id = operation,
                    project = Path.GetFullPath(project), tool = tool, mutation = mutation, state = "running",
                    pid = process.Id, process_start = process.StartTime.ToUniversalTime().Ticks, started = DateTime.UtcNow.ToString("o") };
                Write(directory, row);
                // Only completed receipts are pruned; unresolved writes are retained.
                foreach (var old in rows.Where(r => r.state != "running").OrderByDescending(r => r.updated, StringComparer.Ordinal).Skip(256))
                    File.Delete(Path.Combine(directory, old.id + ".json"));
                return new ProjectOperationGuard(handle, directory, row);
            } catch { handle.Dispose(); throw; }
        }
        private ProjectOperationGuard(FileStream handle, string directory, Receipt receipt)
        { this.handle = handle; this.directory = directory; this.receipt = receipt; }
        internal void Complete(bool failed)
        { receipt.state = failed ? "failed" : "completed"; Write(directory, receipt); completed = true; }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            // A failed receipt write must stay unknown instead of fabricating success.
            try { if (!completed) { receipt.state = "failed"; Write(directory, receipt); } }
            finally { handle.Dispose(); }
        }
        // Called only after explicit recovery was selected and fresh engineering
        // verification passed. Never acknowledges another operation or a live owner.
        internal static void ResolveStopped(string project, string operationId)
        {
            Guid parsed;
            if (!Guid.TryParse(operationId, out parsed) || parsed == Guid.Empty) throw new ArgumentException("无效的原操作编号。");
            string directory = StorageDirectory(project);
            if (!Directory.Exists(directory)) return;
            using (var guard = Acquire(directory, true)) {
                var rows = ReadReceipts(directory, project).Where(r => r.state == "running" && r.mutation && r.operation_id == parsed.ToString("N")).ToArray();
                if (rows.Any(r => OwnerAlive(r) != false)) throw new InvalidOperationException("原后台未确认退出，不能解除未决写入保护。");
                foreach (var row in rows) { row.state = "resolved"; Write(directory, row); }
            }
        }
        internal static bool IsMutation(string name, Dictionary<string, object> args)
        {
            object dry;
            if (new[] { "tia_apply_hardware_configuration", "tia_write_hmi_configuration", "tia_update_graph" }.Contains(name)
                && args.TryGetValue("dry_run", out dry) && Equals(dry, true)) return false;
            // Compile may update instance DBs. Unknown project operations fail closed.
            return !(name.StartsWith("tia_read_", StringComparison.Ordinal) || name.StartsWith("tia_list_", StringComparison.Ordinal) ||
                name.StartsWith("tia_query_", StringComparison.Ordinal) || name.StartsWith("tia_get_", StringComparison.Ordinal) ||
                name.StartsWith("tia_search_", StringComparison.Ordinal) || name.StartsWith("tia_generate_", StringComparison.Ordinal) ||
                new[] { "tia_connect", "tia_disconnect", "tia_disconnect_all", "tia_connection_status", "tia_build_project_index", "tia_patch_graph" }.Contains(name));
        }
    }
}
