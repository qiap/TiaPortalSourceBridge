using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace TiaPortalSourceBridge.Openness
{
    internal sealed class HardwareOperations
    {
        private readonly OpennessSession session;
        private ReflectionApi Api { get { return session.Api; } }
        private readonly Dictionary<string, object> index = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> errors = new Dictionary<string, string>();
        private readonly HashSet<object> seen = new HashSet<object>();
        private readonly Dictionary<object, string> reverse = new Dictionary<object, string>();
        private string scope = "";
        private static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 }; }
        private static string Text(Dictionary<string, object> m, string key, string fallback = "") { object v; return m.TryGetValue(key, out v) ? Convert.ToString(v) : fallback; }
        private static string Segment(string text) { return Uri.EscapeDataString(text); }
        public HardwareOperations(OpennessSession session) { this.session = session; }

        private bool Relevant(string path) { return scope.Length == 0 || string.Equals(path, scope, StringComparison.OrdinalIgnoreCase) || path.StartsWith(scope + "/", StringComparison.OrdinalIgnoreCase) || scope.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase); }
        private void Discover(string prefix = "")
        {
            using (OperationMetrics.Measure("hardware_scan"))
            {
            OperationMetrics.Count("hardware_scans");
            scope = (prefix ?? "").TrimEnd('/');
            if (scope.Length > 0 && !scope.StartsWith("/", StringComparison.Ordinal)) throw new ArgumentException("硬件路径必须以 / 开始。");
            index.Clear(); errors.Clear(); seen.Clear(); reverse.Clear();
            Add("/project", session.Project);
            Devices(Api.Get(session.Project, "Devices", false), "/devices");
            object ungrouped = Api.Get(session.Project, "UngroupedDevices", false);
            Devices(Api.Get(ungrouped, "Devices", false), "/ungrouped/devices");
            Groups(Api.Get(session.Project, "DeviceGroups", false), "/groups");
            Composition(Api.Get(session.Project, "Subnets", false), "/subnets", false);
            }
        }
        private bool Add(string path, object item)
        {
            if (item == null || !seen.Add(item)) return false;
            if (index.Count >= 20000) throw new InvalidOperationException("硬件对象数量超过单次读取上限。");
            index.Add(path, item); reverse[item] = path; return true;
        }
        private void Devices(object composition, string prefix)
        {
            if (!Relevant(prefix)) return;
            foreach (object device in Api.Items(composition))
            {
                string path = prefix + "/" + Segment(Api.GetString(device, "Name"));
                if (Relevant(path) && Add(path, device)) Items(Api.Get(device, "DeviceItems", false), path + "/items");
            }
        }
        private void Groups(object composition, string prefix)
        {
            if (!Relevant(prefix)) return;
            foreach (object group in Api.Items(composition))
            {
                string path = prefix + "/" + Segment(Api.GetString(group, "Name"));
                if (!Relevant(path)) continue;
                Devices(Api.Get(group, "Devices", false), path + "/devices"); Groups(Api.Get(group, "Groups", false), path + "/groups");
            }
        }
        private void Items(object composition, string prefix)
        {
            if (!Relevant(prefix)) return;
            foreach (object item in Api.Items(composition))
            {
                string path = prefix + "/" + Segment(Api.GetString(item, "Name"));
                if (!Relevant(path) || !Add(path, item)) continue;
                Composition(Api.Get(item, "Addresses", false), path + "/addresses", false);
                try
                {
                    object network = Relevant(path + "/network") ? Api.InvokeGenericNoArguments(item, "GetService", Api.RequireType("Siemens.Engineering.HW.Features.NetworkInterface")) : null;
                    if (network != null)
                    {
                        Add(path + "/network", network);
                        Composition(Api.Get(network, "Nodes", false), path + "/network/nodes", false);
                        Composition(Api.Get(network, "IoControllers", false), path + "/network/controllers", true);
                        Composition(Api.Get(network, "IoConnectors", false), path + "/network/connectors", false);
                        Composition(Api.Get(network, "Ports", false), path + "/network/ports", false);
                    }
                }
                catch (Exception ex) { errors[path + "/network"] = ex.Message; }
                Items(Api.Get(item, "DeviceItems", false), path + "/items");
            }
        }
        private void Composition(object composition, string prefix, bool controller)
        {
            if (!Relevant(prefix)) return;
            int i = 0;
            foreach (object item in Api.Items(composition))
            {
                string name = Api.GetString(item, "Name"); string path = prefix + "/" + Segment(string.IsNullOrWhiteSpace(name) ? i.ToString() : name); i++;
                if (!Relevant(path) || !Add(path, item)) continue;
                if (controller) Add(path + "/io_system", Api.Get(item, "IoSystem", false));
            }
        }
        private object Find(string path)
        {
            object target;
            if (string.IsNullOrWhiteSpace(path) || !index.TryGetValue(path.TrimEnd('/'), out target)) throw new InvalidOperationException("找不到硬件路径，请先读取实际路径：" + path);
            return target;
        }

        public object Read(string prefix, bool includeAttributes)
        {
            Discover(prefix);
            var rows = new List<object>();
            var resolveExternalLinks = new List<Action>();
            foreach (var entry in index)
            {
                if (!string.IsNullOrEmpty(prefix) && !entry.Key.Equals(prefix.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
                    && !entry.Key.StartsWith(prefix.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)) continue;
                object attributes = null; string error = "";
                if (includeAttributes) try { attributes = Attributes(entry.Value); } catch (Exception ex) { error = ex.Message; }
                var links = new Dictionary<string, object>();
                foreach (string property in new[] { "ConnectedSubnet", "ConnectedIoSystem" })
                {
                    object linked = Api.Get(entry.Value, property, false);
                    if (linked != null)
                    {
                        string related;
                        bool indexed = reverse.TryGetValue(linked, out related);
                        links[property] = indexed ? related : property == "ConnectedSubnet" ? "/subnets/" + Segment(Api.GetString(linked, "Name")) : Api.GetString(linked, "Name");
                        if (!indexed && scope.Length > 0 && property != "ConnectedSubnet")
                        {
                            string key = property;
                            resolveExternalLinks.Add(() => { string path; if (reverse.TryGetValue(linked, out path)) links[key] = path; });
                        }
                    }
                }
                object[] ports = Api.Items(Api.Get(entry.Value, "ConnectedPorts", false)).ToArray();
                if (ports.Length > 0)
                {
                    var paths = new string[ports.Length];
                    links["ConnectedPorts"] = paths;
                    for (int i = 0; i < ports.Length; i++)
                    {
                        object port = ports[i]; string related;
                        bool indexed = reverse.TryGetValue(port, out related);
                        paths[i] = indexed ? related : Api.GetString(port, "Name");
                        if (!indexed && scope.Length > 0)
                        {
                            int slot = i;
                            resolveExternalLinks.Add(() => { string path; if (reverse.TryGetValue(port, out path)) paths[slot] = path; });
                        }
                    }
                }
                rows.Add(new { path = entry.Key, name = Api.GetString(entry.Value, "Name"), type = entry.Value.GetType().FullName,
                    attributes = attributes, links = links, error = error });
            }
            // A linked IO system or physical port can live outside the requested subtree.
            // Preserve canonical paths; only these cases need a full topology index.
            if (resolveExternalLinks.Count > 0)
            {
                Discover();
                foreach (Action resolve in resolveExternalLinks) resolve();
            }
            return new { project_path = session.GetProjectPath(), portal_version = Api.LoadedPortalVersion, objects = rows.ToArray(), service_errors = errors.ToArray() };
        }
        private object[] Attributes(object target)
        {
            object[] infos = target.GetType().GetMethod("GetAttributeInfos") == null ? new object[0] : Api.Items(Api.Invoke(target, "GetAttributeInfos")).ToArray();
            string[] names = infos.Where(i => Api.GetString(i, "AccessMode").IndexOf("Read", StringComparison.OrdinalIgnoreCase) >= 0).Select(i => Api.GetString(i, "Name")).ToArray();
            var values = new Dictionary<string, object>();
            if (names.Length > 0)
            {
                object[] batch = Api.Items(Api.Invoke(target, "GetAttributes", (object)names)).ToArray();
                for (int i = 0; i < names.Length; i++)
                {
                    object item = i < batch.Length ? batch[i] : null;
                    if (item != null && item.GetType().GetProperty("Key") != null) values[Api.GetString(item, "Key")] = Api.Get(item, "Value", false);
                    else values[names[i]] = item;
                }
            }
            var rows = infos.Select(info =>
            {
                string name = Api.GetString(info, "Name"); object value; values.TryGetValue(name, out value);
                return (object)new { name = name, access = Api.GetString(info, "AccessMode"), type = value == null ? "" : value.GetType().FullName,
                    value = value == null || value is string || value is bool || value.GetType().IsPrimitive || value is decimal ? value : Convert.ToString(value) };
            }).ToList();
            var advertised = new HashSet<string>(infos.Select(i => Api.GetString(i, "Name")), StringComparer.Ordinal);
            foreach (var property in target.GetType().GetProperties().Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && Scalar(p.PropertyType) && !advertised.Contains(p.Name)))
            {
                object value = null; string error = "";
                try { value = property.GetValue(target, null); } catch (Exception ex) { error = ex.Message; }
                rows.Add(new { name = property.Name, access = property.GetSetMethod() != null ? "Read, Write" : "Read", type = property.PropertyType.FullName,
                    value = property.PropertyType.IsEnum ? Convert.ToString(value) : value, error = error });
            }
            return rows.ToArray();
        }
        internal static object ConvertValue(object value, object current)
        {
            if (current == null || value == null) return value;
            Type type = current.GetType();
            if (type.IsEnum) return Enum.Parse(type, Convert.ToString(value), true);
            if (type == typeof(Guid)) return Guid.Parse(Convert.ToString(value));
            if (type == typeof(TimeSpan)) return TimeSpan.Parse(Convert.ToString(value), CultureInfo.InvariantCulture);
            return Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
        }
        private int SetAttributes(object target, Dictionary<string, object> attributes)
        {
            var infos = (target.GetType().GetMethod("GetAttributeInfos") == null ? new object[0] : Api.Items(Api.Invoke(target, "GetAttributeInfos"))).ToDictionary(i => Api.GetString(i, "Name"), StringComparer.Ordinal);
            var writes = new List<KeyValuePair<string, object>>();
            var properties = new List<Tuple<System.Reflection.PropertyInfo, object>>();
            foreach (var pair in attributes)
            {
                object info;
                if (!infos.TryGetValue(pair.Key, out info))
                {
                    var property = target.GetType().GetProperty(pair.Key);
                    if (property == null || !property.CanRead || !property.CanWrite || !Scalar(property.PropertyType) || property.GetSetMethod() == null) throw new ArgumentException("属性不存在或不可写：" + pair.Key);
                    object old = property.GetValue(target, null); object converted = ConvertValue(pair.Value, old);
                    if (!object.Equals(old, converted)) properties.Add(Tuple.Create(property, converted));
                    continue;
                }
                if (Api.GetString(info, "AccessMode").IndexOf("Write", StringComparison.OrdinalIgnoreCase) < 0)
                    throw new ArgumentException("属性不存在或不可写：" + pair.Key);
                object current = Api.GetString(info, "AccessMode").IndexOf("Read", StringComparison.OrdinalIgnoreCase) >= 0 ? Api.Invoke(target, "GetAttribute", pair.Key) : null;
                object changed = ConvertValue(pair.Value, current);
                if (Api.GetString(info, "AccessMode").IndexOf("Read", StringComparison.OrdinalIgnoreCase) < 0 || !object.Equals(current, changed)) writes.Add(new KeyValuePair<string, object>(pair.Key, changed));
            }
            if (writes.Count > 0) Api.Invoke(target, "SetAttributes", writes);
            foreach (var property in properties) property.Item1.SetValue(target, property.Item2, null);
            return writes.Count + properties.Count;
        }
        public CompileSummary Compile(string path) { Discover(); return session.CompileEngineeringObject(Find(path)); }
        private static bool Scalar(Type type) { return type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) || type == typeof(Guid) || type == typeof(TimeSpan); }

        internal static Dictionary<string, object>[] ParsePlan(string planJson)
        {
            var rows = Json().DeserializeObject(planJson) as object[];
            if (rows == null || rows.Length == 0 || rows.Length > 2000) throw new ArgumentException("plan_json 必须是 1..2000 项操作数组。");
            string[] operations = { "create_device", "plug_module", "create_subnet", "set_attributes", "connect_subnet", "create_io_system", "connect_io_system", "connect_ports" };
            return rows.Select((row, i) =>
            {
                var m = row as Dictionary<string, object>;
                if (m == null || !operations.Contains(Text(m, "op"))) throw new ArgumentException("组态计划第 " + i + " 项操作无效。");
                string op = Text(m, "op");
                if ((op == "create_device" || op == "plug_module" || op == "create_subnet") && string.IsNullOrWhiteSpace(Text(m, "type_identifier"))) throw new ArgumentException("创建硬件必须提供实际目录中的 type_identifier。");
                if (op == "create_device" || op == "plug_module" || op == "create_subnet" || op == "create_io_system")
                    if (string.IsNullOrWhiteSpace(Text(m, "name"))) throw new ArgumentException("新对象 name 不能为空。");
                if (op != "create_device" && op != "create_subnet" && string.IsNullOrWhiteSpace(Text(m, "target"))) throw new ArgumentException("操作需要 target 路径。");
                if (op == "plug_module" && (!m.ContainsKey("position") || Convert.ToInt32(m["position"]) < 0)) throw new ArgumentException("模块需要非负 position。");
                if (op == "set_attributes" && !(m.ContainsKey("attributes") && m["attributes"] is Dictionary<string, object>)) throw new ArgumentException("set_attributes 需要 attributes 对象。");
                if (op == "connect_subnet" && string.IsNullOrWhiteSpace(Text(m, "subnet"))) throw new ArgumentException("需要 subnet 路径。");
                if (op == "connect_io_system" && string.IsNullOrWhiteSpace(Text(m, "io_system"))) throw new ArgumentException("需要 io_system 路径。");
                if (op == "connect_ports" && string.IsNullOrWhiteSpace(Text(m, "peer"))) throw new ArgumentException("需要 peer 端口路径。");
                return m;
            }).ToArray();
        }
        public Dictionary<string, object> Apply(string planJson, string backupRoot, bool dryRun)
        {
            Dictionary<string, object>[] plan = ParsePlan(planJson);
            if (dryRun) return new Dictionary<string, object> { { "dry_run", true }, { "saved", false }, { "success", true }, { "syntax_valid", true }, { "runtime_validation_required", true }, { "operations", plan } };
            object before = Read("", true);
            Directory.CreateDirectory(backupRoot);
            string backup = Path.Combine(backupRoot, "hardware-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(backup, Json().Serialize(new { snapshot = before, plan = plan, note = "Attribute and topology snapshot; not a project archive or automatic rollback." }));
            var results = new List<object>(); bool failed = false;
            bool softwareChanged = false;
            session.RunInTransactionConditional("Apply Hardware Configuration (" + plan.Length + " ops)", true, delegate
            {
                for (int i = 0; i < plan.Length; i++)
                {
                    if (failed) { results.Add(new { index = i, ok = false, skipped = true, error = "Stopped after prior failure; V21 transaction rolled back." }); continue; }
                    try
                    {
                        var m = plan[i]; string op = Text(m, "op"); object result = null; int? changedAttributes = null;
                        if (op == "create_device")
                        {
                            object devices = Api.Get(session.Project, "Devices", true);
                            if (Api.Items(devices).Any(d => string.Equals(Api.GetString(d, "Name"), Text(m, "device_name", Text(m, "name")), StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("设备名已存在，请读取后修改已有对象。");
                            softwareChanged = true;
                            result = Api.Invoke(devices, "CreateWithItem", Text(m, "type_identifier"), Text(m, "name"), Text(m, "device_name", Text(m, "name")));
                            string path = "/devices/" + Segment(Api.GetString(result, "Name"));
                            Add(path, result); Items(Api.Get(result, "DeviceItems", false), path + "/items");
                        }
                        else if (op == "create_subnet") { result = Api.Invoke(Api.Get(session.Project, "Subnets", true), "Create", Text(m, "type_identifier"), Text(m, "name")); Add("/subnets/" + Segment(Api.GetString(result, "Name")), result); }
                        else
                        {
                            object target = Find(Text(m, "target"));
                            if (op == "set_attributes")
                            {
                                var attributes = (Dictionary<string, object>)m["attributes"];
                                string[] stable = { "Address", "SubnetMask", "RouterAddress", "UseRouter", "StartAddress", "Comment", "Author", "PnDeviceName", "PnDeviceNameSetDirectly" };
                                try { changedAttributes = SetAttributes(target, attributes); }
                                catch { if (attributes.Keys.Any(k => !stable.Contains(k))) softwareChanged = true; throw; }
                                result = target;
                                if (changedAttributes > 0 && attributes.Keys.Any(k => !stable.Contains(k))) { Discover(); softwareChanged = true; }
                            }
                            else if (op == "plug_module")
                            {
                                if (!(bool)Api.Invoke(target, "CanPlugNew", Text(m, "type_identifier"), Text(m, "name"), Convert.ToInt32(m["position"]))) throw new InvalidOperationException("当前硬件不允许在该槽位插入此模块。");
                                softwareChanged = true;
                                result = Api.Invoke(target, "PlugNew", Text(m, "type_identifier"), Text(m, "name"), Convert.ToInt32(m["position"]));
                                Items(Api.Get(target, "DeviceItems", false), Text(m, "target").TrimEnd('/') + "/items");
                            }
                            else if (op == "connect_subnet") Api.Invoke(target, "ConnectToSubnet", Find(Text(m, "subnet")));
                            else if (op == "create_io_system") { result = Api.Invoke(target, "CreateIoSystem", Text(m, "name")); Add(Text(m, "target").TrimEnd('/') + "/io_system", result); }
                            else if (op == "connect_io_system") Api.Invoke(target, "ConnectToIoSystem", Find(Text(m, "io_system")));
                            else if (op == "connect_ports") Api.Invoke(target, "ConnectToPort", Find(Text(m, "peer")));
                        }
                        string resultPath = ""; if (result != null) reverse.TryGetValue(result, out resultPath);
                        results.Add(new { index = i, ok = true, skipped = false, result_path = resultPath ?? "", changed_attribute_count = changedAttributes, error = "" });
                    }
                    catch (Exception ex) { failed = true; results.Add(new { index = i, ok = false, skipped = false, may_have_changed = false, error = ex.Message }); }
                }
                return Tuple.Create(0, !failed);
            });
            string refreshError = "";
            if (softwareChanged) try { session.RefreshSoftware(); } catch (Exception ex) { refreshError = ex.Message; }
            return new Dictionary<string, object> { { "dry_run", false }, { "saved", false }, { "rolled_back", failed }, { "success", !failed && refreshError.Length == 0 },
                { "snapshot", backup }, { "operations", results.ToArray() }, { "refresh_error", refreshError },
                { "note", failed ? "Failure occurred; V21 Transaction rolled back prior operations." : "Committed in V21 Transaction; project not saved to disk." } };
        }
    }
}
