using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using TiaPortalSourceBridge.Configuration;

namespace TiaPortalSourceBridge.Openness
{
    internal sealed class PlcImporter
    {
        private readonly AppConfig _config;
        private readonly OpennessSession _session;
        private readonly ReflectionApi _api;
        private PlcContext _plc;
        private object _plcSoftware;

        public PlcImporter(AppConfig config, OpennessSession session)
        {
            _config = config;
            _session = session;
            _api = session.Api;
        }

        public ImportResult Import(PlcContext plc)
        {
            _plc = plc;
            _plcSoftware = plc.Software;
            ExportManifest manifest = ReadManifest();
            ImportResult result = new ImportResult();
            List<ImportFailure> pending = new List<ImportFailure>();

            IEnumerable<ManifestEntry> ordered = manifest.Objects
                .OrderBy(e => ImportOrder(e))
                .ThenBy(e => e.GroupPath)
                .ThenBy(e => e.Name);

            foreach (ManifestEntry entry in ordered)
            {
                string error;
                if (TryImport(entry, out error))
                {
                    result.SuccessCount++;
                    Console.WriteLine("[导入] " + entry.RelativePath);
                }
                else
                {
                    pending.Add(new ImportFailure
                    {
                        Entry = entry,
                        FirstError = error,
                        RetryError = ""
                    });
                    Console.Error.WriteLine("[待重试] " + entry.RelativePath + ": " + FirstLine(error));
                    if (!_config.ContinueOnError) break;
                }
            }

            if (pending.Count > 0 && _config.ContinueOnError)
            {
                Console.WriteLine();
                Console.WriteLine("首次导入有 " + pending.Count + " 个对象未成功；依赖对象已导入完毕，现在自动重试。");
                List<ImportFailure> stillFailing = new List<ImportFailure>();
                foreach (ImportFailure failure in pending)
                {
                    string retryError;
                    if (TryImport(failure.Entry, out retryError))
                    {
                        result.SuccessCount++;
                        Console.WriteLine("[重试成功] " + failure.Entry.RelativePath);
                    }
                    else
                    {
                        failure.RetryError = retryError;
                        stillFailing.Add(failure);
                        Console.Error.WriteLine("[失败] " + failure.Entry.RelativePath + ": " + FirstLine(retryError));
                    }
                }
                pending = stillFailing;
            }

            result.Failures.AddRange(pending);
            result.FailureCount = pending.Count;
            result.ErrorLogPath = UpdateFailureLog(result, manifest);

            if (result.FailureCount == 0 && _config.SaveProjectAfterImport)
            {
                _session.SaveProject();
                Console.WriteLine("TIA Portal 项目已保存。");
            }
            else if (result.FailureCount > 0 && _config.SaveProjectAfterImport)
            {
                Console.WriteLine("存在导入失败项，MVP 不自动保存部分成功的结果；请在 TIA Portal 中检查后手动保存或关闭项目放弃更改。");
            }

            return result;
        }

        private bool TryImport(ManifestEntry entry, out string error)
        {
            error = "";
            try
            {
                if (entry == null)
                {
                    throw new InvalidOperationException("manifest 中包含空对象。");
                }
                if (string.IsNullOrWhiteSpace(entry.RelativePath))
                {
                    throw new InvalidOperationException("manifest 对象缺少 RelativePath: " + entry.Name);
                }

                string fullPath = Path.GetFullPath(Path.Combine(_config.SourceDirectory, entry.RelativePath));
                if (!IsInside(_config.SourceDirectory, fullPath))
                {
                    throw new InvalidOperationException("manifest 中的路径越过了 sourceDirectory。");
                }
                if (!File.Exists(fullPath))
                {
                    throw new FileNotFoundException("找不到文件。", fullPath);
                }

                if (string.Equals(entry.Format, "XML", StringComparison.OrdinalIgnoreCase))
                {
                    ImportXml(entry, fullPath);
                }
                else
                {
                    ImportSource(entry, fullPath);
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.ToString();
                return false;
            }
        }

        private void ImportXml(ManifestEntry entry, string file)
        {
            object container = SoftwareUnitHelper.ResolveContainer(_api, _plc, entry.UnitName);
            bool isType = string.Equals(entry.Kind, "Type", StringComparison.OrdinalIgnoreCase);
            object root = _api.Get(container, isType ? "TypeGroup" : "BlockGroup", true);
            object targetGroup = EnsureGroup(root, entry.GroupPath);
            object composition = _api.Get(targetGroup, isType ? "Types" : "Blocks", true);
            object options = _api.EnumValue("Siemens.Engineering.ImportOptions", "Override");
            if (_api.TryRequireType("Siemens.Engineering.SW.SWImportOptions") != null)
            {
                object swOpts = _api.CombineEnumFlags("Siemens.Engineering.SW.SWImportOptions",
                    new[] { "IgnoreStructuralChanges", "IgnoreMissingReferencedObjects" });
                _api.Invoke(composition, "Import", new FileInfo(file), options, swOpts);
            }
            else
            {
                _api.Invoke(composition, "Import", new FileInfo(file), options);
            }
        }

        private void ImportSource(ManifestEntry entry, string file)
        {
            string temporaryFile = CopyWithUtf8Bom(file);
            object externalSource = null;
            try
            {
                object container = SoftwareUnitHelper.ResolveContainer(_api, _plc, entry.UnitName);
                object externalGroup = _api.Get(container, "ExternalSourceGroup", false)
                    ?? _api.Get(_plcSoftware, "ExternalSourceGroup", true);
                object sources = _api.Get(externalGroup, "ExternalSources", true);
                string temporaryName = "CodexImport_" + Guid.NewGuid().ToString("N");
                externalSource = _api.Invoke(sources, "CreateFromFile", temporaryName, temporaryFile);
                object option = _api.EnumValueBySimpleName("None", "GenerateBlockOption", "GenerateBlockOptions");

                object root = string.Equals(entry.Kind, "Type", StringComparison.OrdinalIgnoreCase)
                    ? _api.Get(container, "TypeGroup", true)
                    : _api.Get(container, "BlockGroup", true);
                object targetGroup = EnsureGroup(root, entry.GroupPath);

                if (string.IsNullOrWhiteSpace(entry.GroupPath))
                {
                    try { _api.Invoke(externalSource, "GenerateBlocksFromSource", option); }
                    catch (MissingMethodException) { _api.Invoke(externalSource, "GenerateBlocksFromSource"); }
                }
                else
                {
                    try { _api.Invoke(externalSource, "GenerateBlocksFromSource", targetGroup, option); }
                    catch (MissingMethodException)
                    {
                        Console.WriteLine("[提示] 当前 TIA 版本不支持按用户组生成源码，改为生成到根组: " + entry.Name);
                        _api.Invoke(externalSource, "GenerateBlocksFromSource", option);
                    }
                }
            }
            finally
            {
                if (externalSource != null)
                {
                    try { _api.Invoke(externalSource, "Delete"); }
                    catch { }
                }
                try { File.Delete(temporaryFile); }
                catch { }
            }
        }

        private object EnsureGroup(object root, string groupPath)
        {
            object current = root;
            foreach (string name in PathTools.SplitGroupPath(groupPath))
            {
                object groups = _api.Get(current, "Groups", true);
                object next = _api.Items(groups).FirstOrDefault(g =>
                    string.Equals(_api.GetString(g, "Name"), name, StringComparison.OrdinalIgnoreCase));
                if (next == null)
                {
                    next = _api.Invoke(groups, "Create", name);
                }
                current = next;
            }
            return current;
        }

        private ExportManifest ReadManifest()
        {
            string path = Path.Combine(_config.SourceDirectory, "manifest.json");
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("找不到 manifest.json。为避免把 XML 导入错误的对象组，MVP 只导入由 export 命令生成并保留 manifest 的目录。", path);
            }

            JavaScriptSerializer serializer = new JavaScriptSerializer();
            ExportManifest manifest = serializer.Deserialize<ExportManifest>(File.ReadAllText(path, Encoding.UTF8));
            if (manifest == null || manifest.Objects == null)
            {
                throw new InvalidOperationException("manifest.json 无效。");
            }
            return manifest;
        }

