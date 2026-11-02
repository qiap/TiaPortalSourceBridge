using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TiaPortalSourceBridge.Mcp;
using TiaPortalSourceBridge.Openness;

namespace TiaPortalSourceBridge.Indexing
{
    internal sealed class LiveIndexSource
    {
        private readonly OpennessSession session;
        private Dictionary<string, Dictionary<string, object>> hardwareRowsByPath;
        private bool hardwareFullSnapshotUnavailable;
        private ReflectionApi Api { get { return session.Api; } }
        internal LiveIndexSource(OpennessSession session) { this.session = session; }
        internal List<IndexEntry> Catalog(bool hardware, List<string> warnings)
        {
            var rows = new List<IndexEntry>();
            foreach (string name in session.GetPlcDisplayNames())
            {
                var plc = session.SelectPlc(name); var ops = new BlockOperations(session, plc);
                foreach (var b in ops.GetCachedBlocks(true)) rows.Add(IndexEntry.Create("plc.block", name, b.GroupPath, b.Name, b.Language, Convert.ToString(b.Number)));
                foreach (var b in ops.GetCachedTypes(true)) rows.Add(IndexEntry.Create("plc.type", name, b.GroupPath, b.Name, "UDT"));
                CatalogTags(Api.Get(plc.Software, "TagTableGroup", false), "", name, rows);
            }
            foreach (var hmi in session.GetHmis())
            {
                try
                {
                    var read = new HmiOperations(session, hmi).Read("all", "", "", false, false);
                    foreach (object item in (object[])read["objects"])
                    {
                        var row = IndexJson.Map(item);
                        rows.Add(IndexEntry.Create("hmi." + IndexJson.Text(row, "kind"), hmi.DisplayName, IndexJson.Text(row, "group_path"), IndexJson.Text(row, "name"), "HMI"));
                    }
                }
                catch (Exception ex) { warnings.Add("HMI catalog " + hmi.DisplayName + ": " + ex.Message); }
            }
            if (hardware) try
            {
                var read = IndexJson.Map(new HardwareOperations(session).Read("", false));
                foreach (Dictionary<string, object> row in (object[])read["objects"])
                    rows.Add(IndexEntry.Create("hardware", "hardware", IndexJson.Text(row, "path"), IndexJson.Text(row, "name"), IndexJson.Text(row, "type")));
            }
            catch (Exception ex) { warnings.Add("Hardware catalog: " + ex.Message); }
            return rows;
        }
        private void CatalogTags(object group, string path, string owner, List<IndexEntry> rows)
        {
            if (group == null) return;
            foreach (object tag in Api.Items(Api.Get(group, "TagTables", false))) rows.Add(IndexEntry.Create("plc.tags", owner, path, Api.GetString(tag, "Name"), "Tags"));
            foreach (object child in Api.Items(Api.Get(group, "Groups", false)))
                CatalogTags(child, path.Length == 0 ? Api.GetString(child, "Name") : path + "/" + Api.GetString(child, "Name"), owner, rows);
        }
        internal string Read(IndexEntry entry)
        {
            if (entry.kind.StartsWith("hmi.", StringComparison.Ordinal))
                return new HmiOperations(session, session.SelectHmi(entry.owner)).ReadOneXml(entry.kind.Substring(4), entry.name, entry.group);
            if (entry.kind == "hardware")
            {
                if (hardwareRowsByPath == null && !hardwareFullSnapshotUnavailable)
                {
                    try
                    {
                        var full = IndexJson.Map(new HardwareOperations(session).Read("", true));
                        hardwareRowsByPath = ((object[])full["objects"])
                            .Cast<Dictionary<string, object>>()
                            .ToDictionary(r => IndexJson.Text(r, "path"), r => r, StringComparer.Ordinal);
                    }
                    catch (Exception)
                    {
                        hardwareFullSnapshotUnavailable = true;
                    }
                }
                Dictionary<string, object> cachedRow;
                if (hardwareRowsByPath != null && hardwareRowsByPath.TryGetValue(entry.group, out cachedRow))
                    return IndexJson.Serializer().Serialize(cachedRow);
                var read = IndexJson.Map(new HardwareOperations(session).Read(entry.group, true));
                var row = ((object[])read["objects"]).Cast<Dictionary<string, object>>().Single(r => IndexJson.Text(r, "path") == entry.group);
                return IndexJson.Serializer().Serialize(row);
            }
            var plc = session.SelectPlc(entry.owner); var ops = new BlockOperations(session, plc);
            if (entry.kind == "plc.block") return ops.Read(entry.name, entry.group.Length == 0 ? "/" : entry.group);
            if (entry.kind == "plc.tags") return ops.ReadTagTable(entry.name, entry.group);
            var item = ops.GetCachedTypes().SingleOrDefault(e => e.Name == entry.name && e.GroupPath == entry.group);
            if (item == null) { plc.InvalidateTypeCache(); item = ops.GetCachedTypes().Single(e => e.Name == entry.name && e.GroupPath == entry.group); }
            string temp = Path.Combine(Path.GetTempPath(), "tia-index-" + Guid.NewGuid().ToString("N") + ".xml");
            try { Api.Invoke(item.Item, "Export", new FileInfo(temp), Api.EnumValue("Siemens.Engineering.ExportOptions", "WithDefaults")); return File.ReadAllText(temp); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        internal static IndexContent Hardware(string json)
        {
            var data = (Dictionary<string, object>)IndexJson.Serializer().DeserializeObject(json);
            var c = new IndexContent { hash = IndexJson.Hash(json) };
            c.facts.Add(new IndexFact { kind = "hardware", location = IndexJson.Text(data, "path"), text = IndexJson.Text(data, "name") + " " + IndexJson.Text(data, "type") });
            object raw;
            if (data.TryGetValue("attributes", out raw) && raw is object[]) foreach (object attr in (object[])raw)
            {
                var a = attr as Dictionary<string, object>; if (a == null) continue;
                c.facts.Add(new IndexFact { kind = "hardware_attribute", location = IndexJson.Text(data, "path"), text = IndexJson.Text(a, "name") + "=" + IndexJson.Text(a, "value") });
            }
            if (data.TryGetValue("links", out raw) && raw is Dictionary<string, object>) foreach (var link in (Dictionary<string, object>)raw)
                c.references.Add(new IndexReference { relation = "hardware_" + link.Key, target = IndexJson.Serializer().Serialize(link.Value), location = IndexJson.Text(data, "path"), evidence = "explicit topology link" });
            c.search_text = json; return c;
        }
    }
}
