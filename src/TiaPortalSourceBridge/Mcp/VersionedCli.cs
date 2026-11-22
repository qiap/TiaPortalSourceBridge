using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace TiaPortalSourceBridge.Mcp
{
    internal static class VersionedCli
    {
        internal static int Discover(string configPath, string version, string projectPath)
        {
            var output = Console.Out;
            try
            {
                var args = new Dictionary<string, object>();
                if (version != null) args["portal_version"] = PortalRoute.Exact(version);
                if (!string.IsNullOrWhiteSpace(projectPath)) args["project_path"] = projectPath;
                using (var server = new McpServer(configPath))
                {
                    var result = server.DashboardRead("tia_list_open_projects", args);
                    object data;
                    if (Convert.ToBoolean(result["isError"]) || !result.TryGetValue("structuredContent", out data))
                    {
                        Console.Error.WriteLine(new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(result["content"]));
                        return 1;
                    }
                    var summary = (Dictionary<string, object>)data;
                    var rows = (object[])summary["processes"];
                    output.WriteLine("FOUND " + rows.Length + " PORTAL PROCESSES:");
                    foreach (Dictionary<string, object> row in rows)
                        output.WriteLine("PID=" + row["process_id"] + " | Proj=" + row["project_path"] + " | Ver=" + row["portal_version"]);
                    object errors;
                    if (summary.TryGetValue("discovery_errors", out errors))
                        foreach (string error in (string[])errors) Console.Error.WriteLine(error);
                    return errors is string[] && ((string[])errors).Length > 0 ? 1 : 0;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
            finally { Console.SetOut(output); }
        }

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
