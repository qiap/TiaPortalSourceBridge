using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TiaPortalSourceBridge.Configuration;

namespace TiaPortalSourceBridge.Openness
{
    internal sealed class OpennessSession : IDisposable
    {
        private readonly AppConfig _config;
        private readonly object _portal;
        private readonly object _project;
        private readonly List<PlcContext> _plcs;
        private readonly List<HmiContext> _hmis;

        public ReflectionApi Api { get; private set; }
        public object Project { get { return _project; } }

        private OpennessSession(AppConfig config, ReflectionApi api, object portal, object project)
        {
            _config = config;
            Api = api;
            _portal = portal;
            _project = project;
            _plcs = new List<PlcContext>();
            _hmis = new List<HmiContext>();
            DiscoverSoftware(project, _plcs, _hmis);
        }

        public static OpennessSession Open(AppConfig config)
        {
            if (!File.Exists(config.ProjectPath))
            {
                throw new FileNotFoundException("找不到 TIA Portal 项目文件。", config.ProjectPath);
            }

            ValidateProjectFiles(config.ProjectPath);

            ReflectionApi api = new ReflectionApi(config);
            Type portalType = api.RequireType("Siemens.Engineering.TiaPortal");
            object portal = null;
            string mode = (config.ConnectionMode ?? "AttachOrStart").Trim();
            bool needsUpgrade = !string.Equals(config.ProjectFileVersion, api.LoadedPortalVersion,
                StringComparison.OrdinalIgnoreCase);

            if (needsUpgrade)
            {
                if (!config.AllowProjectUpgrade)
                    throw new InvalidOperationException("当前模式禁止自动升级工程（项目版本 " + config.ProjectFileVersion
                        + "，已加载 API 版本 " + api.LoadedPortalVersion + "）；请使用与项目一致的 TIA Portal 版本配置。");
                Console.WriteLine();
                Console.WriteLine("检测到旧版项目 " + config.ProjectFileVersion + "，本机将使用 TIA Portal "
                    + api.LoadedPortalVersion + " 升级打开。");
                Console.WriteLine("原项目不会被覆盖；TIA Portal 会另存为新版项目文件。");
                Console.Write("是否继续？输入 Y 继续，其他键取消: ");
                if (Console.IsInputRedirected) throw new OperationCanceledException("非交互任务不能确认工程升级，请使用匹配版本的引擎。");
                string answer = Console.ReadLine();
                if (!string.Equals(answer, "Y", StringComparison.OrdinalIgnoreCase))
                {
                    throw new OperationCanceledException("已取消项目升级。原项目未更改。");
                }
            }

            if (!string.Equals(mode, "StartNew", StringComparison.OrdinalIgnoreCase))
            {
                List<object> processList = api.Items(api.InvokeStatic(portalType, "GetProcesses")).ToList();
                object matching = processList.FirstOrDefault(p => ProcessMatches(api, p, config.ProjectPath));
                if (matching == null && !string.Equals(mode, "AttachOnly", StringComparison.OrdinalIgnoreCase))
                {
                    matching = processList.FirstOrDefault(p => api.Get(p, "ProjectPath", false) == null);
                }
                if (matching != null)
                {
                    Console.WriteLine("连接到已运行的 TIA Portal。");
                    Console.WriteLine("如果 TIA Portal 弹出 Openness 连接确认，请在 TIA 窗口中选择“始终允许”或运行 TrustThisTool.bat。");
                    portal = api.Invoke(matching, "Attach");
                }
                else if (string.Equals(mode, "AttachOnly", StringComparison.OrdinalIgnoreCase))
                {
                    string runningInfo = processList.Count == 0
                        ? "当前未检测到任何正在运行的 TIA Portal " + api.LoadedPortalVersion + " 进程。"
                        : "当前运行的 TIA 进程已打开项目: " + string.Join("; ", processList.Select(p =>
                        {
                            FileInfo fp = api.Get(p, "ProjectPath", false) as FileInfo;
                            return fp == null ? "<未打开项目>" : fp.FullName;
                        }));
                    throw new InvalidOperationException("connectionMode=AttachOnly，但没有找到已打开目标工程的 TIA Portal 进程。目标工程: "
                        + config.ProjectPath + "。" + runningInfo);
                }
            }

            if (portal == null)
            {
                object portalMode = api.EnumValue("Siemens.Engineering.TiaPortalMode",
                    config.WithUserInterface ? "WithUserInterface" : "WithoutUserInterface");
                Console.WriteLine("启动新的 TIA Portal 实例。");
                portal = Activator.CreateInstance(portalType, new object[] { portalMode });
            }

            object projects = api.Get(portal, "Projects", true);
            object project = api.Items(projects).FirstOrDefault(p => ProjectMatches(api, p, config.ProjectPath));
            if (project == null)
            {
                Console.WriteLine("打开项目: " + config.ProjectPath);
                project = needsUpgrade
                    ? api.Invoke(projects, "OpenWithUpgrade", new FileInfo(config.ProjectPath))
                    : api.Invoke(projects, "Open", new FileInfo(config.ProjectPath));

                FileInfo openedPath = api.Get(project, "Path", false) as FileInfo;
                if (openedPath != null && !PathsEqual(openedPath.FullName, config.ProjectPath))
                {
                    config.ProjectPath = openedPath.FullName;
                    Console.WriteLine("升级后的项目: " + config.ProjectPath);
                }
            }
            else
            {
                Console.WriteLine("使用已打开的项目: " + config.ProjectPath);
            }

            return new OpennessSession(config, api, portal, project);
        }

