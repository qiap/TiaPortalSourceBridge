using System;
using System.IO;
using System.Windows.Forms;

namespace TiaPortalSourceBridge.Interactive
{
    internal static class InteractiveLauncher
    {
        public static string ChooseProject(string lastProjectPath)
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "选择 TIA Portal V21 项目";
                dialog.Filter = "TIA Portal V21 项目 (*.ap21;*.als21)|*.ap21;*.als21|所有 TIA 项目 (*.ap*;*.als*)|*.ap*;*.als*|所有文件 (*.*)|*.*";
                dialog.CheckFileExists = true;
                dialog.Multiselect = false;

                if (!string.IsNullOrWhiteSpace(lastProjectPath))
                {
                    string directory = Path.GetDirectoryName(lastProjectPath);
                    if (Directory.Exists(directory)) dialog.InitialDirectory = directory;
                    if (File.Exists(lastProjectPath)) dialog.FileName = Path.GetFileName(lastProjectPath);
                }

                return dialog.ShowDialog() == DialogResult.OK ? dialog.FileName : "";
            }
        }

        public static string ChooseAction(string lastAction)
        {
            string defaultChoice = string.Equals(lastAction, "import", StringComparison.OrdinalIgnoreCase) ? "2" : "1";
            Console.WriteLine();
            Console.WriteLine("请选择操作:");
            Console.WriteLine("  1. 导出 PLC 与 HMI 源码给 Codex/AI 编辑");
            Console.WriteLine("  2. 把已修改的 PLC/HMI 源码导回 TIA Portal");
            Console.WriteLine("  3. 只查看项目中的 PLC");
            Console.WriteLine("  0. 取消");

            while (true)
            {
                Console.Write("输入编号，直接回车选择上次操作 [" + defaultChoice + "]: ");
                string input = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(input)) input = defaultChoice;
                if (input == "1") return "export";
                if (input == "2") return "import";
                if (input == "3") return "list-plcs";
                if (input == "0") return "";
                Console.WriteLine("请输入 0、1、2 或 3。");
            }
        }
    }
}
