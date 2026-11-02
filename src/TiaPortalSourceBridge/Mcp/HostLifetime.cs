using System;
using System.Diagnostics;
using System.Threading;

namespace TiaPortalSourceBridge.Mcp
{
    /// <summary>
    /// 后台 mcp-host 的生命周期：由看板以 TIA_BRIDGE_PARENT_PID 启动时，父进程（看板）
    /// 退出后自行结束。退出前等当前请求做完，避免把写到一半的工程操作打断。
    /// 手动启动的服务窗口没有这个环境变量，因此行为不变（常驻直到关窗口）。
    /// </summary>
    internal static class HostLifetime
    {
        private static int busy;
        private static int watchStarted;
        private static volatile bool stopRequested;
        private static Action stopTransport;
        internal static bool StopRequested { get { return stopRequested; } }
        internal static void RegisterTransport(Action close)
        {
            stopTransport = close;
            if (stopRequested && close != null) close();
        }

        internal static bool Busy
        {
            // 只在看板启动的宿主里使用，读多写少，Volatile 足够。
            get { return Volatile.Read(ref busy) > 0; }
        }

        internal static void Enter() { Interlocked.Increment(ref busy); }
        internal static void Leave() { Interlocked.Decrement(ref busy); }

        /// <summary>按环境变量启动父进程看护；没有该变量时什么都不做。</summary>
        internal static void WatchParentFromEnvironment()
        {
            string raw = Environment.GetEnvironmentVariable("TIA_BRIDGE_PARENT_PID");
            if (string.IsNullOrWhiteSpace(raw)) return;
            int pid;
            if (!int.TryParse(raw, out pid) || pid <= 0) return;
            if (Interlocked.Exchange(ref watchStarted, 1) == 1) return;

            Thread watchdog = new Thread(delegate() { Watch(pid); })
            {
                IsBackground = true,
                Name = "ParentWatchdog"
            };
            watchdog.Start();
        }

        private static void Watch(int parentPid)
        {
            while (true)
            {
                Thread.Sleep(1500);
                if (IsAlive(parentPid)) continue;

                // Reject new work, but finish an existing operation without a forced deadline.
                stopRequested = true;
                while (Busy)
                {
                    Thread.Sleep(500);
                }

                try { Console.Error.WriteLine("看板已关闭，后台 MCP 服务随之退出" + (Busy ? "（仍有请求在执行，未等其完成）" : "。") + Environment.NewLine); }
                catch (Exception) { }
                McpHostProcess.CleanupPidFile();
                var close = stopTransport;
                if (close != null) try { close(); } catch { }
                return;
            }
        }

        private static bool IsAlive(int pid)
        {
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    return !p.HasExited;
                }
            }
            catch (ArgumentException)
            {
                return false; // 进程已不存在
            }
            catch (Exception)
            {
                return true; // 查不到时保守认为还活着，宁可晚退也不误杀
            }
        }
    }
}
