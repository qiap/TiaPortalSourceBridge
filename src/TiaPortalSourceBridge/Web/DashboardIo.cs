using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using TiaPortalSourceBridge.Configuration;
using TiaPortalSourceBridge.Indexing;

namespace TiaPortalSourceBridge.Web
{
    internal sealed partial class WebServer
    {
        private sealed class IoJob
        {
            public string id, project, plc, state = "reading", message = "准备读取", error = "";
            public IoSnapshot result;
        }
        private readonly object ioGate = new object();
        private readonly Dictionary<string, IoJob> ioJobs = new Dictionary<string, IoJob>();
        private void IoProgress(IoJob job, string message) { lock (ioGate) job.message = message; }
        private void ReadIo(IoJob job)
        {
            try {
                var result = new IoSnapshot { project_path = job.project, plc_name = job.plc, read_started_utc = DateTime.UtcNow.ToString("o") };
                Func<Dictionary<string, object>> args = () => new Dictionary<string, object> { { "project_path", job.project }, { "plc_name", job.plc }, { "response_mode", "compact" } };
                var tables = IoTable.Maps(ReadDashboard(_configPath, "tia_list_tag_tables", args()), "tag_tables").ToArray();
                result.tables_total = tables.Length;
                foreach (var table in tables) {
                    string name = Text(table, "name"), group = Text(table, "group_path"), unit = Text(table, "unit_name");
                    IoProgress(job, "变量表 " + (result.tables_read + 1) + "/" + tables.Length + "：" + name);
                    try {
                        var a = args(); a["table_name"] = name; a["group_path"] = group.Length == 0 ? "/" : group; a["unit_name"] = unit;
                        result.rows.AddRange(IoTable.Parse(Text(ReadDashboard(_configPath, "tia_read_tag_table", a), "xml"), name, group, unit));
                        result.tables_read++;
                    } catch (Exception ex) { result.warnings.Add("变量表 " + unit + "/" + group + "/" + name + " 读取失败：" + ex.Message); }
                }
                bool native = AppConfig.DetectProjectVersion(job.project) == "V21";
                if (native) {
                    result.reference_source = "V21 官方交叉引用";
                    result.reference_coverage = "逐个变量表读取官方引用；仅在同一变量表和软件单元内关联精确符号或地址。无匹配结果不证明未使用。";
                    foreach (var table in tables) {
                        string unit = Text(table, "unit_name"), name = Text(table, "name"), group = Text(table, "group_path");
                        IoProgress(job, "读取 V21 官方交叉引用：" + unit + "/" + group + "/" + name);
                        try {
                            var a = args(); a["scope_kind"] = "tag_table"; a["target_name"] = name; a["group_path"] = group.Length == 0 ? "/" : group;
                            a["unit_name"] = unit; a["max_results"] = 2000;
                            IoTable.AttachNative(result, ReadDashboard(_configPath, "tia_query_cross_references", a), unit, name, group);
                        } catch (Exception ex) { result.warnings.Add("官方交叉引用 " + unit + "/" + group + "/" + name + " 读取失败：" + ex.Message); }
                    }
                } else {
                    result.reference_source = "索引显式引用";
                    result.reference_coverage = "尚未完成引用读取；零条证据不等于未使用。";
                    try {
                        IoProgress(job, "重新扫描工程目录，准备更新 PLC 引用");
                        var a = args(); a["detail_level"] = "catalog"; a["refresh_catalog"] = true; a["include_hardware"] = false;
                        ReadDashboard(_configPath, "tia_build_project_index", a);
                        var store = new ProjectIndexStore(job.project, AppConfig.DetectProjectVersion(job.project));
                        store.RefreshIoContent(job.plc);
                        int batches = 0;
                        while (true) {
                            IoProgress(job, "重新读取 PLC 程序块引用，第 " + (++batches) + " 批（只读，不编译或保存）");
                            a = args(); a["detail_level"] = "content"; a["kind_filter"] = "plc.block"; a["owner_filter"] = job.plc;
                            a["max_objects"] = 100; a["time_budget_ms"] = 15000;
                            var batch = ReadDashboard(_configPath, "tia_build_project_index", a);
                            if (Text(batch, "stop_reason") == "completed") break;
                            if (batches >= 1000 || Text(batch, "updated") == "0" && Text(batch, "failed") == "0") {
                                result.warnings.Add("引用读取未全部完成：批次无进展或达到读取上限。"); break;
                            }
                        }
                        IoTable.AttachIndex(result, IndexJson.Map(store.IoReferenceSnapshot(job.plc)));
                    } catch (Exception ex) { result.warnings.Add("引用读取失败：" + ex.Message); }
                }
                result.rows = result.rows.OrderBy(r => r.area).ThenBy(IoTable.SortAddress).ThenBy(r => r.name).ToList();
                result.read_completed_utc = DateTime.UtcNow.ToString("o");
                lock (ioGate) { job.result = result; job.state = "completed"; job.message = result.warnings.Count == 0 ? "读取完成" : "读取完成，存在不完整项，请查看说明"; }
            } catch (Exception ex) {
                lock (ioGate) { job.state = "failed"; job.error = ex.Message; job.message = "读取失败"; }
                throw;
            }
        }
        private bool HandleIo(HttpListenerContext context, string path)
        {
            if (!path.StartsWith("/api/io/", StringComparison.Ordinal)) return false;
            var request = context.Request; var response = context.Response;
            // New endpoints are local same-origin only; no arbitrary file paths are accepted.
            response.Headers.Remove("Access-Control-Allow-Origin");
            Uri origin; string originText = request.Headers["Origin"];
            if (!string.IsNullOrEmpty(originText) && (!Uri.TryCreate(originText, UriKind.Absolute, out origin) || origin.Scheme != request.Url.Scheme || origin.Host != request.Url.Host || origin.Port != request.Url.Port)) {
                SendJson(response, 403, new { error = "仅允许本地看板访问" }); return true;
            }
            if (path == "/api/io/read" && request.HttpMethod == "POST") {
                if (shutdownRequested) { SendJson(response, 503, new { error = "服务正在退出" }); return true; }
                if (request.ContentLength64 < 0 || request.ContentLength64 > 16384) { SendJson(response, 413, new { error = "请求过大" }); return true; }
                var map = _json.Deserialize<Dictionary<string, object>>(ReadBody(request));
                string project = Text(map, "project_path"), plc = Text(map, "plc_name");
                if (plc.Length == 0 || !File.Exists(project) || !actionTargets.Verify(project, plc, Text(map, "target_token"))) {
                    SendJson(response, 409, new { error = "请先选择有效工程与 PLC，等待识别完成后重新读取" }); return true;
                }
                var job = new IoJob { id = Guid.NewGuid().ToString("N"), project = project, plc = plc };
                lock (ioGate) {
                    if (!ActionRunner.StartIoRead(project, plc, () => ReadIo(job))) { SendJson(response, 409, new { error = "已有工程任务正在执行，请稍后重新读取" }); return true; }
                    while (ioJobs.Count >= 4) ioJobs.Remove(ioJobs.First().Key);
                    ioJobs.Add(job.id, job);
                }
                SendJson(response, 202, new { id = job.id }); return true;
            }
            if (request.HttpMethod != "GET") { SendJson(response, 405, new { error = "不支持的请求方法" }); return true; }
            IoJob found;
            lock (ioGate) {
                if (!ioJobs.TryGetValue(GetQueryParam(request, "id"), out found)) { SendJson(response, 404, new { error = "读取结果已过期，请重新读取" }); return true; }
                if (path == "/api/io/status") { SendJson(response, 200, new { id = found.id, state = found.state, message = found.message, error = found.error }); return true; }
                if (found.state != "completed") { SendJson(response, 409, new { error = "读取尚未完成" }); return true; }
            }
            IoSnapshot snapshot = found.result;
            var rows = IoTable.Filter(snapshot.rows, GetQueryParam(request, "area"), GetQueryParam(request, "q"), GetQueryParam(request, "table")).ToArray();
            if (path == "/api/io/data") {
                int page; if (!int.TryParse(GetQueryParam(request, "page"), out page) || page < 1) page = 1;
                page = Math.Min(page, Math.Max(1, (rows.Length + 99) / 100));
                SendJson(response, 200, new { id = found.id, project_path = snapshot.project_path, plc_name = snapshot.plc_name, read_started_utc = snapshot.read_started_utc, read_completed_utc = snapshot.read_completed_utc,
                    tables_read = snapshot.tables_read, tables_total = snapshot.tables_total, reference_source = snapshot.reference_source, reference_coverage = snapshot.reference_coverage, warnings = snapshot.warnings,
                    total = rows.Length, total_all = snapshot.rows.Count, page = page, page_size = 100,
                    tables = snapshot.rows.Select(r => r.unit + "/" + r.group + "/" + r.table).Distinct().ToArray(),
                    rows = rows.Skip((page - 1) * 100).Take(100).Select(r => new { r.id, r.table, r.group, r.unit, r.name, r.address, r.area, r.data_type, r.comment, r.value, reference_count = r.references.Count }).ToArray() }); return true;
            }
            if (path == "/api/io/references") {
                var row = snapshot.rows.FirstOrDefault(r => r.id == GetQueryParam(request, "row"));
                if (row == null) { SendJson(response, 404, new { error = "变量不存在" }); return true; }
                SendJson(response, 200, new { row.name, row.address, total = row.references.Count, references = row.references, coverage = snapshot.reference_coverage }); return true;
            }
            if (path == "/api/io/export") {
                byte[] bytes = IoWorkbook.Create(snapshot, rows);
                response.ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
                response.Headers["Content-Disposition"] = "attachment; filename=\"TIA-IO-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".xlsx\"";
                response.ContentLength64 = bytes.Length; response.OutputStream.Write(bytes, 0, bytes.Length); response.Close(); return true;
            }
            SendJson(response, 404, new { error = "接口不存在" }); return true;
        }
    }
}
