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

            TiaPortalVersion = PortalVersions.Normalize(TiaPortalVersion);
            if (TiaPortalVersion == null)
            {
                throw new InvalidOperationException("config.json 的 tiaPortalVersion 需要使用 V16 至 V21。");
            }
            if (string.IsNullOrWhiteSpace(ProjectPath))
            {
                throw new InvalidOperationException("config.json 中必须设置 projectPath。");
            }

            ProjectPath = MakeAbsolute(baseDirectory, Environment.ExpandEnvironmentVariables(ProjectPath));
            ProjectFileVersion = RequireProjectVersion(ProjectPath);
            SourceDirectory = MakeAbsolute(baseDirectory, Environment.ExpandEnvironmentVariables(SourceDirectory));
            if (!string.IsNullOrWhiteSpace(SiemensEngineeringDll))
            {
                SiemensEngineeringDll = MakeAbsolute(baseDirectory, Environment.ExpandEnvironmentVariables(SiemensEngineeringDll));
            }
        }

        internal void SelectPortalVersion(string version)
        {
            string target = PortalVersions.Normalize(version);
            if (target == null) throw new ArgumentException("请选择 V16 至 V21。", "version");
            if (target != PortalVersions.Normalize(TiaPortalVersion)) SiemensEngineeringDll = "";
            TiaPortalVersion = target;
        }

        public void OverrideProject(string projectPath, bool allowNonExistent = false)
        {
            string fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(projectPath));
            string version = RequireProjectVersion(fullPath);
            if (!allowNonExistent && !File.Exists(fullPath))
            {
                throw new FileNotFoundException("找不到指定的项目文件: " + fullPath, fullPath);
            }
            // An explicitly authorized upgrade keeps the selected engine for older source projects.
            SelectPortalVersion(PortalVersions.Normalize(version) ?? TiaPortalVersion);
            ProjectPath = fullPath;
            ProjectFileVersion = version;
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
            return PortalVersions.FromProjectPath(path);
        }

        private string RequireProjectVersion(string path)
        {
            string version = DetectProjectVersion(path);
            if (version == null && AllowProjectUpgrade)
            {
                Match legacy = Regex.Match(Path.GetExtension(path), @"^\.ap(1[0-5])(?:_([0-9]+))?$", RegexOptions.IgnoreCase);
                if (legacy.Success) return "V" + legacy.Groups[1].Value + (legacy.Groups[2].Success ? "." + legacy.Groups[2].Value : "");
            }
            if (version == null) throw new ArgumentException("请选择 V16 至 V21 的 .ap 或 .als 工程文件。", "projectPath");
            return version;
        }
    }
}
