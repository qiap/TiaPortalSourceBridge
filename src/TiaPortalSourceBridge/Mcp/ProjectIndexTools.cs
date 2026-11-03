using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Collections;
using TiaPortalSourceBridge.Configuration;
using TiaPortalSourceBridge.Indexing;

namespace TiaPortalSourceBridge.Mcp
{
    internal sealed partial class McpServer
    {
        private ProjectIndexStore projectIndex;
        private string indexedProjectPath;
        private static bool IsIndexTool(string name)
        { return new[] { "tia_build_project_index", "tia_get_project_tree", "tia_find_references", "tia_get_task_context" }.Contains(name); }
        private ProjectIndexStore Index(AppConfig config)
        {
            if (projectIndex == null || !string.Equals(indexedProjectPath, config.ProjectPath, StringComparison.OrdinalIgnoreCase))
            { indexedProjectPath = config.ProjectPath; projectIndex = new ProjectIndexStore(config.ProjectPath, config.TiaPortalVersion); }
            return projectIndex;
        }
        private object IndexConnectionStatus(AppConfig config)
        {
            var index = Index(config);
            try { return index.Exists ? index.BuildCatalog(w => new LiveIndexSource(_session).Catalog(true, w), false, true) : (object)new { state = "not_built", next_tool = "tia_build_project_index", detail_level = "catalog", then = "tia_get_task_context" }; }
            catch (Exception ex) { return new { state = "unavailable", error = ex.Message, next_tool = "tia_build_project_index", refresh_catalog = true }; }
        }
        private object preparedIndexContext;
        private bool preparedIndexUsed;
        // Read entry points always consult the index inside the server; no client opt-out.
        // Failure remains explicit and does not prevent a requested authoritative live read.
        private void PrepareIndexContext(string name, Dictionary<string, object> args, AppConfig config)
        {
            if (_projects != null || !(name.StartsWith("tia_read_") || name == "tia_list_blocks" || name == "tia_list_types" || name == "tia_list_tag_tables" || name == "tia_list_block_groups" || name == "tia_list_hmi_text_lists" || name == "tia_list_software_units")) return;
            preparedIndexUsed = false;
            try
            {
                var index = Index(config);
                if (!index.Exists) index.BuildCatalog(w => new LiveIndexSource(Connect(config)).Catalog(false, w), false, true, false);
                string query = StringArg(args, "block_name");
                if (query.Length == 0) query = StringArg(args, "table_name");
                if (query.Length == 0) query = StringArg(args, "name_filter");
                if (query.Length == 0 && name == "tia_read_sources_batch") query = string.Join(" ", StringArrayArg(args, "block_names").Take(8));
                string kind = name.Contains("hmi") ? "hmi." : name.Contains("hardware") ? "hardware" : "plc.";
                string owner = StringArg(args, name.Contains("hmi") ? "hmi_name" : "plc_name");
                string group = StringArg(args, "group_path");
                if (group == "/") group = "";
                preparedIndexContext = query.Length > 0 ? index.Context(query.Length > 1000 ? query.Substring(0, 1000) : query, 3, 4, kind, owner, group)
                    : index.Tree(kind, owner, group, "", 0, 10);
                preparedIndexUsed = true;
            }
            catch (Exception ex) { preparedIndexContext = new { state = "index_unavailable", error = ex.Message, next_tool = "tia_build_project_index" }; }
        }
        private static int IndexInt(Dictionary<string, object> args, string name, int fallback, int min, int max)
        {
            object raw = Value(args, name); int value;
            if (raw == null) return fallback;
            if (!int.TryParse(Convert.ToString(raw), out value) || value < min || value > max) throw new ArgumentException(name + " 必须在 " + min + ".." + max);
            return value;
        }
        private Dictionary<string, object> CallIndexTool(string name, Dictionary<string, object> args, AppConfig config)
        {
            var index = Index(config); LiveIndexSource live = null;
            Func<LiveIndexSource> source = () => live ?? (live = new LiveIndexSource(Connect(config)));
            string kind = StringArg(args, "kind_filter"), owner = StringArg(args, "owner_filter"), group = StringArg(args, "group_prefix"), filter = StringArg(args, "name_filter");
            string detail = StringArg(args, "detail_level");
            if (detail.Length > 0 && detail != "catalog" && detail != "content") throw new ArgumentException("detail_level 必须为 catalog 或 content。");
            bool refresh = BoolArg(args, "refresh_catalog", false);
            if (!index.Exists || refresh) index.BuildCatalog(w => source().Catalog(BoolArg(args, "include_hardware", true), w), false, false, BoolArg(args, "include_hardware", true));
            if (name == "tia_build_project_index")
            {
                if (kind.Length == 0 && !BoolArg(args, "include_all_content", false)) kind = "plc.";
                object data = detail == "content"
                    ? index.Hydrate(index.Select(kind, owner, group, filter), e => source().Read(e), IndexInt(args, "max_objects", 25, 1, 1000), BoolArg(args, "force", false), IndexInt(args, "time_budget_ms", 15000, 100, 60000))
                    : index.Status();
                return ToolResult("工程索引已更新；仅写入工程旁的索引文件，没有修改博途工程。", false, data);
            }
            int limit = IndexInt(args, "limit", name == "tia_get_task_context" ? 6 : 100, 1, name == "tia_get_task_context" ? 25 : 500);
            if (name == "tia_get_project_tree") return ToolResult("工程索引树（分页快照）。", false, index.Tree(kind, owner, group, filter, IndexInt(args, "offset", 0, 0, 50000), limit));
            if (name == "tia_find_references") return ToolResult("索引中的显式引用；请同时查看覆盖率和过期状态。", false, index.References(StringArg(args, "symbol"), IndexInt(args, "offset", 0, 0, int.MaxValue), limit));
            string query = StringArg(args, "query"); object hydration = null;
            if (BoolArg(args, "hydrate", true))
            {
                var candidates = index.Ranked(query, IndexInt(args, "max_objects", 8, 1, 100), kind, owner, group);
                hydration = index.Hydrate(candidates, e => source().Read(e), candidates.Length == 0 ? 1 : candidates.Length, BoolArg(args, "force", false));
            }
            return ToolResult("按任务选取的对象结构和引用；按 next_read 读取当前目标后再修改。", false,
                Dict("context", index.Context(query, limit, IndexInt(args, "facts_per_object", 6, 1, 20), kind, owner, group), "hydration", hydration));
        }
        private void InvalidateIndexBeforeMutation(string name, Dictionary<string, object> args)
        {
            if (_projects != null) return; // The project worker owns freshness, including unknown-outcome writes.
            if (!BoolArg(args, "confirm", false)) return;
            if (name == "tia_write_hmi_configuration" && BoolArg(args, "dry_run", true)) return;
            bool mutates = name.StartsWith("tia_import_", StringComparison.Ordinal) || name.StartsWith("tia_write_", StringComparison.Ordinal)
                || name == "tia_update_graph" && !BoolArg(args, "dry_run", true) || name == "tia_delete_block" || name == "tia_restore_backup"
                || name == "tia_organize_block_groups" || name == "tia_apply_hardware_configuration" && !BoolArg(args, "dry_run", true);
            if (!mutates) return;
            string path = StringArg(args, "project_path"); if (path.Length == 0) path = _sessionPath;
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                var config = NewProjectConfig(path, "", ""); var index = Index(config);
                string[] affected = ExtractMutatedNames(name, args);
                string suffix = affected != null && affected.Length > 0 ? ":" + string.Join(",", affected.Take(5)) : "";
                index.MarkDirty(affected, name + suffix);
            }
            catch (Exception ex) { Console.Error.WriteLine("[索引待刷新标记失败] " + ex.Message); }
        }
        private string[] ExtractMutatedNames(string name, Dictionary<string, object> args)
        {
            var names = new List<string>();
            if (name == "tia_update_graph" || name == "tia_delete_block")
            {
                string b = StringArg(args, "block_name").Trim();
                if (b.Length > 0) names.Add(b);
                return names.Count > 0 ? names.ToArray() : null;
            }
            if (name == "tia_import_source")
            {
                string b = StringArg(args, "block_name").Trim();
                if (b.Length == 0) b = ExtractSourceHeaderName(StringArg(args, "source"));
                if (b.Length > 0) names.Add(b);
                return names.Count > 0 ? names.ToArray() : null;
            }
            if (name == "tia_import_sources_batch" || name == "tia_import_blocks_batch")
            {
                try
                {
                    var rows = _json.DeserializeObject(StringArg(args, "items_json")) as object[];
                    if (rows != null) foreach (var row in rows.OfType<Dictionary<string, object>>())
                    {
                        string b = StringArg(row, "block_name").Trim();
                        if (b.Length == 0) b = name == "tia_import_blocks_batch" ? ExtractXmlObjectName(StringArg(row, "xml")) : ExtractSourceHeaderName(StringArg(row, "source"));
                        if (b.Length > 0) names.Add(b);
                    }
                }
                catch { return null; }
                return names.Count > 0 ? names.Distinct(StringComparer.OrdinalIgnoreCase).ToArray() : null;
            }
            if (name == "tia_import_block" || name == "tia_import_tag_table" || name == "tia_import_hmi_text_list")
            {
                string n = ExtractXmlObjectName(StringArg(args, "xml"));
                if (n.Length > 0) names.Add(n);
                return names.Count > 0 ? names.ToArray() : null;
            }
            if (name == "tia_write_graph")
            {
                try
                {
                    var model = _json.Deserialize<Dictionary<string, object>>(StringArg(args, "model_json"));
                    string b = model == null ? "" : StringArg(model, "block_name").Trim();
                    if (b.Length > 0) names.Add(b);
                }
                catch { return null; }
                return names.Count > 0 ? names.ToArray() : null;
            }
            if (name == "tia_write_hmi_configuration")
            {
                try
                {
                    var rows = _json.DeserializeObject(StringArg(args, "objects_json")) as object[];
                    if (rows != null) foreach (var row in rows.OfType<Dictionary<string, object>>())
                    {
                        string n = StringArg(row, "name").Trim();
                        if (n.Length == 0) n = ExtractXmlObjectName(StringArg(row, "xml"));
                        if (n.Length > 0) names.Add(n);
                    }
                }
                catch { return null; }
                return names.Count > 0 ? names.Distinct(StringComparer.OrdinalIgnoreCase).ToArray() : null;
            }
            return null;
        }
        private static string ExtractSourceHeaderName(string source)
        {
            if (string.IsNullOrWhiteSpace(source)) return "";
            var m = System.Text.RegularExpressions.Regex.Match(source, @"\b(?:FUNCTION_BLOCK|FUNCTION|DATA_BLOCK|TYPE)\s+(?:""([^""]+)""|([A-Za-z_][A-Za-z0-9_]*))", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success) return "";
            return (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim();
        }
        private static string ExtractXmlObjectName(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) return "";
            var m = System.Text.RegularExpressions.Regex.Match(xml, @"<AttributeList>\s*(?:<[^>]+>.*?</[^>]+>\s*)*?<Name>([^<]+)</Name>", System.Text.RegularExpressions.RegexOptions.Singleline);
            return m.Success ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim() : "";
        }
        private void AttachIndexHint(string name, Dictionary<string, object> args, Dictionary<string, object> result)
        {
            if (_projects != null || IsIndexTool(name) || name.StartsWith("tia_online_") || name.StartsWith("tia_simulation_")) return;
            if (projectIndex == null && _sessionPath != null) Index(NewProjectConfig(_sessionPath, "", ""));
            if (projectIndex == null) return;
            string captureError = "";
            if (preparedIndexContext != null)
            {
                object payload; var content = result.TryGetValue("structuredContent", out payload) ? payload as Dictionary<string, object> : null;
                if (content != null) content["index_context"] = preparedIndexContext;
            }
            if (projectIndex.Exists && _session != null) try {
                if (BoolArg(args, "confirm", false) && (name.StartsWith("tia_import_") || name.StartsWith("tia_write_") || name == "tia_delete_block" || name == "tia_organize_block_groups" || name == "tia_restore_backup"))
                    projectIndex.BuildCatalog(w => new LiveIndexSource(_session).Catalog(true, w), false, true);
                CaptureIndexRead(name, args, result);
            }
            catch (Exception ex) { captureError = ex.Message; Console.Error.WriteLine("[索引读取积累失败] " + ex.Message); }
            object raw; var meta = result.TryGetValue("_meta", out raw) ? raw as Dictionary<string, object> : null;
            if (meta == null) { meta = new Dictionary<string, object>(); result["_meta"] = meta; }
            meta["project_index"] = new { state = projectIndex.Exists ? "snapshot_available" : "not_built", next_tool = projectIndex.Exists ? "tia_get_task_context" : "tia_build_project_index", live_read_required_before_write = true, capture_error = captureError, index_used = preparedIndexContext != null && preparedIndexUsed, lookup_phase = preparedIndexContext == null ? "not_applicable" : "before_live_operation" };
        }
        private static object IndexProperty(object item, string name)
        {
            var map = item as Dictionary<string, object>; object raw;
            if (map != null) return map.TryGetValue(name, out raw) ? raw : null;
            if (item == null) return null;
            var prop = item.GetType().GetProperty(name); return prop == null ? null : prop.GetValue(item, null);
        }
        private void CaptureIndexRead(string name, Dictionary<string, object> args, Dictionary<string, object> result)
        {
            object raw;
            if (result.TryGetValue("isError", out raw) && Convert.ToBoolean(raw) && name != "tia_read_sources_batch" && name != "tia_import_blocks_batch") return;
            var data = result.TryGetValue("structuredContent", out raw) ? raw : null;
            if (data == null) return;
            var snapshots = new Dictionary<string, string>();
            if (name == "tia_read_block" || name == "tia_read_graph" || name == "tia_read_graph_model")
            {
                object xml = IndexProperty(data, name == "tia_read_graph_model" ? "base_xml" : "xml");
                if (xml == null || Convert.ToString(xml).Length == 0) return;
                string owner = _session.SelectPlc(StringArg(args, "plc_name")).DisplayName;
                var matches = projectIndex.Select("plc.block", "", "", "").Where(e => e.owner == owner && e.name.Equals(StringArg(args, "block_name"), StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length == 1) snapshots[matches[0].id] = Convert.ToString(xml);
            }
            if (name == "tia_read_source" || name == "tia_read_sources_batch" || name == "tia_import_blocks_batch")
            {
                string owner = _session.SelectPlc(StringArg(args, "plc_name")).DisplayName;
                IEnumerable items = name == "tia_read_source" ? new object[] { data } : IndexProperty(data, "items") as IEnumerable;
                if (items != null) foreach (object item in items)
                {
                    if (name == "tia_read_sources_batch" && !Convert.ToBoolean(IndexProperty(item, "ok"))) continue;
                    string itemName = name == "tia_read_source" ? StringArg(args, "block_name") : Convert.ToString(IndexProperty(item, name == "tia_import_blocks_batch" ? "block_name" : "name"));
                    string group = Convert.ToString(IndexProperty(item, "group_path")).Trim('/');
                    string source = Convert.ToString(IndexProperty(item, name == "tia_import_blocks_batch" ? "index_xml" : "source"));
                    if (source.Length == 0) continue;
                    string kind = name != "tia_import_blocks_batch" && Convert.ToString(IndexProperty(item, "extension")).TrimStart('.').Equals("udt", StringComparison.OrdinalIgnoreCase) ? "plc.type" : "plc.block";
                    var matches = projectIndex.Select(kind, "", "", "").Where(e => e.owner == owner && e.name.Equals(itemName, StringComparison.OrdinalIgnoreCase)
                        && e.group.Trim('/') == group && (StringArg(args, "unit_name").Length == 0 || e.unit_name == StringArg(args, "unit_name"))).ToArray();
                    if (matches.Length == 1) snapshots[matches[0].id] = name == "tia_import_blocks_batch" ? source : ContentIndexer.WrapSource(matches[0].name, projectIndex.Load().portal_version, source);
                }
            }
            if (name == "tia_read_tag_table")
            {
                string xml = Convert.ToString(IndexProperty(data, "xml"));
                if (xml.Length > 0)
                {
                    string owner = _session.SelectPlc(StringArg(args, "plc_name")).DisplayName;
                    var matches = projectIndex.Select("plc.tags", "", "", "").Where(e => e.owner == owner && e.name.Equals(StringArg(args, "table_name"), StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (matches.Length == 1) snapshots[matches[0].id] = xml;
                }
            }
            if (name == "tia_read_hmi_text_list")
            {
                string xml = Convert.ToString(IndexProperty(data, "xml"));
                string owner = Convert.ToString(IndexProperty(data, "hmi"));
                string tlName = Convert.ToString(IndexProperty(data, "name"));
                if (xml.Length > 0 && owner.Length > 0 && tlName.Length > 0)
                {
                    var match = projectIndex.Select("hmi.text_lists", "", "", "").SingleOrDefault(e => e.owner == owner && e.name.Equals(tlName, StringComparison.OrdinalIgnoreCase));
                    if (match != null) snapshots[match.id] = xml;
                }
            }
            if (name == "tia_read_hmi_configuration")
            {
                var items = IndexProperty(data, "objects") as IEnumerable;
                string owner = Convert.ToString(IndexProperty(data, "hmi"));
                if (items != null) foreach (object item in items)
                {
                    string xml = Convert.ToString(IndexProperty(item, "xml")); if (xml.Length == 0) continue;
                    string kind = "hmi." + Convert.ToString(IndexProperty(item, "kind")), itemName = Convert.ToString(IndexProperty(item, "name")), group = Convert.ToString(IndexProperty(item, "group_path"));
                    var match = projectIndex.Select(kind, "", "", "").SingleOrDefault(e => e.owner == owner && e.group == group && e.name == itemName);
                    if (match != null) snapshots[match.id] = xml;
                }
            }
            if (snapshots.Count > 0) projectIndex.Hydrate(projectIndex.Select("", "", "", "").Where(e => snapshots.ContainsKey(e.id)), e => snapshots[e.id], snapshots.Count, true);
        }
        private static Dictionary<string, object> IndexInteger(string description, int min, int max)
        { return Dict("type", "integer", "minimum", min, "maximum", max, "description", description); }
        private object[] ProjectIndexDefinitions()
        {
            var project = StringProperty("博途项目完整路径；多项目时必须指定。索引保存在工程旁 .tia-source-index/项目文件名/，随工程迁移。");
            var kind = StringProperty("对象类别前缀：plc.block/plc.type/plc.tags/hmi.screens/hmi.tag_tables/hmi.text_lists/hmi./hardware；省略时全部。");
            var owner = StringProperty("PLC/HMI 所属设备名称关键字，用于限制查询和内容读取。");
            var group = StringProperty("组路径前缀，避免对无关组建立内容索引。");
            return new object[] {
                Tool("tia_build_project_index", "建立可迁移的通用工程索引。默认只扫描目录，不导出全部源码；detail_level=content 分批读取选中 PLC/UDT/变量表/HMI/硬件内容，提取结构及显式引用并保留本地源快照。max_objects 限制每批，默认内容仅建立 PLC 层，HMI/硬件按需或 include_all_content=true；重复调用继续未完成部分；单对象失败记录后继续。不会编译、保存或修改博途工程。", ObjectSchema(
                    Property("project_path", project), Property("detail_level", Dict("type", "string", "enum", new[] { "catalog", "content" })),
                    Property("kind_filter", kind), Property("owner_filter", owner), Property("group_prefix", group), Property("name_filter", StringProperty("对象名称关键字")),
                    Property("include_all_content", BooleanProperty("默认false；内容默认优先 PLC/DB/UDT/变量表。true 包含 HMI/硬件，或用 kind_filter 指定阶段。")),
                    Property("max_objects", IndexInteger("内容每批上限，默认25", 1, 1000)), Property("refresh_catalog", BooleanProperty("默认false；重新扫描目录，并将原内容标记待刷新，以覆盖博途手动修改。")),
                    Property("time_budget_ms", IndexInteger("每批软时间预算，默认15000；在单个对象读取完成后检查，不强行中断 Openness 调用。", 100, 60000)),
                    Property("force", BooleanProperty("默认false；true重新读取已完成或失败对象，可重试编译后恢复可导出的块。")), Property("include_hardware", BooleanProperty("目录扫描包含硬件结构，默认true"))), false, false, true),
                Tool("tia_get_project_tree", "获取分页工程索引树和每个对象的精确读取参数、内容覆盖率及过期状态；默认不读取对象源码。首次无索引时自动建立目录。已有索引可在不连接博途时读取。", ObjectSchema(
                    Property("project_path", project), Property("kind_filter", kind), Property("owner_filter", owner), Property("group_prefix", group), Property("name_filter", StringProperty("对象名称关键字")),
                    Property("offset", IndexInteger("默认0", 0, 50000)), Property("limit", IndexInteger("默认100", 1, 500))), true, false, true),
                Tool("tia_find_references", "在已经索引的内容中查找符号、块或变量的显式引用及证据位置；返回覆盖率，未索引或失败对象不意味着没有引用。不会推断间接地址最终值。", ObjectSchema(new[] { "symbol" },
                    Property("project_path", project), Property("symbol", StringProperty("完整符号或前缀，如 DB_Actuators.Z13")), Property("offset", IndexInteger("默认0", 0, int.MaxValue)), Property("limit", IndexInteger("默认100", 1, 500))), true, false, true),
                Tool("tia_get_task_context", "开始 PLC/GRAPH/HMI/组态修改前优先调用：输入任务关键词，返回相关对象的精简结构、引用证据与 next_read 精确定位。默认按需读取最多8个候选对象建立内容索引，不把整份 XML 传给 Agent。命中基于关键词和显式引用，不代表机器功能推理已完成；修改前仍须读取当前目标。", ObjectSchema(new[] { "query" },
                    Property("project_path", project), Property("query", StringProperty("对象、符号或任务关键词，如 Z13 气缸 页面 映射")), Property("kind_filter", kind), Property("owner_filter", owner), Property("group_prefix", group),
                    Property("hydrate", BooleanProperty("默认true；false只查现有索引，无需连接博途")), Property("force", BooleanProperty("默认false；true刷新本次候选的已完成或失败内容")),
                    Property("max_objects", IndexInteger("按需读取数量，默认8", 1, 100)), Property("limit", IndexInteger("直接命中对象上限，默认6，另返回有限的显式关联对象", 1, 25)), Property("facts_per_object", IndexInteger("每对象摘要条数，默认6", 1, 20))), false, false, true)
            };
        }
    }
}
