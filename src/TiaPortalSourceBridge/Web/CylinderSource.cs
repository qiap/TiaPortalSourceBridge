using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using TiaPortalSourceBridge.Indexing;

namespace TiaPortalSourceBridge.Web
{
    internal static class CylinderSource
    {
        private static XmlDocument Parse(string xml)
        {
            var doc = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings {
                XmlResolver = null, DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 32 * 1024 * 1024
            })) doc.Load(reader);
            return doc;
        }

        internal static object Read(IoSnapshot snapshot, IndexEntry host, Func<IndexEntry, string> source, Func<IndexEntry, IndexContent> content)
        {
            string xml = source(host);
            var doc = Parse(xml);
            var referenced = new HashSet<string>(doc.SelectNodes("//*[local-name()='Symbol']/*[local-name()='Component'][1]/@Name")
                .Cast<XmlAttribute>().Select(a => a.Value), StringComparer.OrdinalIgnoreCase);
            var dbs = snapshot.cylinderBlocks.Where(b => b.unit_name == host.unit_name && b.language == "DB").ToArray();
            var members = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in dbs.Where(b => referenced.Contains(b.name)).GroupBy(b => b.name, StringComparer.OrdinalIgnoreCase)) {
                if (group.Count() != 1) continue;
                var fields = Parse(source(group.Single())).SelectNodes("//*[local-name()='SW.Blocks.GlobalDB']/*[local-name()='AttributeList']/*[local-name()='Interface']/*[local-name()='Sections']/*[local-name()='Section' and @Name='Static']/*[local-name()='Member']").Cast<XmlElement>();
                members[group.Key] = fields.ToDictionary(m => m.GetAttribute("Name"), m => m.GetAttribute("Datatype"), StringComparer.OrdinalIgnoreCase);
            }
            var dbNames = dbs.Select(b => b.name).Distinct().OrderByDescending(n => n.Length).ToArray();
            var usage = new CylinderIoUsage(snapshot, source);
            // Also flag indexed writes to the donor's data/alarm DBs from other blocks.
            var writes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var prefixes = members.Keys.Select(n => ContentIndexer.ComponentName(n) + ".").ToArray();
            foreach (var block in snapshot.cylinderBlocks.Where(b => b.unit_name == host.unit_name && b.language != "DB")) {
                var parsed = content(block);
                if (parsed == null) continue;
                foreach (var reference in parsed.references.Where(r => r.relation == "writes" || r.relation == "reads_writes")) {
                    var symbol = ContentIndexer.NormalizeSymbol(reference.target);
                    if (prefixes.Any(p => symbol.StartsWith(p, StringComparison.OrdinalIgnoreCase))) writes[symbol] = block.name;
                }
            }
            var points = snapshot.rows.Where(r => r.unit == host.unit_name && string.Equals(r.data_type, "Bool", StringComparison.OrdinalIgnoreCase) &&
                (r.area == "I" || r.area == "Q")).Select(r => {
                    string symbol = "\"" + r.name.Replace("\"", "\"\"") + "\"";
                    if (r.mapping_fc != null) {
                        string db = dbNames.FirstOrDefault(n => r.name.StartsWith(n + ".", StringComparison.Ordinal));
                        if (db == null) return null;
                        symbol = "\"" + db.Replace("\"", "\"\"") + "\"" + r.name.Substring(db.Length);
                    }
                    // Direct bit transfers may use raw PLC tags; generate against the
                    // mapped DB member so the mapper does not overwrite the new output.
                    string preferred = usage.PreferredSymbol(r);
                    if (preferred.Length > 0) {
                        string db = dbNames.FirstOrDefault(n => preferred.StartsWith(ContentIndexer.ComponentName(n) + ".",StringComparison.Ordinal));
                        if (db != null) symbol = "\"" + db.Replace("\"", "\"\"") + "\"" + preferred.Substring(ContentIndexer.ComponentName(db).Length);
                    }
                    var state = usage.For(r);
                    return new { r.id, r.address, r.area, r.comment, symbol, r.group, r.mapping_group, r.table,
                        bank = state.Bank, availability = state.Availability, usage_reason = state.Reason, usage_blocks = state.Blocks,
                        aliases = usage.Aliases(r),
                        writes = r.references.Where(x => x.access == "writes" || x.access == "reads_writes" ||
                            (x.access ?? "").IndexOf("write", StringComparison.OrdinalIgnoreCase) >= 0).Select(x => x.block).Distinct().Take(8).ToArray() };
                }).Where(p => p != null).ToArray();
            return new { xml, hash = host.content_hash, unit = host.unit_name, host = host.name, members, points, writes,
                usage_coverage = new { programs_total = snapshot.cylinderProgramsTotal, programs_read = snapshot.cylinderProgramsRead, complete = usage.Complete, note = usage.Note },
                project = snapshot.project_path, plc = snapshot.plc_name, readAt = snapshot.read_completed_utc };
        }
    }
}
