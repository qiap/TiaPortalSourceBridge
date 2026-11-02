using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TiaPortalSourceBridge.Openness
{
    internal static class PathTools
    {
        public static string SafeName(string name)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            char[] chars = (name ?? "unnamed").Select(c => invalid.Contains(c) ? '_' : c).ToArray();
            string safe = new string(chars).Trim().TrimEnd('.');
            return string.IsNullOrWhiteSpace(safe) ? "unnamed" : safe;
        }

        public static string GroupDirectory(string root, string groupPath)
        {
            string result = root;
            foreach (string part in SplitGroupPath(groupPath))
            {
                result = Path.Combine(result, SafeName(part));
            }
            return result;
        }

        public static IList<string> SplitGroupPath(string groupPath)
        {
            if (string.IsNullOrWhiteSpace(groupPath)) return new List<string>();
            return groupPath
                .Replace('\\', '/')
                .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToList();
        }

        public static string NormalizeGroupPath(string groupPath)
        {
            IList<string> parts = SplitGroupPath(groupPath);
            return parts.Count == 0 ? "" : string.Join("/", parts);
        }

        public static string RelativeTo(string root, string file)
        {
            Uri rootUri = new Uri(AppendSeparator(Path.GetFullPath(root)));
            Uri fileUri = new Uri(Path.GetFullPath(file));
            return Uri.UnescapeDataString(rootUri.MakeRelativeUri(fileUri).ToString()).Replace('/', Path.DirectorySeparatorChar);
        }

        private static string AppendSeparator(string path)
        {
            return path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? path : path + Path.DirectorySeparatorChar;
        }
    }
}
