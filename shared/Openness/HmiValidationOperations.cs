using System;
using System.Collections.Generic;
using System.Linq;
using TiaPortalSourceBridge.Mcp;

namespace TiaPortalSourceBridge.Openness
{
    internal sealed partial class HmiOperations
    {
        internal const string ConnectionCoverageNote = "本结果不包含集成连接；Connections 空列表不代表 HMI 没有 PLC 连接。";
        internal static object Coverage()
        {
            return new { complete = false, excluded = new[] { "integrated_connections", "AlarmView_and_other_non_exportable_controls" },
                empty_result = "not_found_in_readable_scope", absence_proven = false, note = ConnectionCoverageNote };
        }
        public Dictionary<string, object> Write(string objectsJson, string backupRoot, bool skipUnchanged = true, bool dryRun = true, string connectionPlcsJson = null)
        {
            using (BeginReadScope()) return WriteScoped(objectsJson, backupRoot, skipUnchanged, dryRun, connectionPlcsJson);
        }
        private Dictionary<string, object> WriteScoped(string objectsJson, string backupRoot, bool skipUnchanged, bool dryRun, string connectionPlcsJson)
        {
            var checks = new List<object>(); var warnings = new List<string>(); HmiDocument[] documents;
            try
            {
                documents = ParseDocuments(objectsJson, Api.LoadedPortalVersion);
                using (OperationMetrics.Measure("hmi_preflight")) Preflight(documents, checks, warnings, connectionPlcsJson);
            }
            catch (Exception ex)
            {
                checks.Add(new { state = "unverified", code = "preflight_read_failed", error = ex.ToString() });
                documents = new HmiDocument[0];
            }
            var preflight = new { passed = checks.Count == 0, issues = checks.ToArray(), warnings = warnings.ToArray(),
                type_evidence = "exact DataType/HmiDataType pairs exported from this HMI; not a complete supported-type catalog",
                plc_symbol_scope = "existing exact binding/type pairs; new elementary DB bindings checked against live PLC exports using an explicitly supplied connection_plcs_json map. Integrated connection partner is caller-verified, not inferred or proven by this API; unsupported layouts remain blocked" };
            if (dryRun || checks.Count > 0)
                return new Dictionary<string, object> { { "saved", false }, { "dry_run", dryRun }, { "preflight", preflight },
                    { "success_count", 0 }, { "changed_count", 0 }, { "failure_count", checks.Count }, { "skipped_count", documents.Length },
                    { "mutation_started", false }, { "items", new object[0] }, { "verification_status", "not_run" } };
            Dictionary<string, object> result;
            try { result = WriteCore(documents, backupRoot, skipUnchanged); }
            catch (Exception ex)
            {
                result = new Dictionary<string, object> { { "saved", false }, { "success_count", 0 }, { "failure_count", 1 },
                    { "changed_count", null }, { "items", new object[0] }, { "native_error", ex.ToString() },
                    { "mutation_state", "unknown" }, { "recovery", "保留备份；重新连接并回读对象后才能判断实际修改和恢复范围。" } };
            }
            result["preflight"] = preflight;
            result["verification_status"] = Convert.ToInt32(result["failure_count"]) == 0 ? "requested_fields_verified" : "failed_or_incomplete";
            result["save_state"] = "not_saved_by_this_operation";
            result["connection_state"] = "not_probed_after_operation";
            result["rollback_state"] = "not_verified";
            result["count_scope"] = "counts describe observations during import, not final persisted or rolled-back state";
            return result;
        }
        private void Preflight(HmiDocument[] documents, List<object> checks, List<string> warnings, string connectionPlcsJson)
        {
            Action<string, string, string> issue = (code, name, detail) => checks.Add(new { state = code.Contains("unverified") ? "unverified" : "invalid", code = code, name = name, detail = detail });
            var catalogs = new Dictionary<string, Tuple<string, object>[]>();
            foreach (var d in documents)
            {
                foreach (string error in HmiValidation.Validate(d.xml)) issue("invalid_xml_contract", d.name, error);
                var spec = Spec(d.kind); var root = Root(spec);
                if (root == null || Api.Get(root, spec[3], false) == null) { issue("unsupported_collection", d.name, d.kind); continue; }
                Tuple<string, object>[] catalog;
                if (!catalogs.TryGetValue(d.kind, out catalog)) { catalog = Enumerate(spec, root, "").ToArray(); catalogs[d.kind] = catalog; }
                var matches = catalog.Where(e => string.Equals(Api.GetString(e.Item2, "Name"), d.name, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length > 1 || matches.Length == 1 && d.group_path.Length > 0 && !string.Equals(matches[0].Item1, d.group_path, StringComparison.OrdinalIgnoreCase))
                    issue("name_conflict", d.name, "同名对象存在于多个位置或目标组不一致；整批尚未写入。");
            }
            if (checks.Count > 0) return;
            bool needsTags = documents.Any(d => d.kind == "tag_tables" || HmiValidation.Parse(d.xml).SelectNodes("//LinkList/Tag").Count > 0);
            if (!needsTags) return;
            var existing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var tagSpec = Spec("tag_tables");
            foreach (var entry in Enumerate(tagSpec, Root(tagSpec), "")) existing.Add(Api.GetString(entry.Item2, "Name"), Export(entry.Item2));
            HmiValidation.CheckTagEvidence(documents, existing, issue, warnings, BindingResolver(connectionPlcsJson, existing, warnings));
            var finalTables = new Dictionary<string, string>(existing, StringComparer.OrdinalIgnoreCase);
            foreach (var d in documents.Where(d => d.kind == "tag_tables")) finalTables[d.name] = d.xml;
            var names = new HashSet<string>(finalTables.Values.SelectMany(HmiValidation.TagPaths).Select(t => t.Key), StringComparer.OrdinalIgnoreCase);
            var screens = documents.Where(d => d.kind != "tag_tables").Select(d => d.xml).ToList();
            var removed = new HashSet<string>(existing.Values.SelectMany(HmiValidation.TagPaths).Select(t => t.Key).Where(n => !names.Contains(n)), StringComparer.OrdinalIgnoreCase);
            if (removed.Count > 0)
                foreach (var spec in Kinds.Where(k => k[0].Contains("screen")))
                    foreach (var entry in Enumerate(spec, Root(spec), ""))
                        if (!documents.Any(d => d.kind == spec[0] && d.name == Api.GetString(entry.Item2, "Name"))) screens.Add(Export(entry.Item2));
            foreach (string screen in screens) foreach (System.Xml.XmlNode link in HmiValidation.Parse(screen).SelectNodes("//LinkList/Tag"))
            {
                string name = HmiValidation.Value(link, "Name");
                if (!names.Contains(name)) issue("screen_binding_unverified", name, "页面引用的变量未在写入后的 HMI 变量表中找到。");
            }
        }
        private IEnumerable<object> PlcBlocks(object group)
        {
            foreach (object block in Api.Items(Api.Get(group, "Blocks", false))) yield return block;
            foreach (object child in Api.Items(Api.Get(group, "Groups", false)))
                foreach (object block in PlcBlocks(child)) yield return block;
        }
        private Func<System.Xml.XmlNode, string> BindingResolver(string mapJson, Dictionary<string, string> existing, List<string> warnings)
        {
            if (string.IsNullOrWhiteSpace(mapJson)) return null;
            var map = new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(mapJson) as Dictionary<string, object>;
            if (map == null || map.Count == 0) throw new ArgumentException("connection_plcs_json 必须是连接名到 PLC 精确名称的非空对象。");
            var connections = new HashSet<string>(existing.Values.SelectMany(HmiValidation.Tags).Select(t => HmiValidation.Value(t, "LinkList/Connection/Name")), StringComparer.Ordinal);
            var contexts = new Dictionary<string, PlcContext>(StringComparer.Ordinal);
            foreach (var pair in map)
            {
                if (!connections.Contains(pair.Key) || !(pair.Value is string) || string.IsNullOrWhiteSpace((string)pair.Value))
                    throw new ArgumentException("连接未见于当前 HMI 变量表或 PLC 名无效：" + pair.Key);
                contexts.Add(pair.Key, session.SelectPlc((string)pair.Value));
                warnings.Add("调用方已核对连接伙伴：" + pair.Key + " -> " + contexts[pair.Key].DisplayName + "；本 API 不证明集成连接伙伴或运行时通信。");
            }
            var catalogs = new Dictionary<PlcContext, object[]>();
            var layouts = new Dictionary<object, HmiPlcBindingValidation.Layout>();
            return tag =>
            {
                try
                {
                    string connection = HmiValidation.Value(tag, "LinkList/Connection/Name"); PlcContext plc;
                    if (!contexts.TryGetValue(connection, out plc)) return "未提供此连接已核对的 PLC 伙伴：" + connection;
                    object[] catalog;
                    if (!catalogs.TryGetValue(plc, out catalog)) catalogs[plc] = catalog = PlcBlocks(Api.Get(plc.Software, "BlockGroup", true)).Where(b => b.GetType().Name == "GlobalDB").ToArray();
                    int? number = HmiPlcBindingValidation.DbNumber(tag); string symbol = HmiPlcBindingValidation.DbName(tag);
                    var matches = catalog.Where(b => number.HasValue ? Convert.ToInt32(Api.Get(b, "Number", true)) == number.Value : symbol.StartsWith(Api.GetString(b, "Name") + ".", StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (matches.Length != 1) return "不能唯一定位 PLC 全局 DB；不猜测或使用其他 PLC。";
                    HmiPlcBindingValidation.Layout layout;
                    if (!layouts.TryGetValue(matches[0], out layout)) layouts[matches[0]] = layout = HmiPlcBindingValidation.ReadLayout(Export(matches[0]));
                    string error = HmiPlcBindingValidation.Check(tag, layout);
                    if (error == null) warnings.Add("已核验新增绑定：" + HmiValidation.Value(tag, "AttributeList/Name") + " -> " + plc.DisplayName + " / " + layout.Name + " (" + HmiValidation.Binding(tag) + ")");
                    return error;
                }
                catch (Exception ex) { return "PLC 端核验未完成：" + ex.Message; }
            };
        }
        private object[] VerifyImported(HmiDocument entry)
        {
            var differences = HmiValidation.Compare(entry.xml, ReadOneXml(entry.kind, entry.name, entry.group_path));
            if (differences.Length > 0) throw new HmiVerificationException(differences);
            return differences;
        }
    }
}
