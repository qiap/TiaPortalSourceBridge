using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace TiaPortalSourceBridge.Mcp
{
    internal static class VersionedCli
    {
        internal static int Run(string command, string[] originalArgs, string configPath, string projectPath)
        {
            string root = null;
            foreach (string start in new[] { Path.GetDirectoryName(Path.GetFullPath(configPath)), AppDomain.CurrentDomain.BaseDirectory })
                for (var dir = new DirectoryInfo(start); dir != null && root == null; dir = dir.Parent)
                {
                    string candidate = Path.Combine(dir.FullName, "engines", "v21");
                    if (File.Exists(Path.Combine(candidate, "mcp-config.json"))) root = candidate;
                }
            if (root == null) throw new DirectoryNotFoundException("缺少 engines/v21，请运行 Build-All.ps1。");
            var arguments = new List<string> { command };
            for (int i = 1; i < originalArgs.Length; i++)
            {
                if (originalArgs[i] == "--config" || originalArgs[i] == "--project") { i++; continue; }
                arguments.Add(originalArgs[i]);
            }
            arguments.Add("--config"); arguments.Add(Path.GetFullPath(configPath));
            arguments.Add("--project"); arguments.Add(projectPath);
            string executable = Path.Combine(root, "src", "TiaPortalSourceBridge", "bin", "Release", "net48", "TiaPortalSourceBridge.exe");
            // Windows filename arguments cannot contain quotes. Quote trailing backslashes correctly.
            var quoted = new List<string>();
            foreach (string argument in arguments)
            {
                if (argument.IndexOfAny(new[] { '"', '\r', '\n' }) >= 0) throw new ArgumentException("命令参数包含非法字符。");
                int trailing = argument.Length - argument.TrimEnd('\\').Length;
                quoted.Add("\"" + argument + new string('\\', trailing) + "\"");
            }
            using (var process = Process.Start(new ProcessStartInfo(executable, string.Join(" ", quoted)) { UseShellExecute = false }))
            { process.WaitForExit(); return process.ExitCode; }
        }
    }
}
