using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace TiaPortalSourceBridge.Interactive
{
    internal sealed class RecentSettings
    {
        public string LastProjectPath { get; set; }
        public string LastPlcName { get; set; }
        public string LastAction { get; set; }
        public List<string> RecentProjectPaths { get; set; }

        [ScriptIgnore]
        private string FilePath { get; set; }

        public RecentSettings()
        {
            LastProjectPath = "";
            LastPlcName = "";
            LastAction = "export";
            RecentProjectPaths = new List<string>();
        }

        public void AddRecentProject(string projectPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath)) return;
            if (RecentProjectPaths == null) RecentProjectPaths = new List<string>();
            try
            {
                string full = Path.GetFullPath(projectPath);
                RecentProjectPaths.RemoveAll(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase));
                RecentProjectPaths.Insert(0, full);
                if (RecentProjectPaths.Count > 10)
                {
                    RecentProjectPaths = RecentProjectPaths.Take(10).ToList();
                }
            }
            catch { }
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
            if (settings.RecentProjectPaths == null) settings.RecentProjectPaths = new List<string>();
            if (!string.IsNullOrWhiteSpace(settings.LastProjectPath))
            {
                settings.AddRecentProject(settings.LastProjectPath);
            }
            settings.FilePath = path;
            return settings;
        }

        public void Save()
        {
            try
            {
                File.WriteAllText(FilePath, new JavaScriptSerializer().Serialize(this), new UTF8Encoding(true));
            }
            catch { }
        }
    }
}
