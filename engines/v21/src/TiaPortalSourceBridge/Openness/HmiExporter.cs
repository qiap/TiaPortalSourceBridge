using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using TiaPortalSourceBridge.Configuration;

namespace TiaPortalSourceBridge.Openness
{
    internal sealed class HmiExporter
    {
        private readonly AppConfig _config;
        private readonly ReflectionApi _api;

        public HmiExporter(AppConfig config, ReflectionApi api)
        {
            _config = config;
            _api = api;
        }

        public ExportResult Export(HmiContext hmi)
        {
            string root = GetTargetDirectory(_config.SourceDirectory, hmi);
            Directory.CreateDirectory(root);
            ExportManifest manifest = new ExportManifest
            {
                TiaPortalVersion = _config.TiaPortalVersion,
                ProjectPath = _config.ProjectPath,
                Plc = hmi.DisplayName
            };
            ExportResult result = new ExportResult();

            ExportFolder(_api.Get(hmi.Target, "ScreenFolder", false), "Screens", "Folders",
                "HmiScreen", "screens", root, manifest, result);
            ExportFolder(_api.Get(hmi.Target, "ScreenTemplateFolder", false), "ScreenTemplates", "Folders",
                "HmiScreenTemplate", "screen-templates", root, manifest, result);
            ExportFolder(_api.Get(hmi.Target, "ScreenPopupFolder", false), "ScreenPopups", "Folders",
                "HmiScreenPopup", "popup-screens", root, manifest, result);
            ExportFolder(_api.Get(hmi.Target, "VBScriptFolder", false), "VBScripts", "Folders",
                "HmiVBScript", "scripts", root, manifest, result);
            ExportFolder(_api.Get(hmi.Target, "TagFolder", false), "TagTables", "Folders",
                "HmiTagTable", "tag-tables", root, manifest, result);

            object slideinFolder = _api.Get(hmi.Target, "ScreenSlideinFolder", false);
            ExportComposition(_api.Get(slideinFolder, "ScreenSlideins", false), "HmiScreenSlidein",
                "slidein-screens", root, manifest, result);
            ExportComposition(_api.Get(hmi.Target, "TextLists", false), "HmiTextList",
                "text-lists", root, manifest, result);
            ExportComposition(_api.Get(hmi.Target, "GraphicLists", false), "HmiGraphicList",
                "graphic-lists", root, manifest, result);
            ExportComposition(_api.Get(hmi.Target, "Cycles", false), "HmiCycle",
                "cycles", root, manifest, result);
            ExportComposition(_api.Get(hmi.Target, "Connections", false), "HmiConnection",
                "connections", root, manifest, result);

            ExportNamed(_api.Get(hmi.Target, "ScreenGlobalElements", false), "HmiScreenGlobalElements",
                "ScreenGlobalElements", "system", "", root, manifest, result);
            ExportNamed(_api.Get(hmi.Target, "ScreenOverview", false), "HmiScreenOverview",
                "ScreenOverview", "system", "", root, manifest, result);

            string manifestPath = Path.Combine(root, "manifest.json");
            File.WriteAllText(manifestPath, new JavaScriptSerializer().Serialize(manifest), new UTF8Encoding(true));
            return result;
        }

        internal static string GetTargetDirectory(string sourceDirectory, HmiContext hmi)
        {
            return Path.Combine(sourceDirectory, "hmi", PathTools.SafeName(hmi.DisplayName));
        }

        private void ExportFolder(object folder, string itemProperty, string folderProperty, string kind,
            string category, string root, ExportManifest manifest, ExportResult result)
        {
            ExportFolder(folder, itemProperty, folderProperty, kind, category, "", root, manifest, result);
        }

