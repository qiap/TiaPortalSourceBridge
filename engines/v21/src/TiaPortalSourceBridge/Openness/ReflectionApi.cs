using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Win32;
using TiaPortalSourceBridge.Configuration;

namespace TiaPortalSourceBridge.Openness
{
    internal sealed class ReflectionApi
    {
        private static readonly object SyncRoot = new object();
        private static readonly List<string> SharedSearchDirectories = new List<string>();
        private static readonly ConcurrentDictionary<string, string> ResolvedDllCache = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // A lookup result must be non-null even when the target property does not exist:
        // ConcurrentDictionary does not permit null values. Cache misses explicitly.
        private static readonly ConcurrentDictionary<PropertyKey, PropertyLookup> PropertyCache =
            new ConcurrentDictionary<PropertyKey, PropertyLookup>();

        private struct PropertyKey : IEquatable<PropertyKey>
        {
            internal readonly Type Type;
            internal readonly string Name;
            internal PropertyKey(Type type, string name) { Type = type; Name = name; }
            public bool Equals(PropertyKey other) { return Type == other.Type && string.Equals(Name, other.Name, StringComparison.Ordinal); }
            public override bool Equals(object obj) { return obj is PropertyKey && Equals((PropertyKey)obj); }
            public override int GetHashCode() { unchecked { return (Type.GetHashCode() * 397) ^ StringComparer.Ordinal.GetHashCode(Name); } }
        }

        private sealed class PropertyLookup
        {
            internal readonly PropertyInfo Property;
            internal PropertyLookup(PropertyInfo property) { Property = property; }
        }

        private struct GenericKey : IEquatable<GenericKey>
        {
            internal readonly Type TargetType;
            internal readonly string MethodName;
            internal readonly Type GenericType;
            internal GenericKey(Type targetType, string methodName, Type genericType)
            {
                TargetType = targetType;
                MethodName = methodName;
                GenericType = genericType;
            }
            public bool Equals(GenericKey other)
            {
                return TargetType == other.TargetType
                    && GenericType == other.GenericType
                    && string.Equals(MethodName, other.MethodName, StringComparison.Ordinal);
            }
            public override bool Equals(object obj) { return obj is GenericKey && Equals((GenericKey)obj); }
            public override int GetHashCode()
            {
                unchecked
                {
                    int h = TargetType.GetHashCode();
                    h = (h * 397) ^ StringComparer.Ordinal.GetHashCode(MethodName);
                    h = (h * 397) ^ GenericType.GetHashCode();
                    return h;
                }
            }
        }

        private static readonly ConcurrentDictionary<string, MethodInfo> MethodCache = new ConcurrentDictionary<string, MethodInfo>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<GenericKey, GenericCall> GenericCache = new ConcurrentDictionary<GenericKey, GenericCall>();
        private sealed class GenericCall { internal MethodInfo Method; internal bool Extension; }

        private readonly List<Assembly> _engineeringAssemblies = new List<Assembly>();
        private readonly Dictionary<string, List<MethodInfo>> _genericExtensionCache = new Dictionary<string, List<MethodInfo>>();
        private readonly ConcurrentDictionary<string, Type> _typeCache = new ConcurrentDictionary<string, Type>(StringComparer.Ordinal);
        private Type[] _cachedAvailableTypes;
        public string LoadedPortalVersion { get; private set; }

        static ReflectionApi()
        {
            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
        }

