using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace TiaPortalSourceBridge.Mcp
{
    // All Openness calls stay on the host's STA thread; the console client only relays JSON lines.
    internal static class PipeTransport
    {
        private static string PipeName
        {
            get { return "TiaPortalSourceBridge-V21-" + WindowsIdentity.GetCurrent().User.Value.Replace('-', '_'); }
        }

        private static string OnlinePipeName { get { return PipeName + "-Online"; } }

        public static int Host(string configPath, string projectPath, bool onlineOnly = false)
        {
            string selectedPipe = onlineOnly ? OnlinePipeName : PipeName;
            using (Mutex singleton = new Mutex(false, "Local\\" + selectedPipe))
            {
                bool owned;
                try { owned = singleton.WaitOne(0); }
                catch (AbandonedMutexException) { owned = true; }
                if (!owned)
                {
                    Console.Error.WriteLine("服务窗口已经运行，请使用现有窗口。");
                    return 1;
                }
                TextWriter originalError = Console.Error;
                HostLog hostLog = null;
                try
                {
                    string logPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath)), onlineOnly ? "last-online-host.log" : "last-mcp-host.log");
                    hostLog = new HostLog(originalError, logPath);
                    Console.SetError(hostLog);
                    Console.Title = onlineOnly ? "TIA MCP - Online Monitor" : "TIA MCP - Persistent Connection";
                    Console.WriteLine("TIA MCP 服务已启动。保持此窗口打开；Ctrl+C 退出。不自动下载 PLC；在线通道默认只读。");
                    Console.WriteLine("配置：" + Path.GetFullPath(configPath));
                    using (McpServer server = new McpServer(configPath))
                    {
                        try
                        {
                            if (!onlineOnly) server.ConfigureInitialProject(projectPath);
                            Console.Error.WriteLine("入口已就绪；工程将在 tia_connect 或首次工程操作时按需连接。");
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine("暂未连接：" + ex.Message + Environment.NewLine + "服务仍可用；打开 TIA 项目后调用 tia_connect。");
                        }
                        while (true)
                        {
                            PipeSecurity security = new PipeSecurity();
                            security.SetAccessRuleProtection(true, false);
                            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
                                PipeAccessRights.FullControl, AccessControlType.Deny));
                            security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User,
                                PipeAccessRights.FullControl, AccessControlType.Allow));
                            using (NamedPipeServerStream pipe = new NamedPipeServerStream(selectedPipe, PipeDirection.InOut, 1,
                                PipeTransmissionMode.Byte, PipeOptions.None, 65536, 65536, security))
                            {
                                Console.Error.WriteLine("等待 MCP 客户端接入（断开客户端不会释放 TIA 连接）……");
                                pipe.WaitForConnection();
                                Console.Error.WriteLine("MCP 客户端已接入。");
                                try
                                {
                                    using (StreamReader reader = new StreamReader(pipe, new UTF8Encoding(false), false, 65536, true))
                                    using (StreamWriter writer = new StreamWriter(pipe, new UTF8Encoding(false), 65536, true))
                                    {
                                        writer.AutoFlush = true;
                                        server.Run(reader, writer);
                                    }
                                }
                                catch (IOException)
                                {
                                    Console.Error.WriteLine("客户端已断开。TIA 连接继续保持。");
                                }
                            }
                        }
                    }
                }
                finally
                {
                    Console.SetError(originalError);
                    if (hostLog != null) hostLog.Dispose();
                    singleton.ReleaseMutex();
                }
            }
        }

        public static int Client(bool onlineOnly = false)
        {
            Console.InputEncoding = new UTF8Encoding(false);
            Console.OutputEncoding = new UTF8Encoding(false);
            try
            {
                using (NamedPipeClientStream pipe = new NamedPipeClientStream(".", onlineOnly ? OnlinePipeName : PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
                {
                    pipe.Connect(8000);
                    using (StreamReader reader = new StreamReader(pipe, new UTF8Encoding(false), false, 65536, true))
                    using (StreamWriter writer = new StreamWriter(pipe, new UTF8Encoding(false), 65536, true))
                    {
                        writer.AutoFlush = true;
                        Thread relay = new Thread(delegate()
                        {
                            try
                            {
                                string response;
                                while ((response = reader.ReadLine()) != null)
                                {
                                    Console.Out.WriteLine(response);
                                    Console.Out.Flush();
                                }
                            }
                            catch (IOException) { }
                            catch (ObjectDisposedException) { }
                        });
                        relay.IsBackground = true;
                        relay.Start();
                        string line;
                        while ((line = Console.ReadLine()) != null)
                        {
                            writer.WriteLine(line);
                        }
                        relay.Join(500);
                    }
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("连接服务窗口失败，请先双击 StartMcp.bat：" + ex.Message);
                return 1;
            }
        }

        private sealed class HostLog : TextWriter
        {
            private readonly TextWriter _console;
            private readonly StreamWriter _file;
            public override Encoding Encoding { get { return Encoding.UTF8; } }
            public HostLog(TextWriter console, string path)
            {
                _console = console;
                _file = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
                _file.AutoFlush = true;
            }
            public override void WriteLine(string value)
            {
                _console.WriteLine(value);
                _file.WriteLine(value);
            }
            public override void Write(string value)
            {
                _console.Write(value);
                _file.Write(value);
            }
            public override void Flush()
            {
                _console.Flush();
                _file.Flush();
            }
            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    try { _file.Flush(); _file.Dispose(); }
                    catch { }
                }
                base.Dispose(disposing);
            }
        }
    }
}
