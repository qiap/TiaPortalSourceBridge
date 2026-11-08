﻿﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using TiaPortalSourceBridge.Configuration;
using TiaPortalSourceBridge.Mcp;

namespace TiaPortalSourceBridge.Web
{
    internal sealed class EngineeringOutcomeUnknownException : Exception
    {
        internal EngineeringOutcomeUnknownException(string message, Exception inner = null) : base(message, inner) { }
    }
    internal sealed class ActionProgress
    {
        public string task_id { get; set; }
        public string project_path { get; set; }
        public string plc_name { get; set; }
        public string stage { get; set; }
        public string actionName { get; set; }
        public string status { get; set; } // idle, running, outcome_unknown, completed, cancelled, failed
        public string message { get; set; }
        public DateTime startTime { get; set; }
        public DateTime endTime { get; set; }
        public int exitCode { get; set; }
        public List<string> logs { get; set; }

        internal ExecutionLog execution;
        internal string warning;

        public ActionProgress()
        {
            status = "idle";
            message = "";
            logs = new List<string>();
        }
    }

    internal sealed class ActionRunner
    {
        // 每个"通道"(lane)最多同时运行一个任务；不同通道的任务可以并行：
        //   trust  -> 注册白名单（需要 UAC，可能长时间等待用户确认，绝不能阻塞其他功能）
        //   tia    -> 建立工程索引及工程操作共用串行通道
        //   tia    -> 导出 / 导入 / 编译 等直接操作 TIA Portal 的任务（同一工程内串行，避免 Openness 冲突）
        private static readonly object _sync = new object();
        private static readonly Dictionary<string, ActionProgress> _lanes = new Dictionary<string, ActionProgress>();
        private static readonly List<string> _logs = new List<string>();
        private static ActionProgress _last = new ActionProgress();
        private static readonly List<ActionProgress> history = new List<ActionProgress>();
        internal static ActionProgress FindTask(string id)
        {
            lock (_sync)
            {
                var p = history.FirstOrDefault(a => a.task_id == id);
                return p == null ? null : new ActionProgress { task_id = p.task_id, project_path = p.project_path, plc_name = p.plc_name, actionName = p.actionName, status = p.status, stage = p.stage, message = p.message, startTime = p.startTime, endTime = p.endTime, exitCode = p.exitCode, logs = new List<string>(p.logs) };
            }
        }
        private const int TrustTimeoutSeconds = 120;
        internal static ExecutionRecord[] ExecutionRecords(string config)
        {
            var records = ExecutionLog.Read(config);
            lock (_sync) {
                foreach (var task in _lanes.Values) {
                    if (task.execution == null || !IsPending(task.status) || task.actionName == "io-read") continue;
                    var row = records.FirstOrDefault(r => r.id == task.execution.Id && r.status == "running");
                    if (row != null) row.message = task.logs.LastOrDefault() ?? task.message;
                }
            }
            return records;
        }
        internal static bool StartIoRead(string config, string project, string plc, Func<Action<string>, string> work)
        { return StartEngineeringTask(config, project, plc, "io-read", "正在读取 I/O 表与引用", work); }

        internal static bool StartEngineeringTask(string config, string project, string plc, string action, string message, Func<Action<string>, string> work, string operationId = null, string recoveryOf = null)
        {
            ActionProgress progress;
            lock (_sync) {
                ActionProgress existing;
                if (_lanes.TryGetValue("tia", out existing) && IsPending(existing.status) &&
                    !(existing.status == "outcome_unknown" && !string.IsNullOrEmpty(recoveryOf) && existing.task_id == recoveryOf)) return false;
                progress = new ActionProgress { task_id = operationId ?? Guid.NewGuid().ToString("N"), project_path = project, plc_name = plc,
                    actionName = action, status = "running", stage = "reading", startTime = DateTime.Now, message = message };
                _lanes["tia"] = progress; _last = progress; history.Add(progress);
                if (history.Count > 100) history.Remove(history.First(a => !IsPending(a.status)));
            }
            new Thread(() => {
                try {
                    progress.execution = ExecutionLog.Begin(config, "看板", action, project + " · " + plc, operationId);
                    progress.warning = work(detail => {
                        lock (_sync) { progress.message = detail; }
                        AppendLog(action, detail);
                        progress.execution.Update("running", detail, false);
                    });
                    Finish(progress, 0, null);
                }
                catch (EngineeringOutcomeUnknownException ex) {
                    lock (_sync) { progress.status = "outcome_unknown"; progress.stage = "needs_review"; progress.message = ex.Message; }
                    progress.execution.Update("unknown", ex.Message, true);
                    AppendLog(action, ex.Message);
                }
                catch (Exception ex) { Finish(progress, -1, ex.Message); }
            }) { IsBackground = true, Name = "DashboardEngineeringTask" }.Start();
            return true;
        }
        private const int ActionTimeoutMinutes = 30;