        private static Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            string simpleName;
            try
            {
                simpleName = new AssemblyName(args.Name).Name;
            }
            catch
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(simpleName) || simpleName.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            Assembly alreadyLoaded = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase));
            if (alreadyLoaded != null) return alreadyLoaded;

            string fileName = simpleName + ".dll";
            string[] directories;
            lock (SyncRoot)
            {
                directories = SharedSearchDirectories.ToArray();
            }

            foreach (string directory in directories)
            {
                string dependency = Path.Combine(directory, fileName);
                if (File.Exists(dependency))
                {
                    return Assembly.LoadFrom(dependency);
                }
            }
            return null;
        }

        public ReflectionApi(AppConfig config)
        {
            string dllPath = ResolveEngineeringApi(config);
            string dllDirectory = Path.GetDirectoryName(dllPath);
            AddSearchDirectory(dllDirectory);
            AddPortalSearchDirectories(dllPath);

            LoadEngineeringAssemblies(dllPath);
            LoadedPortalVersion = InferApiVersion(dllPath);
            config.TiaPortalVersion = LoadedPortalVersion;
            Console.WriteLine("Siemens API: " + dllDirectory);
            Console.WriteLine("使用 TIA Portal: " + LoadedPortalVersion);
        }

        public Type RequireType(string fullName)
        {
            Type cached;
            if (_typeCache.TryGetValue(fullName, out cached)) return cached;

            Type type = _engineeringAssemblies
                .Select(a => a.GetType(fullName, false))
                .FirstOrDefault(t => t != null);
            if (type == null)
            {
                throw new InvalidOperationException("当前 Siemens Openness API 中找不到类型: " + fullName);
            }
            _typeCache[fullName] = type;
            return type;
        }

        public Type FindTypeBySimpleName(params string[] names)
        {
            foreach (string name in names)
            {
                Type cached;
                if (_typeCache.TryGetValue("simple:" + name, out cached)) return cached;
            }

            Type[] types = AvailableTypes();

            foreach (string name in names)
            {
                Type type = types.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));
                if (type != null)
                {
                    _typeCache["simple:" + name] = type;
                    return type;
                }
            }
            throw new InvalidOperationException("当前 Siemens API 中找不到类型: " + string.Join(" / ", names));
        }

        public Type TryRequireType(string fullName)
        {
            Type cached;
            if (_typeCache.TryGetValue(fullName, out cached)) return cached;
            Type type = _engineeringAssemblies
                .Select(a => a.GetType(fullName, false))
                .FirstOrDefault(t => t != null);
            if (type != null) _typeCache[fullName] = type;
            return type;
        }

        public object GetService(object target, string serviceTypeFullName)
        {
            if (target == null) return null;
            Type serviceType = TryRequireType(serviceTypeFullName);
            if (serviceType == null) return null;
            try
            {
                return InvokeGenericNoArguments(target, "GetService", serviceType);
            }
            catch (MissingMethodException)
            {
                return null;
            }
        }

        public object CombineEnumFlags(string fullTypeName, IEnumerable<string> flagNames)
        {
            Type enumType = RequireType(fullTypeName);
            long combined = 0;
            if (flagNames != null)
            {
                foreach (string raw in flagNames)
                {
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    object val = Enum.Parse(enumType, raw.Trim(), true);
                    combined |= Convert.ToInt64(val);
                }
            }
            return Enum.ToObject(enumType, combined);
        }

        public object EnumValue(string fullTypeName, string value)
        {
            return Enum.Parse(RequireType(fullTypeName), value, true);
        }

        public object EnumValueBySimpleName(string value, params string[] typeNames)
        {
            return Enum.Parse(FindTypeBySimpleName(typeNames), value, true);
        }

        public object Get(object target, string propertyName, bool required)
        {
            if (target == null)
            {
                if (required) throw new ArgumentNullException("target");
                return null;
            }

            Type targetType = target.GetType();
            var key = new PropertyKey(targetType, propertyName);
            PropertyInfo property = PropertyCache.GetOrAdd(key,
                k => new PropertyLookup(k.Type.GetProperty(k.Name, BindingFlags.Instance | BindingFlags.Public))).Property;
            if (property == null)
            {
                if (required)
                {
                    throw new MissingMemberException(targetType.FullName, propertyName);
                }
                return null;
            }
            OperationMetrics.Count("property_reads");
            return property.GetValue(target, null);
        }

        public string GetString(object target, string propertyName)
        {
            object value = Get(target, propertyName, false);
            return value == null ? "" : Convert.ToString(value);
        }

        public IEnumerable<object> Items(object composition)
        {
            IEnumerable enumerable = composition as IEnumerable;
            if (enumerable == null)
            {
                yield break;
            }
            foreach (object item in enumerable)
            {
                OperationMetrics.Count("enumerated_items");
                yield return item;
            }
        }

        public object Invoke(object target, string methodName, params object[] arguments)
        {
            if (target == null) throw new ArgumentNullException("target");
            MethodInfo method = FindBestMethod(target.GetType(), methodName, arguments, false);
            return InvokeMethod(target, method, arguments);
        }

        public object InvokeStatic(Type targetType, string methodName, params object[] arguments)
        {
            MethodInfo method = FindBestMethod(targetType, methodName, arguments, true);
            return InvokeMethod(null, method, arguments);
        }

        public object InvokeGenericNoArguments(object target, string methodName, Type genericType)
        {
            var key = new GenericKey(target.GetType(), methodName, genericType);
            GenericCall cached;
            if (GenericCache.TryGetValue(key, out cached))
            {
                OperationMetrics.Count("generic_metadata_cache_hits");
                return InvokeMethod(cached.Extension ? null : target, cached.Method, cached.Extension ? new[] { target } : new object[0]);
            }
            MethodInfo method = target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(m => (m.Name == methodName || m.Name.EndsWith("." + methodName, StringComparison.Ordinal))
                    && m.IsGenericMethodDefinition
                    && m.GetGenericArguments().Length == 1
                    && m.GetParameters().Length == 0);
            if (method != null)
            {
                var call = new GenericCall { Method = method.MakeGenericMethod(genericType) }; GenericCache[key] = call;
                return InvokeMethod(target, call.Method, new object[0]);
            }

            foreach (Type interfaceType in target.GetType().GetInterfaces())
            {
                method = interfaceType.GetMethods().FirstOrDefault(m => m.Name == methodName
                    && m.IsGenericMethodDefinition
                    && m.GetGenericArguments().Length == 1
                    && m.GetParameters().Length == 0);
                if (method != null)
                {
                    var call = new GenericCall { Method = method.MakeGenericMethod(genericType) }; GenericCache[key] = call;
                    return InvokeMethod(target, call.Method, new object[0]);
                }
            }

            List<MethodInfo> extensionMethods;
            if (!_genericExtensionCache.TryGetValue(methodName, out extensionMethods))
            {
                extensionMethods = AvailableTypes()
                    .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public))
                    .Where(m => m.Name == methodName
                        && m.IsGenericMethodDefinition
                        && m.GetGenericArguments().Length == 1
                        && m.GetParameters().Length == 1)
                    .ToList();
                _genericExtensionCache[methodName] = extensionMethods;
            }

            method = extensionMethods.FirstOrDefault(m => m.GetParameters()[0].ParameterType.IsAssignableFrom(target.GetType()));
            if (method != null)
            {
                var call = new GenericCall { Method = method.MakeGenericMethod(genericType), Extension = true }; GenericCache[key] = call;
                return InvokeMethod(null, call.Method, new object[] { target });
            }

            throw new MissingMethodException(target.GetType().FullName, methodName + "<T>()");
        }

        public Array OneItemInterfaceArray(string interfaceFullName, object item)
        {
            Type interfaceType = RequireType(interfaceFullName);
            Array array = Array.CreateInstance(interfaceType, 1);
            array.SetValue(item, 0);
            return array;
        }

        public Array InterfaceArray(string interfaceFullName, IList<object> items)
        {
            Type interfaceType = RequireType(interfaceFullName);
            Array array = Array.CreateInstance(interfaceType, items.Count);
            for (int i = 0; i < items.Count; i++)
            {
                array.SetValue(items[i], i);
            }
            return array;
        }

        private static MethodInfo FindBestMethod(Type type, string name, object[] args, bool isStatic)
        {
            string signatureKey = type.FullName + "|" + (isStatic ? "S|" : "I|") + name + "|"
                + string.Join(",", args.Select(a => a == null ? "null" : a.GetType().FullName));
            MethodInfo cachedMethod;
            if (MethodCache.TryGetValue(signatureKey, out cachedMethod))
            {
                return cachedMethod;
            }

            IEnumerable<MethodInfo> candidates = type.GetMethods(BindingFlags.Public | (isStatic ? BindingFlags.Static : BindingFlags.Instance))
                .Where(m => m.Name == name && !m.IsGenericMethodDefinition && m.GetParameters().Length == args.Length);

            MethodInfo best = null;
            int bestScore = -1;
            foreach (MethodInfo candidate in candidates)
            {
                ParameterInfo[] parameters = candidate.GetParameters();
                int score = 0;
                bool compatible = true;
                for (int i = 0; i < parameters.Length; i++)
                {
                    if (args[i] == null)
                    {
                        compatible = !parameters[i].ParameterType.IsValueType || Nullable.GetUnderlyingType(parameters[i].ParameterType) != null;
                        continue;
                    }

                    Type argumentType = args[i].GetType();
                    if (parameters[i].ParameterType == argumentType) score += 4;
                    else if (parameters[i].ParameterType.IsAssignableFrom(argumentType)) score += 2;
                    else compatible = false;
                }

                if (compatible && score > bestScore)
                {
                    best = candidate;
                    bestScore = score;
                }
            }

            if (best == null)
            {
                throw new MissingMethodException(type.FullName, name + "(" + args.Length + " arguments)");
            }
            MethodCache[signatureKey] = best;
            return best;
        }

        private static object InvokeMethod(object target, MethodInfo method, object[] arguments)
        {
            OperationMetrics.Count("method_calls");
            try
            {
                return method.Invoke(target, arguments);
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                throw new InvalidOperationException(inner.Message, inner);
            }
        }

        private static void AddSearchDirectory(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
            lock (SyncRoot)
            {
                if (!SharedSearchDirectories.Contains(directory, StringComparer.OrdinalIgnoreCase))
                {
                    SharedSearchDirectories.Add(directory);
                }
            }
        }

        private static void AddPortalSearchDirectories(string apiDllPath)
        {
            DirectoryInfo directory = new DirectoryInfo(Path.GetDirectoryName(apiDllPath));
            for (int i = 0; i < 6 && directory != null; i++, directory = directory.Parent)
            {
                if (directory.Name.StartsWith("Portal V", StringComparison.OrdinalIgnoreCase)
                    || directory.Name.StartsWith("Portal_V", StringComparison.OrdinalIgnoreCase))
                {
                    AddSearchDirectory(Path.Combine(directory.FullName, "Bin"));
                    AddSearchDirectory(Path.Combine(directory.FullName, "Bin", "PublicAPI"));
                    break;
                }
            }
        }

        private void LoadEngineeringAssemblies(string primaryDllPath)
        {
            string directory = Path.GetDirectoryName(primaryDllPath);
            string primaryName = Path.GetFileName(primaryDllPath);
            List<string> files = new List<string> { primaryDllPath };

            string legacyHmi = Path.Combine(directory, "Siemens.Engineering.Hmi.dll");
            if (File.Exists(legacyHmi)) files.Add(legacyHmi);

            if (string.Equals(primaryName, "Siemens.Engineering.Base.dll", StringComparison.OrdinalIgnoreCase))
            {
                files.AddRange(Directory.GetFiles(directory, "Siemens.Engineering.*.dll")
                    .Where(path => path.IndexOf(".AddIn.", StringComparison.OrdinalIgnoreCase) < 0)
                    .OrderBy(path => ModuleLoadOrder(Path.GetFileName(path))));
            }

            foreach (string file in files.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    Assembly assembly = Assembly.LoadFrom(file);
                    if (!_engineeringAssemblies.Any(a => string.Equals(a.FullName, assembly.FullName, StringComparison.OrdinalIgnoreCase)))
                    {
                        _engineeringAssemblies.Add(assembly);
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("[Siemens API] 跳过无法加载的模块 " + Path.GetFileName(file) + ": " + ex.Message);
                }
            }

            if (_engineeringAssemblies.Count == 0)
            {
                throw new InvalidOperationException("未能加载 Siemens Openness API 程序集: " + primaryDllPath);
            }
        }

        private static int ModuleLoadOrder(string fileName)
        {
            if (string.Equals(fileName, "Siemens.Engineering.Base.dll", StringComparison.OrdinalIgnoreCase)) return 0;
            if (string.Equals(fileName, "Siemens.Engineering.Step7.dll", StringComparison.OrdinalIgnoreCase)) return 1;
            if (string.Equals(fileName, "Siemens.Engineering.WinCC.dll", StringComparison.OrdinalIgnoreCase)) return 2;
            if (string.Equals(fileName, "Siemens.Engineering.WinCCUnified.dll", StringComparison.OrdinalIgnoreCase)) return 3;
            return 10;
        }

        private static string ResolveEngineeringApi(AppConfig config)
        {
            string requestedVersion = NormalizeVersionTag(config.TiaPortalVersion);
            string cacheKey = (config.SiemensEngineeringDll ?? "") + "|"
                + (Environment.GetEnvironmentVariable("SIEMENS_ENGINEERING_DLL") ?? "") + "|"
                + requestedVersion + "|" + config.AllowProjectUpgrade + "|" + (!string.IsNullOrWhiteSpace(config.ProjectPath));
            string cachedPath;
            if (ResolvedDllCache.TryGetValue(cacheKey, out cachedPath) && File.Exists(cachedPath))
            {
                return cachedPath;
            }

            List<string> explicitCandidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(config.SiemensEngineeringDll))
            {
                AddConfiguredCandidate(explicitCandidates, config.SiemensEngineeringDll);
            }

            string environmentPath = Environment.GetEnvironmentVariable("SIEMENS_ENGINEERING_DLL");
            if (!string.IsNullOrWhiteSpace(environmentPath))
            {
                AddConfiguredCandidate(explicitCandidates, environmentPath);
            }

            string explicitFound = explicitCandidates.FirstOrDefault(File.Exists);
            if (explicitFound != null)
            {
                string fullExplicit = Path.GetFullPath(explicitFound);
                ResolvedDllCache[cacheKey] = fullExplicit;
                return fullExplicit;
            }

            List<string> allDiscovered = DiscoverAllPortalApiCandidates(requestedVersion);

            // 1. 优先精确匹配目标版本（如 V17 或 V21）
            string exactMatch = allDiscovered.FirstOrDefault(path =>
                File.Exists(path) && VersionsMatch(InferApiVersion(path), requestedVersion));
            if (exactMatch != null)
            {
                string fullExact = Path.GetFullPath(exactMatch);
                ResolvedDllCache[cacheKey] = fullExact;
                return fullExact;
            }

            // 2. 若不允许升级且已指定具体工程，严禁跨版本静默加载其他版本的 DLL
            if (!config.AllowProjectUpgrade && !string.IsNullOrWhiteSpace(config.ProjectPath))
            {
                throw new FileNotFoundException(
                    "找不到与目标版本 " + requestedVersion + " 匹配的 Siemens Openness API。"
                    + "请确认本机已安装 TIA Portal " + requestedVersion + " Openness，或在配置文件的 siemensEngineeringDll 中指定对应版本的 DLL 路径。");
            }

            // 3. 允许升级或仅做进程探测时，选择版本最高且存在的 API
            string fallback = allDiscovered
                .Where(File.Exists)
                .OrderByDescending(path => ParseVersion(InferApiVersion(path)))
                .FirstOrDefault();

            if (fallback != null)
            {
                string fullFallback = Path.GetFullPath(fallback);
                ResolvedDllCache[cacheKey] = fullFallback;
                return fullFallback;
            }

            throw new FileNotFoundException(
                "找不到 Siemens Openness API。V20 及更早版本请填写 Siemens.Engineering.dll；V21 可填写 PublicAPI\\V21\\net48 目录或 Siemens.Engineering.Base.dll。尝试过: "
                + string.Join("; ", explicitCandidates.Concat(allDiscovered).Distinct(StringComparer.OrdinalIgnoreCase)));
        }

        private static List<string> DiscoverAllPortalApiCandidates(string requestedVersion)
        {
            List<string> candidates = new List<string>();
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

            foreach (string root in new[] { programFiles, programFilesX86 })
            {
                if (string.IsNullOrWhiteSpace(root)) continue;
                AddPortalFolderCandidates(candidates, Path.Combine(root, "Siemens", "Automation", "Portal " + requestedVersion), requestedVersion);
                AddPortalFolderCandidates(candidates, Path.Combine(root, "Siemens", "Automation", "Portal " + requestedVersion.Replace('.', '_')), requestedVersion);
            }

            // 从正在运行的 Siemens.Automation.Portal 进程发现非默认盘安装位置（优先匹配 requestedVersion）
            List<Tuple<string, string>> runningPortals = new List<Tuple<string, string>>();
            foreach (Process process in Process.GetProcessesByName("Siemens.Automation.Portal"))
            {
                try
                {
                    string executable = process.MainModule == null ? "" : process.MainModule.FileName;
                    string binDirectory = Path.GetDirectoryName(executable);
                    DirectoryInfo portalDirectory = string.IsNullOrWhiteSpace(binDirectory) ? null : Directory.GetParent(binDirectory);
                    if (portalDirectory != null)
                    {
                        string version = VersionFromPortalDirectory(portalDirectory.Name);
                        runningPortals.Add(Tuple.Create(portalDirectory.FullName, version));
                    }
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }

            foreach (Tuple<string, string> item in runningPortals.OrderByDescending(p => VersionsMatch(p.Item2, requestedVersion)))
            {
                AddPortalFolderCandidates(candidates, item.Item1, item.Item2);
            }

            // 从 Windows 注册表查找西门子安装目录（支持 D:\ / E:\ 自定义安装路径）
            DiscoverFromRegistry(candidates, requestedVersion);

            // 扫描各磁盘常见目录（如 C:\Siemens\Automation, D:\Siemens\Automation, E:\plc\TIA*）
            foreach (string root in new[] { programFiles, programFilesX86 })
            {
                if (string.IsNullOrWhiteSpace(root)) continue;
                ScanAutomationRoot(candidates, Path.Combine(root, "Siemens", "Automation"));
            }

            try
            {
                foreach (DriveInfo drive in DriveInfo.GetDrives())
                {
                    if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                    string root = drive.RootDirectory.FullName;
                    ScanAutomationRoot(candidates, Path.Combine(root, "Siemens", "Automation"));
                    ScanAutomationRoot(candidates, Path.Combine(root, "TIA"));
                    ScanAutomationRoot(candidates, Path.Combine(root, "plc"));
                    ScanAutomationRoot(candidates, Path.Combine(root, "botuv_21"));
                }
            }
            catch
            {
            }

            return candidates;
        }

        private static void DiscoverFromRegistry(List<string> candidates, string requestedVersion)
        {
            string[] opennessRoots = new[]
            {
                @"SOFTWARE\Siemens\Automation\Openness",
                @"SOFTWARE\WOW6432Node\Siemens\Automation\Openness"
            };
            foreach (string opennessRoot in opennessRoots)
            {
                try
                {
                    using (RegistryKey baseKey = Registry.LocalMachine.OpenSubKey(opennessRoot))
                    {
                        if (baseKey == null) continue;
                        foreach (string verSub in baseKey.GetSubKeyNames().OrderByDescending(n => n, StringComparer.OrdinalIgnoreCase))
                        {
                            using (RegistryKey pubKey = baseKey.OpenSubKey(verSub + @"\PublicAPI"))
                            {
                                if (pubKey == null) continue;
                                foreach (string apiVerSub in pubKey.GetSubKeyNames().OrderByDescending(n => n, StringComparer.OrdinalIgnoreCase))
                                using (RegistryKey leaf = pubKey.OpenSubKey(apiVerSub))
                                {
                                    if (leaf == null) continue;
                                    string dllVal = Convert.ToString(leaf.GetValue("Siemens.Engineering") ?? "").Trim().Trim('"');
                                    if (string.IsNullOrWhiteSpace(dllVal)) continue;
                                    string apiDir = Path.GetDirectoryName(dllVal);
                                    if (!string.IsNullOrWhiteSpace(apiDir))
                                    {
                                        string net48Base = Path.Combine(apiDir, "net48", "Siemens.Engineering.Base.dll");
                                        if (File.Exists(net48Base)) candidates.Add(net48Base);
                                        if (File.Exists(dllVal)) candidates.Add(dllVal);
                                    }
                                }
                            }
                        }
                    }
                }
                catch
                {
                }
            }

            string[] regPaths = new[]
            {
                @"SOFTWARE\Siemens\Automation\_InstalledSW",
                @"SOFTWARE\WOW6432Node\Siemens\Automation\_InstalledSW"
            };
            foreach (string regPath in regPaths)
            {
                try
                {
                    using (RegistryKey baseKey = Registry.LocalMachine.OpenSubKey(regPath))
                    {
                        if (baseKey == null) continue;
                        ScanRegistryForPortalPaths(baseKey, candidates, requestedVersion, 0);
                    }
                }
                catch
                {
                }
            }
        }

        private static void ScanRegistryForPortalPaths(RegistryKey key, List<string> candidates, string requestedVersion, int depth)
        {
            if (key == null || depth > 3) return;
            try
            {
                object pathObj = key.GetValue("Path") ?? key.GetValue("InstallDir") ?? key.GetValue("Directory");
                string path = pathObj == null ? "" : Convert.ToString(pathObj).Trim().Trim('"');
                if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                {
                    DirectoryInfo dir = new DirectoryInfo(path);
                    for (int i = 0; i < 3 && dir != null; i++, dir = dir.Parent)
                    {
                        if (dir.Name.StartsWith("Portal V", StringComparison.OrdinalIgnoreCase)
                            || dir.Name.StartsWith("Portal_V", StringComparison.OrdinalIgnoreCase))
                        {
                            string ver = VersionFromPortalDirectory(dir.Name);
                            AddPortalFolderCandidates(candidates, dir.FullName, string.IsNullOrWhiteSpace(ver) ? requestedVersion : ver);
                            break;
                        }
                    }
                }

                foreach (string subName in key.GetSubKeyNames())
                {
                    using (RegistryKey sub = key.OpenSubKey(subName))
                    {
                        ScanRegistryForPortalPaths(sub, candidates, requestedVersion, depth + 1);
                    }
                }
            }
            catch
            {
            }
        }

        private static void ScanAutomationRoot(List<string> candidates, string rootDir)
        {
            if (string.IsNullOrWhiteSpace(rootDir) || !Directory.Exists(rootDir)) return;
            try
            {
                foreach (string portalDir in Directory.GetDirectories(rootDir, "Portal V*"))
                {
                    string folderVersion = VersionFromPortalDirectory(Path.GetFileName(portalDir));
                    AddPortalFolderCandidates(candidates, portalDir, folderVersion);
                }
                foreach (string subDir in Directory.GetDirectories(rootDir))
                {
                    foreach (string nested in Directory.GetDirectories(subDir, "Portal V*"))
                    {
                        string folderVersion = VersionFromPortalDirectory(Path.GetFileName(nested));
                        AddPortalFolderCandidates(candidates, nested, folderVersion);
                    }
                    foreach (string nested2 in Directory.GetDirectories(subDir))
                    {
                        foreach (string nestedPortal in Directory.GetDirectories(nested2, "Portal V*"))
                        {
                            string folderVersion = VersionFromPortalDirectory(Path.GetFileName(nestedPortal));
                            AddPortalFolderCandidates(candidates, nestedPortal, folderVersion);
                        }
                    }
                }
            }
            catch
            {
            }
        }

        private static void AddPortalFolderCandidates(List<string> candidates, string portalDirectory, string version)
        {
            if (string.IsNullOrWhiteSpace(portalDirectory) || string.IsNullOrWhiteSpace(version)) return;
            AddModularCandidates(candidates, portalDirectory, version);
            AddLegacyCandidate(candidates, portalDirectory, version);
        }

        private static void AddConfiguredCandidate(List<string> candidates, string path)
        {
            if (Directory.Exists(path))
            {
                candidates.Add(Path.Combine(path, "net48", "Siemens.Engineering.Base.dll"));
                candidates.Add(Path.Combine(path, "Siemens.Engineering.Base.dll"));
                candidates.Add(Path.Combine(path, "Siemens.Engineering.dll"));
                return;
            }
            candidates.Add(path);
        }

        private static void AddLegacyCandidate(List<string> candidates, string portalDirectory, string version)
        {
            if (string.IsNullOrWhiteSpace(version)) return;
            candidates.Add(Path.Combine(portalDirectory, "PublicAPI", version, "Siemens.Engineering.dll"));
        }

        private static void AddModularCandidates(List<string> candidates, string portalDirectory, string version)
        {
            if (string.IsNullOrWhiteSpace(version)) return;
            candidates.Add(Path.Combine(portalDirectory, "PublicAPI", version, "net48", "Siemens.Engineering.Base.dll"));
        }

        private static string VersionFromPortalDirectory(string name)
        {
            int marker = name.IndexOf('V');
            if (marker < 0) marker = name.IndexOf('v');
            return marker < 0 ? "" : "V" + name.Substring(marker + 1).Replace('_', '.').Trim();
        }

        private static string NormalizeVersionTag(string version)
        {
            if (string.IsNullOrWhiteSpace(version)) return "V21";
            string trimmed = version.Trim().Replace('_', '.');
            if (!trimmed.StartsWith("V", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = "V" + trimmed;
            }
            else
            {
                trimmed = "V" + trimmed.Substring(1);
            }
            return trimmed;
        }

        private static bool VersionsMatch(string a, string b)
        {
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
            Version va = ParseVersion(a);
            Version vb = ParseVersion(b);
            if (va.Major > 0 && vb.Major > 0)
            {
                if (va.Minor <= 0 || vb.Minor <= 0) return va.Major == vb.Major;
                return va.Major == vb.Major && va.Minor == vb.Minor;
            }
            return false;
        }

        private static string InferApiVersion(string dllPath)
        {
            DirectoryInfo directory = new DirectoryInfo(Path.GetDirectoryName(dllPath));
            for (int i = 0; i < 4 && directory != null; i++, directory = directory.Parent)
            {
                if (directory.Name.StartsWith("V", StringComparison.OrdinalIgnoreCase)
                    && directory.Name.Length >= 2
                    && char.IsDigit(directory.Name[1]))
                {
                    return "V" + directory.Name.Substring(1).Replace('_', '.');
                }
            }
            return "V" + FileVersionInfo.GetVersionInfo(dllPath).FileMajorPart;
        }

        private static Version ParseVersion(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return new Version(0, 0);
            string cleaned = value.Trim().TrimStart('V', 'v').Replace('_', '.');
            if (cleaned.IndexOf('.') < 0) cleaned = cleaned + ".0";
            Version version;
            return Version.TryParse(cleaned, out version)
                ? version
                : new Version(0, 0);
        }

        private Type[] AvailableTypes()
        {
            if (_cachedAvailableTypes != null) return _cachedAvailableTypes;
            List<Type> list = new List<Type>();
            foreach (Assembly assembly in _engineeringAssemblies)
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(t => t != null).ToArray();
                }
                list.AddRange(types);
            }
            _cachedAvailableTypes = list.ToArray();
            return _cachedAvailableTypes;
        }
    }
}
