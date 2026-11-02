using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        public string[] ToolProfiles { get; set; }

        internal string ConfigFilePath { get; set; }
        internal string ProjectFileVersion { get; set; }
        internal bool IsLocalSession { get; set; }

        public AppConfig()
        {
            TiaPortalVersion = "V21";
            PlcName = "PLC_1";
            SourceDirectory = ".\\plc-src";
            SiemensEngineeringDll = "";
            ConnectionMode = "AttachOrStart";
            WithUserInterface = true;
            SaveProjectAfterImport = false;
            CompileBeforeExport = true;
            IncludeSystemBlocks = false;
            ContinueOnError = true;
            AllowProjectUpgrade = true;
            ToolProfiles = new string[0];
        }

        internal bool IsProfileEnabled(string profile)
        {
            if (string.IsNullOrWhiteSpace(profile) || string.Equals(profile, "core", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (ToolProfiles == null || ToolProfiles.Length == 0)
            {
                return true;
            }
            return ToolProfiles.Any(p =>
                string.Equals(p, "all", StringComparison.OrdinalIgnoreCase)
                || string.Equals(p, profile, StringComparison.OrdinalIgnoreCase));
        }

        internal void NormalizeAndValidate(string configFilePath)
        {
            ConfigFilePath = Path.GetFullPath(configFilePath);
            string baseDirectory = Path.GetDirectoryName(ConfigFilePath);

            if (string.IsNullOrWhiteSpace(TiaPortalVersion))
            {
                TiaPortalVersion = "V21";
            }
            if (string.IsNullOrWhiteSpace(ProjectPath))
            {
                throw new InvalidOperationException("config.json 中必须设置 projectPath（例如 .ap21 或 .als21）。");
            }
            if (string.IsNullOrWhiteSpace(PlcName))
            {
                throw new InvalidOperationException("config.json 中必须设置 plcName；可先运行 list-plcs 查看。");
            }

            ProjectPath = MakeAbsolute(baseDirectory, Environment.ExpandEnvironmentVariables(ProjectPath));
            bool isLocalSession;
            ProjectFileVersion = DetectProjectVersion(ProjectPath, out isLocalSession);
            IsLocalSession = isLocalSession;
            SourceDirectory = MakeAbsolute(baseDirectory, Environment.ExpandEnvironmentVariables(SourceDirectory));
            if (!string.IsNullOrWhiteSpace(SiemensEngineeringDll))
            {
                SiemensEngineeringDll = MakeAbsolute(baseDirectory, Environment.ExpandEnvironmentVariables(SiemensEngineeringDll));
            }
        }

        internal void OverrideProject(string projectPath, bool useProjectSideSourceDirectory)
        {
            string fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(projectPath));
            ProjectPath = fullPath;
            bool isLocalSession;
            ProjectFileVersion = DetectProjectVersion(fullPath, out isLocalSession);
            IsLocalSession = isLocalSession;
            TiaPortalVersion = "V21";

            if (useProjectSideSourceDirectory)
            {
                string projectDirectory = Path.GetDirectoryName(fullPath);
                string projectName = Path.GetFileNameWithoutExtension(fullPath);
                SourceDirectory = Path.Combine(projectDirectory, projectName + "_PLC_Source");
            }
        }

        private static string MakeAbsolute(string baseDirectory, string path)
        {
            return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(baseDirectory, path));
        }

        internal static string DetectProjectVersion(string path, out bool isLocalSession)
        {
            string extension = Path.GetExtension(path) ?? "";
            Match match = Regex.Match(extension, "^\\.(?<kind>ap|als)(?<version>[0-9]+(?:_[0-9]+)?)$", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                throw new InvalidOperationException("请选择解档后的 TIA Portal V21 项目文件（.ap21 / 多用户本地会话 .als21，或允许升级的 .apXX）；.zap21 归档请先解档。");
            }
            isLocalSession = string.Equals(match.Groups["kind"].Value, "als", StringComparison.OrdinalIgnoreCase);
            return "V" + match.Groups["version"].Value.Replace('_', '.');
        }
    }
}
