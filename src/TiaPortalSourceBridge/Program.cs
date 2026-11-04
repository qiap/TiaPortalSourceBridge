using System;
using System.IO;
using System.Linq;
using TiaPortalSourceBridge.Configuration;
using TiaPortalSourceBridge.Interactive;
using TiaPortalSourceBridge.Mcp;
using TiaPortalSourceBridge.Openness;

using TiaPortalSourceBridge.Web;

namespace TiaPortalSourceBridge
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            bool launchedByDoubleClick = args.Length == 0;
            if (launchedByDoubleClick)
            {
                args = new[] { "web" };
            }

            int result = Run(args, launchedByDoubleClick);
            if (launchedByDoubleClick && result != 0)
            {
                Console.WriteLine();
                Console.WriteLine("操作未成功，请查看上面的错误信息。");
                Console.WriteLine("按任意键关闭窗口……");
                Console.ReadKey(true);
            }
            return result;
        }

        private static int Run(string[] args, bool launchedByDoubleClick = false)
        {
            if (IsHelp(args[0]))
            {
                PrintUsage();
                return 0;
            }

            string command = args[0].Trim().ToLowerInvariant();
            string configPath = GetConfigPath(args);
            if (command == "create-shortcuts")
                return DashboardShortcut.Create(configPath, args.Contains("--folder-only"), args.Contains("--notify"));

            if (command == "mcp-project-worker")
                return new McpServer(configPath, true).Run();

            if (command == "list-portals" || command == "discover-projects")
            {
                var cfg = ConfigLoader.Load(configPath);
                var list = PortalDiscovery.List(cfg);
                Console.WriteLine("FOUND " + list.Count + " PORTAL PROCESSES:");
                foreach (var p in list)
                {
                    Console.WriteLine("PID=" + p.ProcessId + " | Proj=" + p.ProjectPath + " | Ver=" + p.PortalVersion);
                }
                return 0;
            }

            if (command == "online-host") return PipeTransport.Host(configPath, "", true);
            if (command == "online-client") return PipeTransport.Client(true);

            if (command == "mcp-host")
                return PipeTransport.Host(configPath, GetOption(args, "--project"));
            if (command == "mcp-client")
                return PipeTransport.Client();

            if (command == "stop-mcp")
                return McpHostProcess.Stop(configPath);

            if (command == "mcp")
            {
                return new McpServer(configPath).Run();
            }

            if (command == "web" || command == "dashboard" || command == "gui")
            {
                int port = 18980;
                string portArg = GetOption(args, "--port");
                if (!string.IsNullOrWhiteSpace(portArg)) int.TryParse(portArg, out port);
                bool openBrowser = !args.Contains("--no-browser");
                string projectOverride = GetOption(args, "--project");
                if (!string.IsNullOrWhiteSpace(projectOverride))
                {
                    AppConfig cfg = ConfigLoader.Load(configPath);
                    cfg.OverrideProject(projectOverride, true);
                    RecentSettings rec = RecentSettings.Load(Path.GetDirectoryName(cfg.ConfigFilePath));
                    rec.LastProjectPath = cfg.ProjectPath;
                    rec.Save();
                }
                bool hideConsole = args.Contains("--hide-console") || launchedByDoubleClick;
                return WebServer.Start(configPath, port, openBrowser, hideConsole);
            }

            try
            {
                AppConfig config = ConfigLoader.Load(configPath);
                bool interactive = command == "interactive";
                string projectOverride = GetOption(args, "--project");
                RecentSettings recent = RecentSettings.Load(Path.GetDirectoryName(config.ConfigFilePath));

                if (interactive && string.IsNullOrWhiteSpace(projectOverride))
                {
                    projectOverride = InteractiveLauncher.ChooseProject(recent.LastProjectPath);
                    if (string.IsNullOrWhiteSpace(projectOverride))
                    {
                        Console.WriteLine("已取消。");
                        return 0;
                    }
                }

                if (!string.IsNullOrWhiteSpace(projectOverride))
                {
                    config.OverrideProject(projectOverride, true);
                }

                if (interactive)
                {
                    command = InteractiveLauncher.ChooseAction(recent.LastAction);
                    if (string.IsNullOrWhiteSpace(command))
                    {
                        Console.WriteLine("已取消。");
                        return 0;
                    }
                    recent.LastProjectPath = config.ProjectPath;
                    recent.LastAction = command;
                    recent.Save();
                }

                if (command == "web" || command == "dashboard" || command == "gui")
                {
                    return WebServer.Start(configPath, 18980, true);
                }

                if (string.Equals(config.TiaPortalVersion, "V21", StringComparison.OrdinalIgnoreCase))
                    return VersionedCli.Run(command, args, configPath, config.ProjectPath);
                Console.WriteLine("配置: " + config.ConfigFilePath);
                Console.WriteLine("项目文件版本: " + config.ProjectFileVersion);
                Console.WriteLine("项目: " + config.ProjectPath);
                Console.WriteLine("源码目录: " + config.SourceDirectory);

                using (OpennessSession session = OpennessSession.Open(config))
                {
                    int legacyExitCode;
                    if (LegacyOp270Tools.TryExecute(command, delegate(string opt) { return GetOption(args, opt); }, session, out legacyExitCode))
                    {
                        return legacyExitCode;
                    }

                    if (command == "save-project")
                    {
                        session.SaveProject();
                        Console.WriteLine("当前工程已保存。");
                        return 0;
                    }
                    if (command == "import-hmi")
                    {
                        int failures = 0;
                        foreach (HmiContext hmi in session.GetHmis())
                        {
                            ImportResult result = new HmiImporter(config, session.Api).Import(hmi);
                            failures += result.FailureCount;
                            Console.WriteLine("HMI 导入: " + result.SuccessCount + " 个；失败: " + result.FailureCount);
                            if (result.FailureCount != 0)
                            {
                                foreach (ImportFailure failure in result.Failures)
                                    Console.Error.WriteLine(failure.Entry.Name + ": " + failure.RetryError);
                                continue;
                            }
                            CompileSummary summary = session.CompileHmi(hmi);
                            WriteCompileLog(configPath, summary);
                            Console.WriteLine("HMI 编译: 错误 " + summary.ErrorCount + "；警告 " + summary.WarningCount);
                            failures += summary.ErrorCount;
                        }
                        if (failures == 0 && config.SaveProjectAfterImport)
                        {
                            session.SaveProject();
                            Console.WriteLine("HMI 导入已保存；未导入 PLC 源码。");
                        }
                        return failures == 0 ? 0 : 1;
                    }
                    if (command == "archive-copy")
                    {
                        string targetDirectory = GetOption(args, "--target-directory");
                        string targetName = GetOption(args, "--target-name");
                        if (string.IsNullOrWhiteSpace(targetDirectory) || string.IsNullOrWhiteSpace(targetName))
                            throw new ArgumentException("需要 --target-directory 和 --target-name。");
                        session.ArchiveProject(Path.GetFullPath(targetDirectory), targetName);
                        Console.WriteLine("已创建独立工程副本: " + Path.Combine(Path.GetFullPath(targetDirectory), targetName));
                        return 0;
                    }
                    if (command == "keep-devices")
                    {
                        string plcDevice = GetOption(args, "--plc-device");
                        string hmiDevice = GetOption(args, "--hmi-device");
                        if (string.IsNullOrWhiteSpace(plcDevice) || string.IsNullOrWhiteSpace(hmiDevice))
                            throw new ArgumentException("需要 --plc-device 和 --hmi-device。");
                        session.KeepOnlyDevices(plcDevice, hmiDevice);
                        Console.WriteLine("独立工程已保存。");
                        return 0;
                    }
                    if (command == "list-plcs")
                    {
                        session.PrintPlcs();
                        session.PrintHmis();
                        return 0;
                    }
                    if (command == "list-hmi-screens")
                    {
                        session.PrintHmiScreens();
                        return 0;
                    }
                    if (command == "compile-hmi")
                    {
                        HmiContext hmi = session.SelectHmi(GetOption(args, "--hmi"));
                        CompileSummary compile = session.CompileHmi(hmi);
                        string compileLog = WriteCompileLog(configPath, compile);
                        Console.WriteLine(compile.ToReport());
                        Console.WriteLine("编译详情: " + compileLog);
                        return compile.ErrorCount == 0 ? 0 : 1;
                    }
                    if (command == "inspect-hmi-tags")
                    {
                        string names = GetOption(args, "--tags");
                        if (string.IsNullOrWhiteSpace(names)) throw new ArgumentException("需要 --tags。");
                        string outputDirectory = GetOption(args, "--output-directory");
                        if (string.IsNullOrWhiteSpace(outputDirectory)) throw new ArgumentException("需要 --output-directory。");
                        session.InspectHmiTags(GetOption(args, "--hmi"), Path.GetFullPath(outputDirectory),
                            names.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
                        return 0;
                    }
                    if (command == "list-hmi-tag-tables")
                    {
                        session.PrintHmiTagTables(GetOption(args, "--hmi"));
                        return 0;
                    }

                    string plcOverride = GetOption(args, "--plc");
                    if (!string.IsNullOrWhiteSpace(plcOverride)) config.PlcName = plcOverride;

                    PlcContext plc = interactive
                        ? session.SelectPlcInteractive(recent.LastPlcName)
                        : session.SelectPlc(config.PlcName);
                    Console.WriteLine("PLC: " + plc.DisplayName);

                    if (interactive)
                    {
                        recent.LastPlcName = plc.MatchNames.Count == 0 ? plc.DisplayName : plc.MatchNames[0];
                        recent.Save();
                    }

                    if (command == "export-review-context")
                    {
                        ExportResult tags = new PlcExporter(config, session.Api).ExportReviewTags(plc);
                        string context = "PLC tag tables: " + tags.SuccessCount + "; failures: " + tags.FailureCount;
                        foreach (HmiContext hmi in session.GetHmis())
                        {
                            object connections = session.Api.Get(hmi.Target, "Connections", false);
                            context += Environment.NewLine + "HMI " + hmi.DisplayName + " Connections property: "
                                + (connections == null ? "unavailable" : "count=" + session.Api.Items(connections).Count());
                        }
                        File.WriteAllText(Path.Combine(config.SourceDirectory, "review-context.txt"), context, new System.Text.UTF8Encoding(true));
                        Console.WriteLine(context);
                        return tags.FailureCount == 0 ? 0 : 1;
                    }

                    if (command == "export")
                    {
                        if (config.CompileBeforeExport)
                        {
                            Console.WriteLine("导出前编译 PLC，确保块和数据类型一致……");
                            CompileSummary compile = session.CompilePlc(plc);
                            string compileLog = WriteCompileLog(configPath, compile);
                            Console.WriteLine("编译完成: 错误 " + compile.ErrorCount + " 个，警告 "
                                + compile.WarningCount + " 个。详情: " + compileLog);
                            if (compile.ErrorCount > 0)
                            {
                                Console.Error.WriteLine("PLC 编译存在错误；继续导出，其间无法导出的不一致对象会单独计为失败。");
                            }
                        }

                        ExportResult result = new PlcExporter(config, session.Api).Export(plc);
                        foreach (HmiContext hmi in session.GetHmis())
                        {
                            Console.WriteLine("导出 HMI: " + hmi.DisplayName);
                            ExportResult hmiResult = new HmiExporter(config, session.Api).Export(hmi);
                            result.SuccessCount += hmiResult.SuccessCount;
                            result.FailureCount += hmiResult.FailureCount;
                        }
                        Console.WriteLine("PLC/HMI 导出完成: " + result.SuccessCount + " 个对象，失败 " + result.FailureCount + " 个。");
                        Console.WriteLine("目录: " + config.SourceDirectory);
                        return result.FailureCount == 0 ? 0 : 1;
                    }

                    if (command == "export-plc")
                    {
                        ExportResult result = new PlcExporter(config, session.Api).Export(plc);
                        Console.WriteLine("PLC 导出完成: " + result.SuccessCount + " 个对象，失败 " + result.FailureCount + " 个。");
                        return result.FailureCount == 0 ? 0 : 1;
                    }

                    if (command == "compile")
                    {
                        CompileSummary compile = session.CompilePlc(plc);
                        string compileLog = WriteCompileLog(configPath, compile);
                        Console.WriteLine(compile.ToReport());
                        Console.WriteLine("编译详情: " + compileLog);
                        return compile.ErrorCount == 0 ? 0 : 1;
                    }

                    if (command == "import")
                    {
                        bool saveAfterImport = config.SaveProjectAfterImport;
                        config.SaveProjectAfterImport = false;
                        ImportResult result = new PlcImporter(config, session).Import(plc);
                        foreach (HmiContext hmi in session.GetHmis())
                        {
                            Console.WriteLine("导入 HMI: " + hmi.DisplayName);
                            ImportResult hmiResult = new HmiImporter(config, session.Api).Import(hmi);
                            result.SuccessCount += hmiResult.SuccessCount;
                            result.FailureCount += hmiResult.FailureCount;
                            result.Failures.AddRange(hmiResult.Failures);
                        }
                        if (result.FailureCount == 0 && saveAfterImport)
                        {
                            session.SaveProject();
                            Console.WriteLine("PLC/HMI 全部导入成功，TIA Portal 项目已保存。");
                        }
                        else if (result.FailureCount > 0 && saveAfterImport)
                        {
                            Console.WriteLine("存在导入失败项，未保存项目；请在 TIA Portal 中检查后手动保存或关闭项目放弃更改。");
                        }
                        Console.WriteLine("PLC/HMI 导入完成: " + result.SuccessCount + " 个对象，失败 " + result.FailureCount + " 个。");
                        if (!string.IsNullOrWhiteSpace(result.ErrorLogPath))
                        {
                            Console.WriteLine("失败详情: " + result.ErrorLogPath);
                        }
                        return result.FailureCount == 0 ? 0 : 1;
                    }

                    Console.Error.WriteLine("未知命令: " + command);
                    PrintUsage();
                    return 2;
                }
            }
            catch (OperationCanceledException ex)
            {
                Console.WriteLine(ex.Message);
                return 3; // cancellation is not success
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine("错误: " + ex.Message);
                WriteErrorLog(configPath, ex);
                Console.Error.WriteLine("详细错误已写入: " + GetErrorLogPath(configPath));
                if (Environment.GetEnvironmentVariable("TIA_BRIDGE_VERBOSE") == "1")
                {
                    Console.Error.WriteLine(ex);
                }
                return 1;
            }
        }

        private static string GetConfigPath(string[] args)
        {
            for (int i = 1; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "--config", StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1];
                }
            }

            DirectoryInfo directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            for (int i = 0; i < 8 && directory != null; i++)
            {
                string candidate = Path.Combine(directory.FullName, "config.json");
                if (File.Exists(candidate)) return candidate;
                directory = directory.Parent;
            }

            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
        }

        private static string GetOption(string[] args, string option)
        {
            for (int i = 1; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], option, StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1];
                }
            }
            return "";
        }

        private static bool IsHelp(string value)
        {
            return value == "-h" || value == "--help" || value == "/?" || value == "help";
        }

        private static void WriteErrorLog(string configPath, Exception ex)
        {
            try
            {
                string content = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine
                    + ex + Environment.NewLine;
                File.WriteAllText(GetErrorLogPath(configPath), content, new System.Text.UTF8Encoding(true));
            }
            catch
            {
            }
        }

        private static string GetErrorLogPath(string configPath)
        {
            string fullConfigPath = Path.GetFullPath(configPath);
            string directory = Path.GetDirectoryName(fullConfigPath);
            return Path.Combine(directory, "last-error.log");
        }

        private static string WriteCompileLog(string configPath, CompileSummary summary)
        {
            string fullConfigPath = Path.GetFullPath(configPath);
            string path = Path.Combine(Path.GetDirectoryName(fullConfigPath), "last-compile.log");
            File.WriteAllText(path,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine
                + summary.ToReport() + Environment.NewLine,
                new System.Text.UTF8Encoding(true));
            return path;
        }

        private static void PrintUsage()
        {
            Console.WriteLine("TIA Portal Source Bridge (v2.2)");
            Console.WriteLine();
            Console.WriteLine("用法:");
            Console.WriteLine("  TiaPortalSourceBridge.exe web [--port <端口>] [--no-browser] [--config <config.json>]");

            Console.WriteLine("  TiaPortalSourceBridge.exe interactive [--project <Project.apXX>] [--config <config.json>]");
            Console.WriteLine("  TiaPortalSourceBridge.exe export [--project <Project.apXX>] [--plc <PLC名>] [--config <config.json>]");
            Console.WriteLine("  TiaPortalSourceBridge.exe export-plc [--project <Project.apXX>] [--plc <PLC名>] [--config <config.json>]");
            Console.WriteLine("  TiaPortalSourceBridge.exe export-review-context [--config <config.json>]");
            Console.WriteLine("  TiaPortalSourceBridge.exe import [--project <Project.apXX>] [--plc <PLC名>] [--config <config.json>]");
            Console.WriteLine("  TiaPortalSourceBridge.exe import-hmi [--project <Project.apXX>] [--config <config.json>]");
            Console.WriteLine("  TiaPortalSourceBridge.exe compile [--project <Project.apXX>] [--plc <PLC名>] [--config <config.json>]");
            Console.WriteLine("  TiaPortalSourceBridge.exe compile-hmi [--hmi <HMI名>] [--config <config.json>]");
            Console.WriteLine("  TiaPortalSourceBridge.exe list-plcs [--project <Project.apXX>] [--config <config.json>]");
            Console.WriteLine("  TiaPortalSourceBridge.exe list-hmi-screens [--config <config.json>]");
            Console.WriteLine("  TiaPortalSourceBridge.exe list-hmi-tag-tables [--hmi <HMI名>] [--config <config.json>]");
            Console.WriteLine("  TiaPortalSourceBridge.exe inspect-hmi-tags --tags <名称1,名称2> --output-directory <目录> [--hmi <HMI名>]");
            Console.WriteLine("  TiaPortalSourceBridge.exe save-project [--config <config.json>]");
            Console.WriteLine("  TiaPortalSourceBridge.exe archive-copy --project <Project.apXX> --target-directory <目录> --target-name <名称>");
            Console.WriteLine("  TiaPortalSourceBridge.exe keep-devices --project <副本.apXX> --plc-device <设备名> --hmi-device <设备名>");
            Console.WriteLine("  TiaPortalSourceBridge.exe mcp [--config <config.json>]");
            Console.WriteLine("  TiaPortalSourceBridge.exe mcp-host --config <mcp-config.json> [--project <Project.apXX>]");
            Console.WriteLine("  TiaPortalSourceBridge.exe mcp-client");
            Console.WriteLine("  TiaPortalSourceBridge.exe stop-mcp [--config <config.json>]");
            Console.WriteLine();
            Console.WriteLine("说明: 运行 web/看板时会自动在后台启动一个无窗口的 mcp-host（见 logs\\mcp-host.pid），");
            Console.WriteLine("      客户端用 args=[\"mcp-client\"] 接入；不需要时用 stop-mcp 或 StopTiaMcp.bat 结束。");
        }
    }
}
