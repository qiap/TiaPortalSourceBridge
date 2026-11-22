using System;
using System.IO;

namespace TiaPortalSourceBridge.Configuration
{
    internal static class PortalVersions
    {
        public static string Normalize(string version)
        {
            string value = (version ?? "").Trim();
            if (value.StartsWith("V", StringComparison.OrdinalIgnoreCase)) value = value.Substring(1);
            if (value.EndsWith(".0", StringComparison.Ordinal)) value = value.Substring(0, value.Length - 2);
            switch (value)
            {
                case "16": case "17": case "18": case "19": case "20": case "21": return "V" + value;
                default: return null;
            }
        }

        public static bool IsClassic(string version)
        {
            string normalized = Normalize(version);
            return normalized != null && normalized != "V21";
        }

        public static string FromProjectPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            string extension = Path.GetExtension(path);
            foreach (string prefix in new[] { ".ap", ".als" })
            {
                if (extension.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    string suffix = extension.Substring(prefix.Length);
                    if (suffix.Length == 2 && Normalize(suffix) != null) return "V" + suffix;
                }
            }
            return null;
        }
    }
}
