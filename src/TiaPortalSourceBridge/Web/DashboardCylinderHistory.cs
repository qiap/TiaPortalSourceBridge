using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using TiaPortalSourceBridge.Configuration;
using TiaPortalSourceBridge.Indexing;
using TiaPortalSourceBridge.Mcp;

namespace TiaPortalSourceBridge.Web
{
    internal sealed partial class WebServer
    {
        private sealed class CylinderObservation : IDisposable
        {
            private readonly Action stop;
            internal CylinderObservation(Action stop) { this.stop = stop; }
            public void Dispose() { stop(); }
        }
        private IDisposable ObserveCylinderBatch(CylinderJob job, Action<string> report)
        {
            string path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_configPath)), "block-backups", "batch-operation-" + job.id, "result.json");
            object gate = new object(); bool stopped = false; string previous = "";
            var timer = new Timer(_ => {
                lock (gate) {
                    if (stopped) return;
                    try {
                        if (!File.Exists(path)) return;
                        string json;
                        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        using (var reader = new StreamReader(stream)) json = reader.ReadToEnd();
                        if (json == previous) return;
                        var batch = CylinderWritePipeline.Json().Deserialize<Dictionary<string, object>>(json);
                        string phase = Text(batch, "phase");
                        string stage = phase == "complete" || phase == "needs_review" ? "verify" : phase;
                        var items = CylinderWritePipeline.Rows(batch, "items");
                        string current = items.Where(i => Text(i, "status") == "importing").Select(i => Text(i, "block_name")).FirstOrDefault();
                        string message = phase == "backup" ? "正在备份程序块及关联实例 DB" :
                            phase == "import" ? "正在写入 " + (current ?? "程序块") + "；已完成 " + items.Count(i => Text(i, "status") == "imported") + "/" + items.Length :
                            phase == "compile" ? "正在编译 PLC" : phase == "verify" ? "正在逐块回读并核对写入结果" :
                            phase == "complete" ? "程序块回读完成，正在确认关联对象及最终结果" :
                            phase == "needs_review" ? "批次存在需要核对的结果，正在收集详情" : "正在检查全部写入对象";
                        lock (ioGate) job.result["live_batch"] = batch;
                        CylinderProgress(job, stage, message, report);
                        previous = json;
                    } catch (IOException) { /* The atomic snapshot may be rotating. */ }
                    catch (Exception) { /* Progress never changes the engineering result. */ }
                }
            }, null, 0, 500);
            return new CylinderObservation(() => { lock (gate) { stopped = true; timer.Dispose(); } });
        }

        private static object PublicCylinderJob(CylinderJob job)
        {
            // Planned XML and whole-project fingerprints stay on the server. Polls
            // need results, not another copy of every generated block.
            return new { job.id, job.request_hash, job.snapshot_id, job.project, job.plc, job.state, job.stage, job.message,
                job.error, job.started, job.updated, job.cylinder_name, job.instance_name, job.parent_id, job.recovery_action,
                job.resolution_id, job.events, job.write_started, job.can_check, job.result };
        }

        private bool HandleCylinderHistory(HttpListenerContext context, string path)
        {
            if (path != "/api/io/cylinder-history") return false;
            if (context.Request.HttpMethod != "GET") { SendJson(context.Response, 405, new { error = "只支持读取历史记录" }); return true; }
            string project = GetQueryParam(context.Request, "project_path"), plc = GetQueryParam(context.Request, "plc_name");
            int offset, limit;
            if (!int.TryParse(GetQueryParam(context.Request, "offset"), out offset)) offset = 0;
            if (!int.TryParse(GetQueryParam(context.Request, "limit"), out limit)) limit = 30;
            offset = Math.Max(0, offset); limit = Math.Max(1, Math.Min(100, limit));
            var jobs = new List<CylinderJob>(); var warnings = new List<string>();
            lock (ioGate) {
                string directory = Path.GetDirectoryName(CylinderReceiptPath("unused"));
                if (Directory.Exists(directory)) foreach (string file in Directory.EnumerateFiles(directory, "*.json")) {
                    string id = CylinderRequestId(Path.GetFileNameWithoutExtension(file));
                    if (id.Length == 0) continue;
                    try {
                        var job = FindCylinder(id);
                        if (job != null && (string.IsNullOrEmpty(project) || PathsEqual(project, job.project)) &&
                            (string.IsNullOrEmpty(plc) || string.Equals(plc, job.plc, StringComparison.OrdinalIgnoreCase))) jobs.Add(job);
                    } catch (Exception ex) { warnings.Add("记录 " + id + " 无法读取：" + ex.Message); }
                }
                var ordered = jobs.OrderByDescending(j => j.started ?? j.updated, StringComparer.Ordinal).ThenBy(j => j.id, StringComparer.Ordinal).ToArray();
                var page = ordered.Skip(offset).Take(limit).Select(job => {
                    object value;
                    var batch = job.result.TryGetValue("import_result", out value) ? CylinderWritePipeline.Map(value) : new Dictionary<string, object>();
                    return new { job.id, job.started, job.updated, job.project, job.plc, job.state, job.stage, job.message,
                        job.cylinder_name, job.instance_name, job.parent_id, job.recovery_action, job.resolution_id, job.can_check,
                        saved = CylinderWritePipeline.True(job.result, "saved"), warning_count = Text(batch, "warning_count"), error_count = Text(batch, "error_count") };
                }).ToArray();
                SendJson(context.Response, 200, new { jobs = page, total = ordered.Length, offset, limit, warnings });
            }
            return true;
        }

        internal static List<Dictionary<string, object>> SnapshotCylinderSignatures(IoSnapshot snapshot, Func<IndexEntry, string> source)
        {
            if (snapshot == null || snapshot.cylinderProgramsTotal <= 0 || snapshot.cylinderProgramsRead != snapshot.cylinderProgramsTotal ||
                snapshot.tables_read != snapshot.tables_total || snapshot.cylinderBlocks == null || snapshot.cylinderBlocks.Length == 0)
                throw new InvalidOperationException("程序或变量表读取不完整，无法建立完整的执行核对记录。");
            var rows = snapshot.cylinderBlocks.OrderBy(b => b.unit_name, StringComparer.Ordinal).ThenBy(b => b.group, StringComparer.Ordinal)
                .ThenBy(b => b.name, StringComparer.Ordinal).Select(b => new Dictionary<string, object> {
                    { "kind", "block" }, { "block_name", b.name }, { "group_path", b.group ?? "" }, { "unit_name", b.unit_name ?? "" },
                    { "signature", XmlBatch.RecoverySignature(source(b)) }
                }).ToList();
            // Mapping declarations are already covered by their full block signatures.
            // Native PLC tags are not changed by cylinder generation; any change to
            // their address/name/type therefore requires a new review.
            var io = snapshot.rows.Where(r => string.IsNullOrEmpty(r.origin) && string.IsNullOrEmpty(r.mapping_fc))
                .Select(r => CylinderWritePipeline.Json().Serialize(new[] { r.unit, r.group, r.table, r.name,
                IoTable.Address(r.address), r.data_type, r.value, r.comment, r.mapping_fc, r.mapping_group, r.carrier, r.origin }))
                .OrderBy(s => s, StringComparer.Ordinal).ToArray();
            rows.Add(new Dictionary<string, object> { { "kind", "io" }, { "block_name", "@io" }, { "group_path", "" }, { "unit_name", "" },
                { "signature", XmlBatch.Hash(CylinderWritePipeline.Json().Serialize(io)) } });
            return rows;
        }

        private void CaptureCylinderPost(CylinderJob job, Action<string> report)
        {
            try {
                CylinderProgress(job, "evidence", "正在记录执行后的工程状态，便于后续核对和恢复", report);
                var fresh = new IoJob { project = job.project, plc = job.plc, cached = false,
                    report = message => CylinderProgress(job, "evidence", message, report) };
                ReadIo(fresh);
                var store = new ProjectIndexStore(job.project, AppConfig.DetectProjectVersion(job.project));
                var captured = SnapshotCylinderSignatures(fresh.result, store.IoSource);
                VerifyCylinderPostEvidence(job.source_signatures, job.result, captured);
                lock (ioGate) {
                    job.post_signatures = captured;
                    job.result.Remove("evidence_error");
                    PersistCylinder(job);
                }
            } catch (Exception ex) {
                lock (ioGate) {
                    job.post_signatures = new List<Dictionary<string, object>>();
                    job.result["evidence_error"] = "未能完整记录执行后状态：" + ex.Message + "；自动继续与恢复不可用。";
                    PersistCylinder(job);
                }
                report(Text(job.result, "evidence_error"));
            }
        }

        internal static void VerifyCylinderPostEvidence(List<Dictionary<string, object>> before,
            Dictionary<string, object> result, List<Dictionary<string, object>> captured)
        {
            var expected = CylinderRecoveryPolicy.Signatures(before);
            var current = CylinderRecoveryPolicy.Signatures(captured);
            object imported;
            if (!result.TryGetValue("import_result", out imported)) throw new InvalidOperationException("缺少本次导入的终态证据。");
            var batch = CylinderWritePipeline.Map(imported);
            foreach (var row in CylinderWritePipeline.Rows(batch, "items").Concat(CylinderWritePipeline.Rows(batch, "related_items"))) {
                string name = Text(row, "block_name"), key = CylinderRecoveryPolicy.Key(row);
                // Save-only receipts omit groups; names remain unique in this V16-20 workflow.
                if (!expected.ContainsKey(key) && Text(row, "status") == "unchanged") {
                    var matches = before.Where(r => Text(r, "block_name") == name).ToArray();
                    if (matches.Length == 1) key = CylinderRecoveryPolicy.Key(matches[0]);
                }
                if (!expected.ContainsKey(key)) throw new InvalidOperationException("回读对象不属于本次工程快照：" + name);
                string signature = Text(row, "after_recovery_signature");
                if (signature.Length == 0) signature = Text(row, "after_signature");
                if (signature.Length == 64) expected[key] = signature;
                else if (!new[] { "pending", "validated", "backed_up", "failed", "unchanged" }.Contains(Text(row, "status")))
                    throw new InvalidOperationException("缺少已写入对象的完整回读证据：" + name);
            }
            var changed = expected.Keys.Where(k => !current.ContainsKey(k) || expected[k] != current[k]).Concat(current.Keys.Where(k => !expected.ContainsKey(k))).ToArray();
            if (changed.Length > 0) throw new InvalidOperationException("批次回读后工程又有变化，不能把后续修改归入原任务：" + string.Join("、", changed.Take(6).Select(k => k.Split('\n')[0])));
        }

        private void EnsureCylinderRecoveryIdle(CylinderJob source)
        {
            if (source.state == "running") throw new InvalidOperationException("原任务仍在执行，请等待最终结果。");
            var activity = ProjectOperationGuard.Inspect(source.project);
            if (activity.Busy || activity.Unavailable) throw new InvalidOperationException(activity.Reason);
        }

        private void ResolveCylinderRecoveryOwner(CylinderJob source)
        {
            EnsureCylinderRecoveryIdle(source);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var current = source;
            while (current != null && ids.Add(current.id)) {
                current = string.IsNullOrEmpty(current.parent_id) ? null : FindCylinder(current.parent_id);
            }
            var activity = ProjectOperationGuard.Inspect(source.project);
            if (activity.OperationIds.Any(id => !ids.Contains(id)))
                throw new InvalidOperationException("此工程还有其他结果未确认的操作，请先核对对应记录。");
            foreach (var id in activity.OperationIds) ProjectOperationGuard.ResolveStopped(source.project, id);
        }
    }
}
