using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Xml;
using TiaPortalSourceBridge.Openness;

namespace TiaPortalSourceBridge.Mcp
{
    internal sealed class GraphBlockSnapshot
    {
        internal CachedBlockEntry Entry;
        internal string Xml;
    }

    internal sealed class BatchImportResult
    {
        public int success_count { get; set; }
        public int failure_count { get; set; }
        public bool rolled_back { get; set; }
        public object[] items { get; set; }
    }

    internal sealed class BlockOperations
    {
        private static readonly Regex SourceHeaderRegex = new Regex(
            @"(?:^|\r|\n)\s*(?:FUNCTION_BLOCK|FUNCTION|DATA_BLOCK|ORGANIZATION_BLOCK|TYPE)\s+(?:""([^""]+)""|([A-Za-z_][A-Za-z0-9_]*))",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly OpennessSession _session;
        private readonly PlcContext _plc;
        private ReflectionApi Api { get { return _session.Api; } }

        public BlockOperations(OpennessSession session, PlcContext plc)
        {
            _session = session;
            _plc = plc;
        }

        internal static string NormalizeGroupPath(string groupPath)
        {
            return PathTools.NormalizeGroupPath(groupPath);
        }

        internal static object EnsureGroup(ReflectionApi api, object rootGroup, string groupPath)
        {
            object group = rootGroup;
            foreach (string part in PathTools.SplitGroupPath(PathTools.NormalizeGroupPath(groupPath)))
            {
                object groups = api.Get(group, "Groups", true);
                object next = TryFindByNameStatic(api, groups, part);
                if (next == null)
                {
                    next = api.Invoke(groups, "Create", part);
                }
                group = next;
            }
            return group;
        }

        internal static CachedBlockEntry ResolveBlockEntry(OpennessSession session, PlcContext plc, string name, string groupPath, string unitName)
        {
            BlockOperations ops = new BlockOperations(session, plc);
            return ops.FindSingleBlockFast(name, groupPath, unitName);
        }

        internal static CachedBlockEntry ResolveTypeEntry(OpennessSession session, PlcContext plc, string name, string groupPath, string unitName)
        {
            BlockOperations ops = new BlockOperations(session, plc);
            return ops.FindSingleTypeFast(name, groupPath, unitName);
        }

        internal static object ResolveTagTableObject(OpennessSession session, PlcContext plc, string name, string groupPath, string unitName)
        {
            BlockOperations ops = new BlockOperations(session, plc);
            return ops.FindSingleTagTable(name, groupPath, unitName).Item2;
        }

        internal static string TryBackupExisting(OpennessSession session, string backupRoot, PlcContext plc, string name, string groupPath, string unitName, bool isType)
        {
            BlockOperations ops = new BlockOperations(session, plc);
            string normalizedGroup = PathTools.NormalizeGroupPath(groupPath);
            string normUnit = (unitName ?? "").Trim();
            List<CachedBlockEntry> list = isType ? ops.GetCachedTypes(true) : ops.GetCachedBlocks(true);
            CachedBlockEntry match = list.FirstOrDefault(e =>
                string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(e.UnitName ?? "", normUnit, StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrEmpty(normalizedGroup) || string.Equals(e.GroupPath, normalizedGroup, StringComparison.OrdinalIgnoreCase)));
            if (match == null) return "";
            try
            {
                return ops.Backup(match.Item, match.Name, match.GroupPath, isType ? "Type" : "Block", backupRoot);
            }
            catch
            {
                return "";
            }
        }

        private IEnumerable<Tuple<string, object>> Blocks(object group, string path)
        {
            foreach (object block in Api.Items(Api.Get(group, "Blocks", true)))
                yield return Tuple.Create(path, block);
            foreach (object child in Api.Items(Api.Get(group, "Groups", true)))
            {
                string childName = Api.GetString(child, "Name");
                string childPath = string.IsNullOrEmpty(path) ? childName : path + "/" + childName;
                foreach (Tuple<string, object> entry in Blocks(child, childPath))
                    yield return entry;
            }
        }

        private IEnumerable<Tuple<string, object>> Types(object group, string path)
        {
            foreach (object plcType in Api.Items(Api.Get(group, "Types", true)))
                yield return Tuple.Create(path, plcType);
            foreach (object child in Api.Items(Api.Get(group, "Groups", true)))
            {
                string childName = Api.GetString(child, "Name");
                string childPath = string.IsNullOrEmpty(path) ? childName : path + "/" + childName;
                foreach (Tuple<string, object> entry in Types(child, childPath))
                    yield return entry;
            }
        }

        private IEnumerable<Tuple<string, object>> TagTables(object group, string path)
        {
            foreach (object table in Api.Items(Api.Get(group, "TagTables", true)))
                yield return Tuple.Create(path, table);
            foreach (object child in Api.Items(Api.Get(group, "Groups", false)))
            {
                string childName = Api.GetString(child, "Name");
                string childPath = string.IsNullOrEmpty(path) ? childName : path + "/" + childName;
                foreach (Tuple<string, object> entry in TagTables(child, childPath))
                    yield return entry;
            }
        }

        public List<CachedBlockEntry> GetCachedBlocks(bool forceRefresh = false)
        {
            if (!forceRefresh && _plc.BlockCache != null)
            {
                OperationMetrics.Count("block_cache_hits");
                return _plc.BlockCache;
            }
            List<CachedBlockEntry> list = ScanAllContainers("", false).ToList();
            _plc.BlockCache = list;
            return list;
        }

        public List<CachedBlockEntry> GetCachedTypes(bool forceRefresh = false)
        {
            if (!forceRefresh && _plc.TypeCache != null)
            {
                OperationMetrics.Count("type_cache_hits");
                return _plc.TypeCache;
            }
            List<CachedBlockEntry> list = ScanAllContainers("", true).ToList();
            _plc.TypeCache = list;
            return list;
        }

        private IEnumerable<CachedBlockEntry> ScanAllContainers(string path, bool types, string onlyUnit = null)
        {
            OperationMetrics.Count(types ? "type_catalog_scans" : "block_catalog_scans");
            if (string.IsNullOrEmpty(onlyUnit))
            {
                foreach (CachedBlockEntry e in ScanContainer(_plc.Software, "", path, types))
                    yield return e;
            }

            foreach (object unit in SoftwareUnitHelper.GetUnits(Api, _plc))
            {
                string uName = Api.GetString(unit, "Name");
                if (onlyUnit != null && !string.Equals(uName, onlyUnit, StringComparison.OrdinalIgnoreCase))
                    continue;
                foreach (CachedBlockEntry e in ScanContainer(unit, uName, path, types))
                    yield return e;
            }
        }

        private IEnumerable<CachedBlockEntry> ScanContainer(object container, string unitName, string path, bool types)
        {
            object root = Api.Get(container, types ? "TypeGroup" : "BlockGroup", false);
            if (root == null) yield break;
            object group;
            if (string.IsNullOrEmpty(path))
            {
                group = root;
            }
            else
            {
                try { group = ResolveGroup(root, path, false); }
                catch { yield break; }
            }

            foreach (Tuple<string, object> entry in types ? Types(group, path) : Blocks(group, path))
            {
                yield return SoftwareUnitHelper.BuildEntry(Api, entry.Item2, entry.Item1, unitName, types);
            }
        }

        public IEnumerable<object> List(string groupFilter = "", string nameFilter = "", string languageFilter = "", bool refresh = false, string scopePath = "", string unitFilter = "")
        {
            string normGroup = PathTools.NormalizeGroupPath(groupFilter);
            string normName = (nameFilter ?? "").Trim();
            string normLang = (languageFilter ?? "").Trim();
            string normUnit = (unitFilter ?? "").Trim();

            IEnumerable<CachedBlockEntry> source = string.IsNullOrWhiteSpace(scopePath)
                ? GetCachedBlocks(refresh)
                : ScanAllContainers(PathTools.NormalizeGroupPath(scopePath), false, normUnit.Length == 0 ? null : normUnit);

            foreach (CachedBlockEntry entry in source)
            {
                if (normUnit.Length > 0 && !string.Equals(entry.UnitName ?? "", normUnit, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!string.IsNullOrEmpty(normGroup)
                    && !string.Equals(entry.GroupPath, normGroup, StringComparison.OrdinalIgnoreCase)
                    && !entry.GroupPath.StartsWith(normGroup + "/", StringComparison.OrdinalIgnoreCase)
                    && entry.GroupPath.IndexOf(normGroup, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (!string.IsNullOrEmpty(normName)
                    && entry.Name.IndexOf(normName, StringComparison.OrdinalIgnoreCase) < 0
                    && (entry.Namespace ?? "").IndexOf(normName, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (!string.IsNullOrEmpty(normLang)
                    && !string.Equals(entry.Language, normLang, StringComparison.OrdinalIgnoreCase))
                    continue;

                yield return new
                {
                    name = entry.Name,
                    group_path = entry.GroupPath,
                    unit_name = entry.UnitName,
                    @namespace = entry.Namespace,
                    number = entry.Number,
                    language = entry.Language,
                    category = entry.Category,
                    is_consistent = entry.IsConsistent,
                    is_know_how_protected = entry.IsKnowHowProtected,
                    modified_date = entry.ModifiedDate,
                    code_modified_date = entry.CodeModifiedDate,
                    interface_modified_date = entry.InterfaceModifiedDate
                };
            }
        }

        public IEnumerable<object> ListTypes(string groupFilter = "", string nameFilter = "", bool refresh = false, string scopePath = "", string unitFilter = "")
        {
            string normGroup = PathTools.NormalizeGroupPath(groupFilter);
            string normName = (nameFilter ?? "").Trim();
            string normUnit = (unitFilter ?? "").Trim();

            IEnumerable<CachedBlockEntry> source = string.IsNullOrWhiteSpace(scopePath)
                ? GetCachedTypes(refresh)
                : ScanAllContainers(PathTools.NormalizeGroupPath(scopePath), true, normUnit.Length == 0 ? null : normUnit);

            foreach (CachedBlockEntry entry in source)
            {
                if (normUnit.Length > 0 && !string.Equals(entry.UnitName ?? "", normUnit, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!string.IsNullOrEmpty(normGroup)
                    && !string.Equals(entry.GroupPath, normGroup, StringComparison.OrdinalIgnoreCase)
                    && !entry.GroupPath.StartsWith(normGroup + "/", StringComparison.OrdinalIgnoreCase)
                    && entry.GroupPath.IndexOf(normGroup, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (!string.IsNullOrEmpty(normName)
                    && entry.Name.IndexOf(normName, StringComparison.OrdinalIgnoreCase) < 0
                    && (entry.Namespace ?? "").IndexOf(normName, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                yield return new
                {
                    name = entry.Name,
                    group_path = entry.GroupPath,
                    unit_name = entry.UnitName,
                    @namespace = entry.Namespace,
                    language = entry.Language,
                    is_consistent = entry.IsConsistent,
                    modified_date = entry.ModifiedDate
                };
            }
        }

        public IEnumerable<object> ListTagTables(string unitFilter = "")
        {
            string normUnit = (unitFilter ?? "").Trim();
            if (normUnit.Length == 0)
            {
                object rootGroup = Api.Get(_plc.Software, "TagTableGroup", true);
                foreach (Tuple<string, object> entry in TagTables(rootGroup, ""))
                {
                    yield return BuildTagTableRow(entry.Item2, entry.Item1, "");
                }
            }

            foreach (object unit in SoftwareUnitHelper.GetUnits(Api, _plc))
            {
                string uName = Api.GetString(unit, "Name");
                if (normUnit.Length > 0 && !string.Equals(uName, normUnit, StringComparison.OrdinalIgnoreCase))
                    continue;
                object unitTagGroup = Api.Get(unit, "TagTableGroup", false);
                if (unitTagGroup == null) continue;
                foreach (Tuple<string, object> entry in TagTables(unitTagGroup, ""))
                {
                    yield return BuildTagTableRow(entry.Item2, entry.Item1, uName);
                }
            }
        }

        private object BuildTagTableRow(object table, string groupPath, string unitName)
        {
            object tags = Api.Get(table, "Tags", false);
            object constants = Api.Get(table, "UserConstants", false);
            return new
            {
                name = Api.GetString(table, "Name"),
                group_path = groupPath,
                unit_name = unitName,
                tag_count = Api.Items(tags).Count(),
                constant_count = Api.Items(constants).Count()
            };
        }

        private Tuple<string, object> FindSingleTagTable(string name, string groupPath, string unitName)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("table_name 不能为空。");
            string trimmedName = name.Trim();
            string normalizedGroup = PathTools.NormalizeGroupPath(groupPath);
            object container = SoftwareUnitHelper.ResolveContainer(Api, _plc, unitName);
            object rootGroup = Api.Get(container, "TagTableGroup", true);

            Tuple<string, object>[] matches = TagTables(rootGroup, "")
                .Where(e => string.Equals(Api.GetString(e.Item2, "Name"), trimmedName, StringComparison.OrdinalIgnoreCase)
                    && (string.IsNullOrEmpty(normalizedGroup) || string.Equals(e.Item1, normalizedGroup, StringComparison.OrdinalIgnoreCase)))
                .ToArray();

            if (matches.Length == 0)
                throw new InvalidOperationException("找不到 PLC 变量表 '" + trimmedName + "'。");
            if (matches.Length > 1)
                throw new InvalidOperationException("存在多个同名 PLC 变量表 '" + trimmedName + "'，请指定 group_path: "
                    + string.Join(", ", matches.Select(m => m.Item1)));
            return matches[0];
        }

        public string ReadTagTable(string name, string groupPath, string unitName = "", bool stripDocumentInfo = false)
        {
            Tuple<string, object> match = FindSingleTagTable(name, groupPath, unitName);
            return Export(match.Item2, stripDocumentInfo);
        }

        public string ImportTagTable(string xml, string groupPath, string backupRoot, bool createGroupIfMissing = true, string unitName = "")
        {
            if (string.IsNullOrWhiteSpace(xml))
                throw new InvalidOperationException("XML 内容不能为空。");
            XmlDocument doc = ParsePlcObject(xml);
            XmlNode tableNode = doc.SelectSingleNode("/Document/SW.Tags.PlcTagTable");
            if (tableNode == null)
                throw new InvalidOperationException("仅接受包含一个 SW.Tags.PlcTagTable 的 SimaticML Document。");
            ValidateVersion(doc);

            XmlNode nameNode = tableNode.SelectSingleNode("AttributeList/Name");
            string name = nameNode == null ? "" : nameNode.InnerText.Trim();
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException("变量表名称不能为空。");

            object container = SoftwareUnitHelper.ResolveContainer(Api, _plc, unitName);
            object rootGroup = Api.Get(container, "TagTableGroup", true);
            string normalizedGroup = PathTools.NormalizeGroupPath(groupPath);
            Tuple<string, object> existingAnywhere = TagTables(rootGroup, "").FirstOrDefault(e =>
                string.Equals(Api.GetString(e.Item2, "Name"), name, StringComparison.OrdinalIgnoreCase));

            if (existingAnywhere != null && string.IsNullOrEmpty(normalizedGroup))
            {
                normalizedGroup = existingAnywhere.Item1;
            }
            else if (existingAnywhere != null && !string.Equals(existingAnywhere.Item1, normalizedGroup, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("同名变量表已存在于其他组：" + existingAnywhere.Item1);
            }

            object targetGroup = ResolveGroup(rootGroup, normalizedGroup, createGroupIfMissing);
            object tables = Api.Get(targetGroup, "TagTables", true);

            string backup = "新变量表，无原备份";
            if (existingAnywhere != null)
            {
                backup = Backup(existingAnywhere.Item2, name, existingAnywhere.Item1, "TagTable", backupRoot);
            }

            string file = Path.Combine(Path.GetTempPath(), "tia-tagtable-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                doc.Save(file);
                _session.RunInTransaction("Import TagTable " + name, true, delegate
                {
                    Api.Invoke(tables, "Import", new FileInfo(file),
                        Api.EnumValue("Siemens.Engineering.ImportOptions", "Override"));
                });
                return backup;
            }
            finally
            {
                if (File.Exists(file)) File.Delete(file);
            }
        }

        public string Read(string name, string groupPath, string unitName = "", bool stripDocumentInfo = false)
        {
            CachedBlockEntry match = FindSingleBlockFast(name, groupPath, unitName);
            return Export(match.Item, stripDocumentInfo);
        }

        internal GraphBlockSnapshot ReadGraphSnapshot(string name, string groupPath, string unitName = "")
        {
            CachedBlockEntry entry = FindSingleBlockFast(name, groupPath, unitName);
            using (OperationMetrics.Measure("graph_read"))
                return new GraphBlockSnapshot { Entry = entry, Xml = Export(entry.Item, false) };
        }

        internal string ImportGraphSnapshot(GraphBlockSnapshot snapshot, XmlDocument document, string backupRoot, bool skipBackup, out object importedBlock)
        {
            CachedBlockEntry entry = snapshot.Entry;
            XmlNode attrs = document.SelectSingleNode("/Document/SW.Blocks.FB/AttributeList");
            if (attrs == null || attrs.SelectSingleNode("ProgrammingLanguage").InnerText != "GRAPH"
                || attrs.SelectSingleNode("Name").InnerText != entry.Name
                || attrs.SelectSingleNode("Number").InnerText != Convert.ToString(Api.Get(entry.Item, "Number", true))
                || Api.GetString(entry.Item, "Name") != entry.Name)
                throw new InvalidOperationException("GRAPH 快速修改不能改变目标块身份。");
            ValidateVersion(document);
            string backup = "已按请求跳过备份";
            if (!skipBackup)
                using (OperationMetrics.Measure("backup"))
                    backup = BackupRecord.Write(backupRoot, entry.Name, snapshot.Xml, _session.GetProjectPath(), _plc.DisplayName, entry.GroupPath, "Block", entry.Name);
            object container = SoftwareUnitHelper.ResolveContainer(Api, _plc, entry.UnitName);
            object group = ResolveGroup(Api.Get(container, "BlockGroup", true), entry.GroupPath, false);
            string file = Path.Combine(Path.GetTempPath(), "tia-graph-import-" + Guid.NewGuid().ToString("N") + ".xml");
            object resolvedBlock = null;
            try
            {
                document.Save(file);
                _session.RunInTransaction("Update GRAPH " + entry.Name, true, delegate
                {
                    using (OperationMetrics.Measure("import"))
                        Api.Invoke(Api.Get(group, "Blocks", true), "Import", new FileInfo(file), Api.EnumValue("Siemens.Engineering.ImportOptions", "Override"));
                    resolvedBlock = TryFindByName(Api.Get(group, "Blocks", true), entry.Name);
                });
                importedBlock = resolvedBlock;
                UpdateBlockCacheEntry(entry.Name, entry.GroupPath, entry.UnitName, importedBlock);
                OperationMetrics.Count("graph_imports");
                return backup;
            }
            catch { _plc.InvalidateBlockCache(); throw; }
            finally { if (File.Exists(file)) File.Delete(file); }
        }

        public object[] ListGroups(string unitName = "")
        {
            object container = SoftwareUnitHelper.ResolveContainer(Api, _plc, unitName);
            return GroupRows(Api.Get(container, "BlockGroup", true), "", unitName ?? "").ToArray();
        }

        private IEnumerable<object> GroupRows(object parent, string path, string unitName)
        {
            foreach (object group in Api.Items(Api.Get(parent, "Groups", true)))
            {
                string name = Api.GetString(group, "Name");
                string childPath = path == "" ? name : path + "/" + name;
                yield return new
                {
                    name = name,
                    group_path = childPath,
                    unit_name = unitName,
                    block_count = Api.Items(Api.Get(group, "Blocks", true)).Count()
                };
                foreach (object child in GroupRows(group, childPath, unitName)) yield return child;
            }
        }

        public object OrganizeGroups(string parentPath, string renameJson, string namesJson, string backupRoot, string unitName = "")
        {
            JavaScriptSerializer json = new JavaScriptSerializer();
            Dictionary<string, string> rename = json.Deserialize<Dictionary<string, string>>(renameJson);
            string[] names = json.Deserialize<string[]>(namesJson);
            if (rename == null || names == null) throw new InvalidOperationException("需要重命名字典和目标组名称数组。");
            foreach (string name in rename.Keys.Concat(rename.Values).Concat(names))
                if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(new[] { '/', '\\' }) >= 0)
                    throw new InvalidOperationException("组名称不能为空或包含路径分隔符。");
            object container = SoftwareUnitHelper.ResolveContainer(Api, _plc, unitName);
            object parent = ResolveGroup(Api.Get(container, "BlockGroup", true), PathTools.NormalizeGroupPath(parentPath), false);
            object groups = Api.Get(parent, "Groups", true);
            Dictionary<string, object> existing = Api.Items(groups).ToDictionary(g => Api.GetString(g, "Name"), g => g, StringComparer.OrdinalIgnoreCase);
            if (rename.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != rename.Count)
                throw new InvalidOperationException("多个组不能重命名为相同目标。");
            foreach (KeyValuePair<string, string> pair in rename)
                if (!string.Equals(pair.Key, pair.Value, StringComparison.OrdinalIgnoreCase)
                    && existing.ContainsKey(pair.Key) && existing.ContainsKey(pair.Value))
                    throw new InvalidOperationException("原组和目标组同时存在，不自动合并：" + pair.Key + " / " + pair.Value);
            Directory.CreateDirectory(backupRoot);
            string backup = Path.Combine(backupRoot, "groups-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(backup, json.Serialize(new { project_path = _session.GetProjectPath(), unit_name = unitName ?? "", groups = ListGroups(unitName), blocks = List("", "", "", false, "", unitName).ToArray() }), new UTF8Encoding(false));
            List<string> changes = new List<string>();

            _session.RunInTransaction("Organize Block Groups", true, delegate
            {
                foreach (KeyValuePair<string, string> pair in rename)
                {
                    if (!existing.ContainsKey(pair.Key) || string.Equals(pair.Key, pair.Value, StringComparison.Ordinal)) continue;
                    object group = existing[pair.Key];
                    var property = group.GetType().GetProperty("Name");
                    try
                    {
                        if (property != null && property.CanWrite) property.SetValue(group, pair.Value, null);
                        else Api.Invoke(group, "SetAttribute", "Name", pair.Value);
                    }
                    catch (Exception ex)
                    {
                        Exception cause = ex;
                        while (cause.InnerException != null) cause = cause.InnerException;
                        if (!(cause is NotSupportedException) && cause.Message.IndexOf("not supported", StringComparison.OrdinalIgnoreCase) < 0) throw;
                        if (Api.Items(Api.Get(group, "Blocks", true)).Any() || Api.Items(Api.Get(group, "Groups", true)).Any())
                            throw new InvalidOperationException("当前 TIA 不支持组重命名，且原组非空，未移动/删除内容：" + pair.Key, ex);
                        object replacement = Api.Invoke(groups, "Create", pair.Value);
                        Api.Invoke(group, "Delete");
                        group = replacement;
                    }
                    existing.Remove(pair.Key); existing[pair.Value] = group;
                    changes.Add("rename: " + pair.Key + " -> " + pair.Value);
                }
                foreach (string name in names)
                    if (!existing.ContainsKey(name))
                    {
                        existing[name] = Api.Invoke(groups, "Create", name);
                        changes.Add("create: " + name);
                    }
            });

            _plc.InvalidateBlockCache();
            return new { saved = false, unit_name = unitName ?? "", snapshot = backup, changes = changes.ToArray(), groups = ListGroups(unitName) };
        }

        public string ReadSource(string name, string groupPath, out string extension, out string resolvedGroupPath, string unitName = "")
        {
            bool refresh = true;
            return ReadSourceCore(name, groupPath, unitName, out extension, out resolvedGroupPath, ref refresh);
        }

        private string ReadSourceCore(string name, string groupPath, string unitName, out string extension, out string resolvedGroupPath, ref bool refreshOnMiss)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("block_name 不能为空。");

            string trimmedName = name.Trim();
            string normalizedGroup = PathTools.NormalizeGroupPath(groupPath);
            string normUnit = (unitName ?? "").Trim();
            bool scoped = !string.IsNullOrWhiteSpace(groupPath);
            object container = SoftwareUnitHelper.ResolveContainer(Api, _plc, normUnit);
            object targetItem = null;

            if (scoped && _plc.BlockCache == null)
            {
                object directBlock = TryFindItemInGroupPath(Api.Get(container, "BlockGroup", true), "Blocks", normalizedGroup, trimmedName);
                if (directBlock != null)
                {
                    targetItem = directBlock;
                    resolvedGroupPath = normalizedGroup;
                    extension = ResolveSourceExtension(name, targetItem);
                    return ExportReadableSource(container, targetItem, extension);
                }
                object directType = TryFindItemInGroupPath(Api.Get(container, "TypeGroup", true), "Types", normalizedGroup, trimmedName);
                if (directType != null)
                {
                    targetItem = directType;
                    resolvedGroupPath = normalizedGroup;
                    extension = ".udt";
                    return ExportReadableSource(container, targetItem, extension);
                }
            }

            CachedBlockEntry[] blockMatches = GetCachedBlocks()
                .Where(e => string.Equals(e.Name, trimmedName, StringComparison.OrdinalIgnoreCase)
                    && (normUnit.Length == 0 || string.Equals(e.UnitName ?? "", normUnit, StringComparison.OrdinalIgnoreCase))
                    && (!scoped || string.Equals(e.GroupPath, normalizedGroup, StringComparison.OrdinalIgnoreCase)))
                .ToArray();

            if (blockMatches.Length == 1)
            {
                targetItem = blockMatches[0].Item;
                resolvedGroupPath = blockMatches[0].GroupPath;
                container = SoftwareUnitHelper.ResolveContainer(Api, _plc, blockMatches[0].UnitName);
                extension = ResolveSourceExtension(name, targetItem);
            }
            else if (blockMatches.Length > 1)
            {
                throw new InvalidOperationException("存在多个同名块 '" + name + "'，请指定 group_path 或 unit_name: "
                    + string.Join(", ", blockMatches.Select(m => (string.IsNullOrEmpty(m.UnitName) ? "" : "[" + m.UnitName + "]") + m.GroupPath)));
            }
            else
            {
                CachedBlockEntry[] typeMatches = GetCachedTypes()
                    .Where(e => string.Equals(e.Name, trimmedName, StringComparison.OrdinalIgnoreCase)
                        && (normUnit.Length == 0 || string.Equals(e.UnitName ?? "", normUnit, StringComparison.OrdinalIgnoreCase))
                        && (!scoped || string.Equals(e.GroupPath, normalizedGroup, StringComparison.OrdinalIgnoreCase)))
                    .ToArray();
                if (typeMatches.Length == 0 && refreshOnMiss)
                {
                    _plc.InvalidateBlockCache();
                    _plc.InvalidateTypeCache();
                    refreshOnMiss = false;
                    return ReadSourceCore(name, groupPath, unitName, out extension, out resolvedGroupPath, ref refreshOnMiss);
                }
                if (typeMatches.Length != 1)
                    throw new InvalidOperationException("找不到名为 '" + name + "' 的程序块或 UDT 数据类型。");
                targetItem = typeMatches[0].Item;
                resolvedGroupPath = typeMatches[0].GroupPath;
                container = SoftwareUnitHelper.ResolveContainer(Api, _plc, typeMatches[0].UnitName);
                extension = ".udt";
            }

            return ExportReadableSource(container, targetItem, extension);
        }

        public object[] ReadSourcesBatch(string[] names, string groupPath, string unitName = "")
        {
            if (names == null || names.Length == 0)
                throw new ArgumentException("block_names 不能为空。");
            List<object> results = new List<object>(names.Length);
            bool refreshOnMiss = true;
            foreach (string rawName in names)
            {
                string name = (rawName ?? "").Trim();
                if (string.IsNullOrEmpty(name)) continue;
                try
                {
                    string ext;
                    string resolvedGroup;
                    string source = ReadSourceCore(name, groupPath, unitName, out ext, out resolvedGroup, ref refreshOnMiss);
                    results.Add(new
                    {
                        name = name,
                        group_path = resolvedGroup,
                        unit_name = unitName ?? "",
                        extension = ext,
                        source = source,
                        ok = true,
                        error = ""
                    });
                }
                catch (Exception ex)
                {
                    results.Add(new
                    {
                        name = name,
                        group_path = groupPath ?? "",
                        unit_name = unitName ?? "",
                        extension = "",
                        source = "",
                        ok = false,
                        error = ex.Message
                    });
                }
            }
            return results.ToArray();
        }

        private string ResolveSourceExtension(string displayName, object targetItem)
        {
            string language = Api.GetString(targetItem, "ProgrammingLanguage");
            string typeName = targetItem.GetType().Name;
            if (string.Equals(language, "SCL", StringComparison.OrdinalIgnoreCase)) return ".scl";
            if (string.Equals(language, "STL", StringComparison.OrdinalIgnoreCase)) return ".awl";
            if (string.Equals(language, "DB", StringComparison.OrdinalIgnoreCase)
                || typeName.IndexOf("DataBlock", StringComparison.OrdinalIgnoreCase) >= 0
                || typeName.EndsWith("DB", StringComparison.OrdinalIgnoreCase)) return ".db";
            throw new InvalidOperationException("程序块 '" + displayName + "' 的语言为 " + language + "，不支持导出传统外部源文件；在 TIA V21 中请优先使用 tia_read_document (.s7dcl) 或 tia_read_block (XML)。");
        }

        private string ExportReadableSource(object container, object targetItem, string extension)
        {
            string tempFile = Path.Combine(Path.GetTempPath(), "tia-src-" + Guid.NewGuid().ToString("N") + extension);
            try
            {
                object externalGroup = Api.Get(container, "ExternalSourceGroup", false)
                    ?? Api.Get(_plc.Software, "ExternalSourceGroup", true);
                Array items = Api.OneItemInterfaceArray("Siemens.Engineering.SW.ExternalSources.IGenerateSource", targetItem);
                object options = Api.EnumValueBySimpleName("None", "GenerateOptions");
                using (OperationMetrics.Measure("export")) Api.Invoke(externalGroup, "GenerateSource", items, new FileInfo(tempFile), options);
                return File.ReadAllText(tempFile, Encoding.UTF8);
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        private object TryFindItemInGroupPath(object rootGroup, string collectionProperty, string normalizedGroup, string itemName)
        {
            try
            {
                object group = rootGroup;
                foreach (string part in PathTools.SplitGroupPath(normalizedGroup))
                {
                    object groups = Api.Get(group, "Groups", true);
                    object next = TryFindByName(groups, part);
                    if (next == null) return null;
                    group = next;
                }
                object collection = Api.Get(group, collectionProperty, true);
                return TryFindByName(collection, itemName);
            }
            catch
            {
                return null;
            }
        }

        private object TryFindByName(object composition, string name)
        {
            return TryFindByNameStatic(Api, composition, name);
        }

        private static object TryFindByNameStatic(ReflectionApi api, object composition, string name)
        {
            if (composition == null || string.IsNullOrWhiteSpace(name)) return null;
            try
            {
                object exact = api.Invoke(composition, "Find", name);
                if (exact != null) return exact;
            }
            catch
            {
            }
            return api.Items(composition).FirstOrDefault(item =>
                string.Equals(api.GetString(item, "Name"), name, StringComparison.OrdinalIgnoreCase));
        }

        private CachedBlockEntry FindSingleBlockFast(string name, string groupPath, string unitName = "")
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException("block_name 不能为空。");
            string trimmedName = name.Trim();
            string normalizedGroup = PathTools.NormalizeGroupPath(groupPath);
            string normUnit = (unitName ?? "").Trim();
            bool scoped = !string.IsNullOrWhiteSpace(groupPath);

            if (scoped && _plc.BlockCache == null)
            {
                object container = SoftwareUnitHelper.ResolveContainer(Api, _plc, normUnit);
                object direct = TryFindItemInGroupPath(Api.Get(container, "BlockGroup", true), "Blocks", normalizedGroup, trimmedName);
                if (direct != null)
                {
                    return SoftwareUnitHelper.BuildEntry(Api, direct, normalizedGroup, normUnit, false);
                }
            }

            CachedBlockEntry[] matches = FilterEntries(GetCachedBlocks(), trimmedName, normalizedGroup, scoped, normUnit);
            if (matches.Length == 0)
            {
                matches = FilterEntries(GetCachedBlocks(true), trimmedName, normalizedGroup, scoped, normUnit);
            }

            if (matches.Length == 0)
                throw new InvalidOperationException("找不到程序块 '" + trimmedName + "'"
                    + (string.IsNullOrEmpty(normalizedGroup) ? "" : "（组路径: " + normalizedGroup + "）")
                    + (string.IsNullOrEmpty(normUnit) ? "。" : "（Software Unit: " + normUnit + "）。"));
            if (matches.Length > 1)
                throw new InvalidOperationException("程序块名称 '" + trimmedName + "' 在多个位置存在，请指定 group_path 或 unit_name: "
                    + string.Join(", ", matches.Select(m => (string.IsNullOrEmpty(m.UnitName) ? "" : "[" + m.UnitName + "]") + m.GroupPath)));
            return matches[0];
        }

        private CachedBlockEntry FindSingleTypeFast(string name, string groupPath, string unitName = "")
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException("type_name 不能为空。");
            string trimmedName = name.Trim();
            string normalizedGroup = PathTools.NormalizeGroupPath(groupPath);
            string normUnit = (unitName ?? "").Trim();
            bool scoped = !string.IsNullOrWhiteSpace(groupPath);

            CachedBlockEntry[] matches = FilterEntries(GetCachedTypes(), trimmedName, normalizedGroup, scoped, normUnit);
            if (matches.Length == 0)
            {
                matches = FilterEntries(GetCachedTypes(true), trimmedName, normalizedGroup, scoped, normUnit);
            }
            if (matches.Length == 0)
                throw new InvalidOperationException("找不到 PLC 数据类型 (UDT/NVT) '" + trimmedName + "'。");
            if (matches.Length > 1)
                throw new InvalidOperationException("数据类型 '" + trimmedName + "' 在多个位置存在，请指定 group_path 或 unit_name。");
            return matches[0];
        }

        private static CachedBlockEntry[] FilterEntries(IEnumerable<CachedBlockEntry> all, string name, string normalizedGroup, bool scoped, string normUnit)
        {
            return all.Where(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)
                && (normUnit.Length == 0 || string.Equals(e.UnitName ?? "", normUnit, StringComparison.OrdinalIgnoreCase))
                && (!scoped || string.Equals(e.GroupPath, normalizedGroup, StringComparison.OrdinalIgnoreCase))).ToArray();
        }

        private string Export(object item, bool stripDocumentInfo = false)
        {
            string file = Path.Combine(Path.GetTempPath(), "tia-block-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                object exportOpt = Api.EnumValue("Siemens.Engineering.ExportOptions", "WithDefaults");
                using (OperationMetrics.Measure("export"))
                {
                    if (stripDocumentInfo && Api.TryRequireType("Siemens.Engineering.DocumentInfoOptions") != null)
                    {
                        object docInfoOpt = Api.EnumValue("Siemens.Engineering.DocumentInfoOptions", "None");
                        Api.Invoke(item, "Export", new FileInfo(file), exportOpt, docInfoOpt);
                    }
                    else
                    {
                        Api.Invoke(item, "Export", new FileInfo(file), exportOpt);
                    }
                }
                return File.ReadAllText(file, Encoding.UTF8);
            }
            finally
            {
                if (File.Exists(file)) File.Delete(file);
            }
        }

        internal static XmlDocument ParseBlock(string xml, bool preserveWhitespace = false)
        {
            if (string.IsNullOrWhiteSpace(xml))
                throw new InvalidOperationException("XML 内容不能为空。");
            XmlDocument doc = new XmlDocument { PreserveWhitespace = preserveWhitespace };
            doc.XmlResolver = null;
            using (XmlReader reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
            {
                doc.Load(reader);
            }
            XmlNodeList children = doc.SelectNodes("/Document/*[starts-with(local-name(),'SW.Blocks.')]");
            string[] supported = new[] { "SW.Blocks.FC", "SW.Blocks.FB", "SW.Blocks.OB", "SW.Blocks.GlobalDB", "SW.Blocks.InstanceDB", "SW.Blocks.ArrayDB" };
            if (children == null || children.Count != 1 || !supported.Contains(children[0].Name))
                throw new InvalidOperationException("仅接受包含一个普通程序块（FC/FB/OB/DB）的 SimaticML Document。");
            return doc;
        }

        public string Import(string xml, string groupPath, string backupRoot, bool createGroupIfMissing = true)
        {
            object importedBlock;
            return Import(xml, groupPath, backupRoot, createGroupIfMissing, out importedBlock, false, "", "");
        }

        public string Import(
            string xml,
            string groupPath,
            string backupRoot,
            bool createGroupIfMissing,
            out object importedBlock,
            bool skipBackup = false,
            string unitName = "",
            string swImportOptionsText = "")
        {
            importedBlock = null;
            XmlDocument doc = ParseBlock(xml);
            XmlNode blockNode = doc.SelectSingleNode("/Document/*[starts-with(local-name(),'SW.Blocks.')]");
            XmlNode nameNode = blockNode.SelectSingleNode("AttributeList/Name");
            string name = nameNode == null ? "" : nameNode.InnerText.Trim();
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException("块名称不能为空。");

            XmlNode engineering = doc.SelectSingleNode("/Document/Engineering/@version");
            if (engineering != null && !string.Equals(engineering.Value, Api.LoadedPortalVersion, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("XML 版本 (" + engineering.Value + ") 与当前连接的 TIA 版本 (" + Api.LoadedPortalVersion + ") 不一致。");

            string normUnit = (unitName ?? "").Trim();
            object container = SoftwareUnitHelper.ResolveContainer(Api, _plc, normUnit);
            object rootBlockGroup = Api.Get(container, "BlockGroup", true);
            List<CachedBlockEntry> unitBlocks = GetCachedBlocks(true)
                .Where(e => string.Equals(e.UnitName ?? "", normUnit, StringComparison.OrdinalIgnoreCase))
                .ToList();

            string normalizedGroup = PathTools.NormalizeGroupPath(groupPath);
            CachedBlockEntry existingAnywhere = unitBlocks.FirstOrDefault(e =>
                string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

            if (existingAnywhere != null)
            {
                if (string.IsNullOrEmpty(normalizedGroup))
                {
                    normalizedGroup = existingAnywhere.GroupPath;
                }
                else if (!string.Equals(existingAnywhere.GroupPath, normalizedGroup, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("同名块 '" + name + "' 已存在于组 '" + existingAnywhere.GroupPath
                        + "' 中，不能同时导入到另一个组 '" + normalizedGroup + "'。请将 group_path 设为 '" + existingAnywhere.GroupPath + "' 或留空。");
                }
            }

            object targetGroup = ResolveGroup(rootBlockGroup, normalizedGroup, createGroupIfMissing);
            object blocks = Api.Get(targetGroup, "Blocks", true);
            object existing = existingAnywhere != null ? existingAnywhere.Item : null;

            XmlNode numberNode = blockNode.SelectSingleNode("AttributeList/Number");
            string number = numberNode == null ? "" : numberNode.InnerText.Trim();
            string targetCategory = GetBlockCategory(blockNode.Name);

            if (!string.IsNullOrEmpty(number))
            {
                foreach (CachedBlockEntry entry in unitBlocks)
                {
                    if (existing != null && string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))
                        continue;
                    string entryCategory = entry.Category;
                    string entryNumber = Convert.ToString(entry.Number ?? "");
                    if (string.Equals(entryCategory, targetCategory, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(entryNumber, number, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("同类型块号 (" + targetCategory + number
                            + ") 已被块 '" + entry.Name + "' 占用，请换一个块号。");
                    }
                }
            }

            string backup = "新块，无原块备份";
            if (existing != null && !skipBackup)
            {
                using (OperationMetrics.Measure("backup")) backup = Backup(existing, name, existingAnywhere.GroupPath, "Block", backupRoot);
            }
            else if (existing != null) backup = "已按请求跳过备份";

            string file = Path.Combine(Path.GetTempPath(), "tia-import-" + Guid.NewGuid().ToString("N") + ".xml");
            object resolvedBlock = null;
            try
            {
                doc.Save(file);
                object importOpt = Api.EnumValue("Siemens.Engineering.ImportOptions", existing == null ? "None" : "Override");
                object swOpt = BuildSwImportOptions(swImportOptionsText);

                _session.RunInTransaction("Import Block " + name, true, delegate
                {
                    using (OperationMetrics.Measure("import"))
                    {
                        if (swOpt != null)
                            Api.Invoke(blocks, "Import", new FileInfo(file), importOpt, swOpt);
                        else
                            Api.Invoke(blocks, "Import", new FileInfo(file), importOpt);
                    }
                    resolvedBlock = TryFindByName(blocks, name);
                });

                importedBlock = resolvedBlock;
                UpdateBlockCacheEntry(name, normalizedGroup, normUnit, importedBlock);
                return backup;
            }
            catch (Exception ex)
            {
                _plc.InvalidateBlockCache();
                throw new InvalidOperationException("导入失败；未保存。备份：" + backup + "；" + ex.Message, ex);
            }
            finally
            {
                if (File.Exists(file)) File.Delete(file);
            }
        }

        public BatchImportResult ImportBlocksBatch(
            string itemsJson,
            string backupRoot,
            bool createGroupIfMissing,
            string defaultSwImportOptions,
            out List<object> importedObjects)
        {
            if (string.IsNullOrWhiteSpace(itemsJson))
                throw new ArgumentException("items_json 不能为空。");
            JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024 };
            object[] rawItems = serializer.DeserializeObject(itemsJson) as object[];
            if (rawItems == null || rawItems.Length == 0)
                throw new ArgumentException("items_json 必须是非空 JSON 数组。");
            if (rawItems.Length > 200)
                throw new ArgumentException("单批最多导入 200 个 XML 对象。");

            string effectiveSwOpt = string.IsNullOrWhiteSpace(defaultSwImportOptions)
                ? "IgnoreStructuralChanges, IgnoreMissingReferencedObjects"
                : defaultSwImportOptions;

            var parsed = rawItems.Select((item, idx) =>
            {
                Dictionary<string, object> map = item as Dictionary<string, object>;
                if (map == null) throw new ArgumentException("items_json 第 " + idx + " 项必须是 JSON 对象。");
                object xmlObj, grpObj, unitObj, optObj;
                string xml = map.TryGetValue("xml", out xmlObj) && xmlObj != null ? Convert.ToString(xmlObj) : "";
                string grp = map.TryGetValue("group_path", out grpObj) && grpObj != null ? Convert.ToString(grpObj) : "";
                string unit = map.TryGetValue("unit_name", out unitObj) && unitObj != null ? Convert.ToString(unitObj) : "";
                string opt = map.TryGetValue("sw_import_options", out optObj) && optObj != null ? Convert.ToString(optObj) : effectiveSwOpt;

                XmlDocument doc = ParsePlcObject(xml);
                XmlNode rootNode = doc.SelectSingleNode("/Document/*[starts-with(local-name(),'SW.')]");
                string nodeName = rootNode.Name;
                XmlNode nNode = rootNode.SelectSingleNode("AttributeList/Name");
                string objName = nNode == null ? "" : nNode.InnerText.Trim();
                bool isType = nodeName.StartsWith("SW.Types.", StringComparison.Ordinal);
                bool isTagTable = nodeName == "SW.Tags.PlcTagTable";
                int order = isType ? 0 : isTagTable ? 1 : (nodeName.EndsWith("DB", StringComparison.Ordinal) ? 3 : (nodeName.EndsWith("OB", StringComparison.Ordinal) ? 4 : 2));
                return new { Index = idx, Name = objName, NodeName = nodeName, IsType = isType, IsTagTable = isTagTable, Xml = xml, GroupPath = grp, UnitName = unit, SwOpt = opt, Order = order };
            }).OrderBy(x => x.Order).ThenBy(x => x.Index).ToArray();

            List<object> collectedObjects = new List<object>();
            List<object> results = new List<object>();
            int successCount = 0;
            int failureCount = 0;

            BatchImportResult outcome = _session.RunInTransactionConditional("Batch Import V21 XML (" + parsed.Length + " objects)", true, delegate
            {
                foreach (var entry in parsed)
                {
                    try
                    {
                        string bkp;
                        if (entry.IsType)
                        {
                            bkp = ImportType(entry.Xml, entry.GroupPath, backupRoot, entry.UnitName, entry.SwOpt);
                        }
                        else if (entry.IsTagTable)
                        {
                            bkp = ImportTagTable(entry.Xml, entry.GroupPath, backupRoot, createGroupIfMissing, entry.UnitName);
                        }
                        else
                        {
                            object imp;
                            bkp = Import(entry.Xml, entry.GroupPath, backupRoot, createGroupIfMissing, out imp, false, entry.UnitName, entry.SwOpt);
                            if (imp != null) collectedObjects.Add(imp);
                        }
                        successCount++;
                        results.Add(new { name = entry.Name, kind = entry.NodeName, group_path = entry.GroupPath, unit_name = entry.UnitName, ok = true, backup = bkp, error = "" });
                    }
                    catch (Exception ex)
                    {
                        failureCount++;
                        results.Add(new { name = entry.Name, kind = entry.NodeName, group_path = entry.GroupPath, unit_name = entry.UnitName, ok = false, backup = "", error = ex.Message });
                        break;
                    }
                }

                bool ok = failureCount == 0;
                BatchImportResult res = new BatchImportResult
                {
                    success_count = ok ? successCount : 0,
                    failure_count = failureCount,
                    rolled_back = !ok,
                    items = results.ToArray()
                };
                return Tuple.Create(res, ok);
            });

            if (outcome.rolled_back)
            {
                _plc.InvalidateBlockCache();
                _plc.InvalidateTypeCache();
                collectedObjects.Clear();
            }

            importedObjects = collectedObjects;
            return outcome;
        }

        private object BuildSwImportOptions(string swImportOptionsText)
        {
            if (string.IsNullOrWhiteSpace(swImportOptionsText)) return null;
            if (Api.TryRequireType("Siemens.Engineering.SW.SWImportOptions") == null) return null;
            string[] flags = swImportOptionsText.Split(new[] { ',', '|', ';' }, StringSplitOptions.RemoveEmptyEntries);
            return flags.Length == 0 ? null : Api.CombineEnumFlags("Siemens.Engineering.SW.SWImportOptions", flags);
        }

        public string ImportSource(string sourceText, string format, string nameHint, string groupPath, string backupRoot, bool createGroupIfMissing = true)
        {
            object primaryImported;
            return ImportSource(sourceText, format, nameHint, groupPath, backupRoot, createGroupIfMissing, out primaryImported, true, "");
        }

        public string ImportSource(string sourceText, string format, string nameHint, string groupPath, string backupRoot, bool createGroupIfMissing, out object primaryImported, bool refreshCache = true, string unitName = "")
        {
            primaryImported = null;
            if (string.IsNullOrWhiteSpace(sourceText))
                throw new ArgumentException("source 文本源码不能为空。");

            string normalizedFormat = (format ?? "scl").Trim().TrimStart('.').ToLowerInvariant();
            if (normalizedFormat != "scl" && normalizedFormat != "db" && normalizedFormat != "udt" && normalizedFormat != "awl")
                throw new ArgumentException("format 仅支持 scl、db、udt 或 awl。");

            bool isUdt = normalizedFormat == "udt";
            string normalizedGroup = PathTools.NormalizeGroupPath(groupPath);
            string normUnit = (unitName ?? "").Trim();

            List<string> candidateNames = new List<string>();
            if (!string.IsNullOrWhiteSpace(nameHint))
            {
                candidateNames.Add(nameHint.Trim());
            }
            foreach (Match m in SourceHeaderRegex.Matches(sourceText))
            {
                string extracted = !string.IsNullOrEmpty(m.Groups[1].Value) ? m.Groups[1].Value : m.Groups[2].Value;
                if (!string.IsNullOrWhiteSpace(extracted) && !candidateNames.Contains(extracted, StringComparer.OrdinalIgnoreCase))
                {
                    candidateNames.Add(extracted.Trim());
                }
            }

            object container = SoftwareUnitHelper.ResolveContainer(Api, _plc, normUnit);
            object rootGroup = isUdt
                ? Api.Get(container, "TypeGroup", true)
                : Api.Get(container, "BlockGroup", true);

            List<CachedBlockEntry> allItems = (isUdt ? GetCachedTypes(refreshCache) : GetCachedBlocks(refreshCache))
                .Where(e => string.Equals(e.UnitName ?? "", normUnit, StringComparison.OrdinalIgnoreCase))
                .ToList();
            List<string> backups = new List<string>();

            foreach (string candidate in candidateNames)
            {
                CachedBlockEntry existingEntry = allItems.FirstOrDefault(e =>
                    string.Equals(e.Name, candidate, StringComparison.OrdinalIgnoreCase));
                if (existingEntry != null)
                {
                    if (string.IsNullOrEmpty(normalizedGroup))
                    {
                        normalizedGroup = existingEntry.GroupPath;
                    }
                    string bkp = Backup(existingEntry.Item, existingEntry.Name, existingEntry.GroupPath,
                        isUdt ? "Type" : "Block", backupRoot);
                    backups.Add(bkp);
                }
            }
            object targetGroup = ResolveGroup(rootGroup, normalizedGroup, createGroupIfMissing);
            string backup = backups.Count == 0
                ? "新源码对象，无原 XML 备份"
                : string.Join("; ", backups);

            string tempFile = Path.Combine(Path.GetTempPath(), "TiaSourceImport_" + Guid.NewGuid().ToString("N") + "." + normalizedFormat);
            object resolvedPrimary = null;
            try
            {
                File.WriteAllText(tempFile, sourceText, new UTF8Encoding(true));
                _session.RunInTransaction("Import Source " + (candidateNames.Count > 0 ? candidateNames[0] : normalizedFormat), true, delegate
                {
                    object externalSource = null;
                    try
                    {
                        object externalGroup = Api.Get(container, "ExternalSourceGroup", false)
                            ?? Api.Get(_plc.Software, "ExternalSourceGroup", true);
                        object sources = Api.Get(externalGroup, "ExternalSources", true);
                        string tempName = "McpImport_" + Guid.NewGuid().ToString("N");
                        externalSource = Api.Invoke(sources, "CreateFromFile", tempName, tempFile);
                        object option = Api.EnumValueBySimpleName("None", "GenerateBlockOption", "GenerateBlockOptions");

                        if (string.IsNullOrEmpty(normalizedGroup))
                        {
                            try { Api.Invoke(externalSource, "GenerateBlocksFromSource", option); }
                            catch (MissingMethodException) { Api.Invoke(externalSource, "GenerateBlocksFromSource"); }
                        }
                        else
                        {
                            try { Api.Invoke(externalSource, "GenerateBlocksFromSource", targetGroup, option); }
                            catch (MissingMethodException) { Api.Invoke(externalSource, "GenerateBlocksFromSource", option); }
                        }

                        if (!isUdt && candidateNames.Count == 1)
                        {
                            object blocks = Api.Get(targetGroup, "Blocks", true);
                            resolvedPrimary = TryFindByName(blocks, candidateNames[0]);
                        }
                    }
                    finally
                    {
                        if (externalSource != null)
                        {
                            try { Api.Invoke(externalSource, "Delete"); }
                            catch { }
                        }
                    }
                });

                primaryImported = resolvedPrimary;
                if (isUdt)
                {
                    _plc.InvalidateTypeCache();
                }
                else if (primaryImported != null && candidateNames.Count == 1)
                {
                    UpdateBlockCacheEntry(candidateNames[0], normalizedGroup, normUnit, primaryImported);
                }
                else
                {
                    _plc.InvalidateBlockCache();
                }
                return backup;
            }
            catch (Exception ex)
            {
                if (isUdt) _plc.InvalidateTypeCache();
                else _plc.InvalidateBlockCache();
                throw new InvalidOperationException("源码导入失败；未保存。备份：" + backup + "；" + ex.Message, ex);
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        public BatchImportResult ImportSourcesBatch(string itemsJson, string backupRoot, bool createGroupIfMissing = true)
        {
            if (string.IsNullOrWhiteSpace(itemsJson))
                throw new ArgumentException("items_json 不能为空。");
            JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 };
            object[] rawItems = serializer.DeserializeObject(itemsJson) as object[];
            if (rawItems == null || rawItems.Length == 0)
                throw new ArgumentException("items_json 必须是非空 JSON 数组。");

            var parsed = rawItems.Select((item, idx) =>
            {
                var map = item as Dictionary<string, object>;
                if (map == null) throw new ArgumentException("items_json 第 " + idx + " 项必须是对象。");
                object srcObj, fmtObj, nameObj, grpObj, unitObj;
                string src = map.TryGetValue("source", out srcObj) && srcObj != null ? Convert.ToString(srcObj) : "";
                string fmt = map.TryGetValue("format", out fmtObj) && fmtObj != null ? Convert.ToString(fmtObj) : "scl";
                string name = map.TryGetValue("block_name", out nameObj) && nameObj != null ? Convert.ToString(nameObj) : "";
                string grp = map.TryGetValue("group_path", out grpObj) && grpObj != null ? Convert.ToString(grpObj) : "";
                string unit = map.TryGetValue("unit_name", out unitObj) && unitObj != null ? Convert.ToString(unitObj) : "";
                string normFmt = fmt.Trim().TrimStart('.').ToLowerInvariant();
                int order = normFmt == "udt" ? 0 : (normFmt == "db" ? 2 : 1);
                return new { Index = idx, Source = src, Format = normFmt, Name = name, GroupPath = grp, UnitName = unit, Order = order };
            }).OrderBy(x => x.Order).ThenBy(x => x.Index).ToArray();

            List<object> results = new List<object>();
            int successCount = 0;
            int failureCount = 0;
            _plc.InvalidateBlockCache();
            _plc.InvalidateTypeCache();

            return _session.RunInTransactionConditional("Batch Import Sources (" + parsed.Length + " items)", true, delegate
            {
                foreach (var entry in parsed)
                {
                    try
                    {
                        object primary;
                        string backup = ImportSource(entry.Source, entry.Format, entry.Name, entry.GroupPath, backupRoot, createGroupIfMissing, out primary, false, entry.UnitName);
                        successCount++;
                        results.Add(new { block_name = entry.Name, format = entry.Format, group_path = entry.GroupPath, unit_name = entry.UnitName, ok = true, backup = backup, error = "" });
                    }
                    catch (Exception ex)
                    {
                        failureCount++;
                        results.Add(new { block_name = entry.Name, format = entry.Format, group_path = entry.GroupPath, unit_name = entry.UnitName, ok = false, backup = "", error = ex.Message });
                        break;
                    }
                }
                bool ok = failureCount == 0;
                return Tuple.Create(new BatchImportResult
                {
                    success_count = ok ? successCount : 0,
                    failure_count = failureCount,
                    rolled_back = !ok,
                    items = results.ToArray()
                }, ok);
            });
        }

        public string RestoreBackup(string backupFilePath, string groupPath, string backupRoot, string unitName = "")
        {
            if (string.IsNullOrWhiteSpace(backupFilePath))
                throw new ArgumentException("backup_path 不能为空。");
            string fullPath = Path.GetFullPath(backupFilePath);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException("找不到备份文件: " + fullPath, fullPath);
            string xml = File.ReadAllText(fullPath, Encoding.UTF8);
            XmlDocument doc = ParsePlcObject(xml);
            XmlNode node = doc.SelectSingleNode("/Document/*[starts-with(local-name(),'SW.')]");
            XmlNode nameNode = node.SelectSingleNode("AttributeList/Name");
            string name = nameNode == null ? "" : nameNode.InnerText.Trim();
            if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("备份缺少对象名称。");
            string kind = node.Name.StartsWith("SW.Types.", StringComparison.Ordinal) ? "Type"
                : node.Name == "SW.Tags.PlcTagTable" ? "TagTable" : "Block";
            BackupRecord record = BackupRecord.Read(fullPath, xml, _session.GetProjectPath(), _plc.DisplayName);
            string targetGroup = groupPath;
            string normUnit = (unitName ?? "").Trim();
            if (record != null)
            {
                if (record.Kind != kind || !string.Equals(record.Name, name, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("备份对象类型或名称与元数据不一致。");
                if (!string.IsNullOrWhiteSpace(groupPath)
                    && !string.Equals(PathTools.NormalizeGroupPath(groupPath), record.GroupPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("group_path 与备份原组不一致，拒绝恢复到其他组。");
                targetGroup = record.GroupPath;
                object container = SoftwareUnitHelper.ResolveContainer(Api, _plc, normUnit);
                string existingGroup = null;
                if (kind == "TagTable")
                    existingGroup = TagTables(Api.Get(container, "TagTableGroup", true), "").Where(e => string.Equals(Api.GetString(e.Item2, "Name"), name, StringComparison.OrdinalIgnoreCase)).Select(e => e.Item1).SingleOrDefault();
                else
                    existingGroup = (kind == "Type" ? GetCachedTypes(true) : GetCachedBlocks(true))
                        .Where(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase) && string.Equals(e.UnitName ?? "", normUnit, StringComparison.OrdinalIgnoreCase))
                        .Select(e => e.GroupPath).SingleOrDefault();
                if (existingGroup != null && !string.Equals(existingGroup, record.GroupPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("当前同名对象已移至其他组，拒绝恢复到错误位置。");
            }
            if (kind == "Type") return ImportType(xml, targetGroup, backupRoot, normUnit, "");
            if (kind == "TagTable") return ImportTagTable(xml, targetGroup, backupRoot, true, normUnit);
            object restored;
            return Import(xml, targetGroup, backupRoot, true, out restored, false, normUnit, "");
        }

        internal static XmlDocument ParsePlcObject(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) throw new ArgumentException("XML 内容不能为空。");
            var doc = new XmlDocument { XmlResolver = null };
            using (XmlReader reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) doc.Load(reader);
            XmlNodeList objects = doc.SelectNodes("/Document/*[starts-with(local-name(),'SW.')]");
            string[] supported = { "SW.Types.PlcStruct", "SW.Types.PlcNamedValueType", "SW.Tags.PlcTagTable", "SW.Blocks.FC", "SW.Blocks.FB",
                "SW.Blocks.OB", "SW.Blocks.GlobalDB", "SW.Blocks.InstanceDB", "SW.Blocks.ArrayDB" };
            if (objects.Count != 1 || !supported.Contains(objects[0].Name))
                throw new InvalidOperationException("仅接受包含一个程序块、UDT/NVT 或 PLC 变量表的 SimaticML Document。");
            return doc;
        }

        private void ValidateVersion(XmlDocument doc)
        {
            XmlNode version = doc.SelectSingleNode("/Document/Engineering/@version");
            if (version != null && !string.Equals(version.Value, Api.LoadedPortalVersion, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("备份或导入 XML 的 TIA 版本与当前连接不一致。");
        }

        private string ImportType(string xml, string groupPath, string backupRoot, string unitName = "", string swImportOptionsText = "")
        {
            XmlDocument doc = ParsePlcObject(xml);
            ValidateVersion(doc);
            XmlNode typeNode = doc.SelectSingleNode("/Document/*[starts-with(local-name(),'SW.Types.')]");
            string name = typeNode.SelectSingleNode("AttributeList/Name").InnerText.Trim();
            string normalizedGroup = PathTools.NormalizeGroupPath(groupPath);
            string normUnit = (unitName ?? "").Trim();
            CachedBlockEntry existing = GetCachedTypes(true).SingleOrDefault(e =>
                string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(e.UnitName ?? "", normUnit, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                if (string.IsNullOrEmpty(normalizedGroup)) normalizedGroup = existing.GroupPath;
                else if (!string.Equals(existing.GroupPath, normalizedGroup, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("同名 UDT/NVT 已存在于其他组：" + existing.GroupPath);
            }
            object container = SoftwareUnitHelper.ResolveContainer(Api, _plc, normUnit);
            object group = ResolveGroup(Api.Get(container, "TypeGroup", true), normalizedGroup, true);
            string backup = existing == null ? "新 UDT，无原备份" : Backup(existing.Item, name, existing.GroupPath, "Type", backupRoot);
            string file = Path.Combine(Path.GetTempPath(), "tia-type-restore-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                doc.Save(file);
                object importOpt = Api.EnumValue("Siemens.Engineering.ImportOptions", existing == null ? "None" : "Override");
                object swOpt = BuildSwImportOptions(swImportOptionsText);
                _session.RunInTransaction("Import Type " + name, true, delegate
                {
                    if (swOpt != null)
                        Api.Invoke(Api.Get(group, "Types", true), "Import", new FileInfo(file), importOpt, swOpt);
                    else
                        Api.Invoke(Api.Get(group, "Types", true), "Import", new FileInfo(file), importOpt);
                });
                return backup;
            }
            finally
            {
                _plc.InvalidateTypeCache();
                if (File.Exists(file)) File.Delete(file);
            }
        }

        public string DeleteBlock(string name, string groupPath, string backupRoot, string unitName = "")
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("block_name 不能为空。");
            string trimmedName = name.Trim();
            string normalizedGroup = PathTools.NormalizeGroupPath(groupPath);
            string normUnit = (unitName ?? "").Trim();

            CachedBlockEntry[] blockMatches = GetCachedBlocks(true)
                .Where(e => string.Equals(e.Name, trimmedName, StringComparison.OrdinalIgnoreCase)
                    && (normUnit.Length == 0 || string.Equals(e.UnitName ?? "", normUnit, StringComparison.OrdinalIgnoreCase))
                    && (string.IsNullOrEmpty(normalizedGroup) || string.Equals(e.GroupPath, normalizedGroup, StringComparison.OrdinalIgnoreCase)))
                .ToArray();

            if (blockMatches.Length > 1)
                throw new InvalidOperationException("存在多个同名块 '" + trimmedName + "'，请指定 group_path 或 unit_name: "
                    + string.Join(", ", blockMatches.Select(m => (string.IsNullOrEmpty(m.UnitName) ? "" : "[" + m.UnitName + "]") + m.GroupPath)));

            if (blockMatches.Length == 1)
            {
                CachedBlockEntry target = blockMatches[0];
                string backup = Backup(target.Item, target.Name, target.GroupPath, "Block", backupRoot, "deleted-");
                _session.RunInTransaction("Delete Block " + target.Name, true, delegate
                {
                    Api.Invoke(target.Item, "Delete");
                });
                if (_plc.BlockCache != null)
                {
                    _plc.BlockCache.RemoveAll(b => string.Equals(b.Name, target.Name, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(b.UnitName ?? "", target.UnitName ?? "", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(b.GroupPath, target.GroupPath, StringComparison.OrdinalIgnoreCase));
                }
                return backup;
            }

            CachedBlockEntry[] typeMatches = GetCachedTypes(true)
                .Where(e => string.Equals(e.Name, trimmedName, StringComparison.OrdinalIgnoreCase)
                    && (normUnit.Length == 0 || string.Equals(e.UnitName ?? "", normUnit, StringComparison.OrdinalIgnoreCase))
                    && (string.IsNullOrEmpty(normalizedGroup) || string.Equals(e.GroupPath, normalizedGroup, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            if (typeMatches.Length == 1)
            {
                CachedBlockEntry target = typeMatches[0];
                string backup = Backup(target.Item, target.Name, target.GroupPath, "Type", backupRoot, "deleted-udt-");
                _session.RunInTransaction("Delete Type " + target.Name, true, delegate
                {
                    Api.Invoke(target.Item, "Delete");
                });
                if (_plc.TypeCache != null)
                {
                    _plc.TypeCache.RemoveAll(t => string.Equals(t.Name, target.Name, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(t.UnitName ?? "", target.UnitName ?? "", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(t.GroupPath, target.GroupPath, StringComparison.OrdinalIgnoreCase));
                }
                return backup;
            }

            throw new InvalidOperationException("找不到要删除的程序块或 UDT '" + trimmedName + "'。");
        }

        private void UpdateBlockCacheEntry(string name, string normalizedGroup, string unitName, object blockItem)
        {
            if (_plc.BlockCache == null || blockItem == null)
            {
                _plc.InvalidateBlockCache();
                return;
            }
            try
            {
                string normUnit = unitName ?? "";
                _plc.BlockCache.RemoveAll(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(b.UnitName ?? "", normUnit, StringComparison.OrdinalIgnoreCase));
                _plc.BlockCache.Add(SoftwareUnitHelper.BuildEntry(Api, blockItem, normalizedGroup ?? "", normUnit, false));
            }
            catch
            {
                _plc.InvalidateBlockCache();
            }
        }

        private string Backup(object item, string name, string group, string kind, string root, string prefix = "")
        {
            return BackupRecord.Write(root, prefix + name, Export(item, false), _session.GetProjectPath(),
                _plc.DisplayName, group, kind, name);
        }

        private object ResolveGroup(object root, string normalizedGroupPath, bool createIfMissing)
        {
            object group = root;
            foreach (string part in PathTools.SplitGroupPath(normalizedGroupPath))
            {
                object groups = Api.Get(group, "Groups", true);
                object next = TryFindByName(groups, part);
                if (next == null)
                {
                    if (!createIfMissing)
                        throw new InvalidOperationException("目标组 '" + normalizedGroupPath + "' 不存在；请先在 TIA 创建组，或启用 create_group_if_missing。");
                    next = Api.Invoke(groups, "Create", part);
                }
                group = next;
            }
            return group;
        }

        private static string GetBlockCategory(string xmlOrTypeName)
        {
            if (string.IsNullOrWhiteSpace(xmlOrTypeName)) return "";
            string clean = xmlOrTypeName.StartsWith("SW.Blocks.", StringComparison.OrdinalIgnoreCase)
                ? xmlOrTypeName.Substring("SW.Blocks.".Length)
                : xmlOrTypeName;
            if (clean.IndexOf("DB", StringComparison.OrdinalIgnoreCase) >= 0
                || clean.IndexOf("DataBlock", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "DB";
            }
            return clean.ToUpperInvariant();
        }
    }
}
