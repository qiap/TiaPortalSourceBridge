using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using TiaPortalSourceBridge.Mcp;

internal static class ProjectOperationGuardChecks
{
    private static int checks;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
    private static void Reject(Action action, string message)
    { bool rejected = false; try { action(); } catch (InvalidOperationException) { rejected = true; } catch (IOException) { rejected = true; } Check(rejected, message); }
    private static Process Child(string project, string operation, string root, string mode)
    {
        var info = new ProcessStartInfo(typeof(ProjectOperationGuardChecks).Assembly.Location,
            "hold \"" + project + "\" " + operation + " \"" + root + "\" " + mode) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        return Process.Start(info);
    }
    private static void WaitReady(Process child, string root)
    {
        var clock = Stopwatch.StartNew();
        while (!File.Exists(Path.Combine(root, "ready")) && !child.HasExited && clock.ElapsedMilliseconds < 10000) Thread.Sleep(10);
        Check(File.Exists(Path.Combine(root, "ready")), "child acquired worker-owned guard");
    }
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "hold") {
            using (var lease = ProjectOperationGuard.Enter(args[1], "tia_import_blocks_batch", args[2], true)) {
                File.WriteAllText(Path.Combine(args[3], "ready"), "ready");
                while (!File.Exists(Path.Combine(args[3], "release"))) Thread.Sleep(10);
                if (args[4] == "crash") Environment.Exit(17);
                lease.Complete(false);
            }
            return 0;
        }
        string root = Path.Combine(Path.GetTempPath(), "tia-project-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string project = Path.Combine(root, "test.ap17"), operation = Guid.NewGuid().ToString("N");
        try {
            Check(!ProjectOperationGuard.Inspect(project).Busy && !ProjectOperationGuard.Inspect(project).Unresolved, "fresh project idle");
            using (var lease = ProjectOperationGuard.Enter(project, "tia_read_block", operation, false)) {
                var status = ProjectOperationGuard.Inspect(project);
                Check(status.Busy && !status.Unresolved && status.ActiveOperationIds.Contains(operation), "active read is busy without unresolved mutation");
                Reject(() => ProjectOperationGuard.Enter(project, "tia_import_block", operation, true), "concurrent writer rejected");
                lease.Complete(false);
            }
            Check(!ProjectOperationGuard.Inspect(project).Busy, "idle worker releases project guard");
            using (var child = Child(project, operation, root, "complete")) {
                WaitReady(child, root);
                child.StandardInput.Close(); // Simulates a parent dashboard closing its pipe.
                var status = ProjectOperationGuard.Inspect(project);
                Check(status.Busy && status.ActiveOperationIds.Contains(operation), "parent pipe loss does not release worker guard");
                Reject(() => ProjectOperationGuard.Enter(project, "tia_import_block", Guid.NewGuid().ToString("N"), true), "new owner cannot race old active worker");
                Reject(() => ProjectOperationGuard.ResolveStopped(project, operation), "live worker cannot be acknowledged");
                File.WriteAllText(Path.Combine(root, "release"), "release");
                Check(child.WaitForExit(10000) && child.ExitCode == 0, "late operation finishes without kill");
            }
            Check(!ProjectOperationGuard.Inspect(project).Busy && !ProjectOperationGuard.Inspect(project).Unresolved, "known terminal receipt is not busy or unknown");
            File.Delete(Path.Combine(root, "ready")); File.Delete(Path.Combine(root, "release"));
            using (var child = Child(project, operation, root, "crash")) {
                WaitReady(child, root); File.WriteAllText(Path.Combine(root, "release"), "release");
                Check(child.WaitForExit(10000) && child.ExitCode == 17, "test worker exits without terminal receipt");
            }
            var interrupted = ProjectOperationGuard.Inspect(project);
            Check(!interrupted.Busy && interrupted.Unresolved && interrupted.OperationIds.SequenceEqual(new[] { operation }), "dead worker is unknown, not busy or success");
            Reject(() => ProjectOperationGuard.Enter(project, "tia_save_project", operation, true), "unresolved write prevents replay");
            using (var read = ProjectOperationGuard.Enter(project, "tia_read_block", Guid.NewGuid().ToString("N"), false)) read.Complete(false);
            Check(ProjectOperationGuard.Inspect(project).Unresolved, "read-only reconciliation does not erase unknown outcome");
            ProjectOperationGuard.ResolveStopped(project, Guid.NewGuid().ToString("N"));
            Check(ProjectOperationGuard.Inspect(project).Unresolved, "another operation cannot resolve original receipt");
            ProjectOperationGuard.ResolveStopped(project, operation);
            Check(!ProjectOperationGuard.Inspect(project).Unresolved, "only exact stopped operation can be resolved");
            using (var lease = ProjectOperationGuard.Enter(project, "tia_save_project", Guid.NewGuid().ToString("N"), true)) lease.Complete(false);
            Check(!ProjectOperationGuard.Inspect(project).Busy, "explicit reconciled continuation can execute");
            string broken = Path.Combine(ProjectOperationGuard.StorageDirectory(project), Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(broken, "{}");
            Check(ProjectOperationGuard.Inspect(project).Unavailable, "malformed evidence is unavailable, not idle");
            Reject(() => ProjectOperationGuard.Enter(project, "tia_save_project", operation, true), "corrupt evidence blocks mutation");
            File.Delete(broken);
            Check(ProjectOperationGuard.IsMutation("tia_compile_plc", new System.Collections.Generic.Dictionary<string, object>()), "compilation is guarded as a mutation");
            Check(ProjectOperationGuard.IsMutation("tia_import_block", new System.Collections.Generic.Dictionary<string, object> { { "dry_run", true } }), "unsupported dry_run cannot bypass mutation guard");
            Console.WriteLine("PASS: " + checks + " project operation guard checks; no TIA or PLC connection.");
            return 0;
        } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally {
            string activity = ProjectOperationGuard.StorageDirectory(project);
            if (activity.StartsWith(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TiaPortalSourceBridge", "project-activity") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.Exists(activity)) Directory.Delete(activity, true);
            if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true);
        }
    }
}
