using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using TiaPortalSourceBridge.Configuration;

namespace TiaPortalSourceBridge.Openness
{
    internal sealed class PortalProcessInfo
    {
        public int ProcessId { get; set; }
        public string ExecutablePath { get; set; }
        public string Mode { get; set; }
        public string ProjectPath { get; set; }
        public string PortalVersion { get; set; }
    }

    internal static class PortalDiscovery
    {
        public static IList<PortalProcessInfo> List(AppConfig config)
        {
            ReflectionApi api = new ReflectionApi(config);
            Type portalType = api.RequireType("Siemens.Engineering.TiaPortal");
            object processes = api.InvokeStatic(portalType, "GetProcesses");
            List<PortalProcessInfo> result = new List<PortalProcessInfo>();

            foreach (object process in api.Items(processes))
            {
                result.Add(new PortalProcessInfo
                {
                    ProcessId = Convert.ToInt32(api.Get(process, "Id", false) ?? 0),
                    ExecutablePath = PathText(api.Get(process, "Path", false)),
                    Mode = Convert.ToString(api.Get(process, "Mode", false) ?? ""),
                    ProjectPath = PathText(api.Get(process, "ProjectPath", false)),
                    PortalVersion = api.LoadedPortalVersion
                });
            }
            return result;
        }

        public static IList<PortalProcessInfo> ListAll(AppConfig config)
        {
            List<PortalProcessInfo> all = new List<PortalProcessInfo>();
            var portalProcs = Process.GetProcessesByName("Siemens.Automation.Portal");
            HashSet<string> versionsToQuery = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var proc in portalProcs)
            {
                try
                {
                    string exe = proc.MainModule != null ? proc.MainModule.FileName : "";
                    Match m = Regex.Match(exe, @"Portal\s+V(?<ver>\d+(?:[._]\d+)?)", RegexOptions.IgnoreCase);
                    if (m.Success) versionsToQuery.Add("V" + m.Groups["ver"].Value);
                }
                catch { }
                finally { proc.Dispose(); }
            }

            if (config != null && !string.IsNullOrWhiteSpace(config.TiaPortalVersion))
            {
                versionsToQuery.Add(config.TiaPortalVersion);
            }
            if (versionsToQuery.Count == 0)
            {
                versionsToQuery.Add("V17");
            }

            foreach (string ver in versionsToQuery)
            {
                try
                {
                    AppConfig copy = new AppConfig
                    {
                        TiaPortalVersion = ver,
                        ProjectPath = config != null ? config.ProjectPath : "",
                        SiemensEngineeringDll = config != null ? config.SiemensEngineeringDll : ""
                    };
                    var list = List(copy);
                    foreach (var item in list)
                    {
                        if (!all.Any(x => x.ProcessId == item.ProcessId))
                        {
                            all.Add(item);
                        }
                    }
                }
                catch { }
            }

            return all;
        }

        private static string PathText(object value)
        {
            if (value == null) return "";
            FileInfo file = value as FileInfo;
            return file == null ? Convert.ToString(value) : file.FullName;
        }
    }
}
