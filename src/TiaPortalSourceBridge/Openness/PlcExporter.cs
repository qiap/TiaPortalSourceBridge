using System;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using TiaPortalSourceBridge.Configuration;

namespace TiaPortalSourceBridge.Openness
{
    internal sealed class PlcExporter
    {
        private readonly AppConfig _config;
        private readonly ReflectionApi _api;
        private readonly ExportManifest _manifest;
        private readonly ExportResult _result;
        private object _plcSoftware;

        public PlcExporter(AppConfig config, ReflectionApi api)
        {
            _config = config;
            _api = api;
            _manifest = new ExportManifest();
            _result = new ExportResult();
        }

        public ExportResult Export(PlcContext plc)
        {
            _plcSoftware = plc.Software;
            Directory.CreateDirectory(_config.SourceDirectory);
            string blocksRoot = Path.Combine(_config.SourceDirectory, "blocks");
            string typesRoot = Path.Combine(_config.SourceDirectory, "types");
            Directory.CreateDirectory(blocksRoot);
            Directory.CreateDirectory(typesRoot);

            _manifest.TiaPortalVersion = _config.TiaPortalVersion;
            _manifest.ProjectPath = _config.ProjectPath;
            _manifest.Plc = plc.DisplayName;

            object blockGroup = _api.Get(_plcSoftware, "BlockGroup", true);
            ExportBlockGroup(blockGroup, "", blocksRoot);

            if (_config.IncludeSystemBlocks)
            {
                object systemGroups = _api.Get(blockGroup, "SystemBlockGroups", false);
                foreach (object group in _api.Items(systemGroups))
                {
                    string name = _api.GetString(group, "Name");
                    ExportBlockGroup(group, "_system/" + name, blocksRoot);
                }
            }

            object typeGroup = _api.Get(_plcSoftware, "TypeGroup", true);
            ExportTypeGroup(typeGroup, "", typesRoot);
            WriteManifest();
            return _result;
        }

        public ExportResult ExportReviewTags(PlcContext plc)
        {
            string root = Path.Combine(_config.SourceDirectory, "plc-tags");
            Directory.CreateDirectory(root);
            _manifest.TiaPortalVersion = _config.TiaPortalVersion;
            _manifest.ProjectPath = _config.ProjectPath;
            _manifest.Plc = plc.DisplayName;
            ExportTagGroup(_api.Get(plc.Software, "TagTableGroup", true), "", root);
            File.WriteAllText(Path.Combine(root, "manifest.json"),
                new JavaScriptSerializer().Serialize(_manifest), new UTF8Encoding(true));
            return _result;
        }

        private void ExportTagGroup(object group, string groupPath, string root)
        {
            foreach (object table in _api.Items(_api.Get(group, "TagTables", true)))
                ExportXml(table, "TagTable", _api.GetString(table, "Name"), groupPath, "", root, "Review context; not part of the block import manifest.");
            foreach (object child in _api.Items(_api.Get(group, "Groups", false)))
                ExportTagGroup(child, JoinGroup(groupPath, _api.GetString(child, "Name")), root);
        }

        private void ExportBlockGroup(object group, string groupPath, string root)
        {
            object blocks = _api.Get(group, "Blocks", false);
            foreach (object block in _api.Items(blocks))
            {
                ExportBlock(block, groupPath, root);
            }

            object groups = _api.Get(group, "Groups", false);
            foreach (object child in _api.Items(groups))
            {
                string name = _api.GetString(child, "Name");
                ExportBlockGroup(child, JoinGroup(groupPath, name), root);
            }
        }

        private void ExportTypeGroup(object group, string groupPath, string root)
        {
            object types = _api.Get(group, "Types", false);
            foreach (object plcType in _api.Items(types))
            {
                ExportType(plcType, groupPath, root);
            }

            object groups = _api.Get(group, "Groups", false);
            foreach (object child in _api.Items(groups))
            {
                string name = _api.GetString(child, "Name");
                ExportTypeGroup(child, JoinGroup(groupPath, name), root);
            }
        }

