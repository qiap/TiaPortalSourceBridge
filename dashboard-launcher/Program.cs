using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

internal static class DashboardLauncher
{
    // Compiled as a Windows GUI application, so Explorer never allocates a terminal.
    [STAThread]
    private static int Main()
    {
        try
        {
            string exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TiaPortalSourceBridge.exe");
            // Forward the original argument tail so Windows quoting and Unicode paths survive.
            string line = Environment.CommandLine.TrimStart();
            int end = line[0] == '"' ? line.IndexOf('"', 1) + 1 : line.IndexOfAny(new[] { ' ', '\t' });
            string options = end < 0 ? "" : line.Substring(end).TrimStart();
            using (Process.Start(new ProcessStartInfo(exe, "web --hide-console " + options)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Environment.CurrentDirectory
            })) { }
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "TIA Portal Source Bridge", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