        internal static bool IsPending(string status) { return status == "running" || status == "outcome_unknown"; }

        private static string LaneOf(string action)
        {
            if (action == "trust") return "trust";
            // Index reads share the engineering lane with imports and compiles.
            return "tia";
        }

        /// <summary>是否有"会占用工程"的任务在运行（trust 不计入，用于退出看板时的等待）。</summary>
        public static bool AnyBlockingRunning
        {
            get
            {
                lock (_sync)
                {
                    foreach (KeyValuePair<string, ActionProgress> kv in _lanes)
                        if (kv.Key != "trust" && IsPending(kv.Value.status)) return true;
                    return false;
                }
            }
        }

        public static ActionProgress Current
        {
            get
            {
                lock (_sync)
                {
                    var running = new List<ActionProgress>();
                    foreach (ActionProgress a in _lanes.Values) if (IsPending(a.status)) running.Add(a);

                    ActionProgress view = new ActionProgress();
                    if (running.Count > 0)
                    {
                        view.status = "running";
                        view.actionName = string.Join(" + ", running.Select(a => a.actionName).ToArray());
                        view.message = "正在执行: " + view.actionName;
                        view.startTime = running.Min(a => a.startTime);
                    }
                    else
                    {
                        view.status = _last.status;
                        view.actionName = _last.actionName;
                        view.message = _last.message;
                        view.startTime = _last.startTime;
                        view.endTime = _last.endTime;
                        view.exitCode = _last.exitCode;
                    }
                    var selected = running.Count > 0 ? running.FirstOrDefault(a => a.status == "outcome_unknown") ?? running[0] : _last;
                    view.task_id = selected.task_id; view.project_path = selected.project_path; view.plc_name = selected.plc_name; view.stage = selected.stage;
                    if (selected.status == "outcome_unknown") { view.status = selected.status; view.message = selected.message; }
                    view.logs = new List<string>(_logs);
                    return view;
                }
            }
        }

        public static List<ActionProgress> Running
        {
            get
            {
                lock (_sync)
                {
                    return _lanes.Values.Where(a => IsPending(a.status)).ToList();
                }
            }
        }

        /// <summary>启动任务；同一通道已有任务运行时返回 false 并通过 busyAction 给出占用者。</summary>
        public static bool StartAction(string action, string configPath, string projectPath, string plcName, out string busyAction)
        { ActionProgress task; return StartAction(action, configPath, projectPath, plcName, out busyAction, out task); }

        public static bool StartAction(string action, string configPath, string projectPath, string plcName, out string busyAction, out ActionProgress task)
        {
            task = null;
            if (!new[] { "trust", "build-index", "export", "import", "compile" }.Contains(action)) throw new ArgumentException("不支持的操作。");
            busyAction = "";
            string lane = LaneOf(action);
            ActionProgress progress;
            lock (_sync)
            {
                ActionProgress existing;
                if (_lanes.TryGetValue(lane, out existing) && IsPending(existing.status))
                {
                    busyAction = existing.actionName;
                    return false;
                }
                progress = new ActionProgress
                {
                    task_id = Guid.NewGuid().ToString("N"), project_path = projectPath, plc_name = plcName, stage = "executing",
                    actionName = action,
                    status = "running",
                    message = "正在执行: " + action + "...",
                    startTime = DateTime.Now
                };
                history.Add(progress);
                if (history.Count > 100) history.Remove(history.First(a => !IsPending(a.status)));
                _lanes[lane] = progress;
                _last = progress;
            }

            Thread worker = new Thread(() => ExecuteAction(progress, action, configPath, projectPath, plcName))
            {
                IsBackground = true,
                Name = "WebActionWorker-" + lane
            };
            task = progress;
            worker.Start();
            return true;
        }

