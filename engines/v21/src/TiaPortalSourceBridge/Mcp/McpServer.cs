using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using System.Xml;
using TiaPortalSourceBridge.Configuration;
using TiaPortalSourceBridge.Openness;
using TiaPortalSourceBridge.Online;
using TiaPortalSourceBridge.Simulation;

namespace TiaPortalSourceBridge.Mcp
{
    internal sealed partial class McpServer : IDisposable
    {
        private const string ProtocolVersion = "2025-06-18";
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();
        private TextWriter _protocolOutput;
        private readonly string _configPath;
        private OpennessSession _session;
        private string _sessionPath;
        private string _loadedVersion;
        private DateTime _cachedConfigTimeUtc;
        private string _cachedConfigJson;
        private readonly OnlineTools _online;
        private readonly ProjectRouter _projects;
        private HashSet<string> _knownTools;

        public McpServer(string configPath, bool projectWorker = false)
        {
            _configPath = Path.GetFullPath(configPath);
            _online = new OnlineTools(Path.GetDirectoryName(_configPath));
            if (!projectWorker)
                _projects = new ProjectRouter(identity => new ProjectWorker(System.Reflection.Assembly.GetEntryAssembly().Location, _configPath, identity), () => NewDiscoveryConfig().TiaPortalVersion);
            _json.MaxJsonLength = 32 * 1024 * 1024;
            _protocolOutput = Console.Out;
            Console.SetOut(Console.Error);
        }

        public int Run()
        {
            var utf8 = new UTF8Encoding(false);
            Console.InputEncoding = utf8;
            Console.OutputEncoding = utf8;
            _protocolOutput = new StreamWriter(Console.OpenStandardOutput(), utf8, 65536) { AutoFlush = true };
            var errorWriter = new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true };
            Console.SetError(errorWriter);
            Console.SetOut(errorWriter);
            try { return Run(Console.In, _protocolOutput); }
            finally { Dispose(); }
        }

