using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

namespace TiaPortalSourceBridge.Simulation
{
    internal static class AdvancedRuntime
    {
        private static Assembly _assembly;
        private static Type _manager;
        private static string _loadedApiPath;

        internal static string LoadedApiPath
        {
            get
            {
                EnsureLoaded();
                return _loadedApiPath ?? "";
            }
        }

        private static Type Manager()
        {
            EnsureLoaded();
            return _manager;
        }

        private static void EnsureLoaded()
        {
            if (_manager != null) return;
            string dll = FindApi();
            if (string.IsNullOrWhiteSpace(dll))
                throw new InvalidOperationException("未找到 S7-PLCSIM V21 / PLCSIM Advanced Runtime API（Siemens.Simatic.Simulation.Runtime.Api.x64.dll）。");
            _loadedApiPath = dll;
            string dir = Path.GetDirectoryName(dll);
            AppDomain.CurrentDomain.AssemblyResolve += delegate(object s, ResolveEventArgs e)
            {
                string candidate = Path.Combine(dir, new AssemblyName(e.Name).Name + ".dll");
                return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
            };
            _assembly = Assembly.LoadFrom(dll);
            _manager = _assembly.GetType("Siemens.Simatic.Simulation.Runtime.SimulationRuntimeManager", true);
        }

        internal static string FindApi()
        {
            string env = Environment.GetEnvironmentVariable("PLCSIM_ADV_API_DLL");
            if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;

            // Prioritize V8.0 (TIA V21 / PLCSIM V21 / PLCSIM Advanced V8.0) and newer first
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86), @"Siemens\PLCSIMADV\API");
            foreach (string ver in new[] { "8.0", "7.0", "6.0", "5.0", "4.0", "3.0" })
            {
                string dll = Path.Combine(root, ver, "Siemens.Simatic.Simulation.Runtime.Api.x64.dll");
                if (File.Exists(dll)) return dll;
            }
            if (Directory.Exists(root))
            {
                string found = Directory.GetDirectories(root)
                    .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                    .Select(d => Path.Combine(d, "Siemens.Simatic.Simulation.Runtime.Api.x64.dll"))
                    .FirstOrDefault(File.Exists);
                if (!string.IsNullOrWhiteSpace(found)) return found;
            }

