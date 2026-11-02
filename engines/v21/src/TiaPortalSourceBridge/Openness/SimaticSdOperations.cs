using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using TiaPortalSourceBridge.Mcp;

namespace TiaPortalSourceBridge.Openness
{
    internal static class SimaticSdOperations
    {
        private static readonly Regex SafeDocBaseRegex = new Regex("^[A-Za-z0-9_.-]{1,120}$", RegexOptions.Compiled);

        public static Dictionary<string, object> ReadDocument(
            OpennessSession session,
            string plcName,
            string objectName,
            string groupPath,
            string unitName,
            string kind,
            bool includeResources)
        {
            if (string.IsNullOrWhiteSpace(objectName))
                throw new ArgumentException("object_name / block_name 不能为空。");

            ReflectionApi api = session.Api;
            PlcContext plc = session.SelectPlc(plcName);
            bool isType = string.Equals(kind, "type", StringComparison.OrdinalIgnoreCase)
                || string.Equals(kind, "udt", StringComparison.OrdinalIgnoreCase);

            CachedBlockEntry found = isType
                ? BlockOperations.ResolveTypeEntry(session, plc, objectName.Trim(), groupPath, unitName)
                : BlockOperations.ResolveBlockEntry(session, plc, objectName.Trim(), groupPath, unitName);

            return ExportItemAsDocument(api, plc, found, isType, includeResources);
        }