        private void ExportBlock(object block, string groupPath, string root)
        {
            string name = _api.GetString(block, "Name");
            string language = _api.GetString(block, "ProgrammingLanguage");
            string typeName = block.GetType().Name;
            string sourceExtension = null;

            if (string.Equals(language, "SCL", StringComparison.OrdinalIgnoreCase)) sourceExtension = ".scl";
            else if (string.Equals(language, "DB", StringComparison.OrdinalIgnoreCase)
                || typeName.IndexOf("DataBlock", StringComparison.OrdinalIgnoreCase) >= 0
                || typeName.EndsWith("DB", StringComparison.OrdinalIgnoreCase)) sourceExtension = ".db";
            else if (string.Equals(language, "STL", StringComparison.OrdinalIgnoreCase)) sourceExtension = ".awl";

            if (sourceExtension != null)
            {
                try
                {
                    string file = BuildFile(root, groupPath, name, sourceExtension);
                    GenerateReadableSource(block, file);
                    AddEntry("Block", name, groupPath, language, sourceExtension.TrimStart('.').ToUpperInvariant(), file, "");
                    Console.WriteLine("[源码] " + PathTools.RelativeTo(_config.SourceDirectory, file));
                    _result.SuccessCount++;
                    return;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[回退 XML] " + name + ": " + ex.Message);
                    ExportXml(block, "Block", name, groupPath, language, root, "源码生成失败，已回退 XML: " + ex.Message);
                    return;
                }
            }

            ExportXml(block, "Block", name, groupPath, language, root, "");
        }

        private void ExportType(object plcType, string groupPath, string root)
        {
            string name = _api.GetString(plcType, "Name");
            try
            {
                string file = BuildFile(root, groupPath, name, ".udt");
                GenerateReadableSource(plcType, file);
                AddEntry("Type", name, groupPath, "UDT", "UDT", file, "");
                Console.WriteLine("[源码] " + PathTools.RelativeTo(_config.SourceDirectory, file));
                _result.SuccessCount++;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[回退 XML] " + name + ": " + ex.Message);
                ExportXml(plcType, "Type", name, groupPath, "UDT", root, "源码生成失败，已回退 XML: " + ex.Message);
            }
        }

        private void GenerateReadableSource(object item, string file)
        {
            PrepareTarget(file);
            object externalGroup = _api.Get(_plcSoftware, "ExternalSourceGroup", true);
            Array items = _api.OneItemInterfaceArray("Siemens.Engineering.SW.ExternalSources.IGenerateSource", item);
            object options = _api.EnumValueBySimpleName("None", "GenerateOptions");
            _api.Invoke(externalGroup, "GenerateSource", items, new FileInfo(file), options);
        }

        private void ExportXml(object item, string kind, string name, string groupPath, string language, string root, string note)
        {
            try
            {
                string file = BuildFile(root, groupPath, name, ".xml");
                PrepareTarget(file);
                object options = _api.EnumValue("Siemens.Engineering.ExportOptions", "WithDefaults");
                _api.Invoke(item, "Export", new FileInfo(file), options);
                AddEntry(kind, name, groupPath, language, "XML", file, note);
                Console.WriteLine("[XML]  " + PathTools.RelativeTo(_config.SourceDirectory, file));
                _result.SuccessCount++;
            }
            catch (Exception ex)
            {
                _result.FailureCount++;
                Console.Error.WriteLine("[失败] " + name + ": " + ex.Message);
                if (!_config.ContinueOnError) throw;
            }
        }

        private string BuildFile(string root, string groupPath, string name, string extension)
        {
            string directory = PathTools.GroupDirectory(root, groupPath);
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, PathTools.SafeName(name) + extension);
        }

        private static void PrepareTarget(string file)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            if (File.Exists(file)) File.Delete(file);
        }

        private void AddEntry(string kind, string name, string groupPath, string language, string format, string file, string note)
        {
            _manifest.Objects.Add(new ManifestEntry
            {
                Kind = kind,
                Name = name,
                GroupPath = groupPath,
                Language = language,
                Format = format,
                RelativePath = PathTools.RelativeTo(_config.SourceDirectory, file),
                Note = note
            });
        }

        private void WriteManifest()
        {
            string path = Path.Combine(_config.SourceDirectory, "manifest.json");
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            File.WriteAllText(path, serializer.Serialize(_manifest), new UTF8Encoding(true));
        }

        private static string JoinGroup(string parent, string child)
        {
            return string.IsNullOrWhiteSpace(parent) ? child : parent + "/" + child;
        }
    }
}
