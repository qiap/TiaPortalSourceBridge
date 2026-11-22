using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using TiaPortalSourceBridge.Configuration;

namespace TiaPortalSourceBridge.Mcp
{
    // Keep incompatible Siemens assemblies in the existing per-project STA processes.
    internal sealed partial class McpServer
    {
        private object[] _mergedToolDefinitions;
        private readonly Dictionary<string, IProjectWorker> _localWorkers = new Dictionary<string, IProjectWorker>(StringComparer.OrdinalIgnoreCase);
        internal object[] DashboardToolDefinitions() { return ToolDefinitions(); }
        internal Dictionary<string, object> DashboardRead(string name, Dictionary<string, object> arguments)
        {
            if (!new[] { "tia_list_open_projects", "tia_list_plcs", "tia_read_hardware_configuration", "tia_list_tag_tables", "tia_read_tag_table", "tia_build_project_index", "tia_query_cross_references" }.Contains(name))
                throw new ArgumentException("看板状态接口只允许读取工程与设备信息。");
            return CallTool(name, arguments);
        }
        // Only the validated cylinder workflow calls this entry point. It is never
        // exposed as an arbitrary tool-name/arguments endpoint in the HTTP API.
        internal Dictionary<string, object> DashboardCylinderCall(string name, Dictionary<string, object> arguments)
        {
            if (!new[] { "tia_connection_status", "tia_import_blocks_batch", "tia_save_project", "tia_compile_plc", "tia_read_block" }.Contains(name))
                throw new ArgumentException("气缸写入流程不支持此操作。");
            return CallTool(name, arguments);
        }
        internal Dictionary<string, object> DashboardTagReviewCall(Dictionary<string, object> arguments)
        {
            return CallTool("tia_write_tag_table_review", arguments);
        }
        internal Dictionary<string, object> DashboardReviewedBlocksCall(Dictionary<string, object> arguments)
        {
            return CallTool("tia_write_reviewed_blocks", arguments);
        }
        internal Dictionary<string, object> DashboardPeripheralCall(string action, Dictionary<string, object> arguments)
        {
            if (!new[] { "start", "status", "stop" }.Contains(action)) throw new ArgumentException("Unsupported peripheral action");
            return CallTool("tia_simulation_peripheral_" + action, arguments);
        }
        internal Dictionary<string, object> DashboardWorkbenchCall(string operation, Dictionary<string, object> arguments)
        {
            switch(operation) {
                case "block-read": return CallTool("tia_read_block",arguments);
                case "compare": return CallTool("tia_compare_projects",arguments);
                case "logic-preview": case "logic-status": return CallTool("tia_read_logic_patch_plan",arguments);
                case "logic-apply": return CallTool("tia_write_logic_patch",arguments);
                case "download-preflight": case "download-apply": return CallTool("tia_simulation_download",arguments);
                default: throw new ArgumentException("看板工作区不支持此操作。");
            }
        }
        internal Dictionary<string, object> DashboardHmiCompanionCall(bool apply, Dictionary<string, object> arguments)
        {return CallTool(apply?"tia_write_hmi_companion":"tia_read_hmi_companion_plan",arguments);}
        private string BackendRoot()
        {
            foreach (string start in new[] { Path.GetDirectoryName(_configPath), AppDomain.CurrentDomain.BaseDirectory })
                for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
                {
                    string candidate = Path.Combine(dir.FullName, "engines", "v21");
                    if (File.Exists(Path.Combine(candidate, "mcp-config.json"))) return candidate;
                }
            throw new DirectoryNotFoundException("缺少合并版 V21 引擎 engines/v21，请运行 Build-All.ps1。");
        }
        private IProjectWorker CreateVersionedWorker(string identity)
        {
            if (PortalRoute.FromIdentity(identity) != "V21")
                return new ProjectWorker(System.Reflection.Assembly.GetEntryAssembly().Location, _configPath, identity);
            string root = BackendRoot();
            return new ProjectWorker(Path.Combine(root, "src", "TiaPortalSourceBridge", "bin", "Release", "net48", "TiaPortalSourceBridge.exe"), Path.Combine(root, "mcp-config.json"), identity);
        }
        private object[] ToolDefinitions()
        {
            if (_mergedToolDefinitions != null) return _mergedToolDefinitions;
            var definitions = LocalToolDefinitions().Cast<Dictionary<string, object>>().ToDictionary(t => Convert.ToString(t["name"]), StringComparer.Ordinal);
            string path = Path.Combine(BackendRoot(), "tool-catalog.json");
            var json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
            var extra = (object[])json.DeserializeObject(File.ReadAllText(path));
            foreach (var tool in extra.Cast<Dictionary<string, object>>())
            {
                string name = Convert.ToString(tool["name"]);
                Dictionary<string, object> current;
                if (!definitions.TryGetValue(name, out current))
                {
                    tool["description"] = "[V21] " + Convert.ToString(tool["description"]);
                    definitions.Add(name, tool);
                    continue;
                }
                var schema = (Dictionary<string, object>)current["inputSchema"];
                var otherSchema = (Dictionary<string, object>)tool["inputSchema"];
                var props = (Dictionary<string, object>)schema["properties"];
                foreach (var property in (Dictionary<string, object>)otherSchema["properties"])
                    if (!props.ContainsKey(property.Key)) props.Add(property.Key, property.Value);
            }
            foreach (var tool in definitions.Values)
            {
                string name = Convert.ToString(tool["name"]);
                var props = (Dictionary<string, object>)((Dictionary<string, object>)tool["inputSchema"])["properties"];
                if (props.ContainsKey("project_path"))
                    props["portal_version"] = StringProperty("可选，V16 至 V21。指定工程时须与工程文件版本一致。");
                if (name.StartsWith("tia_simulation_", StringComparison.Ordinal) || name.StartsWith("tia_generate_", StringComparison.Ordinal) || name == "tia_patch_graph")
                {
                    props["portal_version"] = StringProperty("可选 V16、V17、V18、V19、V20、V21；本地生成和仿真按此选择引擎，省略时按 project_path 或唯一连接项目判断，最后使用配置默认版本。");
                    props["project_path"] = StringProperty("可选：用项目版本选择引擎；同时管理多个项目时请明确指定。");
                }
            }
            return _mergedToolDefinitions = definitions.Values.Cast<object>().ToArray();
        }
        private bool TryVersionedLocalCall(string name, Dictionary<string, object> arguments, out Dictionary<string, object> result)
        {
            result = null;
            if (_projects == null) return false; // Legacy project workers must never forward recursively.
            bool local = name.StartsWith("tia_simulation_", StringComparison.Ordinal) && name != "tia_simulation_download"
                || name == "tia_generate_lad" || name == "tia_generate_graph" || name == "tia_generate_hmi_configuration" || name == "tia_patch_graph";
            if (!local) return false;
            string version = PortalRoute.Requested(arguments);
            string xmlVersion = name == "tia_patch_graph" ? PortalRoute.XmlVersion(StringArg(arguments, "xml"))
                : name == "tia_generate_graph" ? PortalRoute.XmlVersion(StringArg(arguments, "base_xml")) : null;
            PortalRoute.RequireMatch(version, xmlVersion);
            version = version ?? xmlVersion ?? _projects.GenerationVersion("");
            if (name == "tia_simulation_stop" && version != "V21")
                throw new ArgumentException("tia_simulation_stop 需要 portal_version=V21；V16 至 V20 请使用 tia_simulation_control。");
            if (version == "V21" && name.StartsWith("tia_simulation_", StringComparison.Ordinal) && !new[] { "tia_simulation_status", "tia_simulation_start", "tia_simulation_stop" }.Contains(name))
                throw new ArgumentException("该扩展仿真工具尚未适配 V21，请使用 V21 的 status/start/stop/download 接口。");

            IProjectWorker worker;
            if (!_localWorkers.TryGetValue(version, out worker))
            {
                worker = CreateVersionedWorker("local:" + version);
                _localWorkers.Add(version, worker);
            }
            var scoped = new Dictionary<string, object>(arguments);
            scoped["portal_version"] = version;
            result = worker.Call(name, scoped);
            return true;
        }
    }
}