        public static Dictionary<string, object> ReadDocumentsBatch(
            OpennessSession session,
            string plcName,
            object[] items,
            bool includeResources)
        {
            if (items == null || items.Length == 0)
                throw new ArgumentException("items_json 至少需要包含一个待读取对象。");
            if (items.Length > 100)
                throw new ArgumentException("单批最多读取 100 个 SimaticSD 文档。");

            ReflectionApi api = session.Api;
            PlcContext plc = session.SelectPlc(plcName);
            List<object> results = new List<object>();
            List<object> errors = new List<object>();

            foreach (object raw in items)
            {
                Dictionary<string, object> map = raw as Dictionary<string, object>;
                if (map == null) continue;
                string name = Text(map, "block_name");
                if (name.Length == 0) name = Text(map, "object_name");
                string group = Text(map, "group_path");
                string unit = Text(map, "unit_name");
                string kind = Text(map, "kind");
                try
                {
                    bool isType = string.Equals(kind, "type", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(kind, "udt", StringComparison.OrdinalIgnoreCase);
                    CachedBlockEntry entry = isType
                        ? BlockOperations.ResolveTypeEntry(session, plc, name, group, unit)
                        : BlockOperations.ResolveBlockEntry(session, plc, name, group, unit);
                    results.Add(ExportItemAsDocument(api, plc, entry, isType, includeResources));
                }
                catch (Exception ex)
                {
                    errors.Add(new Dictionary<string, object>
                    {
                        { "name", name }, { "group_path", group }, { "unit_name", unit }, { "error", ex.Message }
                    });
                }
            }

            return new Dictionary<string, object>
            {
                { "plc", plc.DisplayName },
                { "succeeded_count", results.Count },
                { "failed_count", errors.Count },
                { "documents", results.ToArray() },
                { "errors", errors.ToArray() }
            };
        }

        public static Dictionary<string, object> ImportDocument(
            OpennessSession session,
            string backupRoot,
            string plcName,
            string objectName,
            string s7dcl,
            string s7res,
            Dictionary<string, object> extraResources,
            string groupPath,
            string unitName,
            string kind,
            string importOptionText,
            string compileMode)
        {
            if (string.IsNullOrWhiteSpace(s7dcl))
                throw new ArgumentException("s7dcl 文档内容不能为空。");

            PlcContext plc = session.SelectPlc(plcName);
            bool isType = string.Equals(kind, "type", StringComparison.OrdinalIgnoreCase)
                || string.Equals(kind, "udt", StringComparison.OrdinalIgnoreCase)
                || IsTypeDocument(s7dcl);

            string inferredName = string.IsNullOrWhiteSpace(objectName) ? InferDocumentObjectName(s7dcl) : objectName.Trim();
            string backupId = "";
            if (!string.IsNullOrWhiteSpace(inferredName))
            {
                backupId = BlockOperations.TryBackupExisting(session, backupRoot, plc, inferredName, groupPath, unitName, isType);
            }

            List<object> importedObjects = new List<object>();
            List<string> messages = new List<string>();
            string resultState = "";

            session.RunInTransaction("Import SimaticSD " + (inferredName.Length == 0 ? "Document" : inferredName), true, delegate
            {
                ImportSingleCore(session, plc, inferredName, s7dcl, s7res, extraResources, groupPath, unitName, isType, importOptionText,
                    out resultState, messages, importedObjects);
            });

            if (isType) plc.InvalidateTypeCache();
            else plc.InvalidateBlockCache();

            CompileSummary compile = RunRequestedCompile(session, plc, importedObjects, compileMode);

            return new Dictionary<string, object>
            {
                { "plc", plc.DisplayName },
                { "unit_name", unitName ?? "" },
                { "group_path", BlockOperations.NormalizeGroupPath(groupPath) },
                { "kind", isType ? "type" : "block" },
                { "name", inferredName },
                { "state", resultState },
                { "imported_names", importedObjects.Select(o => session.Api.GetString(o, "Name")).ToArray() },
                { "messages", messages.ToArray() },
                { "backup_id", backupId },
                { "compile_mode", NormalizeCompileMode(compileMode) },
                { "compile_errors", compile == null ? (object)null : compile.ErrorCount },
                { "compile_warnings", compile == null ? (object)null : compile.WarningCount },
                { "compile_report", compile == null ? "" : compile.ToReport() },
                { "saved", false }
            };
        }

        public static Dictionary<string, object> ImportDocumentsBatch(
            OpennessSession session,
            string backupRoot,
            string plcName,
            object[] items,
            string compileMode)
        {
            if (items == null || items.Length == 0)
                throw new ArgumentException("items_json 至少需要包含一个待导入文档。");
            if (items.Length > 100)
                throw new ArgumentException("单批最多导入 100 个 SimaticSD 文档。");

            PlcContext plc = session.SelectPlc(plcName);
            List<Dictionary<string, object>> prepared = new List<Dictionary<string, object>>();
            List<string> backupIds = new List<string>();

            foreach (object raw in items)
            {
                Dictionary<string, object> map = raw as Dictionary<string, object>;
                if (map == null) throw new ArgumentException("items_json 中的每项必须是 JSON 对象。");
                string s7dcl = Text(map, "s7dcl");
                if (string.IsNullOrWhiteSpace(s7dcl)) throw new ArgumentException("批处理项缺少 s7dcl 内容。");
                bool isType = string.Equals(Text(map, "kind"), "type", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Text(map, "kind"), "udt", StringComparison.OrdinalIgnoreCase)
                    || IsTypeDocument(s7dcl);
                string name = Text(map, "block_name");
                if (name.Length == 0) name = Text(map, "object_name");
                if (name.Length == 0) name = InferDocumentObjectName(s7dcl);
                string group = Text(map, "group_path");
                string unit = Text(map, "unit_name");

                if (name.Length > 0)
                {
                    string bid = BlockOperations.TryBackupExisting(session, backupRoot, plc, name, group, unit, isType);
                    if (!string.IsNullOrEmpty(bid)) backupIds.Add(bid);
                }

                Dictionary<string, object> copy = new Dictionary<string, object>(map, StringComparer.OrdinalIgnoreCase);
                copy["_resolved_name"] = name;
                copy["_is_type"] = isType;
                prepared.Add(copy);
            }

            // Order UDTs before blocks inside the single V21 transaction
            var ordered = prepared.OrderBy(m => Convert.ToBoolean(m["_is_type"]) ? 0 : 1).ToArray();
            List<object> importedResults = new List<object>();
            List<object> allImportedEngineeringObjects = new List<object>();

            session.RunInTransaction("Batch Import SimaticSD (" + ordered.Length + " items)", true, delegate
            {
                foreach (Dictionary<string, object> item in ordered)
                {
                    string name = Convert.ToString(item["_resolved_name"]);
                    bool isType = Convert.ToBoolean(item["_is_type"]);
                    string s7dcl = Text(item, "s7dcl");
                    string s7res = Text(item, "s7res");
                    object resObj;
                    Dictionary<string, object> extraRes = item.TryGetValue("resources", out resObj) ? resObj as Dictionary<string, object> : null;
                    string group = Text(item, "group_path");
                    string unit = Text(item, "unit_name");
                    string opt = Text(item, "import_options");

                    string state;
                    List<string> msgs = new List<string>();
                    List<object> objs = new List<object>();
                    ImportSingleCore(session, plc, name, s7dcl, s7res, extraRes, group, unit, isType, opt, out state, msgs, objs);
                    allImportedEngineeringObjects.AddRange(objs);
                    importedResults.Add(new Dictionary<string, object>
                    {
                        { "name", name },
                        { "kind", isType ? "type" : "block" },
                        { "group_path", BlockOperations.NormalizeGroupPath(group) },
                        { "unit_name", unit },
                        { "state", state },
                        { "messages", msgs.ToArray() }
                    });
                }
            });

            plc.InvalidateTypeCache();
            plc.InvalidateBlockCache();

            CompileSummary compile = RunRequestedCompile(session, plc, allImportedEngineeringObjects, compileMode);

            return new Dictionary<string, object>
            {
                { "plc", plc.DisplayName },
                { "imported_count", importedResults.Count },
                { "backup_ids", backupIds.ToArray() },
                { "items", importedResults.ToArray() },
                { "compile_mode", NormalizeCompileMode(compileMode) },
                { "compile_errors", compile == null ? (object)null : compile.ErrorCount },
                { "compile_warnings", compile == null ? (object)null : compile.WarningCount },
                { "compile_report", compile == null ? "" : compile.ToReport() },
                { "saved", false }
            };
        }

        private static Dictionary<string, object> ExportItemAsDocument(
            ReflectionApi api,
            PlcContext plc,
            CachedBlockEntry entry,
            bool isType,
            bool includeResources)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "TiaV21_SimaticSD_Out_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string fileBase = SafeFileBase(entry.Name);
                object exportResult;
                using (OperationMetrics.Measure("simaticsd_export"))
                {
                    exportResult = api.Invoke(entry.Item, "ExportAsDocuments", new DirectoryInfo(tempDir), fileBase);
                }

                string state = api.GetString(exportResult, "State");
                List<string> messages = CollectDocMessages(api, api.Get(exportResult, "Messages", false));

                string[] allFiles = Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories);
                string dclFile = allFiles.FirstOrDefault(f => f.EndsWith(".s7dcl", StringComparison.OrdinalIgnoreCase));
                if (dclFile == null)
                {
                    throw new InvalidOperationException("ExportAsDocuments 未生成 .s7dcl 文件（State=" + state + "）: "
                        + string.Join("; ", messages));
                }

