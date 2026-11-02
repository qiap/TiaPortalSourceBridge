﻿﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using TiaPortalSourceBridge.Configuration;

namespace TiaPortalSourceBridge.Web
{
    internal sealed class ActionProgress
    {
        public string actionName { get; set; }
        public string status { get; set; } // "idle", "running", "completed", "failed"
        public string message { get; set; }
        public DateTime startTime { get; set; }
        public DateTime endTime { get; set; }
        public int exitCode { get; set; }
        public List<string> logs { get; set; }

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
        //   index  -> 建立工程索引
        //   tia    -> 导出 / 导入 / 编译 等直接操作 TIA Portal 的任务（同一工程内串行，避免 Openness 冲突）
        private static readonly object _sync = new object();
        private static readonly Dictionary<string, ActionProgress> _lanes = new Dictionary<string, ActionProgress>();
        private static readonly List<string> _logs = new List<string>();
        private static ActionProgress _last = new ActionProgress();
        private const int TrustTimeoutSeconds = 120;
        private const int ActionTimeoutMinutes = 30;

        private static string LaneOf(string action)
        {
            if (action == "trust") return "trust";
            if (action == "build-index") return "index";
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
                        if (kv.Key != "trust" && kv.Value.status == "running") return true;
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
                    foreach (ActionProgress a in _lanes.Values) if (a.status == "running") running.Add(a);

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
                    return _lanes.Values.Where(a => a.status == "running").ToList();
                }
            }
        }

        /// <summary>启动任务；同一通道已有任务运行时返回 false 并通过 busyAction 给出占用者。</summary>
        public static bool StartAction(string action, string configPath, string projectPath, string plcName, out string busyAction)
        {
            busyAction = "";
            string lane = LaneOf(action);
            ActionProgress progress;
            lock (_sync)
            {
                ActionProgress existing;
                if (_lanes.TryGetValue(lane, out existing) && existing.status == "running")
                {
                    busyAction = existing.actionName;
                    return false;
                }
                progress = new ActionProgress
                {
                    actionName = action,
                    status = "running",
                    message = "正在执行: " + action + "...",
                    startTime = DateTime.Now
                };
                _lanes[lane] = progress;
                _last = progress;
            }

            Thread worker = new Thread(() => ExecuteAction(progress, action, configPath, projectPath, plcName))
            {
                IsBackground = true,
                Name = "WebActionWorker-" + lane
            };
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
                _logs.Add(string.Format("[{0:HH:mm:ss}] [{1}] {2}", DateTime.Now, action, line));
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
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit((int)timeout.TotalMilliseconds))
                {
                    timedOut = true;
                    try { p.Kill(); } catch { }
                    return -2;
                }
                p.WaitForExit(); // flush async output
                return p.ExitCode;
            }
        }

        private static void Finish(ActionProgress progress, int exitCode, string failMessage)
        {
            lock (_sync)
            {
                progress.exitCode = exitCode;
                progress.endTime = DateTime.Now;
                if (exitCode == 0)
                {
                    progress.status = "completed";
                    progress.message = "任务执行成功 (耗时 " + (int)(progress.endTime - progress.startTime).TotalSeconds + " 秒)";
                }
                else
                {
                    progress.status = "failed";
                    progress.message = failMessage ?? ("任务退出异常 (错误码: " + exitCode + ")");
                }
                _last = progress;
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
                AppendLog(action, ">>> 启动任务: " + action);

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

                if (timedOut) failMessage = "任务超过 " + ActionTimeoutMinutes + " 分钟未结束，已强制终止";
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