using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Web.Script.Serialization;
using TiaPortalSourceBridge.Alarms;
using TiaPortalSourceBridge.Openness;

namespace TiaPortalSourceBridge.Mcp
{
    internal sealed partial class McpServer
    {
        private readonly ConcurrentDictionary<string, AlarmSnapshot> alarmSnapshots = new ConcurrentDictionary<string, AlarmSnapshot>(StringComparer.OrdinalIgnoreCase);
        private IAlarmUiBridge alarmUi;
        private sealed class AlarmSnapshot { internal AlarmWorkbook Book; internal DateTime Captured; internal string Path; internal long Length; internal long Modified; }
        private static bool IsAlarmTool(string name) { return name == "tia_read_hmi_alarms" || name == "tia_write_hmi_alarms"; }
        [ThreadStatic] private static bool alarmThread;
        // Keep one desktop handle for the process: closing it on a thread still assigned to it fails.
        private static readonly Lazy<IntPtr> alarmDesktop = new Lazy<IntPtr>(() => OpenDesktop("Default", 0, false, 0x01FF));
        private static JavaScriptSerializer AlarmJson() { return new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 }; }
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenDesktop(string lpszDesktop, uint dwFlags, bool fInherit, uint dwDesiredAccess);
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetThreadDesktop(IntPtr hDesktop);

