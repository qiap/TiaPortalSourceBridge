using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using TiaPortalSourceBridge.Configuration;
using TiaPortalSourceBridge.Indexing;
using TiaPortalSourceBridge.Mcp;
using TiaPortalSourceBridge.Openness;

namespace TiaPortalSourceBridge.Web
{
    // Pure, conservative recovery planning. No PLC call is made by this class.
    internal sealed class CylinderRecoveryAssessment
    {
        internal bool CanContinue, CanRestore;
        internal string Reason = "";
        internal readonly List<Dictionary<string, object>> Items = new List<Dictionary<string, object>>();
        internal readonly List<Dictionary<string, object>> ContinueItems = new List<Dictionary<string, object>>();
        internal readonly List<Dictionary<string, object>> RestoreItems = new List<Dictionary<string, object>>();
    }

    internal static class CylinderRecoveryPolicy
    {
        internal static string Key(string name, string group)
        { return (name ?? "").ToUpperInvariant() + "\n" + PathTools.NormalizeGroupPath(group).ToUpperInvariant(); }
        internal static string Key(Dictionary<string, object> row)
        { return Key(CylinderWritePipeline.Text(row, "block_name"), CylinderWritePipeline.Text(row, "group_path")); }
        internal static string Name(string xml)
        {
            var doc = BlockOperations.ParseBlock(xml);
            var node = doc.SelectSingleNode("/Document/*[starts-with(local-name(),'SW.Blocks.')]/AttributeList/Name");
            if (node == null || string.IsNullOrWhiteSpace(node.InnerText)) throw new InvalidOperationException("恢复证据缺少程序块名称。");
            return node.InnerText;
        }
        internal static Dictionary<string, string> Signatures(IEnumerable<Dictionary<string, object>> rows)
        {
            if (rows == null) throw new InvalidOperationException("缺少完整工程快照证据。");
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var row in rows) {
                string signature = CylinderWritePipeline.Text(row, "signature"), key = Key(row);
                if (CylinderWritePipeline.Text(row, "block_name").Length == 0 || signature.Length != 64 || !signature.All(Uri.IsHexDigit) || result.ContainsKey(key))
                    throw new InvalidOperationException("工程快照证据缺失、损坏或含重复对象。");
                result.Add(key, signature);
            }
            if (result.Count == 0) throw new InvalidOperationException("缺少完整工程快照证据。");
            return result;
        }
        internal static bool Same(IEnumerable<Dictionary<string, object>> a, IEnumerable<Dictionary<string, object>> b)
        {
            var left = Signatures(a); var right = Signatures(b);
            return left.Count == right.Count && left.All(p => right.ContainsKey(p.Key) && p.Value == right[p.Key]);
        }
        internal static List<Dictionary<string, object>> GuardedItems(IEnumerable<Dictionary<string, object>> items,
            IDictionary<string, string> current)
        {
            return items.Select(item => {
                var copy = new Dictionary<string, object>(item);
                string name = Name(CylinderWritePipeline.Text(copy, "xml"));
                string key = Key(name, CylinderWritePipeline.Text(copy, "group_path"));
                if (!current.ContainsKey(key)) throw new InvalidOperationException("当前工程缺少恢复对象：" + name);
                copy["block_name"] = name; copy.Remove("expected_hash"); copy.Remove("expected_content_signature");
                copy["expected_recovery_signature"] = current[key];
                return copy;
            }).ToList();
        }
        internal static CylinderRecoveryAssessment Evaluate(List<Dictionary<string, object>> plan,
            List<Dictionary<string, object>> beforeRows, List<Dictionary<string, object>> postRows,
            List<Dictionary<string, object>> currentRows, List<Dictionary<string, object>> backups)
        {
            var result = new CylinderRecoveryAssessment();
            try {
                if (plan == null || plan.Count == 0) throw new InvalidOperationException("这条历史记录没有完整生成方案，只能查看现场与备份，不能自动继续或恢复。");
                var before = Signatures(beforeRows); var post = Signatures(postRows); var current = Signatures(currentRows);
                if (!beforeRows.Any(r => CylinderWritePipeline.Text(r, "kind") == "io") ||
                    !postRows.Any(r => CylinderWritePipeline.Text(r, "kind") == "io") || !currentRows.Any(r => CylinderWritePipeline.Text(r, "kind") == "io"))
                    throw new InvalidOperationException("这条记录缺少完整 I/O 声明证据，不能自动继续或恢复。");
                if (before.Count != post.Count || before.Keys.Any(k => !post.ContainsKey(k)))
                    throw new InvalidOperationException("操作前后的程序目录不一致，需要人工核对新增、删除或移动的对象。");
                var planned = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
                foreach (var item in plan) {
                    string xml = CylinderWritePipeline.Text(item, "xml"), name = Name(xml), key = Key(name, CylinderWritePipeline.Text(item, "group_path"));
                    BlockOperations.ValidateCompileUnitOrder(BlockOperations.ParseBlock(xml));
                    if (planned.ContainsKey(key) || !before.ContainsKey(key)) throw new InvalidOperationException("生成方案中存在重复或不属于原快照的程序块。");
                    planned.Add(key, item);
                }
                var originals = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
                foreach (var item in backups ?? new List<Dictionary<string, object>>()) {
                    string xml = CylinderWritePipeline.Text(item, "xml"), key = Key(Name(xml), CylinderWritePipeline.Text(item, "group_path"));
                    if (originals.ContainsKey(key) || !before.ContainsKey(key) || before[key] != XmlBatch.RecoverySignature(xml))
                        throw new InvalidOperationException("备份与本次操作前的完整快照不一致。");
                    originals.Add(key, item);
                }
                if (planned.Keys.Any(k => !originals.ContainsKey(k))) throw new InvalidOperationException("本次操作的程序块备份不完整，不能自动继续或恢复。");
                var conflicts = new List<string>();
                foreach (var row in postRows) {
                    string key = Key(row), name = CylinderWritePipeline.Text(row, "block_name");
                    bool changed = !current.ContainsKey(key) || current[key] != post[key];
                    bool unrelated = before[key] != post[key] && !originals.ContainsKey(key);
                    if (changed || unrelated) {
                        conflicts.Add(name);
                        result.Items.Add(new Dictionary<string, object> { { "block_name", name }, { "status", "conflict" },
                            { "message", changed ? "与上次写入后的记录不一致，存在后续修改或对象缺失" : "操作期间出现计划以外的修改" } });
                    }
                }
                foreach (var row in currentRows.Where(r => !post.ContainsKey(Key(r)))) {
                    conflicts.Add(CylinderWritePipeline.Text(row, "block_name"));
                    result.Items.Add(new Dictionary<string, object> { { "block_name", CylinderWritePipeline.Text(row, "block_name") },
                        { "status", "conflict" }, { "message", "本次任务之后新增了对象" } });
                }
                if (conflicts.Count > 0) throw new InvalidOperationException("发现工程后续或计划外修改，已停止自动继续与恢复：" + string.Join("、", conflicts.Take(6)));
                bool compatible = true;
                foreach (var pair in planned) {
                    string xml = CylinderWritePipeline.Text(pair.Value, "xml"), name = Name(xml), signature = XmlBatch.RecoverySignature(xml);
                    bool done = current[pair.Key] == signature, unchanged = current[pair.Key] == before[pair.Key];
                    if (!done) result.ContinueItems.Add(pair.Value);
                    if (!done && !unchanged) compatible = false;
                    result.Items.Add(new Dictionary<string, object> { { "block_name", name }, { "status", done ? "matched" : unchanged ? "not_written" : "different" },
                        { "message", done ? "已与原生成方案一致，无需再次导入" : unchanged ? "仍为操作前内容" : "内容既不等于原块，也不等于生成方案；需检查差异" } });
                }
                foreach (var pair in originals) {
                    if (current[pair.Key] != before[pair.Key]) result.RestoreItems.Add(pair.Value);
                    if (!planned.ContainsKey(pair.Key)) result.Items.Add(new Dictionary<string, object> {
                        { "block_name", Name(CylinderWritePipeline.Text(pair.Value, "xml")) }, { "status", "associated" },
                        { "message", current[pair.Key] == before[pair.Key] ? "关联实例 DB 与操作前一致" : "关联实例 DB 已变化，将随恢复方案处理" } });
                }
                result.CanContinue = compatible;
                result.CanRestore = result.RestoreItems.Count > 0;
                result.Reason = compatible ? result.CanRestore ? "核对通过；可查看剩余步骤或恢复本次变更的方案。" : "核对通过，程序仍为操作前内容，无需恢复；可继续未完成步骤。" : "存在部分改变的程序块，不能自动补写；可检查整套备份恢复方案。";
                var remaining = GuardedItems(result.ContinueItems, current); result.ContinueItems.Clear(); result.ContinueItems.AddRange(remaining);
                var restore = GuardedItems(result.RestoreItems, current); result.RestoreItems.Clear(); result.RestoreItems.AddRange(restore);
            } catch (Exception ex) {
                result.CanContinue = result.CanRestore = false; result.Reason = ex.Message;
                result.ContinueItems.Clear(); result.RestoreItems.Clear();
            }
            return result;
        }
    }

    internal static class CylinderRecoveryWritePipeline
    {
        internal static void Run(List<Dictionary<string, object>> remaining, List<Dictionary<string, object>> targets,
            string project, string plc, Dictionary<string, object> result, Dictionary<string, object> priorBatch, bool compileAlreadyPassed,
            Func<string, Dictionary<string, object>, Dictionary<string, object>> call, Action verifyUnchanged, Action<string> report)
        {
            if (remaining.Count > 0) { CylinderWritePipeline.Run(remaining, project, plc, result, call, report); return; }
            var compilation = priorBatch ?? new Dictionary<string, object>();
            if (!compileAlreadyPassed) {
                report("程序内容已一致，正在重新编译并核对");
                var reply = call("tia_compile_plc", new Dictionary<string, object> { { "project_path", project }, { "plc_name", plc }, { "response_mode", "compact" } });
                compilation = CylinderWritePipeline.Data(reply); lock (result) result["compile_result"] = compilation; int errors;
                if (CylinderWritePipeline.True(reply, "isError") || !int.TryParse(CylinderWritePipeline.Text(compilation, "error_count"), out errors) || errors != 0)
                    throw new CylinderReviewException("重新编译未通过，未保存。" + CylinderWritePipeline.Error(reply), true);
                verifyUnchanged();
            }
            lock (result) result["import_result"] = new Dictionary<string, object> { { "ok", true }, { "compiled", true }, { "error_count", 0 },
                { "warning_count", CylinderWritePipeline.Text(compilation, "warning_count") }, { "compile_report", CylinderWritePipeline.Text(compilation, "compile_report") },
                { "items", targets.Select(i => new Dictionary<string, object> { { "block_name", CylinderRecoveryPolicy.Name(CylinderWritePipeline.Text(i, "xml")) },
                    { "group_path", CylinderWritePipeline.Text(i, "group_path") }, { "status", "unchanged" }, { "verification", "matched" } }).ToArray() } };
            report("核对已通过，正在保存当前工程（包括其他未保存修改）");
            var savedReply = call("tia_save_project", new Dictionary<string, object> { { "project_path", project }, { "confirm", true }, { "response_mode", "compact" } });
            lock (result) result["save_result"] = CylinderWritePipeline.Data(savedReply);
            if (CylinderWritePipeline.True(savedReply, "isError") || !CylinderWritePipeline.True(CylinderWritePipeline.Map(result["save_result"]), "saved"))
                throw new CylinderReviewException("程序内容已核对，但保存未确认成功。" + CylinderWritePipeline.Error(savedReply), true);
            lock (result) result["saved"] = true;
        }
    }

    internal sealed partial class WebServer
    {
        private bool HandleCylinderRecovery(HttpListenerContext context, string path)
        {
            bool check = path == "/api/io/cylinder-recovery-check";
            if (!check && path != "/api/io/cylinder-recover") return false;
            var request = context.Request; var response = context.Response;
            if (request.HttpMethod != "POST") { SendJson(response, 405, new { error = "不支持的请求方法" }); return true; }
            if (shutdownRequested) { SendJson(response, 503, new { error = "服务正在退出" }); return true; }
            if (request.ContentLength64 < 0 || request.ContentLength64 > 16384) { SendJson(response, 413, new { error = "请求过大" }); return true; }
            Dictionary<string, object> map;
            try { map = CylinderWritePipeline.Json().Deserialize<Dictionary<string, object>>(ReadBody(request)); }
            catch (ArgumentException) { SendJson(response, 400, new { error = "恢复参数格式无效" }); return true; }
            if (map == null) { SendJson(response, 400, new { error = "缺少恢复参数" }); return true; }
            string id = CylinderRequestId(Text(map, "id")), requestId = CylinderRequestId(Text(map, "request_id"));
            string action = check ? "check" : Text(map, "action"), checkId = CylinderRequestId(Text(map, "check_id"));
            if (id.Length == 0 || requestId.Length == 0 || (!check && action != "continue" && action != "restore")) {
                SendJson(response, 400, new { error = "缺少有效的任务编号或处理方式" }); return true;
            }
            if (!check && (!CylinderWritePipeline.True(map, "confirm") || !CylinderWritePipeline.True(map, "confirm_save"))) {
                SendJson(response, 400, new { error = "请核对具体方案，并确认写入及保存当前工程（包括其他未保存修改）" }); return true;
            }
            string hash = XmlBatch.Hash(path + "\n" + CylinderWritePipeline.Json().Serialize(CanonicalCylinderValue(map)));
            lock (ioGate) {
                var prior = FindCylinder(requestId);
                if (prior != null) {
                    if (prior.request_hash != hash) SendJson(response, 409, new { error = "操作编号已用于其他请求，未重复执行" });
                    else SendJson(response, 202, new { id = prior.id, state = prior.state, resumed = true });
                    return true;
                }
                var source = FindCylinder(id);
                if (source == null) { SendJson(response, 404, new { error = "没有找到原任务" }); return true; }
                if (source.state == "running" || source.recovery_action == "check") { SendJson(response, 409, new { error = "原任务尚未结束，或选择的是核对记录，请打开原工程变更任务" }); return true; }
                if (!string.IsNullOrEmpty(source.resolution_id)) { SendJson(response, 409, new { error = "此任务已由后续任务处理，请查看关联结果", id = source.resolution_id }); return true; }
                if (!check && source.state == "completed") { SendJson(response, 409, new { error = "此任务已完成；失败恢复不会撤销正常完成的任务" }); return true; }
                if (!check && AppConfig.DetectProjectVersion(source.project) == "V21") { SendJson(response, 409, new { error = "V21 气缸写入恢复尚未开放，请查看记录并人工核对" }); return true; }
                CylinderJob checkedJob = null;
                if (!check) {
                    checkedJob = checkId.Length == 0 ? null : FindCylinder(checkId);
                    object report;
                    if (checkedJob == null || checkedJob.parent_id != source.id || checkedJob.recovery_action != "check" || checkedJob.state != "completed" ||
                        !checkedJob.result.TryGetValue("recovery_check", out report) ||
                        !CylinderWritePipeline.True(CylinderWritePipeline.Map(report), action == "continue" ? "can_continue" : "can_restore") ||
                        checkedJob.result.ContainsKey("consumed_by")) {
                        SendJson(response, 409, new { error = "请先重新核对当前工程，核对结果仅可用于一次后续处理" }); return true;
                    }
                }
                try { EnsureCylinderRecoveryIdle(source); }
                catch (Exception ex) { SendJson(response, 409, new { error = ex.Message }); return true; }
                var job = new CylinderJob { id = requestId, request_hash = hash, project = source.project, plc = source.plc,
                    parent_id = source.id, recovery_action = action, cylinder_name = source.cylinder_name, instance_name = source.instance_name,
                    started = DateTime.UtcNow.ToString("o"), message = check ? "准备只读核对当前工程" : "准备再次核对并处理原任务" };
                PersistCylinder(job); cylinderJobs[job.id] = job;
                if (!ActionRunner.StartEngineeringTask(_configPath, job.project, job.plc, check ? "核对气缸执行结果" : action == "continue" ? "继续气缸任务" : "恢复气缸变更", job.message,
                    progress => RunCylinderRecovery(job, source, checkedJob, progress), job.id, source.id)) {
                    job.state = "failed"; job.error = job.message = "已有工程任务正在执行；本次未进行处理"; PersistCylinder(job);
                    SendJson(response, 409, new { error = job.error, id = job.id }); return true;
                }
                if (checkedJob != null) { checkedJob.result["consumed_by"] = job.id; PersistCylinder(checkedJob); }
                source.result["latest_recovery_id"] = job.id; PersistCylinder(source);
                SendJson(response, 202, new { id = job.id }); return true;
            }
        }

        private List<Dictionary<string, object>> ReadCylinderRecoveryBackups(CylinderJob source)
        {
            var found = new List<Dictionary<string, object>>();
            object imported;
            if (source.result.TryGetValue("import_result", out imported))
                foreach (var item in CylinderWritePipeline.Rows(CylinderWritePipeline.Map(imported), "items")
                    .Concat(CylinderWritePipeline.Rows(CylinderWritePipeline.Map(imported), "related_items"))) {
                    string path = Text(item, "backup");
                    if (path.Length > 0) found.Add(new Dictionary<string, object> { { "block_name", Text(item, "block_name") }, { "group_path", Text(item, "group_path") }, { "path", path } });
                }
            found.AddRange(CylinderWritePipeline.Rows(source.result, "instance_db_backups"));
            // A save-only follow-up still carries the checked objects' own backups.
            found.AddRange(CylinderWritePipeline.Rows(source.result, "recovery_backups"));
            var result = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
            foreach (var item in found) {
                string path = Text(item, "path");
                if (!File.Exists(path)) throw new InvalidOperationException("找不到本次任务的备份：" + Text(item, "block_name"));
                string xml = File.ReadAllText(path, Encoding.UTF8), name = CylinderRecoveryPolicy.Name(xml);
                var metadata = BackupRecord.Read(path, xml, source.project, source.plc);
                if (metadata == null || metadata.Kind != "Block" || !string.Equals(metadata.Name, name, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(name, Text(item, "block_name"), StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(metadata.GroupPath, PathTools.NormalizeGroupPath(Text(item, "group_path")), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("备份身份或完整性无法验证：" + Text(item, "block_name"));
                var row = new Dictionary<string, object> { { "xml", xml }, { "block_name", name }, { "group_path", metadata.GroupPath }, { "path", path } };
                string key = CylinderRecoveryPolicy.Key(row);
                if (result.ContainsKey(key) && XmlBatch.RecoverySignature(Text(result[key], "xml")) != XmlBatch.RecoverySignature(xml))
                    throw new InvalidOperationException("同一对象存在不一致的恢复备份：" + name);
                result[key] = row;
            }
            // Preserve the original dependency order, with instance DBs after their host FB.
            var order = (source.plan_items ?? new List<Dictionary<string, object>>()).Select(i => CylinderRecoveryPolicy.Key(CylinderRecoveryPolicy.Name(Text(i, "xml")), Text(i, "group_path"))).ToArray();
            return result.Values.OrderBy(i => { int position = Array.IndexOf(order, CylinderRecoveryPolicy.Key(i)); return position < 0 ? int.MaxValue : position; }).ToList();
        }

        private Dictionary<string, object> CylinderCheckResult(CylinderJob source, CylinderJob check, CylinderRecoveryAssessment assessment)
        {
            bool savedOnly = assessment.ContinueItems.Count == 0 && CylinderCompilePassed(source);
            return new Dictionary<string, object> {
                { "source_id", source.id }, { "check_id", check.id }, { "can_continue", assessment.CanContinue && source.state != "completed" },
                { "can_restore", assessment.CanRestore && source.state != "completed" }, { "reason", assessment.Reason }, { "items", assessment.Items.ToArray() },
                { "continue_steps", savedOnly ? new[] { "再次核对相关块、点位与后台状态", "仅保存当前工程（包括其他未保存修改）", "关联原任务并保留完整记录" } :
                    new[] { "再次核对完整工程与本次检查结果", "仅导入尚未完成的 " + assessment.ContinueItems.Count + " 个程序块", "编译、回读检查后保存当前工程" } },
                { "restore_steps", new[] { "再次核对当前内容并备份当前现场", "恢复本次改变的 " + assessment.RestoreItems.Count + " 个块（包括需要恢复的关联实例 DB）", "编译、回读检查后保存当前工程" } }
            };
        }
        private static bool CylinderCompilePassed(CylinderJob job)
        {
            object imported; int errors;
            if (!job.result.TryGetValue("import_result", out imported)) return false;
            var batch = CylinderWritePipeline.Map(imported);
            return CylinderWritePipeline.True(batch, "compiled") && int.TryParse(Text(batch, "error_count"), out errors) && errors == 0 &&
                CylinderWritePipeline.True(batch, "ok") && CylinderWritePipeline.Rows(batch, "items").All(i => Text(i, "verification") == "matched");
        }
        private string RunCylinderRecovery(CylinderJob job, CylinderJob source, CylinderJob checkedJob, Action<string> report)
        {
            bool check = job.recovery_action == "check";
            try {
                CylinderProgress(job, "preflight", "正在只读核对工程目录、程序块、关联实例 DB 与点位", report);
                var fresh = new IoJob { project = job.project, plc = job.plc, cached = false, report = detail => CylinderProgress(job, "preflight", detail, report) };
                ReadIo(fresh);
                var store = new ProjectIndexStore(job.project, AppConfig.DetectProjectVersion(job.project));
                var signatures = SnapshotCylinderSignatures(fresh.result, store.IoSource);
                job.source_signatures = signatures;
                List<Dictionary<string, object>> backups = null;
                CylinderRecoveryAssessment assessment;
                try {
                    backups = ReadCylinderRecoveryBackups(source);
                    assessment = CylinderRecoveryPolicy.Evaluate(source.plan_items, source.source_signatures, source.post_signatures, signatures, backups);
                } catch (Exception ex) { assessment = new CylinderRecoveryAssessment { Reason = ex.Message }; }
                if (check) {
                    var result = CylinderCheckResult(source, job, assessment);
                    lock (ioGate) {
                        job.result["recovery_check"] = result; job.state = "completed"; job.stage = "checked"; job.message = assessment.Reason;
                        source.result["latest_check"] = result; PersistCylinder(job); PersistCylinder(source);
                    }
                    return assessment.CanContinue || assessment.CanRestore ? null : assessment.Reason;
                }
                if (checkedJob == null || !CylinderRecoveryPolicy.Same(checkedJob.source_signatures, signatures))
                    throw new InvalidOperationException("工程在核对之后又发生变化，本次未写入。请重新核对差异。");
                bool continuing = job.recovery_action == "continue";
                if (!(continuing ? assessment.CanContinue : assessment.CanRestore)) throw new InvalidOperationException(assessment.Reason);
                ResolveCylinderRecoveryOwner(source);
                var items = continuing ? assessment.ContinueItems : assessment.RestoreItems;
                var current = CylinderRecoveryPolicy.Signatures(signatures);
                // Preserve all original targets in a save-only task so a later save failure remains recoverable.
                job.plan_items = items.Count > 0 ? items : CylinderRecoveryPolicy.GuardedItems(continuing ? source.plan_items : backups, current);
                CylinderProgress(job, "backup", "正在备份当前现场及关联实例 DB", report);
                var recoveryBackups = new List<Dictionary<string, object>>();
                string directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_configPath)), "block-backups", "recovery-" + job.id);
                foreach (var original in backups) {
                    string key = CylinderRecoveryPolicy.Key(original);
                    var entry = fresh.result.cylinderBlocks.Single(e => CylinderRecoveryPolicy.Key(e.name, e.group) == key);
                    string xml = store.IoSource(entry);
                    string path = BackupRecord.Write(directory, "current-" + recoveryBackups.Count, xml, job.project, job.plc, entry.group, "Block", entry.name);
                    recoveryBackups.Add(new Dictionary<string, object> { { "block_name", entry.name }, { "group_path", entry.group }, { "path", path }, { "hash", XmlBatch.Hash(xml) } });
                }
                lock (ioGate) { job.result["recovery_backups"] = recoveryBackups.ToArray(); PersistCylinder(job); }
                object prior;
                CylinderRecoveryWritePipeline.Run(items, job.plan_items, job.project, job.plc, job.result,
                    source.result.TryGetValue("import_result", out prior) ? CylinderWritePipeline.Map(prior) : null,
                    continuing && CylinderCompilePassed(source), (name, args) => {
                        lock (ioGate) { job.write_started = true; PersistCylinder(job); }
                        return CylinderCall(job, name, args, report);
                    }, () => {
                        var readback = new IoJob { project = job.project, plc = job.plc, cached = false };
                        ReadIo(readback);
                        var now = SnapshotCylinderSignatures(readback.result, store.IoSource);
                        if (!CylinderRecoveryPolicy.Same(signatures, now)) throw new CylinderReviewException("编译后的内容发生变化，需要重新核对；未保存。", true);
                    }, detail => CylinderProgress(job, detail.Contains("正在保存") ? "save" : detail.Contains("重新编译") ? "compile" : "write", detail, report));
                CaptureCylinderPost(job, report);
                lock (ioGate) {
                    job.state = "completed"; job.stage = "finished";
                    job.message = continuing ? "后续处理完成，工程已保存；原失败记录已关联。" : "本次变更已恢复，编译及回读通过，工程已保存。";
                    source.resolution_id = job.id; source.result["resolution_action"] = job.recovery_action;
                    PersistCylinder(job); PersistCylinder(source);
                    foreach (var old in ioJobs.Values.Where(j => PathsEqual(j.project, job.project) && j.plc == job.plc)) consumedCylinderSnapshots.Add(old.id);
                }
                return null;
            } catch (Exception ex) {
                if (!check && job.write_started && !(ex is EngineeringOutcomeUnknownException) && job.result.ContainsKey("import_result")) CaptureCylinderPost(job, report);
                lock (ioGate) {
                    job.state = job.write_started ? "needs_review" : "failed"; job.stage = "finished"; job.message = job.error = ex.Message;
                    PersistCylinder(job);
                }
                throw;
            }
        }
    }
}