        public static bool StartAction(string action, string configPath, string projectPath, string plcName = "")
        {
            string busy;
            return StartAction(action, configPath, projectPath, plcName, out busy);
        }

        private static void AppendLog(string action, string line)
        {
            lock (_sync)
            {
                string entry = string.Format("[{0:HH:mm:ss}] [{1}] {2}", DateTime.Now, action, line);
                _logs.Add(entry);
                ActionProgress task;
                if (_lanes.TryGetValue(LaneOf(action), out task)) { task.logs.Add(entry); if (task.logs.Count > 1000) task.logs.RemoveAt(0); }
                if (_logs.Count > 1000) _logs.RemoveAt(0);
            }
        }

        private static int RunProcess(ProcessStartInfo psi, string action, TimeSpan timeout, out bool timedOut)
        {
            timedOut = false;
            using (Process p = Process.Start(psi))
            {
                p.OutputDataReceived += (s, e) => { if (e.Data != null) AppendLog(action, e.Data); };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) AppendLog(action, "[ERR] " + e.Data); };
                p.StandardInput.Close(); // Non-interactive actions must never wait for console input.
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit((int)timeout.TotalMilliseconds))
                {
                    timedOut = true;
                    ActionProgress pending;
                    lock (_sync) {
                        pending = _lanes[LaneOf(action)];
                        pending.status = "outcome_unknown";
                        pending.stage = "awaiting_result";
                        pending.message = "等待超时，后台仍在执行；请勿重复提交，完成后将自动更新结果。";
                    }
                    if (pending.execution != null) pending.execution.Update("waiting", pending.message, false);
                    AppendLog(action, "等待超时，保留后台进程并继续等待最终结果。");
                    p.WaitForExit();
                }
                p.WaitForExit(); // flush async output
                return p.ExitCode;
            }
        }

        private static void Finish(ActionProgress progress, int exitCode, string failMessage)
        {
            lock (_sync)
            {
                progress.stage = "finished";
                progress.exitCode = exitCode;
                progress.endTime = DateTime.Now;
                if (exitCode == 0)
                {
                    progress.status = "completed";
                    progress.message = "任务执行成功 (耗时 " + (int)(progress.endTime - progress.startTime).TotalSeconds + " 秒)";
                }
                else
                {
                    progress.status = exitCode == 3 ? "cancelled" : "failed";
                    progress.message = exitCode == 3 ? "任务已取消。" : failMessage ?? ("任务退出异常 (错误码: " + exitCode + ")");
                }
                _last = progress;
            }
            if (progress.execution != null) {
                string detail = progress.warning ?? progress.message;
                if (progress.status == "failed" && string.IsNullOrEmpty(failMessage)) {
                    lock (_sync) {
                        var diagnostic = progress.logs.LastOrDefault(line => line.Contains("[ERR]") || line.Contains("错误") || line.Contains("失败"));
                        if (diagnostic != null) detail += " " + diagnostic;
                    }
                }
                progress.execution.Update(progress.warning != null && exitCode == 0 ? "warning" : progress.status, detail, true);
            }
            AppendLog(progress.actionName, exitCode == 0 ? ">>> 任务完成: 成功。" : ">>> 任务未成功: " + progress.message);
        }

        private static void ExecuteAction(ActionProgress progress, string action, string configPath, string projectPath, string plcName)
        {
            string rootDir = Path.GetDirectoryName(Path.GetFullPath(configPath));
            string exePath = System.Reflection.Assembly.GetEntryAssembly().Location;
            int exitCode = 0;
            string failMessage = null;
            bool timedOut = false;

            try
            {
                progress.execution = ExecutionLog.Begin(configPath, "看板", action, projectPath + " · " + plcName);
                AppendLog(action, ">>> 启动任务: " + action + " | " + progress.task_id + " | " + projectPath + " | " + plcName);

                if (action == "trust")
                {
                    string scriptPath = Path.Combine(rootDir, "TrustThisTool.ps1");
                    if (!File.Exists(scriptPath))
                        throw new FileNotFoundException("未找到 TrustThisTool.ps1", scriptPath);

                    string logPath = Path.Combine(rootDir, "last-trust.log");
                    if (File.Exists(logPath))
                    {
                        try { File.Delete(logPath); } catch { }
                    }

                    AppendLog(action, "正在申请 Windows 管理员 (UAC) 权限（后台等待，不影响其他功能；最长 " + TrustTimeoutSeconds + " 秒）...");
                    AppendLog(action, "提示：如果任务栏出现闪烁的盾牌图标，请点击并选择【是】以允许写入 HKLM 注册表白名单。");

                    string psCommand = string.Format(
                        "& {{ try {{ & '{0}' *>&1 | Out-File -FilePath '{1}' -Encoding utf8 }} catch {{ $_ | Out-File -FilePath '{1}' -Encoding utf8; exit 1 }} }}",
                        scriptPath.Replace("'", "''"),
                        logPath.Replace("'", "''"));

                    ProcessStartInfo psi = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"" + psCommand + "\"",
                        WorkingDirectory = rootDir,
                        Verb = "runas",
                        UseShellExecute = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };

                    try
                    {
                        // Process.Start 在 UAC 弹窗未处理前会一直阻塞，所以放在独立线程里并加超时。
                        Process started = null;
                        Exception startError = null;
                        Thread starter = new Thread(() =>
                        {
                            try { started = Process.Start(psi); } catch (Exception ex) { startError = ex; }
                        }) { IsBackground = true, Name = "TrustUacStarter" };
                        starter.Start();
                        if (!starter.Join(TimeSpan.FromSeconds(TrustTimeoutSeconds)))
                        {
                            exitCode = -2;
                            failMessage = "等待 UAC 确认超时 (" + TrustTimeoutSeconds + " 秒)，请重新点击【注册白名单】，或右键以管理员身份运行 TrustThisTool.bat";
                        }
                        else if (startError != null)
                        {
                            throw startError;
                        }
                        else if (started != null)
                        {
                            using (started)
                            {
                                if (!started.WaitForExit(TrustTimeoutSeconds * 1000))
                                {
                                    exitCode = -2;
                                    failMessage = "白名单注册脚本超时未结束";
                                }
                                else
                                {
                                    exitCode = started.ExitCode;
                                }
                            }
                        }
                    }
                    catch (Win32Exception winEx)
                    {
                        if (winEx.NativeErrorCode == 1223) // ERROR_CANCELLED
                        {
                            AppendLog(action, "[WARN] 用户取消了 Windows UAC 管理员提权弹窗。");
                            exitCode = 1223;
                            failMessage = "用户取消了 UAC 提权";
                        }
                        else
                        {
                            throw;
                        }
                    }

                    if (File.Exists(logPath))
                    {
                        foreach (string l in File.ReadAllLines(logPath, Encoding.UTF8))
                            if (!string.IsNullOrWhiteSpace(l)) AppendLog(action, l);
                    }
                }
                else if (action == "build-index")
                {
                    string scriptPath = Path.Combine(rootDir, "Build-ProjectIndex.ps1");
                    string args = "-NoProfile -STA -ExecutionPolicy Bypass -File \"" + scriptPath + "\"";
                    if (!string.IsNullOrWhiteSpace(projectPath))
                        args += " -ProjectPath \"" + projectPath + "\"";

                    ProcessStartInfo psi = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = args,
                        WorkingDirectory = rootDir,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        RedirectStandardInput = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8
                    };
                    exitCode = RunProcess(psi, action, TimeSpan.FromMinutes(ActionTimeoutMinutes), out timedOut);
                }
                else
                {
                    string arguments = action + " --config \"" + configPath + "\"";
                    if (!string.IsNullOrWhiteSpace(projectPath))
                        arguments += " --project \"" + projectPath + "\"";
                    if (!string.IsNullOrWhiteSpace(plcName))
                        arguments += " --plc \"" + plcName + "\"";

                    ProcessStartInfo psi = new ProcessStartInfo
                    {
                        FileName = exePath,
                        Arguments = arguments,
                        WorkingDirectory = rootDir,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        RedirectStandardInput = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8
                    };
                    exitCode = RunProcess(psi, action, TimeSpan.FromMinutes(ActionTimeoutMinutes), out timedOut);
                }

                // A late result is final; a timeout alone never terminates an engineering operation.
                Finish(progress, exitCode, failMessage);
            }
            catch (Exception ex)
            {
                AppendLog(action, ">>> 发生异常: " + ex);
                Finish(progress, -1, "执行异常: " + ex.Message);
            }
        }
    }
}
