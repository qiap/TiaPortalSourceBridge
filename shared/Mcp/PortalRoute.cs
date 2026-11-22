using System;
using System.Collections.Generic;
using System.IO;
using TiaPortalSourceBridge.Configuration;
using System.Xml;

namespace TiaPortalSourceBridge.Mcp
{
    internal static class PortalRoute
    {
        internal const string WorkerEnvironmentKey = "TIA_BRIDGE_WORKER_MAJOR";

        internal static string Exact(string version)
        {
            string value = PortalVersions.Normalize(version);
            if (value == null) throw new ArgumentException("不支持的博途版本 " + version + "；请选择 V16 至 V21。");
            return value;
        }

        internal static string FromProject(string path)
        {
            string version = PortalVersions.FromProjectPath(path);
            if (version == null) throw new ArgumentException("无法识别工程扩展名，请指定 .ap16 至 .ap21 工程或同版本的 .als 本地会话：" + path);
            return version;
        }

        internal static string FromIdentity(string identity)
        {
            foreach (string prefix in new[] { "discovery:", "local:" })
                if (identity.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return Exact(identity.Substring(prefix.Length));
            return FromProject(identity);
        }

        internal static void RequireMatch(string version, string expected)
        {
            if (version != null && expected != null && version != expected)
                throw new ArgumentException("博途目标版本不一致：" + version + " 与 " + expected + "。请核对 portal_version 和工程或 XML 的版本。");
        }

        internal static string Requested(Dictionary<string, object> args)
        {
            object value;
            string version = args.TryGetValue("portal_version", out value) ? Exact(Convert.ToString(value)) : null;
            string path = args.TryGetValue("project_path", out value) ? Convert.ToString(value) : null;
            if (string.IsNullOrWhiteSpace(path)) return version;
            string projectVersion = FromProject(path);
            RequireMatch(version, projectVersion);
            return projectVersion;
        }

        internal static string XmlVersion(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) return null;
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32 * 1024 * 1024 };
            using (var reader = XmlReader.Create(new StringReader(xml), settings))
            {
                reader.MoveToContent();
                if (reader.LocalName == "Document" && reader.ReadToDescendant("Engineering") && reader.Depth == 1)
                    return Exact(reader.GetAttribute("version"));
            }
            throw new ArgumentException("XML 缺少 Document/Engineering 的版本，无法确定目标博途版本。");
        }
    }
}