            // Fallback: S7-PLCSIM V21 embedded runtime API
            foreach (string progRoot in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"E:\botuv_21", @"D:\Program Files" })
            {
                if (string.IsNullOrWhiteSpace(progRoot)) continue;
                string plcsimV21Dll = Path.Combine(progRoot, @"Siemens\Automation\PLCSIM_V21\resources\bin\wwwroot\assets\lib\runtime\Siemens.Simatic.Simulation.Runtime.Api.x64.dll");
                if (File.Exists(plcsimV21Dll)) return plcsimV21Dll;
            }

            return "";
        }

        internal static bool IsRuntimeProcessRunning()
        {
            try
            {
                return Process.GetProcessesByName("Siemens.Simatic.Simulation.Runtime.Manager").Length > 0
                    || Process.GetProcessesByName("Siemens.Simatic.Simulation.Runtime.Manager.x64").Length > 0;
            }
            catch
            {
                return false;
            }
        }

        internal static string FindRuntimeManagerExe()
        {
            string common = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86),
                @"Siemens\PLCSIMADV\Siemens.Simatic.Simulation.Runtime.Manager.exe");
            if (File.Exists(common)) return common;
            if (!string.IsNullOrWhiteSpace(_loadedApiPath))
            {
                DirectoryInfo dir = Directory.GetParent(_loadedApiPath);
                while (dir != null)
                {
                    string candidate = Path.Combine(dir.FullName, "Siemens.Simatic.Simulation.Runtime.Manager.exe");
                    if (File.Exists(candidate)) return candidate;
                    dir = dir.Parent;
                }
            }
            return "";
        }

        internal static bool IsRuntimeManagerAvailable()
        {
            if (!IsRuntimeProcessRunning()) return false;
            try
            {
                PropertyInfo prop = Manager().GetProperty("IsRuntimeManagerAvailable", BindingFlags.Public | BindingFlags.Static);
                if (prop == null) return true;
                return Convert.ToBoolean(prop.GetValue(null, null));
            }
            catch
            {
                return false;
            }
        }

        internal static string ApiVersion()
        {
            EnsureLoaded();
            if (IsRuntimeProcessRunning())
            {
                try
                {
                    PropertyInfo prop = Manager().GetProperty("Version", BindingFlags.Public | BindingFlags.Static);
                    if (prop != null)
                    {
                        object raw = prop.GetValue(null, null);
                        if (raw != null)
                        {
                            string text = Convert.ToString(raw);
                            if (!string.IsNullOrWhiteSpace(text) && text != "0") return text;
                        }
                    }
                }
                catch
                {
                }
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(_loadedApiPath) && File.Exists(_loadedApiPath))
                {
                    string fileVer = FileVersionInfo.GetVersionInfo(_loadedApiPath).FileVersion;
                    if (!string.IsNullOrWhiteSpace(fileVer)) return fileVer;
                }
            }
            catch
            {
            }

            return _assembly != null && _assembly.GetName().Version != null
                ? _assembly.GetName().Version.ToString()
                : "";
        }

        internal static string[] SupportedCpuTypes()
        {
            EnsureLoaded();
            Type cpuType = _assembly.GetType("Siemens.Simatic.Simulation.Runtime.ECPUType", false);
            if (cpuType == null || !cpuType.IsEnum)
                return new[] { "CPU1500_Unspecified", "CPU1511", "CPU1513", "CPU1515", "CPU1516", "CPU1517", "CPU1518", "CPU1200G2_Unspecified" };
            return Enum.GetNames(cpuType);
        }

        internal static bool IsValidCpuOrArticle(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            string trimmed = value.Trim();
            if (IsArticleNumber(trimmed)) return true;
            return SupportedCpuTypes().Any(n => string.Equals(n, trimmed, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsArticleNumber(string value)
        {
            return value.StartsWith("6ES7", StringComparison.OrdinalIgnoreCase)
                || value.IndexOf('-') >= 0
                || value.IndexOf('/') >= 0;
        }

        private static object Get(object target, string name)
        {
            Type t = target as Type ?? target.GetType();
            try
            {
                PropertyInfo prop = t.GetProperty(name);
                if (prop != null) return prop.GetValue(target is Type ? null : target, null);
                FieldInfo field = t.GetField(name);
                return field == null ? null : field.GetValue(target is Type ? null : target);
            }
            catch (TargetInvocationException ex)
            {
                throw ex.InnerException ?? ex;
            }
        }

        private static void Set(object target, string name, object value)
        {
            PropertyInfo prop = target.GetType().GetProperty(name);
            if (prop == null) throw new MissingMemberException(target.GetType().FullName, name);
            prop.SetValue(target, prop.PropertyType.IsEnum ? Enum.Parse(prop.PropertyType, value.ToString(), true) : value, null);
        }

        private static object Call(object target, string method, params object[] args)
        {
            Type type = target as Type ?? target.GetType();
            MethodInfo m = type.GetMethods().FirstOrDefault(x => x.Name == method && x.GetParameters().Length == args.Length &&
                x.GetParameters().Select((p, i) => args[i] == null || p.ParameterType.IsInstanceOfType(args[i]) || p.ParameterType.IsEnum && args[i] is string).All(ok => ok));
            if (m == null) throw new MissingMethodException(type.FullName, method);
            object[] converted = m.GetParameters().Select((p, i) => p.ParameterType.IsEnum && args[i] is string ? Enum.Parse(p.ParameterType, (string)args[i], true) : args[i]).ToArray();
            try { return m.Invoke(target is Type ? null : target, converted); }
            catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
        }

        internal static Dictionary<string, object>[] Instances()
        {
            string ignored;
            return Instances(out ignored);
        }

        internal static Dictionary<string, object>[] Instances(out string runtimeNote)
        {
            runtimeNote = "";
            EnsureLoaded();
            if (!IsRuntimeProcessRunning())
            {
                runtimeNote = "Siemens.Simatic.Simulation.Runtime.Manager 当前未运行（0 个活动仿真实例；调用 tia_simulation_start 且 confirm=true 或打开 S7-PLCSIM V21 时按需启动）。";
                return new Dictionary<string, object>[0];
            }

            IEnumerable items;
            try
            {
                items = (IEnumerable)(Get(Manager(), "RegisteredInstanceInfo") ?? new object[0]);
            }
            catch (Exception ex)
            {
                runtimeNote = "PLCSIM Runtime Manager 当前未激活: " + ex.Message;
                return new Dictionary<string, object>[0];
            }

            var list = new List<Dictionary<string, object>>();
            foreach (object info in items)
            {
                string name = Convert.ToString(Get(info, "Name") ?? "");
                object instance = null; string state = "", cpu = "", ip = "";
                try
                {
                    instance = Call(Manager(), "CreateInterface", name);
                    state = Convert.ToString(Get(instance, "OperatingState") ?? "");
                    cpu = Convert.ToString(Get(instance, "CPUType") ?? "");
                    string[] ips = Call(Get(instance, "ControllerAreaNetwork"), "GetIPSuiteStrings") as string[];
                    ip = ips != null && ips.Length > 0 ? ips[0] : "";
                }
                catch (Exception ex) { state = "unavailable: " + ex.Message; }
                finally { IDisposable d = instance as IDisposable; if (d != null) d.Dispose(); }
                list.Add(new Dictionary<string, object> { { "id", Get(info, "ID") }, { "name", name }, { "operating_state", state }, { "cpu_type", cpu }, { "ip", ip } });
            }
            return list.ToArray();
        }

        internal static IDisposable Open(string name)
        {
            return (IDisposable)Call(Manager(), "CreateInterface", name);
        }

        internal static IDisposable Register(string name, string cpuOrArticle)
        {
            Type mgr = Manager();
            string spec = (cpuOrArticle ?? "").Trim();
            MethodInfo registerMethod;
            object firstArg;

            if (IsArticleNumber(spec))
            {
                // V8+ explicit overload: RegisterInstance(string in_ArticleNumber, string in_InstanceName)
                registerMethod = mgr.GetMethod("RegisterInstance", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), typeof(string) }, null);
                if (registerMethod == null)
                    throw new InvalidOperationException("当前加载的 PLCSIM Runtime API 不支持按订货号（ArticleNumber）注册实例: " + _loadedApiPath);
                firstArg = spec;
            }
            else
            {
                // Explicitly select RegisterInstance(ECPUType in_CPUType, string in_InstanceName) to prevent V8 overload collision with (string, string)!
                Type ecpuType = _assembly.GetType("Siemens.Simatic.Simulation.Runtime.ECPUType", true);
                object parsedCpu = Enum.Parse(ecpuType, spec, true);
                registerMethod = mgr.GetMethod("RegisterInstance", BindingFlags.Public | BindingFlags.Static, null, new[] { ecpuType, typeof(string) }, null);
                if (registerMethod == null)
                    throw new MissingMethodException(mgr.FullName, "RegisterInstance(ECPUType, String)");
                firstArg = parsedCpu;
            }

            try
            {
                return (IDisposable)registerMethod.Invoke(null, new[] { firstArg, name });
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                if (!IsRuntimeManagerAvailable())
                {
                    throw new InvalidOperationException("PLCSIM Runtime Manager 服务未就绪或启动失败（请确认已安装 S7-PLCSIM V21 或 PLCSIM Advanced V8.0）: " + inner.Message, inner);
                }
                throw inner;
            }
        }

        internal static void ConfigureIp(object instance, string ip, string mask, string gateway)
        {
            Set(instance, "IsSendSyncEventInDefaultModeEnabled", false);
            try { Set(Get(instance, "ControllerAreaNetwork"), "IPSuite4", new string[] { ip, mask, string.IsNullOrWhiteSpace(gateway) ? "0.0.0.0" : gateway }); }
            catch (Exception ex) { if (ex.GetType().Name != "SimulationRuntimeWarningException") throw; }
        }

        internal static void PowerOn(object instance, uint timeoutMs) { Call(instance, "PowerOn", timeoutMs); }
        internal static void Run(object instance, uint timeoutMs) { Call(instance, "Run", timeoutMs); }
        internal static void Stop(object instance, uint timeoutMs) { Call(instance, "Stop", timeoutMs); }
        internal static void PowerOff(object instance, uint timeoutMs) { Call(instance, "PowerOff", timeoutMs); }
        internal static void Unregister(object instance) { Call(instance, "UnregisterInstance"); }
        internal static string State(object instance) { return Convert.ToString(Get(instance, "OperatingState") ?? ""); }
    }
}
