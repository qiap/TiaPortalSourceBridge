using System;
using System.Collections.Generic;
using System.IO;
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

        private static string PathText(object value)
        {
            if (value == null) return "";
            FileInfo file = value as FileInfo;
            return file == null ? Convert.ToString(value) : file.FullName;
        }
    }
}