                string s7dcl = File.ReadAllText(dclFile, Encoding.UTF8);
                string primaryRes = "";
                Dictionary<string, string> resourceMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                if (includeResources)
                {
                    foreach (string resPath in allFiles.Where(f => f.EndsWith(".s7res", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                    {
                        string content = File.ReadAllText(resPath, Encoding.UTF8);
                        string fileName = Path.GetFileName(resPath);
                        resourceMap[fileName] = content;
                        if (primaryRes.Length == 0) primaryRes = content;
                    }
                }

                return new Dictionary<string, object>
                {
                    { "plc", plc.DisplayName },
                    { "unit_name", entry.UnitName },
                    { "group_path", entry.GroupPath },
                    { "kind", isType ? "type" : "block" },
                    { "name", entry.Name },
                    { "namespace", entry.Namespace },
                    { "number", entry.Number },
                    { "language", entry.Language },
                    { "category", entry.Category },
                    { "is_consistent", entry.IsConsistent },
                    { "modified_date", entry.ModifiedDate },
                    { "state", state },
                    { "messages", messages.ToArray() },
                    { "file_base", Path.GetFileNameWithoutExtension(dclFile) },
                    { "s7dcl", s7dcl },
                    { "s7res", primaryRes },
                    { "resources", resourceMap }
                };
            }
            finally
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); }
                catch { }
            }
        }

        private static void ImportSingleCore(
            OpennessSession session,
            PlcContext plc,
            string objectName,
            string s7dcl,
            string s7res,
            Dictionary<string, object> extraResources,
            string groupPath,
            string unitName,
            bool isType,
            string importOptionText,
            out string state,
            List<string> messages,
            List<object> importedObjects)
        {
            ReflectionApi api = session.Api;
            object container = SoftwareUnitHelper.ResolveContainer(api, plc, unitName);
            object rootGroup = api.Get(container, isType ? "TypeGroup" : "BlockGroup", true);
            object targetGroup = BlockOperations.EnsureGroup(api, rootGroup, groupPath);
            object composition = api.Get(targetGroup, isType ? "Types" : "Blocks", true);

            string tempDir = Path.Combine(Path.GetTempPath(), "TiaV21_SimaticSD_In_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string fileBase = SafeFileBase(string.IsNullOrWhiteSpace(objectName) ? "SimaticSdObject" : objectName);
                UTF8Encoding utf8NoBom = new UTF8Encoding(false);
                File.WriteAllText(Path.Combine(tempDir, fileBase + ".s7dcl"), s7dcl, utf8NoBom);

                if (!string.IsNullOrWhiteSpace(s7res))
                {
                    File.WriteAllText(Path.Combine(tempDir, fileBase + ".s7res"), s7res, utf8NoBom);
                }

                if (extraResources != null)
                {
                    foreach (KeyValuePair<string, object> pair in extraResources)
                    {
                        string resFileName = Path.GetFileName(pair.Key ?? "");
                        if (string.IsNullOrWhiteSpace(resFileName) || !resFileName.EndsWith(".s7res", StringComparison.OrdinalIgnoreCase))
                            continue;
                        // Rewrite prefix if caller passed original exported resource name with a different base prefix
                        if (!resFileName.StartsWith(fileBase, StringComparison.OrdinalIgnoreCase))
                        {
                            int dot = resFileName.IndexOf('.');
                            resFileName = dot > 0 ? fileBase + resFileName.Substring(dot) : fileBase + ".s7res";
                        }
                        File.WriteAllText(Path.Combine(tempDir, resFileName), Convert.ToString(pair.Value ?? ""), utf8NoBom);
                    }
                }

                string[] flagTokens = string.IsNullOrWhiteSpace(importOptionText)
                    ? new[] { "Override" }
                    : importOptionText.Split(new[] { ',', '|', ';' }, StringSplitOptions.RemoveEmptyEntries);
                object options = api.CombineEnumFlags("Siemens.Engineering.SW.ImportDocumentOptions", flagTokens);

                object importResult;
                using (OperationMetrics.Measure("simaticsd_import"))
                {
                    importResult = api.Invoke(composition, "ImportFromDocuments", new DirectoryInfo(tempDir), fileBase, options);
                }

                state = api.GetString(importResult, "State");
                messages.AddRange(CollectDocMessages(api, api.Get(importResult, "Messages", false)));

                object blocksComp = api.Get(importResult, "ImportedPlcBlocks", false);
                foreach (object b in api.Items(blocksComp)) importedObjects.Add(b);

                object typesComp = api.Get(importResult, "ImportedPlcTypes", false);
                foreach (object t in api.Items(typesComp)) importedObjects.Add(t);

                if (string.Equals(state, "Failure", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("ImportFromDocuments 失败（已回滚事务）：\n" + string.Join(Environment.NewLine, messages));
                }
            }
            finally
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); }
                catch { }
            }
        }

        private static CompileSummary RunRequestedCompile(OpennessSession session, PlcContext plc, List<object> importedObjects, string compileMode)
        {
            string mode = NormalizeCompileMode(compileMode);
            if (mode == "none") return null;
            if (mode == "block" && importedObjects != null && importedObjects.Count == 1)
            {
                return session.CompileTarget(importedObjects[0], plc);
            }
            return session.CompilePlc(plc);
        }

        private static string NormalizeCompileMode(string compileMode)
        {
            if (string.IsNullOrWhiteSpace(compileMode)) return "plc";
            string m = compileMode.Trim().ToLowerInvariant();
            if (m == "none" || m == "block" || m == "plc") return m;
            throw new ArgumentException("compile_mode 仅支持 plc、block 或 none。");
        }

        private static List<string> CollectDocMessages(ReflectionApi api, object composition)
        {
            List<string> list = new List<string>();
            foreach (object item in api.Items(composition))
            {
                string msg = api.GetString(item, "Message");
                if (!string.IsNullOrWhiteSpace(msg)) list.Add(msg);
            }
            return list;
        }

        private static bool IsTypeDocument(string s7dcl)
        {
            return Regex.IsMatch(s7dcl ?? "", @"^\s*(?:\{[\s\S]*?\}\s*)*TYPE\b", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        }

        internal static string InferDocumentObjectName(string s7dcl)
        {
            if (string.IsNullOrWhiteSpace(s7dcl)) return "";
            Match m = Regex.Match(s7dcl,
                @"\b(?:FUNCTION_BLOCK|FUNCTION|DATA_BLOCK|ORGANIZATION_BLOCK|TYPE)\s+(?:""([^""]+)""|([A-Za-z_][A-Za-z0-9_.]*))",
                RegexOptions.IgnoreCase);
            if (!m.Success) return "";
            string raw = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim();
            int lastDot = raw.LastIndexOf('.');
            return lastDot >= 0 && !m.Groups[1].Success ? raw.Substring(lastDot + 1) : raw;
        }

        private static string SafeFileBase(string name)
        {
            string cleaned = PathTools.SafeName(name);
            if (string.IsNullOrWhiteSpace(cleaned) || !SafeDocBaseRegex.IsMatch(cleaned))
            {
                cleaned = Regex.Replace(cleaned ?? "Block", "[^A-Za-z0-9_.-]", "_");
                if (cleaned.Length == 0) cleaned = "Block";
                if (cleaned.Length > 100) cleaned = cleaned.Substring(0, 100);
            }
            return cleaned;
        }

        private static string Text(Dictionary<string, object> map, string key)
        {
            object v;
            return map != null && map.TryGetValue(key, out v) && v != null ? Convert.ToString(v).Trim() : "";
        }
    }
}
