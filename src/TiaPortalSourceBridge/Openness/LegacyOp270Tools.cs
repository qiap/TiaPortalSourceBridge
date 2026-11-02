using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;

namespace TiaPortalSourceBridge.Openness
{
    /// <summary>
    /// 针对特定产线工程（OP260/OP270 HMI 拆分与清理）的专用维护命令，
    /// 从核心 OpennessSession 中剥离，保持通用桥接器代码整洁。
    /// </summary>
    internal static class LegacyOp270Tools
    {
        public static bool TryExecute(string command, Func<string, string> getOption, OpennessSession session, out int exitCode)
        {
            exitCode = 0;
            if (command == "finalize-op270-screens")
            {
                string screenDirectory = getOption("--screen-directory");
                if (string.IsNullOrWhiteSpace(screenDirectory))
                    throw new ArgumentException("需要 --screen-directory。");
                ReplaceOp270Screens(session, Path.GetFullPath(screenDirectory));
                Console.WriteLine("OP270 画面已保存。");
                return true;
            }
            if (command == "retitle-op270-screens")
            {
                string outputDirectory = getOption("--output-directory");
                if (string.IsNullOrWhiteSpace(outputDirectory))
                    throw new ArgumentException("需要 --output-directory。");
                int changed = RetitleOp270Screens(session, Path.GetFullPath(outputDirectory));
                Console.WriteLine("已更新 " + changed + " 个 OP270 画面页眉。");
                return true;
            }
            if (command == "make-op270-main")
            {
                string outputDirectory = getOption("--output-directory");
                if (string.IsNullOrWhiteSpace(outputDirectory))
                    throw new ArgumentException("需要 --output-directory。");
                MakeOp270MainScreen(session, Path.GetFullPath(outputDirectory));
                Console.WriteLine("OP270 主页面已保存。");
                return true;
            }
            if (command == "verify-op270-main")
            {
                string outputDirectory = getOption("--output-directory");
                if (string.IsNullOrWhiteSpace(outputDirectory))
                    throw new ArgumentException("需要 --output-directory。");
                VerifyOp270MainScreen(session, Path.GetFullPath(outputDirectory));
                return true;
            }
            if (command == "remove-op260-legacy-hmi")
            {
                string outputDirectory = getOption("--output-directory");
                if (string.IsNullOrWhiteSpace(outputDirectory))
                    throw new ArgumentException("需要 --output-directory。");
                RemoveOp260LegacyHmiPages(session, Path.GetFullPath(outputDirectory));
                Console.WriteLine("OP260 遗留 HMI 页面与无效变量已清理并保存。");
                return true;
            }
            return false;
        }

        private static HmiContext RequireOp270Hmi(OpennessSession session)
        {
            IList<HmiContext> hmis = session.GetHmis();
            HmiContext hmi = hmis.SingleOrDefault(h => h.MatchNames.Any(n =>
                string.Equals(n, "HD2303-OP270HMI", StringComparison.OrdinalIgnoreCase)));
            if (hmi == null || hmis.Count != 1)
                throw new InvalidOperationException("此命令只适用于仅含 OP270 HMI (HD2303-OP270HMI) 的工程副本。");
            return hmi;
        }

        public static void ReplaceOp270Screens(OpennessSession session, string sourceDirectory)
        {
            ReflectionApi api = session.Api;
            HmiContext hmi = RequireOp270Hmi(session);
            string[] names = { "HD_01_MainScreen", "HD_25_RFID_OP270", "HD_90_Torque", "HD_91_270AutoRun" };
            foreach (string name in names)
                if (!File.Exists(Path.Combine(sourceDirectory, name + ".xml")))
                    throw new FileNotFoundException("缺少画面 XML", Path.Combine(sourceDirectory, name + ".xml"));

            object root = api.Get(hmi.Target, "ScreenFolder", true);
            object folders = api.Get(root, "Folders", true);
            object folder = api.Invoke(folders, "Find", "OP260&270_HMI");
            if (folder == null) throw new InvalidOperationException("找不到 OP260&270_HMI 画面文件夹。");
            object screens = api.Get(folder, "Screens", true);
            foreach (string name in names)
            {
                Console.WriteLine("更新 OP270 画面: " + name);
                object options = api.EnumValue("Siemens.Engineering.ImportOptions", "Override");
                api.Invoke(screens, "Import", new FileInfo(Path.Combine(sourceDirectory, name + ".xml")), options);
            }
            foreach (string name in new[] { "HD_25_RFID_OP260", "HD_91_260AutoRun" })
            {
                object screen = api.Invoke(screens, "Find", name);
                if (screen == null) throw new InvalidOperationException("找不到待移除画面: " + name);
                Console.WriteLine("移除 OP260 专属画面: " + name);
                api.Invoke(screen, "Delete");
            }
            session.SaveProject();
        }

