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
    internal sealed class ProjectIndexStore
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
                d.names = d.names.Concat(names ?? new string[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToList(); d.reason = reason;
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
        internal object BuildCatalog(Func<List<string>, List<IndexEntry>> scan)
        {
            using (Lock())
            {
                var warnings = new List<string>(); var rows = scan(warnings);
                if (rows.Count > 50000 || rows.Select(e => e.id).Distinct().Count() != rows.Count) throw new InvalidOperationException("工程目录过大或对象身份重复。");
                var old = new Dictionary<string, IndexEntry>();
                if (Exists) try { old = Load().objects.ToDictionary(e => e.id); }
                catch (Exception ex) { warnings.Add("Previous index rebuilt: " + ex.Message); catalog = null; }
                foreach (var row in rows)
                {
                    IndexEntry prior;
                    if (!old.TryGetValue(row.id, out prior)) continue;
                    row.content_hash = prior.content_hash; row.indexed_utc = prior.indexed_utc; row.error = prior.error; row.fingerprint = prior.fingerprint;
                    // Catalog metadata cannot prove that unsaved TIA UI edits did not change a block.
                    row.state = prior.content_hash.Length == 0 ? "catalog_only" : "stale";
                }
                catalog = new IndexCatalog { project_file = projectFile, portal_version = version, built_utc = DateTime.UtcNow.ToString("o"), objects = rows, warnings = warnings };
                Save(); if (File.Exists(DirtyFile)) File.Delete(DirtyFile);
                OperationMetrics.Count("index_catalog_builds"); return Status();
            }
        }
        internal object Hydrate(IEnumerable<IndexEntry> selected, Func<IndexEntry, string> read, int limit, bool force, int timeBudgetMs = 15000)
        {
            if (limit < 1 || limit > 1000) throw new ArgumentException("max_objects 必须在1..1000。");
            int updated = 0, reused = 0, failed = 0; var errors = new List<object>(); var ids = selected.Select(e => e.id).Distinct().ToArray();
            var timer = Stopwatch.StartNew(); string stop = "completed";
            using (Lock())
            {
                ConsumeDirty();
                var snapshot = Snapshot();
                var current = Load().objects.ToDictionary(e => e.id);
                foreach (string id in ids)
                {
                    IndexEntry e; if (!current.TryGetValue(id, out e)) continue;
                    if ((State(e, snapshot) == "ready" || e.state == "error") && !force) { reused++; continue; }
                    if (updated + failed >= limit) { stop = "batch_limit"; break; }
                    if (updated + failed > 0 && timer.ElapsedMilliseconds >= timeBudgetMs) { stop = "time_budget"; break; }
                    try
                    {
                        string source;
                        using (OperationMetrics.Measure("index_export")) source = read(e);
                        IndexContent content;
                        using (OperationMetrics.Measure("index_parse")) content = e.kind == "hardware" ? LiveIndexSource.Hardware(source) : ContentIndexer.Parse(source);
                        if (e.kind != "hardware" && (content.object_name != e.name || !string.Equals(content.source_version, version, StringComparison.OrdinalIgnoreCase)))
                            throw new InvalidOperationException("读取内容的名称或 TIA 版本与索引对象不匹配。");
                        string path = Path.Combine(Root, "content", content.hash + ".json");
                        if (!File.Exists(path)) Atomic(path, IndexJson.Serializer().Serialize(content));
                        // XML/JSON snapshots are local evidence, never used as the current write baseline.
                        string sourcePath = Path.Combine(Root, "source", content.hash + (e.kind == "hardware" ? ".json" : ".xml"));
                        if (!File.Exists(sourcePath)) Atomic(sourcePath, source);
                        memory[content.hash] = content; e.content_hash = content.hash; e.indexed_utc = DateTime.UtcNow.ToString("o");
                        e.state = "ready"; e.error = ""; e.fingerprint = content.hash; updated++; OperationMetrics.Count("index_objects_parsed");
                    }
                    catch (Exception ex) { e.state = "error"; e.error = ex.Message; failed++; errors.Add(new { id = e.id, name = e.name, error = e.error }); }
                    if ((updated + failed) % 5 == 0) Save(); // Bounded checkpoints avoid rewriting a large catalog for every block.
                }
                if (updated + failed > 0) Save();
            }
            return new { updated = updated, reused = reused, failed = failed, stop_reason = stop, elapsed_ms = timer.ElapsedMilliseconds, errors = errors.ToArray(), status = Status() };
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
            IndexContent c;
            if (!memory.TryGetValue(e.content_hash, out c))
            {
                string file = Path.Combine(Root, "content", e.content_hash + ".json");
                if (!File.Exists(file)) return null;
                c = IndexJson.Serializer().Deserialize<IndexContent>(File.ReadAllText(file)); memory[e.content_hash] = c;
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
                total = c.objects.Count, ready = ready,
                pending = c.objects.Count - ready, errors = errors,
                warnings = c.warnings.ToArray(), freshness = "snapshot; manual TIA edits require refresh; always live-read before writing",
                recommended_tools = new[] { "tia_get_task_context", "tia_get_project_tree", "tia_find_references" } };
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
            else { args["plc_name"] = e.owner; args["group_path"] = e.group.Length == 0 ? "/" : e.group; args[e.kind == "plc.tags" ? "table_name" : "block_name"] = e.name; }
            return new { id = e.id, kind = e.kind, owner = e.owner, group_path = e.group, name = e.name, number = e.number, language = e.language,
                state = State(e, snapshot), indexed_utc = e.indexed_utc, content_hash = e.content_hash, error = e.error, next_read = new { tool = read, arguments = args } };
        }
        internal object Tree(string kind, string owner, string group, string name, int offset, int limit)
        {
            var snapshot = Snapshot();
            var all = Select(kind, owner, group, name).OrderBy(e => e.owner).ThenBy(e => e.kind).ThenBy(e => e.group).ThenBy(e => e.name).ToArray();
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
            var terms = Terms(query); if (terms.Length == 0) throw new ArgumentException("query 需要有效的对象名称、符号或任务关键词。");
            return Select(kind, owner, group, "").Select(e => {
                var c = Content(e); int score = 4 * Score(e.name + " " + e.group + " " + e.kind + " " + e.language, terms) + (c == null ? 0 : Score(c.search_text ?? "", terms));
                return new { entry = e, score = score };
            }).Where(r => r.score > 0).OrderByDescending(r => r.score).ThenBy(r => r.entry.id).Take(limit).Select(r => r.entry).ToArray();
        }
        private object Summary(IndexEntry e, string query, int facts, QueryState snapshot)
        {
            var c = Content(e); var terms = Terms(query);
            return new { identity = Locator(e, snapshot), facts = c == null ? new object[0] : c.facts.OrderByDescending(f => Score(f.text, terms) + (f.kind == "member_structure" ? 2 : 0) + (f.kind == "hmi_tag" && (f.text.Contains("address=%") || StartNonEmptyRegex.IsMatch(f.text)) ? 3 : 0)).Take(facts).Select(f => (object)new { kind = f.kind, location = f.location, text = f.text.Length > 600 ? f.text.Substring(0, 600) : f.text, truncated = f.text.Length > 600 }).ToArray(),
                references = c == null ? new object[0] : c.references.OrderByDescending(r => Score(r.target, terms)).Take(facts).Select(r => (object)new { relation = r.relation, target = r.target.Length > 500 ? r.target.Substring(0, 500) : r.target, location = r.location, evidence = r.evidence }).ToArray(),
                total_facts = c == null ? 0 : c.facts.Count, total_references = c == null ? 0 : c.references.Count,
                warnings = c == null ? new[] { "Content not indexed; hydrate this scope or live-read the target." } : c.warnings.ToArray() };
        }
        internal object Context(string query, int limit, int facts, string kind = "", string owner = "", string group = "")
        {
            var snapshot = Snapshot();
            var selected = Ranked(query, limit, kind, owner, group).ToList(); var direct = selected.Select(e => e.id).ToArray();
            var terms = Terms(query); var related = new Dictionary<string, IndexEntry>(); var relevance = new Dictionary<string, int>();
            var declared = new Dictionary<string, List<IndexEntry>>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in Load().objects)
            {
                var content = Content(entry); if (content == null) continue;
                foreach (string symbol in content.definitions.Where(s => !string.IsNullOrEmpty(s)).Distinct())
                { List<IndexEntry> list; if (!declared.TryGetValue(symbol, out list)) declared[symbol] = list = new List<IndexEntry>(); list.Add(entry); }
            }
            foreach (var e in selected.ToArray())
            {
                var c = Content(e); if (c == null) continue;
                foreach (var target in c.references.Select(r => r.target).Distinct().Take(250))
                {
                    var candidates = Load().objects.Where(n => n.id != e.id && (target == n.name || target.StartsWith(n.name + ".", StringComparison.Ordinal)));
                    List<IndexEntry> definitions;
                    if (declared.TryGetValue(target, out definitions)) candidates = candidates.Concat(definitions.Where(n => n.owner == e.owner));
                    if (e.kind.StartsWith("plc.")) candidates = candidates.Where(n => n.owner == e.owner);
                    foreach (var n in candidates.Where(n => !selected.Contains(n)))
                    {
                        related[n.id] = n; int prior; relevance.TryGetValue(n.id, out prior);
                        var detail = Content(n);
                        int score = 4 * Score(n.name + " " + n.group + " " + n.kind, terms) + (detail == null ? 0 : Score(detail.search_text ?? "", terms)) + 2 * Score(target, terms);
                        relevance[n.id] = Math.Max(prior, score);
                    }
                }
            }
            selected.AddRange(related.Values.OrderByDescending(n => relevance[n.id]).ThenBy(n => n.id).Take(limit));
            return new { status = Status(snapshot), query = query, match_basis = "keywords and explicit references; relevance is not proof of machine function",
                direct_match_ids = direct, objects = selected.Select(e => Summary(e, query, facts, snapshot)).ToArray(),
                next_step = "Read exact live targets using next_read before modifying. If context is incomplete, build content for the selected kind/owner/group/name; do not assume missing references mean no references." };
        }
        internal object References(string symbol, int offset, int limit)
        {
            if (string.IsNullOrWhiteSpace(symbol)) throw new ArgumentException("symbol 不能为空。");
            var snapshot = Snapshot();
            Func<string, bool> match = s => string.Equals(s, symbol, StringComparison.OrdinalIgnoreCase) || s.StartsWith(symbol + ".", StringComparison.OrdinalIgnoreCase) || s.StartsWith(symbol + "[", StringComparison.OrdinalIgnoreCase);
            var rows = Load().objects.SelectMany(e => { var c = Content(e); return c == null ? new object[0] : c.references.Where(r => match(r.target)).Select(r => (object)new { source = Locator(e, snapshot), reference = r }).ToArray(); }).ToArray();
            return new { status = Status(snapshot), symbol = symbol, total_matches = rows.Length, offset = offset,
                next_offset = offset < rows.Length && limit < rows.Length - offset ? (int?)(offset + limit) : null, references = rows.Skip(offset).Take(limit).ToArray(),
                coverage_note = "Only indexed snapshots are searched; pending/error objects and dynamic expressions can contain additional references." };
        }
    }
}
