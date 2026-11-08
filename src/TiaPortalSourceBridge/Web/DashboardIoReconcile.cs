using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using TiaPortalSourceBridge.Mcp;

namespace TiaPortalSourceBridge.Web
{
    internal sealed partial class WebServer
    {
        private sealed class ReconcileUpload { internal string Id, Project, Plc; internal IoSheetImport File; }
        private readonly object reconcileGate = new object();
        private bool reconcileBusy;
        private readonly Dictionary<string, ReconcileUpload> reconcileUploads = new Dictionary<string, ReconcileUpload>();
        private readonly Dictionary<string, IoReconcileReport> reconcileReports = new Dictionary<string, IoReconcileReport>();
        private IoJob ReconcileSnapshot(string id)
        {
            lock (ioGate) {
                IoJob job;
                if (!ioJobs.TryGetValue(id ?? "", out job) || job.state != "completed" || job.result == null || consumedCylinderSnapshots.Contains(id))
                    throw new InvalidOperationException("PLC 点表快照已失效，请重新读取当前工程。");
                return job;
            }
        }
        private bool HandleIoReconcile(HttpListenerContext context, string path)
        {
            if (!path.StartsWith("/api/io/reconcile-", StringComparison.Ordinal)) return false;
            var request = context.Request; var response = context.Response; response.Headers["Cache-Control"] = "no-store";
            bool acquired = false;
            try {
                if (new[] { "/api/io/reconcile-options", "/api/io/reconcile-preview", "/api/io/reconcile-export" }.Contains(path)) {
                    if (request.HttpMethod != "GET") { SendJson(response, 405, new { error = "仅支持读取" }); return true; }
                    if (path == "/api/io/reconcile-options") {
                        var job = ReconcileSnapshot(GetQueryParam(request, "id"));
                        SendJson(response, 200, new { snapshot_id = job.id, project_path = job.project, plc_name = job.plc, choices = IoReconcile.Choices(job.result) }); return true;
                    }
                    if (path == "/api/io/reconcile-preview") {
                        ReconcileUpload upload;
                        lock (reconcileGate) if (!reconcileUploads.TryGetValue(GetQueryParam(request, "upload_id"), out upload)) throw new InvalidOperationException("上传文件已过期，请重新选择文件。");
                        int row; if (!int.TryParse(GetQueryParam(request, "header_row"), out row) || row < 0 || row > 1048576) throw new ArgumentException("表头行号无效。");
                        SendJson(response, 200, upload.File.Preview(GetQueryParam(request, "sheet"), row)); return true;
                    }
                    IoReconcileReport report;
                    lock (reconcileGate) if (!reconcileReports.TryGetValue(GetQueryParam(request, "id"), out report)) throw new InvalidOperationException("对账报告已过期，请重新对账。");
                    byte[] bytes = IoReconcileWorkbook.Create(report);
                    response.ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
                    response.Headers["Content-Disposition"] = "attachment; filename=\"TIA-IO-Compare-" + report.id + ".xlsx\"";
                    response.ContentLength64 = bytes.Length; response.OutputStream.Write(bytes, 0, bytes.Length); response.Close(); return true;
                }
                if (path != "/api/io/reconcile-upload" && path != "/api/io/reconcile-run") { SendJson(response, 404, new { error = "对账接口不存在" }); return true; }
                if (request.HttpMethod != "POST") { SendJson(response, 405, new { error = "请使用上传或对账按钮" }); return true; }
                if (shutdownRequested) { SendJson(response, 503, new { error = "服务正在退出" }); return true; }
                int limit = path.EndsWith("upload", StringComparison.Ordinal) ? 12 * 1024 * 1024 : 16384;
                if (request.ContentLength64 < 0 || request.ContentLength64 > limit) { SendJson(response, 413, new { error = "上传文件或请求过大" }); return true; }
                lock (reconcileGate) {
                    if (reconcileBusy) { SendJson(response, 409, new { error = "正在处理另一个点表，请稍后重试" }); return true; }
                    reconcileBusy = acquired = true;
                }
                var json = CylinderWritePipeline.Json(); json.MaxJsonLength = limit;
                var map = json.Deserialize<Dictionary<string, object>>(ReadBody(request));
                if (map == null) throw new ArgumentException("缺少点表参数。");
                string project = Text(map, "project_path"), plc = Text(map, "plc_name");
                if (!File.Exists(project) || plc.Length == 0 || !actionTargets.Verify(project, plc, Text(map, "target_token"))) throw new InvalidOperationException("工程或 PLC 已变化，请等待重新识别后再操作。");
                if (path == "/api/io/reconcile-upload") {
                    string encoded = Text(map, "content_base64");
                    if (encoded.Length > (IoSheetImport.MaxBytes + 2) / 3 * 4) throw new ArgumentException("请选择不超过 8 MB 的 .xlsx 文件。");
                    var file = IoSheetImport.Load(Convert.FromBase64String(encoded), Text(map, "file_name"));
                    var upload = new ReconcileUpload { Id = Guid.NewGuid().ToString("N"), Project = project, Plc = plc, File = file };
                    var first = file.Sheets.FirstOrDefault(s => s.Rows.Count > 0);
                    if (first == null) throw new ArgumentException("文件中的工作表均为空。");
                    object preview = file.Preview(first.Name, 0);
                    lock (reconcileGate) { while (reconcileUploads.Count >= 3) reconcileUploads.Remove(reconcileUploads.First().Key); reconcileUploads.Add(upload.Id, upload); }
                    SendJson(response, 200, new { upload_id = upload.Id, file_name = file.FileName, project_path = project, plc_name = plc,
                        sheets = file.Sheets.Select(s => new { name = s.Name, hidden = s.Hidden, rows = s.Rows.Count }).ToArray(), preview }); return true;
                }
                ReconcileUpload selected;
                lock (reconcileGate) if (!reconcileUploads.TryGetValue(Text(map, "upload_id"), out selected)) throw new InvalidOperationException("上传文件已过期，请重新选择文件。");
                if (!PathsEqual(selected.Project, project) || selected.Plc != plc) throw new InvalidOperationException("上传点表属于其他工程或 PLC，请重新上传。");
                var snapshot = ReconcileSnapshot(Text(map, "id"));
                if (!PathsEqual(snapshot.project, project) || snapshot.plc != plc) throw new InvalidOperationException("PLC 快照属于其他目标，请重新读取。");
                object raw; if (!map.TryGetValue("options", out raw) || !(raw is Dictionary<string, object>)) throw new ArgumentException("缺少列对应关系。");
                var optionMap = (Dictionary<string, object>)raw;
                foreach (string field in new[] { "header_row", "address", "name", "type", "comment" }) {
                    object value;
                    if (!optionMap.TryGetValue(field, out value) || !(value is int)) throw new ArgumentException("表头行和列对应关系必须是整数。");
                }
                var options = json.Deserialize<IoReconcileOptions>(json.Serialize(raw));
                var log = ExecutionLog.Begin(_configPath, "看板", "电气 Excel 点表对账", project + " · " + plc + " · " + selected.File.FileName);
                try {
                    var report = IoReconcile.Compare(selected.File, snapshot.result, options); report.snapshot_id = snapshot.id; report.source_mode = snapshot.cached ? "local_cache" : "tia_readback";
                    // Refuse oversized results rather than publishing an unreadable report.
                    var validator = CylinderWritePipeline.Json(); validator.MaxJsonLength = 32 * 1024 * 1024;
                    validator.Serialize(report);
                    lock (reconcileGate) { while (reconcileReports.Count >= 3) reconcileReports.Remove(reconcileReports.First().Key); reconcileReports.Add(report.id, report); }
                    log.Update("completed", "只读对账完成，Excel " + report.excel_rows + " 行 / PLC " + report.plc_rows + " 项", true);
                    SendJson(response, 200, report);
                } catch (Exception ex) { log.Update("failed", ex.Message, true); throw; }
                return true;
            } catch (InvalidDataException ex) { SendJson(response, 400, new { error = "Excel 文件损坏或格式不支持：" + ex.Message }); return true;
            } catch (System.Xml.XmlException) { SendJson(response, 400, new { error = "Excel 内部 XML 格式无效或超过读取上限" }); return true;
            } catch (FormatException) { SendJson(response, 400, new { error = "上传内容或数字格式无效" }); return true;
            } catch (ArgumentException ex) { SendJson(response, 400, new { error = ex.Message }); return true;
            } catch (InvalidOperationException ex) { SendJson(response, 409, new { error = ex.Message }); return true;
            } finally { if (acquired) lock (reconcileGate) reconcileBusy = false; }
        }
    }
}
