using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using Microsoft.Win32;
using TiaPortalSourceBridge.Configuration;

namespace TiaPortalSourceBridge.Openness
{
    internal static class OpennessSdk
    {
        // Only used when the official registration and normal install paths are absent.
        private static readonly object FolderLock = new object();
        private static string[] alternateFolders;
        private static DateTime nextFolderScanUtc;

        internal static string PrimaryName(string version)
        {
            return version == "V21" ? "Siemens.Engineering.Base" : "Siemens.Engineering";
        }

        internal static string Resolve(string version, string configuredPath, bool modularEngine)
        {
            version = PortalVersions.Normalize(version);
            if (version == null || (version == "V21") != modularEngine)
                throw new NotSupportedException(modularEngine ? "此引擎要求 V21 Openness。" : "此引擎要求 V16 至 V20 Openness，V21 请使用独立引擎。");

            string pin = string.IsNullOrWhiteSpace(configuredPath)
                ? Environment.GetEnvironmentVariable("SIEMENS_ENGINEERING_DLL") : configuredPath;
            if (!string.IsNullOrWhiteSpace(pin))
            {
                string path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(pin.Trim().Trim('"')));
                if (Directory.Exists(path)) path = Path.Combine(path, PrimaryName(version) + ".dll");
                if (MatchesPrimary(path, version)) return path;
                throw new FileNotFoundException("指定的 Siemens Openness API 与 " + version + " 不匹配、文件缺失或无法读取。请检查 siemensEngineeringDll / SIEMENS_ENGINEERING_DLL。", path);
            }

            string installed = FindInstalled(version);
            if (installed != null) return installed;
            throw new FileNotFoundException("未找到 TIA Portal " + version + " 的原生 Openness API。请安装该版本的 Openness，或用 siemensEngineeringDll 指定对应的 "
                + PrimaryName(version) + ".dll。");
        }

        // Reading manifests never loads the SDK or attaches to a project.
        internal static string FindInstalled(string requested)
        {
            string version = PortalVersions.Normalize(requested);
            if (version == null) return null;
            string registered = RegisteredPaths(version).FirstOrDefault(path => MatchesPrimary(path, version));
            if (registered != null) return Path.GetFullPath(registered);
            string found = InstallationFolders(version).Concat(AlternateInstallations().Where(folder =>
                string.Equals(Path.GetFileName(folder).Replace('_', ' '), "Portal " + version, StringComparison.OrdinalIgnoreCase)))
                .Select(folder => PrimaryIn(folder, version))
                .FirstOrDefault(path => MatchesPrimary(path, version));
            return found == null ? null : Path.GetFullPath(found);
        }

        internal static string PrimaryIn(string portalFolder, string version)
        {
            string directory = Path.Combine(portalFolder, "PublicAPI", version);
            if (version == "V21") directory = Path.Combine(directory, "net48");
            return Path.Combine(directory, PrimaryName(version) + ".dll");
        }

