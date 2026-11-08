using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Xml;
using TiaPortalSourceBridge.Configuration;
using TiaPortalSourceBridge.Indexing;
using TiaPortalSourceBridge.Mcp;
using TiaPortalSourceBridge.Openness;

namespace TiaPortalSourceBridge.Web
{
    internal sealed class CylinderReviewException : Exception
    {
        internal readonly bool MayHaveChanged;
        internal CylinderReviewException(string message, bool mayHaveChanged) : base(message) { MayHaveChanged = mayHaveChanged; }
    }

    // Kept separate from HTTP and Openness so success/partial-write/save behaviour
    // can be exercised with a fake transport, without touching an engineering project.
    internal static class CylinderWritePipeline
    {
        internal static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024, RecursionLimit = 128 }; }
        internal static Dictionary<string, object> Map(object value)
        { return value as Dictionary<string, object> ?? (value == null ? new Dictionary<string, object>() : Json().Deserialize<Dictionary<string, object>>(Json().Serialize(value))); }
        internal static string Text(Dictionary<string, object> map, string key)
        { object value; return map != null && map.TryGetValue(key, out value) ? Convert.ToString(value) : ""; }
        internal static bool True(Dictionary<string, object> map, string key)
        { object value; return map != null && map.TryGetValue(key, out value) && value is bool && (bool)value; }
        internal static Dictionary<string, object> Data(Dictionary<string, object> reply)
        { object value; return reply != null && reply.TryGetValue("structuredContent", out value) ? Map(value) : new Dictionary<string, object>(); }
        internal static string Error(Dictionary<string, object> reply)
        {
            object content;
            var rows = reply != null && reply.TryGetValue("content", out content) ? content as IEnumerable : null;
            return rows == null ? "未收到完整的操作结果" : string.Join("；", rows.Cast<object>().Select(Map).Select(m => Text(m, "text")).Where(t => t.Length > 0));
        }
        internal static Dictionary<string, object>[] Rows(Dictionary<string, object> map, string key)
        { object value; var rows = map.TryGetValue(key, out value) ? value as IEnumerable : null; return rows == null ? new Dictionary<string, object>[0] : rows.Cast<object>().Select(Map).ToArray(); }

        internal static void Run(IList<Dictionary<string, object>> items, string project, string plc,
            Dictionary<string, object> result, Func<string, Dictionary<string, object>, Dictionary<string, object>> call,
            Action<string> report)
        {
            if (items == null || items.Count == 0 || items.Count > 50 || items.Any(i => Text(i, "expected_content_signature").Length != 64 && Text(i, "expected_recovery_signature").Length != 64))
                throw new InvalidOperationException("生成方案缺少完整的原块检查信息；尚未写入。");
            report("正在备份并写入气缸、点位命名和映射，随后统一编译与回读");
            var reply = call("tia_import_blocks_batch", new Dictionary<string, object> {
                { "project_path", project }, { "plc_name", plc }, { "items_json", Json().Serialize(items) },
                { "confirm", true }, { "compile_mode", "plc" }, { "response_mode", "compact" }
            });
            var imported = Data(reply); lock (result) result["import_result"] = imported;
            var rows = Rows(imported, "items");
            int errors;
            bool verified = !True(reply, "isError") && True(imported, "ok") && True(imported, "compiled") &&
                int.TryParse(Text(imported, "error_count"), out errors) && errors == 0 && rows.Length == items.Count &&
                rows.All(r => Text(r, "status") == "imported" && Text(r, "verification") == "matched");
            if (!verified) {
                bool mayHaveChanged = rows.Length == 0 || rows.Any(r => new[] { "importing", "imported", "outcome_unknown" }.Contains(Text(r, "status")));
                string reason = Text(imported, "error");
                if (reason.Length == 0) reason = Error(reply);
                throw new CylinderReviewException((mayHaveChanged ? "写入结果需要检查，未自动保存。" : "检查或备份未通过，尚未写入。") + reason, mayHaveChanged);
            }
            report("所有块已写入，编译和回读检查通过；正在保存当前工程");
            var savedReply = call("tia_save_project", new Dictionary<string, object> {
                { "project_path", project }, { "confirm", true }, { "response_mode", "compact" }
            });
            var saved = Data(savedReply); lock (result) result["save_result"] = saved;
            if (True(savedReply, "isError") || !True(saved, "saved"))
                throw new CylinderReviewException("块已写入并通过编译和回读，但保存未确认成功。" + Error(savedReply), true);
            lock (result) result["saved"] = true;
        }
    }

    internal sealed partial class WebServer
    {
        private sealed class CylinderJob
        {
            public string id, request_hash, snapshot_id, project, plc, state = "running", stage = "preflight", message = "准备检查并生成气缸", error = "", updated;
            public string started, cylinder_name, instance_name, parent_id, recovery_action, resolution_id;
            public List<Dictionary<string, object>> events = new List<Dictionary<string, object>>();
            public List<Dictionary<string, object>> plan_items = new List<Dictionary<string, object>>();
            public List<Dictionary<string, object>> source_signatures = new List<Dictionary<string, object>>();
            public List<Dictionary<string, object>> post_signatures = new List<Dictionary<string, object>>();
            public Dictionary<string, object> result = new Dictionary<string, object> { { "saved", false } };
            public bool write_started;
            public bool can_check { get { return state != "running" && recovery_action != "check" && state != "completed"; } }
            public CylinderJob() { }
        }
        private readonly Dictionary<string, CylinderJob> cylinderJobs = new Dictionary<string, CylinderJob>();
        private readonly HashSet<string> consumedCylinderSnapshots = new HashSet<string>();

        private static string CylinderRequestId(string value)
        { Guid id; return Guid.TryParse(value, out id) && id != Guid.Empty ? id.ToString("N") : ""; }
        private string CylinderReceiptPath(string id)
        { return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_configPath)), "logs", "cylinder-jobs", id + ".json"); }
        private void PersistCylinder(CylinderJob job)
        {
            job.updated = DateTime.UtcNow.ToString("o");
            if (string.IsNullOrEmpty(job.started)) job.started = job.updated;
            if (job.events == null) job.events = new List<Dictionary<string, object>>();
            var last = job.events.LastOrDefault();
            if (last == null || Text(last, "stage") != job.stage || Text(last, "message") != job.message || Text(last, "state") != job.state) {
                job.events.Add(new Dictionary<string, object> { { "at", job.updated }, { "stage", job.stage }, { "state", job.state }, { "message", job.message } });
            }
            string path = CylinderReceiptPath(job.id);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            lock (job.result) File.WriteAllText(path + ".tmp", CylinderWritePipeline.Json().Serialize(job), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(path + ".tmp", path, null); else File.Move(path + ".tmp", path);
        }
        private CylinderJob FindCylinder(string id)
        {
            CylinderJob job;
            if (cylinderJobs.TryGetValue(id, out job)) return job;
            string path = CylinderReceiptPath(id);
            if (!File.Exists(path)) return null;
            job = CylinderWritePipeline.Json().Deserialize<CylinderJob>(File.ReadAllText(path));
            if (job == null || job.id != id) throw new InvalidOperationException("气缸操作记录损坏；请检查工程与备份后再操作。");
            // Old receipts do not include creation time. Preserve their known timestamp.
            if (string.IsNullOrEmpty(job.started)) job.started = job.updated;
            if (job.state == "running") {
                job.state = "needs_review"; job.stage = "interrupted";
                job.error = job.message = "服务已重新启动，未收到上次操作的最终结果；请检查博途工程和备份，不要重复生成。";
                PersistCylinder(job);
            }
            cylinderJobs[id] = job;
            consumedCylinderSnapshots.Add(job.snapshot_id);
            return job;
        }
        private static object CanonicalCylinderValue(object value)
        {
            var map = value as Dictionary<string, object>;
            if (map != null) return new SortedDictionary<string, object>(map.ToDictionary(p => p.Key, p => CanonicalCylinderValue(p.Value)), StringComparer.Ordinal);
            var array = value as object[];
            return array == null ? value : array.Select(CanonicalCylinderValue).ToArray();
        }
        internal static IList<Dictionary<string, object>> CylinderRequests(Dictionary<string, object> request)
        {
            object value;
            if (!request.TryGetValue("batch", out value)) return new[] { request };
            var array = value as IEnumerable;
            if (array == null || value is string || value is IDictionary)
                throw new InvalidOperationException("批量气缸参数必须是列表。");
            var rows = array.Cast<object>().ToArray();
            if (rows.Length == 0 || rows.Length > CylinderBatchPlan.MaxRows)
                throw new InvalidOperationException("每批请填写 1～" + CylinderBatchPlan.MaxRows + " 个气缸。");
            var keys = new[] { "cylinder_name", "instance_name", "alarm_index", "bindings" };
            var result = new List<Dictionary<string, object>>();
            foreach (var item in rows) {
                var row = item as Dictionary<string, object>;
                if (row == null || row.Keys.Any(k => !keys.Contains(k)) || keys.Any(k => !row.ContainsKey(k)) || !(row["bindings"] is Dictionary<string, object>))
                    throw new InvalidOperationException("每行只能填写气缸名称、实例名、数组索引和点位，整批共用当前样板。");
                var merged = new Dictionary<string, object>(request); merged.Remove("batch");
                foreach (string key in keys) merged[key] = row[key];
                result.Add(merged);
            }
            return result;
        }
        private bool HandleCylinderBatchPreview(HttpListenerContext context, string path)
        {
            if (path != "/api/io/cylinder-batch-preview") return false;
            var request = context.Request; var response = context.Response;
            if (request.HttpMethod != "POST") { SendJson(response, 405, new { error = "不支持的请求方法" }); return true; }
            if (request.ContentLength64 < 0 || request.ContentLength64 > 262144) { SendJson(response, 413, new { error = "请求过大" }); return true; }
            try {
                var map = CylinderWritePipeline.Json().Deserialize<Dictionary<string, object>>(ReadBody(request));
                if (map == null || !map.ContainsKey("batch")) throw new InvalidOperationException("请先加入批量气缸清单。");
                var rows = CylinderRequests(map);
                lock (ioGate) {
                    IoJob source;
                    if (!ioJobs.TryGetValue(Text(map, "id"), out source) || source.state != "completed" || source.result == null ||
                        !PathsEqual(source.project, Text(map, "project_path")) || source.plc != Text(map, "plc_name") || consumedCylinderSnapshots.Contains(source.id))
                        throw new InvalidOperationException("源码快照已失效，请重新读取。");
                    var host = source.result.cylinderBlocks.SingleOrDefault(b => b.id == Text(map, "host"));
                    if (host == null || host.content_hash != Text(map, "source_hash")) throw new InvalidOperationException("样板已改变，请重新选择。");
                    var store = new ProjectIndexStore(source.project, AppConfig.DetectProjectVersion(source.project));
                    var plan = CylinderBatchPlan.Build(source.result, host, store.IoSource, rows);
                    SendJson(response, 200, new { summary = plan.Summary });
                }
            } catch (Exception ex) { SendJson(response, 400, new { error = ex.Message }); }
            return true;
        }
        private bool HandleCylinderApply(HttpListenerContext context, string path)
        {
            if (path != "/api/io/cylinder-apply" && path != "/api/io/cylinder-apply-status") return false;
            var request = context.Request; var response = context.Response;
            if (path == "/api/io/cylinder-apply-status" && request.HttpMethod == "GET") {
                string id = CylinderRequestId(GetQueryParam(request, "id"));
                if (id.Length == 0) id = CylinderRequestId(GetQueryParam(request, "request_id"));
                lock (ioGate) {
                    var job = id.Length == 0 ? null : FindCylinder(id);
                    if (job == null) SendJson(response, 404, new { error = "没有找到本次生成记录；请重新读取工程检查结果，不要重复提交旧方案。" });
                    else lock (job.result) SendJson(response, 200, PublicCylinderJob(job));
                }
                return true;
            }
            if (path != "/api/io/cylinder-apply" || request.HttpMethod != "POST") { SendJson(response, 405, new { error = "不支持的请求方法" }); return true; }
            if (shutdownRequested) { SendJson(response, 503, new { error = "服务正在退出" }); return true; }
            if (request.ContentLength64 < 0 || request.ContentLength64 > 262144) { SendJson(response, 413, new { error = "请求过大" }); return true; }
            Dictionary<string, object> map;
            try { map = CylinderWritePipeline.Json().Deserialize<Dictionary<string, object>>(ReadBody(request)); }
            catch (ArgumentException) { SendJson(response, 400, new { error = "生成参数格式无效" }); return true; }
            if (map == null) { SendJson(response, 400, new { error = "缺少生成参数" }); return true; }
            IList<Dictionary<string, object>> rows;
            try { rows = CylinderRequests(map); }
            catch (InvalidOperationException ex) { SendJson(response, 400, new { error = ex.Message }); return true; }
            string project = Text(map, "project_path"), plc = Text(map, "plc_name"), requestId = CylinderRequestId(Text(map, "request_id"));
            if (requestId.Length == 0) { SendJson(response, 400, new { error = "缺少有效的操作编号" }); return true; }
            string hash = XmlBatch.Hash(CylinderWritePipeline.Json().Serialize(CanonicalCylinderValue(map)));
            lock (ioGate) {
                var previous = FindCylinder(requestId);
                if (previous != null) {
                    if (previous.request_hash != hash) SendJson(response, 409, new { error = "操作编号已经用于另一份方案；未再次执行" });
                    else SendJson(response, 202, new { id = previous.id, state = previous.state, resumed = true });
                    return true;
                }
                if (plc.Length == 0 || !File.Exists(project) || !actionTargets.Verify(project, plc, Text(map, "target_token"))) {
                    SendJson(response, 409, new { error = "请先选择有效工程与 PLC，等待识别完成后重新读取" }); return true;
                }
                if (AppConfig.DetectProjectVersion(project) == "V21") {
                    SendJson(response, 409, new { error = "V21 的气缸一键写入尚未开放，请使用方案预览与下载。" }); return true;
                }
                IoJob source;
                if (!ioJobs.TryGetValue(Text(map, "id"), out source) || source.state != "completed" || source.result == null ||
                    !PathsEqual(source.project, project) || source.plc != plc) {
                    SendJson(response, 409, new { error = "源码快照已失效或与当前工程不一致，请重新读取" }); return true;
                }
                if (consumedCylinderSnapshots.Contains(source.id)) {
                    SendJson(response, 409, new { error = "这份源码已提交过生成操作，请等待结果并重新读取工程" }); return true;
                }
                var host = (source.result.cylinderBlocks ?? new IndexEntry[0]).SingleOrDefault(b => b.id == Text(map, "host"));
                if (host == null || host.language == "DB" || host.content_hash != Text(map, "source_hash")) {
                    SendJson(response, 409, new { error = "样板已改变，请重新选择宿主程序块并生成方案" }); return true;
                }
                var job = new CylinderJob { id = requestId, request_hash = hash, snapshot_id = source.id, project = project, plc = plc,
                    cylinder_name = map.ContainsKey("batch") ? "批量生成 " + rows.Count + " 个气缸" : Text(map, "cylinder_name"),
                    instance_name = string.Join("、", rows.Select(r => Text(r, "instance_name"))) };
                // Persist before starting the task. A lost HTTP response must never
                // cause a duplicate write; GET status can recover this operation.
                PersistCylinder(job); cylinderJobs[job.id] = job; consumedCylinderSnapshots.Add(source.id);
                bool started = ActionRunner.StartEngineeringTask(_configPath, project, plc, "生成并写入气缸", job.message,
                    report => RunCylinder(job, source.result, host, map, report), job.id);
                if (!started) {
                    job.state = "failed"; job.error = job.message = "已有工程任务正在执行；本次尚未写入，请稍后重新读取再生成。";
                    PersistCylinder(job);
                    SendJson(response, 409, new { id = job.id, error = job.error }); return true;
                }
                SendJson(response, 202, new { id = job.id }); return true;
            }
        }
        private void CylinderProgress(CylinderJob job, string stage, string message, Action<string> report)
        {
            lock (ioGate) { job.stage = stage; job.message = message; PersistCylinder(job); }
            report(message);
        }
        internal static void SameCylinderSources(IoSnapshot original, IoSnapshot fresh, Func<IndexEntry, string> source, Dictionary<string, object> bindings)
        {
            if (bindings == null || bindings.Count == 0) throw new InvalidOperationException("请选择气缸输入和输出点位；尚未写入。");
            var oldUsage = new CylinderIoUsage(original, source);
            var newUsage = new CylinderIoUsage(fresh, source);
            foreach (string pointId in bindings.Values.Select(Convert.ToString)) {
                var oldRows = original.rows.Where(r => r.id == pointId).ToArray();
                var newRows = fresh.rows.Where(r => r.id == pointId).ToArray();
                if (oldRows.Length != 1 || newRows.Length != 1)
                    throw new InvalidOperationException("已选点位已变化或不再唯一，请重新读取并选择；尚未写入。");
                var a = oldRows[0]; var b = newRows[0];
                if (IoTable.Address(a.address) != IoTable.Address(b.address) || a.area != b.area || a.data_type != b.data_type ||
                    a.name != b.name || a.unit != b.unit || a.group != b.group || a.table != b.table ||
                    a.mapping_fc != b.mapping_fc || a.mapping_group != b.mapping_group ||
                    oldUsage.PreferredSymbol(a) != newUsage.PreferredSymbol(b))
                    throw new InvalidOperationException("已选点位的地址或映射已变化（" + a.address + "），请重新读取并核对方案；尚未写入。");
            }
            var before = original.cylinderBlocks ?? new IndexEntry[0];
            var after = fresh.cylinderBlocks ?? new IndexEntry[0];
            if (before.Length != after.Length || before.Any(b => !after.Any(a => a.id == b.id)))
                throw new InvalidOperationException("工程程序目录已变化，请重新读取并核对生成方案；尚未写入。");
            foreach (var entry in before) {
                var current = after.Single(b => b.id == entry.id);
                if (XmlBatch.ContentSignature(source(entry)) != XmlBatch.ContentSignature(source(current)))
                    throw new InvalidOperationException("工程源码已变化（" + entry.name + "），请重新读取并核对方案；尚未写入。");
            }
        }
        internal static Dictionary<string, object>[] BackupCylinderInstances(string configPath, string jobId,
            IoSnapshot snapshot, IndexEntry host, Func<IndexEntry, string> source)
        {
            Guid operation;
            if (!Guid.TryParse(jobId, out operation) || operation == Guid.Empty)
                throw new InvalidOperationException("气缸备份操作编号无效；尚未写入。");
            var matches = new List<KeyValuePair<IndexEntry, string>>();
            foreach (var entry in snapshot.cylinderBlocks.Where(b => b.language == "DB" && (b.unit_name ?? "") == (host.unit_name ?? ""))) {
                string xml = source(entry);
                var document = BlockOperations.ParseBlock(xml);
                var attributes = document.SelectSingleNode("/Document/SW.Blocks.InstanceDB/AttributeList");
                if (attributes == null) continue;
                Func<string, string> attribute = key => { var node = attributes.SelectSingleNode(key); return node == null ? "" : node.InnerText.Trim(); };
                string baseName = attribute("InstanceOfName"), baseNumber = attribute("InstanceOfNumber"), baseType = attribute("InstanceOfType");
                bool nameMatches = string.Equals(baseName, host.name, StringComparison.OrdinalIgnoreCase);
                int expectedNumber, actualNumber;
                bool numberMatches = int.TryParse(host.number, out expectedNumber) && int.TryParse(baseNumber, out actualNumber) && expectedNumber == actualNumber;
                if (!nameMatches && !(baseName.Length == 0 && numberMatches)) continue;
                if ((baseType.Length > 0 && baseType != "FB") || (baseNumber.Length > 0 && !numberMatches))
                    throw new InvalidOperationException("实例 DB 的宿主信息不一致，无法可靠备份：" + entry.name + "；尚未写入。");
                matches.Add(new KeyValuePair<IndexEntry, string>(entry, xml));
            }
            if (matches.Count == 0) return new Dictionary<string, object>[0];
            string directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath)), "block-backups", "cylinder-" + operation.ToString("N"));
            var backups = new List<Dictionary<string, object>>();
            foreach (var match in matches) {
                // Compilation regenerates these instance DB interfaces. Save their
                // exact fresh exports, including declared start values, but never
                // import an unchanged instance DB alongside the expanded host FB.
                string path = BackupRecord.Write(directory, "instance-" + backups.Count.ToString("D3"), match.Value,
                    snapshot.project_path, snapshot.plc_name, match.Key.group, "Block", match.Key.name);
                backups.Add(new Dictionary<string, object> { { "block_name", match.Key.name }, { "path", path },
                    { "hash", XmlBatch.Hash(match.Value) }, { "group_path", match.Key.group } });
            }
            File.WriteAllText(Path.Combine(directory, "manifest.json"), CylinderWritePipeline.Json().Serialize(new {
                project_path = snapshot.project_path, plc_name = snapshot.plc_name, host = host.name,
                instance_db_backups = backups.ToArray()
            }), new UTF8Encoding(false));
            return backups.ToArray();
        }
        private string RunCylinder(CylinderJob job, IoSnapshot original, IndexEntry previousHost,
            Dictionary<string, object> request, Action<string> report)
        {
            try {
                CylinderProgress(job, "preflight", "正在确认当前博途工程并重新读取全部点位与程序引用", report);
                var open = ReadDashboard(_configPath, "tia_list_open_projects", new Dictionary<string, object> { { "portal_version", AppConfig.DetectProjectVersion(job.project) } });
                if (!IoTable.Maps(open, "processes").Any(p => PathsEqual(Text(p, "project_path"), job.project)))
                    throw new InvalidOperationException("目标工程尚未在博途中打开，请打开后重新读取；尚未写入。");
                var fresh = new IoJob { project = job.project, plc = job.plc, cached = false,
                    report = detail => CylinderProgress(job, "preflight", detail, report) };
                ReadIo(fresh);
                var store = new ProjectIndexStore(job.project, AppConfig.DetectProjectVersion(job.project));
                var requests = CylinderRequests(request);
                var bindings = new Dictionary<string, object>();
                for (int i = 0; i < requests.Count; i++) {
                    object selected;
                    var row = requests[i].TryGetValue("bindings", out selected) ? selected as Dictionary<string, object> : null;
                    if (row == null) throw new InvalidOperationException("请选择气缸输入和输出点位；尚未写入。");
                    foreach (var binding in row) bindings[i + ":" + binding.Key] = binding.Value;
                }
                SameCylinderSources(original, fresh.result, store.IoSource, bindings);
                var host = fresh.result.cylinderBlocks.Single(b => b.id == previousHost.id);
                // The full source comparison above accepts regenerated export IDs.
                // Plan against the checked fresh host while retaining the original
                // request and its idempotency hash unchanged.
                if (request.ContainsKey("batch")) requests = requests.Select(row => {
                    var current = new Dictionary<string, object>(row); current["source_hash"] = host.content_hash; return current;
                }).ToArray();
                CylinderProgress(job, "validate", "正在检查空闲点位、实例、命名及气缸接线，并生成写入方案", report);
                var plan = request.ContainsKey("batch") ? CylinderBatchPlan.Build(fresh.result, host, store.IoSource, requests) : CylinderApplyPlan.Build(fresh.result, host, store.IoSource, request);
                lock (ioGate) {
                    job.result["summary"] = plan.Summary;
                    job.plan_items = plan.Items.ToList();
                    job.source_signatures = SnapshotCylinderSignatures(fresh.result, store.IoSource);
                    PersistCylinder(job);
                }
                CylinderProgress(job, "backup", "正在备份编译时将更新的现有实例 DB", report);
                var instanceBackups = BackupCylinderInstances(_configPath, job.id, fresh.result, host, store.IoSource);
                lock (ioGate) { job.result["instance_db_backups"] = instanceBackups; PersistCylinder(job); }
                try {
                    CylinderWritePipeline.Run(plan.Items, job.project, job.plc, job.result, (name, args) => {
                        lock (ioGate) { job.write_started = true; PersistCylinder(job); }
                        return CylinderCall(job, name, args, report);
                    }, detail => CylinderProgress(job, detail.Contains("正在保存") ? "save" : "write", detail, report));
                } finally {
                    // No new engineering calls after an unconfirmed transport outcome.
                    if (job.result.ContainsKey("import_result") && !job.result.ContainsKey("outcome_unknown")) CaptureCylinderPost(job, report);
                }
                lock (ioGate) {
                    job.state = "completed"; job.stage = "finished";
                    int warningCount;
                    var batch = CylinderWritePipeline.Map(job.result["import_result"]);
                    job.message = (request.ContainsKey("batch") ? requests.Count + " 个气缸" : "气缸") + "已生成并写入博途，编译及回读检查通过，工程已保存。";
                    if (int.TryParse(Text(batch, "warning_count"), out warningCount) && warningCount > 0) job.message += "编译警告 " + warningCount + " 项，请查看详情。";
                    PersistCylinder(job);
                }
                // All snapshots for this PLC now describe an older engineering state.
                lock (ioGate) foreach (var old in ioJobs.Values.Where(j => PathsEqual(j.project, job.project) && j.plc == job.plc)) consumedCylinderSnapshots.Add(old.id);
                report(job.message);
                var imported = CylinderWritePipeline.Map(job.result["import_result"]);
                int warnings;
                return int.TryParse(Text(imported, "warning_count"), out warnings) && warnings > 0 ? job.message + "编译警告 " + warnings + " 项，请查看编译报告。" : null;
            }
            catch (Exception ex) {
                var review = ex as CylinderReviewException;
                bool changed = review == null ? job.write_started : review.MayHaveChanged;
                lock (ioGate) {
                    job.state = changed ? "needs_review" : "failed"; job.stage = "finished";
                    job.error = job.message = ex.Message;
                    // Preserve the import journal and backups already received.
                    PersistCylinder(job);
                    foreach (var old in ioJobs.Values.Where(j => PathsEqual(j.project, job.project) && j.plc == job.plc)) consumedCylinderSnapshots.Add(old.id);
                }
                throw;
            }
        }
        private Dictionary<string, object> CylinderCall(CylinderJob job, string name, Dictionary<string, object> args, Action<string> report)
        {
            args = new Dictionary<string, object>(args) { { "bridge_operation_id", job.id } };
            if (name == "tia_import_blocks_batch") {
                var imported = XmlBatch.Parse(Text(args, "items_json"));
                var written = new HashSet<string>(imported.Select(i => CylinderRecoveryPolicy.Key(i.block_name, i.group_path)), StringComparer.Ordinal);
                var signatures = CylinderRecoveryPolicy.Signatures(job.source_signatures);
                var related = CylinderWritePipeline.Rows(job.result, "instance_db_backups").Concat(CylinderWritePipeline.Rows(job.result, "recovery_backups"))
                    .Where(r => !written.Contains(CylinderRecoveryPolicy.Key(r))).GroupBy(CylinderRecoveryPolicy.Key).Select(g => g.First()).Select(r => {
                        string key = CylinderRecoveryPolicy.Key(r);
                        if (!signatures.ContainsKey(key)) throw new InvalidOperationException("关联对象缺少执行前核对证据：" + Text(r, "block_name"));
                        return new Dictionary<string, object> { { "block_name", Text(r, "block_name") }, { "group_path", Text(r, "group_path") },
                            { "expected_recovery_signature", signatures[key] } };
                    }).ToArray();
                if (related.Length > 0) args["related_blocks_json"] = CylinderWritePipeline.Json().Serialize(related);
            }
            IDisposable observer = name == "tia_import_blocks_batch" ? ObserveCylinderBatch(job, report) : null;
            try { lock (DashboardGate) return Dashboard(_configPath).DashboardCylinderCall(name, args); }
            catch (TimeoutException) {
                CylinderProgress(job, "waiting", "等待博途结果超时，后台仍可能执行；正在等待最终结果，请勿重复提交", report);
                while (true) {
                    Thread.Sleep(2000);
                    Dictionary<string, object> reply;
                    try { lock (DashboardGate) reply = Dashboard(_configPath).DashboardCylinderCall("tia_connection_status", new Dictionary<string, object> { { "project_path", job.project } }); }
                    catch (Exception ex) { job.result["outcome_unknown"] = true; throw new EngineeringOutcomeUnknownException("无法确认博途的最终结果；请检查工程和备份，不要重复生成。" + ex.Message, ex); }
                    var status = CylinderWritePipeline.Data(reply);
                    if (Text(status, "worker_state") == "busy_outcome_unknown") continue;
                    object late;
                    if (Text(status, "late_tool") == name && status.TryGetValue("late_result", out late)) {
                        var result = late as Dictionary<string, object>;
                        if (result != null && result.ContainsKey("isError")) return result;
                    }
                    job.result["outcome_unknown"] = true;
                    throw new EngineeringOutcomeUnknownException("未收到博途操作的完整最终结果；请检查工程和备份，不要重复生成。");
                }
            }
            catch (Exception ex) { job.result["outcome_unknown"] = true; throw new EngineeringOutcomeUnknownException("与博途的连接异常，操作结果未确认；请检查工程和备份，不要重复生成。" + ex.Message, ex); }
            finally { if (observer != null) observer.Dispose(); }
        }
    }
}