        public static int RetitleOp270Screens(OpennessSession session, string outputDirectory)
        {
            ReflectionApi api = session.Api;
            HmiContext hmi = RequireOp270Hmi(session);
            Directory.CreateDirectory(outputDirectory);
            object root = api.Get(hmi.Target, "ScreenFolder", true);
            int changed = 0;
            foreach (string groupName in new[] { "OP260&270_HMI", "IOMapping" })
            {
                object folder = api.Invoke(api.Get(root, "Folders", true), "Find", groupName);
                if (folder == null) throw new InvalidOperationException("找不到画面文件夹: " + groupName);
                object screens = api.Get(folder, "Screens", true);
                List<string> names = api.Items(screens).Select(s => api.GetString(s, "Name")).ToList();
                foreach (string name in names)
                {
                    object screen = api.Invoke(screens, "Find", name);
                    string file = Path.Combine(outputDirectory, name + ".xml");
                    if (File.Exists(file)) File.Delete(file);
                    object exportOptions = api.EnumValue("Siemens.Engineering.ExportOptions", "WithDefaults");
                    api.Invoke(screen, "Export", new FileInfo(file), exportOptions);
                    string xml = File.ReadAllText(file, Encoding.UTF8);
                    string revised = xml.Replace("<Text><body><p>OP260</p></body></Text>",
                        "<Text><body><p>OP270</p></body></Text>");
                    if (name == "HD_90_Torque")
                        revised = revised.Replace("<Text><body><p>OP260监控</p></body></Text>",
                            "<Text><body><p>OP270监控</p></body></Text>");
                    if (revised == xml) continue;
                    File.WriteAllText(file, revised, new UTF8Encoding(true));
                    object importOptions = api.EnumValue("Siemens.Engineering.ImportOptions", "Override");
                    api.Invoke(screens, "Import", new FileInfo(file), importOptions);
                    Console.WriteLine("更正 OP270 页眉: " + name);
                    changed++;
                }
            }
            session.SaveProject();
            return changed;
        }

        public static void MakeOp270MainScreen(OpennessSession session, string outputDirectory)
        {
            ReflectionApi api = session.Api;
            HmiContext hmi = RequireOp270Hmi(session);
            Directory.CreateDirectory(outputDirectory);

            object templateFolder = api.Get(hmi.Target, "ScreenTemplateFolder", true);
            object templates = api.Get(templateFolder, "ScreenTemplates", true);
            object oldTemplate = api.Invoke(templates, "Find", "Template_OP260");
            if (oldTemplate == null) throw new InvalidOperationException("找不到原有 Template_OP260。");
            string templateFile = Path.Combine(outputDirectory, "Template_OP270.xml");
            if (File.Exists(templateFile)) File.Delete(templateFile);
            api.Invoke(oldTemplate, "Export", new FileInfo(templateFile),
                api.EnumValue("Siemens.Engineering.ExportOptions", "WithDefaults"));
            string templateXml = File.ReadAllText(templateFile, Encoding.UTF8)
                .Replace("Template_OP260", "Template_OP270")
                .Replace("<Text><body><p>OP260</p></body></Text>",
                    "<Text><body><p>OP270</p></body></Text>");
            File.WriteAllText(templateFile, templateXml, new UTF8Encoding(true));
            api.Invoke(templates, "Import", new FileInfo(templateFile),
                api.EnumValue("Siemens.Engineering.ImportOptions", "Override"));
            Console.WriteLine("创建 OP270 专用画面模板。");

            object screenRoot = api.Get(hmi.Target, "ScreenFolder", true);
            object folder = api.Invoke(api.Get(screenRoot, "Folders", true), "Find", "OP260&270_HMI");
            object screens = api.Get(folder, "Screens", true);
            foreach (string name in new[] { "HD_01_MainScreen", "HD_22_HuanXing" })
            {
                object screen = api.Invoke(screens, "Find", name);
                string file = Path.Combine(outputDirectory, name + ".xml");
                if (File.Exists(file)) File.Delete(file);
                api.Invoke(screen, "Export", new FileInfo(file),
                    api.EnumValue("Siemens.Engineering.ExportOptions", "WithDefaults"));
                XmlDocument xml = new XmlDocument();
                xml.PreserveWhitespace = true;
                xml.Load(file);
                XmlNode templateName = xml.SelectSingleNode("//*[local-name()='Template']/*[local-name()='Name']");
                if (templateName == null || templateName.InnerText != "Template_OP260")
                    throw new InvalidOperationException("画面模板引用与预期不符: " + name);
                templateName.InnerText = "Template_OP270";
                if (name == "HD_01_MainScreen")
                {
                    XmlNode op260Button = xml.SelectSingleNode("//*[local-name()='Hmi.Screen.Button' and @ID='295']");
                    XmlNode op270Button = xml.SelectSingleNode("//*[local-name()='Hmi.Screen.Button' and @ID='327']");
                    if (op260Button == null || op270Button == null ||
                        !op260Button.InnerText.Contains("OP260判断工件合格") ||
                        !op270Button.InnerText.Contains("OP270判断工件合格"))
                        throw new InvalidOperationException("主页面工件判断按钮与预期不符；未修改该区域。");
                    XmlNode top = op270Button.SelectSingleNode("./*[local-name()='AttributeList']/*[local-name()='Top']");
                    if (top == null) throw new InvalidOperationException("找不到 OP270 按钮位置。");
                    top.InnerText = "334";
                    op260Button.ParentNode.RemoveChild(op260Button);
                    Console.WriteLine("主页面保留 OP270 工件判断按钮，并移除 OP260 操作按钮。");
                }
                xml.Save(file);
                api.Invoke(screens, "Import", new FileInfo(file),
                    api.EnumValue("Siemens.Engineering.ImportOptions", "Override"));
                Console.WriteLine("绑定 OP270 模板: " + name);
            }
            api.Invoke(oldTemplate, "Delete");
            session.SaveProject();
        }