        private static string CopyWithUtf8Bom(string source)
        {
            string extension = Path.GetExtension(source);
            string destination = Path.Combine(Path.GetTempPath(), "TiaPortalSourceBridge_" + Guid.NewGuid().ToString("N") + extension);
            string text;
            using (StreamReader reader = new StreamReader(source, Encoding.UTF8, true))
            {
                text = reader.ReadToEnd();
            }
            File.WriteAllText(destination, text, new UTF8Encoding(true));
            return destination;
        }

        private string UpdateFailureLog(ImportResult result, ExportManifest manifest)
        {
            string directory = Path.GetDirectoryName(_config.ConfigFilePath);
            string path = Path.Combine(directory, "last-import-errors.log");
            if (result.FailureCount == 0)
            {
                try { if (File.Exists(path)) File.Delete(path); }
                catch { }
                return "";
            }

            StringBuilder text = new StringBuilder();
            text.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            text.AppendLine("Project: " + _config.ProjectPath);
            text.AppendLine("PLC source: " + _config.SourceDirectory);
            text.AppendLine("Manifest objects: " + (manifest.Objects == null ? 0 : manifest.Objects.Count));
            text.AppendLine("Succeeded: " + result.SuccessCount);
            text.AppendLine("Failed: " + result.FailureCount);
            text.AppendLine();

            for (int i = 0; i < result.Failures.Count; i++)
            {
                ImportFailure failure = result.Failures[i];
                ManifestEntry entry = failure.Entry;
                text.AppendLine("[" + (i + 1) + "] " + entry.RelativePath);
                text.AppendLine("Kind=" + entry.Kind + "; Name=" + entry.Name + "; Language=" + entry.Language + "; Format=" + entry.Format);
                text.AppendLine("First attempt:");
                text.AppendLine(failure.FirstError);
                if (!string.IsNullOrWhiteSpace(failure.RetryError))
                {
                    text.AppendLine("Retry:");
                    text.AppendLine(failure.RetryError);
                }
                text.AppendLine();
            }

            try
            {
                File.WriteAllText(path, text.ToString(), new UTF8Encoding(true));
                Console.Error.WriteLine("失败详情已写入: " + path);
                return path;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("无法写入导入失败日志: " + ex.Message);
                return "";
            }
        }

        private static int ImportOrder(ManifestEntry entry)
        {
            if (string.Equals(entry.Kind, "Type", StringComparison.OrdinalIgnoreCase)) return 0;
            if (IsDataBlock(entry)) return 3;
            if (!string.Equals(entry.Format, "XML", StringComparison.OrdinalIgnoreCase)) return 1;
            return 2;
        }

        private static bool IsDataBlock(ManifestEntry entry)
        {
            return string.Equals(entry.Kind, "Block", StringComparison.OrdinalIgnoreCase)
                && (string.Equals(entry.Format, "DB", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(entry.Language, "DB", StringComparison.OrdinalIgnoreCase));
        }

        private static string FirstLine(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "未知错误";
            int end = value.IndexOfAny(new[] { '\r', '\n' });
            return end < 0 ? value : value.Substring(0, end);
        }

        private static bool IsInside(string root, string file)
        {
            string normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return file.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
        }
    }
}
