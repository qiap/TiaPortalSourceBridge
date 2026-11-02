using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using TiaPortalSourceBridge.Configuration;

namespace TiaPortalSourceBridge.Openness
{
    internal sealed class HmiImporter
    {
        private readonly AppConfig _config;
        private readonly ReflectionApi _api;

        public HmiImporter(AppConfig config, ReflectionApi api)
        {
            _config = config;
            _api = api;
        }

        public ImportResult Import(HmiContext hmi)
        {
            string root = HmiExporter.GetTargetDirectory(_config.SourceDirectory, hmi);
            string manifestPath = Path.Combine(root, "manifest.json");
            ImportResult result = new ImportResult();
            if (!File.Exists(manifestPath)) return result;

            ExportManifest manifest = new JavaScriptSerializer().Deserialize<ExportManifest>(
                File.ReadAllText(manifestPath, Encoding.UTF8));
            if (manifest == null || manifest.Objects == null)
                throw new InvalidOperationException("HMI manifest.json 无效: " + manifestPath);

            List<ImportFailure> pending = new List<ImportFailure>();
            foreach (ManifestEntry entry in manifest.Objects.OrderBy(ImportOrder).ThenBy(e => e.GroupPath).ThenBy(e => e.Name))
            {
                string error;
                if (TryImport(hmi, root, entry, out error))
                {
                    result.SuccessCount++;
                    Console.WriteLine("[HMI 导入] " + entry.RelativePath);
                }
                else
                {
                    pending.Add(new ImportFailure { Entry = entry, FirstError = error, RetryError = "" });
                    Console.Error.WriteLine("[HMI 待重试] " + entry.RelativePath + ": " + FirstLine(error));
                    if (!_config.ContinueOnError) break;
                }
            }

            if (pending.Count > 0 && _config.ContinueOnError)
            {
                List<ImportFailure> failed = new List<ImportFailure>();
                foreach (ImportFailure failure in pending)
                {
                    string retryError;
                    if (TryImport(hmi, root, failure.Entry, out retryError))
                    {
                        result.SuccessCount++;
                        Console.WriteLine("[HMI 重试成功] " + failure.Entry.RelativePath);
                    }
                    else
                    {
                        failure.RetryError = retryError;
                        failed.Add(failure);
                        Console.Error.WriteLine("[HMI 失败] " + failure.Entry.RelativePath + ": " + FirstLine(retryError));
                    }
                }
                pending = failed;
            }

            result.Failures.AddRange(pending);
            result.FailureCount = pending.Count;
            return result;
        }

        private bool TryImport(HmiContext hmi, string root, ManifestEntry entry, out string error)
        {
            try
            {
                string file = Path.GetFullPath(Path.Combine(root, entry.RelativePath));
                string normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!file.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("HMI manifest 路径越过源码目录。 ");
                if (!File.Exists(file)) throw new FileNotFoundException("找不到 HMI 导入文件。", file);

                object options = _api.EnumValue("Siemens.Engineering.ImportOptions", "Override");
                if (entry.Kind == "HmiScreenGlobalElements")
                {
                    _api.Invoke(hmi.Target, "ImportScreenGlobalElements", new FileInfo(file), options);
                }
                else if (entry.Kind == "HmiScreenOverview")
                {
                    _api.Invoke(hmi.Target, "ImportScreenOverview", new FileInfo(file), options);
                }
                else
                {
                    object composition = ResolveComposition(hmi.Target, entry);
                    _api.Invoke(composition, "Import", new FileInfo(file), options);
                }
                error = "";
                return true;
            }
            catch (Exception ex)
            {
                error = ex.ToString();
                return false;
            }
        }

        private object ResolveComposition(object target, ManifestEntry entry)
        {
            if (entry.Kind == "HmiScreen") return FolderComposition(target, "ScreenFolder", "Screens", entry.GroupPath);
            if (entry.Kind == "HmiScreenTemplate") return FolderComposition(target, "ScreenTemplateFolder", "ScreenTemplates", entry.GroupPath);
            if (entry.Kind == "HmiScreenPopup") return FolderComposition(target, "ScreenPopupFolder", "ScreenPopups", entry.GroupPath);
            if (entry.Kind == "HmiVBScript") return FolderComposition(target, "VBScriptFolder", "VBScripts", entry.GroupPath);
            if (entry.Kind == "HmiTagTable") return FolderComposition(target, "TagFolder", "TagTables", entry.GroupPath);
            if (entry.Kind == "HmiTag")
            {
                IList<string> parts = PathTools.SplitGroupPath(entry.GroupPath);
                if (parts.Count == 0) throw new InvalidOperationException("HMI Tag 缺少变量表路径: " + entry.Name);
                string tableName = parts[parts.Count - 1];
                string folderPath = string.Join("/", parts.Take(parts.Count - 1));
                object tables = FolderComposition(target, "TagFolder", "TagTables", folderPath);
                object table = _api.Invoke(tables, "Find", tableName);
                if (table == null) throw new InvalidOperationException("找不到 HMI 变量表: " + entry.GroupPath);
                return _api.Get(table, "Tags", true);
            }
            if (entry.Kind == "HmiScreenSlidein") return _api.Get(_api.Get(target, "ScreenSlideinFolder", true), "ScreenSlideins", true);
            if (entry.Kind == "HmiTextList") return _api.Get(target, "TextLists", true);
            if (entry.Kind == "HmiGraphicList") return _api.Get(target, "GraphicLists", true);
            if (entry.Kind == "HmiCycle") return _api.Get(target, "Cycles", true);
            if (entry.Kind == "HmiConnection") return _api.Get(target, "Connections", true);
            throw new InvalidOperationException("不支持的 HMI 对象类型: " + entry.Kind);
        }

        private object FolderComposition(object target, string rootProperty, string itemProperty, string groupPath)
        {
            object folder = _api.Get(target, rootProperty, true);
            foreach (string name in PathTools.SplitGroupPath(groupPath))
            {
                object folders = _api.Get(folder, "Folders", true);
                object child = _api.Items(folders).FirstOrDefault(x =>
                    string.Equals(_api.GetString(x, "Name"), name, StringComparison.OrdinalIgnoreCase));
                if (child == null) child = _api.Invoke(folders, "Create", name);
                folder = child;
            }
            return _api.Get(folder, itemProperty, true);
        }

        private static int ImportOrder(ManifestEntry entry)
        {
            if (entry.Kind == "HmiConnection" || entry.Kind == "HmiCycle") return 0;
            if (entry.Kind == "HmiTagTable" || entry.Kind == "HmiTag") return 1;
            if (entry.Kind == "HmiVBScript" || entry.Kind == "HmiTextList" || entry.Kind == "HmiGraphicList") return 2;
            if (entry.Kind == "HmiScreenTemplate") return 3;
            return 4;
        }

        private static string FirstLine(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "未知错误";
            int end = value.IndexOfAny(new[] { '\r', '\n' });
            return end < 0 ? value : value.Substring(0, end);
        }
    }
}
