using System;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace TiaPortalSourceBridge.Configuration
{
    internal static class ConfigLoader
    {
        public static AppConfig Load(string path)
        {
            string fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("找不到配置文件。请复制并修改 config.json。", fullPath);
            }

            string json = File.ReadAllText(fullPath, Encoding.UTF8);
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            AppConfig config = serializer.Deserialize<AppConfig>(json);
            if (config == null)
            {
                throw new InvalidOperationException("config.json 内容为空或无效。");
            }

            config.NormalizeAndValidate(fullPath);
            return config;
        }
    }
}
