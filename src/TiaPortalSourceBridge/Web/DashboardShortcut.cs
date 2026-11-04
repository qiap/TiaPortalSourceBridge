using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows.Forms;

namespace TiaPortalSourceBridge.Web
{
    internal static class DashboardShortcut
    {
        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        private class ShellLink { }

        // IShellLinkW vtable order is defined by shobjidl_core.h.
        [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ILink
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int size, IntPtr data, uint flags);
            void GetIDList(out IntPtr list);
            void SetIDList(IntPtr list);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int size);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string text);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int size);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int size);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
            void GetHotkey(out short key);
            void SetHotkey(short key);
            void GetShowCmd(out int cmd);
            void SetShowCmd(int cmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int size, out int index);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
            void Resolve(IntPtr owner, uint flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
        }

        internal static void Write(string path, string exe, string args, string dir)
        {
            var link = (ILink)new ShellLink();
            try {
                link.SetPath(exe);
                link.SetArguments(args);
                link.SetWorkingDirectory(dir);
                link.SetIconLocation(exe, 0);
                link.SetDescription("TIA Portal Source Bridge Dashboard");
                ((IPersistFile)link).Save(path, true);
            } finally { Marshal.FinalReleaseComObject(link); }
        }

        internal static string[] Read(string path)
        {
            var link = (ILink)new ShellLink();
            try {
                ((IPersistFile)link).Load(path, 0);
                var buffer = new StringBuilder(32768);
                link.GetPath(buffer, buffer.Capacity, IntPtr.Zero, 4);
                var exe = buffer.ToString(); buffer.Length = 0;
                link.GetArguments(buffer, buffer.Capacity);
                var args = buffer.ToString(); buffer.Length = 0;
                link.GetWorkingDirectory(buffer, buffer.Capacity);
                var dir = buffer.ToString(); buffer.Length = 0;
                int index; link.GetIconLocation(buffer, buffer.Capacity, out index);
                return new[] { exe, args, dir, buffer.ToString(), index.ToString() };
            } finally { Marshal.FinalReleaseComObject(link); }
        }

        internal static int Create(string config, bool folderOnly, bool notify)
        {
            string location = "";
            try {
                config = Path.GetFullPath(config);
                if (!File.Exists(config)) throw new FileNotFoundException("请先完整解压 ZIP。缺少配置文件", config);
                string root = Path.GetDirectoryName(config);
                string exe = Assembly.GetExecutingAssembly().Location;
                string args = "web --hide-console --config \"" + config + "\"";
                var folders = folderOnly ? new[] {root} : new[] {root, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)};
                foreach (string folder in folders) {
                    if (string.IsNullOrEmpty(folder)) throw new DirectoryNotFoundException("无法定位桌面目录。");
                    location = Path.Combine(folder, "TIA Portal Source Bridge.lnk");
                    Write(location, exe, args, root);
                    string[] saved = Read(location);
                    if (!string.Equals(saved[0], exe, StringComparison.OrdinalIgnoreCase) || saved[1] != args ||
                        !string.Equals(saved[3], exe, StringComparison.OrdinalIgnoreCase) || saved[4] != "0")
                        throw new IOException("快捷方式保存后与预期不符。");
                }
                string msg = "快捷方式已创建：" + Environment.NewLine + string.Join(Environment.NewLine, folders);
                Console.WriteLine(msg);
                if (notify) MessageBox.Show(msg, "TIA Portal Source Bridge");
                return 0;
            } catch (Exception ex) {
                string msg = location + Environment.NewLine + ex.Message;
                Console.Error.WriteLine(msg);
                if (notify) MessageBox.Show(msg, "创建快捷方式失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
    }
}
