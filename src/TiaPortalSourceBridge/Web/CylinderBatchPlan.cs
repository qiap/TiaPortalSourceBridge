using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using TiaPortalSourceBridge.Indexing;
using TiaPortalSourceBridge.Mcp;
using TiaPortalSourceBridge.Openness;

namespace TiaPortalSourceBridge.Web
{
    // Build every row against a progressively updated in-memory engineering source.
    // The returned batch still guards the real original snapshot, and imports each
    // affected block once. This class never connects to or mutates TIA Portal.
    internal static class CylinderBatchPlan
    {
        internal const int MaxRows = 20;
        private static string Text(Dictionary<string, object> row, string key) { return CylinderWritePipeline.Text(row, key); }
        private static Dictionary<string, object> Map(Dictionary<string, object> row, string key)
        {
            object value;
            var result = row != null && row.TryGetValue(key, out value) ? value as Dictionary<string, object> : null;
            if (result == null) throw new InvalidOperationException("缺少批量气缸参数：" + key);
            return result;
        }
        private static object Canonical(object value)
        {
            var map = value as Dictionary<string, object>;
            if (map != null) return new SortedDictionary<string, object>(map.ToDictionary(p => p.Key, p => Canonical(p.Value)), StringComparer.Ordinal);
            var array = value as object[]; return array == null ? value : array.Select(Canonical).ToArray();
        }
        private static string Template(Dictionary<string, object> request)
        {
            var values = new Dictionary<string, object>();
            foreach (string key in new[] { "project_path", "plc_name", "host", "source_hash", "id", "sample_id", "sample_network", "rules" }) {
                object value; values[key] = request.TryGetValue(key, out value) ? Canonical(value) : null;
            }
            return CylinderWritePipeline.Json().Serialize(Canonical(values));
        }
        private static void ValidateRows(IoSnapshot snapshot, IndexEntry host, IList<Dictionary<string, object>> requests)
        {
            if (snapshot == null || host == null || snapshot.cylinderBlocks == null || !snapshot.cylinderBlocks.Any(b => b.id == host.id))
                throw new InvalidOperationException("批量宿主不属于当前工程快照。");
            if (requests == null || requests.Count == 0 || requests.Count > MaxRows)
                throw new InvalidOperationException("每批请填写 1～" + MaxRows + " 个气缸。");
            if (requests.Any(r => r == null)) throw new InvalidOperationException("批量列表包含空行。");
            string template = Template(requests[0]);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var instances = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var alarmIndices = new HashSet<int>(); var points = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < requests.Count; index++) {
                var request = requests[index]; string prefix = "第 " + (index + 1) + " 行：";
                if (Template(request) != template) throw new InvalidOperationException(prefix + "批量操作必须使用同一工程、PLC、宿主、样板和参数规则。");
                string name = Text(request, "cylinder_name").Trim(), instance = Text(request, "instance_name");
                if (name.Length == 0 || !names.Add(name)) throw new InvalidOperationException(prefix + "气缸名称为空或在本批重复：" + name);
                if (!Regex.IsMatch(instance, @"^[\p{L}_][\p{L}\p{N}_]{0,63}$") || !instances.Add(instance))
                    throw new InvalidOperationException(prefix + "程序实例名无效或在本批重复：" + instance);
                if (Text(request, "host").Length > 0 && Text(request, "host") != host.id)
                    throw new InvalidOperationException(prefix + "宿主与当前选择不一致。");
                if (Text(request, "plc_name").Length > 0 && !string.Equals(Text(request, "plc_name"), snapshot.plc_name, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(prefix + "PLC 与当前快照不一致。");
                if (Text(request, "project_path").Length > 0 && !string.Equals(Path.GetFullPath(Text(request, "project_path")), Path.GetFullPath(snapshot.project_path), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(prefix + "工程与当前快照不一致。");
                if (Text(request, "source_hash").Length > 0 && Text(request, "source_hash") != host.content_hash)
                    throw new InvalidOperationException(prefix + "原宿主内容已经改变，请重新读取。");
                var rules = Map(request, "rules"); var bindings = Map(request, "bindings");
                if (rules.Values.Any(v => Convert.ToString(v) == "index")) {
                    int value;
                    if (!Regex.IsMatch(Text(request, "alarm_index"), @"^\d{1,9}$") || !int.TryParse(Text(request, "alarm_index"), out value) || !alarmIndices.Add(value))
                        throw new InvalidOperationException(prefix + "报警索引无效或在本批重复：" + Text(request, "alarm_index"));
                }
                foreach (var binding in bindings) {
                    string id = Convert.ToString(binding.Value);
                    var matches = snapshot.rows.Where(r => r.id == id && r.unit == host.unit_name).ToArray();
                    if (matches.Length != 1) throw new InvalidOperationException(prefix + "所选点位不存在或不唯一：" + binding.Key);
                    string address = IoTable.Address(matches[0].address);
                    if (!Regex.IsMatch(address, @"^[IQ]\d+\.[0-7]$") || !points.Add((matches[0].unit ?? "") + "\n" + address))
                        throw new InvalidOperationException(prefix + "点位无效或本批重复占用：" + address);
                }
            }
        }
        private static IoRow Clone(IoRow row)
        {
            return new IoRow { id = row.id, table = row.table, group = row.group, unit = row.unit, name = row.name, address = row.address,
                area = row.area, data_type = row.data_type, comment = row.comment, value = row.value, mapping_group = row.mapping_group,
                mapping_fc = row.mapping_fc, carrier = row.carrier, origin = row.origin,
                references = row.references.Select(r => new IoReference { block = r.block, group = r.group, access = r.access,
                    location = r.location, evidence = r.evidence, source = r.source, state = r.state }).ToList() };
        }
        private static IoSnapshot VirtualSnapshot(IoSnapshot original, Func<IndexEntry, string> source)
        {
            var snapshot = new IoSnapshot { project_path = original.project_path, plc_name = original.plc_name,
                cylinderBlocks = original.cylinderBlocks, cylinderProgramsTotal = original.cylinderProgramsTotal, cylinderProgramsRead = original.cylinderProgramsRead,
                tables_total = original.tables_total, tables_read = original.tables_read, read_started_utc = original.read_started_utc,
                read_completed_utc = original.read_completed_utc, reference_source = original.reference_source, reference_coverage = original.reference_coverage,
                warnings = new List<string>(original.warnings),
                rows = original.rows.Where(r => r.mapping_fc == null && r.origin == null && r.area != "DB" && r.area != "LOCAL").Select(Clone).ToList() };
            IoMapping.Expand(snapshot, snapshot.cylinderBlocks, source);
            IoDeclarations.Add(snapshot, snapshot.cylinderBlocks, source);
            return snapshot;
        }
        internal static CylinderApplyPlan Build(IoSnapshot snapshot, IndexEntry host, Func<IndexEntry, string> source,
            IList<Dictionary<string, object>> requests)
        {
            ValidateRows(snapshot, host, requests);
            if (source == null) throw new InvalidOperationException("缺少工程源码读取器。");
            var original = snapshot.cylinderBlocks.ToDictionary(b => b.id, source);
            var working = new Dictionary<string, string>(original);
            var changed = new HashSet<string>(); var summaries = new List<object>();
            Func<IndexEntry, string> virtualSource = entry => working[entry.id];
            // Resolve incoming point IDs through immutable physical endpoints. A prior
            // row's rename must not accidentally change a later row's selected address.
            for (int row = 0; row < requests.Count; row++) {
                try {
                    var current = VirtualSnapshot(snapshot, virtualSource);
                    var request = new Dictionary<string, object>(requests[row]);
                    var bindings = new Dictionary<string, object>();
                    foreach (var binding in Map(request, "bindings")) {
                        var selected = snapshot.rows.Single(r => r.id == Convert.ToString(binding.Value) && r.unit == host.unit_name);
                        var matches = current.rows.Where(r => r.unit == selected.unit && r.area == selected.area &&
                            IoTable.Address(r.address) == IoTable.Address(selected.address) && r.data_type == selected.data_type &&
                            r.table == selected.table && r.group == selected.group && r.mapping_fc == selected.mapping_fc && r.mapping_group == selected.mapping_group &&
                            r.origin == selected.origin).ToArray();
                        if (matches.Length != 1) throw new InvalidOperationException("虚拟工程中点位定位不唯一或已变化：" + selected.address);
                        bindings[binding.Key] = matches[0].id;
                    }
                    request["bindings"] = bindings;
                    var next = CylinderApplyPlan.Build(current, host, virtualSource, request);
                    foreach (var item in next.Items) {
                        string xml = Text(item, "xml"), name = CylinderRecoveryPolicy.Name(xml), group = PathTools.NormalizeGroupPath(Text(item, "group_path"));
                        var matches = snapshot.cylinderBlocks.Where(b => b.unit_name == host.unit_name && string.Equals(b.name, name, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(PathTools.NormalizeGroupPath(b.group), group, StringComparison.OrdinalIgnoreCase)).ToArray();
                        if (matches.Length != 1) throw new InvalidOperationException("无法唯一合并生成块：" + name);
                        working[matches[0].id] = xml; changed.Add(matches[0].id);
                    }
                    summaries.Add(next.Summary);
                } catch (Exception ex) { throw new InvalidOperationException("第 " + (row + 1) + " 行气缸未通过检查，本批尚未写入：" + ex.Message, ex); }
            }
            var plan = new CylinderApplyPlan();
            foreach (var entry in snapshot.cylinderBlocks.Where(b => changed.Contains(b.id)).OrderBy(b => b.id == host.id ? 2 : b.language == "DB" ? 0 : 1).ThenBy(b => b.name, StringComparer.Ordinal))
                plan.Items.Add(new Dictionary<string, object> { { "xml", working[entry.id] }, { "group_path", string.IsNullOrEmpty(entry.group) ? "/" : entry.group },
                    { "expected_content_signature", XmlBatch.ContentSignature(original[entry.id]) },
                    { "expected_recovery_signature", XmlBatch.RecoverySignature(original[entry.id]) } });
            var details = summaries.Select(CylinderWritePipeline.Map).ToArray();
            plan.Summary = new { batch = true, count = requests.Count, host = host.name, blocks = plan.Items.Count, cylinders = details,
                renames = details.SelectMany(d => CylinderWritePipeline.Rows(d, "renames")).ToArray(),
                shared_alarm_reset = details.Any(d => CylinderWritePipeline.True(d, "shared_alarm_reset")),
                note = "本批气缸先完成全部校验，合并为每个程序块一次写入；统一备份、编译、回读和保存。保留样板公共条件，不下载 PLC。" };
            return plan;
        }
    }
}
