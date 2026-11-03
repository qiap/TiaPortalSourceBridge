using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Diagnostics;
using TiaPortalSourceBridge.Openness;

namespace TiaPortalSourceBridge.Indexing
{
    internal sealed partial class ProjectIndexStore
    {
        internal readonly string Root;
        private readonly string projectFile, version, currentProjectPath;
        private long catalogStamp;
        private IndexCatalog catalog;
        private readonly Dictionary<string, IndexContent> memory = new Dictionary<string, IndexContent>();
        private string CatalogFile { get { return Path.Combine(Root, "catalog.json"); } }
        private string DirtyFile { get { return Path.Combine(Root, "dirty.json"); } }
        internal bool Exists { get { return File.Exists(CatalogFile); } }
        internal ProjectIndexStore(string projectPath, string version)
        {
            string full = Path.GetFullPath(projectPath); currentProjectPath = full; projectFile = Path.GetFileName(full); this.version = version;
            Root = Path.Combine(Path.GetDirectoryName(full), ".tia-source-index", projectFile);
        }
        internal IDisposable Lock()
        {
            Directory.CreateDirectory(Root);
            return new FileStream(Path.Combine(Root, "write.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        private static void Atomic(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)); string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllText(temp, text, new UTF8Encoding(false)); if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        internal IndexCatalog Load()
        {
            if (catalog != null && File.GetLastWriteTimeUtc(CatalogFile).Ticks == catalogStamp) return catalog;
            if (!Exists) throw new InvalidOperationException("尚未建立工程索引，请调用 tia_build_project_index。");
            catalog = IndexJson.Serializer().Deserialize<IndexCatalog>(File.ReadAllText(CatalogFile));
            if (catalog.schema != 1 || catalog.project_file != projectFile || !string.Equals(catalog.portal_version, version, StringComparison.OrdinalIgnoreCase))
            { catalog = null; throw new InvalidOperationException("索引格式或工程标识不匹配，请重新建立目录。"); }
            catalogStamp = File.GetLastWriteTimeUtc(CatalogFile).Ticks; return catalog;
        }
        private void Save() { Atomic(CatalogFile, IndexJson.Serializer().Serialize(catalog)); catalogStamp = File.GetLastWriteTimeUtc(CatalogFile).Ticks; }
        private IndexDirty Dirty()
        { OperationMetrics.Count("index_dirty_reads"); return File.Exists(DirtyFile) ? IndexJson.Serializer().Deserialize<IndexDirty>(File.ReadAllText(DirtyFile)) : new IndexDirty(); }
        private static bool Affected(IndexDirty dirty, IndexEntry e) { return dirty.all || dirty.names.Any(n => string.Equals(n, e.name, StringComparison.OrdinalIgnoreCase)); }
        internal void MarkDirty(string[] names, string reason)
        {
            if (!Exists) return;
            using (Lock())
            {
                var d = Dirty(); d.all |= names == null || names.Length == 0;
                d.names = d.names.Concat(names ?? new string[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToList(); d.reason = reason; d.revision = Guid.NewGuid().ToString("N");
                Atomic(DirtyFile, IndexJson.Serializer().Serialize(d));
            }
        }
        private void ConsumeDirty()
        {
            if (!File.Exists(DirtyFile)) return;
            var d = Dirty();
            foreach (var e in Load().objects.Where(e => Affected(d, e))) if (e.state == "ready" || e.state == "error") e.state = "stale";
            Save(); if (File.Exists(DirtyFile)) File.Delete(DirtyFile);
        }
        // Scanning TIA and exporting content never hold the filesystem writer lock.
        internal object BuildCatalog(Func<List<string>, List<IndexEntry>> scan, bool forceAllStale = false, bool reconcileOnly = false, bool includeHardware = true)
        {
            long beforeScan = Exists ? File.GetLastWriteTimeUtc(CatalogFile).Ticks : 0;
            string dirtyBeforeScan = DirtyRevision();
            var warnings = new List<string>(); var rows = scan(warnings);
            if (rows.Count > 50000 || rows.Select(e => e.id).Distinct().Count() != rows.Count) throw new InvalidOperationException("工程目录过大或对象身份重复。");
            using (Lock())
            {
                if ((Exists ? File.GetLastWriteTimeUtc(CatalogFile).Ticks : 0) != beforeScan || DirtyRevision() != dirtyBeforeScan)
                    throw new InvalidOperationException("目录扫描期间索引发生变化，请重试目录核对。");
                var old = new Dictionary<string, IndexEntry>();
                if (Exists) try { old = Load().objects.ToDictionary(e => e.id); }
                catch (Exception ex) { warnings.Add("Previous index rebuilt: " + ex.Message); catalog = null; }
                var dirty = Dirty();
                // A failed/omitted subsystem scan is not evidence that its objects were deleted.
                foreach (var prior in old.Values)
                    if ((!includeHardware && prior.kind == "hardware" || warnings.Any(w => w.StartsWith("Hardware catalog:") && prior.kind == "hardware"
                        || w.StartsWith("HMI catalog " + prior.owner + ":") && prior.kind.StartsWith("hmi."))) && !rows.Any(e => e.id == prior.id)) rows.Add(prior);
                foreach (var row in rows)
                {
                    IndexEntry prior; if (!old.TryGetValue(row.id, out prior)) continue;
                    row.content_hash = prior.content_hash; row.indexed_utc = prior.indexed_utc; row.error = prior.error; row.fingerprint = prior.fingerprint;
                    bool stamped = !string.IsNullOrEmpty(row.source_stamp) && row.source_stamp == prior.source_stamp;
                    bool metadata = row.language == prior.language && row.number == prior.number && row.object_namespace == prior.object_namespace;
                    bool reuse = !forceAllStale && !Affected(dirty, row) && metadata && (stamped || reconcileOnly && string.IsNullOrEmpty(row.source_stamp));
                    row.state = prior.content_hash.Length == 0 ? "catalog_only" : reuse ? prior.state : "stale";
                    row.freshness = stamped && reuse ? "source_stamp_matched" : "unverified_snapshot";
                }
                catalog = new IndexCatalog { project_file = projectFile, portal_version = version, built_utc = DateTime.UtcNow.ToString("o"), objects = rows, warnings = warnings,
                    added = rows.Count(e => !old.ContainsKey(e.id)), removed = old.Keys.Except(rows.Select(e => e.id)).Count(), retained = rows.Count(e => old.ContainsKey(e.id)) };
                Save(); if (File.Exists(DirtyFile)) File.Delete(DirtyFile);
                OperationMetrics.Count("index_catalog_builds");
            }
            return Status();
        }
        private string DirtyRevision() { return File.Exists(DirtyFile) ? File.ReadAllText(DirtyFile) : ""; }
        internal object Hydrate(IEnumerable<IndexEntry> selected, Func<IndexEntry, string> read, int limit, bool force, int timeBudgetMs = 15000)
        {
            if (limit < 1 || limit > 1000) throw new ArgumentException("max_objects 必须在1..1000。");
            int updated = 0, reused = 0, failed = 0, conflicts = 0, attempted = 0;
            var errors = new List<object>(); var ids = selected.Select(e => e.id).Distinct().ToArray();
            var timer = Stopwatch.StartNew(); string stop = "completed";
            using (Lock()) { Load(); ConsumeDirty(); }
            var hydrationSnapshot = Snapshot();
            foreach (string id in ids)
            {
                var e = Load().objects.FirstOrDefault(n => n.id == id); if (e == null) continue;
                if ((State(e, hydrationSnapshot) == "ready" || e.state == "error") && !force) { reused++; continue; }
                if (attempted >= limit) { stop = "batch_limit"; break; }
                if (attempted > 0 && timer.ElapsedMilliseconds >= timeBudgetMs) { stop = "time_budget"; break; }
                string before = IndexJson.Serializer().Serialize(e), dirtyBefore = DirtyRevision();
                string source = null; IndexContent content = null; Exception failure = null;
                attempted++;
                try
                {
                    using (OperationMetrics.Measure("index_export")) source = read(e);
                    using (OperationMetrics.Measure("index_parse")) content = e.kind == "hardware" ? LiveIndexSource.Hardware(source) : ContentIndexer.Parse(source);
                    if (e.kind != "hardware" && (content.object_name != e.name || !string.Equals(content.source_version, version, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException("读取内容的名称或 TIA 版本与索引对象不匹配。");
                }
                catch (Exception ex) { failure = ex; }
                using (Lock())
                {
                    var current = Load().objects.FirstOrDefault(n => n.id == id);
                    if (current == null || IndexJson.Serializer().Serialize(current) != before || DirtyRevision() != dirtyBefore)
                    { conflicts++; continue; } // Concurrent write/refresh wins; never publish an older export as fresh.
                    if (failure == null)
                    {
                        Atomic(Path.Combine(Root, "content", content.hash + ".json"), IndexJson.Serializer().Serialize(content));
                        string sourcePath = Path.Combine(Root, "source", content.hash + (e.kind == "hardware" ? ".json" : ".xml"));
                        if (!File.Exists(sourcePath)) Atomic(sourcePath, source);
                        memory[content.hash] = content; current.content_hash = content.hash; current.indexed_utc = DateTime.UtcNow.ToString("o");
                        current.state = "ready"; current.freshness = "live_read_snapshot"; current.error = ""; current.fingerprint = content.hash;
                        updated++; OperationMetrics.Count("index_objects_parsed");
                    }
                    else { current.state = "error"; current.error = failure.Message; failed++; errors.Add(new { id = e.id, name = e.name, error = failure.Message }); }
                    Save(); // Each completed export is a resumable checkpoint; the slow call happened outside this lock.
                }
            }
            return new { updated = updated, reused = reused, failed = failed, conflicts = conflicts, stop_reason = stop, elapsed_ms = timer.ElapsedMilliseconds, errors = errors.ToArray(), status = Status() };
        }
        internal IEnumerable<IndexEntry> Select(string kind, string owner, string group, string name)
        {
            return Load().objects.Where(e => (kind.Length == 0 || e.kind.StartsWith(kind, StringComparison.OrdinalIgnoreCase))
                && (owner.Length == 0 || e.owner.IndexOf(owner, StringComparison.OrdinalIgnoreCase) >= 0)
                && (group.Length == 0 || e.group == group || e.group.StartsWith(group.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase))
                && (name.Length == 0 || e.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0));
        }
        internal IndexContent Content(IndexEntry e)
        {
            if (e.content_hash.Length == 0) return null;
            if (!HashHexRegex.IsMatch(e.content_hash)) throw new InvalidOperationException("非法索引内容指纹。");
            if (!File.Exists(Path.Combine(Root, "content", e.content_hash + ".json"))) return null;
            IndexContent c;
            if (!memory.TryGetValue(e.content_hash, out c))
            {
                string file = Path.Combine(Root, "content", e.content_hash + ".json");
                if (!File.Exists(file)) return null;
                c = IndexJson.Serializer().Deserialize<IndexContent>(File.ReadAllText(file)); memory[e.content_hash] = c;
            }
            if (c.parser != ContentIndexer.ParserVersion && e.kind != "hardware")
            {
                string source = Path.Combine(Root, "source", e.content_hash + ".xml");
                if (File.Exists(source))
                {
                    c = ContentIndexer.Parse(File.ReadAllText(source)); memory[e.content_hash] = c;
                    using (Lock()) Atomic(Path.Combine(Root, "content", e.content_hash + ".json"), IndexJson.Serializer().Serialize(c));
                    OperationMetrics.Count("index_local_reparses");
                }
            }
            return c;
        }
        // A query observes one dirty snapshot; no persistent cache hides external edits.
        private sealed class QueryState
        {
            internal readonly bool All;
            internal readonly HashSet<string> Names;
            internal readonly Dictionary<string, string> States = new Dictionary<string, string>();
            internal readonly Dictionary<string, bool> Files = new Dictionary<string, bool>();
            internal QueryState(IndexDirty dirty)
            { All = dirty.all; Names = new HashSet<string>(dirty.names, StringComparer.OrdinalIgnoreCase); }
        }
        private QueryState Snapshot() { return new QueryState(Dirty()); }
        internal object Status() { return Status(Snapshot()); }
        private object Status(QueryState snapshot)
        {
            var c = Load(); int ready = 0, errors = 0;
            foreach (var e in c.objects)
            { if (State(e, snapshot) == "ready") ready++; if (e.state == "error") errors++; }
            return new { schema = c.schema, project_file = c.project_file, portal_version = c.portal_version, catalog_built_utc = c.built_utc,
                total = c.objects.Count, ready = ready, catalog_changes = new { added = c.added, removed = c.removed, retained = c.retained },
                layers = c.objects.GroupBy(e => e.kind.StartsWith("plc.") ? "plc" : e.kind.StartsWith("hmi.") ? "hmi" : "hardware").Select(g => new { layer = g.Key, total = g.Count(), ready = g.Count(e => State(e, snapshot) == "ready"), errors = g.Count(e => e.state == "error") }).ToArray(),
                pending = c.objects.Count - ready, errors = errors,
                coverage = new { complete = false, excluded = new[] { "hmi.integrated_connections", "non_exportable_hmi_controls", "unified_hmi" }, empty_result = "not_found_in_indexed_scope", absence_proven = false,
                    states = new { catalog_only = "尚未索引内容", error = "读取或解析失败，详见对象 error", unsupported = "接口不支持或本索引未覆盖", not_found = "仅在当前索引范围未找到，不证明对象不存在" },
                    note = "HMI 连接结果不包含集成连接；空列表不代表无连接。" },
                warnings = c.warnings.ToArray(), freshness = "snapshot coverage is not live freshness; catalog reconciliation alone does not verify content; manual TIA edits require refresh",
                recommended_tools = version == "V21" ? new[] { "tia_get_task_context", "tia_get_project_tree", "tia_find_references", "tia_query_cross_references" } : new[] { "tia_get_task_context", "tia_get_project_tree", "tia_find_references" } };
        }
        private string State(IndexEntry e, QueryState snapshot)
        {
            string state;
            if (snapshot.States.TryGetValue(e.id, out state)) return state;
            state = e.state;
            if (snapshot.All || snapshot.Names.Contains(e.name)) state = "stale";
            else if (state == "ready")
            {
                bool exists;
                if (!snapshot.Files.TryGetValue(e.content_hash, out exists))
                {
                    OperationMetrics.Count("index_content_file_checks");
                    exists = File.Exists(Path.Combine(Root, "content", e.content_hash + ".json"));
                    snapshot.Files[e.content_hash] = exists;
                }
                if (!exists) state = "missing_content";
            }
            snapshot.States[e.id] = state;
            return state;
        }
        private object Locator(IndexEntry e, QueryState snapshot)
        {
            string read = e.kind.StartsWith("hmi.") ? "tia_read_hmi_configuration" : e.kind == "hardware" ? "tia_read_hardware_configuration" : e.kind == "plc.tags" ? "tia_read_tag_table" : e.kind == "plc.type" ? "tia_read_source" : "tia_read_block";
            if (e.kind == "plc.block" && (e.language == "SCL" || e.language == "STL")) read = "tia_read_source";
            var args = new Dictionary<string, object> { { "project_path", currentProjectPath }, { "response_mode", "compact" } };
            if (e.kind.StartsWith("hmi.")) { args["hmi_name"] = e.owner; args["kind"] = e.kind.Substring(4); args["name_filter"] = e.name; args["group_prefix"] = e.group; args["include_xml"] = true; }
            else if (e.kind == "hardware") { args["path_prefix"] = e.group; args["include_attributes"] = true; }
            else { if (!string.IsNullOrEmpty(e.unit_name)) args["unit_name"] = e.unit_name; args["plc_name"] = e.owner; args["group_path"] = e.group.Length == 0 ? "/" : e.group; args[e.kind == "plc.tags" ? "table_name" : "block_name"] = e.name; }
            return new { id = e.id, kind = e.kind, owner = e.owner, unit_name = e.unit_name, object_namespace = e.object_namespace, freshness = e.freshness, group_path = e.group, name = e.name, number = e.number, language = e.language,
                state = State(e, snapshot), indexed_utc = e.indexed_utc, content_hash = e.content_hash, error = e.error, preferred_v21_tool = version == "V21" && (e.kind == "plc.type" || e.kind == "plc.block" && e.language != "GRAPH") ? "tia_read_document" : read, next_read = new { tool = read, arguments = args } };
        }
        internal object Tree(string kind, string owner, string group, string name, int offset, int limit)
        {
            var snapshot = Snapshot();
            var all = Select(kind, owner, group, name).OrderBy(e => e.owner).ThenBy(e => e.unit_name).ThenBy(e => e.kind).ThenBy(e => e.group).ThenBy(e => e.name).ToArray();
            var page = all.Skip(offset).Take(limit).ToArray();
            return new { status = Status(snapshot), total_matches = all.Length, offset = offset, next_offset = offset + page.Length < all.Length ? (int?)(offset + page.Length) : null,
                tree = string.Join("\n", page.Select(e => e.owner + "/" + e.kind + "/" + (e.group.Length > 0 ? e.group + "/" : "") + e.name + " [" + State(e, snapshot) + "]")),
                objects = page.Select(e => Locator(e, snapshot)).ToArray() };
        }
        private static readonly Regex TermTokenRegex = new Regex(@"[A-Za-z_][A-Za-z0-9_]*|[0-9]+|[\p{IsCJKUnifiedIdeographs}]{2,}", RegexOptions.Compiled);
        private static readonly Regex AlphaNumGroupRegex = new Regex(@"[A-Za-z_]+([0-9]+)", RegexOptions.Compiled);
        private static readonly Regex CjkOnlyRegex = new Regex(@"^[\p{IsCJKUnifiedIdeographs}]+$", RegexOptions.Compiled);
        private static readonly Regex AlphaNumWeightRegex = new Regex(@"[A-Za-z_]+[0-9]+", RegexOptions.Compiled);
        private static readonly Regex HashHexRegex = new Regex("^[a-f0-9]{64}$", RegexOptions.Compiled);
        private static readonly Regex StartNonEmptyRegex = new Regex(@"start=[^ ]", RegexOptions.Compiled);

        private struct WeightedTerm
        {
            internal readonly string Text;
            internal readonly int Weight;
            internal WeightedTerm(string text)
            {
                Text = text;
                Weight = Math.Min(text.Length, 8) * (AlphaNumWeightRegex.IsMatch(text) ? 4 : 1);
            }
        }

        private static WeightedTerm[] Terms(string query)
        {
            var terms = TermTokenRegex.Matches(query).Cast<Match>().Select(m => m.Value).ToList();
            terms.AddRange(AlphaNumGroupRegex.Matches(query).Cast<Match>().Select(m => m.Groups[1].Value));
            foreach (string chinese in terms.Where(t => CjkOnlyRegex.IsMatch(t)).ToArray())
                for (int i = 0; i + 1 < chinese.Length; i++) terms.Add(chinese.Substring(i, 2));
            foreach (var pair in new[] { new[] { "气缸", "cylinder" }, new[] { "复位", "reset" }, new[] { "自动", "auto" }, new[] { "报警", "alarm" }, new[] { "映射", "mapping" }, new[] { "画面", "hmi.screens" }, new[] { "流程", "graph" } })
                if (query.Contains(pair[0])) terms.Add(pair[1]);
            return terms.Distinct(StringComparer.OrdinalIgnoreCase).Select(t => new WeightedTerm(t)).ToArray();
        }
        private static int Score(string text, WeightedTerm[] terms)
        {
            int sum = 0;
            for (int i = 0; i < terms.Length; i++)
            {
                if (text.IndexOf(terms[i].Text, StringComparison.OrdinalIgnoreCase) >= 0)
                    sum += terms[i].Weight;
            }
            return sum;
        }
        internal IndexEntry[] Ranked(string query, int limit, string kind = "", string owner = "", string group = "")
        {
            if (query.Length > 1000) throw new ArgumentException("query 最多1000字符。");
            EnsureLookup();
            var terms = Terms(query); if (terms.Length == 0) throw new ArgumentException("query 需要有效的对象名称、符号或任务关键词。");
            string key = PlanKey(query, limit, kind, owner, group); string[] cached;
            if (rankedPlans.TryGetValue(key, out cached)) return cached.Select(id => entriesById[id]).ToArray();
            var ranked = Select(kind, owner, group, "").Select(e => {
                var c = Lookup(e); int score = 4 * Score(e.name + " " + e.object_namespace + " " + e.unit_name + " " + e.group + " " + e.kind + " " + e.language, terms) + (c == null ? 0 : Score(c.search_text ?? "", terms));
                return new { entry = e, score = score };
            }).Where(r => r.score > 0).OrderByDescending(r => r.score).ThenBy(r => r.entry.id).Take(limit).Select(r => r.entry).ToArray();
            Remember(rankedPlans, key, ranked); return ranked;
        }
        private object Summary(IndexEntry e, string query, int facts, QueryState snapshot)
        {
            var c = Content(e); var terms = Terms(query);
            return new { identity = Locator(e, snapshot), facts = c == null ? new object[0] : c.facts.OrderByDescending(f => Score(f.text, terms) + (f.kind == "member_structure" ? 2 : 0) + (f.kind == "hmi_tag" && (f.text.Contains("address=%") || StartNonEmptyRegex.IsMatch(f.text)) ? 3 : 0)).Take(facts).Select(f => (object)new { kind = f.kind, location = f.location, text = f.text.Length > 600 ? f.text.Substring(0, 600) : f.text, truncated = f.text.Length > 600 }).ToArray(),
                references = c == null ? new object[0] : c.references.OrderByDescending(r => Score(r.target, terms)).Take(facts).Select(r => (object)new { relation = r.relation, target = r.target.Length > 500 ? r.target.Substring(0, 500) : r.target, location = r.location, evidence = r.evidence }).ToArray(),
                io_mappings = c == null ? new object[0] : c.mappings.Take(facts).Select(m => (object)new { source = m.source, target = m.target, location = m.location, source_addresses = ResolveAddresses(e, m.source, snapshot), target_addresses = ResolveAddresses(e, m.target, snapshot) }).ToArray(),
                total_facts = c == null ? 0 : c.facts.Count, total_references = c == null ? 0 : c.references.Count,
                warnings = c == null ? new[] { "Content not indexed; hydrate this scope or live-read the target." } : c.warnings.ToArray() };
        }
        internal object Context(string query, int limit, int facts, string kind = "", string owner = "", string group = "")
        {
            var snapshot = Snapshot();
            var selected = Ranked(query, limit, kind, owner, group).ToList(); var direct = selected.Select(e => e.id).ToArray();
            var terms = Terms(query); var related = new Dictionary<string, IndexEntry>(); var relevance = new Dictionary<string, int>();
            string key = PlanKey(query, limit, kind, owner, group); string[] cached;
            if (contextPlans.TryGetValue(key, out cached)) selected = cached.Select(id => entriesById[id]).ToList();
            else {
            foreach (var e in selected.ToArray())
            {
                var c = Content(e); if (c == null) continue;
                foreach (var target in c.references.Select(r => r.target).Distinct().Take(250))
                {
                    var candidates = ResolveDefinitions(target).Where(n => n.id != e.id);
                    if (e.kind.StartsWith("plc.")) candidates = candidates.Where(n => n.owner == e.owner);
                    foreach (var n in candidates.Where(n => !selected.Contains(n)))
                    {
                        related[n.id] = n; int prior; relevance.TryGetValue(n.id, out prior);
                        var detail = Lookup(n);
                        int score = 4 * Score(n.name + " " + n.group + " " + n.kind, terms) + (detail == null ? 0 : Score(detail.search_text ?? "", terms)) + 2 * Score(target, terms);
                        relevance[n.id] = Math.Max(prior, score);
                    }
                }
            }
            selected.AddRange(related.Values.OrderByDescending(n => relevance[n.id]).ThenBy(n => n.id).Take(limit));
            Remember(contextPlans, key, selected);
            }
            return new { status = Status(snapshot), query = query, match_basis = "keywords and explicit references; relevance is not proof of machine function",
                direct_match_ids = direct, objects = selected.Select(e => Summary(e, query, facts, snapshot)).ToArray(),
                next_step = "Read exact live targets using next_read before modifying. If context is incomplete, build content for the selected kind/owner/group/name; do not assume missing references mean no references." };
        }
        internal object References(string symbol, int offset, int limit)
        {
            if (string.IsNullOrWhiteSpace(symbol)) throw new ArgumentException("symbol 不能为空。");
            symbol = ContentIndexer.NormalizeSymbol(symbol);
            var snapshot = Snapshot();
            EnsureLookup(); List<ReferenceHit> matches;
            if (!referenceLookup.TryGetValue(symbol, out matches)) matches = new List<ReferenceHit>();
            var byId = Load().objects.ToDictionary(e => e.id);
            var rows = matches.Where(h => byId.ContainsKey(h.id)).ToArray();
            return new { status = Status(snapshot), symbol = symbol, total_matches = rows.Length, offset = offset,
                next_offset = offset < rows.Length && limit < rows.Length - offset ? (int?)(offset + limit) : null, references = rows.Skip(offset).Take(limit).Select(h => new { source = Locator(byId[h.id], snapshot), reference = h.reference }).ToArray(),
                coverage_note = "Only indexed snapshots are searched; pending/error objects and dynamic expressions can contain additional references." };
        }
    }
}
