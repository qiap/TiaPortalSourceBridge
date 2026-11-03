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
        public Dictionary<string, object> Write(string objectsJson, string backupRoot, bool skipUnchanged = true, bool dryRun = true)
        {
            var checks = new List<object>(); var warnings = new List<string>(); HmiDocument[] documents;
            try
            {
                documents = ParseDocuments(objectsJson, Api.LoadedPortalVersion);
                Preflight(documents, checks, warnings);
            }
            catch (Exception ex)
            {
                checks.Add(new { state = "unverified", code = "preflight_read_failed", error = ex.ToString() });
                documents = new HmiDocument[0];
            }
            var preflight = new { passed = checks.Count == 0, issues = checks.ToArray(), warnings = warnings.ToArray(),
                type_evidence = "exact DataType/HmiDataType pairs exported from this HMI; not a complete supported-type catalog",
                plc_symbol_scope = "existing exact binding/type pairs only; new PLC bindings require a native resolver and are blocked as unverified" };
            if (dryRun || checks.Count > 0)
                return new Dictionary<string, object> { { "saved", false }, { "dry_run", dryRun }, { "preflight", preflight },
                    { "success_count", 0 }, { "changed_count", 0 }, { "failure_count", checks.Count }, { "skipped_count", documents.Length },
                    { "mutation_started", false }, { "items", new object[0] }, { "verification_status", "not_run" } };
            Dictionary<string, object> result;
            try { result = WriteCore(objectsJson, backupRoot, skipUnchanged); }
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
        private void Preflight(HmiDocument[] documents, List<object> checks, List<string> warnings)
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
            HmiValidation.CheckTagEvidence(documents, existing, issue, warnings);
            var oldTags = existing.Values.SelectMany(HmiValidation.Tags).ToArray();
            var finalTables = new Dictionary<string, string>(existing, StringComparer.OrdinalIgnoreCase);
            foreach (var d in documents.Where(d => d.kind == "tag_tables")) finalTables[d.name] = d.xml;
            var names = new HashSet<string>(finalTables.Values.SelectMany(HmiValidation.Tags).Select(t => HmiValidation.Value(t, "AttributeList/Name")), StringComparer.OrdinalIgnoreCase);
            var screens = documents.Where(d => d.kind != "tag_tables").Select(d => d.xml).ToList();
            var removed = new HashSet<string>(oldTags.Select(t => HmiValidation.Value(t, "AttributeList/Name")).Where(n => !names.Contains(n)), StringComparer.OrdinalIgnoreCase);
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
        private object[] VerifyImported(HmiDocument entry)
        {
            var differences = HmiValidation.Compare(entry.xml, ReadOneXml(entry.kind, entry.name, entry.group_path));
            if (differences.Length > 0) throw new HmiVerificationException(differences);
            return differences;
        }
    }
}
