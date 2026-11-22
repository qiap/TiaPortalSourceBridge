using System;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using TiaPortalSourceBridge.Configuration;
using TiaPortalSourceBridge.Openness;

internal static class OpennessSdkChecks
{
    private static int checks;
    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
        checks++;
    }

    private static void Reject<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { checks++; return; }
        throw new InvalidOperationException(message);
    }

    private static string MakeSdk(string root, int major)
    {
        string version = "V" + major;
        string path = OpennessSdk.PrimaryIn(Path.Combine(root, "Portal " + version), version);
        string dir = Path.GetDirectoryName(path);
        Directory.CreateDirectory(dir);
        string name = Path.GetFileNameWithoutExtension(path);
        var asm = AppDomain.CurrentDomain.DefineDynamicAssembly(
            new AssemblyName(name) { Version = new Version(major, 0, 0, 0) }, AssemblyBuilderAccess.Save, dir);
        asm.DefineDynamicModule(name, name + ".dll");
        asm.Save(name + ".dll");
        return path;
    }

    public static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "inspect")
        {
            string version = args[1];
            string path = OpennessSdk.FindInstalled(version);
            if (path == null) return 0;
            var api = new ReflectionApi(new AppConfig { TiaPortalVersion = version, SiemensEngineeringDll = path });
            Check(api.LoadedPortalVersion == version, "requested native runtime major");
            Check(api.RequireType("Siemens.Engineering.TiaPortal").GetMethod("GetProcesses") != null, "GetProcesses");
            Check(api.RequireType("Siemens.Engineering.SW.PlcSoftware").GetProperty("BlockGroup") != null, "PLC block group");
            if (File.Exists(Path.Combine(Path.GetDirectoryName(path), version == "V21" ? "Siemens.Engineering.WinCC.dll" : "Siemens.Engineering.Hmi.dll")))
                Check(api.RequireType("Siemens.Engineering.Hmi.HmiTarget") != null, "classic HMI module");
            if (version == "V17")
            {
                string other = OpennessSdk.FindInstalled("V18");
                if (other != null)
                    Reject<InvalidOperationException>(() => new ReflectionApi(new AppConfig
                        { TiaPortalVersion = "V18", SiemensEngineeringDll = other }), "in-process API mixing");
            }
            Console.WriteLine("PASS: " + version + " loaded in an isolated process; " + checks + " API contract checks.");
            return 0;
        }
        string root = args[0];
        for (int major = 16; major <= 21; major++)
        {
            string v = "V" + major;
            Check(PortalVersions.Normalize(major + ".0") == v, v + " config value");
            Check(PortalVersions.FromProjectPath("station.AP" + major) == v, v + " project");
            Check(PortalVersions.FromProjectPath("station.ALS" + major) == v, v + " multiuser session");
            Check(PortalVersions.IsClassic(v) == (major < 21), v + " engine family");
            string sdk = MakeSdk(root, major);
            Check(OpennessSdk.MatchesPrimary(sdk, v), v + " exact manifest");
            Check(OpennessSdk.Resolve(v, sdk, major == 21) == sdk, v + " pinned SDK");
            Check(OpennessSdk.Resolve(v, Path.GetDirectoryName(sdk), major == 21) == sdk, v + " pinned folder");
            Reject<NotSupportedException>(() => OpennessSdk.Resolve(v, sdk, major != 21), "wrong engine " + v);
            string installed = OpennessSdk.FindInstalled(v);
            if (installed == null) Console.WriteLine(v + " native SDK: not installed");
            else
            {
                Check(OpennessSdk.MatchesPrimary(installed, v), v + " installed DLL");
                var native = Assembly.ReflectionOnlyLoadFrom(installed);
                var portal = native.GetType("Siemens.Engineering.TiaPortal", true);
                Check(portal.GetMethod("GetProcesses", BindingFlags.Static | BindingFlags.Public) != null, v + " process discovery contract");
                Console.WriteLine(v + " native SDK: " + installed);
            }
        }

        var discovery = new AppConfig { TiaPortalVersion = "17.0", SiemensEngineeringDll = "explicit-v17.dll" };
        discovery.SelectPortalVersion("V17");
        Check(discovery.SiemensEngineeringDll == "explicit-v17.dll", "same-major discovery keeps its configured DLL");
        discovery.SelectPortalVersion("V18");
        Check(discovery.TiaPortalVersion == "V18" && discovery.SiemensEngineeringDll == "", "V18 discovery cannot inherit a V17 SDK pin");
        string example = OpennessSdk.PrimaryIn(Path.Combine(root, "Portal V17"), "V17");
        string compatibilityPath = OpennessSdk.PrimaryIn(Path.Combine(root, "Portal V19"), "V17");
        Directory.CreateDirectory(Path.GetDirectoryName(compatibilityPath));
        File.Copy(example, compatibilityPath);
        Check(!OpennessSdk.MatchesPrimary(compatibilityPath, "V17"), "newer host compatibility SDK");
        var misleading = Path.Combine(root, "V19", "Siemens.Engineering.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(misleading));
        File.Copy(example, misleading);
        Reject<FileNotFoundException>(() => OpennessSdk.Resolve("V19", misleading, false), "misleading folder");
        Reject<FileNotFoundException>(() => OpennessSdk.Resolve("V17", root, false), "missing configured SDK");
        File.WriteAllText(misleading, "");
        Check(!OpennessSdk.MatchesPrimary(misleading, "V19"), "unreadable managed manifest");
        string net8 = Path.Combine(root, "net8.0-windows", "Siemens.Engineering.Base.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(net8));
        File.Copy(OpennessSdk.PrimaryIn(Path.Combine(root, "Portal V21"), "V21"), net8);
        Check(!OpennessSdk.MatchesPrimary(net8, "V21"), "incompatible V21 framework");
        Check(OpennessSdk.MatchesProcess("project.ap18", @"C:\Portal V18\Bin\Portal.exe", "V18"), "runtime match");
        Check(!OpennessSdk.MatchesProcess("project.ap18", @"C:\Portal V19\Bin\Portal.exe", "V18"), "runtime mismatch");
        Check(!OpennessSdk.MatchesProcess("project.ap17", null, "V18"), "project mismatch");
        Check(!OpennessSdk.MatchesProcess(null, null, "V18"), "unknown runtime identity");
        Check(PortalVersions.FromProjectPath("not-a-project.zap18") == null, "archive is not a project");
        Check(PortalVersions.FromProjectPath("wrong.ap22") == null, "unsupported major");
        var config = new AppConfig { TiaPortalVersion = "V17", SiemensEngineeringDll = example };
        config.OverrideProject(Path.Combine(root, "station.ap17"), true);
        Check(config.SiemensEngineeringDll == example, "same major retains config pin");
        config.OverrideProject(Path.Combine(root, "station.ap19"), true);
        Check(config.TiaPortalVersion == "V19" && config.SiemensEngineeringDll == "", "switch to independent SDK");
        Reject<ArgumentException>(() => config.OverrideProject(Path.Combine(root, "arch.zap19"), true), "archive rejection");
        Check(config.TiaPortalVersion == "V19", "bad override leaves previous config intact");
        config.AllowProjectUpgrade = true;
        config.OverrideProject(Path.Combine(root, "older.ap15_1"), true);
        Check(config.ProjectFileVersion == "V15.1" && config.TiaPortalVersion == "V19", "explicit upgrade keeps selected API");
        config.TiaPortalVersion = "V22";
        Reject<InvalidOperationException>(() => config.NormalizeAndValidate(Path.Combine(root, "config.json")), "future config major");
        Console.WriteLine("PASS: " + checks + " Openness SDK selection and identity checks.");
        return 0;
    }
}