        public static void VerifyOp270MainScreen(OpennessSession session, string outputDirectory)
        {
            ReflectionApi api = session.Api;
            HmiContext hmi = RequireOp270Hmi(session);
            Directory.CreateDirectory(outputDirectory);
            object templates = api.Get(api.Get(hmi.Target, "ScreenTemplateFolder", true), "ScreenTemplates", true);
            object template = api.Invoke(templates, "Find", "Template_OP270");
            if (template == null || api.Invoke(templates, "Find", "Template_OP260") != null)
                throw new InvalidOperationException("OP270 模板未正确替换。");
            string templateFile = Path.Combine(outputDirectory, "Template_OP270.xml");
            if (File.Exists(templateFile)) File.Delete(templateFile);
            api.Invoke(template, "Export", new FileInfo(templateFile),
                api.EnumValue("Siemens.Engineering.ExportOptions", "WithDefaults"));
            string templateXml = File.ReadAllText(templateFile, Encoding.UTF8);
            if (!templateXml.Contains("<Text><body><p>OP270</p></body></Text>") ||
                templateXml.Contains("<Text><body><p>OP260</p></body></Text>"))
                throw new InvalidOperationException("模板顶部站名未正确更新。");
            object root = api.Get(hmi.Target, "ScreenFolder", true);
            object folder = api.Invoke(api.Get(root, "Folders", true), "Find", "OP260&270_HMI");
            object screens = api.Get(folder, "Screens", true);
            foreach (string name in new[] { "HD_01_MainScreen", "HD_22_HuanXing" })
            {
                string file = Path.Combine(outputDirectory, name + ".xml");
                if (File.Exists(file)) File.Delete(file);
                api.Invoke(api.Invoke(screens, "Find", name), "Export", new FileInfo(file),
                    api.EnumValue("Siemens.Engineering.ExportOptions", "WithDefaults"));
                string xml = File.ReadAllText(file, Encoding.UTF8);
                if (!xml.Contains("<Name>Template_OP270</Name>") || xml.Contains("<Name>Template_OP260</Name>"))
                    throw new InvalidOperationException("画面仍引用 OP260 模板: " + name);
                if (name == "HD_01_MainScreen" &&
                    (xml.Contains("OP260判断工件合格") || !xml.Contains("OP270判断工件合格")))
                    throw new InvalidOperationException("主页面工件判断操作未正确切换到 OP270。");
            }
            Console.WriteLine("核对通过：OP270 模板、首页站名和 OP270 工件判断按钮均已生效。");
        }

