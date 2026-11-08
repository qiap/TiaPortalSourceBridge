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
            var list = ScanGroup("", false).ToList();
            _plc.BlockCache = list;
            return list;
        }

        private IEnumerable<CachedBlockEntry> ScanGroup(string path, bool types)
        {
            OperationMetrics.Count(types ? "type_catalog_scans" : "block_catalog_scans");
            object root = Api.Get(_plc.Software, types ? "TypeGroup" : "BlockGroup", true);
            object group = string.IsNullOrEmpty(path) ? root : ResolveGroup(root, path, false);
            foreach (var entry in types ? Types(group, path) : Blocks(group, path))
            {
                string name = Api.GetString(entry.Item2, "Name");
                yield return new CachedBlockEntry
                {
                    Name = name, GroupPath = entry.Item1, Item = entry.Item2,
                    Number = types ? null : Api.Get(entry.Item2, "Number", false),
                    Language = types ? "UDT" : Api.GetString(entry.Item2, "ProgrammingLanguage"),
                    Category = types ? "UDT" : GetBlockCategory(entry.Item2.GetType().Name)
                };
            }
        }

        public List<CachedBlockEntry> GetCachedTypes(bool forceRefresh = false)
        {
            if (!forceRefresh && _plc.TypeCache != null)
            {
                OperationMetrics.Count("type_cache_hits");
                return _plc.TypeCache;
            }
            var list = ScanGroup("", true).ToList();
            _plc.TypeCache = list;
            return list;
        }

        public IEnumerable<object> List(string groupFilter = "", string nameFilter = "", string languageFilter = "", bool refresh = false, string scopePath = "")
        {
            string normGroup = PathTools.NormalizeGroupPath(groupFilter);
            string normName = (nameFilter ?? "").Trim();
            string normLang = (languageFilter ?? "").Trim();

            foreach (CachedBlockEntry entry in string.IsNullOrWhiteSpace(scopePath) ? GetCachedBlocks(refresh) : ScanGroup(PathTools.NormalizeGroupPath(scopePath), false))
            {
                if (!string.IsNullOrEmpty(normGroup)
                    && !string.Equals(entry.GroupPath, normGroup, StringComparison.OrdinalIgnoreCase)
                    && !entry.GroupPath.StartsWith(normGroup + "/", StringComparison.OrdinalIgnoreCase)
                    && entry.GroupPath.IndexOf(normGroup, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (!string.IsNullOrEmpty(normName)
                    && entry.Name.IndexOf(normName, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (!string.IsNullOrEmpty(normLang)
                    && !string.Equals(entry.Language, normLang, StringComparison.OrdinalIgnoreCase))
                    continue;

                yield return new
                {
                    name = entry.Name,
                    group_path = entry.GroupPath,
                    number = entry.Number,
                    language = entry.Language
                };
            }
        }

        public IEnumerable<object> ListTypes(string groupFilter = "", string nameFilter = "", bool refresh = false, string scopePath = "")
        {
            string normGroup = PathTools.NormalizeGroupPath(groupFilter);
            string normName = (nameFilter ?? "").Trim();

            foreach (CachedBlockEntry entry in string.IsNullOrWhiteSpace(scopePath) ? GetCachedTypes(refresh) : ScanGroup(PathTools.NormalizeGroupPath(scopePath), true))
            {
                if (!string.IsNullOrEmpty(normGroup)
                    && !string.Equals(entry.GroupPath, normGroup, StringComparison.OrdinalIgnoreCase)
                    && !entry.GroupPath.StartsWith(normGroup + "/", StringComparison.OrdinalIgnoreCase)
                    && entry.GroupPath.IndexOf(normGroup, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (!string.IsNullOrEmpty(normName)
                    && entry.Name.IndexOf(normName, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                yield return new
                {
                    name = entry.Name,
                    group_path = entry.GroupPath,
                    language = "UDT"
                };
            }
        }

        public IEnumerable<object> ListTagTables()
        {
            object rootGroup = Api.Get(_plc.Software, "TagTableGroup", true);
            foreach (Tuple<string, object> entry in TagTables(rootGroup, ""))
            {
                object tags = Api.Get(entry.Item2, "Tags", false);
                object constants = Api.Get(entry.Item2, "UserConstants", false);
                yield return new
                {
                    name = Api.GetString(entry.Item2, "Name"),
                    group_path = entry.Item1,
                    tag_count = Api.Items(tags).Count(),
                    constant_count = Api.Items(constants).Count()
                };
            }
        }

        public string ReadTagTable(string name, string groupPath)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("table_name 不能为空。");
            string trimmedName = name.Trim();
            string normalizedGroup = PathTools.NormalizeGroupPath(groupPath);
            object rootGroup = Api.Get(_plc.Software, "TagTableGroup", true);

            Tuple<string, object>[] matches = TagTables(rootGroup, "")
                .Where(e => string.Equals(Api.GetString(e.Item2, "Name"), trimmedName, StringComparison.OrdinalIgnoreCase)
                    && (string.IsNullOrWhiteSpace(groupPath) || string.Equals(e.Item1, normalizedGroup, StringComparison.OrdinalIgnoreCase)))
                .ToArray();

            if (matches.Length == 0)
                throw new InvalidOperationException("找不到 PLC 变量表 '" + trimmedName + "'。");
            if (matches.Length > 1)
                throw new InvalidOperationException("存在多个同名 PLC 变量表 '" + trimmedName + "'，请指定 group_path: "
                    + string.Join(", ", matches.Select(m => m.Item1)));

            return Export(matches[0].Item2);
        }

        public string ImportTagTable(string xml, string groupPath, string backupRoot, bool createGroupIfMissing = true)
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

            object rootGroup = Api.Get(_plc.Software, "TagTableGroup", true);
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
                Api.Invoke(tables, "Import", new FileInfo(file),
                    Api.EnumValue("Siemens.Engineering.ImportOptions", "Override"));
                return backup;
            }
            finally
            {
                if (File.Exists(file)) File.Delete(file);
            }
        }

        public string Read(string name, string groupPath)
        {
            CachedBlockEntry match = FindSingleBlockFast(name, groupPath);
            return Export(match.Item);
        }

        internal GraphBlockSnapshot ReadGraphSnapshot(string name, string groupPath)
        {
            CachedBlockEntry entry = FindSingleBlockFast(name, groupPath);
            using (OperationMetrics.Measure("graph_read"))
                return new GraphBlockSnapshot { Entry = entry, Xml = Export(entry.Item) };
        }

        internal string ImportGraphSnapshot(GraphBlockSnapshot snapshot, XmlDocument document, string backupRoot, bool skipBackup, out object importedBlock)
        {
            var entry = snapshot.Entry;
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
            object group = ResolveGroup(Api.Get(_plc.Software, "BlockGroup", true), entry.GroupPath, false);
            string file = Path.Combine(Path.GetTempPath(), "tia-graph-import-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                document.Save(file);
                using (OperationMetrics.Measure("import"))
                    Api.Invoke(Api.Get(group, "Blocks", true), "Import", new FileInfo(file), Api.EnumValue("Siemens.Engineering.ImportOptions", "Override"));
                importedBlock = TryFindByName(Api.Get(group, "Blocks", true), entry.Name);
                UpdateBlockCacheEntry(entry.Name, entry.GroupPath, importedBlock);
                OperationMetrics.Count("graph_imports");
                return backup;
            }
            catch { _plc.InvalidateBlockCache(); throw; }
            finally { if (File.Exists(file)) File.Delete(file); }
        }

        public object[] ListGroups()
        {
            return GroupRows(Api.Get(_plc.Software, "BlockGroup", true), "").ToArray();
        }

        private IEnumerable<object> GroupRows(object parent, string path)
        {
            foreach (object group in Api.Items(Api.Get(parent, "Groups", true)))
            {
                string name = Api.GetString(group, "Name");
                string childPath = path == "" ? name : path + "/" + name;
                yield return new { name = name, group_path = childPath,
                    block_count = Api.Items(Api.Get(group, "Blocks", true)).Count() };
                foreach (object child in GroupRows(group, childPath)) yield return child;
            }
        }

        public object OrganizeGroups(string parentPath, string renameJson, string namesJson, string backupRoot)
        {
            var json = new JavaScriptSerializer();
            var rename = json.Deserialize<Dictionary<string, string>>(renameJson);
            var names = json.Deserialize<string[]>(namesJson);
            if (rename == null || names == null) throw new InvalidOperationException("需要重命名字典和目标组名称数组。");
            foreach (string name in rename.Keys.Concat(rename.Values).Concat(names))
                if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(new[] { '/', '\\' }) >= 0)
                    throw new InvalidOperationException("组名称不能为空或包含路径分隔符。");
            object parent = ResolveGroup(Api.Get(_plc.Software, "BlockGroup", true), PathTools.NormalizeGroupPath(parentPath), false);
            object groups = Api.Get(parent, "Groups", true);
            var existing = Api.Items(groups).ToDictionary(g => Api.GetString(g, "Name"), g => g, StringComparer.OrdinalIgnoreCase);
            if (rename.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != rename.Count)
                throw new InvalidOperationException("多个组不能重命名为相同目标。");
            foreach (var pair in rename)
                if (!string.Equals(pair.Key, pair.Value, StringComparison.OrdinalIgnoreCase)
                    && existing.ContainsKey(pair.Key) && existing.ContainsKey(pair.Value))
                    throw new InvalidOperationException("原组和目标组同时存在，不自动合并：" + pair.Key + " / " + pair.Value);
            Directory.CreateDirectory(backupRoot);
            string backup = Path.Combine(backupRoot, "groups-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(backup, json.Serialize(new { project_path = _session.GetProjectPath(), groups = ListGroups(), blocks = List().ToArray() }), new UTF8Encoding(false));
            var changes = new List<string>();
            foreach (var pair in rename)
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
                    // Some V17 objects expose a Name setter but reject it at runtime.
                    // Only an empty leaf may be replaced; never move/delete any blocks.
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
            _plc.InvalidateBlockCache();
            return new { saved = false, snapshot = backup, changes = changes.ToArray(), groups = ListGroups() };
        }

        public string ReadSource(string name, string groupPath, out string extension, out string resolvedGroupPath)
        {
            bool refresh = true;
            return ReadSourceCore(name, groupPath, out extension, out resolvedGroupPath, ref refresh);
        }

        private string ReadSourceCore(string name, string groupPath, out string extension, out string resolvedGroupPath, ref bool refreshOnMiss)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("block_name 不能为空。");

            string trimmedName = name.Trim();
            string normalizedGroup = PathTools.NormalizeGroupPath(groupPath);
            bool scoped = !string.IsNullOrWhiteSpace(groupPath);
            object targetItem = null;

            // 1. 若提供了明确 group_path 且尚未构建全量缓存，先尝试直达查找（仅需 2~3 次 IPC）
            if (scoped && _plc.BlockCache == null)
            {
                object directBlock = TryFindItemInGroupPath(Api.Get(_plc.Software, "BlockGroup", true), "Blocks", normalizedGroup, trimmedName);
                if (directBlock != null)
                {
                    targetItem = directBlock;
                    resolvedGroupPath = normalizedGroup;
                    extension = ResolveSourceExtension(name, targetItem);
                    return ExportReadableSource(targetItem, extension);
                }
                object directType = TryFindItemInGroupPath(Api.Get(_plc.Software, "TypeGroup", true), "Types", normalizedGroup, trimmedName);
                if (directType != null)
                {
                    targetItem = directType;
                    resolvedGroupPath = normalizedGroup;
                    extension = ".udt";
                    return ExportReadableSource(targetItem, extension);
                }
            }

            // 2. 使用内存缓存快速检索
            CachedBlockEntry[] blockMatches = GetCachedBlocks()
                .Where(e => string.Equals(e.Name, trimmedName, StringComparison.OrdinalIgnoreCase)
                    && (!scoped || string.Equals(e.GroupPath, normalizedGroup, StringComparison.OrdinalIgnoreCase)))
                .ToArray();

            if (blockMatches.Length == 1)
            {
                targetItem = blockMatches[0].Item;
                resolvedGroupPath = blockMatches[0].GroupPath;
                extension = ResolveSourceExtension(name, targetItem);
            }
            else if (blockMatches.Length > 1)
            {
                throw new InvalidOperationException("存在多个同名块 '" + name + "'，请指定 group_path: "
                    + string.Join(", ", blockMatches.Select(m => m.GroupPath)));
            }
            else
            {
                CachedBlockEntry[] typeMatches = GetCachedTypes()
                    .Where(e => string.Equals(e.Name, trimmedName, StringComparison.OrdinalIgnoreCase)
                        && (!scoped || string.Equals(e.GroupPath, normalizedGroup, StringComparison.OrdinalIgnoreCase)))
                    .ToArray();
                if (typeMatches.Length == 0 && refreshOnMiss)
                {
                    _plc.InvalidateBlockCache();
                    _plc.InvalidateTypeCache();
                    refreshOnMiss = false;
                    return ReadSourceCore(name, groupPath, out extension, out resolvedGroupPath, ref refreshOnMiss);
                }
                if (typeMatches.Length != 1)
                    throw new InvalidOperationException("找不到名为 '" + name + "' 的程序块或 UDT 数据类型。");
                targetItem = typeMatches[0].Item;
                resolvedGroupPath = typeMatches[0].GroupPath;
                extension = ".udt";
            }

            return ExportReadableSource(targetItem, extension);
        }

        public object[] ReadSourcesBatch(string[] names, string groupPath)
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
                    string source = ReadSourceCore(name, groupPath, out ext, out resolvedGroup, ref refreshOnMiss);
                    results.Add(new
                    {
                        name = name,
                        group_path = resolvedGroup,
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
            throw new InvalidOperationException("程序块 '" + displayName + "' 的语言为 " + language + "，不支持导出为纯文本源码；请改用 tia_read_block 读取 XML。");
        }

        private string ExportReadableSource(object targetItem, string extension)
        {
            string tempFile = Path.Combine(Path.GetTempPath(), "tia-src-" + Guid.NewGuid().ToString("N") + extension);
            try
            {
                object externalGroup = Api.Get(_plc.Software, "ExternalSourceGroup", true);
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
            if (composition == null || string.IsNullOrWhiteSpace(name)) return null;
            try
            {
                object exact = Api.Invoke(composition, "Find", name);
                if (exact != null) return exact;
            }
            catch
            {
            }
            return Api.Items(composition).FirstOrDefault(item =>
                string.Equals(Api.GetString(item, "Name"), name, StringComparison.OrdinalIgnoreCase));
        }

        private CachedBlockEntry FindSingleBlockFast(string name, string groupPath)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException("block_name 不能为空。");
            string trimmedName = name.Trim();
            string normalizedGroup = PathTools.NormalizeGroupPath(groupPath);
            bool scoped = !string.IsNullOrWhiteSpace(groupPath);

            // 若指定了 group_path 且尚未缓存全工程，直接按路径查找，省去全工程扫描
            if (scoped && _plc.BlockCache == null)
            {
                object direct = TryFindItemInGroupPath(Api.Get(_plc.Software, "BlockGroup", true), "Blocks", normalizedGroup, trimmedName);
                if (direct != null)
                {
                    return new CachedBlockEntry
                    {
                        Name = Api.GetString(direct, "Name"),
                        GroupPath = normalizedGroup,
                        Number = Api.Get(direct, "Number", false),
                        Language = Api.GetString(direct, "ProgrammingLanguage"),
                        Category = GetBlockCategory(direct.GetType().Name),
                        Item = direct
                    };
                }
            }

            CachedBlockEntry[] matches = GetCachedBlocks()
                .Where(e => string.Equals(e.Name, trimmedName, StringComparison.OrdinalIgnoreCase)
                    && (!scoped || string.Equals(e.GroupPath, normalizedGroup, StringComparison.OrdinalIgnoreCase)))
                .ToArray();

            if (matches.Length == 0)
            {
                // 如果缓存未命中，尝试刷新一次缓存以防用户在 TIA 界面手动新建了块
                matches = GetCachedBlocks(true)
                    .Where(e => string.Equals(e.Name, trimmedName, StringComparison.OrdinalIgnoreCase)
                        && (!scoped || string.Equals(e.GroupPath, normalizedGroup, StringComparison.OrdinalIgnoreCase)))
                    .ToArray();
            }

            if (matches.Length == 0)
                throw new InvalidOperationException("找不到程序块 '" + trimmedName + "'"
                    + (string.IsNullOrEmpty(normalizedGroup) ? "。" : "（组路径: " + normalizedGroup + "）。"));
            if (matches.Length > 1)
                throw new InvalidOperationException("程序块名称 '" + trimmedName + "' 在多个组中存在，请指定 group_path: "
                    + string.Join(", ", matches.Select(m => m.GroupPath)));
            return matches[0];
        }

        private string Export(object item)
        {
            string file = Path.Combine(Path.GetTempPath(), "tia-block-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                object options = Api.EnumValue("Siemens.Engineering.ExportOptions", "WithDefaults");
                try
                {
                    using (OperationMetrics.Measure("export")) Api.Invoke(item, "Export", new FileInfo(file), options);
                }
                catch (Exception first)
                {
                    // TIA 会拒绝导出处于"未编译"状态的对象：刚被导入且 compile_mode=none，
                    // 或上一次编译失败，都会落到这里（错误文本只有 "Error when calling method
                    // 'Export' of type 'Siemens.Engineering.SW.Blocks.FB'"，对使用者没有信息量）。
                    // 导出的目的是给覆盖前的备份，编译只发生在内存里：不改工程数据、不保存、
                    // 不下载，所以这里自动补齐编译再重试一次。
                    string name = "";
                    try { name = Api.GetString(item, "Name"); } catch (Exception) { }
                    CompileSummary compiled;
                    try
                    {
                        using (OperationMetrics.Measure("export_recompile")) compiled = _session.CompileObject(item);
                    }
                    catch (Exception compileError)
                    {
                        throw new InvalidOperationException("无法导出" + Describe(name) + "作为备份：它可能处于未编译状态，重新编译也失败（"
                            + compileError.Message + "）。请先在 TIA Portal 中编译该对象，或改用 backup_mode=skip 跳过备份。原始导出错误："
                            + first.Message, first);
                    }

                    try
                    {
                        using (OperationMetrics.Measure("export_retry")) Api.Invoke(item, "Export", new FileInfo(file), options);
                    }
                    catch (Exception second)
                    {
                        throw new InvalidOperationException("无法导出" + Describe(name) + "作为备份：重新编译（错误 "
                            + compiled.ErrorCount + " 个、警告 " + compiled.WarningCount
                            + " 个）后 TIA 仍拒绝导出。请先在 TIA Portal 中打开并编译该对象，或改用 backup_mode=skip 跳过备份。原始错误："
                            + second.Message, second);
                    }
                }

                return File.ReadAllText(file, Encoding.UTF8);
            }
            finally
            {
                if (File.Exists(file)) File.Delete(file);
            }
        }

        private static string Describe(string name)
        {
            return string.IsNullOrWhiteSpace(name) ? "该对象" : "对象 '" + name + "'";
        }

        internal static XmlDocument ParseBlock(string xml, bool preserveWhitespace = false)
        {
            if (string.IsNullOrWhiteSpace(xml))
                throw new InvalidOperationException("XML 内容不能为空。");
            XmlDocument doc = new XmlDocument { PreserveWhitespace = preserveWhitespace };
            doc.XmlResolver = null;
            using (XmlTextReader reader = new XmlTextReader(new StringReader(xml))
                { Normalization = false, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })
            {
                doc.Load(reader);
            }
            XmlNodeList children = doc.SelectNodes("/Document/*[starts-with(local-name(),'SW.Blocks.')]");
            string[] supported = new[] { "SW.Blocks.FC", "SW.Blocks.FB", "SW.Blocks.OB", "SW.Blocks.GlobalDB", "SW.Blocks.InstanceDB", "SW.Blocks.ArrayDB" };
            if (children == null || children.Count != 1 || !supported.Contains(children[0].Name))
                throw new InvalidOperationException("仅接受包含一个普通程序块（FC/FB/OB/DB）的 SimaticML Document。");
            return doc;
        }

        internal static void ValidateCompileUnitOrder(XmlDocument doc)
        {
            // SimaticML streams each ordered composition as a single group. If a
            // block title/comment splits CompileUnits, the importer starts that
            // composition a second time and fails after earlier objects changed.
            foreach (XmlElement list in doc.SelectNodes("/Document/*[starts-with(local-name(),'SW.Blocks.')]/ObjectList"))
            {
                bool seen = false, closed = false;
                foreach (XmlElement child in list.ChildNodes.OfType<XmlElement>())
                {
                    bool unit = child.LocalName == "SW.Blocks.CompileUnit" || child.GetAttribute("CompositionName") == "CompileUnits";
                    if (unit)
                    {
                        if (closed) throw new InvalidOperationException("程序网络（CompileUnits）被标题或其他对象分隔，必须连续排列；请重新生成 XML，尚未写入。");
                        seen = true;
                    }
                    else if (seen) closed = true;
                }
            }
        }

        public string Import(string xml, string groupPath, string backupRoot, bool createGroupIfMissing = true)
        {
            object importedBlock;
            return Import(xml, groupPath, backupRoot, createGroupIfMissing, out importedBlock);
        }

        public string Import(string xml, string groupPath, string backupRoot, bool createGroupIfMissing, out object importedBlock, bool skipBackup = false, bool refreshCache = true)
        {
            importedBlock = null;
            XmlDocument doc = ParseBlock(xml);
            ValidateCompileUnitOrder(doc);
            XmlNode blockNode = doc.SelectSingleNode("/Document/*[starts-with(local-name(),'SW.Blocks.')]");
            XmlNode nameNode = blockNode.SelectSingleNode("AttributeList/Name");
            string name = nameNode == null ? "" : nameNode.InnerText.Trim();
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException("块名称不能为空。");

            XmlNode engineering = doc.SelectSingleNode("/Document/Engineering/@version");
            if (engineering != null && !string.Equals(engineering.Value, Api.LoadedPortalVersion, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("XML 版本 (" + engineering.Value + ") 与当前连接的 TIA 版本 (" + Api.LoadedPortalVersion + ") 不一致。");

            object rootBlockGroup = Api.Get(_plc.Software, "BlockGroup", true);
            // A user may have edited the project in TIA since the last cached read.
            List<CachedBlockEntry> allBlocks = GetCachedBlocks(refreshCache);

            string normalizedGroup = PathTools.NormalizeGroupPath(groupPath);
            CachedBlockEntry existingAnywhere = allBlocks.FirstOrDefault(e =>
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
                foreach (CachedBlockEntry entry in allBlocks)
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
            try
            {
                doc.Save(file);
                using (OperationMetrics.Measure("import"))
                    Api.Invoke(blocks, "Import", new FileInfo(file),
                        Api.EnumValue("Siemens.Engineering.ImportOptions", existing == null ? "None" : "Override"));
                importedBlock = TryFindByName(blocks, name);
                UpdateBlockCacheEntry(name, normalizedGroup, importedBlock);
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

        public string ImportSource(string sourceText, string format, string nameHint, string groupPath, string backupRoot, bool createGroupIfMissing = true)
        {
            object primaryImported;
            return ImportSource(sourceText, format, nameHint, groupPath, backupRoot, createGroupIfMissing, out primaryImported);
        }

        public string ImportSource(string sourceText, string format, string nameHint, string groupPath, string backupRoot, bool createGroupIfMissing, out object primaryImported, bool refreshCache = true)
        {
            primaryImported = null;
            if (string.IsNullOrWhiteSpace(sourceText))
                throw new ArgumentException("source 文本源码不能为空。");

            string normalizedFormat = (format ?? "scl").Trim().TrimStart('.').ToLowerInvariant();
            if (normalizedFormat != "scl" && normalizedFormat != "db" && normalizedFormat != "udt" && normalizedFormat != "awl")
                throw new ArgumentException("format 仅支持 scl、db、udt 或 awl。");

            bool isUdt = normalizedFormat == "udt";
            string normalizedGroup = PathTools.NormalizeGroupPath(groupPath);

            // 自动从源码头部提取块或 UDT 名称（兼容未显式传 block_name 或单文件含多块的情况）
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

            object rootGroup = isUdt
                ? Api.Get(_plc.Software, "TypeGroup", true)
                : Api.Get(_plc.Software, "BlockGroup", true);

            List<CachedBlockEntry> allItems = isUdt ? GetCachedTypes(refreshCache) : GetCachedBlocks(refreshCache);
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
            object externalSource = null;
            try
            {
                File.WriteAllText(tempFile, sourceText, new UTF8Encoding(true));
                object externalGroup = Api.Get(_plc.Software, "ExternalSourceGroup", true);
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

                if (isUdt)
                {
                    _plc.InvalidateTypeCache();
                }
                else
                {
                    if (candidateNames.Count == 1)
                    {
                        object blocks = Api.Get(targetGroup, "Blocks", true);
                        primaryImported = TryFindByName(blocks, candidateNames[0]);
                        if (primaryImported != null)
                        {
                            UpdateBlockCacheEntry(candidateNames[0], normalizedGroup, primaryImported);
                        }
                        else
                        {
                            _plc.InvalidateBlockCache();
                        }
                    }
                    else
                    {
                        _plc.InvalidateBlockCache();
                    }
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
                if (externalSource != null)
                {
                    try { Api.Invoke(externalSource, "Delete"); }
                    catch { }
                }
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        public XmlBatchResult ImportXmlBatch(string itemsJson, string backupRoot, Func<CompileSummary> compile, string relatedBlocksJson = null, string operationId = null)
        {
            var items = XmlBatch.Parse(itemsJson);
            var related = XmlBatch.ParseRelated(relatedBlocksJson, items);
            var catalog = GetCachedBlocks(true);
            string directory = XmlBatch.CreateJournalDirectory(backupRoot, operationId);
            string journal = Path.Combine(directory, "result.json");
            var serializer = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 };
            bool relatedFinished = related.Length == 0;
            Action<XmlBatchResult> persist = result =>
            {
                if (!relatedFinished && result.phase == "complete") result.phase = "verify";
                result.journal = journal;
                result.related_items = related;
                string temp = journal + ".tmp";
                File.WriteAllText(temp, serializer.Serialize(result), Encoding.UTF8);
                if (File.Exists(journal)) File.Replace(temp, journal, null); else File.Move(temp, journal);
            };
            bool relatedValidated = false, relatedBackedUp = false;
            try
            {
                var result = XmlBatch.Run(items, item =>
                {
                    if (!relatedValidated) {
                        foreach (var relation in related) {
                            try {
                                var matchesRelated = catalog.Where(e => string.Equals(e.Name, relation.block_name, StringComparison.OrdinalIgnoreCase)).ToArray();
                                if (matchesRelated.Length != 1) throw new InvalidOperationException("关联对象必须为唯一现有程序块：" + relation.block_name);
                                relation.Entry = matchesRelated[0];
                                string relatedGroup = PathTools.NormalizeGroupPath(relation.group_path);
                                if (!string.IsNullOrWhiteSpace(relation.group_path) && !string.Equals(relatedGroup, relation.Entry.GroupPath, StringComparison.OrdinalIgnoreCase))
                                    throw new InvalidOperationException("关联对象组与当前工程不一致：" + relation.block_name);
                                relation.group_path = relation.Entry.GroupPath;
                                XmlBatch.ValidateRelatedBefore(relation, Export(relation.Entry.Item));
                            } catch (Exception ex) { relation.status = "failed"; relation.error = ex.Message; throw; }
                        }
                        relatedValidated = true;
                    }
                    var matches = catalog.Where(e => string.Equals(e.Name, item.block_name, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (matches.Length != 1) throw new InvalidOperationException("批量 XML 仅覆盖唯一的现有块：" + item.block_name);
                    item.Entry = matches[0];
                    string group = PathTools.NormalizeGroupPath(item.group_path);
                    if (!string.IsNullOrWhiteSpace(item.group_path) && !string.Equals(group, item.Entry.GroupPath, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("目标组与现有块不一致：" + item.block_name);
                    item.group_path = item.Entry.GroupPath;
                    var doc = ParseBlock(item.Xml);
                    item.Xml = doc.OuterXml; // Preserve attribute line breaks through subsequent serializations.
                    var version = doc.SelectSingleNode("/Document/Engineering/@version");
                    if (version != null && !string.Equals(version.Value, Api.LoadedPortalVersion, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("XML 与连接的 TIA 版本不一致。");
                    item.Before = Export(item.Entry.Item);
                    item.before_hash = XmlBatch.Hash(item.Before);
                    if (!string.IsNullOrEmpty(item.expected_hash) && !string.Equals(item.expected_hash, item.before_hash, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("原块指纹已变化，请重新读取：" + item.block_name);
                    if (!string.IsNullOrEmpty(item.expected_content_signature) && !string.Equals(item.expected_content_signature, XmlBatch.ContentSignature(item.Before), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("原块内容已变化，请重新读取；尚未写入：" + item.block_name);
                    if (!string.IsNullOrEmpty(item.expected_recovery_signature) && !string.Equals(item.expected_recovery_signature, XmlBatch.RecoverySignature(item.Before), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("原块内容、标题或注释已变化，请重新核对恢复方案；尚未写入：" + item.block_name);
                    var before = ParseBlock(item.Before);
                    string path = "/Document/*[starts-with(local-name(),'SW.Blocks.')]";
                    var a = doc.SelectSingleNode(path); var b = before.SelectSingleNode(path);
                    if (a.Name != b.Name || a.SelectSingleNode("AttributeList/Number").InnerText != b.SelectSingleNode("AttributeList/Number").InnerText)
                        throw new InvalidOperationException("批量覆盖不允许更改块类型或块号。");
                }, item =>
                {
                    if (!relatedBackedUp) {
                        foreach (var relation in related) {
                            try {
                                relation.backup = BackupRecord.Write(directory, "related-" + relation.block_name, relation.Before,
                                    _session.GetProjectPath(), _plc.DisplayName, relation.group_path, "Block", relation.block_name);
                                relation.status = "backed_up";
                            } catch (Exception ex) { relation.status = "failed"; relation.error = ex.Message; throw; }
                        }
                        relatedBackedUp = true;
                    }
                    File.WriteAllText(Path.Combine(directory, Array.IndexOf(items, item).ToString("D2") + ".requested.xml"), item.Xml, Encoding.UTF8);
                    item.backup = BackupRecord.Write(directory, item.block_name, item.Before, _session.GetProjectPath(), _plc.DisplayName,
                        item.group_path, "Block", item.block_name);
                }, item =>
                {
                    object imported;
                    Import(item.Xml, item.group_path, directory, false, out imported, true, false);
                }, compile, item =>
                {
                    string after = Read(item.block_name, item.group_path);
                    item.index_xml = after;
                    // Number is fixed during preflight; filenames do not use user block names.
                    string prefix = Array.IndexOf(items, item).ToString("D2");
                    File.WriteAllText(Path.Combine(directory, prefix + ".after.xml"), after, Encoding.UTF8);
                    File.WriteAllText(Path.Combine(directory, prefix + ".requested.xml"), item.Xml, Encoding.UTF8);
                    item.after_hash = XmlBatch.Hash(after);
                    item.after_recovery_signature = XmlBatch.RecoverySignature(after);
                    item.differences = XmlBatch.Differences(item.Xml, after);
                    item.verification = item.differences.Length == 0 ? "matched" : "different";
                }, persist);
                // Still inside the same worker call/project lock: compilation can
                // change related instance DBs even after an import failed halfway.
                // Capture these exports here, before returning control to another task.
                if (items.Any(item => item.status == "imported" || item.status == "outcome_unknown")) {
                    result.phase = "verify"; persist(result);
                    foreach (var relation in related) {
                        result.current_block = relation.block_name; persist(result);
                        try {
                            string after = Read(relation.block_name, relation.group_path);
                            string path = Path.Combine(directory, "related-" + Array.IndexOf(related, relation).ToString("D3") + ".after.xml");
                            File.WriteAllText(path, after, Encoding.UTF8);
                            relation.after_path = path;
                            relation.after_hash = XmlBatch.Hash(after);
                            relation.after_signature = XmlBatch.RecoverySignature(after);
                            relation.status = "read_back";
                        } catch (Exception ex) {
                            relation.status = "readback_failed"; relation.error = ex.Message;
                            result.ok = false; result.phase = "needs_review";
                            result.error = (result.error ?? "") + " related readback " + relation.block_name + ": " + ex.Message;
                        }
                        persist(result);
                    }
                }
                relatedFinished = true;
                result.phase = result.ok ? "complete" : "needs_review";
                result.current_block = "";
                persist(result);
                return result;
            }
            finally { _plc.InvalidateBlockCache(); }
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
                object srcObj, fmtObj, nameObj, grpObj;
                string src = map.TryGetValue("source", out srcObj) && srcObj != null ? Convert.ToString(srcObj) : "";
                string fmt = map.TryGetValue("format", out fmtObj) && fmtObj != null ? Convert.ToString(fmtObj) : "scl";
                string name = map.TryGetValue("block_name", out nameObj) && nameObj != null ? Convert.ToString(nameObj) : "";
                string grp = map.TryGetValue("group_path", out grpObj) && grpObj != null ? Convert.ToString(grpObj) : "";
                string normFmt = fmt.Trim().TrimStart('.').ToLowerInvariant();
                int order = normFmt == "udt" ? 0 : (normFmt == "db" ? 2 : 1);
                return new { Index = idx, Source = src, Format = normFmt, Name = name, GroupPath = grp, Order = order };
            }).OrderBy(x => x.Order).ThenBy(x => x.Index).ToArray();

            List<object> results = new List<object>();
            int successCount = 0;
            int failureCount = 0;
            _plc.InvalidateBlockCache(); _plc.InvalidateTypeCache();
            foreach (var entry in parsed)
            {
                try
                {
                    object primary;
                    string backup = ImportSource(entry.Source, entry.Format, entry.Name, entry.GroupPath, backupRoot, createGroupIfMissing, out primary, false);
                    successCount++;
                    results.Add(new { block_name = entry.Name, format = entry.Format, group_path = entry.GroupPath, ok = true, backup = backup, error = "" });
                }
                catch (Exception ex)
                {
                    failureCount++;
                    results.Add(new { block_name = entry.Name, format = entry.Format, group_path = entry.GroupPath, ok = false, backup = "", error = ex.Message });
                }
            }
            return new BatchImportResult { success_count = successCount, failure_count = failureCount, items = results.ToArray() };
        }

        public string RestoreBackup(string backupFilePath, string groupPath, string backupRoot)
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
            string kind = node.Name == "SW.Types.PlcStruct" ? "Type"
                : node.Name == "SW.Tags.PlcTagTable" ? "TagTable" : "Block";
            BackupRecord record = BackupRecord.Read(fullPath, xml, _session.GetProjectPath(), _plc.DisplayName);
            string targetGroup = groupPath;
            if (record != null)
            {
                if (record.Kind != kind || !string.Equals(record.Name, name, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("备份对象类型或名称与元数据不一致。");
                if (!string.IsNullOrWhiteSpace(groupPath)
                    && !string.Equals(PathTools.NormalizeGroupPath(groupPath), record.GroupPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("group_path 与备份原组不一致，拒绝恢复到其他组。");
                targetGroup = record.GroupPath;
                string existingGroup = null;
                if (kind == "TagTable")
                    existingGroup = TagTables(Api.Get(_plc.Software, "TagTableGroup", true), "").Where(e => string.Equals(Api.GetString(e.Item2, "Name"), name, StringComparison.OrdinalIgnoreCase)).Select(e => e.Item1).SingleOrDefault();
                else
                    existingGroup = (kind == "Type" ? GetCachedTypes(true) : GetCachedBlocks(true)).Where(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)).Select(e => e.GroupPath).SingleOrDefault();
                if (existingGroup != null && !string.Equals(existingGroup, record.GroupPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("当前同名对象已移至其他组，拒绝恢复到错误位置。");
            }
            if (kind == "Type") return ImportType(xml, targetGroup, backupRoot);
            if (kind == "TagTable") return ImportTagTable(xml, targetGroup, backupRoot);
            return Import(xml, targetGroup, backupRoot, true);
        }

        internal static XmlDocument ParsePlcObject(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) throw new ArgumentException("XML 内容不能为空。");
            var doc = new XmlDocument { XmlResolver = null };
            using (XmlReader reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) doc.Load(reader);
            XmlNodeList objects = doc.SelectNodes("/Document/*[starts-with(local-name(),'SW.')]");
            string[] supported = { "SW.Types.PlcStruct", "SW.Tags.PlcTagTable", "SW.Blocks.FC", "SW.Blocks.FB",
                "SW.Blocks.OB", "SW.Blocks.GlobalDB", "SW.Blocks.InstanceDB", "SW.Blocks.ArrayDB" };
            if (objects.Count != 1 || !supported.Contains(objects[0].Name))
                throw new InvalidOperationException("仅接受包含一个程序块、UDT 或 PLC 变量表的 SimaticML Document。");
            return doc;
        }

        private void ValidateVersion(XmlDocument doc)
        {
            XmlNode version = doc.SelectSingleNode("/Document/Engineering/@version");
            if (version != null && !string.Equals(version.Value, Api.LoadedPortalVersion, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("备份或导入 XML 的 TIA 版本与当前连接不一致。");
        }

        private string ImportType(string xml, string groupPath, string backupRoot)
        {
            XmlDocument doc = ParsePlcObject(xml);
            ValidateVersion(doc);
            string name = doc.SelectSingleNode("/Document/SW.Types.PlcStruct/AttributeList/Name").InnerText.Trim();
            string normalizedGroup = PathTools.NormalizeGroupPath(groupPath);
            CachedBlockEntry existing = GetCachedTypes(true).SingleOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                if (string.IsNullOrEmpty(normalizedGroup)) normalizedGroup = existing.GroupPath;
                else if (!string.Equals(existing.GroupPath, normalizedGroup, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("同名 UDT 已存在于其他组：" + existing.GroupPath);
            }
            object group = ResolveGroup(Api.Get(_plc.Software, "TypeGroup", true), normalizedGroup, true);
            string backup = existing == null ? "新 UDT，无原备份" : Backup(existing.Item, name, existing.GroupPath, "Type", backupRoot);
            string file = Path.Combine(Path.GetTempPath(), "tia-type-restore-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                doc.Save(file);
                Api.Invoke(Api.Get(group, "Types", true), "Import", new FileInfo(file),
                    Api.EnumValue("Siemens.Engineering.ImportOptions", existing == null ? "None" : "Override"));
                return backup;
            }
            finally
            {
                _plc.InvalidateTypeCache();
                if (File.Exists(file)) File.Delete(file);
            }
        }

        public string DeleteBlock(string name, string groupPath, string backupRoot)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("block_name 不能为空。");
            string trimmedName = name.Trim();
            string normalizedGroup = PathTools.NormalizeGroupPath(groupPath);

            CachedBlockEntry[] blockMatches = GetCachedBlocks(true)
                .Where(e => string.Equals(e.Name, trimmedName, StringComparison.OrdinalIgnoreCase)
                    && (string.IsNullOrEmpty(normalizedGroup) || string.Equals(e.GroupPath, normalizedGroup, StringComparison.OrdinalIgnoreCase)))
                .ToArray();

            if (blockMatches.Length > 1)
                throw new InvalidOperationException("存在多个同名块 '" + trimmedName + "'，请指定 group_path: "
                    + string.Join(", ", blockMatches.Select(m => m.GroupPath)));

            if (blockMatches.Length == 1)
            {
                CachedBlockEntry target = blockMatches[0];
                string backup = Backup(target.Item, target.Name, target.GroupPath, "Block", backupRoot, "deleted-");
                Api.Invoke(target.Item, "Delete");
                if (_plc.BlockCache != null)
                {
                    _plc.BlockCache.RemoveAll(b => string.Equals(b.Name, target.Name, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(b.GroupPath, target.GroupPath, StringComparison.OrdinalIgnoreCase));
                }
                return backup;
            }

            CachedBlockEntry[] typeMatches = GetCachedTypes(true)
                .Where(e => string.Equals(e.Name, trimmedName, StringComparison.OrdinalIgnoreCase)
                    && (string.IsNullOrEmpty(normalizedGroup) || string.Equals(e.GroupPath, normalizedGroup, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            if (typeMatches.Length == 1)
            {
                CachedBlockEntry target = typeMatches[0];
                string backup = Backup(target.Item, target.Name, target.GroupPath, "Type", backupRoot, "deleted-udt-");
                Api.Invoke(target.Item, "Delete");
                if (_plc.TypeCache != null)
                {
                    _plc.TypeCache.RemoveAll(t => string.Equals(t.Name, target.Name, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(t.GroupPath, target.GroupPath, StringComparison.OrdinalIgnoreCase));
                }
                return backup;
            }

            throw new InvalidOperationException("找不到要删除的程序块或 UDT '" + trimmedName + "'。");
        }

        private void UpdateBlockCacheEntry(string name, string normalizedGroup, object blockItem)
        {
            if (_plc.BlockCache == null || blockItem == null)
            {
                _plc.InvalidateBlockCache();
                return;
            }
            try
            {
                string actualName = Api.GetString(blockItem, "Name");
                string language = Api.GetString(blockItem, "ProgrammingLanguage");
                object number = Api.Get(blockItem, "Number", false);
                string category = GetBlockCategory(blockItem.GetType().Name);
                _plc.BlockCache.RemoveAll(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));
                _plc.BlockCache.Add(new CachedBlockEntry
                {
                    Name = actualName,
                    GroupPath = normalizedGroup ?? "",
                    Number = number,
                    Language = language,
                    Category = category,
                    Item = blockItem
                });
            }
            catch
            {
                _plc.InvalidateBlockCache();
            }
        }

        private string Backup(object item, string name, string group, string kind, string root, string prefix = "")
        {
            return BackupRecord.Write(root, prefix + name, Export(item), _session.GetProjectPath(),
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