        private void ExportFolder(object folder, string itemProperty, string folderProperty, string kind,
            string category, string groupPath, string root, ExportManifest manifest, ExportResult result)
        {
            if (folder == null) return;
            object composition = _api.Get(folder, itemProperty, false);
            // Snapshot the Openness composition before exporting. Reuse the actual objects
            // instead of issuing a separate IPC Find(name) for every item.
            var items = _api.Items(composition)
                .Select(item => new { Item = item, Name = _api.GetString(item, "Name") })
                .Where(item => !string.IsNullOrWhiteSpace(item.Name))
                .ToList();
            foreach (var item in items)
                ExportNamed(item.Item, kind, item.Name, category, groupPath, root, manifest, result);

            object folderComposition = _api.Get(folder, folderProperty, false);
            var folders = _api.Items(folderComposition)
                .Select(child => new { Folder = child, Name = _api.GetString(child, "Name") })
                .Where(child => !string.IsNullOrWhiteSpace(child.Name))
                .ToList();
            foreach (var child in folders)
            {
                string childPath = string.IsNullOrWhiteSpace(groupPath) ? child.Name : groupPath + "/" + child.Name;
                ExportFolder(child.Folder, itemProperty, folderProperty, kind, category, childPath, root, manifest, result);
            }
        }

        private void ExportComposition(object composition, string kind, string category, string root,
            ExportManifest manifest, ExportResult result)
        {
            foreach (object item in _api.Items(composition))
            {
                ExportItem(item, kind, category, "", root, manifest, result);
            }
        }

        private void ExportItem(object item, string kind, string category, string groupPath, string root,
            ExportManifest manifest, ExportResult result)
        {
            string name = _api.GetString(item, "Name");
            if (string.IsNullOrWhiteSpace(name)) name = _api.GetString(item, "SlideinType");
            if (string.IsNullOrWhiteSpace(name)) name = kind;
            ExportNamed(item, kind, name, category, groupPath, root, manifest, result);
        }

        private void ExportNamed(object item, string kind, string name, string category, string groupPath,
            string root, ExportManifest manifest, ExportResult result)
        {
            if (item == null) return;
            try
            {
                string directory = PathTools.GroupDirectory(Path.Combine(root, category), groupPath);
                Directory.CreateDirectory(directory);
                string file = Path.Combine(directory, PathTools.SafeName(name) + ".xml");
                if (File.Exists(file)) File.Delete(file);
                object options = _api.EnumValue("Siemens.Engineering.ExportOptions", "WithDefaults");
                _api.Invoke(item, "Export", new FileInfo(file), options);
                manifest.Objects.Add(new ManifestEntry
                {
                    Kind = kind,
                    Name = name,
                    GroupPath = groupPath,
                    Language = "HMI",
                    Format = "XML",
                    RelativePath = PathTools.RelativeTo(root, file),
                    Note = ""
                });
                result.SuccessCount++;
                Console.WriteLine("[HMI XML] " + PathTools.RelativeTo(_config.SourceDirectory, file));
            }
            catch (Exception ex)
            {
                if (kind == "HmiTagTable" && ExportTagsFromTable(item, name, category, groupPath, root, manifest, result))
                {
                    Console.WriteLine("[HMI 回退] 变量表 " + name + " 整表导出失败，已逐个导出表内变量。 ");
                    return;
                }
                result.FailureCount++;
                Console.Error.WriteLine("[HMI 失败] " + hmiName(kind, name) + ": " + ex.Message);
                if (!_config.ContinueOnError) throw;
            }
        }

        private bool ExportTagsFromTable(object table, string tableName, string category, string groupPath,
            string root, ExportManifest manifest, ExportResult result)
        {
            try
            {
                object tags = _api.Get(table, "Tags", true);
                var entries = _api.Items(tags)
                    .Select(tag => new { Tag = tag, Name = _api.GetString(tag, "Name") })
                    .Where(tag => !string.IsNullOrWhiteSpace(tag.Name)).ToList();
                if (entries.Count == 0) return false;
                string tagGroupPath = string.IsNullOrWhiteSpace(groupPath) ? tableName : groupPath + "/" + tableName;
                foreach (var tag in entries)
                    ExportNamed(tag.Tag, "HmiTag", tag.Name, category, tagGroupPath, root, manifest, result);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string hmiName(string kind, string name)
        {
            return kind + " / " + name;
        }
    }
}
