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
        private sealed class AuditJob
        {
            public string id, snapshot_id, project_path, plc_name, state = "running", message = "正在分析已读取的源码", error = "";
            public AuditReport result;
        }
        private readonly object auditGate = new object();
        private readonly Dictionary<string, AuditJob> auditJobs = new Dictionary<string, AuditJob>();

        internal static void ValidateAuditResponse(AuditReport report, int maximumCharacters = 64 * 1024 * 1024 - 65536)
        {
            // Reserve room for the status envelope. A completed job must remain
            // readable through the same bounded JSON transport as its status.
            var json = CylinderWritePipeline.Json(); json.MaxJsonLength = maximumCharacters;
            try { json.Serialize(report); }
            catch (InvalidOperationException ex) {
                throw new InvalidOperationException("体检报告超出可返回的大小或结构限制，无法完整展示；未生成截断报告。请缩小读取范围后重试。", ex);
            }
        }

        // Only rules already saved for this exact project/PLC/host are accepted.
        // An obsolete or conflicting template is reported as uncovered, never guessed.
        internal static IList<AuditTemplate> AuditTemplates(string root, IoSnapshot snapshot,
            Func<IndexEntry, string> source, IList<string> warnings)
        {
            var result = new List<AuditTemplate>();
            string directory = Path.Combine(root, "project-templates", XmlBatch.Hash(Path.GetFullPath(snapshot.project_path).ToUpperInvariant()));
            if (!Directory.Exists(directory)) return result;
            var files = Directory.EnumerateFiles(directory, "*.json").OrderBy(p => p, StringComparer.Ordinal).Take(257).ToArray();
            if (files.Length > 256) warnings.Add("项目模板超过 256 份，仅检查前 256 份；其余模板未覆盖。");
            foreach (string file in files.Take(256)) {
                try {
                    if (new FileInfo(file).Length > 65536) throw new InvalidOperationException("模板超过读取大小上限");
                    var record = CylinderWritePipeline.Json().Deserialize<Dictionary<string, object>>(File.ReadAllText(file));
                    if (record == null) throw new InvalidOperationException("模板内容为空");
                    if (!PathsEqual(Text(record, "project_path"), snapshot.project_path) || Text(record, "plc_name") != snapshot.plc_name) continue;
                    var hosts = snapshot.cylinderBlocks.Where(b => b.name == Text(record, "host_name") &&
                        (b.group ?? "") == Text(record, "group_path") && (b.unit_name ?? "") == Text(record, "unit_name")).ToArray();
                    int network;
                    if (hosts.Length != 1 || !int.TryParse(Text(record, "sample_network"), out network))
                        throw new InvalidOperationException("宿主或样板网络不在当前快照内");
                    var identity = CylinderTemplateStore.Context(snapshot, hosts[0], source, Text(record, "sample_id"), network);
                    if (!PathsEqual(CylinderTemplateStore.PathFor(root, identity), file)) throw new InvalidOperationException("模板存储身份不一致");
                    record = CylinderTemplateStore.Read(root, identity);
                    if (record == null || Text(record, "fb") != identity.fb || Text(record, "sample_fingerprint") != identity.sample_fingerprint)
                        throw new InvalidOperationException("样板或 FB 接口已变化，请在气缸页重新核对模板");
                    object raw; var roles = record.TryGetValue("rules", out raw) ? raw as Dictionary<string, object> : null;
                    var allowed = new[] { "shared", "device", "index", "input", "output", "open", "feedback" };
                    if (roles == null || roles.Count != identity.parameters.Length || identity.parameters.Any(p => !roles.ContainsKey(p)) ||
                        roles.Any(p => !(p.Value is string) || !allowed.Contains((string)p.Value)))
                        throw new InvalidOperationException("模板参数规则不完整");
                    result.Add(new AuditTemplate { host_id = hosts[0].id, fb = identity.fb, sample_id = identity.sample_id,
                        parameters = roles.Where(p => (string)p.Value != "open").Select(p => p.Key).OrderBy(p => p, StringComparer.Ordinal).ToArray() });
                } catch (Exception ex) { warnings.Add("模板未参与引脚检查（" + Path.GetFileName(file) + "）：" + ex.Message); }
            }
            var coherent = new List<AuditTemplate>();
            foreach (var group in result.GroupBy(t => t.host_id + "\n" + t.fb)) {
                if (group.Select(t => string.Join("\n", t.parameters)).Distinct(StringComparer.Ordinal).Count() != 1)
                    warnings.Add("同一宿主的 " + group.First().fb + " 存在不同连接要求，未自动选用模板；请先核对模板。");
                else coherent.Add(group.First());
            }
            return coherent;
        }

        private bool HandleAudit(HttpListenerContext context, string path)
        {
            if (path != "/api/io/audit-run" && path != "/api/io/audit-status" && path != "/api/io/audit-export") return false;
            var request = context.Request; var response = context.Response;
            response.Headers["Cache-Control"] = "no-store";
            if (path != "/api/io/audit-run") {
                if (request.HttpMethod != "GET") { SendJson(response, 405, new { error = "只支持读取体检结果" }); return true; }
                AuditJob job; string id = CylinderRequestId(GetQueryParam(request, "id"));
                lock (auditGate) {
                    if (!auditJobs.TryGetValue(id, out job)) { SendJson(response, 404, new { error = "体检结果已过期，请重新检查当前快照" }); return true; }
                    if (path == "/api/io/audit-status") {
                        SendJson(response, 200, new { job.id, job.snapshot_id, job.state, job.message, job.error, job.project_path, job.plc_name, job.result }); return true;
                    }
                    if (job.state != "completed") { SendJson(response, 409, new { error = "体检尚未完成，无法导出" }); return true; }
                }
                byte[] bytes = AuditWorkbook.Create(job.result);
                response.ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
                response.Headers["Content-Disposition"] = "attachment; filename=\"TIA-Audit-" + job.id + ".xlsx\"";
                response.ContentLength64 = bytes.Length; response.OutputStream.Write(bytes, 0, bytes.Length); response.Close(); return true;
            }
            if (request.HttpMethod != "POST") { SendJson(response, 405, new { error = "请使用体检按钮启动检查" }); return true; }
            if (shutdownRequested) { SendJson(response, 503, new { error = "服务正在退出" }); return true; }
            if (request.ContentLength64 < 0 || request.ContentLength64 > 16384) { SendJson(response, 413, new { error = "请求过大" }); return true; }
            Dictionary<string, object> map;
            try { map = CylinderWritePipeline.Json().DeserializeObject(ReadBody(request)) as Dictionary<string, object>; }
            catch (ArgumentException) { SendJson(response, 400, new { error = "体检参数格式无效" }); return true; }
            if (map == null) { SendJson(response, 400, new { error = "缺少体检参数" }); return true; }
            string project = Text(map, "project_path"), plc = Text(map, "plc_name");
            IoJob snapshot;
            lock (ioGate) {
                if (plc.Length == 0 || !File.Exists(project) || !actionTargets.Verify(project, plc, Text(map, "target_token")) ||
                    !ioJobs.TryGetValue(Text(map, "id"), out snapshot) || snapshot.state != "completed" || snapshot.result == null ||
                    !PathsEqual(snapshot.project, project) || snapshot.plc != plc || consumedCylinderSnapshots.Contains(snapshot.id)) {
                    SendJson(response, 409, new { error = "目标或源码快照已失效，请先读取当前工程和 PLC" }); return true;
                }
            }
            lock (auditGate) {
                if (auditJobs.Values.Any(j => j.state == "running")) { SendJson(response, 409, new { error = "已有体检正在执行，请等待完成" }); return true; }
                while (auditJobs.Count >= 4) auditJobs.Remove(auditJobs.First().Key);
                var job = new AuditJob { id = Guid.NewGuid().ToString("N"), snapshot_id = snapshot.id, project_path = project, plc_name = plc };
                auditJobs.Add(job.id, job);
                ThreadPool.QueueUserWorkItem(_ => RunAudit(job, snapshot));
                SendJson(response, 202, new { job.id }); return true;
            }
        }
        private void RunAudit(AuditJob job, IoJob snapshot)
        {
            var log = ExecutionLog.Begin(_configPath, "看板", "程序体检", job.project_path + " · " + job.plc_name);
            try {
                // This path reads content-addressed local XML only. It never invokes
                // Dashboard, Openness, compilation, save, or any write pipeline.
                var store = new ProjectIndexStore(job.project_path, AppConfig.DetectProjectVersion(job.project_path));
                var warnings = new List<string>();
                var templates = AuditTemplates(Path.GetDirectoryName(_configPath), snapshot.result, store.IoSource, warnings);
                var report = ProgramAudit.Run(snapshot.result, store.IoSource, templates);
                report.id = job.id; report.warnings.AddRange(warnings);
                report.coverage["source_mode"] = snapshot.cached ? "local_cache" : "tia_readback";
                report.coverage["source_note"] = snapshot.result.reference_coverage ?? "";
                ValidateAuditResponse(report);
                string message = "体检完成：待核对 " + report.findings.Count(f => f.severity == "warning") + " 项，提示 " + report.findings.Count(f => f.severity == "info") + " 项；请同时查看覆盖范围。";
                log.Update(report.findings.Count > 0 || report.warnings.Count > 0 ? "warning" : "completed", message, true);
                lock (auditGate) {
                    job.result = report; job.state = "completed";
                    job.message = message;
                }
            } catch (Exception ex) {
                string message = "体检失败，未修改工程";
                log.Update("failed", message + "：" + ex.Message, true);
                lock (auditGate) { job.state = "failed"; job.error = ex.Message; job.message = message; }
            }
        }
    }
}