        internal static bool MatchesPrimary(string path, string version)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
            string expected = PrimaryName(version);
            if (!string.Equals(Path.GetFileName(path), expected + ".dll", StringComparison.OrdinalIgnoreCase)) return false;
            DirectoryInfo directory = new FileInfo(path).Directory;
            for (; directory != null; directory = directory.Parent)
            {
                if (directory.Name.StartsWith("net8", StringComparison.OrdinalIgnoreCase)) return false;
                if (directory.Name.Equals("PublicAPI", StringComparison.OrdinalIgnoreCase) && directory.Parent != null)
                {
                    string folder = directory.Parent.Name.Replace('_', ' ');
                    if (folder.StartsWith("Portal V", StringComparison.OrdinalIgnoreCase)
                        && !folder.Substring("Portal ".Length).Equals(version, StringComparison.OrdinalIgnoreCase)) return false;
                    break;
                }
            }
            AssemblyName identity = ReadIdentity(path);
            return identity != null && identity.Name.Equals(expected, StringComparison.OrdinalIgnoreCase)
                && version == "V" + identity.Version.Major;
        }

        internal static bool MatchesProcess(string projectPath, string executablePath, string version)
        {
            string projectVersion = PortalVersions.FromProjectPath(projectPath);
            if (projectVersion != null && version != projectVersion) return false;
            if (!string.IsNullOrWhiteSpace(executablePath))
            {
                var dir = new FileInfo(executablePath).Directory;
                for (; dir != null; dir = dir.Parent)
                {
                    string name = dir.Name.Replace('_', ' ');
                    if (name.StartsWith("Portal V", StringComparison.OrdinalIgnoreCase))
                        return PortalVersions.Normalize(name.Substring("Portal ".Length)) == version;
                }
                if (File.Exists(executablePath))
                {
                    int major = FileVersionInfo.GetVersionInfo(executablePath).ProductMajorPart;
                    if (major >= 1600 && major <= 2199) major /= 100;
                    if (major >= 16 && major <= 21) return version == "V" + major;
                }
            }
            return projectVersion == version;
        }

        internal static AssemblyName ReadIdentity(string path)
        {
            try { return AssemblyName.GetAssemblyName(path); }
            catch (BadImageFormatException) { return null; }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (SecurityException) { return null; }
        }

        internal static bool MatchesReference(AssemblyName candidate, AssemblyName requested)
        {
            return candidate != null && string.Equals(candidate.FullName, requested.FullName, StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> RegisteredPaths(string version)
        {
            string major = version.Substring(1);
            // Earlier APIs shipped with a newer Portal appear under the newer host key too.
            // Only the native host's own PublicAPI key identifies an installed runtime.
            string key = @"SOFTWARE\Siemens\Automation\Openness\" + major + @".0\PublicAPI\" + major + ".0.0.0";
            if (version == "V21") key += @"\net48";
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                string path = null;
                try
                {
                    using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (var api = machine.OpenSubKey(key))
                        if (api != null) path = api.GetValue(PrimaryName(version)) as string;
                }
                catch (UnauthorizedAccessException) { }
                catch (SecurityException) { }
                catch (IOException) { }
                if (!string.IsNullOrWhiteSpace(path)) yield return path.Trim().Trim('"');
            }
        }

        private static string[] ChildDirectories(string path)
        {
            try { return Directory.Exists(path) ? Directory.GetDirectories(path) : new string[0]; }
            catch (IOException) { return new string[0]; }
            catch (UnauthorizedAccessException) { return new string[0]; }
            catch (SecurityException) { return new string[0]; }
        }

        private static bool IsLink(string directory)
        {
            try { return (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0; }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
        }

        private static IEnumerable<string> AlternateInstallations()
        {
            string[] snapshot;
            lock (FolderLock)
            {
                if (alternateFolders == null || DateTime.UtcNow >= nextFolderScanUtc)
                {
                    var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (DriveInfo drive in DriveInfo.GetDrives())
                    {
                        if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                        string root = drive.RootDirectory.FullName;
                        foreach (string name in new[] { @"Siemens\Automation", @"Program Files\Siemens\Automation",
                            @"Program Files (x86)\Siemens\Automation", "TIA", "plc" })
                        {
                            var level = new[] { Path.Combine(root, name) };
                            for (int i = 0; i < 3 && level.Length != 0; i++)
                            {
                                var next = new List<string>();
                                foreach (string directory in level)
                                {
                                    foreach (string child in ChildDirectories(directory))
                                    {
                                        if (IsLink(child)) continue;
                                        string leaf = Path.GetFileName(child).Replace('_', ' ');
                                        if (leaf.StartsWith("Portal V", StringComparison.OrdinalIgnoreCase)) found.Add(child);
                                        else if (i < 2) next.Add(child);
                                    }
                                }
                                level = next.ToArray();
                            }
                        }
                    }
                    alternateFolders = found.ToArray();
                    nextFolderScanUtc = DateTime.UtcNow.AddMinutes(1);
                }
                snapshot = alternateFolders;
            }
            foreach (string path in snapshot) yield return path;
        }

        private static IEnumerable<string> InstallationFolders(string version)
        {
            foreach (var name in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            {
                string folder = Environment.GetFolderPath(name);
                if (!string.IsNullOrWhiteSpace(folder)) yield return Path.Combine(folder, "Siemens", "Automation", "Portal " + version);
            }
            var folders = new List<string>();
            foreach (var process in Process.GetProcessesByName("Siemens.Automation.Portal"))
            {
                using (process)
                {
                    try
                    {
                        var module = process.MainModule;
                        var bin = module == null ? null : new FileInfo(module.FileName).Directory;
                        if (bin != null && bin.Parent != null) folders.Add(bin.Parent.FullName);
                    }
                    catch (Win32Exception) { }
                    catch (InvalidOperationException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            foreach (string folder in folders.Distinct(StringComparer.OrdinalIgnoreCase)) yield return folder;
        }
    }
}
