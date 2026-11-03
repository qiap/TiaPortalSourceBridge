using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TiaPortalSourceBridge.Indexing
{
    internal sealed class IndexLookupDocument
    {
        public string id { get; set; }
        public string hash { get; set; }
        public string search_text { get; set; }
        public List<string> definitions { get; set; }
        public List<IndexBinding> bindings { get; set; }
        public List<IndexReference> references { get; set; }
    }
    internal sealed class IndexLookupFile
    {
        public string parser { get; set; }
        public List<IndexLookupDocument> documents { get; set; }
    }
    internal sealed partial class ProjectIndexStore
    {
        private sealed class ReferenceHit { internal string id; internal IndexReference reference; }
        private Dictionary<string, IndexLookupDocument> lookupDocuments;
        private readonly Dictionary<string, List<ReferenceHit>> referenceLookup = new Dictionary<string, List<ReferenceHit>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<string>> definitionLookup = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, IndexEntry> entriesById;
        private long planCatalogStamp;
        private readonly Dictionary<string, string[]> rankedPlans = new Dictionary<string, string[]>();
        private readonly Dictionary<string, string[]> contextPlans = new Dictionary<string, string[]>();
        private static string PlanKey(string query, int limit, string kind, string owner, string group)
        { return IndexJson.Serializer().Serialize(new object[] { query, limit, kind, owner, group }); }
        private void Remember(Dictionary<string, string[]> plans, string key, IEnumerable<IndexEntry> entries)
        { if (plans.Count >= 32) plans.Clear(); plans[key] = entries.Select(e => e.id).ToArray(); }


        // Dots inside quoted component names are literal characters, never hierarchy separators.
        internal static IEnumerable<string> SymbolPrefixes(string symbol)
        {
            bool quoted = false; int bracket = 0;
            for (int i = 0; i < symbol.Length; i++)
            {
                char ch = symbol[i];
                if (ch == '"') { quoted = !quoted; continue; }
                if (quoted) continue;
                if ((ch == '.' || ch == '[') && bracket == 0 && i > 0) yield return symbol.Substring(0, i);
                if (ch == '[') bracket++;
                if (ch == ']') bracket--;
            }
            yield return symbol;
        }
        private IndexLookupDocument Lookup(IndexEntry e)
        { IndexLookupDocument d; return lookupDocuments.TryGetValue(e.id, out d) ? d : null; }
        private void AddLookup(IndexLookupDocument d)
        {
            foreach (string symbol in d.definitions.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                HashSet<string> ids;
                if (!definitionLookup.TryGetValue(symbol, out ids)) definitionLookup[symbol] = ids = new HashSet<string>();
                ids.Add(d.id);
            }
            foreach (var r in d.references)
                foreach (string prefix in SymbolPrefixes(r.target).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    List<ReferenceHit> hits;
                    if (!referenceLookup.TryGetValue(prefix, out hits)) referenceLookup[prefix] = hits = new List<ReferenceHit>();
                    hits.Add(new ReferenceHit { id = d.id, reference = r });
                }
        }
        private void RemoveLookup(IndexLookupDocument d)
        {
            foreach (string symbol in d.definitions.Distinct(StringComparer.OrdinalIgnoreCase))
            { HashSet<string> ids; if (definitionLookup.TryGetValue(symbol, out ids)) { ids.Remove(d.id); if (ids.Count == 0) definitionLookup.Remove(symbol); } }
            foreach (string prefix in d.references.SelectMany(r => SymbolPrefixes(r.target)).Distinct(StringComparer.OrdinalIgnoreCase))
            { List<ReferenceHit> hits; if (referenceLookup.TryGetValue(prefix, out hits)) { hits.RemoveAll(h => h.id == d.id); if (hits.Count == 0) referenceLookup.Remove(prefix); } }
            lookupDocuments.Remove(d.id);
        }
        private IEnumerable<IndexEntry> ResolveDefinitions(string target)
        {
            var found = new HashSet<string>();
            foreach (string prefix in SymbolPrefixes(target))
            {
                HashSet<string> ids;
                if (!definitionLookup.TryGetValue(prefix, out ids)) continue;
                foreach (string id in ids) if (found.Add(id) && entriesById.ContainsKey(id)) yield return entriesById[id];
            }
        }
        private object[] ResolveAddresses(IndexEntry entry, string symbol, QueryState snapshot)
        {
            return ResolveDefinitions(symbol).Where(e => e.kind == "plc.tags" && e.owner == entry.owner && e.unit_name == entry.unit_name)
                .SelectMany(e => Lookup(e).bindings.Where(b => b.symbol == symbol || ContentIndexer.ComponentName(b.symbol) == symbol)
                    .Select(b => (object)new { address = b.address, tag = b.symbol, table = e.name, table_state = State(e, snapshot), indexed_utc = e.indexed_utc })).ToArray();
        }
        private void EnsureLookup()
        {
            string path = Path.Combine(Root, "query-cache.json"); bool changed = false;
            if (lookupDocuments == null)
            {
                lookupDocuments = new Dictionary<string, IndexLookupDocument>();
                try
                {
                    if (File.Exists(path))
                    {
                        var saved = IndexJson.Serializer().Deserialize<IndexLookupFile>(File.ReadAllText(path));
                        if (saved.parser == ContentIndexer.ParserVersion)
                            foreach (var d in saved.documents) { lookupDocuments[d.id] = d; AddLookup(d); }
                    }
                }
                catch { lookupDocuments.Clear(); referenceLookup.Clear(); definitionLookup.Clear(); } // Derived cache is disposable.
            }
            entriesById = Load().objects.ToDictionary(e => e.id);
            if (planCatalogStamp != catalogStamp) { rankedPlans.Clear(); contextPlans.Clear(); planCatalogStamp = catalogStamp; }
            foreach (var d in lookupDocuments.Values.ToArray())
            {
                IndexEntry e;
                if (!entriesById.TryGetValue(d.id, out e) || e.content_hash != d.hash || d.hash.Length > 0 && !File.Exists(Path.Combine(Root, "content", d.hash + ".json")))
                { RemoveLookup(d); changed = true; }
            }
            foreach (var e in entriesById.Values)
            {
                if (lookupDocuments.ContainsKey(e.id)) continue;
                var c = Content(e);
                // Catalog-only names also resolve dependencies without requiring a content export.
                var d = new IndexLookupDocument { id = e.id, hash = e.content_hash, search_text = c == null ? "" : c.search_text,
                    definitions = new List<string> { e.name, ContentIndexer.ComponentName(e.name) }, bindings = c == null ? new List<IndexBinding>() : c.bindings, references = c == null ? new List<IndexReference>() : c.references };
                if (c != null) d.definitions.AddRange(c.definitions.Where(s => !string.IsNullOrEmpty(s)).SelectMany(s => new[] { s, ContentIndexer.ComponentName(s) }));
                lookupDocuments[e.id] = d; AddLookup(d); changed = true;
            }
            if (changed)
            {
                rankedPlans.Clear(); contextPlans.Clear();
                // A stale concurrent cache is harmless: every load validates IDs and hashes against the catalog.
                try { using (Lock()) Atomic(path, IndexJson.Serializer().Serialize(new IndexLookupFile { parser = ContentIndexer.ParserVersion, documents = lookupDocuments.Values.ToList() })); }
                catch (IOException) { } // Query results must not depend on acquiring the optional cache writer lock.
            }
        }
    }
}
