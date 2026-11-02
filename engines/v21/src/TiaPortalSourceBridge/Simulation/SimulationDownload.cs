using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security;
using TiaPortalSourceBridge.Openness;

namespace TiaPortalSourceBridge.Simulation
{
    internal static class SimulationDownload
    {
        internal static Dictionary<string, object> Execute(OpennessSession session, PlcContext plc, string ip,
            string interfaceHint, int rack, int slot, bool startPlc)
        {
            ReflectionApi api = session.Api;
            EnsureLegacyPlcCommunicationIfAvailable(api, session.Project);
            object target = plc.DeviceItem ?? api.Get(plc.Software, "Parent", true);
            object downloadProvider = api.GetService(target, "Siemens.Engineering.Download.DownloadProvider");
            if (downloadProvider == null)
                throw new InvalidOperationException("PLC 设备不支持 Openness V21 DownloadProvider：" + plc.DisplayName);

            object configuration = api.Get(downloadProvider, "Configuration", true);
            object interfaceObject = SelectInterface(api, configuration, interfaceHint);
            object targetConfiguration = FindTargetInterface(api, interfaceObject, rack, slot);
            ConfigureAddress(api, targetConfiguration, ip);

            List<string> applied = new List<string>();
            Type delegateType = api.RequireType("Siemens.Engineering.Download.DownloadConfigurationDelegate");
            Delegate pre = BuildDelegate(delegateType, delegate(object c) { Configure(api, c, applied, false); });
            Delegate post = BuildDelegate(delegateType, delegate(object c) { if (startPlc) Configure(api, c, applied, true); });
            object options = Enum.Parse(api.RequireType("Siemens.Engineering.Download.DownloadOptions"), "Hardware, Software");

            MethodInfo download = downloadProvider.GetType().GetMethods().First(m => m.Name == "Download" && m.GetParameters().Length == 4);
            object result = download.Invoke(downloadProvider, new[] { targetConfiguration, pre, post, options });
            string state = Convert.ToString(api.Get(result, "State", false) ?? "");
            int errors = Convert.ToInt32(api.Get(result, "ErrorCount", false) ?? 0);
            int warnings = Convert.ToInt32(api.Get(result, "WarningCount", false) ?? 0);
            string messages = CollectMessages(api, api.Get(result, "Messages", false));
            if (errors > 0 || string.Equals(state, "Error", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("PLCSIM 下载失败（State=" + state + ", Errors=" + errors + "）：\n" + messages);

            return new Dictionary<string, object>
            {
                { "plc", plc.DisplayName }, { "ip", ip }, { "state", state },
                { "error_count", errors }, { "warning_count", warnings },
                { "applied_configurations", applied.ToArray() }, { "messages", messages }
            };
        }

        private static void EnsureLegacyPlcCommunicationIfAvailable(ReflectionApi api, object project)
        {
            try
            {
                object settingsFolder = api.Get(project, "SettingsFolder", false);
                if (settingsFolder == null) return;
            }
            catch
            {
            }
        }

        private static object SelectInterface(ReflectionApi api, object configuration, string hint)
        {
            var all = new List<object>();
            foreach (object mode in api.Items(api.Get(configuration, "Modes", true)))
                if (Convert.ToString(api.Get(mode, "Name", false) ?? "").IndexOf("PN/IE", StringComparison.OrdinalIgnoreCase) >= 0)
                    foreach (object pc in api.Items(api.Get(mode, "PcInterfaces", true)))
                        all.Add(pc);
            object selected = all.FirstOrDefault(pc => Convert.ToString(api.Get(pc, "Name", false) ?? "").IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0)
                ?? all.FirstOrDefault(pc => Convert.ToString(api.Get(pc, "Name", false) ?? "").IndexOf("PLCSIM", StringComparison.OrdinalIgnoreCase) >= 0);
            if (selected == null)
                throw new InvalidOperationException("未找到虚拟仿真网卡（关键字：" + hint + "）。为防止误下载到物理网卡，仅允许 PLCSIM 接口。可用接口："
                    + string.Join(" | ", all.Select(pc => Convert.ToString(api.Get(pc, "Name", false)))));
            return selected;
        }

        private static object FindTargetInterface(ReflectionApi api, object pcInterface, int rack, int slot)
        {
            foreach (object target in api.Items(api.Get(pcInterface, "TargetInterfaces", false)))
                return target;
            foreach (object station in api.Items(api.Get(pcInterface, "TargetStations", false)))
                foreach (object r in api.Items(api.Get(station, "Racks", false)))
                    if (Convert.ToInt32(api.Get(r, "Number", false) ?? -1) == rack)
                        foreach (object s in api.Items(api.Get(r, "Slots", false)))
                            if (Convert.ToInt32(api.Get(s, "Number", false) ?? -1) == slot)
                                return s;
            throw new InvalidOperationException("在仿真接口下未找到目标槽位 Rack " + rack + " Slot " + slot + "。");
        }

        private static void ConfigureAddress(ReflectionApi api, object targetConfiguration, string ip)
        {
            object addresses = api.Get(targetConfiguration, "Addresses", false);
            if (addresses == null) return;
            object existing = api.Items(addresses).FirstOrDefault(a => string.Equals(Convert.ToString(api.Get(a, "Address", false)), ip, StringComparison.OrdinalIgnoreCase));
            if (existing == null) api.Invoke(addresses, "Create", ip);
        }

        private static Delegate BuildDelegate(Type delegateType, Action<object> handler)
        {
            var bridge = new DelegateBridge(handler);
            return Delegate.CreateDelegate(delegateType, bridge, typeof(DelegateBridge).GetMethod("Invoke"));
        }

        private sealed class DelegateBridge
        {
            private readonly Action<object> _handler;
            public DelegateBridge(Action<object> handler) { _handler = handler; }
            public void Invoke(object configuration) { _handler(configuration); }
        }

        private static void Configure(ReflectionApi api, object item, List<string> applied, bool isPostDownload)
        {
            if (item == null) return;
            Type type = item.GetType();
            string name = type.Name;

            // 1. Handle V21 DownloadPasswordConfiguration (PlcMasterSecretPassword, ModuleWriteAccessPassword, etc.)
            PropertyInfo isSecureProp = type.GetProperty("IsSecure");
            MethodInfo setPasswordMethod = type.GetMethod("SetPassword", new[] { typeof(SecureString) });
            if (setPasswordMethod != null)
            {
                string envPwd = Environment.GetEnvironmentVariable("TIA_SIMULATION_PLC_PASSWORD");
                if (!string.IsNullOrEmpty(envPwd))
                {
                    using (SecureString secure = ToSecureString(envPwd))
                    {
                        setPasswordMethod.Invoke(item, new object[] { secure });
                        applied.Add(name + "=SetPassword(from env)");
                        return;
                    }
                }
                applied.Add(name + "=PasswordRequired(set TIA_SIMULATION_PLC_PASSWORD if CPU has protection)");
                return;
            }

            // 2. Handle V21 DownloadCheckConfiguration subclasses (DownloadCertificate, FitHmiComponents, UpgradeTargetDevice, CheckBeforeDownload, etc.)
            PropertyInfo checkedProp = type.GetProperty("Checked");
            if (checkedProp != null && checkedProp.CanWrite && checkedProp.PropertyType == typeof(bool))
            {
                checkedProp.SetValue(item, true, null);
                applied.Add(name + ".Checked=true");
                return;
            }

            // 3. Handle V21 Selection-based DownloadConfigurations
            PropertyInfo current = type.GetProperty("CurrentSelection");
            if (current == null || !current.CanWrite)
            {
                applied.Add(name + "=Unhandled(" + Convert.ToString(api.Get(item, "Message", false) ?? "") + ")");
                return;
            }

            string[] preferred = PreferredSelections(name, isPostDownload);
            foreach (string choice in preferred)
            {
                if (!Enum.GetNames(current.PropertyType).Any(n => string.Equals(n, choice, StringComparison.OrdinalIgnoreCase)))
                    continue;
                object parsed = Enum.Parse(current.PropertyType, choice, true);
                current.SetValue(item, parsed, null);
                applied.Add(name + "=" + parsed);
                return;
            }

            applied.Add(name + "=Default(" + Convert.ToString(current.GetValue(item, null)) + ")");
        }

        private static string[] PreferredSelections(string configTypeName, bool isPostDownload)
        {
            if (isPostDownload)
            {
                return new[] { "StartModule", "Wait", "AcceptAll", "NoAction" };
            }
            switch (configTypeName)
            {
                case "TargetForSoftware":
                    return new[] { "PlcSimulationAdvanced", "CPU" };
                case "DataBlockReinitializationOrKeepActualValues":
                    return new[] { "KeepActualValues", "StopPlcAndReinitialize" };
                case "ProtectionLevelChanged":
                    return new[] { "ContinueDownloading" };
                case "UserManagementDownload":
                    return new[] { "DownloadAllUserManagementDataResetToProject", "UpdateUserManagementDataButKeepOnlinePassword", "KeepOnlineUserManagementData" };
                case "LoadIdentificationData":
                    return new[] { "LoadData", "LoadNothing" };
                case "OverwriteOnMemoryCard":
                    return new[] { "Load", "NoAction" };
                case "StopHSystemOrModule":
                    return new[] { "StopModule", "StopHSystem" };
                default:
                    return new[]
                    {
                        "StopAll",
                        "DownloadAllBlocks",
                        "ConsistentDownload",
                        "StopPlcAndReinitialize",
                        "AcceptAll",
                        "Overwrite",
                        "ContinueDownloading",
                        "Download",
                        "StartModule",
                        "Wait"
                    };
            }
        }

        private static SecureString ToSecureString(string value)
        {
            SecureString secure = new SecureString();
            for (int i = 0; i < value.Length; i++)
            {
                secure.AppendChar(value[i]);
            }
            secure.MakeReadOnly();
            return secure;
        }

        private static string CollectMessages(ReflectionApi api, object messages)
        {
            var lines = new List<string>();
            WalkMessages(api, messages, lines);
            return string.Join(Environment.NewLine, lines);
        }

        private static void WalkMessages(ReflectionApi api, object messages, List<string> lines)
        {
            foreach (object msg in api.Items(messages))
            {
                string text = Convert.ToString(api.Get(msg, "Message", false) ?? "");
                if (!string.IsNullOrWhiteSpace(text)) lines.Add(text);
                WalkMessages(api, api.Get(msg, "Messages", false), lines);
            }
        }
    }
}
