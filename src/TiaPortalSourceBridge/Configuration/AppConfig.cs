using System;
using System.IO;
using System.Text.RegularExpressions;

namespace TiaPortalSourceBridge.Configuration
{
    public sealed class AppConfig
    {
        public string TiaPortalVersion { get; set; }
        public string ProjectPath { get; set; }
        public string PlcName { get; set; }
        public string SourceDirectory { get; set; }
        public string SiemensEngineeringDll { get; set; }
        public string ConnectionMode { get; set; }
        public bool WithUserInterface { get; set; }
        public bool SaveProjectAfterImport { get; set; }
        public bool CompileBeforeExport { get; set; }
        public bool IncludeSystemBlocks { get; set; }
        public bool ContinueOnError { get; set; }
        public bool AllowProjectUpgrade { get; set; }

        public string ConfigFilePath { get; set; }
        public string ProjectFileVersion { get; set; }

        public AppConfig()
        {
            TiaPortalVersion = "V20";
            PlcName = "PLC_1";
            SourceDirectory = ".\\plc-src";
            SiemensEngineeringDll = "";
            ConnectionMode = "AttachOrStart";
            WithUserInterface = true;
            SaveProjectAfterImport = false;
            CompileBeforeExport = false;
            IncludeSystemBlocks = false;
            ContinueOnError = true;
            AllowProjectUpgrade = false;
            ProjectFileVersion = "";
        }

        public void NormalizeAndValidate(string configFilePath)
        {
            ConfigFilePath = Path.GetFullPath(configFilePath);
            string baseDirectory = Path.GetDirectoryName(ConfigFilePath);

            if (string.IsNullOrWhiteSpace(TiaPortalVersion))
            {
                throw new InvalidOperationException("config.json 中必须设置 tiaPortalVersion，例如 V20。");
            }
            if (string.IsNullOrWhiteSpace(ProjectPath))
            {
                throw new InvalidOperationException("config.json 中必须设置 projectPath。");
            }

            ProjectPath = MakeAbsolute(baseDirectory, Environment.ExpandEnvironmentVariables(ProjectPath));
            ProjectFileVersion = DetectProjectVersion(ProjectPath);
            SourceDirectory = MakeAbsolute(baseDirectory, Environment.ExpandEnvironmentVariables(SourceDirectory));
            if (!string.IsNullOrWhiteSpace(SiemensEngineeringDll))
            {
                SiemensEngineeringDll = MakeAbsolute(baseDirectory, Environment.ExpandEnvironmentVariables(SiemensEngineeringDll));
            }
        }

        public void OverrideProject(string projectPath, bool allowNonExistent = false)
        {
            string fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(projectPath));
            ProjectPath = fullPath;
            ProjectFileVersion = DetectProjectVersion(fullPath);
            TiaPortalVersion = ProjectFileVersion;

            if (!allowNonExistent && !File.Exists(fullPath))
            {
                throw new FileNotFoundException("找不到指定的项目文件: " + fullPath, fullPath);
            }
        }

        private static string MakeAbsolute(string baseDirectory, string path)
        {
            if (Path.IsPathRooted(path))
            {
                return Path.GetFullPath(path);
            }
            return Path.GetFullPath(Path.Combine(baseDirectory, path));
        }

        public static string DetectProjectVersion(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "V17";
            string extension = Path.GetExtension(path);
            Match match = Regex.Match(extension, "^\\.(?:ap|als)(?<version>[0-9]+(?:_[0-9]+)?)$", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                return "V17";
            }
            return "V" + match.Groups["version"].Value.Replace('_', '.');
        }
    }
}