        public static void RemoveOp260LegacyHmiPages(OpennessSession session, string outputDirectory)
        {
            ReflectionApi api = session.Api;
            HmiContext hmi = RequireOp270Hmi(session);
            Directory.CreateDirectory(outputDirectory);
            string[] obsoleteScreens = {
                "HD_52_IAI_Left_X", "HD_53_IAI_Left_Y", "HD_54_IAI_Left_Z",
                "HD_55_IAI_Right_X", "HD_56_IAI_Right_Y", "HD_57_IAI_Right_Z",
                "HD_71_Para_Left_X", "HD_72_Para_Left_Y", "HD_73_Para_Left_Z",
                "HD_74_Para_Right_X", "HD_75_Para_Right_Y", "HD_76_Para_Right_Z",
                "HD_90_Torque"
            };
            HashSet<string> obsolete = new HashSet<string>(obsoleteScreens, StringComparer.OrdinalIgnoreCase);
            object screenRoot = api.Get(hmi.Target, "ScreenFolder", true);
            object screenFolder = api.Invoke(api.Get(screenRoot, "Folders", true), "Find", "OP260&270_HMI");
            object screens = api.Get(screenFolder, "Screens", true);
            foreach (string name in obsoleteScreens)
                if (api.Invoke(screens, "Find", name) == null)
                    throw new InvalidOperationException("找不到待移除画面: " + name);

            object main = api.Invoke(screens, "Find", "HD_01_MainScreen");
            string mainFile = Path.Combine(outputDirectory, "HD_01_MainScreen.xml");
            if (File.Exists(mainFile)) File.Delete(mainFile);
            api.Invoke(main, "Export", new FileInfo(mainFile),
                api.EnumValue("Siemens.Engineering.ExportOptions", "WithDefaults"));
            XmlDocument xml = new XmlDocument();
            xml.PreserveWhitespace = true;
            xml.Load(mainFile);
            int removedButtons = 0;
            foreach (XmlNode button in xml.SelectNodes("//*[local-name()='Hmi.Screen.Button']").Cast<XmlNode>().ToList())
            {
                XmlNode objectName = button.SelectSingleNode("./*[local-name()='AttributeList']/*[local-name()='ObjectName']");
                bool isLegacyHeader = objectName != null && objectName.InnerText == "OBJECT_7";
                bool linksToLegacy = button.SelectNodes(".//*[local-name()='Hmi.Event.FunctionListEntryParameter']/*[local-name()='LinkList']/*[local-name()='Value']/*[local-name()='Name']")
                    .Cast<XmlNode>().Any(n => obsolete.Contains(n.InnerText));
                if (!isLegacyHeader && !linksToLegacy) continue;
                button.ParentNode.RemoveChild(button);
                removedButtons++;
            }
            if (removedButtons != 8)
                throw new InvalidOperationException("首页 OP260 入口数量与预期不符: " + removedButtons);
            xml.Save(mainFile);
            api.Invoke(screens, "Import", new FileInfo(mainFile),
                api.EnumValue("Siemens.Engineering.ImportOptions", "Override"));
            Console.WriteLine("首页移除 " + removedButtons + " 个 OP260 轴/扭矩入口。");

            foreach (string name in obsoleteScreens)
            {
                api.Invoke(api.Invoke(screens, "Find", name), "Delete");
                Console.WriteLine("移除 OP260 画面: " + name);
            }

            object tagRoot = api.Get(hmi.Target, "TagFolder", true);
            object op260Folder = api.Invoke(api.Get(tagRoot, "Folders", true), "Find", "OP0260");
            object tables = api.Get(op260Folder, "TagTables", true);
            object torqueTable = api.Invoke(tables, "Find", "HD_92_Torque,angle");
            if (torqueTable == null || api.Items(api.Get(torqueTable, "Tags", true)).Count() != 48)
                throw new InvalidOperationException("OP260 扭矩变量表与预期不符。");
            api.Invoke(torqueTable, "Delete");
            Console.WriteLine("移除 48 个无效 OP260 扭矩变量。");

            foreach (string side in new[] { "Left", "Right" })
                foreach (string axis in new[] { "X", "Y", "Z" })
                {
                    string tableName = "HD_51_IAI_" + side + "_" + axis;
                    object table = api.Invoke(tables, "Find", tableName);
                    object tags = api.Get(table, "Tags", true);
                    string prefix = "IAI" + (side == "Left" ? "左" : "右") + axis + "轴";
                    foreach (string suffix in new[] { "目标速度", "目标位置" })
                    {
                        string name = prefix + suffix;
                        object tag = api.Invoke(tags, "Find", name);
                        if (tag == null) throw new InvalidOperationException("找不到无效 IAI 变量: " + name);
                        api.Invoke(tag, "Delete");
                        Console.WriteLine("移除无效 IAI 变量: " + name);
                    }
                }
            object stationTable = api.Invoke(tables, "Find", "HD_91_状态监控");
            object stationTags = api.Get(stationTable, "Tags", true);
            object oldMesTag = api.Invoke(stationTags, "Find", "OP260_MES_DB_OP260MES屏蔽");
            if (oldMesTag == null) throw new InvalidOperationException("找不到无效 OP260 MES 变量。");
            api.Invoke(oldMesTag, "Delete");
            Console.WriteLine("移除无效 OP260 MES 变量。");
            session.SaveProject();
        }
    }
}