        public int Run(TextReader input, TextWriter output)
        {
            _protocolOutput = output;
            string line;
            while ((line = input.ReadLine()) != null)
            {
                line = line.Trim().Trim('\uFEFF');
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    Dictionary<string, object> request = _json.DeserializeObject(line) as Dictionary<string, object>;
                    if (request == null)
                    {
                        WriteError(null, -32600, "Invalid Request");
                        continue;
                    }
                    Handle(request);
                }
                catch (Exception ex)
                {
                    WriteError(null, -32700, "Parse error: " + RootMessage(ex));
                }
            }
            return 0;
        }

        private void Handle(Dictionary<string, object> request)
        {
            object id = Value(request, "id");
            string method = Convert.ToString(Value(request, "method") ?? "");
            Dictionary<string, object> parameters = Value(request, "params") as Dictionary<string, object>
                ?? new Dictionary<string, object>();

            if (method == "notifications/initialized" || method.StartsWith("notifications/", StringComparison.Ordinal))
            {
                return;
            }
            if (method == "initialize")
            {
                WriteResult(id, Dict(
                    "protocolVersion", ProtocolVersion,
                    "capabilities", Dict("tools", new Dictionary<string, object>()),
                    "serverInfo", Dict("name", "tia-portal-openness-v21", "version", "4.0.0"),
                    "instructions", "TIA Portal V21 专用 Openness / PLCSIM V8 MCP 服务。支持多个本机 .ap21/.als21 项目并行保持独立 STA 后台连接。先用 tia_connect(project_path) 连接，用 tia_list_connections 查看。工程分析优先：先查看 tia_connect 返回的 index_status；新工程调用 tia_build_project_index 建立目录，再用 tia_get_task_context 获取相关对象，或直接调用 V21 原生 tia_query_cross_references 查询精确读写交叉引用与未使用符号。V21 代码读写推荐优先使用 SimaticSD 文档工具（tia_read_document / tia_import_document / tia_import_documents_batch，原生支持 SCL/LAD/FBD/STL/DB/UDT 的 .s7dcl 与多语言 .s7res），复杂多块 XML 使用 tia_import_blocks_batch（自动启用 SWImportOptions 并在单事务内原子提交，失败自动回滚）。支持 Software Units（通过 tia_list_software_units 查看并在各工具传 unit_name）与无头 WinCC Unified 读取/编译（tia_read_unified_hmi / tia_compile_unified_hmi）。支持 response_mode=compact 减少重复体积。所有写入操作均包裹在 Openness V21 ExclusiveAccess + Transaction 中，默认备份原对象，不自动保存、绝不连接物理 PLC 下载；导入与保存必须 confirm=true。"));
                return;
            }
            if (method == "ping")
            {
                WriteResult(id, new Dictionary<string, object>());
                return;
            }
            if (method == "tools/list")
            {
                WriteResult(id, Dict("tools", ToolDefinitions()));
                return;
            }
            if (method == "tools/call")
            {
                string name = Convert.ToString(Value(parameters, "name") ?? "");
                Dictionary<string, object> arguments = Value(parameters, "arguments") as Dictionary<string, object>
                    ?? new Dictionary<string, object>();
                using (var metrics = OperationMetrics.Begin())
                {
                    Dictionary<string, object> result;
                    try { InvalidateIndexBeforeMutation(name, arguments); result = CallTool(name, arguments); AttachIndexHint(name, arguments, result); }
                    catch (Exception ex) { result = ToolResult("操作失败：" + RootMessage(ex), true, null); }
                    if (StringArg(arguments, "response_mode") == "compact") CompactResponse(result);
                    metrics.Attach(result);
                    WriteResult(id, result);
                }
                return;
            }
            WriteError(id, -32601, "Method not found: " + method);
        }

        private Dictionary<string, object> CallTool(string name, Dictionary<string, object> args)
        {
            string responseMode = StringArg(args, "response_mode");
            if (responseMode.Length > 0 && responseMode != "full" && responseMode != "compact") throw new ArgumentException("response_mode 必须为 full 或 compact。");
            _knownTools = new HashSet<string>(ToolDefinitions().Cast<Dictionary<string, object>>().Select(t => Convert.ToString(t["name"])), StringComparer.Ordinal);
            if (!_knownTools.Contains(name)) throw new ArgumentException("未知或当前配置已禁用的工具：" + name);
            if (name == "tia_list_process_library" || name == "tia_read_process_library" || name == "tia_render_process_template")
                return CallProcessLibraryTool(name, args);
            if (IsAlarmTool(name)) return CallAlarmTool(name, args);
            if (name.StartsWith("tia_online_", StringComparison.Ordinal))
                return (Dictionary<string, object>)_online.Call(name, args);
            if (SimulationTools.IsSimulationTool(name) && name != "tia_simulation_download")
            {
                switch (name)
                {
                    case "tia_simulation_status": return SimulationTools.Status();
                    case "tia_simulation_start": return SimulationTools.Start(args);
                    case "tia_simulation_stop": return SimulationTools.Stop(args);
                    default: throw new ArgumentException("未知仿真工具：" + name);
                }
            }
            if (_projects != null && name != "tia_generate_lad" && name != "tia_generate_graph" && name != "tia_generate_hmi_configuration" && name != "tia_patch_graph")
                return _projects.Call(name, args);
            if (name == "tia_connection_status" || name == "tia_list_connections")
            {
                bool alive = _session != null && _session.IsAlive();
                if (_session != null && !alive)
                {
                    ResetSessionOnly();
                }
                return ToolResult(_session == null ? "未连接 TIA" : "连接保持中：" + _sessionPath, false,
                    Dict("connected", _session != null, "project_path", _sessionPath ?? "", "portal_version", _loadedVersion ?? NewDiscoveryConfig().TiaPortalVersion));
            }
            if (name == "tia_disconnect" || name == "tia_disconnect_all")
            {
                ResetSessionOnly();
                return ToolResult("已断开；未保存、未关闭 TIA 项目。", false, null);
            }
            if (name == "tia_generate_lad")
            {
                string targetVersion = GenerationVersion(args);
                string xml = LadBuilder.Generate(args, targetVersion);
                return ToolResult(xml, false, Dict("xml", xml, "imported", false, "portal_version", targetVersion));
            }
            if (name == "tia_patch_graph")
            {
                XmlDocument doc;
                using (OperationMetrics.Measure("graph_parse")) doc = GraphEditor.Parse(StringArg(args, "xml"));
                using (OperationMetrics.Measure("graph_patch")) GraphEditor.Patch(doc, StringArg(args, "patches_json"),
                    StringArg(args, "new_block_name"), StringArg(args, "new_block_number"));
                return ToolResult("GRAPH 源码已生成，未导入。", false, Dict("xml", doc.OuterXml, "graph", GraphEditor.Inspect(doc), "graph_hash", GraphEditor.Fingerprint(doc), "imported", false));
            }
            if (name == "tia_generate_graph")
            {
                string version = GenerationVersion(args);
                XmlDocument doc;
                using (OperationMetrics.Measure("graph_generate")) doc = GraphBuilder.GenerateDocument(StringArg(args, "model_json"), StringArg(args, "base_xml"), version, Path.GetDirectoryName(_configPath));
                return ToolResult("GRAPH 完整流程已生成，尚未导入。", false, BoolArg(args, "include_model", true) ? Dict("xml", doc.OuterXml, "model", GraphBuilder.ReadModel(doc), "imported", false) : Dict("xml", doc.OuterXml, "imported", false));
            }
            if (name == "tia_generate_hmi_configuration")
            {
                string version = GenerationVersion(args);
                HmiDocument[] objects = HmiModelBuilder.Generate(StringArg(args, "model_json"), version, Path.GetDirectoryName(_configPath));
                return ToolResult("HMI 组态已生成，尚未写入。", false, Dict("objects", objects, "imported", false));
            }
            if (name == "tia_list_open_projects")
            {
                AppConfig discoveryConfig = NewDiscoveryConfig();
                if (!string.IsNullOrWhiteSpace(StringArg(args, "portal_version"))) discoveryConfig.TiaPortalVersion = StringArg(args, "portal_version");
                if (_loadedVersion != null) discoveryConfig.TiaPortalVersion = _loadedVersion;
                IList<PortalProcessInfo> processes = PortalDiscovery.List(discoveryConfig);
                object[] rows = processes.Select(p => (object)Dict(
                    "process_id", p.ProcessId,
                    "portal_version", p.PortalVersion,
                    "mode", p.Mode,
                    "project_path", p.ProjectPath,
                    "executable_path", p.ExecutablePath)).ToArray();
                string text = processes.Count == 0
                    ? "没有检测到所选版本的 TIA Portal 实例；请确认 TIA Portal 已打开，或检查 mcp-config.json 版本设置。"
                    : string.Join(Environment.NewLine, processes.Select(p =>
                        "PID " + p.ProcessId + " | " + p.PortalVersion + " | "
                        + (string.IsNullOrWhiteSpace(p.ProjectPath) ? "未打开项目" : p.ProjectPath)));
                return ToolResult(text, false, Dict("processes", rows));
            }

            string projectPath = ResolveProjectPath(StringArg(args, "project_path"));
            AppConfig config = NewProjectConfig(projectPath, StringArg(args, "plc_name"), StringArg(args, "source_directory"));

            if (IsIndexTool(name)) return CallIndexTool(name, args, config);

            OpennessSession session = Connect(config);
            {
                Console.Error.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + name);
                if (name == "tia_connect")
                    return ToolResult("连接已保持：" + session.GetProjectPath(), false,
                        Dict("project_path", session.GetProjectPath(), "portal_version", _loadedVersion, "index_status", IndexConnectionStatus(config)));
                if (name == "tia_read_hardware_configuration")
                    return ToolResult("硬件、I/O 地址、网络接口和拓扑组态。", false,
                        new HardwareOperations(session).Read(StringArg(args, "path_prefix"), BoolArg(args, "include_attributes", true)));
                if (name == "tia_apply_hardware_configuration")
                {
                    bool dryRun = BoolArg(args, "dry_run", true);
                    if (!dryRun) RequireConfirmation(args, "硬件构建计划会修改离线设备、模块和网络组态");
                    var result = new HardwareOperations(session).Apply(StringArg(args, "plan_json"), Path.Combine(Path.GetDirectoryName(_configPath), "configuration-backups"), dryRun);
                    return ToolResult(dryRun ? "硬件构建计划语法检查完成，尚未执行；设备目录和路径需在执行时核验。" : "硬件组态计划已执行，请核对逐项结果；尚未保存。", !(bool)result["success"], result);
                }
                if (name == "tia_compile_hardware_configuration")
                {
                    CompileSummary summary = new HardwareOperations(session).Compile(StringArg(args, "target_path"));
                    return ToolResult(summary.ToReport(), summary.ErrorCount > 0, Dict("saved", false, "error_count", summary.ErrorCount, "warning_count", summary.WarningCount));
                }
                if (name == "tia_read_hmi_configuration")
                {
                    var result = new HmiOperations(session, session.SelectHmi(StringArg(args, "hmi_name"))).Read(
                        StringArg(args, "kind"), StringArg(args, "name_filter"), StringArg(args, "group_prefix"), BoolArg(args, "include_xml", false), BoolArg(args, "include_model", false));
                    return ToolResult("经典 HMI 组态批量读取结果。", Convert.ToInt32(result["failure_count"]) > 0, result);
                }
                if (name == "tia_write_hmi_configuration")
                {
                    RequireConfirmation(args, "批量写入 HMI 画面、变量等组态会修改离线工程");
                    bool compile = BoolArg(args, "compile", true);
                    HmiContext target = session.SelectHmi(StringArg(args, "hmi_name"));
                    var result = new HmiOperations(session, target).Write(StringArg(args, "objects_json"), Path.Combine(Path.GetDirectoryName(_configPath), "hmi-backups"), BoolArg(args, "skip_unchanged", true));
                    bool failed = Convert.ToInt32(result["failure_count"]) > 0;
                    result["compiled"] = false;
                    if (compile && Convert.ToInt32(result["success_count"]) > 0) try
                    {
                        CompileSummary summary = session.CompileHmi(target); result["compiled"] = true;
                        result["error_count"] = summary.ErrorCount; result["warning_count"] = summary.WarningCount; result["compile_report"] = summary.ToReport(); failed |= summary.ErrorCount > 0;
                    }
                    catch (Exception ex) { failed = true; result["compile_error"] = RootMessage(ex); }
                    return ToolResult("HMI 组态写入结果；尚未保存，请核对逐项状态及编译诊断。", failed, result);
                }
                if (name == "tia_list_plcs")
                {
                    IList<string> plcs = session.GetPlcDisplayNames();
                    string[] hmis = session.GetHmis().Select(h => h.DisplayName).ToArray();
                    string[] unifiedHmis = session.GetUnifiedHmis().Select(u => u.DisplayName).ToArray();
                    string listText = (plcs.Count == 0 ? "没有 PLC" : "PLC：" + string.Join("；", plcs))
                        + Environment.NewLine + (hmis.Length == 0 ? "没有经典 WinCC HMI" : "经典 HMI：" + string.Join("；", hmis))
                        + Environment.NewLine + (unifiedHmis.Length == 0 ? "没有 WinCC Unified HMI" : "WinCC Unified HMI：" + string.Join("；", unifiedHmis));
                    return ToolResult(listText, false,
                        Dict("project_path", session.GetProjectPath(), "plcs", plcs.ToArray(), "hmis", hmis, "unified_hmis", unifiedHmis));
                }
                if (name == "tia_read_unified_hmi")
                {
                    int limit = IntArg(args, "limit", 200);
                    Dictionary<string, object> uResult = UnifiedHmiOperations.Read(
                        session,
                        StringArg(args, "hmi_name"),
                        StringArg(args, "kind"),
                        StringArg(args, "name_filter"),
                        limit);
                    return ToolResult("WinCC Unified 组态读取完成（" + Convert.ToString(uResult["hmi"]) + "）。", false, uResult);
                }
                if (name == "tia_compile_unified_hmi")
                {
                    UnifiedHmiContext uHmi = session.SelectUnifiedHmi(StringArg(args, "hmi_name"));
                    CompileSummary uSummary = session.CompileUnifiedHmi(uHmi);
                    return ToolResult(uSummary.ToReport(), uSummary.ErrorCount > 0,
                        Dict("state", uSummary.State, "error_count", uSummary.ErrorCount,
                            "warning_count", uSummary.WarningCount, "unified_hmi", uHmi.DisplayName, "saved", false));
                }
                if (name == "tia_search_hardware_catalog")
                {
                    int limit = IntArg(args, "limit", 50);
                    Dictionary<string, object> catResult = HardwareCatalogOperations.Search(
                        session,
                        StringArg(args, "filter"),
                        StringArg(args, "catalog_path"),
                        limit);
                    return ToolResult("V21 硬件目录检索完成（共返回 " + catResult["returned_count"] + " 项）。", false, catResult);
                }
                if (name == "tia_export_aml")
                {
                    Dictionary<string, object> amlOut = CaxOperations.ExportAml(
                        session,
                        StringArg(args, "aml_path"),
                        StringArg(args, "device_name"));
                    bool ok = Convert.ToBoolean(amlOut["succeeded"]);
                    return ToolResult("AutomationML 导出" + (ok ? "完成：" : "存在异常：") + Convert.ToString(amlOut["aml_path"]), !ok, amlOut);
                }
                if (name == "tia_import_aml")
                {
                    RequireConfirmation(args, "导入 AutomationML (.aml) 会修改离线硬件与拓扑组态");
                    Dictionary<string, object> amlIn = CaxOperations.ImportAml(
                        session,
                        StringArg(args, "aml_path"),
                        StringArg(args, "import_option"));
                    bool ok = Convert.ToBoolean(amlIn["succeeded"]);
                    return ToolResult("AutomationML 事务导入" + (ok ? "完成（未保存）。" : "失败。"), !ok, amlIn);
                }
                if (name == "tia_compile_hmi")
                {
                    HmiContext hmi = session.SelectHmi(StringArg(args, "hmi_name"));
                    CompileSummary summary = session.CompileHmi(hmi);
                    return ToolResult(summary.ToReport(), summary.ErrorCount > 0,
                        Dict("state", summary.State, "error_count", summary.ErrorCount,
                            "warning_count", summary.WarningCount, "hmi", hmi.DisplayName));
                }
                if (name == "tia_list_hmi_text_lists")
                {
                    HmiContext hmi = session.SelectHmi(StringArg(args, "hmi_name"));
                    string nameFilter = StringArg(args, "name_filter");
                    object textLists = session.Api.Get(hmi.Target, "TextLists", true);
                    List<object> rows = new List<object>();
                    foreach (object item in session.Api.Items(textLists))
                    {
                        string tlName = session.Api.GetString(item, "Name");
                        if (!string.IsNullOrWhiteSpace(nameFilter) &&
                            tlName.IndexOf(nameFilter.Trim(), StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            continue;
                        }
                        rows.Add(Dict("name", tlName, "list_range", session.Api.GetString(item, "ListRange")));
                    }
                    return ToolResult("HMI 文本列表（共 " + rows.Count + " 个）", false,
                        Dict("hmi", hmi.DisplayName, "text_lists", rows.ToArray()));
                }
                if (name == "tia_read_hmi_text_list")
                {
                    HmiContext hmi = session.SelectHmi(StringArg(args, "hmi_name"));
                    string tlName = StringArg(args, "text_list_name").Trim();
                    if (string.IsNullOrWhiteSpace(tlName))
                        throw new ArgumentException("需要提供 text_list_name。");
                    object textLists = session.Api.Get(hmi.Target, "TextLists", true);
                    object target = FindHmiTextList(session.Api, textLists, tlName);
                    if (target == null)
                        throw new InvalidOperationException("找不到 HMI 文本列表: " + tlName);
                    string xml = ExportHmiObjectXml(session.Api, target);
                    return ToolResult(xml, false, Dict("hmi", hmi.DisplayName, "name", session.Api.GetString(target, "Name"), "xml", xml));
                }
                if (name == "tia_import_hmi_text_list")
                {
                    RequireConfirmation(args, "导入 HMI 文本列表会修改离线项目");
                    HmiContext hmi = session.SelectHmi(StringArg(args, "hmi_name"));
                    string xml = StringArg(args, "xml");
                    if (string.IsNullOrWhiteSpace(xml))
                        throw new ArgumentException("需要提供 HMI 文本列表 XML。");
                    XmlDocument doc = new XmlDocument();
                    doc.PreserveWhitespace = true;
                    doc.LoadXml(xml);
                    XmlNode nameNode = doc.SelectSingleNode("//*[local-name()='Hmi.TextGraphicList.TextList']/*[local-name()='AttributeList']/*[local-name()='Name']");
                    string tlName = nameNode != null ? nameNode.InnerText.Trim() : "";
                    if (string.IsNullOrWhiteSpace(tlName))
                        throw new InvalidOperationException("XML 中缺少 Hmi.TextGraphicList.TextList Name 节点。");

                    object textLists = session.Api.Get(hmi.Target, "TextLists", true);
                    object existing = FindHmiTextList(session.Api, textLists, tlName);
                    string backupFile = "";
                    if (existing != null)
                    {
                        string hmiBackupDir = Path.Combine(Path.GetDirectoryName(_configPath), "block-backups", "hmi-text-lists");
                        Directory.CreateDirectory(hmiBackupDir);
                        backupFile = Path.Combine(hmiBackupDir, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + PathTools.SafeName(tlName) + ".xml");
                        object exportOpts = session.Api.EnumValue("Siemens.Engineering.ExportOptions", "WithDefaults");
                        session.Api.Invoke(existing, "Export", new FileInfo(backupFile), exportOpts);
                    }

                    string tempFile = Path.Combine(Path.GetTempPath(), "tia-hmi-tl-" + Guid.NewGuid().ToString("N") + ".xml");
                    try
                    {
                        string normalizedXml = xml.Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"")
                            .Replace("encoding='utf-16'", "encoding='utf-8'");
                        File.WriteAllText(tempFile, normalizedXml, new UTF8Encoding(true));
                        object importOpts = session.Api.EnumValue("Siemens.Engineering.ImportOptions", "Override");
                        session.Api.Invoke(textLists, "Import", new FileInfo(tempFile), importOpts);
                    }
                    finally
                    {
                        if (File.Exists(tempFile)) File.Delete(tempFile);
                    }

                    return ToolResult("HMI 文本列表导入完成（" + tlName + "），尚未保存。" + (string.IsNullOrEmpty(backupFile) ? "" : " 备份：" + backupFile),
                        false,
                        Dict("saved", false, "hmi", hmi.DisplayName, "name", tlName, "backup", backupFile));
                }

                if (name == "tia_save_project")
                {
                    RequireConfirmation(args, "保存会把当前博途项目中的未保存修改写入磁盘");
                    session.SaveProject();
                    return ToolResult("项目已保存：" + session.GetProjectPath(), false,
                        Dict("project_path", session.GetProjectPath(), "saved", true));
                }

                if (name == "tia_simulation_download")
                {
                    return SimulationTools.Download(session, args);
                }
                PlcContext plc = session.SelectPlc(config.PlcName);
                BlockOperations ops = new BlockOperations(session, plc);
                string backupRoot = Path.Combine(Path.GetDirectoryName(_configPath), "block-backups");
                string unitName = StringArg(args, "unit_name");
                bool stripDocInfo = BoolArg(args, "strip_document_info", false);

                if (name == "tia_list_software_units")
                {
                    Dictionary<string, object> unitsResult = SoftwareUnitHelper.ListSoftwareUnits(session, plc.DisplayName);
                    return ToolResult("PLC Software Units（共 " + unitsResult["unit_count"] + " 个）", false, unitsResult);
                }
                if (name == "tia_query_cross_references")
                {
                    int maxResults = IntArg(args, "max_results", 200);
                    Dictionary<string, object> xrefResult = CrossReferenceOperations.Query(
                        session,
                        plc.DisplayName,
                        StringArg(args, "scope_kind"),
                        StringArg(args, "target_name"),
                        StringArg(args, "group_path"),
                        unitName,
                        StringArg(args, "filter"),
                        StringArg(args, "symbol_filter"),
                        StringArg(args, "access_filter"),
                        maxResults);
                    return ToolResult("V21 官方交叉引用查询完成（返回 " + xrefResult["returned_count"] + " 项）", false, xrefResult);
                }
                if (name == "tia_read_document")
                {
                    string objName = StringArg(args, "block_name");
                    if (objName.Length == 0) objName = StringArg(args, "object_name");
                    Dictionary<string, object> docResult = SimaticSdOperations.ReadDocument(
                        session,
                        plc.DisplayName,
                        objName,
                        StringArg(args, "group_path"),
                        unitName,
                        StringArg(args, "kind"),
                        BoolArg(args, "include_resources", true));
                    return ToolResult(Convert.ToString(docResult["s7dcl"]), false, docResult);
                }
                if (name == "tia_read_documents_batch")
                {
                    object[] rawItems = _json.DeserializeObject(StringArg(args, "items_json")) as object[];
                    Dictionary<string, object> batchDocs = SimaticSdOperations.ReadDocumentsBatch(
                        session,
                        plc.DisplayName,
                        rawItems,
                        BoolArg(args, "include_resources", true));
                    bool anyFailed = Convert.ToInt32(batchDocs["failed_count"]) > 0;
                    return ToolResult("SimaticSD 批量读取完成：成功 " + batchDocs["succeeded_count"] + "，失败 " + batchDocs["failed_count"], anyFailed, batchDocs);
                }
                if (name == "tia_import_document")
                {
                    RequireConfirmation(args, "导入 SimaticSD 文档 (.s7dcl/.s7res) 会修改离线项目");
                    string objName = StringArg(args, "block_name");
                    if (objName.Length == 0) objName = StringArg(args, "object_name");
                    Dictionary<string, object> extraRes = null;
                    string resJson = StringArg(args, "resources_json");
                    if (!string.IsNullOrWhiteSpace(resJson))
                        extraRes = _json.DeserializeObject(resJson) as Dictionary<string, object>;
                    Dictionary<string, object> impDoc = SimaticSdOperations.ImportDocument(
                        session,
                        backupRoot,
                        plc.DisplayName,
                        objName,
                        StringArg(args, "s7dcl"),
                        StringArg(args, "s7res"),
                        extraRes,
                        StringArg(args, "group_path"),
                        unitName,
                        StringArg(args, "kind"),
                        StringArg(args, "import_options"),
                        StringArg(args, "compile_mode"));
                    bool compileErr = impDoc["compile_errors"] != null && Convert.ToInt32(impDoc["compile_errors"]) > 0;
                    string report = Convert.ToString(impDoc["compile_report"]);
                    return ToolResult("SimaticSD 文档事务导入完成（" + Convert.ToString(impDoc["name"]) + "），尚未保存。"
                        + (string.IsNullOrEmpty(report) ? "" : Environment.NewLine + report), compileErr, impDoc);
                }
                if (name == "tia_import_documents_batch")
                {
                    RequireConfirmation(args, "批量导入 SimaticSD 文档会在单事务中修改离线项目");
                    object[] rawItems = _json.DeserializeObject(StringArg(args, "items_json")) as object[];
                    Dictionary<string, object> batchImp = SimaticSdOperations.ImportDocumentsBatch(
                        session,
                        backupRoot,
                        plc.DisplayName,
                        rawItems,
                        StringArg(args, "compile_mode"));
                    bool compileErr = batchImp["compile_errors"] != null && Convert.ToInt32(batchImp["compile_errors"]) > 0;
                    string report = Convert.ToString(batchImp["compile_report"]);
                    return ToolResult("SimaticSD 批量事务导入完成（共 " + batchImp["imported_count"] + " 项），尚未保存。"
                        + (string.IsNullOrEmpty(report) ? "" : Environment.NewLine + report), compileErr, batchImp);
                }
                if (name == "tia_import_blocks_batch")
                {
                    RequireConfirmation(args, "批量导入 SimaticML XML 会在单事务中修改离线项目");
                    bool createGroup = BoolArg(args, "create_group_if_missing", true);
                    string compileMode = NormalizeCompileMode(StringArg(args, "compile_mode"));
                    List<object> importedObjects;
                    BatchImportResult xmlBatch = ops.ImportBlocksBatch(
                        StringArg(args, "items_json"),
                        backupRoot,
                        createGroup,
                        StringArg(args, "sw_import_options"),
                        out importedObjects);
                    if (compileMode == "block" && importedObjects.Count == 1)
                        return FinishBatchImport(xmlBatch, "plc", () => session.CompileTarget(importedObjects[0], plc));
                    return FinishBatchImport(xmlBatch, compileMode, () => session.CompilePlc(plc));
                }

                if (name == "tia_list_blocks")
                {
                    object[] blocks = ops.List(
                        StringArg(args, "group_filter"),
                        StringArg(args, "name_filter"),
                        StringArg(args, "language_filter"),
                        BoolArg(args, "refresh", false),
                        StringArg(args, "scope_path"),
                        unitName).ToArray();
                    return ToolResult("程序块列表（共 " + blocks.Length + " 个）", false, Dict("blocks", blocks));
                }
                if (name == "tia_list_types")
                {
                    object[] types = ops.ListTypes(
                        StringArg(args, "group_filter"),
                        StringArg(args, "name_filter"),
                        BoolArg(args, "refresh", false),
                        StringArg(args, "scope_path"),
                        unitName).ToArray();
                    return ToolResult("PLC 数据类型 (UDT/NVT) 列表（共 " + types.Length + " 个）", false, Dict("types", types));
                }
                if (name == "tia_list_tag_tables")
                {
                    object[] tables = ops.ListTagTables(unitName).ToArray();
                    return ToolResult("PLC 变量表列表（共 " + tables.Length + " 个）", false, Dict("tag_tables", tables));
                }
                if (name == "tia_read_tag_table")
                {
                    string xml = ops.ReadTagTable(StringArg(args, "table_name"), StringArg(args, "group_path"), unitName, stripDocInfo);
                    return ToolResult(xml, false, Dict("xml", xml));
                }
                if (name == "tia_import_tag_table")
                {
                    RequireConfirmation(args, "导入 PLC 变量表会修改离线项目");
                    bool createGroup = BoolArg(args, "create_group_if_missing", true);
                    string backup = ops.ImportTagTable(StringArg(args, "xml"), StringArg(args, "group_path"), backupRoot, createGroup, unitName);
                    return ToolResult("PLC 变量表导入完成，尚未保存。备份：" + backup, false, Dict("saved", false, "backup", backup));
                }
                if (name == "tia_list_block_groups")
                    return ToolResult("程序块组（含空组）", false, Dict("groups", ops.ListGroups(unitName)));
                if (name == "tia_organize_block_groups")
                {
                    RequireConfirmation(args, "组整理会重命名和创建离线程序块组，不修改块内容");
                    object result = ops.OrganizeGroups(StringArg(args, "parent_path"),
                        StringArg(args, "rename_json"), StringArg(args, "group_names_json"), Path.Combine(Path.GetDirectoryName(_configPath), "group-backups"), unitName);
                    return ToolResult("程序块组整理完成；未保存、未改块内容、未下载。", false, result);
                }
                if (name == "tia_read_block")
                {
                    string xml = ops.Read(StringArg(args, "block_name"), StringArg(args, "group_path"), unitName, stripDocInfo);
                    return ToolResult(xml, false, Dict("xml", xml));
                }
                if (name == "tia_read_source")
                {
                    string ext;
                    string resolvedGroup;
                    string source = ops.ReadSource(StringArg(args, "block_name"), StringArg(args, "group_path"), out ext, out resolvedGroup, unitName);
                    return ToolResult(source, false, Dict("source", source, "extension", ext, "group_path", resolvedGroup, "unit_name", unitName));
                }
                if (name == "tia_read_sources_batch")
                {
                    string[] names = StringArrayArg(args, "block_names");
                    object[] items = ops.ReadSourcesBatch(names, StringArg(args, "group_path"), unitName);
                    bool failed = items.Any(item => !(bool)item.GetType().GetProperty("ok").GetValue(item, null));
                    return ToolResult("批量读取完成（共 " + items.Length + " 项）", failed, Dict("items", items));
                }
                if (name == "tia_read_graph")
                {
                    string xml = ops.Read(StringArg(args, "block_name"), StringArg(args, "group_path"), unitName, false);
                    XmlDocument doc;
                    object graph;
                    using (OperationMetrics.Measure("graph_parse")) { doc = GraphEditor.Parse(xml); graph = GraphEditor.Inspect(doc, BoolArg(args, "include_logic", true)); }
                    bool includeRawXml = BoolArg(args, "include_raw_xml", true);
                    Dictionary<string, object> payload = includeRawXml
                        ? Dict("xml", xml, "graph", graph, "graph_hash", GraphEditor.Fingerprint(doc))
                        : Dict("graph", graph, "graph_hash", GraphEditor.Fingerprint(doc));
                    return ToolResult("GRAPH 步、转换条件、动作和连线", false, payload);
                }
                if (name == "tia_read_graph_model")
                {
                    string xml = ops.Read(StringArg(args, "block_name"), StringArg(args, "group_path"), unitName, false);
                    XmlDocument doc; object model;
                    using (OperationMetrics.Measure("graph_parse")) { doc = GraphEditor.Parse(xml); model = GraphBuilder.ReadModel(doc); }
                    return ToolResult("GRAPH 完整可编辑模型。", false, BoolArg(args, "include_base_xml", true) ? Dict("model", model, "base_xml", xml, "graph_hash", GraphEditor.Fingerprint(doc)) : Dict("model", model, "graph_hash", GraphEditor.Fingerprint(doc)));
                }
                if (name == "tia_update_graph")
                {
                    bool dryRun = BoolArg(args, "dry_run", true);
                    if (!dryRun) RequireConfirmation(args, "GRAPH 通用修改会写入离线工程");
                    string backupMode = NormalizeBackupMode(StringArg(args, "backup_mode"));
                    string mode = NormalizeCompileMode(StringArg(args, "compile_mode"));
                    GraphBlockSnapshot snapshot = ops.ReadGraphSnapshot(StringArg(args, "block_name"), StringArg(args, "group_path"), unitName);
                    XmlDocument doc;
                    using (OperationMetrics.Measure("graph_parse")) doc = GraphEditor.Parse(snapshot.Xml);
                    string beforeHash = GraphEditor.Fingerprint(doc), expected = StringArg(args, "expected_hash");
                    if (expected.Length > 0 && !string.Equals(beforeHash, expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("GRAPH 已发生变化，expected_hash 不匹配；未写入。");
                    using (OperationMetrics.Measure("graph_patch")) GraphEditor.Patch(doc, StringArg(args, "patches_json"));
                    string afterHash = GraphEditor.Fingerprint(doc);
                    bool changed = beforeHash != afterHash;
                    object imported = snapshot.Entry.Item;
                    string backup = "";
                    Dictionary<string, object> result;
                    if (!dryRun && (changed || !BoolArg(args, "skip_unchanged", true)))
                    {
                        backup = ops.ImportGraphSnapshot(snapshot, doc, backupRoot, backupMode == "skip", out imported);
                        result = CompileAfterImport(session, plc, imported, backup, "GRAPH 修改已写入，尚未保存。", mode);
                    }
                    else result = ToolResult(dryRun ? "GRAPH 修改预览，未写入。" : "GRAPH 内容未变，已跳过导入与编译。", false, Dict("saved", false, "compiled", false));
                    var data = (Dictionary<string, object>)result["structuredContent"];
                    data["dry_run"] = dryRun; data["changed"] = changed; data["imported"] = !dryRun && (changed || !BoolArg(args, "skip_unchanged", true));
                    data["backup_mode"] = backupMode; data["before_hash"] = beforeHash; data["after_hash"] = afterHash;
                    data["graph"] = GraphEditor.Inspect(doc, false);
                    if (BoolArg(args, "include_xml", false)) data["xml"] = doc.OuterXml;
                    return result;
                }
                if (name == "tia_write_graph")
                {
                    RequireConfirmation(args, "新建或完整修改 GRAPH 会修改离线工程");
                    string mode = NormalizeCompileMode(StringArg(args, "compile_mode"));
                    bool skipBackup = NormalizeBackupMode(StringArg(args, "backup_mode")) == "skip";
                    string baseXml = StringArg(args, "base_xml");
                    if (string.IsNullOrWhiteSpace(baseXml))
                    {
                        var model = _json.Deserialize<Dictionary<string, object>>(StringArg(args, "model_json"));
                        string blockName = model == null ? "" : StringArg(model, "block_name");
                        var existing = ops.GetCachedBlocks(true).Where(e => string.Equals(e.Name, blockName, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(e.UnitName ?? "", unitName, StringComparison.OrdinalIgnoreCase)).ToArray();
                        if (existing.Length > 1) throw new InvalidOperationException("存在多个同名块，拒绝自动选择 GRAPH 基础块。");
                        if (existing.Length == 1) baseXml = ops.Read(blockName, existing[0].GroupPath, unitName, false);
                    }
                    string xml;
                    using (OperationMetrics.Measure("graph_generate")) xml = GraphBuilder.Generate(StringArg(args, "model_json"), baseXml, _loadedVersion, Path.GetDirectoryName(_configPath));
                    object imported;
                    string backup = ops.Import(xml, StringArg(args, "group_path"), backupRoot, BoolArg(args, "create_group_if_missing", true), out imported, skipBackup, unitName, StringArg(args, "sw_import_options"));
                    return CompileAfterImport(session, plc, imported, backup, "GRAPH 完整流程已写入，尚未保存。", mode);
                }
                if (name == "tia_import_block")
                {
                    RequireConfirmation(args, "单块导入会修改离线项目");
                    bool createGroup = BoolArg(args, "create_group_if_missing", true);
                    string compileMode = NormalizeCompileMode(StringArg(args, "compile_mode"));
                    bool skipBackup = NormalizeBackupMode(StringArg(args, "backup_mode")) == "skip";
                    object importedBlock;
                    string backup = ops.Import(StringArg(args, "xml"),
                        StringArg(args, "group_path"), backupRoot, createGroup, out importedBlock, skipBackup, unitName, StringArg(args, "sw_import_options"));
                    return CompileAfterImport(session, plc, importedBlock, backup, "单块 XML 导入完成，尚未保存。", compileMode);
                }
                if (name == "tia_import_source")
                {
                    RequireConfirmation(args, "单块源码导入会修改离线项目");
                    bool createGroup = BoolArg(args, "create_group_if_missing", true);
                    string compileMode = NormalizeCompileMode(StringArg(args, "compile_mode"));
                    object importedBlock;
                    string backup = ops.ImportSource(
                        StringArg(args, "source"),
                        StringArg(args, "format"),
                        StringArg(args, "block_name"),
                        StringArg(args, "group_path"),
                        backupRoot,
                        createGroup,
                        out importedBlock,
                        true,
                        unitName);
                    return CompileAfterImport(session, plc, importedBlock, backup, "单块文本源码导入完成，尚未保存。", compileMode);
                }
                if (name == "tia_import_sources_batch")
                {
                    RequireConfirmation(args, "批量源码导入会修改离线项目");
                    bool createGroup = BoolArg(args, "create_group_if_missing", true);
                    string compileMode = NormalizeCompileMode(StringArg(args, "compile_mode"));
                    BatchImportResult batchResult = ops.ImportSourcesBatch(StringArg(args, "items_json"), backupRoot, createGroup);
                    return FinishBatchImport(batchResult, compileMode, () => session.CompilePlc(plc));
                }
                if (name == "tia_restore_backup")
                {
                    RequireConfirmation(args, "还原备份会覆盖离线工程中的对应程序块");
                    string compileMode = NormalizeCompileMode(StringArg(args, "compile_mode"));
                    string backup = ops.RestoreBackup(StringArg(args, "backup_path"), StringArg(args, "group_path"), backupRoot, unitName);
                    return CompileAfterImport(session, plc, null, backup, "已从备份还原 PLC 对象，尚未保存。", compileMode);
                }
                if (name == "tia_delete_block")
                {
                    RequireConfirmation(args, "删除块或 UDT 会修改离线项目（删除前自动导出 XML 备份）");
                    string backup = ops.DeleteBlock(StringArg(args, "block_name"), StringArg(args, "group_path"), backupRoot, unitName);
                    return ToolResult("对象已删除（未保存）。已生成删除前备份：" + backup, false, Dict("saved", false, "backup", backup));
                }
                if (name == "tia_export_plc_sources")
                {
                    ExportResult result = new PlcExporter(config, session.Api).Export(plc);
                    foreach (HmiContext hmi in session.GetHmis())
                    {
                        ExportResult hmiResult = new HmiExporter(config, session.Api).Export(hmi);
                        result.SuccessCount += hmiResult.SuccessCount;
                        result.FailureCount += hmiResult.FailureCount;
                    }
                    return ToolResult("PLC/HMI 导出完成：成功 " + result.SuccessCount + "，失败 " + result.FailureCount
                        + "。目录：" + config.SourceDirectory,
                        result.FailureCount > 0,
                        Dict("success_count", result.SuccessCount, "failure_count", result.FailureCount,
                            "source_directory", config.SourceDirectory, "plc", plc.DisplayName));
                }
                if (name == "tia_compile_plc")
                {
                    CompileSummary summary = session.CompilePlc(plc);
                    return ToolResult(summary.ToReport(), summary.ErrorCount > 0,
                        Dict("state", summary.State, "error_count", summary.ErrorCount,
                            "warning_count", summary.WarningCount, "plc", plc.DisplayName));
                }
                if (name == "tia_import_plc_sources")
                {
                    RequireConfirmation(args, "导入源码会修改当前已打开的博途项目");
                    config.SaveProjectAfterImport = false;
                    ImportResult result = new PlcImporter(config, session).Import(plc);
                    plc.InvalidateBlockCache();
                    plc.InvalidateTypeCache();
                    foreach (HmiContext hmi in session.GetHmis())
                    {
                        ImportResult hmiResult = new HmiImporter(config, session.Api).Import(hmi);
                        result.SuccessCount += hmiResult.SuccessCount;
                        result.FailureCount += hmiResult.FailureCount;
                    }
                    return ToolResult("PLC/HMI 导入完成但尚未保存：成功 " + result.SuccessCount + "，失败 "
                        + result.FailureCount + "。请检查结果，再决定是否调用 tia_save_project。"
                        + (string.IsNullOrWhiteSpace(result.ErrorLogPath) ? "" : " 失败日志：" + result.ErrorLogPath),
                        result.FailureCount > 0,
                        Dict("success_count", result.SuccessCount, "failure_count", result.FailureCount,
                            "saved", false, "error_log", result.ErrorLogPath ?? ""));
                }
            }
            throw new InvalidOperationException("未知工具：" + name);
        }

        private Dictionary<string, object> CompileAfterImport(OpennessSession session, PlcContext plc, object importedTarget, string backup, string prefixMessage, string compileMode)
        {
            string mode = NormalizeCompileMode(compileMode);
            if (mode == "none")
            {
                return ToolResult(prefixMessage + "（已跳过编译）备份：" + backup,
                    false,
                    Dict("saved", false, "backup", backup, "compiled", false, "error_count", 0, "warning_count", 0));
            }

            CompileSummary summary;
            try
            {
                summary = (mode == "block" && importedTarget != null)
                    ? session.CompileTarget(importedTarget, plc)
                    : session.CompilePlc(plc);
            }
            catch (Exception ex)
            {
                return ToolResult("导入已执行但编译失败；未保存。备份：" + backup + "；" + RootMessage(ex), true,
                    Dict("saved", false, "backup", backup));
            }
            return ToolResult(prefixMessage + "备份：" + backup + Environment.NewLine + summary.ToReport(),
                summary.ErrorCount > 0,
                Dict("saved", false, "backup", backup, "compiled", true, "compile_mode", mode, "error_count", summary.ErrorCount, "warning_count", summary.WarningCount));
        }

        private static string NormalizeCompileMode(string value)
        {
            string mode = string.IsNullOrWhiteSpace(value) ? "plc" : value.Trim().ToLowerInvariant();
            if (mode != "plc" && mode != "block" && mode != "none")
                throw new ArgumentException("compile_mode 仅支持 plc、block 或 none。");
            return mode;
        }

        private static string NormalizeBackupMode(string value)
        {
            string mode = string.IsNullOrWhiteSpace(value) ? "required" : value.Trim().ToLowerInvariant();
            if (mode != "required" && mode != "skip") throw new ArgumentException("backup_mode 仅支持 required 或 skip。");
            return mode;
        }

        internal Dictionary<string, object> FinishBatchImport(BatchImportResult batch, string mode, Func<CompileSummary> compile)
        {
            bool importFailed = batch.failure_count > 0;
            string message = "批量导入：成功 " + batch.success_count + "，失败 " + batch.failure_count + "；尚未保存。";
            if (mode == "none" || batch.success_count == 0)
                return ToolResult(message + "已跳过编译。", importFailed,
                    Dict("saved", false, "batch", batch, "compiled", false));
            try
            {
                CompileSummary summary = compile();
                return ToolResult(message + Environment.NewLine + summary.ToReport(), importFailed || summary.ErrorCount > 0,
                    Dict("saved", false, "batch", batch, "compiled", true, "compile_mode", "plc",
                        "error_count", summary.ErrorCount, "warning_count", summary.WarningCount));
            }
            catch (Exception ex)
            {
                return ToolResult(message + "编译未完成：" + RootMessage(ex), true,
                    Dict("saved", false, "batch", batch, "compiled", false, "compile_error", RootMessage(ex)));
            }
        }

        private static object FindHmiTextList(ReflectionApi api, object textLists, string name)
        {
            try
            {
                object direct = api.Invoke(textLists, "Find", name);
                if (direct != null) return direct;
            }
            catch { }
            foreach (object item in api.Items(textLists))
            {
                if (string.Equals(api.GetString(item, "Name"), name, StringComparison.OrdinalIgnoreCase))
                    return item;
            }
            return null;
        }

        private static string ExportHmiObjectXml(ReflectionApi api, object item)
        {
            string tempFile = Path.Combine(Path.GetTempPath(), "tia-hmi-read-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                object exportOpts = api.EnumValue("Siemens.Engineering.ExportOptions", "WithDefaults");
                api.Invoke(item, "Export", new FileInfo(tempFile), exportOpts);
                return File.ReadAllText(tempFile, Encoding.UTF8);
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        private AppConfig NewDiscoveryConfig()
        {
            if (File.Exists(_configPath))
            {
                DateTime lastWrite = File.GetLastWriteTimeUtc(_configPath);
                if (_cachedConfigJson == null || lastWrite != _cachedConfigTimeUtc)
                {
                    _cachedConfigJson = File.ReadAllText(_configPath, Encoding.UTF8);
                    _cachedConfigTimeUtc = lastWrite;
                }
                AppConfig loaded = _json.Deserialize<AppConfig>(_cachedConfigJson);
                loaded.ConfigFilePath = _configPath;
                return loaded;
            }
            AppConfig fallback = new AppConfig();
            fallback.ConfigFilePath = _configPath;
            return fallback;
        }

        private string GenerationVersion(Dictionary<string, object> args)
        {
            if (_projects != null) return _projects.GenerationVersion(StringArg(args, "project_path"));
            if (!string.IsNullOrWhiteSpace(StringArg(args, "project_path")))
            {
                AppConfig config = NewDiscoveryConfig(); config.OverrideProject(StringArg(args, "project_path"), false); return config.TiaPortalVersion;
            }
            return _loadedVersion ?? NewDiscoveryConfig().TiaPortalVersion;
        }

        private OpennessSession Connect(AppConfig config)
        {
            if (_loadedVersion != null && !string.Equals(_loadedVersion, config.TiaPortalVersion, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("当前服务已加载 TIA " + _loadedVersion + " API，切换至 " + config.TiaPortalVersion + " 需要重启服务窗口以避免混用 Siemens DLL。");
            if (_session != null && !_session.IsAlive())
            {
                Console.Error.WriteLine("[自动恢复] 检测到之前的 TIA Portal 会话已失效，正在清理并重连……");
                ResetSessionOnly();
            }
            if (_session != null && string.Equals(_sessionPath, config.ProjectPath, StringComparison.OrdinalIgnoreCase))
                return _session;
            if (_session != null)
                throw new InvalidOperationException("已连接其他工程 (" + _sessionPath + ")；请先调用 tia_disconnect，再连接新项目。");
            _session = OpennessSession.Open(config);
            _sessionPath = config.ProjectPath;
            _loadedVersion = _session.Api.LoadedPortalVersion;
            return _session;
        }

        public void ConfigureInitialProject(string projectPath)
        {
            if (_projects != null)
            {
                _projects.ConfigureInitialProject(projectPath);
                return;
            }
            Connect(NewProjectConfig(ResolveProjectPath(projectPath), "", ""));
        }

        private void ResetSessionOnly()
        {
            if (_session != null)
            {
                try { _session.Dispose(); }
                catch { }
            }
            _session = null;
            _sessionPath = null;
        }

        public void Dispose()
        {
            if (_projects != null) _projects.Dispose();
            _online.Dispose();
            ResetSessionOnly();
        }

        private AppConfig NewProjectConfig(string projectPath, string plcName, string sourceDirectory)
        {
            AppConfig config = NewDiscoveryConfig();
            config.ConnectionMode = "AttachOnly";
            config.WithUserInterface = true;
            config.PlcName = plcName ?? "";
            config.SaveProjectAfterImport = false;
            config.AllowProjectUpgrade = false;
            config.OverrideProject(projectPath, true);
            if (!string.IsNullOrWhiteSpace(sourceDirectory))
            {
                config.SourceDirectory = Path.GetFullPath(Environment.ExpandEnvironmentVariables(sourceDirectory));
            }
            return config;
        }

        private string ResolveProjectPath(string requested)
        {
            if (!string.IsNullOrWhiteSpace(requested))
                return Path.GetFullPath(Environment.ExpandEnvironmentVariables(requested));
            if (_session != null && _session.IsAlive()) return _sessionPath;

            AppConfig discoveryConfig = NewDiscoveryConfig();
            if (_loadedVersion != null) discoveryConfig.TiaPortalVersion = _loadedVersion;
            IList<PortalProcessInfo> processes = PortalDiscovery.List(discoveryConfig);
            string[] projects = processes.Select(p => p.ProjectPath).Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (projects.Length == 1) return projects[0];
            if (projects.Length == 0)
                throw new InvalidOperationException("没有检测到已打开的博途项目，请先在 TIA Portal 中打开工程，或在调用时传入 project_path。");
            throw new InvalidOperationException("检测到多个已打开项目，请传入 project_path：" + string.Join("；", projects));
        }

        private static void RequireConfirmation(Dictionary<string, object> args, string action)
        {
            object value = Value(args, "confirm");
            if (!(value is bool) || !(bool)value)
            {
                throw new InvalidOperationException(action + "。确认后请把 confirm 设为 true 再调用。");
            }
        }

        private static bool BoolArg(Dictionary<string, object> args, string name, bool defaultValue)
        {
            object value = Value(args, name);
            if (value == null) return defaultValue;
            if (value is bool) return (bool)value;
            bool parsed;
            return bool.TryParse(Convert.ToString(value), out parsed) ? parsed : defaultValue;
        }

        private static int IntArg(Dictionary<string, object> args, string name, int defaultValue)
        {
            object value = Value(args, name);
            if (value == null) return defaultValue;
            if (value is int) return (int)value;
            int parsed;
            return int.TryParse(Convert.ToString(value), out parsed) ? parsed : defaultValue;
        }

        private static string[] StringArrayArg(Dictionary<string, object> args, string name)
        {
            object raw = Value(args, name);
            IEnumerable enumerable = raw as IEnumerable;
            if (enumerable == null || raw is string)
                throw new ArgumentException(name + " 必须是字符串数组。");
            return enumerable.Cast<object>().Select(x => Convert.ToString(x ?? "").Trim()).Where(s => s.Length > 0).ToArray();
        }

        private static string GetToolProfile(string name)
        {
            if (name == "tia_list_process_library" || name == "tia_read_process_library" || name == "tia_render_process_template")
                return "process_library";
            if (name == "tia_connection_status" || name == "tia_list_connections" || name == "tia_disconnect_all"
                || name == "tia_disconnect" || name == "tia_connect" || name == "tia_list_open_projects"
                || name == "tia_list_plcs" || name == "tia_save_project")
                return "core";
            if (name == "tia_read_document" || name == "tia_read_documents_batch"
                || name == "tia_import_document" || name == "tia_import_documents_batch")
                return "simaticsd";
            if (name == "tia_list_software_units")
                return "units";
            if (name == "tia_query_cross_references")
                return "xref";
            if (name == "tia_generate_graph" || name == "tia_read_graph_model" || name == "tia_update_graph"
                || name == "tia_write_graph" || name == "tia_read_graph" || name == "tia_patch_graph")
                return "graph";
            if (name == "tia_read_unified_hmi" || name == "tia_compile_unified_hmi")
                return "unified";
            if (name == "tia_generate_hmi_configuration" || name == "tia_read_hmi_configuration" || name == "tia_write_hmi_configuration"
                || name == "tia_compile_hmi" || name == "tia_list_hmi_text_lists" || name == "tia_read_hmi_text_list"
                || name == "tia_import_hmi_text_list" || IsAlarmTool(name))
                return "hmi";
            if (name == "tia_read_hardware_configuration" || name == "tia_apply_hardware_configuration"
                || name == "tia_compile_hardware_configuration" || name == "tia_search_hardware_catalog")
                return "hardware";
            if (name == "tia_export_aml" || name == "tia_import_aml")
                return "cax";
            if (name.StartsWith("tia_simulation_", StringComparison.Ordinal))
                return "simulation";
            if (name.StartsWith("tia_online_", StringComparison.Ordinal))
                return "online";
            if (IsIndexTool(name))
                return "index";
            return "plc";
        }

        private object[] ToolDefinitions()
        {
            Dictionary<string, object> project = StringProperty("TIA Portal V21 项目文件完整路径（.ap21 或多用户本地会话 .als21）。只管理一个项目时可省略；管理多个项目时必须明确指定，禁止默认切换目标。");
            Dictionary<string, object> plc = StringProperty("PLC 名称。不填时，仅在项目中恰好一个 PLC 时自动选择。");
            Dictionary<string, object> source = StringProperty("源码目录完整路径。不填时使用项目旁的 <项目名>_PLC_Source。");
            Dictionary<string, object> unitProp = StringProperty("可选：TIA V21 Software Unit（软件单元）名称。留空表示访问 PLC 根程序区。");
            Dictionary<string, object> stripDocProp = BooleanProperty("可选：设为 true 时启用 V21 DocumentInfoOptions.None，剥离时间戳头部噪音以获得更小、更适合对比的纯净 XML（默认 false）。");
            Dictionary<string, object> swImportOptProp = StringProperty("可选：V21 SWImportOptions 枚举组合（逗号分隔，如 IgnoreStructuralChanges, IgnoreMissingReferencedObjects, IgnoreUnitAttributes）。");
            Dictionary<string, object> compileModeProp = Dict("type", "string", "enum", new[] { "plc", "block", "none" },
                "description", "编译策略：plc（默认，编译整个PLC）、block（极速，仅编译当前导入块）、none（跳过编译，适合多块连续导入中间步骤）。");
            Dictionary<string, object> backupModeProp = Dict("type", "string", "enum", new[] { "required", "skip" },
                "description", "备份策略：required（默认，备份失败则停止写入）；skip（明确跳过备份）。");
            object[] allTools = new object[]
            {
                Tool("tia_list_process_library", "检索本地 V21 工艺模板与知识库目录，不连接博途。按 id 读取详情，再渲染模板。",
                    ObjectSchema(Property("kind", StringProperty("all/template/knowledge；默认 all")), Property("query", StringProperty("目录关键词，空格分隔；例如 SimaticSD、SCL、FB")),
                        Property("offset", Dict("type", "integer", "minimum", 0)), Property("limit", Dict("type", "integer", "minimum", 1, "maximum", 100))), true, false, true),
                Tool("tia_read_process_library", "按 id 读取单条模板或知识：含参数、依赖、来源哈希和适用范围。",
                    ObjectSchema(new[] { "id" }, Property("id", StringProperty("目录返回的精确 id"))), true, false, true),
                Tool("tia_render_process_template", "参数化生成工艺源码草稿。缺参、未知参数或格式错误时拒绝生成。不调用博途，不导入、不编译。",
                    ObjectSchema(new[] { "id", "parameters_json" }, Property("id", StringProperty("template 条目 id")), Property("parameters_json", StringProperty("参数对象 JSON；先读取模板的 parameters、dependencies 和 review_notes"))), true, false, true),
                Tool("tia_connection_status", "查看全部或指定项目的连接、后台进程及超时延迟结果。不会自动连接新项目。", ObjectSchema(Property("project_path", project)), true, false, true),
                Tool("tia_list_connections", "列出当前管理的所有项目连接、版本和独立后台进程状态。", ObjectSchema(), true, false, true),
                Tool("tia_disconnect_all", "释放全部项目连接。不保存、不关闭用户工程；仍有请求执行时拒绝断开。", ObjectSchema(), false, false, true),
                Tool("tia_connect", "连接已打开的 .ap21/.als21 工程并保持连接；可重复连接不同项目，原连接保留。", ObjectSchema(Property("project_path", project)), true, false, true),
                Tool("tia_read_hardware_configuration", "批量读取 PLC/设备/模块、I/O 地址、子网、网络节点、IO 系统和物理端口的路径及可读写属性。include_attributes=false 可快速列出结构。",
                    ObjectSchema(Property("project_path", project), Property("path_prefix", StringProperty("可选：按实际硬件路径前缀筛选")), Property("include_attributes", BooleanProperty("默认 true"))), true, false, true),
                Tool("tia_apply_hardware_configuration", "在 Openness V21 事务中执行硬件/网络批量构建计划（异常自动回滚）。支持 create_device/plug_module/create_subnet/set_attributes/connect_subnet/create_io_system/connect_io_system/connect_ports。默认 dry_run=true；执行须 dry_run=false 且 confirm=true。不保存、不下载。",
                    ObjectSchema(new[] { "plan_json" }, Property("project_path", project), Property("plan_json", StringProperty("硬件组态操作数组 JSON，详见 CAPABILITIES.md")),
                        Property("dry_run", BooleanProperty("默认 true；false 才执行")), Property("confirm", BooleanProperty("确认修改离线组态"))), false, true, false),
                Tool("tia_compile_hardware_configuration", "通过目标设备提供的 ICompilable 服务编译硬件组态并返回诊断。不保存。",
                    ObjectSchema(new[] { "target_path" }, Property("project_path", project), Property("target_path", StringProperty("实际设备路径，如 /devices/Station"))), true, false, true),
                Tool("tia_search_hardware_catalog", "查询 TIA Portal V21 官方 HardwareCatalog.Find 硬件目录，获取可用于 create_device/plug_module 的精确 TypeIdentifier 与订货号（ArticleNumber）。",
                    ObjectSchema(new[] { "filter" }, Property("project_path", project),
                        Property("filter", StringProperty("搜索关键词或订货号，例如 'CPU 1516'、'6ES7 515'、'1214C'")),
                        Property("catalog_path", StringProperty("可选目录路径过滤")),
                        Property("limit", Dict("type", "integer", "minimum", 1, "maximum", 500, "description", "返回条数上限，默认 50"))), true, false, true),
                Tool("tia_export_aml", "通过 TIA Portal V21 CaxProvider 将整项目或指定设备的硬件与网络拓扑导出为标准 AutomationML (.aml) 文件。只读不改工程。",
                    ObjectSchema(new[] { "aml_path" }, Property("project_path", project),
                        Property("aml_path", StringProperty("导出 .aml 文件完整保存路径")),
                        Property("device_name", StringProperty("可选：仅导出指定设备名称；留空导出整个项目"))), false, false, true),
                Tool("tia_import_aml", "在 Openness V21 事务中通过 CaxProvider 导入 AutomationML (.aml) 硬件与网络拓扑（失败自动回滚）。不保存、不下载；必须 confirm=true。",
                    ObjectSchema(new[] { "aml_path", "confirm" }, Property("project_path", project),
                        Property("aml_path", StringProperty("待导入的 .aml 文件完整路径")),
                        Property("import_option", Dict("type", "string", "enum", new[] { "RetainTiaDevice", "OverwriteTiaDevice", "MoveToParkingLot" }, "description", "冲突处理策略，默认 RetainTiaDevice")),
                        Property("confirm", BooleanProperty("确认修改离线硬件与网络组态"))), false, true, false),
                Tool("tia_read_unified_hmi", "无头（免 UI 焦点）读取 TIA Portal V21 WinCC Unified (HmiSoftware) 组态：支持 summary/screens/tags/tag_tables/discrete_alarms/analog_alarms/alarm_classes/connections/scripts/text_lists。",
                    ObjectSchema(Property("project_path", project),
                        Property("hmi_name", StringProperty("可选 WinCC Unified HMI 名称；仅有一个时自动选择")),
                        Property("kind", Dict("type", "string", "enum", new[] { "summary", "screens", "tags", "tag_tables", "discrete_alarms", "analog_alarms", "alarm_classes", "connections", "scripts", "text_lists" }, "description", "读取类别，默认 summary")),
                        Property("name_filter", StringProperty("可选名称、PLC 变量或报警文本过滤关键字")),
                        Property("limit", Dict("type", "integer", "minimum", 1, "maximum", 1000, "description", "返回上限，默认 200"))), true, false, true),
                Tool("tia_compile_unified_hmi", "编译指定 WinCC Unified HMI 并返回错误与警告；不保存项目。",
                    ObjectSchema(Property("project_path", project), Property("hmi_name", StringProperty("可选 WinCC Unified HMI 名称；仅有一个时自动选择"))), false, false, true),
                Tool("tia_generate_hmi_configuration", "从模型批量构建经典 HMI 画面/控件和变量表 XML，不写工程。",
                    ObjectSchema(new[] { "model_json" }, Property("project_path", project), Property("model_json", StringProperty("screens/tag_tables/objects 模型 JSON，详见 CAPABILITIES.md"))), true, false, true),
                Tool("tia_read_hmi_configuration", "快速列出或批量读取经典 HMI 画面/模板/弹窗/变量表/脚本/文本图形列表/周期/连接。默认只返回目录；include_xml/include_model 可获取完整可编辑内容。",
                    ObjectSchema(Property("project_path", project), Property("hmi_name", StringProperty("HMI 完整名称或精确别名")), Property("kind", StringProperty("all/screens/screen_templates/popup_screens/slidein_screens/tag_tables/scripts/text_lists/graphic_lists/cycles/connections")),
                        Property("name_filter", StringProperty("名称关键字")), Property("group_prefix", StringProperty("组路径前缀")), Property("include_xml", BooleanProperty("返回 XML，默认 false")), Property("include_model", BooleanProperty("返回完整节点模型，默认 false"))), true, false, true),
                Tool("tia_write_hmi_configuration", "在 Openness V21 事务中批量写入经典 HMI XML 或通用节点模型（失败自动回滚）。覆盖前备份，默认最后统一编译 HMI。不保存、不下载。",
                    ObjectSchema(new[] { "objects_json", "confirm" }, Property("project_path", project), Property("hmi_name", StringProperty("HMI 完整名称或精确别名")), Property("objects_json", StringProperty("对象数组，每项 kind/group_path/xml 或 model")),
                        Property("compile", BooleanProperty("默认 true")), Property("skip_unchanged", BooleanProperty("默认 true；严格比较对象 XML 完全相同时跳过导入。")), Property("confirm", BooleanProperty("确认写入离线 HMI 组态"))), false, true, false),
                Tool("tia_disconnect", "释放指定项目连接，保留其他项目连接。不保存、不关闭用户项目；多个项目时必须传 project_path。", ObjectSchema(Property("project_path", project)), false, false, true),
                Tool("tia_list_software_units", "列出 PLC 中的所有 TIA V21 Software Units（含 SafetyUnits）、预设命名空间（NamespacePreset）、块/类型/变量表数量及单元间关系（Relations）。",
                    ObjectSchema(Property("project_path", project), Property("plc_name", plc)), true, false, true),
                Tool("tia_query_cross_references", "调用 TIA Portal V21 官方 CrossReferenceService 引擎，精确查询 PLC、Software Unit、单个块、UDT 或变量表的交叉引用（含 Read/Write 读写方向、双线圈排查及 UnusedObjects 未使用符号检测）。",
                    ObjectSchema(Property("project_path", project), Property("plc_name", plc), Property("unit_name", unitProp),
                        Property("scope_kind", Dict("type", "string", "enum", new[] { "plc", "block", "type", "tag_table" }, "description", "查询范围，默认 plc")),
                        Property("target_name", StringProperty("当 scope_kind 为 block/type/tag_table 时的目标对象名称")),
                        Property("group_path", StringProperty("可选目标组路径")),
                        Property("filter", Dict("type", "string", "enum", new[] { "AllObjects", "ObjectsWithReferences", "ObjectsWithoutReferences", "UnusedObjects" }, "description", "官方交叉引用过滤器，默认 ObjectsWithReferences")),
                        Property("symbol_filter", StringProperty("可选符号名/路径/地址关键字过滤")),
                        Property("access_filter", StringProperty("可选访问方向过滤：Read / Write / ReadWrite")),
                        Property("max_results", Dict("type", "integer", "minimum", 1, "maximum", 2000, "description", "返回源对象条数上限，默认 200"))), true, false, true),
                Tool("tia_read_document", "【V21 首选】通过 Openness V21 SimaticSD (ExportAsDocuments) 将任意支持块（SCL/LAD/FBD/STL/DB）或数据类型（UDT/NVT）导出为紧凑人类可读的 .s7dcl 代码文档与 .s7res 多语言注释资源，相比传统 XML 大幅节省 70%~90% Token。",
                    ObjectSchema(new[] { "block_name" }, Property("project_path", project), Property("plc_name", plc), Property("unit_name", unitProp),
                        Property("block_name", StringProperty("程序块或 UDT 名称")),
                        Property("group_path", StringProperty("可选组路径，用 / 分隔")),
                        Property("kind", Dict("type", "string", "enum", new[] { "block", "type" }, "description", "对象类别：block（默认）或 type（UDT/NVT）")),
                        Property("include_resources", BooleanProperty("是否同时返回 .s7res 多语言注释文件（默认 true）"))), true, false, true),
                Tool("tia_read_documents_batch", "【V21 首选】批量读取多个程序块或 UDT 的 SimaticSD (.s7dcl / .s7res) 文档，最多 100 项。",
                    ObjectSchema(new[] { "items_json" }, Property("project_path", project), Property("plc_name", plc),
                        Property("items_json", StringProperty("JSON 数组，每项包含 {block_name, group_path(可选), unit_name(可选), kind('block'|'type',可选)}")),
                        Property("include_resources", BooleanProperty("默认 true"))), true, false, true),
                Tool("tia_import_document", "【V21 首选】在 Openness V21 事务中通过 ImportFromDocuments 导入单个 SimaticSD (.s7dcl + 可选 .s7res) 程序块或 UDT（自动备份原对象，导入失败自动回滚事务）。不保存、不下载；必须 confirm=true。",
                    ObjectSchema(new[] { "s7dcl", "confirm" }, Property("project_path", project), Property("plc_name", plc), Property("unit_name", unitProp),
                        Property("block_name", StringProperty("可选对象名称（留空自动从 .s7dcl 头部提取）")),
                        Property("group_path", StringProperty("可选目标组路径（留空默认根组）")),
                        Property("kind", Dict("type", "string", "enum", new[] { "block", "type" }, "description", "可选：block 或 type（留空自动识别）")),
                        Property("s7dcl", StringProperty("完整 SimaticSD .s7dcl 文档内容")),
                        Property("s7res", StringProperty("可选主 .s7res 多语言文本资源 YAML/文档内容")),
                        Property("resources_json", StringProperty("可选附加资源字典 JSON（文件名 -> 内容）")),
                        Property("import_options", StringProperty("ImportDocumentOptions 标志，默认 Override（可选 Skip, Override, ActivateInactiveElements）")),
                        Property("compile_mode", compileModeProp),
                        Property("confirm", BooleanProperty("确认修改离线工程"))), false, true, false),
                Tool("tia_import_documents_batch", "【V21 首选】在单个 Openness V21 事务中原子批量导入多个 SimaticSD (.s7dcl/.s7res) 文档，内部自动按 UDT -> 程序块顺序导入，任一失败整批自动回滚，最后统一编译一次。",
                    ObjectSchema(new[] { "items_json", "confirm" }, Property("project_path", project), Property("plc_name", plc),
                        Property("items_json", StringProperty("JSON 数组，每项包含 {s7dcl, s7res(可选), block_name(可选), group_path(可选), unit_name(可选), kind(可选), import_options(可选)}")),
                        Property("compile_mode", compileModeProp),
                        Property("confirm", BooleanProperty("确认修改离线工程"))), false, true, false),
                Tool("tia_import_blocks_batch", "在单个 Openness V21 事务中原子批量导入多个 SimaticML XML 对象（UDT/NVT、PlcTagTable、FC/FB/DB/OB），默认开启 IgnoreStructuralChanges 与 IgnoreMissingReferencedObjects 解决交叉依赖，任一失败整批自动回滚。",
                    ObjectSchema(new[] { "items_json", "confirm" }, Property("project_path", project), Property("plc_name", plc),
                        Property("items_json", StringProperty("JSON 数组，每项包含 {xml, group_path(可选), unit_name(可选), sw_import_options(可选)}")),
                        Property("create_group_if_missing", BooleanProperty("默认 true")),
                        Property("sw_import_options", swImportOptProp),
                        Property("compile_mode", compileModeProp),
                        Property("confirm", BooleanProperty("确认修改离线工程"))), false, true, false),
                Tool("tia_list_blocks", "快速列出 PLC 程序块及 Software Unit、Namespace、一致性与修改时间戳（内置会话缓存，支持按单元/组/名称/语言过滤）。",
                    ObjectSchema(Property("project_path", project), Property("plc_name", plc), Property("unit_name", unitProp),
                    Property("scope_path", StringProperty("精确组路径：直接扫描该组及子组，不扫描其他组；/ 表示根组。")),
                    Property("group_filter", StringProperty("可选：按文件夹路径过滤")),
                    Property("name_filter", StringProperty("可选：按块名称或 Namespace 关键字过滤")),
                    Property("language_filter", StringProperty("可选：按语言过滤（SCL/LAD/FBD/GRAPH/DB/STL）")),
                    Property("refresh", BooleanProperty("可选：是否强制重新从 TIA 扫描刷新缓存（默认 false）"))), true, false, true),
                Tool("tia_list_types", "快速列出 PLC 中的所有数据类型（UDT / V21 NamedValueType NVT）及 Software Unit、Namespace 与修改时间。",
                    ObjectSchema(Property("project_path", project), Property("plc_name", plc), Property("unit_name", unitProp),
                    Property("scope_path", StringProperty("精确组路径：直接扫描该组及子组，/ 表示根组。")),
                    Property("group_filter", StringProperty("可选：按组路径过滤")),
                    Property("name_filter", StringProperty("可选：按名称或 Namespace 关键字过滤")),
                    Property("refresh", BooleanProperty("可选：是否强制刷新缓存（默认 false）"))), true, false, true),
                Tool("tia_list_tag_tables", "列出 PLC（及各 Software Unit）所有变量表及表内变量与常量数量。",
                    ObjectSchema(Property("project_path", project), Property("plc_name", plc), Property("unit_name", unitProp)), true, false, true),
                Tool("tia_read_tag_table", "读取单个 PLC 变量表（PlcTagTable）的 SimaticML XML。",
                    ObjectSchema(new[] { "table_name" }, Property("project_path", project), Property("plc_name", plc), Property("unit_name", unitProp),
                    Property("table_name", StringProperty("PLC 变量表名称")),
                    Property("group_path", StringProperty("可选变量表组路径")),
                    Property("strip_document_info", stripDocProp)), true, false, true),
                Tool("tia_import_tag_table", "在 V21 事务中导入单个 PLC 变量表 SimaticML XML，导入前自动备份原表。不保存、不下载。",
                    ObjectSchema(new[] { "xml", "confirm" }, Property("project_path", project), Property("plc_name", plc), Property("unit_name", unitProp),
                    Property("group_path", StringProperty("可选目标组路径")),
                    Property("xml", StringProperty("完整 SW.Tags.PlcTagTable SimaticML XML")),
                    Property("create_group_if_missing", BooleanProperty("目标组不存在时是否自动创建（默认 true）")),
                    Property("confirm", BooleanProperty("确认修改离线项目"))), false, true, false),
                Tool("tia_list_block_groups", "列出程序块组，包含空文件夹。", ObjectSchema(Property("project_path", project), Property("plc_name", plc), Property("unit_name", unitProp)), true, false, true),
                Tool("tia_organize_block_groups", "在 V21 事务中仅重命名/补齐指定父组的直属文件夹，保留块内容，记录原组树快照。不合并、不删除块、不保存、不下载。",
                    ObjectSchema(new[] { "parent_path", "rename_json", "group_names_json", "confirm" }, Property("project_path", project), Property("plc_name", plc), Property("unit_name", unitProp),
                    Property("parent_path", StringProperty("已存在的父组路径")), Property("rename_json", StringProperty("原名到新名的JSON对象，例如 {\"Main\":\"00 Main\"}")),
                    Property("group_names_json", StringProperty("需补齐的直属组名称JSON数组")), Property("confirm", BooleanProperty("确认整理离线工程的组"))), false, false, true),
                Tool("tia_read_block", "读取单个程序块的 SimaticML XML（支持 V21 FlgNet/v5 与 strip_document_info 纯净导出）。", ObjectSchema(new[] { "block_name" },
                    Property("project_path", project), Property("plc_name", plc), Property("unit_name", unitProp), Property("block_name", StringProperty("程序块名称（大小写不敏感）")),
                    Property("group_path", StringProperty("可选组路径，用 / 分隔；提供后可极速直达查找。")),
                    Property("strip_document_info", stripDocProp)), true, false, true),
                Tool("tia_read_source", "读取单个 SCL/STL/DB 程序块或 UDT 数据类型的传统外部文本源码（.scl/.db/.udt/.awl）。V21 亦推荐使用 tia_read_document。", ObjectSchema(new[] { "block_name" },
                    Property("project_path", project), Property("plc_name", plc), Property("unit_name", unitProp), Property("block_name", StringProperty("程序块或 UDT 名称")),
                    Property("group_path", StringProperty("可选组路径，用 / 分隔。"))), true, false, true),
                Tool("tia_read_sources_batch", "批量读取多个 SCL/STL/DB 程序块或 UDT 的纯文本源码。",
                    ObjectSchema(new[] { "block_names" }, Property("project_path", project), Property("plc_name", plc), Property("unit_name", unitProp),
                    Property("block_names", Dict("type", "array", "minItems", 1, "maxItems", 50, "items", Dict("type", "string"), "description", "要读取的程序块或 UDT 名称数组")),
                    Property("group_path", StringProperty("可选公共组路径，留空时自动从缓存定位各块所在组"))), true, false, true),
                Tool("tia_import_block", "在 V21 事务中导入单个 XML 程序块，支持 SWImportOptions，自动备份已有块并按 compile_mode 编译。不保存、不下载。",
                    ObjectSchema(new[] { "xml", "confirm" }, Property("project_path", project), Property("plc_name", plc), Property("unit_name", unitProp),
                    Property("group_path", StringProperty("目标组路径，用 / 分隔。")),
                    Property("xml", StringProperty("单块完整 SimaticML XML")),
                    Property("create_group_if_missing", BooleanProperty("目标组不存在时是否自动创建（默认 true）")),
                    Property("sw_import_options", swImportOptProp),
                    Property("backup_mode", backupModeProp),
                    Property("compile_mode", compileModeProp),
                    Property("confirm", BooleanProperty("确认修改离线项目"))), false, true, false),
                Tool("tia_import_source", "在 V21 事务中导入单个 SCL/STL/DB 程序块或 UDT 的纯文本源码，自动备份原对象并按 compile_mode 编译。不保存、不下载。",
                    ObjectSchema(new[] { "source", "format", "confirm" }, Property("project_path", project), Property("plc_name", plc), Property("unit_name", unitProp),
                    Property("block_name", StringProperty("可选：原块或 UDT 名称（留空时自动从源码头提取）")),
                    Property("group_path", StringProperty("目标组路径，用 / 分隔")),
                    Property("format", StringProperty("源码格式：scl、db、udt 或 awl")),
                    Property("source", StringProperty("完整 SCL/DB/UDT/AWL 文本源码")),
                    Property("create_group_if_missing", BooleanProperty("目标组不存在时是否自动创建（默认 true）")),
                    Property("compile_mode", compileModeProp),
                    Property("confirm", BooleanProperty("确认修改离线项目"))), false, true, false),
                Tool("tia_import_sources_batch", "在单个 V21 事务中批量导入多个 SCL/STL/DB/UDT 文本源码（任一失败整批自动回滚），内部按 UDT -> FC/FB -> DB 顺序导入并自动备份，最后统一编译一次。",
                    ObjectSchema(new[] { "items_json", "confirm" }, Property("project_path", project), Property("plc_name", plc),
                    Property("items_json", StringProperty("JSON 数组，每项包含 {source, format, block_name(可选), group_path(可选), unit_name(可选)}")),
                    Property("create_group_if_missing", BooleanProperty("目标组不存在时是否自动创建（默认 true）")),
                    Property("compile_mode", compileModeProp),
                    Property("confirm", BooleanProperty("确认修改离线项目"))), false, true, false),
                Tool("tia_restore_backup", "从备份 XML 还原程序块、UDT 或 PLC 变量表；核对工程、PLC 和内容指纹。",
                    ObjectSchema(new[] { "backup_path", "confirm" }, Property("project_path", project), Property("plc_name", plc), Property("unit_name", unitProp),
                    Property("backup_path", StringProperty("备份 XML 文件路径")),
                    Property("group_path", StringProperty("可选目标组路径")),
                    Property("compile_mode", compileModeProp),
                    Property("confirm", BooleanProperty("确认还原覆盖离线块"))), false, true, false),
                Tool("tia_delete_block", "在 V21 事务中删除指定的程序块或 UDT（删除前自动导出 XML 备份到 block-backups）。不保存、不下载。",
                    ObjectSchema(new[] { "block_name", "confirm" }, Property("project_path", project), Property("plc_name", plc), Property("unit_name", unitProp),
                    Property("block_name", StringProperty("要删除的程序块或 UDT 名称")),
                    Property("group_path", StringProperty("可选组路径")),
                    Property("confirm", BooleanProperty("确认从离线工程删除该对象"))), false, true, false),
                Tool("tia_generate_graph", "从零生成完整 V21 GRAPH FB（Graph/v6 + FlgNet/v5）。model_json 定义 block_name/block_number/namespace(可选)/sequences；支持互锁、监控、跳转和并行/选择分支。不修改工程。",
                    ObjectSchema(new[] { "model_json" }, Property("project_path", project), Property("model_json", StringProperty("完整 GRAPH 模型 JSON，详见 CAPABILITIES.md")), Property("include_model", BooleanProperty("默认 true；只需要生成 XML 时设 false。")), Property("base_xml", StringProperty("可选：同版本 GRAPH FB 基础 XML"))), true, false, true),
                Tool("tia_read_graph_model", "读取可完整编辑并往返构建的 GRAPH 模型，同时返回保留块接口和设置的 base_xml。",
                    ObjectSchema(new[] { "block_name" }, Property("project_path", project), Property("plc_name", plc), Property("unit_name", unitProp), Property("block_name", StringProperty("GRAPH FB 名称")), Property("include_base_xml", BooleanProperty("默认 true；设 false 只返回完整模型。")), Property("group_path", StringProperty("组路径"))), true, false, true),
                Tool("tia_update_graph", "通用 GRAPH 批量修改：一次导出、一次解析、批量补丁、V21 事务导入。默认仅预览；不保存、不下载。",
                    ObjectSchema(new[] { "block_name", "patches_json" }, Property("project_path", project), Property("plc_name", plc), Property("unit_name", unitProp),
                        Property("block_name", StringProperty("现有 GRAPH FB 名称")), Property("group_path", StringProperty("已知组路径；/ 表示根组")),
                        Property("patches_json", StringProperty("有序补丁数组：set_attribute/set_text/replace_xml/append_xml/insert_before/insert_after/remove；XPath 使用 g 命名空间。")),
                        Property("expected_hash", StringProperty("可选：读取时返回的 graph_hash；内容已变化则拒绝写入")),
                        Property("dry_run", BooleanProperty("默认 true；false 且 confirm=true 才写入")),
                        Property("skip_unchanged", BooleanProperty("默认 true；补丁后内容不变则跳过导入和编译")),
                        Property("include_xml", BooleanProperty("返回修改后的完整 XML，默认 false")),
                        Property("backup_mode", backupModeProp), Property("compile_mode", compileModeProp),
                        Property("confirm", BooleanProperty("确认修改离线工程"))), false, true, false),
                Tool("tia_write_graph", "从模型新建或完整修改 V21 GRAPH FB，备份已有块并按 compile_mode 编译。不保存、不下载。",
                    ObjectSchema(new[] { "model_json", "confirm" }, Property("project_path", project), Property("plc_name", plc), Property("unit_name", unitProp), Property("model_json", StringProperty("GRAPH 模型 JSON")),
                        Property("base_xml", StringProperty("可选：已有 GRAPH XML")), Property("group_path", StringProperty("目标组")), Property("create_group_if_missing", BooleanProperty("自动创建组，默认 true")),
                        Property("sw_import_options", swImportOptProp),
                        Property("backup_mode", backupModeProp), Property("compile_mode", compileModeProp), Property("confirm", BooleanProperty("确认修改离线工程"))), false, true, false),
                Tool("tia_read_graph", "读取现有 GRAPH FB，返回步/动作/转换条件/分支/连线结构化摘要。",
                    ObjectSchema(new[] { "block_name" }, Property("project_path", project), Property("plc_name", plc), Property("unit_name", unitProp),
                    Property("block_name", StringProperty("GRAPH FB 名称")), Property("group_path", StringProperty("可选组路径")),
                    Property("include_logic", BooleanProperty("默认 true；false 省略动作、互锁、监控和转换条件 XML")),
                    Property("include_raw_xml", BooleanProperty("是否同时返回完整原始 XML（默认 true）"))), true, false, true),
                Tool("tia_patch_graph", "通用 GRAPH XML 离线补丁，不连接或写工程。",
                    ObjectSchema(new[] { "xml", "patches_json" }, Property("xml", StringProperty("完整 GRAPH FB XML")),
                    Property("patches_json", StringProperty("例如 [{\"op\":\"set_attribute\",\"xpath\":\"//g:Step[@Number='1']\",\"attribute\":\"WarningTime\",\"value\":\"T#8S\"}]")),
                    Property("new_block_name", StringProperty("可选：生成独立副本的 FB 名称")),
                    Property("new_block_number", Dict("type", "integer", "minimum", 1, "maximum", 65535))), true, false, true),
                Tool("tia_generate_lad", "生成符合 TIA V21 LADFBD_v5 (FlgNet/v5) 规范的 LAD FC/FB XML，不修改项目。支持 FC/FB（含 Static 静态变量与 V21 Namespace）、全局符号与局部变量、数组下标、常开/常闭/上升沿/下降沿触点、比较指令及普通/取反/置位/复位线圈。",
                    ObjectSchema(new[] { "block_name", "block_number", "networks_json" }, Property("project_path", project), Property("block_name", StringProperty("块名称")),
                    Property("block_number", Dict("type", "integer", "minimum", 1, "maximum", 65535)),
                    Property("block_type", Dict("type", "string", "enum", new[] { "FC", "FB" }, "description", "可选：FC（默认）或 FB")),
                    Property("namespace", StringProperty("可选：TIA V21 块命名空间（如 MyUnit.Motion），留空无命名空间")),
                    Property("networks_json", StringProperty("例如 [{\"title\":\"启动保持\",\"coil\":\"Motor\",\"coil_type\":\"coil\",\"branches\":[[{\"symbol\":\"Start\"},{\"symbol\":\"Stop\",\"negated\":true}],[{\"symbol\":\"Motor\"}]]}]")),
                    Property("interface_json", StringProperty("可选接口定义 JSON，支持 Input/Output/InOut/Static(仅FB)/Temp/Constant"))), true, false, true),
                Tool("tia_list_open_projects", "在独立进程中列出已打开的 TIA Portal V21 实例与项目（支持 .ap21 与多用户 .als21）；不影响已有连接。", ObjectSchema(Property("portal_version", StringProperty("可选，默认 V21"))), true, false, true),
                Tool("tia_list_plcs", "列出指定或当前唯一已打开项目中的 PLC、经典 WinCC HMI 以及 WinCC Unified HMI。", ObjectSchema(Property("project_path", project)), true, false, true),
                Tool("tia_export_plc_sources", "把 PLC 程序块、UDT、Software Units 以及经典 WinCC HMI 工程对象导出到源码目录；不修改博途项目。",
                    ObjectSchema(Property("project_path", project), Property("plc_name", plc), Property("source_directory", source)), false, false, true),
                Tool("tia_compile_plc", "编译指定 PLC 并返回错误和警告；不会保存项目。",
                    ObjectSchema(Property("project_path", project), Property("plc_name", plc)), false, false, true),
                Tool("tia_compile_hmi", "编译指定经典 WinCC HMI 并返回错误和警告；不会保存项目。",
                    ObjectSchema(Property("project_path", project), Property("hmi_name", StringProperty("可选 HMI 名称，仅有一个 HMI 时自动选择"))), false, false, true),
                Tool("tia_list_hmi_text_lists", "快速列出经典 WinCC HMI 的文本列表（TextLists），支持按名称关键字过滤。",
                    ObjectSchema(Property("project_path", project),
                        Property("hmi_name", StringProperty("可选 HMI 名称，仅有一个 HMI 时自动选择")),
                        Property("name_filter", StringProperty("可选：按文本列表名称关键字过滤"))), true, false, true),
                Tool("tia_read_hmi_text_list", "读取单个经典 WinCC HMI 文本列表（HmiTextList）的 SimaticML XML。",
                    ObjectSchema(new[] { "text_list_name" }, Property("project_path", project),
                        Property("hmi_name", StringProperty("可选 HMI 名称，仅有一个 HMI 时自动选择")),
                        Property("text_list_name", StringProperty("HMI 文本列表名称"))), true, false, true),
                Tool("tia_import_hmi_text_list", "导入单个经典 WinCC HMI 文本列表 SimaticML XML，自动备份原有同名文本列表。不自动保存。",
                    ObjectSchema(new[] { "xml", "confirm" }, Property("project_path", project),
                        Property("hmi_name", StringProperty("可选 HMI 名称，仅有一个 HMI 时自动选择")),
                        Property("xml", StringProperty("完整 Hmi.TextGraphicList.TextList SimaticML XML")),
                        Property("confirm", BooleanProperty("必须为 true，表示确认修改离线项目"))), false, true, false),
                Tool("tia_import_plc_sources", "从源码目录导入 PLC（含 Software Units）和经典 WinCC HMI 对象。会修改已打开项目，但不会自动保存；必须 confirm=true。",
                    ObjectSchema(new[] { "confirm" }, Property("project_path", project), Property("plc_name", plc),
                        Property("source_directory", source), Property("confirm", BooleanProperty("必须为 true，表示确认修改项目。"))), false, true, false),
                Tool("tia_save_project", "保存当前博途项目的全部未保存修改；必须 confirm=true。",
                    ObjectSchema(new[] { "confirm" }, Property("project_path", project),
                        Property("confirm", BooleanProperty("必须为 true，表示确认保存项目。"))), false, true, false)
            }.Concat(AlarmDefinitions()).Concat(ProjectIndexDefinitions()).Concat(SimulationDefinitions()).Concat(OnlineTools.Definitions()).ToArray();

            AppConfig profileCfg = NewDiscoveryConfig();
            return allTools.Where(t =>
            {
                Dictionary<string, object> map = t as Dictionary<string, object>;
                if (map == null || !map.ContainsKey("name")) return true;
                return profileCfg.IsProfileEnabled(GetToolProfile(Convert.ToString(map["name"])));
            }).ToArray();
        }

        private static object[] SimulationDefinitions()
        {
            object project = StringProperty("博途项目文件完整路径（.ap21/.als21）。");
            object instance = StringProperty("PLCSIM 虚拟控制器实例名称（默认 TIA_V21_SIM）。");
            object confirm = BooleanProperty("必须设为 true 以确认改变本机虚拟仿真 PLC 状态。");
            return new object[] {
                Tool("tia_simulation_status", "检测 PLCSIM Runtime API 8.0 / S7-PLCSIM V21 并只读列出本机仿真实例、API 路径/版本、全部支持的 CPU 类型数量以及 PG/PC 逻辑设备。", ObjectSchema(), true, false, true),
                Tool("tia_simulation_start", "通过 PLCSIM Runtime API 8.0 / S7-PLCSIM V21 注册或打开本机虚拟 PLC 实例并上电配置网络。支持全部 78 种 V8 ECPUType 枚举（含 S7-1500、ET200SP、T/F CPU、S7-1200 G2）及 6ES7 订货号。",
                    ObjectSchema(new[] { "confirm" },
                        Property("instance_name", instance),
                        Property("cpu_type", StringProperty("默认 CPU1500_Unspecified；支持全部 78 种 PLCSIM V8 ECPUType 或 6ES7 订货号（如 CPU1516, CPU1515T, 6ES7 214-1HG40-0XB0）。")),
                        Property("ip", StringProperty("可选虚拟接口 IPv4 地址（如 192.168.0.10）")),
                        Property("subnet_mask", StringProperty("默认 255.255.255.0")),
                        Property("gateway", StringProperty("可选网关 IPv4 地址")),
                        Property("run", BooleanProperty("上电后是否自动切到 RUN 模式，默认 false")),
                        Property("show_panel", BooleanProperty("是否自动拉起 S7-PLCSIM V21 Compact 或 Advanced 面板 UI，默认 false")),
                        Property("timeout_ms", Dict("type", "integer", "minimum", 5000, "maximum", 300000, "description", "超时毫秒数，默认 60000")),
                        Property("confirm", confirm)), false, false, false),
                Tool("tia_simulation_stop", "对本机 PLCSIM 仿真实例执行 STOP、断电（power_off）或注销销毁（unregister）；绝不触碰物理 PLC。",
                    ObjectSchema(new[] { "confirm" },
                        Property("instance_name", instance),
                        Property("mode", Dict("type", "string", "enum", new[] { "stop", "power_off", "unregister" }, "description", "操作模式，默认 power_off")),
                        Property("timeout_ms", Dict("type", "integer", "minimum", 5000, "maximum", 180000, "description", "默认 30000")),
                        Property("confirm", confirm)), false, true, false),
                Tool("tia_simulation_download", "仅通过 PLCSIM 虚拟接口向已在本机运行且 IP 匹配的 PLCSIM 仿真实例下载硬件与软件（自动适配 V21 安全 PLC 密码环境变量 TIA_SIMULATION_PLC_PASSWORD 与新装载校验选项）；严格拒绝未知实例或物理网卡。不保存工程；必须 confirm=true。",
                    ObjectSchema(new[] { "ip", "confirm" },
                        Property("project_path", project),
                        Property("plc_name", StringProperty("可选目标 PLC 名称；仅有一个 PLC 时自动选择")),
                        Property("instance_name", instance),
                        Property("ip", StringProperty("目标 PLCSIM 实例 IPv4 地址（必须与运行中的本机实例 IP 一致）")),
                        Property("interface_hint", StringProperty("下载接口筛选关键字，默认 PLCSIM")),
                        Property("rack", Dict("type", "integer", "minimum", 0, "maximum", 7, "description", "默认 0")),
                        Property("slot", Dict("type", "integer", "minimum", 0, "maximum", 31, "description", "默认 1")),
                        Property("compile_before_download", BooleanProperty("下载前自动编译 PLC 并检查零错误，默认 true")),
                        Property("start_plc", BooleanProperty("下载完成后是否启动虚拟 PLC，默认 true")),
                        Property("confirm", confirm)), false, true, false)
            };
        }

        private static Dictionary<string, object> Tool(string name, string description, object schema,
            bool readOnly, bool destructive, bool idempotent)
        {
            ((Dictionary<string, object>)((Dictionary<string, object>)schema)["properties"])["response_mode"] = Dict("type", "string", "enum", new[] { "full", "compact" }, "description", "默认 full 保持兼容；compact 不在文本内容中重复 structuredContent 已包含的 XML/源码，减少响应体积。");
            return Dict("name", name, "description", description, "inputSchema", schema,
                "annotations", Dict("title", name, "readOnlyHint", readOnly, "destructiveHint", destructive,
                    "idempotentHint", idempotent, "openWorldHint", false));
        }

        private static Dictionary<string, object> ObjectSchema(params KeyValuePair<string, object>[] properties)
        {
            return ObjectSchema(new string[0], properties);
        }

        private static Dictionary<string, object> ObjectSchema(string[] required, params KeyValuePair<string, object>[] properties)
        {
            Dictionary<string, object> map = new Dictionary<string, object>();
            foreach (KeyValuePair<string, object> property in properties) map[property.Key] = property.Value;
            return Dict("type", "object", "properties", map, "required", required, "additionalProperties", false);
        }

        private static KeyValuePair<string, object> Property(string name, object schema)
        {
            return new KeyValuePair<string, object>(name, schema);
        }

        private static Dictionary<string, object> StringProperty(string description)
        {
            return Dict("type", "string", "description", description);
        }

        private static Dictionary<string, object> BooleanProperty(string description)
        {
            return Dict("type", "boolean", "description", description);
        }

        private static string StringArg(Dictionary<string, object> args, string name)
        {
            object value = Value(args, name);
            return value == null ? "" : Convert.ToString(value);
        }

        private static object Value(Dictionary<string, object> map, string name)
        {
            object value;
            return map != null && map.TryGetValue(name, out value) ? value : null;
        }

        private Dictionary<string, object> ToolResult(string text, bool isError, object structured)
        {
            Dictionary<string, object> result = Dict("content", new object[] { Dict("type", "text", "text", text) }, "isError", isError);
            if (structured != null) result["structuredContent"] = structured;
            return result;
        }

        private static void CompactResponse(Dictionary<string, object> result)
        {
            object raw; var data = result.TryGetValue("structuredContent", out raw) ? raw as Dictionary<string, object> : null;
            var content = result.TryGetValue("content", out raw) ? raw as object[] : null;
            if (data == null || content == null) return;
            foreach (var item in content.OfType<Dictionary<string, object>>())
            {
                string text = StringArg(item, "text");
                foreach (string key in new[] { "xml", "source", "s7dcl" })
                    if (data.ContainsKey(key) && data[key] is string && text == (string)data[key])
                    { item["text"] = "内容已读取；完整 " + key + " 保留在 structuredContent。"; break; }
            }
        }

        private void WriteResult(object id, object result)
        {
            Write(Dict("jsonrpc", "2.0", "id", id, "result", result));
        }

        private void WriteError(object id, int code, string message)
        {
            Write(Dict("jsonrpc", "2.0", "id", id, "error", Dict("code", code, "message", message)));
        }

        private void Write(object value)
        {
            _protocolOutput.WriteLine(_json.Serialize(value));
            _protocolOutput.Flush();
        }

        private static Dictionary<string, object> Dict(params object[] values)
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            for (int i = 0; i + 1 < values.Length; i += 2) result[Convert.ToString(values[i])] = values[i + 1];
            return result;
        }

        private static string RootMessage(Exception ex)
        {
            while (ex.InnerException != null) ex = ex.InnerException;
            return ex.Message;
        }
    }
}
