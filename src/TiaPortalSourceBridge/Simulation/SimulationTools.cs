using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using TiaPortalSourceBridge.Mcp;

namespace TiaPortalSourceBridge.Simulation
{
    internal sealed class SimulationTools
    {
        internal const string Fixture = "DB_BridgeSim_AutoReset";
        internal const uint Signature = 0x42525332;
        private readonly string root;
        private readonly Func<ISimulationRuntime> factory;
        private ISimulationRuntime runtime;
        private ISimulationRuntime Api { get { return runtime ?? (runtime = factory()); } }
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 };
        internal SimulationTools(string root, Func<ISimulationRuntime> factory = null) { this.root = root; this.factory = factory ?? (() => new AdvancedRuntime()); }
        internal static string Text(Dictionary<string, object> m, string key) { object v; return m.TryGetValue(key, out v) ? Convert.ToString(v) : ""; }
        internal static bool Flag(Dictionary<string, object> m, string key) { object v; return m.TryGetValue(key, out v) && Convert.ToBoolean(v); }
        internal static Dictionary<string, object> Map(params object[] pairs)
        { var m = new Dictionary<string, object>(); for (int i = 0; i < pairs.Length; i += 2) m.Add((string)pairs[i], pairs[i + 1]); return m; }
        internal static string EnumText(object value)
        { return value == null ? "" : value.GetType().IsEnum ? Enum.GetName(value.GetType(), value) ?? Convert.ToString(value) : Convert.ToString(value); }
        private string Manifest(string name)
        {
            using (var sha = SHA256.Create()) return Path.Combine(root, "simulation-sessions", BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(name))).Replace("-", "") + ".json");
        }
        private void Save(string path, object value)
        { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, json.Serialize(value), new UTF8Encoding(false)); }
        internal object Detail(object instance)
        { return Map("instance_name", Api.Get(instance, "Name"), "instance_id", Api.Get(instance, "ID"), "state", EnumText(Api.Get(instance, "OperatingState")), "communication", EnumText(Api.Get(instance, "CommunicationInterface")), "ip_addresses", Api.Get(instance, "ControllerIP"), "license_status", EnumText(Api.Get(instance, "LicenseStatus"))); }
        internal object Owned(Dictionary<string, object> args)
        {
            string name = Text(args, "instance_name");
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("必须明确 instance_name。");
            string file = Manifest(name);
            if (!File.Exists(file)) throw new InvalidOperationException("此实例不是本工具创建的受管本机实例，拒绝控制或下载。可使用状态接口只读查看。");
            var receipt = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(file));
            string project = ProjectRouter.Normalize(Text(args, "project_path"));
            if (!string.Equals(Text(receipt, "project_path"), project, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("仿真实例绑定的项目与请求不一致。");
            object instance = Api.Open(name);
            if (Text(receipt, "instance_name") != name || Convert.ToInt32(receipt["instance_id"]) != Convert.ToInt32(Api.Get(instance, "ID")))
                throw new InvalidOperationException("仿真实例身份已经变化，拒绝使用旧凭据。");
            if (EnumText(Api.Get(instance, "CommunicationInterface")) != "Softbus") throw new InvalidOperationException("仅允许本机 Softbus 仿真，拒绝 TCP/IP 或其他通信接口。");
            return instance;
        }
        internal object Status()
        {
            try
            {
                return Map("available", Convert.ToBoolean(Api.Get(null, "IsRuntimeManagerAvailable")), "api_path", Api.ApiPath,
                    "instances", Api.Instances().Select(info => Detail(Api.Open(Convert.ToString(info.GetType().GetField("Name").GetValue(info))))).ToArray(),
                    "backend", "PLCSIM Advanced local API", "ordinary_plcsim_automatic_control", false);
            }
            catch (Exception ex) { return Map("available", false, "error", ex.GetBaseException().Message, "ordinary_plcsim_automatic_control", false); }
        }
        internal object Start(Dictionary<string, object> args)
        {
            if (!Flag(args, "confirm")) throw new ArgumentException("启动本机仿真实例必须 confirm=true。");
            string project = ProjectRouter.Normalize(Text(args, "project_path"));
            if (!File.Exists(project)) throw new FileNotFoundException("目标项目不存在。", project);
            string name = Text(args, "instance_name");
            if (name.Length == 0) name = "BridgeSim_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            if (!name.StartsWith("BridgeSim_", StringComparison.Ordinal) || name.Length > 64 || name.Any(c => !char.IsLetterOrDigit(c) && c != '_'))
                throw new ArgumentException("受管实例名称必须以 BridgeSim_ 开头，仅使用字母、数字、下划线，最多64字符。");
            object instance;
            bool exists = Api.Instances().Any(info => Convert.ToString(info.GetType().GetField("Name").GetValue(info)) == name);
            if (exists) { var ownedArgs = new Dictionary<string, object>(args); ownedArgs["instance_name"] = name; instance = Owned(ownedArgs); }
            else
            {
                string cpu = Text(args, "cpu_type"); if (cpu.Length == 0) cpu = "CPU1513";
                if (!new[] { "CPU1511", "CPU1513", "CPU1515", "CPU1516", "CPU1517", "CPU1518", "CPU1500_Unspecified" }.Contains(cpu)) throw new ArgumentException("不支持该仿真 CPU 类型。");
                instance = Api.Register(name, cpu);
                Api.Set(instance, "CommunicationInterface", "Softbus");
                Save(Manifest(name), Map("project_path", project, "instance_name", name, "instance_id", Api.Get(instance, "ID"), "created_at_utc", DateTime.UtcNow.ToString("o"), "api_path", Api.ApiPath));
            }
            string state = EnumText(Api.Get(instance, "OperatingState"));
            object code = null;
            if (state == "Off") code = Api.Call(instance, "PowerOn", (uint)45000);
            else if (state != "Stop" && state != "Run") throw new InvalidOperationException("实例正在启动或关闭；请查询状态，不自动重复 PowerOn。");
            if ((code != null && code.GetType().IsEnum && Convert.ToInt32(code) < 0) || !new[] { "Stop", "Run" }.Contains(EnumText(Api.Get(instance, "OperatingState"))))
                throw new InvalidOperationException("仿真启动未确认完成，请查询实例状态；不自动重复 PowerOn。");
            if (Flag(args, "show_ui")) ShowPanel();
            return Map("project_path", project, "instance", Detail(instance), "power_on_result", code == null ? null : EnumText(code));
        }
        private static void ShowPanel()
        {
            foreach (string uninstall in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
            using (var baseKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(uninstall))
            {
                if (baseKey == null) continue;
                foreach (string name in baseKey.GetSubKeyNames()) using (var key = baseKey.OpenSubKey(name))
                {
                    if (key == null || !Convert.ToString(key.GetValue("DisplayName")).StartsWith("SIMATIC S7-PLCSIM Advanced V", StringComparison.OrdinalIgnoreCase)) continue;
                    string path = Path.Combine(Convert.ToString(key.GetValue("InstallLocation")), "bin", "Siemens.Simatic.PlcSim.Advanced.UserInterface.exe");
                    if (File.Exists(path)) { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Normal }); return; }
                }
            }
            throw new FileNotFoundException("仿真 CPU 已启动，但未找到 Advanced 控制面板程序。请查询仿真状态。");
        }
        internal object Control(Dictionary<string, object> args)
        {
            if (!Flag(args, "confirm")) throw new ArgumentException("仿真运行状态控制必须 confirm=true。");
            object instance = Owned(args); string action = Text(args, "action");
            string method = action == "run" ? "Run" : action == "stop" ? "Stop" : action == "power_off" ? "PowerOff" : null;
            if (method == null) throw new ArgumentException("action 仅支持 run、stop、power_off。");
            Api.Call(instance, method, (uint)30000);
            return Map("project_path", Text(args, "project_path"), "instance", Detail(instance));
        }
        internal object Read(Dictionary<string, object> args)
        {
            object instance = Owned(args); object raw;
            var tags = args.TryGetValue("tags", out raw) ? raw as object[] : null;
            if (tags == null || tags.Length == 0 || tags.Length > 200) throw new ArgumentException("tags 必须为1..200项 {name,type} 数组。");
            Api.Call(instance, "UpdateTagList");
            var items = new List<object>();
            foreach (var tag in tags)
            {
                var row = tag as Dictionary<string, object>; if (row == null) throw new ArgumentException("标签必须是对象。");
                string name = Text(row, "name"), type = Text(row, "type");
                try { items.Add(Map("name", name, "type", type, "value", ReadValue(instance, name, type), "ok", true)); }
                catch (Exception ex) { items.Add(Map("name", name, "type", type, "ok", false, "error", ex.GetBaseException().Message)); }
            }
            return Map("instance_name", Text(args, "instance_name"), "items", items.ToArray(), "failure_count", items.OfType<Dictionary<string, object>>().Count(row => !(bool)row["ok"]));
        }
        private object ReadValue(object instance, string tag, string type)
        {
            string method = type == "bool" ? "ReadBool" : type == "udint" ? "ReadUInt32" : type == "dint" || type == "time" ? "ReadInt32" : type == "real" ? "ReadFloat" : null;
            if (method == null) throw new ArgumentException("type 仅支持 bool、udint、dint、time、real。");
            return Api.Call(instance, method, tag);
        }
        private void CheckFixture(object instance)
        {
            Api.Call(instance, "UpdateTagList");
            if (Convert.ToUInt32(Api.Call(instance, "ReadUInt32", Fixture + ".Signature")) != Signature) throw new InvalidOperationException("仿真测试 DB 签名不匹配，拒绝测试写入。");
        }
        internal object WriteTest(Dictionary<string, object> args)
        {
            if (!Flag(args, "confirm")) throw new ArgumentException("测试写入必须 confirm=true。");
            string field = Text(args, "field");
            if (!new[] { "Enable", "Trigger", "ManualReset", "ClearCount" }.Contains(field)) throw new ArgumentException("只允许写自动复位测试 DB 的 Enable、Trigger、ManualReset、ClearCount。");
            object raw; if (!args.TryGetValue("value", out raw) || !(raw is bool)) throw new ArgumentException("value 必须为布尔值。");
            object instance = Owned(args); CheckFixture(instance);
            Api.Call(instance, "WriteBool", Fixture + "." + field, (bool)raw);
            return Map("instance_name", Text(args, "instance_name"), "field", field, "value", Api.Call(instance, "ReadBool", Fixture + "." + field));
        }
        internal object TestAutoReset(Dictionary<string, object> args)
        {
            if (!Flag(args, "confirm")) throw new ArgumentException("运行自动复位测试必须 confirm=true。");
            object instance = Owned(args); CheckFixture(instance);
            if (EnumText(Api.Get(instance, "OperatingState")) != "Run") throw new InvalidOperationException("仿真 CPU 必须处于 RUN。");
            int delay = Convert.ToInt32(Api.Call(instance, "ReadInt32", Fixture + ".ResetDelay"));
            if (delay < 200 || delay > 3000) throw new InvalidOperationException("自动测试需要 ResetDelay 在200..3000ms范围。");
            var checks = new List<object>(); var timer = Stopwatch.StartNew();
            Action<string, bool> write = (field, value) => Api.Call(instance, "WriteBool", Fixture + "." + field, value);
            Func<string, bool> bit = field => Convert.ToBoolean(Api.Call(instance, "ReadBool", Fixture + "." + field));
            Func<uint> count = () => Convert.ToUInt32(Api.Call(instance, "ReadUInt32", Fixture + ".AutoResetCount"));
            Action<bool, string> check = (ok, name) => { checks.Add(Map("name", name, "passed", ok, "elapsed_ms", timer.ElapsedMilliseconds)); if (!ok) throw new InvalidOperationException(name); };
            bool passed = false; string error = "";
            try
            {
                write("Enable", false); write("Trigger", false); write("ManualReset", false); write("ClearCount", true); Thread.Sleep(150);
                check(!bit("Active") && count() == 0, "关闭使能并清零");
                write("ClearCount", false); write("Enable", true); Thread.Sleep(100); write("Trigger", true); Thread.Sleep(75);
                check(bit("Active"), "触发上升沿使输出置位");
                Wait(() => count() == 1 && !bit("Active"), delay + 1500);
                check(count() == 1 && !bit("Active"), "到时自动复位且计数一次");
                Thread.Sleep(delay + 100); check(count() == 1 && !bit("Active"), "持续高电平不重复触发");
                write("Trigger", false); Thread.Sleep(100); write("Trigger", true); Thread.Sleep(75); check(bit("Active"), "重新上升沿再次触发");
                write("ManualReset", true); Thread.Sleep(100); check(!bit("Active") && count() == 1, "手动复位取消且不增加自动次数");
                write("ManualReset", false); write("Trigger", false); Thread.Sleep(100); write("Trigger", true); Thread.Sleep(75); write("Enable", false); Thread.Sleep(delay + 100);
                check(!bit("Active") && count() == 1, "关闭使能取消正在运行的定时");
                write("ClearCount", true); Thread.Sleep(100); check(count() == 0, "计数清零");
                passed = true;
            }
            catch (Exception ex) { error = ex.GetBaseException().Message; }
            finally
            {
                // Only release fixture inputs. Never stop another instance or restore arbitrary outputs.
                try { write("Enable", false); write("Trigger", false); write("ManualReset", false); write("ClearCount", false); }
                catch (Exception ex) { passed = false; error += "; 释放测试输入失败：" + ex.GetBaseException().Message; }
            }
            var report = Map("passed", passed, "error", error, "checks", checks.ToArray(), "elapsed_ms", timer.ElapsedMilliseconds,
                "project_path", Text(args, "project_path"), "instance_name", Text(args, "instance_name"), "reset_delay_ms", delay, "physical_plc_accessed", false, "tested_at_utc", DateTime.UtcNow.ToString("o"));
            string path = Path.Combine(root, "simulation-results", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json");
            Save(path, report); report["report_path"] = path; return report;
        }
        private static void Wait(Func<bool> condition, int timeout)
        { var sw = Stopwatch.StartNew(); while (!condition() && sw.ElapsedMilliseconds < timeout) Thread.Sleep(25); }
        internal string[] Addresses(object instance) { return ((IEnumerable)Api.Get(instance, "ControllerIP")).Cast<object>().Select(Convert.ToString).ToArray(); }
        internal void EnsureUniqueAddress(object instance, string address)
        {
            if (!Addresses(instance).Contains(address)) throw new InvalidOperationException("目标仿真实例 IP 在准备期间已改变，未装载。");
            int id = Convert.ToInt32(Api.Get(instance, "ID"));
            foreach (object info in Api.Instances())
            {
                object other = Api.Open(Convert.ToString(info.GetType().GetField("Name").GetValue(info)));
                if (Convert.ToInt32(Api.Get(other, "ID")) != id && EnumText(Api.Get(other, "OperatingState")) != "Off" && Addresses(other).Contains(address))
                    throw new InvalidOperationException("其他本机仿真实例使用相同 IP，无法唯一定位目标，未装载。");
            }
        }
    }
}