        public PlcContext SelectPlc(string name)
        {
            if (_plcs.Count == 0)
            {
                throw new InvalidOperationException("项目中没有找到 PlcSoftware。");
            }
            if (_plcs.Count == 1 && string.IsNullOrWhiteSpace(name)) return _plcs[0];
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException("项目中有多个 PLC，必须指定 plc_name。可用 PLC: "
                    + string.Join(", ", _plcs.Select(p => p.DisplayName)));
            }

            PlcContext matched = FindPlcByName(name);
            if (matched != null) return matched;

            throw new InvalidOperationException("找不到 PLC '" + name + "'。可运行 list-plcs 查看可用名称。找到的 PLC: "
                + string.Join(", ", _plcs.Select(p => p.DisplayName)));
        }

        private PlcContext FindPlcByName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            string requested = name.Trim();
            PlcContext[] matches = _plcs.Where(p =>
                string.Equals(p.DisplayName, requested, StringComparison.OrdinalIgnoreCase)
                || p.MatchNames.Any(n => string.Equals(n, requested, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (matches.Length == 0 && requested.Contains("(") && requested.EndsWith(")"))
            {
                int paren = requested.IndexOf('(');
                string shortCpu = requested.Substring(0, paren).Trim();
                matches = _plcs.Where(p =>
                    string.Equals(p.DisplayName, shortCpu, StringComparison.OrdinalIgnoreCase)
                    || p.MatchNames.Any(n => string.Equals(n, shortCpu, StringComparison.OrdinalIgnoreCase))).ToArray();
            }
            if (matches.Length > 1)
                throw new InvalidOperationException("PLC 名称匹配多个目标，请使用完整显示名称：" + name);
            return matches.SingleOrDefault();
        }

        public HmiContext SelectHmi(string name)
        {
            if (_hmis.Count == 0)
            {
                throw new InvalidOperationException("项目中没有找到经典 WinCC HMI。");
            }
            if (_hmis.Count == 1 && string.IsNullOrWhiteSpace(name)) return _hmis[0];

            if (!string.IsNullOrWhiteSpace(name))
            {
                string requested = name.Trim();
                HmiContext[] matches = _hmis.Where(h =>
                    string.Equals(h.DisplayName, requested, StringComparison.OrdinalIgnoreCase)
                    || h.MatchNames.Any(n => string.Equals(n, requested, StringComparison.OrdinalIgnoreCase))).ToArray();
                if (matches.Length > 1)
                    throw new InvalidOperationException("HMI 名称匹配多个目标，请使用完整显示名称：" + name);
                if (matches.Length == 1) return matches[0];

                throw new InvalidOperationException("找不到 HMI '" + name + "'。找到的 HMI: "
                    + string.Join(", ", _hmis.Select(h => h.DisplayName)));
            }

            throw new InvalidOperationException("项目中有多个 HMI，请通过 --hmi 指定名称。可用 HMI: "
                + string.Join(", ", _hmis.Select(h => h.DisplayName)));
        }

        public PlcContext SelectPlcInteractive(string preferredName)
        {
            if (_plcs.Count == 0)
            {
                throw new InvalidOperationException("项目中没有找到 PlcSoftware。");
            }
            if (_plcs.Count == 1)
            {
                Console.WriteLine("项目中只有一个 PLC，已自动选择。");
                return _plcs[0];
            }

            int defaultIndex = 0;
            for (int i = 0; i < _plcs.Count; i++)
            {
                if (_plcs[i].MatchNames.Any(n => string.Equals(n, preferredName, StringComparison.OrdinalIgnoreCase)))
                {
                    defaultIndex = i;
                    break;
                }
            }

            Console.WriteLine();
            Console.WriteLine("项目中有多个 PLC，请选择:");
            for (int i = 0; i < _plcs.Count; i++)
            {
                Console.WriteLine("  " + (i + 1) + ". " + _plcs[i].DisplayName + (i == defaultIndex ? "  [默认]" : ""));
            }

            while (true)
            {
                Console.Write("输入编号，直接回车选择默认项: ");
                string input = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(input)) return _plcs[defaultIndex];
                int selected;
                if (int.TryParse(input, out selected) && selected >= 1 && selected <= _plcs.Count)
                {
                    return _plcs[selected - 1];
                }
                Console.WriteLine("请输入 1 到 " + _plcs.Count + " 之间的编号。");
            }
        }

        public void PrintPlcs()
        {
            if (_plcs.Count == 0)
            {
                Console.WriteLine("项目中没有找到 PlcSoftware。");
                return;
            }
            Console.WriteLine("可用 PLC:");
            foreach (PlcContext plc in _plcs)
            {
                Console.WriteLine("  - " + plc.DisplayName);
            }
        }

        public IList<string> GetPlcDisplayNames()
        {
            return _plcs.Select(p => p.DisplayName).ToList();
        }

        public IList<HmiContext> GetHmis()
        {
            return _hmis;
        }

        public void PrintHmis()
        {
            if (_hmis.Count == 0)
            {
                Console.WriteLine("项目中没有找到经典 WinCC HMI。");
                return;
            }
            Console.WriteLine("可用 HMI:");
            foreach (HmiContext hmi in _hmis) Console.WriteLine("  - " + hmi.DisplayName);
        }

        public string GetProjectPath()
        {
            FileInfo path = Api.Get(_project, "Path", false) as FileInfo;
            return path == null ? _config.ProjectPath : path.FullName;
        }

        public void RefreshSoftware()
        {
            _plcs.Clear(); _hmis.Clear();
            DiscoverSoftware(_project, _plcs, _hmis);
        }

        public bool IsAlive()
        {
            if (_portal == null || _project == null) return false;
            try
            {
                FileInfo path = Api.Get(_project, "Path", false) as FileInfo;
                return path != null && !string.IsNullOrWhiteSpace(path.FullName);
            }
            catch
            {
                return false;
            }
        }

        public CompileSummary CompilePlc(PlcContext plc)
        {
            return CompileSoftware(plc.Software);
        }

        public CompileSummary CompileEngineeringObject(object target) { return CompileSoftware(target); }

        public CompileSummary CompileTarget(object target, PlcContext fallbackPlc)
        {
            if (target != null)
            {
                try
                {
                    return CompileSoftware(target);
                }
                catch
                {
                    // Fallback to full PLC compile if single-object compile service is unavailable
                }
            }
            return CompilePlc(fallbackPlc);
        }

        /// <summary>
        /// 编译单个对象，不回退到整 PLC 编译。供"导出前补齐未编译状态"这类内部重试使用：
        /// 调用方需要的是这个对象本身编译成功，而不是把整个 PLC 编译一遍。
        /// </summary>
        public CompileSummary CompileObject(object target)
        {
            if (target == null) throw new ArgumentNullException("target");
            return CompileSoftware(target);
        }

        public CompileSummary CompileHmi(HmiContext hmi)
        {
            return CompileSoftware(hmi.Target);
        }

        private CompileSummary CompileSoftware(object software)
        {
            using (OperationMetrics.Measure("compile")) return CompileSoftwareCore(software);
        }

        private CompileSummary CompileSoftwareCore(object software)
        {
            Type compilableType = Api.RequireType("Siemens.Engineering.Compiler.ICompilable");
            object compileService = Api.InvokeGenericNoArguments(software, "GetService", compilableType);
            if (compileService == null)
            {
                throw new InvalidOperationException("当前软件不提供 ICompilable 服务。");
            }

            object result = Api.Invoke(compileService, "Compile");
            CompileSummary summary = new CompileSummary
            {
                State = Api.GetString(result, "State"),
                ErrorCount = Convert.ToInt32(Api.Get(result, "ErrorCount", false) ?? 0),
                WarningCount = Convert.ToInt32(Api.Get(result, "WarningCount", false) ?? 0)
            };
            CollectCompileMessages(Api.Get(result, "Messages", false), summary.Messages, 0);
            return summary;
        }

        public void SaveProject()
        {
            Api.Invoke(_project, "Save");
        }

        public void ArchiveProject(string targetDirectory, string targetName)
        {
            if (Directory.Exists(Path.Combine(targetDirectory, targetName)))
                throw new InvalidOperationException("目标工程目录已存在: " + Path.Combine(targetDirectory, targetName));
            Directory.CreateDirectory(targetDirectory);
            object mode = Api.EnumValueBySimpleName("None", "ProjectArchivationMode");
            Api.Invoke(_project, "Archive", new DirectoryInfo(targetDirectory), targetName, mode);
        }

        public void KeepOnlyDevices(string plcDeviceName, string hmiDeviceName)
        {
            object devices = Api.Get(_project, "Devices", true);
            List<object> all = Api.Items(devices).ToList();
            string[] expected = { plcDeviceName, hmiDeviceName };
            foreach (string name in expected)
                if (all.Count(d => string.Equals(Api.GetString(d, "Name"), name, StringComparison.OrdinalIgnoreCase)) != 1)
                    throw new InvalidOperationException("无法唯一定位要保留的设备: " + name);
            foreach (object device in all)
            {
                string name = Api.GetString(device, "Name");
                if (!expected.Any(e => string.Equals(e, name, StringComparison.OrdinalIgnoreCase)))
                {
                    Console.WriteLine("移除副本设备: " + name);
                    Api.Invoke(device, "Delete");
                }
            }
            SaveProject();
        }

        public void PrintHmiScreens()
        {
            foreach (HmiContext hmi in _hmis)
            {
                Console.WriteLine("HMI 画面: " + hmi.DisplayName);
                object root = Api.Get(hmi.Target, "ScreenFolder", true);
                PrintScreensInFolder(root, "");
            }
        }

        private void PrintScreensInFolder(object folder, string path)
        {
            foreach (object screen in Api.Items(Api.Get(folder, "Screens", false)))
                Console.WriteLine("  " + path + Api.GetString(screen, "Name"));
            foreach (object child in Api.Items(Api.Get(folder, "Folders", false)))
                PrintScreensInFolder(child, path + Api.GetString(child, "Name") + "/");
        }

        public void InspectHmiTags(string hmiName, string outputDirectory, params string[] names)
        {
            HmiContext hmi = SelectHmi(hmiName);
            Directory.CreateDirectory(outputDirectory);
            object root = Api.Get(hmi.Target, "TagFolder", true);
            InspectTagFolder(root, "", new HashSet<string>(names, StringComparer.OrdinalIgnoreCase), outputDirectory);
        }

        public void PrintHmiTagTables(string hmiName)
        {
            if (string.IsNullOrWhiteSpace(hmiName))
            {
                foreach (HmiContext hmi in _hmis)
                {
                    Console.WriteLine("HMI 变量表: " + hmi.DisplayName);
                    PrintTagTables(Api.Get(hmi.Target, "TagFolder", true), "");
                }
                return;
            }
            HmiContext selected = SelectHmi(hmiName);
            PrintTagTables(Api.Get(selected.Target, "TagFolder", true), "");
        }

        private void PrintTagTables(object folder, string path)
        {
            foreach (object table in Api.Items(Api.Get(folder, "TagTables", false)))
            {
                int count = Api.Items(Api.Get(table, "Tags", false)).Count();
                Console.WriteLine(path + Api.GetString(table, "Name") + " (" + count + " 变量)");
            }
            foreach (object child in Api.Items(Api.Get(folder, "Folders", false)))
                PrintTagTables(child, path + Api.GetString(child, "Name") + "/");
        }

        private void InspectTagFolder(object folder, string path, HashSet<string> names, string outputDirectory)
        {
            foreach (object table in Api.Items(Api.Get(folder, "TagTables", false)))
            {
                string tableName = Api.GetString(table, "Name");
                foreach (object tag in Api.Items(Api.Get(table, "Tags", false)))
                {
                    string name = Api.GetString(tag, "Name");
                    if (!names.Contains(name)) continue;
                    Console.WriteLine("HMI 变量: " + path + tableName + "/" + name);
                    foreach (string property in new[] { "AddressAccessMode", "LogicalAddress", "Connection", "PlcTag", "DataType", "HmiDataType", "SymbolicAddress", "Address" })
                    {
                        try
                        {
                            object value = Api.Invoke(tag, "GetAttribute", property);
                            Console.WriteLine("  " + property + " = " + (value == null ? "<null>" : value.ToString()));
                        }
                        catch (Exception ex) { Console.WriteLine("  " + property + " = <" + ex.Message + ">"); }
                    }
                    Console.WriteLine("  可用属性: " + string.Join(", ", tag.GetType().GetProperties().Select(p => p.Name)));
                    foreach (string optionName in new[] { "WithDefaults", "None" })
                    {
                        try
                        {
                            string file = Path.Combine(outputDirectory, PathTools.SafeName(name) + "_" + optionName + ".xml");
                            if (File.Exists(file)) File.Delete(file);
                            Api.Invoke(tag, "Export", new FileInfo(file),
                                Api.EnumValue("Siemens.Engineering.ExportOptions", optionName));
                            Console.WriteLine("  导出成功: " + file);
                        }
                        catch (Exception ex) { Console.WriteLine("  导出失败(" + optionName + "): " + ex.Message); }
                    }
                }
            }
            foreach (object child in Api.Items(Api.Get(folder, "Folders", false)))
                InspectTagFolder(child, path + Api.GetString(child, "Name") + "/", names, outputDirectory);
        }

        private void CollectCompileMessages(object composition, IList<CompileMessage> target, int depth)
        {
            foreach (object message in Api.Items(composition))
            {
                CompileMessage item = new CompileMessage
                {
                    Depth = depth,
                    Path = Api.GetString(message, "Path"),
                    State = Api.GetString(message, "State"),
                    Description = Api.GetString(message, "Description"),
                    ErrorCount = Convert.ToInt32(Api.Get(message, "ErrorCount", false) ?? 0),
                    WarningCount = Convert.ToInt32(Api.Get(message, "WarningCount", false) ?? 0)
                };
                target.Add(item);
                CollectCompileMessages(Api.Get(message, "Messages", false), target, depth + 1);
            }
        }

        public void Dispose()
        {
            IDisposable disposable = _portal as IDisposable;
            if (disposable != null)
            {
                disposable.Dispose();
            }
            else if (_portal != null)
            {
                try { Api.Invoke(_portal, "Dispose"); }
                catch { }
            }
        }

        private void DiscoverSoftware(object project, List<PlcContext> plcs, List<HmiContext> hmis)
        {
            HashSet<object> seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            DiscoverInComposition(Api.Get(project, "Devices", false), plcs, hmis, seen);
            DiscoverInComposition(Api.Get(project, "UngroupedDevices", false), plcs, hmis, seen);
            DiscoverInGroups(Api.Get(project, "DeviceGroups", false), plcs, hmis, seen);
        }

        private void DiscoverInGroups(object groups, List<PlcContext> plcs, List<HmiContext> hmis, HashSet<object> seen)
        {
            foreach (object group in Api.Items(groups))
            {
                DiscoverInComposition(Api.Get(group, "Devices", false), plcs, hmis, seen);
                DiscoverInGroups(Api.Get(group, "Groups", false), plcs, hmis, seen);
            }
        }

        private void DiscoverInComposition(object composition, List<PlcContext> plcs, List<HmiContext> hmis, HashSet<object> seen)
        {
            foreach (object device in Api.Items(composition))
            {
                string deviceName = Api.GetString(device, "Name");
                DiscoverInItem(device, new List<string> { deviceName }, plcs, hmis, seen);
            }
        }

        private void DiscoverInItem(object item, List<string> path, List<PlcContext> plcs, List<HmiContext> hmis, HashSet<object> seen)
        {
            if (TryAddSoftware(item, path, plcs, hmis, seen)) return;

            object children = Api.Get(item, "DeviceItems", false);
            foreach (object child in Api.Items(children))
            {
                List<string> childPath = new List<string>(path);
                childPath.Add(Api.GetString(child, "Name"));
                DiscoverInItem(child, childPath, plcs, hmis, seen);
            }
        }

        private bool TryAddSoftware(object item, List<string> path, List<PlcContext> plcs, List<HmiContext> hmis, HashSet<object> seen)
        {
            try
            {
                Type containerType = Api.RequireType("Siemens.Engineering.HW.Features.SoftwareContainer");
                object container = Api.InvokeGenericNoArguments(item, "GetService", containerType);
                object software = Api.Get(container, "Software", false);
                if (software == null) return false;
                if (!seen.Add(software)) return true;

                List<string> names = path.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
                string softwareName = Api.GetString(software, "Name");
                if (!string.IsNullOrWhiteSpace(softwareName)) names.Add(softwareName);
                string typeName = software.GetType().FullName;
                string displayName = string.Join(" / ", names);
                if (typeName == "Siemens.Engineering.SW.PlcSoftware")
                {
                    plcs.Add(new PlcContext(software, displayName, names, item)); return true;
                }
                else if (typeName == "Siemens.Engineering.Hmi.HmiTarget")
                {
                    hmis.Add(new HmiContext(software, displayName, names)); return true;
                }
                return false;
            }
            catch (MissingMethodException)
            {
                // Most DeviceItems are not software containers.
                return false;
            }
            catch (InvalidOperationException ex)
            {
                if (ex.InnerException == null) throw;
                return false;
            }
        }

        private static bool ProcessMatches(ReflectionApi api, object process, string expectedPath)
        {
            FileInfo path = api.Get(process, "ProjectPath", false) as FileInfo;
            return path != null && PathsEqual(path.FullName, expectedPath);
        }

        private static bool ProjectMatches(ReflectionApi api, object project, string expectedPath)
        {
            FileInfo path = api.Get(project, "Path", false) as FileInfo;
            return path != null && PathsEqual(path.FullName, expectedPath);
        }

        private static bool PathsEqual(string a, string b)
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        private static void ValidateProjectFiles(string projectPath)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(projectPath));
            bool hasSystem = Directory.Exists(Path.Combine(directory, "System"));
            bool hasXRef = Directory.Exists(Path.Combine(directory, "XRef"));

            if (!hasSystem || !hasXRef)
            {
                throw new InvalidOperationException(
                    "TIA Portal 工程不完整。当前只有项目入口文件，旁边缺少 System、XRef 等工程数据目录。"
                    + Environment.NewLine
                    + "请复制包含该 .ap 文件及 System、XRef、IM、UserFiles 等目录的整个工程文件夹；"
                    + "如果拿到的是 .zap 归档，请先在 TIA Portal 中恢复/解档。"
                    + Environment.NewLine
                    + "当前目录: " + directory);
            }
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
            public new bool Equals(object x, object y) { return object.ReferenceEquals(x, y); }
            public int GetHashCode(object obj) { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj); }
        }
    }
}
