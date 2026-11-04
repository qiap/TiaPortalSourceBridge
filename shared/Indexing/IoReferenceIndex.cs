using System;
using System.Linq;

namespace TiaPortalSourceBridge.Indexing
{
    internal sealed partial class ProjectIndexStore
    {
        // A user-requested reread must export again even when TIA metadata is unchanged.
        internal void RefreshIoContent(string owner)
        {
            using (Lock()) {
                foreach (var entry in Load().objects.Where(e => e.kind == "plc.block" && string.Equals(e.owner, owner, StringComparison.OrdinalIgnoreCase)))
                    entry.state = "stale";
                Save();
            }
        }
        internal IndexEntry[] IoBlocks(string owner)
        {
            var snapshot = Snapshot();
            return Load().objects.Where(e => e.kind == "plc.block" && string.Equals(owner,e.owner,StringComparison.OrdinalIgnoreCase)
                && State(e,snapshot) == "ready").ToArray();
        }
        internal string IoSource(IndexEntry entry)
        {
            if (string.IsNullOrEmpty(entry.content_hash) || !HashHexRegex.IsMatch(entry.content_hash)) throw new System.IO.IOException("未找到源码：" + entry.name);
            return System.IO.File.ReadAllText(System.IO.Path.Combine(Root,"source",entry.content_hash + ".xml"));
        }
        // One pass over the selected PLC; do not mix same-named tags from other PLCs.
        internal object IoReferenceSnapshot(string owner)
        {
            var snapshot = Snapshot();
            var entries = Load().objects.Where(e => e.kind == "plc.block" && string.Equals(e.owner, owner, StringComparison.OrdinalIgnoreCase)).ToArray();
            return new {
                total = entries.Length,
                ready = entries.Count(e => State(e, snapshot) == "ready"),
                errors = entries.Where(e => State(e, snapshot) != "ready").Select(e => new { name = e.name, state = State(e, snapshot), error = e.error }).ToArray(),
                references = entries.SelectMany(e => {
                    var content = Content(e);
                    return (content == null ? new IndexReference[0] : content.references.ToArray())
                        .Where(r => new[] { "reads", "writes", "reads_writes", "symbol_reference", "token_reference", "address" }.Contains(r.relation))
                        .Select(r => new { block = e.name, group = e.group, unit = e.unit_name, state = State(e, snapshot), indexed_utc = e.indexed_utc,
                            target = r.target, access = r.relation, location = r.location, evidence = r.evidence });
                }).ToArray()
            };
        }
    }
}
