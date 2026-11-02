using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace TiaPortalSourceBridge.Mcp
{
    internal interface IProjectWorker : IDisposable
    {
        bool IsAlive { get; }
        bool IsBusy { get; }
        Dictionary<string, object> Describe();
        Dictionary<string, object> Call(string tool, Dictionary<string, object> arguments);
    }

    // A separate STA executable owns each project's Siemens assemblies and Openness objects.
    internal sealed class ProjectWorker : IProjectWorker
    {
        private readonly Process process;
        private readonly StreamWriter input;
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024, RecursionLimit = 128 };
        private readonly int timeoutMs;
        private readonly object logGate = new object();
        private readonly string logPath;
        private Task<string> pending;
        private int requestId;
        private string pendingTool;
        private object lateResult;
        private string lateTool;
        private bool disposed;
        private bool broken;

        internal ProjectWorker(string executable, string configPath, string identity, int timeoutMs = 600000)
        {
            this.timeoutMs = timeoutMs;
            string directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath)), "logs", "project-workers");
            Directory.CreateDirectory(directory);
            string key;
            using (var sha = SHA256.Create()) key = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(identity.ToUpperInvariant()))).Replace("-", "").Substring(0, 20);
            logPath = Path.Combine(directory, key + ".log");
            var start = new ProcessStartInfo(executable, "mcp-project-worker --config " + Quote(configPath))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false)
            };
            process = new Process { StartInfo = start };
            process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
            {
                if (e.Data == null) return;
                try { lock (logGate) File.AppendAllText(logPath, DateTime.UtcNow.ToString("o") + " " + e.Data + Environment.NewLine, new UTF8Encoding(false)); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            };
            if (!process.Start()) throw new InvalidOperationException("无法启动项目后台进程。");
            // The pipe protocol stays UTF-8 even when mcp-host runs in a legacy Chinese console.
            input = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false), 65536) { AutoFlush = true };
            process.BeginErrorReadLine();
        }

        private static string Quote(string value)
        {
            // Config is a filename, so a double quote/newline cannot be part of a valid Windows path.
            if (value.IndexOfAny(new[] { '"', '\r', '\n' }) >= 0) throw new ArgumentException("配置路径包含非法字符。");
            return "\"" + Path.GetFullPath(value) + "\"";
        }
        public bool IsAlive { get { return !disposed && !process.HasExited; } }
        public bool IsBusy { get { return pending != null && !pending.IsCompleted; } }
        private Dictionary<string, object> ParseResponse(string line)
        {
            if (line == null) throw new IOException("项目后台进程已断开；操作结果可能未知，请核实后显式重新连接。日志：" + logPath);
            var reply = json.DeserializeObject(line) as Dictionary<string, object>;
            if (reply == null || !reply.ContainsKey("id") || Convert.ToInt32(reply["id"]) != requestId || reply.ContainsKey("error") || !reply.ContainsKey("result"))
                throw new IOException("项目后台返回了无效响应；不会自动重试。日志：" + logPath);
            var result = reply["result"] as Dictionary<string, object>;
            if (result == null) throw new IOException("项目后台结果格式错误。");
            return result;
        }
        public Dictionary<string, object> Describe()
        {
            if (pending != null && pending.IsCompleted)
            {
                lateTool = pendingTool;
                try { lateResult = ParseResponse(pending.GetAwaiter().GetResult()); }
                catch (Exception ex) { broken = true; lateResult = new { error = ex.Message }; }
                pending = null;
            }
            return new Dictionary<string, object> {
                { "worker_process_id", process.Id }, { "worker_alive", IsAlive },
                { "worker_state", !IsAlive ? "exited" : broken ? "faulted" : pending != null ? "busy_outcome_unknown" : "ready" },
                { "pending_tool", pending == null ? "" : pendingTool }, { "late_tool", lateTool ?? "" }, { "late_result", lateResult }, { "log_path", logPath }
            };
        }
        public Dictionary<string, object> Call(string tool, Dictionary<string, object> arguments)
        {
            if (pending != null) throw new InvalidOperationException("该项目上次请求超时，禁止继续或重试写入；请先查询 tia_connection_status 核实延迟结果。");
            if (!IsAlive || broken) throw new IOException("项目后台进程不可用；不会自动重放操作，请核实后显式调用 tia_connect。日志：" + logPath);
            requestId++;
            pendingTool = tool;
            try
            {
                input.WriteLine(json.Serialize(new { jsonrpc = "2.0", id = requestId, method = "tools/call", @params = new { name = tool, arguments = arguments } }));
                pending = process.StandardOutput.ReadLineAsync();
                if (!pending.Wait(timeoutMs)) throw new TimeoutException("项目请求超时，后台操作可能仍在执行；不会重试或终止写入。请查询 tia_connection_status。日志：" + logPath);
                string line = pending.GetAwaiter().GetResult();
                pending = null;
                return ParseResponse(line);
            }
            catch (TimeoutException) { throw; }
            catch { broken = true; throw; }
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            // EOF lets a running request finish, then releases Openness. Never kill a possible write.
            try { input.Close(); } catch (IOException) { } catch (InvalidOperationException) { }
            if (!IsBusy) try { process.WaitForExit(2000); } catch (InvalidOperationException) { }
            process.Dispose();
        }
    }
}
