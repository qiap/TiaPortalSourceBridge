using System;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace TiaPortalSourceBridge.Interactive
{
    internal sealed class RecentSettings
    {
        public string LastProjectPath { get; set; }
        public string LastPlcName { get; set; }
        public string LastAction { get; set; }

        private string FilePath { get; set; }

        public RecentSettings()
        {
            LastProjectPath = "";
            LastPlcName = "";
            LastAction = "export";
        }

        public static RecentSettings Load(string toolDirectory)
        {
            string path = Path.Combine(toolDirectory, "recent.json");
            RecentSettings settings = null;
            if (File.Exists(path))
            {
                try
                {
                    settings = new JavaScriptSerializer().Deserialize<RecentSettings>(File.ReadAllText(path, Encoding.UTF8));
                }
                catch
                {
                    settings = null;
                }
            }
            if (settings == null) settings = new RecentSettings();
            settings.FilePath = path;
            return settings;
        }

        public void Save()
        {
            File.WriteAllText(FilePath, new JavaScriptSerializer().Serialize(this), new UTF8Encoding(true));
        }
    }
}
