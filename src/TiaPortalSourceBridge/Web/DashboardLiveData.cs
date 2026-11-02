using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using TiaPortalSourceBridge.Mcp;
using TiaPortalSourceBridge.Openness;

namespace TiaPortalSourceBridge.Web
{
    internal sealed partial class WebServer
    {
        private static readonly object DashboardGate = new object();
        private static McpServer DashboardServer;
        private static string DashboardConfig;
        private static readonly Dictionary<string, List<string>> ProjectHmis = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> ScanningProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> PlcScanErrors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static McpServer Dashboard(string configPath)
        {
            if (DashboardServer == null || !PathsEqual(DashboardConfig, configPath))
            {
                if (DashboardServer != null) DashboardServer.Dispose();
                DashboardServer = new McpServer(configPath);
                DashboardConfig = configPath;
            }
            return DashboardServer;
        }
        private static Dictionary<string, object> ReadDashboard(string configPath, string name, Dictionary<string, object> arguments)
        {
            lock (DashboardGate)
            {
                var reply = Dashboard(configPath).DashboardRead(name, arguments);
                object error;
                if (reply.TryGetValue("isError", out error) && Convert.ToBoolean(error))
                    throw new InvalidOperationException("实时读取失败：" + new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(reply));
                return (Dictionary<string, object>)reply["structuredContent"];
            }
        }
        private static string Text(Dictionary<string, object> map, string key)
        { object value; return map != null && map.TryGetValue(key, out value) ? Convert.ToString(value) : ""; }
        private static IList<PortalProcessInfo> DiscoverDashboardProjects(string configPath)
        {
            var data = ReadDashboard(configPath, "tia_list_open_projects", new Dictionary<string, object>());
            object value;
            var rows = data.TryGetValue("processes", out value) ? value as object[] : null;
            return (rows ?? new object[0]).Cast<Dictionary<string, object>>().Select(r => new PortalProcessInfo {
                ProcessId = Convert.ToInt32(r["process_id"]), ProjectPath = Text(r, "project_path"),
                PortalVersion = Text(r, "portal_version"), ExecutablePath = Text(r, "executable_path"), Mode = Text(r, "mode")
            }).ToList();
        }
        private static object[] DashboardTools(string configPath)
        {
            lock (DashboardGate)
                return Dashboard(configPath).DashboardToolDefinitions().Cast<Dictionary<string, object>>().Select(t => {
                    var annotations = t["annotations"] as Dictionary<string, object>;
                    object readOnly;
                    return (object)new { name = Text(t, "name"), description = Text(t, "description"), category = Text(t, "description").StartsWith("[V21]") ? "V21" : "TIA", safe = annotations != null && annotations.TryGetValue("readOnlyHint", out readOnly) && Convert.ToBoolean(readOnly) };
                }).ToArray();
        }
        private static List<Dictionary<string, string>> GetPlcsFast(string rootDir, string projectPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath)) return new List<Dictionary<string, string>>();
            bool live;
            lock (_portalLock) live = _cachedPortalProcs.Any(p => PathsEqual(p.ProjectPath, projectPath));
            if (!live)
            {
                // Offline evidence is scoped to the selected file; never reuse another project's global cache.
                lock (_plcLock) { _cachedPlcList = null; _cachedHmiList = null; }
                return GetPlcsOffline(rootDir, projectPath);
            }
            lock (_plcLock)
            {
                List<Dictionary<string, string>> cached;
                if (_projectPlcsCache.TryGetValue(projectPath, out cached))
                {
                    List<string> hmis; _cachedHmiList = ProjectHmis.TryGetValue(projectPath, out hmis) ? hmis : null;
                    return cached;
                }
            }
            lock (_plcLock) { _cachedHmiList = null; }
            TriggerPlcScanAsync(Path.Combine(rootDir, Path.GetFileName(DashboardConfig ?? "config.json")), projectPath);
            return new List<Dictionary<string, string>>();
        }
        private static void TriggerPlcScanAsync(string configPath, string projectPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath)) return;
            lock (_portalLock) if (!_cachedPortalProcs.Any(p => PathsEqual(p.ProjectPath, projectPath))) return;
            lock (_plcLock) { if (!ScanningProjects.Add(projectPath)) return; }
            ThreadPool.QueueUserWorkItem(_ => {
                try
                {
                    var data = ReadDashboard(configPath, "tia_list_plcs", new Dictionary<string, object> { { "project_path", projectPath } });
                    var rows = new List<Dictionary<string, string>>();
                    foreach (string full in ((object[])data["plcs"]).Select(Convert.ToString))
                    {
                        string cpu = ExtractCpuName(full), station = ExtractStationName(full);
                        string prefix = "/devices/" + Uri.EscapeDataString(station) + "/items/" + Uri.EscapeDataString(cpu);
                        var hardware = ReadDashboard(configPath, "tia_read_hardware_configuration", new Dictionary<string, object> { { "project_path", projectPath }, { "path_prefix", prefix }, { "include_attributes", true } });
                        var device = ((object[])hardware["objects"]).Cast<Dictionary<string, object>>().FirstOrDefault(o => Text(o, "path") == prefix);
                        var attrs = device != null && device["attributes"] is object[] ? (object[])device["attributes"] : new object[0];
                        Func<string, string> attr = key => attrs.Cast<Dictionary<string, object>>().Where(a => Text(a, "name") == key).Select(a => Text(a, "value")).FirstOrDefault() ?? "";
                        string model = attr("TypeName"), order = attr("OrderNumber");
                        rows.Add(new Dictionary<string, string> { { "cpuName", cpu }, { "fullName", full }, { "stationName", station }, { "hardwareType", model }, { "orderNumber", order }, { "displayName", cpu + (model.Length == 0 ? "" : " [" + model + "]") } });
                    }
                    lock (_plcLock)
                    {
                        _projectPlcsCache[projectPath] = rows;
                        ProjectHmis[projectPath] = data.ContainsKey("hmis") ? ((object[])data["hmis"]).Select(Convert.ToString).ToList() : new List<string>();
                        PlcScanErrors.Remove(projectPath);
                    }
                }
                catch (Exception ex) { lock (_plcLock) PlcScanErrors[projectPath] = ex.Message; }
                finally { lock (_plcLock) ScanningProjects.Remove(projectPath); }
            });
        }
    }
}