        private static T AlarmSta<T>(Func<T> work, int waitMs = 125000, bool forceThread = false)
        {
            if (alarmThread && !forceThread) return work();
            T result = default(T); Exception error = null;
            var thread = new Thread(() =>
            {
                try
                {
                    IntPtr hDesk = alarmDesktop.Value;
                    if (hDesk == IntPtr.Zero || !SetThreadDesktop(hDesk)) throw new InvalidOperationException("无法连接可交互的 Windows 桌面，请在已登录、未锁屏的桌面会话中运行报警工具。");
                    alarmThread = true;
                    result = work();
                }
                catch (Exception ex) { error = ex; }
            });
            thread.IsBackground = true; thread.SetApartmentState(ApartmentState.MTA); thread.Start();
            // UIA calls can themselves hang. Do not start another operation while a timed out call is active.
            if (!thread.Join(waitMs)) throw new TimeoutException("报警界面驱动未响应；任务可能仍在执行，不要重复写入。后台任务释放互斥锁前会拒绝后续界面请求。");
            if (error != null) throw error; return result;
        }
        private Dictionary<string, object> CallAlarmTool(string name, Dictionary<string, object> args)
        {
            int timeout = IndexInt(args, "timeout_ms", 60000, 1000, 120000);
            Func<Dictionary<string, object>> work = () => {
                using (var metrics = OperationMetrics.Begin()) {
                    Dictionary<string, object> result;
                    try { result = CallAlarmToolCore(name, args); }
                    catch (Exception ex) { result = ToolResult("操作失败：" + RootMessage(ex), true, Dict()); }
                    metrics.Attach(result); return result;
                }
            };
            return StringArg(args, "source_file").Length > 0 ? work() : AlarmSta(work, 4 * timeout + 5000, true);
        }
        private Dictionary<string, object> CallAlarmToolCore(string name, Dictionary<string, object> args)
        {
            bool write = name == "tia_write_hmi_alarms", dry = BoolArg(args, "dry_run", true);
            string file = StringArg(args, "source_file"), project = StringArg(args, "project_path"), hmi = StringArg(args, "hmi_name");
            bool live = file.Length == 0;
            if (live && project.Length == 0)
            {
                project = (_projects != null ? _projects.TryResolveSingleProject() : (_session != null && _session.IsAlive() ? _sessionPath : null)) ?? "";
            }
            if (live && (project.Length == 0 || hmi.Length == 0)) throw new ArgumentException("工程报警操作必须明确 project_path（单工程已连接时可省略）和 hmi_name；文件操作提供 source_file。");
            if (live) project = Path.GetFullPath(project);
            else file = Path.GetFullPath(file);
            int processId = IndexInt(args, "process_id", 0, 0, int.MaxValue), timeout = IndexInt(args, "timeout_ms", 60000, 1000, 120000);
            string key = live ? project + "|" + hmi + "|" + processId : file;
            int age = IndexInt(args, "max_age_ms", 2000, 0, 60000);
            bool refresh = BoolArg(args, "refresh", false);
            string sessionsRoot = Path.Combine(Path.GetDirectoryName(_configPath), "alarm-sessions");
            string directory = Path.Combine(sessionsRoot, Guid.NewGuid().ToString("N"));
            AlarmSnapshot snapshot;
            var watch = Stopwatch.StartNew();
            string mutexName = "Local\\TiaSourceBridge-HmiAlarmUI-" + System.Security.Principal.WindowsIdentity.GetCurrent().User.Value;
            using (var mutex = live ? new Mutex(false, mutexName) : null)
            {
                bool held = false;
                try
                {
                    if (live) { try { held = mutex.WaitOne(0); } catch (AbandonedMutexException) { held = true; } if (!held) throw new InvalidOperationException("另一项报警界面任务正在执行，请等待完成。"); }
                    PruneOldAlarmSessions(sessionsRoot);
                    bool cache = !write && !refresh && age > 0 && alarmSnapshots.TryGetValue(key, out snapshot)
                        && (DateTime.UtcNow - snapshot.Captured).TotalMilliseconds <= age;
                    snapshot = cache ? alarmSnapshots[key] : null;
                    if (cache && !live)
                    {
                        var info = new FileInfo(file); cache = info.Exists && info.Length == snapshot.Length && info.LastWriteTimeUtc.Ticks == snapshot.Modified;
                        if (!cache) snapshot = null;
                    }
                    if (snapshot == null)
                    {
                        if (live)
                        {
                            Directory.CreateDirectory(directory); file = Path.Combine(directory, "current.xlsx");
                            using (OperationMetrics.Measure("alarm_ui_export")) AlarmSta(() => { (alarmUi ?? (alarmUi = new AlarmUiBridge())).Export(project, hmi, processId, file, timeout); return true; });
                        }
                        using (OperationMetrics.Measure("alarm_parse")) snapshot = new AlarmSnapshot { Book = new AlarmWorkbook(file), Captured = DateTime.UtcNow, Path = file };
                        var info = new FileInfo(file); snapshot.Length = info.Length; snapshot.Modified = info.LastWriteTimeUtc.Ticks;
                        if (alarmSnapshots.Count >= 16) { AlarmSnapshot removed; alarmSnapshots.TryRemove(alarmSnapshots.OrderBy(e => e.Value.Captured).First().Key, out removed); }
                        alarmSnapshots[key] = snapshot;
                    }
                    var expected = StringArg(args, "expected_hash");
                    if (expected.Length > 0 && !string.Equals(expected, snapshot.Book.Hash, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("报警内容已变化，请重新读取后合并修改。");
                    if (!write)
                        return ToolResult("报警读取完成。", false, Dict("source", live ? "tia_ui" : "native_file", "project_path", project, "hmi_name", hmi,
                            "cached", cache, "snapshot_age_ms", Math.Round((DateTime.UtcNow - snapshot.Captured).TotalMilliseconds), "elapsed_ms", watch.ElapsedMilliseconds,
                            "result", snapshot.Book.Read(StringArg(args, "filter"), StringArg(args, "trigger_tag"), IndexInt(args, "offset", 0, 0, int.MaxValue), IndexInt(args, "limit", 100, 1, 1000), BoolArg(args, "include_fields", false))));
                    // Cached objects are immutable. Every write works on a fresh request-local document.
                    var book = new AlarmWorkbook(snapshot.Path);
                    object plan;
                    using (OperationMetrics.Measure("alarm_patch")) plan = book.Patch(StringArg(args, "changes_json"));
                    var serialized = AlarmJson().Serialize(plan);
                    var planMap = AlarmJson().DeserializeObject(serialized) as Dictionary<string, object>;
                    int changed = Convert.ToInt32(planMap["changed_count"]);
                    var result = Dict("project_path", project, "hmi_name", hmi, "source", live ? "tia_ui" : "native_file", "dry_run", dry,
                        "imported", false, "verified", false, "saved", false, "compiled", false, "downloaded", false, "plan", plan, "before_hash", snapshot.Book.Hash);
                    if (dry || changed == 0) { result["elapsed_ms"] = watch.ElapsedMilliseconds; result["unchanged"] = changed == 0; return ToolResult(dry ? "报警批次预览完成，尚未导入工程。" : "报警内容未变化，跳过写入。", false, result); }
                    RequireConfirmation(args, live ? "批量写入 HMI 离散报警会修改离线工程" : "生成修改后的原生报警文件");
                    string output = StringArg(args, "output_file");
                    if (output.Length == 0) { Directory.CreateDirectory(directory); output = Path.Combine(directory, "updated.xlsx"); }
                    output = Path.GetFullPath(output);
                    using (OperationMetrics.Measure("alarm_write_package")) book.Write(output);
                    result["output_file"] = output;
                    if (!live) { result["requires_tia_import"] = true; result["elapsed_ms"] = watch.ElapsedMilliseconds; return ToolResult("原生报警文件已生成，尚未写入工程。", false, result); }
                    AlarmSnapshot invalidated; alarmSnapshots.TryRemove(key, out invalidated);
                    result["may_have_changed"] = true;
                    try
                    {
                        using (OperationMetrics.Measure("alarm_ui_import")) AlarmSta(() => { alarmUi.Import(project, hmi, processId, output, timeout); return true; });
                        result["imported"] = true;
                        string readback = Path.Combine(directory, "readback.xlsx");
                        using (OperationMetrics.Measure("alarm_ui_readback")) AlarmSta(() => { alarmUi.Export(project, hmi, processId, readback, timeout); return true; });
                        var actual = new AlarmWorkbook(readback);
                        result["after_hash"] = actual.Hash;
                        // Reapplying the plan must produce no changes, proving all requested fields exist.
                        var remaining = AlarmJson().DeserializeObject(AlarmJson().Serialize(actual.Verify(StringArg(args, "changes_json")))) as Dictionary<string, object>;
                        if (Convert.ToInt32(remaining["changed_count"]) != 0) throw new InvalidOperationException("导入后回读与请求不一致；请查看逐项结果，不要直接重复提交。");
                        result["verified"] = true; result["may_have_changed"] = false;
                        var info = new FileInfo(readback); alarmSnapshots[key] = new AlarmSnapshot { Book = actual, Captured = DateTime.UtcNow, Path = readback, Length = info.Length, Modified = info.LastWriteTimeUtc.Ticks };
                    }
                    catch (Exception ex) { result["error"] = RootMessage(ex); result["elapsed_ms"] = watch.ElapsedMilliseconds; return ToolResult("报警工程写入或回读未完成；请查看 imported/may_have_changed，未自动重试。", true, result); }
                    result["elapsed_ms"] = watch.ElapsedMilliseconds;
                    return ToolResult("报警已导入并回读确认；未编译、保存或下载。", false, result);
                }
                finally { if (held) mutex.ReleaseMutex(); }
            }
        }
        private void PruneOldAlarmSessions(string sessionsRoot)
        {
            try
            {
                if (!Directory.Exists(sessionsRoot)) return;
                string prefix = Path.GetFullPath(sessionsRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var activeDirs = new HashSet<string>(alarmSnapshots.Values
                    .Select(s => string.IsNullOrEmpty(s.Path) ? "" : Path.GetDirectoryName(s.Path))
                    .Where(d => !string.IsNullOrEmpty(d)), StringComparer.OrdinalIgnoreCase);
                DateTime cutoff = DateTime.UtcNow.AddMinutes(-30);
                foreach (string dir in Directory.GetDirectories(sessionsRoot))
                {
                    string target = Path.GetFullPath(dir);
                    if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0) continue;
                    if (activeDirs.Contains(dir)) continue;
                    if (Directory.GetCreationTimeUtc(dir) < cutoff && !File.Exists(Path.Combine(dir, "updated.xlsx")))
                    {
                        try { Directory.Delete(dir, true); } catch { }
                    }
                }
            }
            catch { }
        }
        private static object[] AlarmDefinitions()
        {
            var common = new[] {
                Property("project_path", StringProperty("工程入口文件完整路径；界面模式必填。")), Property("hmi_name", StringProperty("项目树中的 HMI 设备名；界面模式必填。")),
                Property("source_file", StringProperty("可选：直接读取博途原生 xlsx 报警文件，跳过界面。")),
                Property("process_id", IndexInteger("可选：目标博途进程 ID", 0, int.MaxValue)), Property("timeout_ms", IndexInteger("每次完整导出或导入的最大等待，默认60000毫秒；写入最多三次界面操作",1000,120000))
            };
            return new object[] {
                Tool("tia_read_hmi_alarms", "读取经典 HMI 离散报警。原生文件直接解析；工程模式通过界面批量导出一次。支持过滤、分页、短缓存及完整字段，不连接 Openness，不编译。",
                    ObjectSchema(common.Concat(new[] { Property("filter",StringProperty("按名称、文本等关键字过滤")),Property("trigger_tag",StringProperty("按触发变量精确过滤")),
                        Property("offset",IndexInteger("默认0",0,int.MaxValue)),Property("limit",IndexInteger("默认100",1,1000)),Property("max_age_ms",IndexInteger("允许缓存年龄，默认2000；0强制新读",0,60000)),
                        Property("refresh",BooleanProperty("强制刷新，默认false")),Property("include_fields",BooleanProperty("返回全部原生列，默认false")) }).ToArray()),true,false,true),
                Tool("tia_write_hmi_alarms", "批量新增/修改经典 HMI 离散报警。默认dry_run=true；执行需confirm=true。工程模式刷新导出一次、预检整批、原生导入一次、回读一次。完全相同内容跳过导入。文件模式只生成新文件。未编译、保存或下载。",
                    ObjectSchema(new[]{"changes_json"},common.Concat(new[] {Property("changes_json",StringProperty("操作数组：{id,op:create/update/upsert,name,alarm_class,trigger_tag,trigger_bit,texts:{zh-CN:文本},template_id,fields:{原生列:值}}。未知列保留。")),
                        Property("output_file",StringProperty("可选新输出文件；不覆盖已有文件")),Property("expected_hash",StringProperty("可选：读取结果中的稳定内容指纹，拒绝过期修改")),
                        Property("dry_run",BooleanProperty("默认true，仅预览")),Property("confirm",BooleanProperty("明确执行离线报警修改"))}).ToArray()),false,true,false)
            };
        }
    }
}
