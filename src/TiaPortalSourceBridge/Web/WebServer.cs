using System.Runtime.InteropServices;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using TiaPortalSourceBridge.Configuration;
using TiaPortalSourceBridge.Interactive;
using TiaPortalSourceBridge.Indexing;
using TiaPortalSourceBridge.Mcp;
using TiaPortalSourceBridge.Simulation;
using TiaPortalSourceBridge.Openness;

namespace TiaPortalSourceBridge.Web
{
    internal sealed partial class WebServer
    {
        private readonly HttpListener _listener;
        private readonly ActionTarget actionTargets = new ActionTarget();
        private readonly string _configPath;
        private readonly int _port;
        private readonly DateTime _startTime;
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024 };
        private volatile bool _running;
        private AppConfig _config;

        private static List<Dictionary<string, string>> _cachedPlcList = null;
        private static List<string> _cachedHmiList = null;
        private static DateTime _lastPlcScan = DateTime.MinValue;
        private static bool _isScanningPlcs = false;
        private static readonly object _plcLock = new object();
        private static DateTime _lastPortalScan = DateTime.MinValue;
        private static IList<PortalProcessInfo> _cachedPortalProcs = new List<PortalProcessInfo>();
        private static readonly object _portalLock = new object();
        private static string _lastDetectedLiveProject = "";
        private static readonly Dictionary<string, List<Dictionary<string, string>>> _projectPlcsCache =
            new Dictionary<string, List<Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);

        private static IList<PortalProcessInfo> GetRunningProjectsCached(AppConfig config, bool force = false)
        {
            lock (_portalLock)
            {
                if (!force && (DateTime.UtcNow - _lastPortalScan).TotalSeconds < 3 && _cachedPortalProcs != null && _cachedPortalProcs.Count > 0)
                {
                    return _cachedPortalProcs;
                }
            }

            IList<PortalProcessInfo> list;
            try
            {
                list = DiscoverDashboardProjects(config.ConfigFilePath);
            }
            catch
            {
                list = new List<PortalProcessInfo>();
            }

            lock (_portalLock)
            {
                _cachedPortalProcs = list;
                _lastPortalScan = DateTime.UtcNow;
            }
            return list;
        }

        private static bool PathsEqual(string a, string b)
        {
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            try
            {
                return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public WebServer(string configPath, int port)
        {
            _configPath = Path.GetFullPath(configPath);
            _port = port;
            _startTime = DateTime.UtcNow;
            _config = ConfigLoader.Load(_configPath);

            _listener = new HttpListener();
            _listener.Prefixes.Add("http://localhost:" + port + "/");
            _listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        public static void HideConsole()
        {
            try
            {
                IntPtr hWnd = GetConsoleWindow();
                if (hWnd != IntPtr.Zero)
                {
                    ShowWindow(hWnd, 0); // SW_HIDE = 0
                }
            }
            catch { }
        }

        // ???????????????:????????????,?????(???????),??????
        private static string CurrentBuildStamp()
        {
            string exe = System.Reflection.Assembly.GetEntryAssembly().Location;
            return exe.ToLowerInvariant() + "|" + File.GetLastWriteTimeUtc(exe).Ticks;
        }

        private static string QueryRunningBuildStamp(int port)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/api/build");
                req.Timeout = 1500;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    var map = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
                    object v;
                    return map != null && map.TryGetValue("stamp", out v) ? Convert.ToString(v) : "";
                }
            }
            catch { return ""; } // ????? /api/build -> ????
        }

        private static void ReplaceStaleInstance(int port)
        {
            if (!IsPortResponding(port)) return;
            if (string.Equals(QueryRunningBuildStamp(port), CurrentBuildStamp(), StringComparison.OrdinalIgnoreCase)) return;

            try
            {
                var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/api/shutdown");
                req.Method = "POST"; req.ContentLength = 0; req.Timeout = 2000;
                using (req.GetResponse()) { }
            }
            catch { }

            for (int i = 0; i < 16 && IsPortResponding(port); i++) Thread.Sleep(500);
            if (!IsPortResponding(port)) return;

            // ?????????(?????? UAC ????):???????? web ??????
            int self = Process.GetCurrentProcess().Id;
            try
            {
                using (var searcher = new System.Management.ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'TiaPortalSourceBridge.exe'"))
                {
                    foreach (System.Management.ManagementObject mo in searcher.Get())
                    {
                        int pid = Convert.ToInt32(mo["ProcessId"]);
                        string cmd = Convert.ToString(mo["CommandLine"]) ?? "";
                        if (pid == self) continue;
                        if (cmd.IndexOf(" web ", StringComparison.OrdinalIgnoreCase) < 0 && cmd.IndexOf(" dashboard ", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        try
                        {
                            var kill = Process.Start(new ProcessStartInfo("taskkill.exe", "/PID " + pid + " /T /F") { CreateNoWindow = true, UseShellExecute = false });
                            kill.WaitForExit(5000);
                        }
                        catch { }
                    }
                }
            }
            catch { }
            for (int i = 0; i < 10 && IsPortResponding(port); i++) Thread.Sleep(300);
        }
        private static bool IsPortResponding(int port)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/api/status");
                req.Timeout = 600;
                req.Method = "GET";
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    return resp.StatusCode == HttpStatusCode.OK;
                }
            }
            catch
            {
                return false;
            }
        }

        public static int Start(string configPath, int preferredPort = 18980, bool openBrowser = true, bool hideConsole = false)
        {
            if (hideConsole)
            {
                HideConsole();
            }

            // 看板与 MCP 使用同一个启动入口：先确保后台有一个无窗口的 mcp-host，
            // 客户端配 args=["mcp-client"] 即可直接接入；已有宿主时这里什么都不做。
            ReplaceStaleInstance(preferredPort);
            McpHostProcess.StartIfNeeded(configPath);

            // Reuse existing instance if preferredPort is already responding
            if (IsPortResponding(preferredPort))
            {
                if (openBrowser)
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "http://localhost:" + preferredPort + "/",
                            UseShellExecute = true
                        });
                    }
                    catch { }
                }
                return 0;
            }

            int port = preferredPort;
            WebServer server = null;

            for (int attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    server = new WebServer(configPath, port);
                    server._listener.Start();
                    break;
                }
                catch (HttpListenerException)
                {
                    port++;
                    if (server != null)
                    {
                        try { server._listener.Close(); } catch { }
                    }
                }
            }

            if (server == null || !server._listener.IsListening)
            {
                Console.Error.WriteLine("无法在端口 " + preferredPort + " 到 " + port + " 启动 Web 服务。");
                return 1;
            }

            server._running = true;
            server.StartBrowserLifetime();
            string url = "http://localhost:" + port + "/";

            Console.WriteLine("=================================================");
            Console.WriteLine("  TIA Portal Source Bridge - 可视化控制看板");
            Console.WriteLine("=================================================");
            Console.WriteLine("服务地址: " + url);
            Console.WriteLine("当前配置: " + Path.GetFullPath(configPath));
            Console.WriteLine("按 Ctrl+C 可停止看板服务。");
            Console.WriteLine();

            if (openBrowser)
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = url,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    Console.WriteLine("自动打开浏览器失败，请手动访问 " + url + " (" + ex.Message + ")");
                }
            }

            server.ListenLoop();
            return 0;
        }

        private void ListenLoop()
        {
            while (_running)
            {
                try
                {
                    HttpListenerContext context = _listener.GetContext();
                    ThreadPool.QueueUserWorkItem(state => ProcessRequest((HttpListenerContext)state), context);
                }
                catch (HttpListenerException)
                {
                    if (!_running) break;
                }
                catch (Exception ex)
                {
                    if (!_running) break;
                    Console.Error.WriteLine("Web 服务监听错误: " + ex.Message);
                }
            }
        }

        private void ProcessRequest(HttpListenerContext context)
        {
            HttpListenerRequest request = context.Request;
            HttpListenerResponse response = context.Response;

            // CORS headers
            response.Headers.Add("Access-Control-Allow-Origin", "*");
            response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

            if (request.HttpMethod == "OPTIONS")
            {
                response.StatusCode = 204;
                response.Close();
                return;
            }

            try
            {
                string rawUrl = request.RawUrl ?? "/";
                string path = rawUrl.Split('?')[0];

                if (HandleIo(context, path)) return;

                if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
                {
                    HandleApi(context, path);
                }
                else
                {
                    ServeStaticFile(context, path);
                }
            }
            catch (Exception ex)
            {
                SendJson(response, 500, new Dictionary<string, object>
                {
                    { "error", ex.Message },
                    { "stack", ex.StackTrace }
                });
            }
        }

        private void ServeStaticFile(HttpListenerContext context, string path)
        {
            HttpListenerResponse response = context.Response;
            string rootDir = Path.GetDirectoryName(_configPath);
            string webDir = Path.Combine(rootDir, "web");

            if (path == "/" || string.IsNullOrWhiteSpace(path))
            {
                path = "/index.html";
            }

            string relativePath = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            string localFilePath = Path.Combine(webDir, relativePath);

            if (File.Exists(localFilePath))
            {
                string ext = Path.GetExtension(localFilePath).ToLowerInvariant();
                string contentType = "text/html; charset=utf-8";
                if (ext == ".css") contentType = "text/css; charset=utf-8";
                else if (ext == ".js") contentType = "application/javascript; charset=utf-8";
                else if (ext == ".json") contentType = "application/json; charset=utf-8";
                else if (ext == ".svg") contentType = "image/svg+xml";
                else if (ext == ".ico") contentType = "image/x-icon";
                else if (ext == ".png") contentType = "image/png";

                byte[] bytes = File.ReadAllBytes(localFilePath);
                response.ContentType = contentType;
                response.ContentLength64 = bytes.Length;
                response.OutputStream.Write(bytes, 0, bytes.Length);
                response.Close();
                return;
            }

            // Fallback to embedded default HTML
            if (path == "/index.html" || path == "/")
            {
                byte[] htmlBytes = Encoding.UTF8.GetBytes(EmbeddedAssets.GetDashboardHtml());
                response.ContentType = "text/html; charset=utf-8";
                response.ContentLength64 = htmlBytes.Length;
                response.OutputStream.Write(htmlBytes, 0, htmlBytes.Length);
                response.Close();
                return;
            }

            response.StatusCode = 404;
            response.Close();
        }

        private static string GetQueryParam(HttpListenerRequest request, string name)
        {
            string rawUrl = request.RawUrl;
            if (string.IsNullOrEmpty(rawUrl)) return "";
            int qIdx = rawUrl.IndexOf('?');
            if (qIdx < 0 || qIdx >= rawUrl.Length - 1) return "";
            string query = rawUrl.Substring(qIdx + 1);
            string[] pairs = query.Split('&');
            foreach (string pair in pairs)
            {
                int eq = pair.IndexOf('=');
                if (eq > 0)
                {
                    string k = pair.Substring(0, eq);
                    if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase))
                    {
                        string v = pair.Substring(eq + 1);
                        try
                        {
                            return Uri.UnescapeDataString(v.Replace("+", " "));
                        }
                        catch
                        {
                            return v;
                        }
                    }
                }
            }
            return "";
        }

        private static void DetectTiaInfo(string configuredProject, out string activeProject, out string detectedVersion, out string versionReason, out List<Dictionary<string, object>> procs)
        {
            procs = new List<Dictionary<string, object>>();
            detectedVersion = "";
            versionReason = "";
            activeProject = configuredProject;

            foreach (var p in Process.GetProcessesByName("Siemens.Automation.Portal"))
            {
                try
                {
                    string exe = p.MainModule != null ? p.MainModule.FileName : "";
                    string ver = "";
                    if (!string.IsNullOrEmpty(exe))
                    {
                        Match m = Regex.Match(exe, @"Portal\s+V(?<ver>\d+(?:[._]\d+)?)", RegexOptions.IgnoreCase);
                        if (m.Success) ver = "V" + m.Groups["ver"].Value;
                    }
                    if (string.IsNullOrEmpty(ver) && p.MainModule != null && p.MainModule.FileVersionInfo != null)
                    {
                        int maj = p.MainModule.FileVersionInfo.FileMajorPart;
                        if (maj >= 1000) maj = maj / 100;
                        if (maj > 0) ver = "V" + maj;
                    }

                    procs.Add(new Dictionary<string, object>
                    {
                        { "pid", p.Id },
                        { "name", p.ProcessName },
                        { "title", p.MainWindowTitle },
                        { "memory_mb", (p.WorkingSet64 / 1024 / 1024) },
                        { "version", ver }
                    });

                    if (string.IsNullOrEmpty(detectedVersion) && !string.IsNullOrEmpty(ver))
                    {
                        detectedVersion = ver;
                        versionReason = "运行中的博途进程 (PID: " + p.Id + ")";
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }

            // If active project is on disk, detect its version
            if (File.Exists(activeProject))
            {
                string ext = Path.GetExtension(activeProject);
                Match m = Regex.Match(ext, @"^\.ap(?<ver>\d+(?:_\d+)?)$", RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    string projVer = "V" + m.Groups["ver"].Value.Replace('_', '.');
                    if (string.IsNullOrEmpty(detectedVersion))
                    {
                        detectedVersion = projVer;
                        versionReason = "当前工程版本 (" + Path.GetFileName(activeProject) + ")";
                    }
                }
            }

            if (string.IsNullOrEmpty(detectedVersion))
            {
                detectedVersion = "V17";
                versionReason = "默认版本";
            }
        }

        private static List<Dictionary<string, string>> GetPlcsOffline(string rootDir, string projectPath)
        {
            lock (_plcLock)
            {
                if (_cachedPlcList != null && _cachedPlcList.Count > 0)
                {
                    return _cachedPlcList;
                }
            }

            var result = new List<Dictionary<string, string>>();

            // 1. From block-backups/*.meta.json (fast and authoritative)
            string backupDir = Path.Combine(rootDir, "block-backups");
            if (Directory.Exists(backupDir))
            {
                var metaFiles = Directory.GetFiles(backupDir, "*.meta.json")
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Take(30);

                foreach (var fi in metaFiles)
                {
                    try
                    {
                        var map = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(fi.FullName, Encoding.UTF8));
                        if (map != null && map.ContainsKey("Plc"))
                        {
                            string metaProj = map.ContainsKey("ProjectPath") ? Convert.ToString(map["ProjectPath"]) : "";
                            if (!string.IsNullOrEmpty(metaProj) && !PathsEqual(metaProj, projectPath))
                            {
                                continue;
                            }
                            string fullPlc = Convert.ToString(map["Plc"]);
                            if (!string.IsNullOrWhiteSpace(fullPlc))
                            {
                                string cpu = ExtractCpuName(fullPlc);
                                string station = ExtractStationName(fullPlc);
                                string hw = DetectCpuHardwareType(projectPath, cpu);
                                string order = DetectCpuOrderNumber(projectPath, cpu);
                                string display = !string.IsNullOrEmpty(hw)
                                    ? cpu + " [" + hw + "] (" + station + ")"
                                    : FormatPlcDisplayName(fullPlc);
                                if (!result.Any(r => r["fullName"] == fullPlc || r["cpuName"] == cpu))
                                {
                                    result.Add(new Dictionary<string, string>
                                    {
                                        { "displayName", display },
                                        { "cpuName", cpu },
                                        { "hardwareType", hw },
                                        { "orderNumber", order },
                                        { "stationName", station },
                                        { "fullName", fullPlc }
                                    });
                                }
                            }
                        }
                    }
                    catch { }
                }
            }

            // 2. From project index (.tia-source-index)
            if (result.Count == 0 && !string.IsNullOrEmpty(projectPath))
            {
                string catFile = Path.Combine(Path.GetDirectoryName(projectPath), ".tia-source-index", Path.GetFileName(projectPath), "catalog.json");
                if (File.Exists(catFile))
                {
                    try
                    {
                        string json = File.ReadAllText(catFile, Encoding.UTF8);
                        IndexCatalog cat = new JavaScriptSerializer().Deserialize<IndexCatalog>(json);
                        if (cat != null && cat.objects != null)
                        {
                            var owners = cat.objects
                                .Where(o => (o.kind == "Block" || o.kind == "TagTable") && !string.IsNullOrWhiteSpace(o.owner))
                                .Select(o => o.owner)
                                .Distinct(StringComparer.OrdinalIgnoreCase);

                            foreach (var owner in owners)
                            {
                                string cpu = ExtractCpuName(owner);
                                string station = ExtractStationName(owner);
                                string hw = DetectCpuHardwareType(projectPath, cpu);
                                string order = DetectCpuOrderNumber(projectPath, cpu);
                                string display = !string.IsNullOrEmpty(hw)
                                    ? cpu + " [" + hw + "] (" + station + ")"
                                    : FormatPlcDisplayName(owner);
                                if (!result.Any(r => r["fullName"] == owner || r["cpuName"] == cpu))
                                {
                                    result.Add(new Dictionary<string, string>
                                    {
                                        { "displayName", display },
                                        { "cpuName", cpu },
                                        { "hardwareType", hw },
                                        { "orderNumber", order },
                                        { "stationName", station },
                                        { "fullName", owner }
                                    });
                                }
                            }
                        }
                    }
                    catch { }
                }
            }

            // 3. Fallback to direct PEData hardware scan if no index yet
            if (result.Count == 0 && !string.IsNullOrEmpty(projectPath))
            {
                try
                {
                    string dir = Path.GetDirectoryName(projectPath);
                    string peData = Path.Combine(dir, "System", "PEData.plf");
                    if (File.Exists(peData))
                    {
                        string cpu = "PLC_CPU1513";
                        string station = "S71500/ET200MP station_1";
                        string hw = DetectCpuHardwareType(projectPath, cpu);
                        string order = DetectCpuOrderNumber(projectPath, cpu);
                        string display = !string.IsNullOrEmpty(hw)
                            ? cpu + " [" + hw + "] (" + station + ")"
                            : cpu;
                        result.Add(new Dictionary<string, string>
                        {
                            { "displayName", display },
                            { "cpuName", cpu },
                            { "hardwareType", hw },
                            { "orderNumber", order },
                            { "stationName", station },
                            { "fullName", station + " / " + cpu + " / " + cpu }
                        });
                    }
                }
                catch { }
            }

            lock (_plcLock)
            {
                if (result.Count > 0)
                {
                    _cachedPlcList = result;
                }
            }

            return result;
        }

        private static void TriggerLegacyPlcScanAsync(string configPath, string projectPath)
        {
            lock (_plcLock)
            {
                if (_isScanningPlcs) return;
                _isScanningPlcs = true;
            }

            ThreadPool.QueueUserWorkItem(state =>
            {
                try
                {
                    if (File.Exists(projectPath))
                    {
                        string exePath = System.Reflection.Assembly.GetEntryAssembly().Location;
                        string rootDir = Path.GetDirectoryName(Path.GetFullPath(configPath));
                        ProcessStartInfo psi = new ProcessStartInfo
                        {
                            FileName = exePath,
                            Arguments = "list-plcs --project \"" + projectPath + "\" --config \"" + configPath + "\"",
                            WorkingDirectory = rootDir,
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true,
                            StandardOutputEncoding = Encoding.UTF8,
                            StandardErrorEncoding = Encoding.UTF8
                        };

                        var plcs = new List<Dictionary<string, string>>();
                        var hmis = new List<string>();

                        using (Process p = new Process())
                        {
                            p.StartInfo = psi;
                            bool inPlc = false;
                            bool inHmi = false;
                            p.OutputDataReceived += (s, e) =>
                            {
                                if (e.Data == null) return;
                                string trimmed = e.Data.Trim();
                                if (trimmed.StartsWith("可用 PLC:", StringComparison.OrdinalIgnoreCase))
                                {
                                    inPlc = true; inHmi = false; return;
                                }
                                if (trimmed.StartsWith("可用 HMI:", StringComparison.OrdinalIgnoreCase))
                                {
                                    inPlc = false; inHmi = true; return;
                                }
                                if (trimmed.StartsWith("- "))
                                {
                                    string item = trimmed.Substring(2).Trim();
                                    if (inPlc)
                                    {
                                        string cpu = ExtractCpuName(item);
                                        string station = ExtractStationName(item);
                                        string display = FormatPlcDisplayName(item);
                                        plcs.Add(new Dictionary<string, string>
                                        {
                                            { "displayName", display },
                                            { "cpuName", cpu },
                                            { "stationName", station },
                                            { "fullName", item }
                                        });
                                    }
                                    else if (inHmi)
                                    {
                                        hmis.Add(item);
                                    }
                                }
                            };

                            p.Start();
                            p.BeginOutputReadLine();
                            p.BeginErrorReadLine();
                            p.WaitForExit(10000);
                        }

                        lock (_plcLock)
                        {
                            if (plcs.Count > 0)
                            {
                                _cachedPlcList = plcs;
                                _cachedHmiList = hmis;
                                _lastPlcScan = DateTime.UtcNow;
                            }
                        }
                    }
                }
                catch { }
                finally
                {
                    lock (_plcLock) { _isScanningPlcs = false; }
                }
            });
        }

        private static readonly Dictionary<string, string> _cpuHwCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> _cpuOrderCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static string DetectCpuHardwareType(string projectPath, string plcName)
        {
            if (string.IsNullOrEmpty(projectPath)) return "";
            string key = projectPath + "::" + (plcName ?? "");
            lock (_cpuHwCache)
            {
                if (_cpuHwCache.ContainsKey(key)) return _cpuHwCache[key];
            }

            string result = "";
            try
            {
                string projDir = Path.GetDirectoryName(projectPath);
                string peData = Path.Combine(projDir, "System", "PEData.plf");
                if (File.Exists(peData))
                {
                    byte[] bytes;
                    using (var fs = new FileStream(peData, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        bytes = new byte[fs.Length];
                        fs.Read(bytes, 0, (int)fs.Length);
                    }
                    string latin = Encoding.GetEncoding("ISO-8859-1").GetString(bytes);

                    if (!string.IsNullOrEmpty(plcName))
                    {
                        int idx = latin.IndexOf(plcName);
                        while (idx >= 0)
                        {
                            int chunkLen = Math.Min(600, latin.Length - idx);
                            string chunk = latin.Substring(idx, chunkLen);
                            Match mHw = Regex.Match(chunk, @"CPU[\/](CPU_[A-Za-z0-9_]+)[\/]MDD_([A-Za-z0-9_\-]+)");
                            if (mHw.Success)
                            {
                                string raw = mHw.Groups[1].Value;
                                string fmt = raw.Replace("CPU_", "CPU ").Replace('_', '-');
                                if (fmt.EndsWith("-PN")) fmt = fmt.Substring(0, fmt.Length - 3) + " PN";
                                result = fmt;
                                break;
                            }
                            idx = latin.IndexOf(plcName, idx + plcName.Length);
                        }
                    }

                    if (string.IsNullOrEmpty(result))
                    {
                        Match mHw = Regex.Match(latin, @"CPU[\/](CPU_[A-Za-z0-9_]+)[\/]MDD_([A-Za-z0-9_\-]+)");
                        if (mHw.Success)
                        {
                            string raw = mHw.Groups[1].Value;
                            string fmt = raw.Replace("CPU_", "CPU ").Replace('_', '-');
                            if (fmt.EndsWith("-PN")) fmt = fmt.Substring(0, fmt.Length - 3) + " PN";
                            result = fmt;
                        }
                    }

                    if (string.IsNullOrEmpty(result))
                    {
                        string order = DetectCpuOrderNumber(projectPath, plcName);
                        if (!string.IsNullOrEmpty(order))
                        {
                            if (order.Contains("511-1") || order.Contains("511")) result = "CPU 1511-1 PN";
                            else if (order.Contains("512-1") || order.Contains("512")) result = "CPU 1512-1 PN";
                            else if (order.Contains("513-1") || order.Contains("513")) result = "CPU 1513-1 PN";
                            else if (order.Contains("515-2") || order.Contains("515")) result = "CPU 1515-2 PN";
                            else if (order.Contains("516-3") || order.Contains("516")) result = "CPU 1516-3 PN/DP";
                            else if (order.Contains("517-3") || order.Contains("517")) result = "CPU 1517-3 PN/DP";
                            else if (order.Contains("518-4") || order.Contains("518")) result = "CPU 1518-4 PN/DP";
                        }
                    }
                }
            }
            catch { }

            lock (_cpuHwCache) { _cpuHwCache[key] = result; }
            return result;
        }

        private static string DetectCpuOrderNumber(string projectPath, string plcName)
        {
            if (string.IsNullOrEmpty(projectPath)) return "";
            string key = projectPath + "::" + (plcName ?? "");
            lock (_cpuOrderCache)
            {
                if (_cpuOrderCache.ContainsKey(key)) return _cpuOrderCache[key];
            }

            string result = "";
            try
            {
                string projDir = Path.GetDirectoryName(projectPath);
                string peData = Path.Combine(projDir, "System", "PEData.plf");
                if (File.Exists(peData))
                {
                    byte[] bytes;
                    using (var fs = new FileStream(peData, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        bytes = new byte[fs.Length];
                        fs.Read(bytes, 0, (int)fs.Length);
                    }
                    string latin = Encoding.GetEncoding("ISO-8859-1").GetString(bytes);
                    if (!string.IsNullOrEmpty(plcName))
                    {
                        int idx = latin.IndexOf(plcName);
                        while (idx >= 0)
                        {
                            int chunkLen = Math.Min(600, latin.Length - idx);
                            string chunk = latin.Substring(idx, chunkLen);
                            Match mHw = Regex.Match(chunk, @"MDD_([0-9A-Z]{3,5}[0-9A-Z\-]{6,16})");
                            if (mHw.Success)
                            {
                                string order = mHw.Groups[1].Value;
                                if (order.StartsWith("6ES7"))
                                {
                                    result = "6ES7 " + order.Substring(4);
                                }
                                else
                                {
                                    result = order;
                                }
                                break;
                            }
                            idx = latin.IndexOf(plcName, idx + plcName.Length);
                        }
                    }
                }
            }
            catch { }

            lock (_cpuOrderCache) { _cpuOrderCache[key] = result; }
            return result;
        }

        private static string ExtractCpuName(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return "";
            string[] parts = fullName.Split(new[] { " / " }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length <= 1) return fullName.Trim();
            // In TIA Portal: parts[0] is Station Name (e.g. "S71500/ET200MP station_1"), parts[last] is CPU Name (e.g. "PLC_CPU1513")
            string candidate = parts[parts.Length - 1].Trim();
            if (!string.IsNullOrEmpty(candidate)) return candidate;
            return parts[0].Trim();
        }

        private static string ExtractStationName(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return "";
            string[] parts = fullName.Split(new[] { " / " }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 1 ? parts[0].Trim() : "";
        }

        private static string FormatPlcDisplayName(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return "";
            string cpu = ExtractCpuName(fullName);
            string station = ExtractStationName(fullName);
            if (!string.IsNullOrEmpty(station) && !string.Equals(cpu, station, StringComparison.OrdinalIgnoreCase))
            {
                return cpu + " (" + station + ")";
            }
            return cpu;
        }

        private static string ExtractShortName(string fullName)
        {
            return ExtractCpuName(fullName);
        }

        private void HandleApi(HttpListenerContext context, string path)
        {
            HttpListenerRequest request = context.Request;
            HttpListenerResponse response = context.Response;
            string method = request.HttpMethod.ToUpperInvariant();

            string rootDir = Path.GetDirectoryName(_configPath);
            RecentSettings recent = RecentSettings.Load(rootDir);

            if ((path == "/api/browser/heartbeat" || path == "/api/browser/release") && method == "POST")
            {
                var payload = _json.Deserialize<Dictionary<string, object>>(ReadBody(request));
                string clientId = payload != null && payload.ContainsKey("client_id") ? Convert.ToString(payload["client_id"]) : "";
                string pageId = payload != null && payload.ContainsKey("page_id") ? Convert.ToString(payload["page_id"]) : clientId;
                if (clientId.Length < 8 || clientId.Length > 128) { SendJson(response, 400, new { error = "Invalid browser client" }); return; }
                lock (browserLifetimeGate)
                {
                    if (shutdownRequested) { SendJson(response, 503, new { error = "Service stopping" }); return; }
                    if (path.EndsWith("heartbeat")) browserLeases.Heartbeat(clientId, pageId, DateTime.UtcNow);
                    else browserLeases.Release(clientId, pageId, DateTime.UtcNow);
                }
                SendJson(response, 200, new { success = true });
                return;
            }

            if (path.Equals("/api/build", StringComparison.OrdinalIgnoreCase))
            {
                SendJson(response, 200, new Dictionary<string, object> { { "stamp", CurrentBuildStamp() } });
                return;
            }

            if (path.Equals("/api/shutdown", StringComparison.OrdinalIgnoreCase) && method == "POST")
            {
                SendJson(response, 200, new Dictionary<string, object> { { "success", true }, { "message", "控制看板服务正在安全退出..." } });
                RequestDashboardShutdown();
                return;
            }

            if (path.Equals("/api/status", StringComparison.OrdinalIgnoreCase))
            {
                bool forceRefresh = request.QueryString["refresh"] == "true";
                var runningPortals = GetRunningProjectsCached(_config, forceRefresh);

                string activeProject = !string.IsNullOrWhiteSpace(recent.LastProjectPath) ? recent.LastProjectPath : _config.ProjectPath;

                // Live Project Auto-adaptation:
                var openLiveProjects = runningPortals.Where(p => !string.IsNullOrEmpty(p.ProjectPath)).ToList();
                if (openLiveProjects.Count > 0)
                {
                    string currentLive = openLiveProjects.Any(p => PathsEqual(p.ProjectPath, activeProject))
                        ? activeProject : openLiveProjects[0].ProjectPath;
                    if (!PathsEqual(_lastDetectedLiveProject, currentLive))
                    {
                        // Detected a newly opened project in TIA Portal (or first startup)
                        _lastDetectedLiveProject = currentLive;
                        if (!PathsEqual(activeProject, currentLive))
                        {
                            activeProject = currentLive;
                            recent.LastProjectPath = activeProject;
                            recent.AddRecentProject(activeProject);
                            recent.Save();
                            _config.OverrideProject(activeProject, true);
                            lock (_plcLock) { _cachedPlcList = null; }
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(activeProject))
                {
                    recent.AddRecentProject(activeProject);
                }

                string detectedVersion;
                string versionReason;
                List<Dictionary<string, object>> tiaProcs;
                DetectTiaInfo(activeProject, out activeProject, out detectedVersion, out versionReason, out tiaProcs);
                var osProcesses = tiaProcs;
                tiaProcs = runningPortals.OrderByDescending(p => PathsEqual(p.ProjectPath, activeProject)).Select(p => {
                    var info = osProcesses.FirstOrDefault(row => Convert.ToInt32(row["pid"]) == p.ProcessId);
                    return info ?? new Dictionary<string, object> { { "pid", p.ProcessId }, { "version", p.PortalVersion }, { "memory_mb", 0 } };
                }).ToList();

                if (!string.IsNullOrWhiteSpace(activeProject))
                {
                    detectedVersion = AppConfig.DetectProjectVersion(activeProject);
                    versionReason = openLiveProjects.Any(p => PathsEqual(p.ProjectPath, activeProject))
                        ? "Openness 实时识别当前打开工程" : "本地工程文件版本（当前未打开）";
                }

                _config.TiaPortalVersion = detectedVersion;

                // Build Available Projects List for Dropdown
                var availableProjects = new List<Dictionary<string, object>>();

                // 1. Live Running Projects in TIA Portal
                foreach (var lp in openLiveProjects)
                {
                    bool isActive = PathsEqual(lp.ProjectPath, activeProject);
                    if (!availableProjects.Any(x => PathsEqual(Convert.ToString(x["path"]), lp.ProjectPath)))
                    {
                        availableProjects.Add(new Dictionary<string, object>
                        {
                            { "path", lp.ProjectPath },
                            { "name", Path.GetFileName(lp.ProjectPath) },
                            { "pid", lp.ProcessId },
                            { "version", lp.PortalVersion },
                            { "is_running", true },
                            { "is_active", isActive }
                        });
                    }
                }

                // 2. Recent & Local Projects
                if (recent.RecentProjectPaths != null)
                {
                    foreach (var hist in recent.RecentProjectPaths)
                    {
                        if (!string.IsNullOrWhiteSpace(hist) && File.Exists(hist) && !availableProjects.Any(x => PathsEqual(Convert.ToString(x["path"]), hist)))
                        {
                            availableProjects.Add(new Dictionary<string, object>
                            {
                                { "path", hist },
                                { "name", Path.GetFileName(hist) },
                                { "pid", 0 },
                                { "version", AppConfig.DetectProjectVersion(hist) },
                                { "is_running", false },
                                { "is_active", PathsEqual(hist, activeProject) }
                            });
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(_config.ProjectPath) && File.Exists(_config.ProjectPath) && !availableProjects.Any(x => PathsEqual(Convert.ToString(x["path"]), _config.ProjectPath)))
                {
                    availableProjects.Add(new Dictionary<string, object>
                    {
                        { "path", _config.ProjectPath },
                        { "name", Path.GetFileName(_config.ProjectPath) },
                        { "pid", 0 },
                        { "version", AppConfig.DetectProjectVersion(_config.ProjectPath) },
                        { "is_running", false },
                        { "is_active", PathsEqual(_config.ProjectPath, activeProject) }
                    });
                }

                // Fast PLC discovery (non-blocking)
                var availablePlcs = GetPlcsFast(rootDir, activeProject);

                string currentPlc = !string.IsNullOrWhiteSpace(recent.LastPlcName) ? recent.LastPlcName : _config.PlcName;
                string currentStation = "";
                string currentHw = DetectCpuHardwareType(activeProject, currentPlc);
                string currentOrder = DetectCpuOrderNumber(activeProject, currentPlc);
                string currentDisplay = !string.IsNullOrEmpty(currentHw) ? currentPlc + " [" + currentHw + "]" : currentPlc;
                bool liveProject = openLiveProjects.Any(p => PathsEqual(p.ProjectPath, activeProject));
                if (liveProject) { currentHw = ""; currentOrder = ""; }
                if (liveProject && availablePlcs.Count == 0) { currentPlc = ""; currentDisplay = "正在实时读取 PLC…"; }

                if (availablePlcs.Count > 0)
                {
                    var matched = availablePlcs.FirstOrDefault(p => string.Equals(p["fullName"], currentPlc, StringComparison.OrdinalIgnoreCase)) ?? availablePlcs.FirstOrDefault(p =>
                        string.Equals(p["cpuName"], currentPlc, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(p["displayName"], currentPlc, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(p["fullName"], currentPlc, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(p["stationName"], currentPlc, StringComparison.OrdinalIgnoreCase));

                    if (matched != null)
                    {
                        currentPlc = matched["cpuName"];
                        currentStation = matched["stationName"];
                        currentDisplay = matched["displayName"];
                        if (liveProject) { currentHw = matched.ContainsKey("hardwareType") ? matched["hardwareType"] : ""; currentOrder = matched.ContainsKey("orderNumber") ? matched["orderNumber"] : ""; }
                    }
                    else
                    {
                        currentPlc = availablePlcs[0]["cpuName"];
                        currentStation = availablePlcs[0]["stationName"];
                        currentDisplay = availablePlcs[0]["displayName"];
                        if (liveProject) { currentHw = availablePlcs[0].ContainsKey("hardwareType") ? availablePlcs[0]["hardwareType"] : ""; currentOrder = availablePlcs[0].ContainsKey("orderNumber") ? availablePlcs[0]["orderNumber"] : ""; }
                        recent.LastPlcName = currentPlc;
                        recent.Save();
                    }
                }

                int backupCount = 0;
                string backupDir = Path.Combine(rootDir, "block-backups");
                if (Directory.Exists(backupDir))
                {
                    backupCount = Directory.GetFiles(backupDir, "*.meta.json").Length;
                }

                int configBackupCount = 0;
                string configBackupDir = Path.Combine(rootDir, "configuration-backups");
                if (Directory.Exists(configBackupDir))
                {
                    configBackupCount = Directory.GetFiles(configBackupDir, "*.json").Length;
                }

                // Check index status
                bool hasIndex = false;
                string indexPath = "";
                if (!string.IsNullOrWhiteSpace(activeProject))
                {
                    indexPath = Path.Combine(Path.GetDirectoryName(activeProject), ".tia-source-index", Path.GetFileName(activeProject), "catalog.json");
                    hasIndex = File.Exists(indexPath);
                }

                var exactTargets = availablePlcs.Where(p => p["cpuName"] == currentPlc && p["stationName"] == currentStation).ToArray();
                string targetPlc = exactTargets.Length == 1 ? exactTargets[0]["fullName"] : "";
                var status = new Dictionary<string, object>
                {
                    { "tool_name", "TIA Portal Source Bridge" },
                    { "tool_version", "v4.3.0" },
                    { "server_process_id", Process.GetCurrentProcess().Id },
                    { "browser_clients", BrowserClientCount() },
                    { "tool_count", DashboardTools(_configPath).Length },
                    { "uptime_seconds", (int)(DateTime.UtcNow - _startTime).TotalSeconds },
                    { "config_path", _configPath },
                    { "project_path", activeProject },
                    { "plc_name", currentPlc },
                    { "target_plc", targetPlc },
                    { "target_token", actionTargets.Sign(activeProject, targetPlc) },
                    { "plc_hardware", currentHw },
                    { "plc_order_number", currentOrder },
                    { "plc_station", currentStation },
                    { "plc_display", currentDisplay },
                    { "available_plcs", availablePlcs },
                    { "available_hmis", _cachedHmiList ?? new List<string>() },
                    { "source_directory", _config.SourceDirectory },
                    { "tia_portal_version", detectedVersion },
                    { "detected_version", detectedVersion },
                    { "version_source", versionReason },
                    { "tia_processes", tiaProcs },
                    { "backup_count", backupCount },
                    { "config_backup_count", configBackupCount },
                    { "has_index", hasIndex },
                    { "index_path", indexPath },
                    { "current_action", ActionRunner.Current },
                    { "active_project", activeProject },
                    { "active_project_name", Path.GetFileName(activeProject) },
                    { "active_project_is_running", openLiveProjects.Any(p => PathsEqual(p.ProjectPath, activeProject)) },
                    { "active_project_pid", openLiveProjects.Where(p => PathsEqual(p.ProjectPath, activeProject)).Select(p => p.ProcessId).FirstOrDefault() },
                    { "available_projects", availableProjects },
                    { "running_projects_count", openLiveProjects.Count }
                };

                SendJson(response, 200, status);
                return;
            }

            if (path.Equals("/api/plcs", StringComparison.OrdinalIgnoreCase))
            {
                string activeProject = !string.IsNullOrWhiteSpace(recent.LastProjectPath) ? recent.LastProjectPath : _config.ProjectPath;
                bool force = string.Equals(GetQueryParam(request, "refresh"), "true", StringComparison.OrdinalIgnoreCase);

                if (force)
                {
                    lock (_plcLock) { _projectPlcsCache.Remove(activeProject); }
                    TriggerPlcScanAsync(_configPath, activeProject);
                }

                var plcs = GetPlcsFast(rootDir, activeProject);
                SendJson(response, 200, new Dictionary<string, object>
                {
                    { "plcs", plcs },
                    { "hmis", _cachedHmiList ?? new List<string>() }
                });
                return;
            }

            if (path.Equals("/api/plc/select", StringComparison.OrdinalIgnoreCase) && method == "POST")
            {
                string body = ReadBody(request);
                var map = _json.Deserialize<Dictionary<string, object>>(body);
                string newPlc = map != null && map.ContainsKey("plc_name") ? Convert.ToString(map["plc_name"]) : "";

                if (!string.IsNullOrWhiteSpace(newPlc))
                {
                    recent.LastPlcName = newPlc;
                    recent.Save();
                    _config.PlcName = newPlc;
                    SendJson(response, 200, new Dictionary<string, object> { { "success", true }, { "plc_name", newPlc } });
                    return;
                }
                SendJson(response, 400, new Dictionary<string, object> { { "error", "无效的 PLC 名称。" } });
                return;
            }

            if (path.Equals("/api/projects", StringComparison.OrdinalIgnoreCase))
            {
                var projects = new List<Dictionary<string, object>>();
                string activeProject = !string.IsNullOrWhiteSpace(recent.LastProjectPath) ? recent.LastProjectPath : _config.ProjectPath;

                if (!string.IsNullOrWhiteSpace(activeProject))
                {
                    projects.Add(new Dictionary<string, object>
                    {
                        { "path", activeProject },
                        { "name", Path.GetFileName(activeProject) },
                        { "is_recent", true },
                        { "is_active", true }
                    });
                }

                if (!string.IsNullOrWhiteSpace(_config.ProjectPath) && !string.Equals(_config.ProjectPath, activeProject, StringComparison.OrdinalIgnoreCase))
                {
                    projects.Add(new Dictionary<string, object>
                    {
                        { "path", _config.ProjectPath },
                        { "name", Path.GetFileName(_config.ProjectPath) },
                        { "is_recent", false },
                        { "is_active", false }
                    });
                }

                SendJson(response, 200, projects);
                return;
            }

            if (path.Equals("/api/project/refresh", StringComparison.OrdinalIgnoreCase))
            {
                lock (_portalLock) { _lastPortalScan = DateTime.MinValue; }
                lock (_plcLock) { _cachedPlcList = null; _cachedHmiList = null; _projectPlcsCache.Clear(); ProjectHmis.Clear(); }
                SendJson(response, 200, new Dictionary<string, object> { { "success", true }, { "message", "已刷新工程与博途状态缓存" } });
                return;
            }

            if ((path.Equals("/api/project/switch", StringComparison.OrdinalIgnoreCase) || path.Equals("/api/project/select", StringComparison.OrdinalIgnoreCase)) && method == "POST")
            {
                string body = ReadBody(request);
                var map = _json.Deserialize<Dictionary<string, object>>(body);
                string newPath = map != null && map.ContainsKey("project_path") ? Convert.ToString(map["project_path"]) : "";

                if (string.IsNullOrWhiteSpace(newPath) || !File.Exists(newPath))
                {
                    SendJson(response, 400, new Dictionary<string, object> { { "error", "无效的工程文件路径。" } });
                    return;
                }

                recent.LastProjectPath = Path.GetFullPath(newPath);
                _lastDetectedLiveProject = recent.LastProjectPath;
                recent.AddRecentProject(recent.LastProjectPath);
                recent.Save();
                _config.OverrideProject(newPath, true);

                lock (_plcLock) { _cachedPlcList = null; }

                SendJson(response, 200, new Dictionary<string, object>
                {
                    { "success", true },
                    { "project_path", recent.LastProjectPath },
                    { "project_name", Path.GetFileName(recent.LastProjectPath) },
                    { "source_directory", _config.SourceDirectory }
                });
                return;
            }

            if (path.Equals("/api/backups", StringComparison.OrdinalIgnoreCase))
            {
                string backupDir = Path.Combine(rootDir, "block-backups");
                var list = new List<Dictionary<string, object>>();

                if (Directory.Exists(backupDir))
                {
                    var metaFiles = Directory.GetFiles(backupDir, "*.meta.json")
                        .Select(f => new FileInfo(f))
                        .OrderByDescending(f => f.LastWriteTimeUtc)
                        .Take(100);

                    foreach (var fi in metaFiles)
                    {
                        try
                        {
                            string metaJson = File.ReadAllText(fi.FullName, Encoding.UTF8);
                            var meta = _json.Deserialize<Dictionary<string, object>>(metaJson);
                            string xmlFile = fi.FullName.Substring(0, fi.FullName.Length - ".meta.json".Length);
                            long xmlSize = File.Exists(xmlFile) ? new FileInfo(xmlFile).Length : 0;

                            list.Add(new Dictionary<string, object>
                            {
                                { "meta_file", fi.Name },
                                { "xml_file", Path.GetFileName(xmlFile) },
                                { "xml_size", xmlSize },
                                { "name", meta.ContainsKey("Name") ? meta["Name"] : "" },
                                { "kind", meta.ContainsKey("Kind") ? meta["Kind"] : "" },
                                { "group", meta.ContainsKey("GroupPath") ? meta["GroupPath"] : "" },
                                { "plc", meta.ContainsKey("Plc") ? meta["Plc"] : "" },
                                { "created_at", meta.ContainsKey("CreatedAtUtc") ? meta["CreatedAtUtc"] : fi.LastWriteTime.ToString("o") },
                                { "sha256", meta.ContainsKey("Sha256") ? meta["Sha256"] : "" }
                            });
                        }
                        catch { }
                    }
                }

                SendJson(response, 200, list);
                return;
            }

            if (path.Equals("/api/diff", StringComparison.OrdinalIgnoreCase))
            {
                // UTF-8 unescape query parameters
                string backupXml = GetQueryParam(request, "backup");
                string sourceFile = GetQueryParam(request, "file");
                string blockName = GetQueryParam(request, "name");

                string backupDir = Path.Combine(rootDir, "block-backups");
                string activeProject = !string.IsNullOrWhiteSpace(recent.LastProjectPath) ? recent.LastProjectPath : _config.ProjectPath;
                string sourceDir = _config.SourceDirectory;

                // Read exact Name from metadata file if backup is given to eliminate any URL decoding issues
                if (!string.IsNullOrWhiteSpace(backupXml))
                {
                    string metaPath = Path.Combine(backupDir, Path.GetFileName(backupXml) + ".meta.json");
                    if (File.Exists(metaPath))
                    {
                        try
                        {
                            var metaMap = _json.Deserialize<Dictionary<string, object>>(File.ReadAllText(metaPath, Encoding.UTF8));
                            if (metaMap != null && metaMap.ContainsKey("Name"))
                            {
                                string exactName = Convert.ToString(metaMap["Name"]);
                                if (!string.IsNullOrWhiteSpace(exactName))
                                {
                                    blockName = exactName;
                                }
                            }
                        }
                        catch { }
                    }
                }

                string oldPath = "";
                string newPath = "";

                if (!string.IsNullOrWhiteSpace(backupXml))
                {
                    oldPath = Path.Combine(backupDir, Path.GetFileName(backupXml));
                }

                if (!string.IsNullOrWhiteSpace(sourceFile))
                {
                    newPath = Path.Combine(sourceDir, sourceFile);
                }
                else if (!string.IsNullOrWhiteSpace(blockName) && Directory.Exists(sourceDir))
                {
                    // Find matching block in source directory
                    string[] candidates = Directory.GetFiles(sourceDir, blockName + ".*", SearchOption.AllDirectories);
                    if (candidates.Length > 0)
                    {
                        newPath = candidates[0];
                    }
                }

                string displayName = !string.IsNullOrWhiteSpace(blockName) ? blockName : Path.GetFileName(newPath ?? oldPath);
                FileDiffResult diff = DiffHelper.CompareFiles(oldPath, newPath, displayName);
                SendJson(response, 200, diff);
                return;
            }

            if (path.Equals("/api/index/summary", StringComparison.OrdinalIgnoreCase))
            {
                string activeProject = !string.IsNullOrWhiteSpace(recent.LastProjectPath) ? recent.LastProjectPath : _config.ProjectPath;
                if (string.IsNullOrWhiteSpace(activeProject))
                {
                    SendJson(response, 200, new Dictionary<string, object> { { "has_index", false } });
                    return;
                }

                string catalogPath = Path.Combine(Path.GetDirectoryName(activeProject), ".tia-source-index", Path.GetFileName(activeProject), "catalog.json");
                if (!File.Exists(catalogPath))
                {
                    SendJson(response, 200, new Dictionary<string, object>
                    {
                        { "has_index", false },
                        { "project_file", Path.GetFileName(activeProject) },
                        { "catalog_path", catalogPath }
                    });
                    return;
                }

                try
                {
                    string jsonText = File.ReadAllText(catalogPath, Encoding.UTF8);
                    IndexCatalog catalog = _json.Deserialize<IndexCatalog>(jsonText);

                    var objects = catalog.objects ?? new List<IndexEntry>();
                    var summary = new Dictionary<string, object>
                    {
                        { "has_index", true },
                        { "project_file", catalog.project_file },
                        { "portal_version", catalog.portal_version },
                        { "built_utc", catalog.built_utc },
                        { "total_objects", objects.Count },
                        { "blocks_count", objects.Count(o => o.kind == "Block" || o.kind == "OB" || o.kind == "FB" || o.kind == "FC" || o.kind == "DB") },
                        { "types_count", objects.Count(o => o.kind == "Type" || o.kind == "UDT") },
                        { "tags_count", objects.Count(o => o.kind == "TagTable") },
                        { "hmi_count", objects.Count(o => o.kind == "HMIScreen") },
                        { "warnings", catalog.warnings ?? new List<string>() }
                    };

                    SendJson(response, 200, summary);
                }
                catch (Exception ex)
                {
                    SendJson(response, 500, new Dictionary<string, object> { { "error", "读取索引失败: " + ex.Message } });
                }
                return;
            }

            if (path.Equals("/api/index/search", StringComparison.OrdinalIgnoreCase))
            {
                string query = GetQueryParam(request, "q").Trim();
                string kindFilter = GetQueryParam(request, "kind").Trim();

                string activeProject = !string.IsNullOrWhiteSpace(recent.LastProjectPath) ? recent.LastProjectPath : _config.ProjectPath;
                string catalogPath = Path.Combine(Path.GetDirectoryName(activeProject), ".tia-source-index", Path.GetFileName(activeProject), "catalog.json");

                if (!File.Exists(catalogPath))
                {
                    SendJson(response, 200, new List<object>());
                    return;
                }

                try
                {
                    string jsonText = File.ReadAllText(catalogPath, Encoding.UTF8);
                    IndexCatalog catalog = _json.Deserialize<IndexCatalog>(jsonText);
                    var list = catalog.objects ?? new List<IndexEntry>();

                    var filtered = list.Where(item =>
                    {
                        if (!string.IsNullOrEmpty(kindFilter) && !string.Equals(item.kind, kindFilter, StringComparison.OrdinalIgnoreCase))
                            return false;
                        if (string.IsNullOrEmpty(query))
                            return true;
                        return (item.name != null && item.name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                            || (item.group != null && item.group.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                            || (item.owner != null && item.owner.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
                    }).Take(150).ToList();

                    SendJson(response, 200, filtered);
                }
                catch (Exception ex)
                {
                    SendJson(response, 500, new Dictionary<string, object> { { "error", ex.Message } });
                }
                return;
            }

            if (path.Equals("/api/simulation/status", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var sim = new SimulationTools(rootDir);
                    object status = sim.Status();
                    SendJson(response, 200, status);
                }
                catch (Exception ex)
                {
                    SendJson(response, 200, new Dictionary<string, object>
                    {
                        { "available", false },
                        { "error", ex.Message }
                    });
                }
                return;
            }

            if (path.Equals("/api/logs", StringComparison.OrdinalIgnoreCase))
            {
                string mcpLogPath = Path.Combine(rootDir, "last-mcp-host.log");
                string errorLogPath = Path.Combine(rootDir, "last-error.log");
                string compileLogPath = Path.Combine(rootDir, "last-compile.log");

                var logs = new Dictionary<string, object>
                {
                    { "mcp_log", ReadTail(mcpLogPath, 100) },
                    { "error_log", ReadTail(errorLogPath, 100) },
                    { "compile_log", ReadTail(compileLogPath, 100) }
                };

                SendJson(response, 200, logs);
                return;
            }

            if (path.Equals("/api/tools", StringComparison.OrdinalIgnoreCase))
            {
                SendJson(response, 200, DashboardTools(_configPath));
                return;
            }

            if (path.Equals("/api/action/run", StringComparison.OrdinalIgnoreCase) && method == "POST")
            {
                if (shutdownRequested) { SendJson(response, 503, new { error = "看板正在退出，不能开始新任务。" }); return; }
                string body = ReadBody(request);
                var map = _json.Deserialize<Dictionary<string, object>>(body);
                string action = map != null && map.ContainsKey("action") ? Convert.ToString(map["action"]) : "";

                if (!new[] { "trust", "build-index", "export", "import", "compile" }.Contains(action))
                { SendJson(response, 400, new { error = "不支持的操作。" }); return; }
                string activeProject = Text(map, "project_path"), plcName = Text(map, "plc_name");
                if (action != "trust" && (!File.Exists(activeProject) || (action != "build-index" && string.IsNullOrWhiteSpace(plcName)) || !actionTargets.Verify(activeProject, plcName, Text(map, "target_token"))))
                {
                    SendJson(response, 409, new { error = "工程或 PLC 目标信息无效，请刷新页面并重新选择目标。" }); return;
                }
                string busy; ActionProgress startedTask;
                bool started = ActionRunner.StartAction(action, _configPath, activeProject, plcName, out busy, out startedTask);
                if (started)
                {
                    SendJson(response, 200, new Dictionary<string, object>
                    {
                        { "success", true },
                        { "action", action },
                        { "message", "任务已启动。" },
                        { "task", ActionRunner.FindTask(startedTask.task_id) }
                    });
                }
                else
                {
                    SendJson(response, 409, new Dictionary<string, object>
                    {
                        { "success", false },
                        { "error", "已有其他任务在执行中，请等待其完成。" }
                    });
                }
                return;
            }

            if (path.Equals("/api/action/status", StringComparison.OrdinalIgnoreCase))
            {
                string taskId = GetQueryParam(request, "task_id");
                var task = string.IsNullOrEmpty(taskId) ? ActionRunner.Current : ActionRunner.FindTask(taskId);
                if (task == null) SendJson(response, 404, new { error = "任务不存在或已从最近 100 项记录中移除。" });
                else SendJson(response, 200, task);
                return;
            }

            SendJson(response, 404, new Dictionary<string, object> { { "error", "API 路径未找到: " + path } });
        }

        private static string ReadBody(HttpListenerRequest request)
        {
            using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        private static string ReadTail(string filePath, int lineCount)
        {
            if (!File.Exists(filePath)) return "";
            try
            {
                string[] lines = File.ReadAllLines(filePath, Encoding.UTF8);
                if (lines.Length <= lineCount) return string.Join("\n", lines);
                return string.Join("\n", lines.Skip(lines.Length - lineCount));
            }
            catch (Exception ex)
            {
                return "[读取失败: " + ex.Message + "]";
            }
        }

        private void SendJson(HttpListenerResponse response, int statusCode, object data)
        {
            response.StatusCode = statusCode;
            response.ContentType = "application/json; charset=utf-8";
            string text = _json.Serialize(data);
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            response.ContentLength64 = bytes.Length;
            response.OutputStream.Write(bytes, 0, bytes.Length);
            response.Close();
        }

        private static List<Dictionary<string, object>> GetToolCatalog()
        {
            return new List<Dictionary<string, object>>
            {
                new Dictionary<string, object> { { "name", "tia_build_project_index" }, { "category", "两阶段工程索引" }, { "safe", true }, { "description", "建立可随工程迁移的轻量离线索引与符号引用树" } },
                new Dictionary<string, object> { { "name", "tia_get_project_tree" }, { "category", "两阶段工程索引" }, { "safe", true }, { "description", "按组或类型查询工程目录树（秒级响应）" } },
                new Dictionary<string, object> { { "name", "tia_find_references" }, { "category", "两阶段工程索引" }, { "safe", true }, { "description", "查找符号被哪些程序块或变量显式引用" } },
                new Dictionary<string, object> { { "name", "tia_get_task_context" }, { "category", "两阶段工程索引" }, { "safe", true }, { "description", "按任务关键词一键提取相关程序块与变量上下文" } },

                new Dictionary<string, object> { { "name", "tia_read_source" }, { "category", "PLC 源码与块操作" }, { "safe", true }, { "description", "读取单个 SCL/STL/DB/UDT 纯文本源码" } },
                new Dictionary<string, object> { { "name", "tia_read_sources_batch" }, { "category", "PLC 源码与块操作" }, { "safe", true }, { "description", "批量读取多个程序块源码，大幅减少 AI 交互往返" } },
                new Dictionary<string, object> { { "name", "tia_import_source" }, { "category", "PLC 源码与块操作" }, { "safe", false }, { "description", "回导单个纯文本源码，自动备份原块并编译" } },
                new Dictionary<string, object> { { "name", "tia_import_sources_batch" }, { "category", "PLC 源码与块操作" }, { "safe", false }, { "description", "批量回导源码（UDT -> FC/FB -> DB 顺序）并统编译" } },
                new Dictionary<string, object> { { "name", "tia_read_block" }, { "category", "PLC 源码与块操作" }, { "safe", true }, { "description", "读取单个程序块完整 SimaticML XML（含 LAD/FBD 网络）" } },
                new Dictionary<string, object> { { "name", "tia_import_block" }, { "category", "PLC 源码与块操作" }, { "safe", false }, { "description", "导入 SimaticML XML 块，支持 compile_mode 控制" } },
                new Dictionary<string, object> { { "name", "tia_restore_backup" }, { "category", "PLC 源码与块操作" }, { "safe", false }, { "description", "一键回滚历史 XML 备份，校验指纹与原组" } },
                new Dictionary<string, object> { { "name", "tia_delete_block" }, { "category", "PLC 源码与块操作" }, { "safe", false }, { "description", "安全删除块或 UDT（强制先行导出备份）" } },
                new Dictionary<string, object> { { "name", "tia_generate_lad" }, { "category", "PLC 源码与块操作" }, { "safe", true }, { "description", "从零生成梯形图 LAD XML（带局部变量/触点/线圈/运算）" } },
                new Dictionary<string, object> { { "name", "tia_list_blocks" }, { "category", "PLC 源码与块操作" }, { "safe", true }, { "description", "列出 PLC 所有程序块，支持按组、语言、名称过滤" } },
                new Dictionary<string, object> { { "name", "tia_list_types" }, { "category", "PLC 源码与块操作" }, { "safe", true }, { "description", "列出 PLC 所有用户数据类型 (UDT)" } },
                new Dictionary<string, object> { { "name", "tia_list_tag_tables" }, { "category", "PLC 源码与块操作" }, { "safe", true }, { "description", "列出所有变量表及表内变量/常量统计" } },
                new Dictionary<string, object> { { "name", "tia_read_tag_table" }, { "category", "PLC 源码与块操作" }, { "safe", true }, { "description", "读取变量表 SimaticML XML" } },
                new Dictionary<string, object> { { "name", "tia_import_tag_table" }, { "category", "PLC 源码与块操作" }, { "safe", false }, { "description", "导入变量表 XML 并备份原表" } },

                new Dictionary<string, object> { { "name", "tia_read_hmi_alarms" }, { "category", "经典 HMI 与报警" }, { "safe", true }, { "description", "批量读取经典 WinCC 离散量报警" } },
                new Dictionary<string, object> { { "name", "tia_write_hmi_alarms" }, { "category", "经典 HMI 与报警" }, { "safe", false }, { "description", "批量新增/更新经典 WinCC 离散量报警（UIA/Excel 补丁）" } },
                new Dictionary<string, object> { { "name", "tia_generate_hmi_configuration" }, { "category", "经典 HMI 与报警" }, { "safe", true }, { "description", "批量生成经典 HMI 画面与控件结构" } },
                new Dictionary<string, object> { { "name", "tia_compile_hmi" }, { "category", "经典 HMI 与报警" }, { "safe", true }, { "description", "编译经典 WinCC HMI 并返回诊断信息" } },

                new Dictionary<string, object> { { "name", "tia_update_graph" }, { "category", "GRAPH 顺控" }, { "safe", false }, { "description", "通用批量修改 GRAPH 顺序：步、转换、分支、连线" } },
                new Dictionary<string, object> { { "name", "tia_generate_graph" }, { "category", "GRAPH 顺控" }, { "safe", false }, { "description", "从零构建完整 GRAPH 顺控 FB" } },
                new Dictionary<string, object> { { "name", "tia_read_graph_model" }, { "category", "GRAPH 顺控" }, { "safe", true }, { "description", "读取现有 GRAPH 的结构化动作与转换条件模型" } },

                new Dictionary<string, object> { { "name", "tia_simulation_status" }, { "category", "PLCSIM Advanced 仿真" }, { "safe", true }, { "description", "获取本机 PLCSIM Advanced 实例与 Softbus 状态" } },
                new Dictionary<string, object> { { "name", "tia_simulation_create" }, { "category", "PLCSIM Advanced 仿真" }, { "safe", false }, { "description", "创建受管本机仿真实例" } },
                new Dictionary<string, object> { { "name", "tia_simulation_start" }, { "category", "PLCSIM Advanced 仿真" }, { "safe", false }, { "description", "启动仿真实例并切换到 RUN" } },
                new Dictionary<string, object> { { "name", "tia_simulation_stop" }, { "category", "PLCSIM Advanced 仿真" }, { "safe", false }, { "description", "停止仿真实例" } },
                new Dictionary<string, object> { { "name", "tia_simulation_read_symbol" }, { "category", "PLCSIM Advanced 仿真" }, { "safe", true }, { "description", "按符号名读取优化 DB 变量值" } },
                new Dictionary<string, object> { { "name", "tia_simulation_write_symbol" }, { "category", "PLCSIM Advanced 仿真" }, { "safe", false }, { "description", "按符号名写入仿真变量" } },
                new Dictionary<string, object> { { "name", "tia_simulation_auto_reset" }, { "category", "PLCSIM Advanced 仿真" }, { "safe", false }, { "description", "执行自动复位回归测试" } },

                new Dictionary<string, object> { { "name", "tia_read_hardware_configuration" }, { "category", "硬件与网络" }, { "safe", true }, { "description", "读取硬件机架、模块、子网及 IO 系统组态" } },
                new Dictionary<string, object> { { "name", "tia_apply_hardware_configuration" }, { "category", "硬件与网络" }, { "safe", false }, { "description", "执行硬件配置计划（插入模块/连线/地址设置）" } },
                new Dictionary<string, object> { { "name", "tia_compile_hardware_configuration" }, { "category", "硬件与网络" }, { "safe", true }, { "description", "编译硬件与网络组态" } },

                new Dictionary<string, object> { { "name", "tia_online_connect" }, { "category", "在线通道与监视" }, { "safe", true }, { "description", "建立只读 S7 TCP 在线连接" } },
                new Dictionary<string, object> { { "name", "tia_online_read_db" }, { "category", "在线通道与监视" }, { "safe", true }, { "description", "读取标准 DB 原始字节" } },
                new Dictionary<string, object> { { "name", "tia_online_diagnose_graph" }, { "category", "在线通道与监视" }, { "safe", true }, { "description", "在线诊断 GRAPH 活动工步与阻断转换条件" } },

                new Dictionary<string, object> { { "name", "tia_list_process_library" }, { "category", "工艺资料库" }, { "safe", true }, { "description", "检索本地标准工艺模板与参数化知识" } },
                new Dictionary<string, object> { { "name", "tia_render_process_template" }, { "category", "工艺资料库" }, { "safe", true }, { "description", "根据工艺参数渲染标准化程序源码" } }
            };
        }
    }
}
