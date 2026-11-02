using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace TiaPortalSourceBridge.Mcp
{
    /// <summary>
    /// 由可视化看板顺带启动一个无窗口的 mcp-host，使 MCP 端点随看板一起可用。
    /// 宿主不随看板退出而结束（可能正在写入工程），进程号写入 logs\mcp-host.pid，
    /// 用 stop-mcp / StopTiaMcp.bat 结束。
    /// </summary>
    internal static class McpHostProcess
    {
        private const string MutexScope = "Local\\";
        private static bool _restartPending;
        private static string _pidFilePath;

        internal static string PipeName
        {
            get { return "TiaPortalSourceBridge-" + WindowsIdentity.GetCurrent().User.Value.Replace('-', '_'); }
        }

        internal static string PidFile(string toolRoot)
        {
            return Path.Combine(toolRoot, "logs", "mcp-host.pid");
        }

        /// <summary>与 PipeTransport.Host 使用同一个互斥体，因此探测结果就是"是否已有宿主"。</summary>
        internal static bool IsRunning()
        {
            try
            {
                using (Mutex probe = Mutex.OpenExisting(MutexScope + PipeName))
                {
                    return true;
                }
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                // 别的账户持有同名互斥体时打不开；视为已在运行，避免重复启动。
                return true;
            }
        }

        internal static void WritePidFile(string toolRoot)
        {
            try
            {
                string path = PidFile(toolRoot);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string content = Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture)
                    + Environment.NewLine + DateTime.UtcNow.ToString("o") + Environment.NewLine;
                File.WriteAllText(path, content, new UTF8Encoding(false));
                _pidFilePath = path;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        internal static void DeletePidFile(string toolRoot)
        {
            DeleteFile(_pidFilePath ?? PidFile(toolRoot));
            _pidFilePath = null;
        }

        /// <summary>
        /// 供看护线程在 Environment.Exit 之前调用：Environment.Exit 不会执行 finally，
        /// 不显式删就会留下一个指向已退出进程的 pid 文件。
        /// </summary>
        internal static void CleanupPidFile()
        {
            DeleteFile(_pidFilePath);
            _pidFilePath = null;
        }

        private static void DeleteFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>看板启动时调用；已有宿主则跳过。失败只提示，不影响看板本身。</summary>
        internal static void StartIfNeeded(string configPath)
        {
            StartIfNeeded(configPath, true);
        }

        private static void StartIfNeeded(string configPath, bool allowDelayedRetry)
        {
            if (IsRunning())
            {
                Console.WriteLine("[MCP] 已有 MCP 服务在运行，跳过后台启动。");
                if (allowDelayedRetry && !_restartPending)
                {
                    // 上一次看板刚关、宿主还没来得及发现父进程已退出：几秒后补启一次，
                    // 否则"关掉看板立刻重开"会出现看板在跑但没有 MCP 端点的情况。
                    _restartPending = true;
                    string pendingConfig = configPath;
                    ThreadPool.QueueUserWorkItem(delegate(object state)
                    {
                        Thread.Sleep(3000);
                        _restartPending = false;
                        if (IsRunning()) return;
                        Console.WriteLine("[MCP] 上一个后台服务已退出，重新启动。");
                        StartIfNeeded(pendingConfig, false);
                    });
                }
                return;
            }

            string configFull = Path.GetFullPath(configPath);
            string toolRoot = Path.GetDirectoryName(configFull);
            string exePath = System.Reflection.Assembly.GetEntryAssembly().Location;
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                Console.Error.WriteLine("[MCP] 无法定位 TiaPortalSourceBridge.exe，跳过后台 MCP 服务。");
                return;
            }

            try
            {
                // 不重定向输出：看板退出后管道会断，重定向的句柄一写就抛异常，
                // 反而会把本应常驻的宿主拖死。无控制台时 Console.Out 自动为空流，
                // 宿主自己的日志由 HostLog 写入 last-mcp-host.log。
                ProcessStartInfo info = new ProcessStartInfo(exePath, "mcp-host --config \"" + configFull + "\"");
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.WorkingDirectory = toolRoot;
                // 让宿主知道自己的父进程是谁：看板退出后它自行结束（先做完手上的请求）。
                info.EnvironmentVariables["TIA_BRIDGE_PARENT_PID"] = Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture);

                Process child = Process.Start(info);
                if (child == null) return;

                bool exited = child.WaitForExit(1500);
                int childPid = child.Id;
                int exitCode = exited ? child.ExitCode : 0;
                child.Dispose();

                if (exited)
                {
                    Console.Error.WriteLine("[MCP] 后台服务启动后立即退出（退出码 " + exitCode + "），详见 "
                        + Path.Combine(toolRoot, "last-mcp-host.log"));
                    return;
                }

                Console.WriteLine("[MCP] 已在后台启动无窗口 MCP 服务（PID " + childPid + "），客户端 args 配 [\"mcp-client\"] 即可接入。");
                Console.WriteLine("[MCP] 该服务随看板退出而结束（会先完成正在执行的请求）；需要立刻结束时用 stop-mcp 或 StopTiaMcp.bat。");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[MCP] 后台服务启动失败: " + ex.Message);
            }
        }

        internal static int Stop(string configPath)
        {
            string toolRoot = Path.GetDirectoryName(Path.GetFullPath(configPath));
            string file = PidFile(toolRoot);
            if (!File.Exists(file))
            {
                Console.WriteLine("没有找到 MCP 后台服务记录（" + file + "）；可能本来就未通过看板启动。");
                return 1;
            }

            int pid;
            string[] lines = File.ReadAllLines(file);
            if (lines.Length == 0 || !int.TryParse(lines[0].Trim(), out pid) || pid <= 0)
            {
                Console.Error.WriteLine("PID 文件内容无效: " + file);
                return 1;
            }

            try
            {
                using (Process target = Process.GetProcessById(pid))
                {
                    string image = target.MainModule == null ? "" : target.MainModule.FileName;
                    if (!string.Equals(Path.GetFileName(image), "TiaPortalSourceBridge.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.Error.WriteLine("PID " + pid + " 不是本工具进程（" + image + "），已拒绝结束，请手工核对。");
                        return 1;
                    }

                    target.Kill();
                    target.WaitForExit(5000);
                    Console.WriteLine("已结束 MCP 服务进程 " + pid + "。");
                }

                DeletePidFile(toolRoot);
                return 0;
            }
            catch (ArgumentException)
            {
                Console.WriteLine("MCP 服务进程 " + pid + " 已不存在，清理记录。");
                DeletePidFile(toolRoot);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("结束 MCP 服务失败: " + ex.Message);
                return 1;
            }
        }
    }
}
