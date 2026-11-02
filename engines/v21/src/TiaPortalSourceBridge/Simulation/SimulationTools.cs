using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using TiaPortalSourceBridge.Online;
using TiaPortalSourceBridge.Openness;

namespace TiaPortalSourceBridge.Simulation
{
    internal static class SimulationTools
    {
        private static readonly Regex InstanceName = new Regex("^[A-Za-z0-9_.-]{1,64}$", RegexOptions.Compiled);

        private static string Str(Dictionary<string, object> a, string k, string fallback = "")
        {
            object v; return a != null && a.TryGetValue(k, out v) && v != null ? Convert.ToString(v).Trim() : fallback;
        }
        private static bool Bool(Dictionary<string, object> a, string k, bool fallback)
        {
            object v; if (a == null || !a.TryGetValue(k, out v) || v == null) return fallback;
            bool b; return v is bool ? (bool)v : (bool.TryParse(v.ToString(), out b) ? b : fallback);
        }
        private static int Int(Dictionary<string, object> a, string k, int fallback, int min, int max)
        {
            object v; int n;
            if (a == null || !a.TryGetValue(k, out v) || v == null || !int.TryParse(v.ToString(), out n)) n = fallback;
            if (n < min || n > max) throw new ArgumentOutOfRangeException(k, k + " 必须在 " + min + ".." + max + " 范围内。");
            return n;
        }
        private static string ValidName(Dictionary<string, object> a)
        {
            string name = Str(a, "instance_name", "TIA_V21_SIM");
            if (!InstanceName.IsMatch(name)) throw new ArgumentException("instance_name 只能包含字母、数字、_、-、.，长度 1..64。");
            return name;
        }
        private static string ValidIp(string value, string field, bool allowEmpty)
        {
            if (string.IsNullOrWhiteSpace(value)) { if (allowEmpty) return ""; throw new ArgumentException(field + " 不能为空。"); }
            IPAddress ip;
            if (!IPAddress.TryParse(value, out ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                throw new ArgumentException(field + " 必须是 IPv4 地址：" + value);
            return ip.ToString();
        }
        private static void RequireConfirm(Dictionary<string, object> a, string action)
        {
            if (!Bool(a, "confirm", false)) throw new InvalidOperationException(action + " 会改变本机仿真 PLC 状态，请设置 confirm=true。");
        }
        private static Dictionary<string, object> Result(string text, object data)
        {
            return new Dictionary<string, object>
            {
                { "isError", false },
                { "content", new object[] { new Dictionary<string, object> { { "type", "text" }, { "text", text } } } },
                { "structuredContent", data }
            };
        }

        internal static bool IsSimulationTool(string name)
        {
            return name == "tia_simulation_status" || name == "tia_simulation_start"
                || name == "tia_simulation_download" || name == "tia_simulation_stop";
        }

        internal static Dictionary<string, object> Status()
        {
            string foundApi = AdvancedRuntime.FindApi();
            if (string.IsNullOrWhiteSpace(foundApi))
            {
                return Result("未在本机检测到 S7-PLCSIM V21 / PLCSIM Advanced Runtime API。", new Dictionary<string, object>
                {
                    { "api_installed", false },
                    { "api_path", "" },
                    { "api_version", "" },
                    { "runtime_manager_available", false },
                    { "supported_cpu_type_count", 0 },
                    { "instances", new object[0] },
                    { "runtime_note", "未找到 Siemens.Simatic.Simulation.Runtime.Api.x64.dll" },
                    { "pg_pc_logical_devices", ListPgPcLogicalDevices() }
                });
            }

            string runtimeNote;
            var instances = AdvancedRuntime.Instances(out runtimeNote);
            return Result("PLCSIM V21 / Advanced 仿真状态已读取。", new Dictionary<string, object>
            {
                { "api_installed", true },
                { "api_path", AdvancedRuntime.LoadedApiPath },
                { "api_version", AdvancedRuntime.ApiVersion() },
                { "runtime_manager_running", AdvancedRuntime.IsRuntimeProcessRunning() },
                { "runtime_manager_available", AdvancedRuntime.IsRuntimeManagerAvailable() },
                { "runtime_manager_exe", AdvancedRuntime.FindRuntimeManagerExe() },
                { "supported_cpu_type_count", AdvancedRuntime.SupportedCpuTypes().Length },
                { "instances", instances },
                { "runtime_note", runtimeNote },
                { "pg_pc_logical_devices", ListPgPcLogicalDevices() }
            });
        }

        private static string[] ListPgPcLogicalDevices()
        {
            HashSet<string> devices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (RegistryKey logDevices = baseKey.OpenSubKey(@"SOFTWARE\Siemens\SINEC\LogDevices"))
                    {
                        if (logDevices == null) continue;
                        foreach (string name in logDevices.GetSubKeyNames())
                        {
                            if (!string.IsNullOrWhiteSpace(name)) devices.Add(name);
                        }
                    }
                }
                catch
                {
                }
            }
            return devices.OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        internal static Dictionary<string, object> Start(Dictionary<string, object> args)
        {
            RequireConfirm(args, "启动 PLCSIM 仿真实例");
            string name = ValidName(args);
            string cpu = Str(args, "cpu_type", "CPU1500_Unspecified");
            if (!AdvancedRuntime.IsValidCpuOrArticle(cpu))
            {
                throw new ArgumentException("不支持的 cpu_type: " + cpu + "。支持 S7-1500 / ET200SP / S7-1200 G2 等 ECPUType 枚举值（如 CPU1516, CPU1515T, CPU1214C_DCDCDC）或 6ES7 订货号。");
            }
            string ip = ValidIp(Str(args, "ip", ""), "ip", true);
            string mask = ValidIp(Str(args, "subnet_mask", "255.255.255.0"), "subnet_mask", false);
            string gateway = ValidIp(Str(args, "gateway", ""), "gateway", true);
            uint timeout = (uint)Int(args, "timeout_ms", 60000, 5000, 300000);
            bool created = !AdvancedRuntime.Instances().Any(i => string.Equals(Convert.ToString(i["name"]), name, StringComparison.OrdinalIgnoreCase));
            using (IDisposable instance = created ? AdvancedRuntime.Register(name, cpu) : AdvancedRuntime.Open(name))
            {
                string state = AdvancedRuntime.State(instance);
                if (state == "Off") AdvancedRuntime.PowerOn(instance, timeout);
                if (!string.IsNullOrWhiteSpace(ip)) AdvancedRuntime.ConfigureIp(instance, ip, mask, gateway);
                if (Bool(args, "run", false) && AdvancedRuntime.State(instance) == "Stop") AdvancedRuntime.Run(instance, timeout);
                bool panel = Bool(args, "show_panel", false) && ShowPanel();
                return Result("仿真实例已就绪：" + name + " (" + AdvancedRuntime.State(instance) + ")", new Dictionary<string, object>
                {
                    { "instance_name", name }, { "created", created }, { "cpu_type", cpu }, { "ip", ip },
                    { "operating_state", AdvancedRuntime.State(instance) }, { "panel_started", panel },
                    { "api_path", AdvancedRuntime.LoadedApiPath }
                });
            }
        }

        internal static Dictionary<string, object> Stop(Dictionary<string, object> args)
        {
            RequireConfirm(args, "停止或注销 PLCSIM 仿真实例");
            string name = ValidName(args);
            string mode = Str(args, "mode", "power_off").ToLowerInvariant();
            if (!new[] { "stop", "power_off", "unregister" }.Contains(mode))
                throw new ArgumentException("mode 必须是 stop、power_off 或 unregister。");
            if (!AdvancedRuntime.Instances().Any(i => string.Equals(Convert.ToString(i["name"]), name, StringComparison.OrdinalIgnoreCase)))
                return Result("仿真实例不存在：" + name, new Dictionary<string, object> { { "instance_name", name }, { "exists", false } });
            uint timeout = (uint)Int(args, "timeout_ms", 30000, 5000, 180000);
            using (IDisposable instance = AdvancedRuntime.Open(name))
            {
                string state = AdvancedRuntime.State(instance);
                if (mode == "stop" && state == "Run") AdvancedRuntime.Stop(instance, timeout);
                if ((mode == "power_off" || mode == "unregister") && state != "Off") AdvancedRuntime.PowerOff(instance, timeout);
                if (mode == "unregister") AdvancedRuntime.Unregister(instance);
                return Result("仿真实例操作完成：" + name + " -> " + mode, new Dictionary<string, object>
                {
                    { "instance_name", name }, { "mode", mode }, { "operating_state", mode == "unregister" ? "Unregistered" : AdvancedRuntime.State(instance) }
                });
            }
        }

        internal static Dictionary<string, object> Download(OpennessSession session, Dictionary<string, object> args)
        {
            if (!Bool(args, "confirm", false))
                throw new InvalidOperationException("仿真下载只会下载到本机 PLCSIM 虚拟控制器，仍需设置 confirm=true。");
            string instance = ValidName(args);
            string ip = ValidIp(Str(args, "ip", ""), "ip", false);
            var running = AdvancedRuntime.Instances().FirstOrDefault(i => string.Equals(Convert.ToString(i["name"]), instance, StringComparison.OrdinalIgnoreCase));
            if (running == null) throw new InvalidOperationException("未找到本机 PLCSIM 实例：" + instance + "；拒绝下载，避免误连物理 PLC。");
            string instanceIp = Convert.ToString(running["ip"] ?? "");
            if (!string.IsNullOrWhiteSpace(instanceIp) && instanceIp != "0.0.0.0" && instanceIp != ip)
                throw new InvalidOperationException("请求 IP (" + ip + ") 与 PLCSIM 实例 IP (" + instanceIp + ") 不一致；已拒绝下载。");

            PlcContext plc = session.SelectPlc(Str(args, "plc_name", ""));
            CompileSummary compile = null;
            if (Bool(args, "compile_before_download", true))
            {
                compile = session.CompilePlc(plc);
                if (compile.ErrorCount > 0)
                    throw new InvalidOperationException("PLC 编译有 " + compile.ErrorCount + " 个错误，无法下载：\n" + compile.ToReport());
            }
            var data = SimulationDownload.Execute(session, plc, ip, Str(args, "interface_hint", "PLCSIM"),
                Int(args, "rack", 0, 0, 7), Int(args, "slot", 1, 0, 31), Bool(args, "start_plc", true));
            data["instance_name"] = instance;
            if (compile != null)
            {
                data["compile_errors"] = compile.ErrorCount;
                data["compile_warnings"] = compile.WarningCount;
            }
            return Result("PLC 程序已下载到 PLCSIM 仿真实例 " + instance + " (" + ip + ")；未保存博途工程。", data);
        }

        private static bool ShowPanel()
        {
            if (Process.GetProcessesByName("S7PLCSIMV21").Length > 0
                || Process.GetProcessesByName("Siemens.Simatic.PlcSim.Advanced.UserInterface").Length > 0
                || Process.GetProcessesByName("Siemens.Simatic.PlcSim.Compact").Length > 0)
            {
                return true;
            }

            // 1. Check S7-PLCSIM V21 UI first (S7PLCSIMV21.exe / Siemens.Simatic.PlcSim.Compact.exe)
            foreach (string progRoot in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"E:\botuv_21", @"D:\Program Files" })
            {
                if (string.IsNullOrWhiteSpace(progRoot)) continue;
                foreach (string exeName in new[] { "S7PLCSIMV21.exe", "Siemens.Simatic.PlcSim.Compact.exe" })
                {
                    string v21Exe = Path.Combine(progRoot, @"Siemens\Automation\PLCSIM_V21\" + exeName);
                    if (File.Exists(v21Exe))
                    {
                        Process.Start(new ProcessStartInfo(v21Exe) { WorkingDirectory = Path.GetDirectoryName(v21Exe) });
                        return true;
                    }
                }
            }

            // 2. Fallback to PLCSIM Advanced UI in Uninstall registry
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
            using (RegistryKey uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
            {
                if (uninstall == null) continue;
                foreach (string sub in uninstall.GetSubKeyNames())
                using (RegistryKey k = uninstall.OpenSubKey(sub))
                {
                    string display = Convert.ToString(k == null ? null : k.GetValue("DisplayName"));
                    string loc = Convert.ToString(k == null ? null : k.GetValue("InstallLocation"));
                    if (display == null || string.IsNullOrWhiteSpace(loc)) continue;

                    if (display.IndexOf("PLCSIM_V21", StringComparison.OrdinalIgnoreCase) >= 0
                        || display.IndexOf("PLCSIM V21", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        foreach (string exeName in new[] { "S7PLCSIMV21.exe", "Siemens.Simatic.PlcSim.Compact.exe" })
                        {
                            string compactExe = Path.Combine(loc, exeName);
                            if (File.Exists(compactExe))
                            {
                                Process.Start(new ProcessStartInfo(compactExe) { WorkingDirectory = loc });
                                return true;
                            }
                        }
                    }

                    if (display.IndexOf("SIMATIC S7-PLCSIM Advanced", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        string exe = Path.Combine(loc, "Siemens.Simatic.PlcSim.Advanced.UserInterface.exe");
                        if (File.Exists(exe))
                        {
                            Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = loc });
                            return true;
                        }
                    }
                }
            }
            return false;
        }
    }
}
