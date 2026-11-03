using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using System.Xml;
using TiaPortalSourceBridge.Mcp;

namespace TiaPortalSourceBridge.Openness
{
    internal sealed partial class HmiOperations
    {
        // kind, root XML type, target property, collection property, hierarchical folder
        internal static readonly string[][] Kinds = {
            new[] { "screens", "Hmi.Screen.Screen", "ScreenFolder", "Screens", "1" },
            new[] { "screen_templates", "Hmi.Screen.ScreenTemplate", "ScreenTemplateFolder", "ScreenTemplates", "1" },
            new[] { "popup_screens", "Hmi.Screen.ScreenPopup", "ScreenPopupFolder", "ScreenPopups", "1" },
            new[] { "slidein_screens", "Hmi.Screen.ScreenSlidein", "ScreenSlideinFolder", "ScreenSlideins", "0" },
            new[] { "tag_tables", "Hmi.Tag.TagTable", "TagFolder", "TagTables", "1" },
            new[] { "scripts", "Hmi.RuntimeScripting.VBScript", "VBScriptFolder", "VBScripts", "1" },
            new[] { "text_lists", "Hmi.TextGraphicList.TextList", "", "TextLists", "0" },
            new[] { "graphic_lists", "Hmi.TextGraphicList.GraphicList", "", "GraphicLists", "0" },
            new[] { "cycles", "Hmi.Cycle.Cycle", "", "Cycles", "0" },
            new[] { "connections", "Hmi.Communication.Connection", "", "Connections", "0" }
        };
        private readonly OpennessSession session;
        private readonly HmiContext hmi;
        private ReflectionApi Api { get { return session.Api; } }
        public HmiOperations(OpennessSession session, HmiContext hmi) { this.session = session; this.hmi = hmi; }
        private static string Text(Dictionary<string, object> m, string k, string fallback = "") { object v; return m.TryGetValue(k, out v) ? Convert.ToString(v) : fallback; }
        private static string[] Spec(string kind) { var spec = Kinds.SingleOrDefault(k => k[0] == kind); if (spec == null) throw new ArgumentException("不支持的经典 HMI 对象类别：" + kind); return spec; }
        private IEnumerable<Tuple<string, object>> Enumerate(string[] spec, object folder, string path)
        {
            if (folder == null) yield break;
            foreach (object item in Api.Items(Api.Get(folder, spec[3], false))) yield return Tuple.Create(path, item);
            if (spec[4] == "1") foreach (object child in Api.Items(Api.Get(folder, "Folders", false)))
            {
                string name = Api.GetString(child, "Name");
                foreach (var item in Enumerate(spec, child, string.IsNullOrEmpty(path) ? name : path + "/" + name)) yield return item;
            }
        }
        private object Root(string[] spec) { return spec[2] == "" ? hmi.Target : Api.Get(hmi.Target, spec[2], false); }
        private object Folder(string[] spec, string path, bool create, Dictionary<string, object> cache)
        {
            string key = spec[0] + "|" + path; object found;
            if (cache.TryGetValue(key, out found)) return found;
            object folder = Root(spec);
            foreach (string part in PathTools.SplitGroupPath(path))
            {
                if (folder == null) return null;
                object folders = Api.Get(folder, "Folders", false);
                object next = null;
                try { next = Api.Invoke(folders, "Find", part); } catch (MissingMethodException) { }
                if (next == null) next = Api.Items(folders).SingleOrDefault(g => string.Equals(Api.GetString(g, "Name"), part, StringComparison.OrdinalIgnoreCase));
                if (next == null && create) next = Api.Invoke(folders, "Create", part);
                folder = next;
            }
            cache[key] = folder; return folder;
        }
        private string Export(object item)
        {
            string file = Path.Combine(Path.GetTempPath(), "tia-hmi-" + Guid.NewGuid().ToString("N") + ".xml");
            try { using (OperationMetrics.Measure("export")) { Api.Invoke(item, "Export", new FileInfo(file), Api.EnumValue("Siemens.Engineering.ExportOptions", "WithDefaults")); return File.ReadAllText(file); } }
            finally { if (File.Exists(file)) File.Delete(file); }
        }
        internal string ReadOneXml(string kind, string name, string group)
        {
            string[] spec = Spec(kind);
            object folder = Folder(spec, PathTools.NormalizeGroupPath(group), false, new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase));
            if (folder == null) throw new InvalidOperationException("HMI 目标组不存在。");
            object collection = Api.Get(folder, spec[3], true), found = null;
            try { found = Api.Invoke(collection, "Find", name); } catch (MissingMethodException) { }
            if (found == null) found = Api.Items(collection).SingleOrDefault(o => string.Equals(Api.GetString(o, "Name"), name, StringComparison.OrdinalIgnoreCase));
            if (found == null) throw new InvalidOperationException("HMI 对象已不存在：" + name);
            return Export(found);
        }
        public Dictionary<string, object> Read(string kind, string nameFilter, string groupPrefix, bool includeXml, bool includeModel)
        {
            var results = new List<object>(); var scopes = new List<object>(); int failures = 0;
            string prefix = PathTools.NormalizeGroupPath(groupPrefix);
            var folders = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var spec in string.IsNullOrWhiteSpace(kind) || kind == "all" ? Kinds : new[] { Spec(kind) })
            {
                if (prefix.Length > 0 && spec[4] != "1") continue;
                OperationMetrics.Count("hmi_catalog_scans");
                object readFolder = Folder(spec, prefix, false, folders);
                bool supported = readFolder != null && Api.Get(readFolder, spec[3], false) != null;
                scopes.Add(new { kind = spec[0], state = supported ? "readable_scope" : prefix.Length > 0 ? "group_not_found_or_unreadable" : "unsupported", complete = supported && spec[0] != "connections" });
                foreach (var entry in Enumerate(spec, readFolder, prefix))
                {
                    string name = Api.GetString(entry.Item2, "Name");
                    if (!string.IsNullOrEmpty(nameFilter) && name.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    string xml = "", error = ""; object model = null;
                    try
                    {
                        if (includeXml || includeModel) xml = Export(entry.Item2);
                        if (includeModel) model = HmiModelBuilder.ReadTree(Parse(xml).DocumentElement.ChildNodes.OfType<XmlElement>().Single(e => e.Name.StartsWith("Hmi.", StringComparison.Ordinal)));
                    }
                    catch (Exception ex) { error = ex.Message; failures++; }
                    results.Add(new { kind = spec[0], name = name, group_path = entry.Item1, xml = includeXml ? xml : "", model = model, ok = error.Length == 0, error = error });
                }
            }
            var response = new Dictionary<string, object> { { "hmi", hmi.DisplayName }, { "objects", results.ToArray() }, { "failure_count", failures } };
            if ((includeXml || includeModel) && (string.IsNullOrWhiteSpace(kind) || kind == "all" || kind.IndexOf("screen", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                response["openness_note"] = "注意：TIA Openness 在导出/导入经典 HMI 画面 XML 时会过滤 AlarmView（报警视图）等特定复合控件；对含报警视图的画面（如 HD_31_Alarm / HD_32_AlarmHistory）执行 XML 覆盖回导会导致该控件丢失。";
            }
            response["coverage"] = Coverage();
            response["scope_status"] = scopes.ToArray();
            return response;
        }
        private static XmlDocument Parse(string xml)
        {
            var doc = new XmlDocument { XmlResolver = null };
            using (XmlReader reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) doc.Load(reader);
            return doc;
        }
        internal static HmiDocument[] ParseDocuments(string objectsJson, string version)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 };
            var input = json.DeserializeObject(objectsJson) as object[];
            if (input == null || input.Length == 0 || input.Length > 1000) throw new ArgumentException("objects_json 必须是 1..1000 项对象数组。");
            var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return input.Select(row =>
            {
                var m = row as Dictionary<string, object>; if (m == null) throw new ArgumentException("HMI 对象必须是 JSON 对象。");
                string kind = Text(m, "kind"), xml = Text(m, "xml"); string[] spec = Spec(kind);
                if (string.IsNullOrWhiteSpace(xml) && m.ContainsKey("model"))
                {
                    var generated = HmiModelBuilder.Generate(json.Serialize(new { objects = new[] { m } }), version, ""); xml = generated[0].xml;
                }
                XmlDocument doc = new XmlDocument { XmlResolver = null, PreserveWhitespace = true };
                using (XmlReader reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) doc.Load(reader);
                XmlNode versionNode = doc.SelectSingleNode("/Document/Engineering/@version");
                if (versionNode == null || !string.Equals(versionNode.Value, version, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("HMI XML 与目标 TIA 版本不一致。");
                var roots = doc.SelectNodes("/Document/*[starts-with(local-name(),'Hmi.')]");
                if (roots.Count != 1 || roots[0].Name != spec[1]) throw new ArgumentException("HMI XML 根对象与 kind 不一致：" + kind);
                XmlNode nameNode = roots[0].SelectSingleNode("AttributeList/Name");
                if (nameNode == null || string.IsNullOrWhiteSpace(nameNode.InnerText)) throw new ArgumentException("HMI XML 缺少对象名称。");
                string name = nameNode.InnerText.Trim();
                if (!string.IsNullOrWhiteSpace(Text(m, "name")) && !string.Equals(Text(m, "name"), name, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("HMI 对象名称与 XML 不一致。");
                string group = PathTools.NormalizeGroupPath(Text(m, "group_path"));
                if (group.Length > 0 && spec[4] != "1") throw new ArgumentException("该 HMI 类别不支持组路径：" + kind);
                if (!identities.Add(kind + "|" + name)) throw new ArgumentException("批次内存在重复 HMI 对象：" + name);
                return new HmiDocument { kind = kind, group_path = group, name = name, xml = xml, RootOuterXml = roots[0].OuterXml };
            }).ToArray();
        }
        private static string ExtractRootOuterXml(string xml)
        {
            var doc = new XmlDocument { XmlResolver = null, PreserveWhitespace = true };
            using (var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { XmlResolver = null, DtdProcessing = DtdProcessing.Prohibit })) doc.Load(reader);
            return doc.DocumentElement.ChildNodes.OfType<XmlElement>().Single(e => e.Name.StartsWith("Hmi.", StringComparison.Ordinal)).OuterXml;
        }
        internal static bool SameObjectXml(string left, string right)
        {
            // Strict object XML comparison deliberately keeps whitespace and IDs: false negatives are safe.
            return string.Equals(ExtractRootOuterXml(left), ExtractRootOuterXml(right), StringComparison.Ordinal);
        }
        private Dictionary<string, object> WriteCore(string objectsJson, string backupRoot, bool skipUnchanged = true)
        {
            HmiDocument[] objects = ParseDocuments(objectsJson, Api.LoadedPortalVersion);
            string[] order = { "connections", "cycles", "tag_tables", "text_lists", "graphic_lists", "scripts", "screen_templates", "popup_screens", "slidein_screens", "screens" };
            var results = new List<object>(); int success = 0, failures = 0; bool stopped = false;
            int unchanged = 0;
            var catalogs = new Dictionary<string, Dictionary<string, Tuple<string, object>[]>>(StringComparer.Ordinal);
            var folders = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in objects.OrderBy(o => Array.IndexOf(order, o.kind)))
            {
                if (stopped) { results.Add(new { name = entry.name, kind = entry.kind, ok = false, skipped = true, backup = "", error = "前一项失败，已停止后续导入。" }); continue; }
                string backup = ""; bool mutationStarted = false;
                try
                {
                    string[] spec = Spec(entry.kind);
                    Dictionary<string, Tuple<string, object>[]> catalog;
                    if (!catalogs.TryGetValue(entry.kind, out catalog))
                    {
                        OperationMetrics.Count("hmi_catalog_scans");
                        catalog = Enumerate(spec, Root(spec), "").GroupBy(e => Api.GetString(e.Item2, "Name"), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
                        catalogs[entry.kind] = catalog;
                    }
                    Tuple<string, object>[] matches; if (!catalog.TryGetValue(entry.name, out matches)) matches = new Tuple<string, object>[0];
                    if (matches.Length > 1) throw new InvalidOperationException("存在多个同名 HMI 对象，拒绝覆盖：" + entry.name);
                    if (matches.Length == 1)
                    {
                        if (!string.IsNullOrEmpty(entry.group_path) && !string.Equals(matches[0].Item1, entry.group_path, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("目标对象已存在于其他组。");
                        entry.group_path = matches[0].Item1;
                        string original = Export(matches[0].Item2);
                        if (skipUnchanged && (entry.RootOuterXml != null
                            ? string.Equals(ExtractRootOuterXml(original), entry.RootOuterXml, StringComparison.Ordinal)
                            : SameObjectXml(original, entry.xml)))
                        {
                            unchanged++; success++;
                            results.Add(new { name = entry.name, kind = entry.kind, ok = true, skipped = false, unchanged = true, backup = "", error = "" });
                            continue;
                        }
                        backup = BackupRecord.Write(backupRoot, entry.name, original, session.GetProjectPath(), hmi.DisplayName, entry.group_path, entry.kind, entry.name);
                    }
                    mutationStarted = true;
                    object folder = Folder(spec, entry.group_path, true, folders); if (folder == null) throw new InvalidOperationException("当前 HMI 不提供该对象类型：" + entry.kind);
                    string path = Path.Combine(Path.GetTempPath(), "tia-hmi-import-" + Guid.NewGuid().ToString("N") + ".xml");
                    try { using (OperationMetrics.Measure("import")) { File.WriteAllText(path, entry.xml); Api.Invoke(Api.Get(folder, spec[3], true), "Import", new FileInfo(path), Api.EnumValue("Siemens.Engineering.ImportOptions", matches.Length == 0 ? "None" : "Override")); } }
                    finally { if (File.Exists(path)) File.Delete(path); }
                    VerifyImported(entry);
                    success++; results.Add(new { name = entry.name, kind = entry.kind, ok = true, skipped = false, backup = backup, verification = "requested_fields_verified", error = "" });
                }
                catch (Exception ex) { failures++; stopped = true; results.Add(new { name = entry.name, kind = entry.kind, ok = false, skipped = false, backup = backup, may_have_changed = mutationStarted, stage = mutationStarted ? "import_or_readback" : "before_import", error = ex.ToString(), verification_differences = ex is HmiVerificationException ? ((HmiVerificationException)ex).Differences : new object[0] }); }
            }
            return new Dictionary<string, object> { { "saved", false }, { "success_count", success }, { "changed_count", success - unchanged }, { "unchanged_count", unchanged }, { "failure_count", failures }, { "skipped_count", objects.Length - success - failures }, { "items", results.ToArray() } };
        }
    }
}
