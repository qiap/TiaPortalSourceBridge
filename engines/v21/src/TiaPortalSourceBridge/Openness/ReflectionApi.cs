using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TiaPortalSourceBridge.Configuration;

namespace TiaPortalSourceBridge.Openness
{
    internal sealed class ReflectionApi
    {
        private static readonly object SyncRoot = new object();
        private static readonly List<string> SharedSearchDirectories = new List<string>();
        private static readonly object AssemblyLoadLock = new object();
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
            var requested = new AssemblyName(args.Name);
            if (string.IsNullOrWhiteSpace(requested.Name) || requested.Name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
                return null;
            var alreadyLoaded = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => OpennessSdk.MatchesReference(a.GetName(), requested));
            if (alreadyLoaded != null) return alreadyLoaded;
            string fileName = requested.Name + ".dll";
            string[] directories;
            lock (SyncRoot)
            {
                directories = SharedSearchDirectories.ToArray();
            }

            foreach (string directory in directories)
            {
                string dependency = Path.Combine(directory, fileName);
                if (File.Exists(dependency) && OpennessSdk.MatchesReference(OpennessSdk.ReadIdentity(dependency), requested))
                {
                    return Assembly.LoadFrom(dependency);
                }
            }
            return null;
        }

        public ReflectionApi(AppConfig config)
        {
            string dllPath = OpennessSdk.Resolve(config.TiaPortalVersion, config.SiemensEngineeringDll, true);
            string dllDirectory = Path.GetDirectoryName(dllPath);
            LoadedPortalVersion = PortalVersions.Normalize(config.TiaPortalVersion);
            lock (AssemblyLoadLock)
            {
                var loaded = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName())
                    .FirstOrDefault(a => (a.Name == "Siemens.Engineering" || a.Name == "Siemens.Engineering.Base")
                        && "V" + a.Version.Major != LoadedPortalVersion);
                if (loaded != null)
                    throw new InvalidOperationException("当前进程已加载 " + loaded.FullName + "，请使用独立的 " + LoadedPortalVersion + " 工程进程。");
                AddSearchDirectory(dllDirectory);
                AddPortalSearchDirectories(dllPath);
                LoadEngineeringAssemblies(dllPath);
            }
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
                    var identity = OpennessSdk.ReadIdentity(file);
                    if (identity == null || "V" + identity.Version.Major != LoadedPortalVersion)
                        throw new FileLoadException("模块版本与 " + LoadedPortalVersion + " 不匹配。", file);
                    Assembly assembly = Assembly.LoadFrom(file);
                    if (!_engineeringAssemblies.Any(a => string.Equals(a.FullName, assembly.FullName, StringComparison.OrdinalIgnoreCase)))
                    {
                        _engineeringAssemblies.Add(assembly);
                    }
                }
                catch (Exception ex)
                {
                    if (file.Equals(primaryDllPath, StringComparison.OrdinalIgnoreCase)) throw;
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
