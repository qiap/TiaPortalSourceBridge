using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Linq;
using TiaPortalSourceBridge.Configuration;

namespace TiaPortalSourceBridge.Mcp
{
    internal sealed class ProjectRouter : IDisposable
    {
        private readonly Dictionary<string, IProjectWorker> workers = new Dictionary<string, IProjectWorker>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IProjectWorker> discoveryWorkers = new Dictionary<string, IProjectWorker>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> discoveryRetry = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly Func<string, IProjectWorker> factory;
        private readonly Func<string> defaultVersion;
        private string initialProject;
        private readonly Func<IEnumerable<string>> runningVersions;
        internal ProjectRouter(Func<string, IProjectWorker> factory, Func<string> defaultVersion, Func<IEnumerable<string>> runningVersions = null)
        {
            this.factory = factory;
            this.defaultVersion = defaultVersion;
            this.runningVersions = runningVersions ?? RunningVersions;
        }

        private static IEnumerable<string> RunningVersions()
        {
            var versions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var process in Process.GetProcessesByName("Siemens.Automation.Portal"))
            {
                using (process)
                {
                    try
                    {
                        var module = process.MainModule;
                        if (module == null) { versions.Add(""); continue; }
                        var folder = Regex.Match(module.FileName, @"[\\/]Portal\s+V(\d+)[\\/]", RegexOptions.IgnoreCase);
                        string major = folder.Success ? folder.Groups[1].Value : module.FileVersionInfo.ProductMajorPart.ToString();
                        versions.Add(PortalVersions.Normalize(major) ?? "");
                    }
                    catch (InvalidOperationException) { /* The process exited during enumeration. */ }
                    catch (System.ComponentModel.Win32Exception) { versions.Add(""); }
                    catch (ArgumentException) { versions.Add(""); }
                }
            }
            return versions;
        }
        private static string Text(Dictionary<string, object> m, string k) { object value; return m.TryGetValue(k, out value) ? Convert.ToString(value) : ""; }
        private static bool Error(Dictionary<string, object> result) { object value; return result.TryGetValue("isError", out value) && Convert.ToBoolean(value); }
        private static Dictionary<string, object> Data(Dictionary<string, object> result)
        {
            object value;
            var data = result.TryGetValue("structuredContent", out value) ? value as Dictionary<string, object> : null;
            if (data == null) { data = new Dictionary<string, object>(); result["structuredContent"] = data; }
            return data;
        }
        private static Dictionary<string, object> Result(string text, object data)
        { return new Dictionary<string, object> { { "isError", false }, { "content", new object[] { new { type = "text", text = text } } }, { "structuredContent", data } }; }
        private static void VerifyProject(Dictionary<string, object> data, string expected)
        {
            string actual = Text(data, "project_path");
            if (!string.IsNullOrWhiteSpace(actual) && !string.Equals(Normalize(actual), expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("后台返回的项目与请求不一致，已拒绝该结果；请核实工程状态。请求：" + expected + "；返回：" + actual);
        }
        internal static string Normalize(string path)
        {
            PortalRoute.FromProject(path);
            var config = new AppConfig(); config.OverrideProject(path, false); return config.ProjectPath;
        }
        internal void ConfigureInitialProject(string path) { initialProject = string.IsNullOrWhiteSpace(path) ? null : Normalize(path); }
        internal string TryResolveSingleProject()
        {
            if (workers.Count > 1) return null;
            return workers.Keys.SingleOrDefault() ?? initialProject;
        }
        private string SingleProject()
        {
            if (workers.Count > 1) throw new InvalidOperationException("当前管理多个项目，请明确提供 project_path；不会默认选择或切换写入目标。");
            return workers.Keys.SingleOrDefault();
        }
        internal string GenerationVersion(string projectPath)
        {
            string path = string.IsNullOrWhiteSpace(projectPath) ? SingleProject() ?? initialProject : Normalize(projectPath);
            return path == null ? PortalRoute.Exact(defaultVersion()) : PortalRoute.FromProject(path);
        }
        private Dictionary<string, object> Discover(Dictionary<string, object> arguments)
        {
            string version = Text(arguments, "portal_version");
            if (string.IsNullOrWhiteSpace(version))
            {
                var rows = new List<object>();
                var errors = new List<string>();
                var observed = runningVersions().ToList();
                if (observed.Any(string.IsNullOrEmpty))
                    errors.Add("存在无法确认版本的博途进程；请指定 portal_version 查询。");
                var candidates = observed.Where(v => !string.IsNullOrEmpty(v)).Concat(workers.Keys.Select(PortalRoute.FromProject))
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(v => v).ToArray();
                foreach (var idleVersion in discoveryWorkers.Keys.Except(candidates, StringComparer.OrdinalIgnoreCase).ToList())
                {
                    var idle = discoveryWorkers[idleVersion];
                    if (idle.IsBusy) continue;
                    idle.Dispose();
                    discoveryWorkers.Remove(idleVersion);
                }
                foreach (string candidate in candidates)
                {
                    try
                    {
                        var reply = Discover(new Dictionary<string, object> { { "portal_version", candidate } });
                        object processes;
                        if (!Error(reply) && Data(reply).TryGetValue("processes", out processes))
                            rows.AddRange(processes as object[] ?? new object[0]);
                        else errors.Add(candidate + ": discovery failed");
                    }
                    catch (Exception ex) { errors.Add(candidate + ": " + ex.Message); }
                }
                return Result("已检查各版本的打开工程；多个工程时请明确 project_path。", new Dictionary<string, object> { { "processes", rows.ToArray() }, { "discovery_errors", errors.ToArray() }, { "versions_checked", candidates } });
            }
            version = PortalRoute.Exact(version);
            var existing = workers.Where(pair => string.Equals(AppConfig.DetectProjectVersion(pair.Key), version, StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Value).FirstOrDefault(w => w.IsAlive && !w.IsBusy && Text(w.Describe(), "worker_state") == "ready");
            if (existing != null)
            {
                try
                {
                    return existing.Call("tia_list_open_projects", new Dictionary<string, object> { { "portal_version", version } });
                }
                catch
                {
                    // Fallback to isolated discovery worker if existing worker fails
                }
            }
            DateTime retry;
            if(discoveryRetry.TryGetValue(version,out retry) && DateTime.UtcNow < retry)
                throw new InvalidOperationException(version + " 工程发现暂不可用，正在退避等待；不会重复启动进程。");
            IProjectWorker worker;
            try {
                if(discoveryWorkers.TryGetValue(version,out worker)) {
                    if(worker.IsBusy) throw new InvalidOperationException(version + " 发现请求仍在执行，未启动新的发现进程。");
                    if(!worker.IsAlive || Text(worker.Describe(),"worker_state")=="faulted") {
                        worker.Dispose(); discoveryWorkers.Remove(version); worker=null;
                    }
                }
                if(worker==null) { worker=factory("discovery:"+version); discoveryWorkers[version]=worker; }
                var result=worker.Call("tia_list_open_projects",new Dictionary<string,object>{{"portal_version",version}});
                if(Error(result)) discoveryRetry[version]=DateTime.UtcNow.AddSeconds(60);
                return result;
            } catch { discoveryRetry[version]=DateTime.UtcNow.AddSeconds(60); throw; }
        }
        private string Resolve(string requested, string version)
        {
            if (!string.IsNullOrWhiteSpace(requested)) return Normalize(requested);
            string current = SingleProject();
            if (current != null) return current;
            if (initialProject != null) return initialProject;
            var scope = new Dictionary<string, object>();
            if (version != null) scope["portal_version"] = version;
            var discovery = Discover(scope);
            if (Error(discovery)) throw new InvalidOperationException("无法发现项目；请提供 project_path。" + new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(discovery));
            object errors;
            if (Data(discovery).TryGetValue("discovery_errors", out errors) && ((string[])errors).Length > 0)
                throw new InvalidOperationException("工程发现不完整，请指定 project_path。 " + string.Join("；", (string[])errors));
            object rows; Data(discovery).TryGetValue("processes", out rows);
            var projects = (rows as object[] ?? new object[0]).OfType<Dictionary<string, object>>().Select(r => Text(r, "project_path"))
                .Where(p => !string.IsNullOrWhiteSpace(p)).Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (projects.Length != 1) throw new InvalidOperationException(projects.Length == 0 ? "未发现已打开的项目，请打开项目并提供 project_path。" : "发现多个已打开项目，请明确提供 project_path：" + string.Join("；", projects));
            return projects[0];
        }
        private Dictionary<string, object> Status(string path, IProjectWorker worker)
        {
            var transport = worker.Describe();
            var data = new Dictionary<string, object> { { "project_path", path }, { "portal_version", PortalRoute.FromProject(path) }, { "connected", worker.IsAlive ? (object)null : false } };
            if (Text(transport, "worker_state") == "ready")
            {
                try
                {
                    var reply = worker.Call("tia_connection_status", new Dictionary<string, object> { { "project_path", path } });
                    VerifyProject(Data(reply), path);
                    foreach (var pair in Data(reply)) data[pair.Key] = pair.Value;
                    if (Error(reply)) data["status_error"] = reply;
                }
                catch (Exception ex) { data["status_error"] = ex.Message; }
            }
            foreach (var pair in transport) data[pair.Key] = pair.Value;
            data["project_activity"] = ProjectOperationGuard.Inspect(path);
            return data;
        }
        private static Dictionary<string, object> UnmanagedStatus(string path)
        {
            var activity = ProjectOperationGuard.Inspect(path);
            return new Dictionary<string, object> { { "connected", false }, { "project_path", path }, { "portal_version", PortalRoute.FromProject(path) },
                { "worker_state", activity.Busy ? "busy_outcome_unknown" : activity.Unavailable || activity.Unresolved ? "outcome_unknown" : "unmanaged" },
                { "project_activity", activity }, { "recovery", activity.Reason } };
        }
        internal Dictionary<string, object> Call(string tool, Dictionary<string, object> arguments)
        {
            string requested = Text(arguments, "project_path");
            string version = PortalRoute.Requested(arguments);
            if (tool == "tia_list_open_projects") return Discover(version == null ? arguments : new Dictionary<string, object> { { "portal_version", version } });
            if (tool == "tia_list_connections" || tool == "tia_connection_status")
            {
                if (!string.IsNullOrWhiteSpace(requested))
                {
                    string path = Normalize(requested); IProjectWorker worker;
                    return Result("指定项目的连接状态。", workers.TryGetValue(path, out worker) ? Status(path, worker) : UnmanagedStatus(path));
                }
                var states = workers.Where(w => version == null || PortalRoute.FromProject(w.Key) == version).Select(w => Status(w.Key, w.Value)).ToArray();
                var data = states.Length == 1 ? new Dictionary<string, object>(states[0]) : new Dictionary<string, object> { { "connected", states.Any(s => Convert.ToBoolean(s["connected"])) }, { "project_path", "" }, { "portal_version", "" } };
                data["projects"] = states; data["managed_project_count"] = states.Length;
                data["connected_project_count"] = states.Count(s => Convert.ToBoolean(s["connected"]));
                data["unknown_connection_count"] = states.Count(s => s["connected"] == null);
                data["explicit_project_required"] = workers.Count > 1;
                data["preferred_project_path"] = initialProject ?? "";
                return Result("多项目连接状态；管理 " + states.Length + " 个项目。", data);
            }
            if (tool == "tia_disconnect_all")
            {
                if (version != null) throw new ArgumentException("tia_disconnect_all 作用于全部工程。请用 tia_disconnect 和 project_path 断开指定工程。");
                // Check all workers before disconnecting any, so a busy worker cannot cause partial teardown.
                if (workers.Values.Any(w => w.IsBusy)) throw new InvalidOperationException("仍有项目请求在执行，请等待完成并核实结果后再断开全部连接。");
                foreach (var worker in workers.Values) worker.Dispose(); workers.Clear();
                return Result("已释放全部连接；未保存、未关闭博途项目。", new { connected = false, connected_project_count = 0 });
            }
            if (tool == "tia_disconnect")
            {
                string path = string.IsNullOrWhiteSpace(requested) ? SingleProject() : Normalize(requested); IProjectWorker worker;
                if (path != null) PortalRoute.RequireMatch(version, PortalRoute.FromProject(path));
                if (path != null && workers.TryGetValue(path, out worker))
                {
                    if (worker.IsBusy) throw new InvalidOperationException("该项目仍在执行请求，请等待完成并查询状态后再断开。");
                    worker.Dispose(); workers.Remove(path);
                }
                return Result("已释放指定连接；其他项目连接保留，未保存、未关闭博途项目。", new { connected = false, project_path = path ?? "", remaining_project_count = workers.Count });
            }
            string target = Resolve(requested, version);
            PortalRoute.RequireMatch(version, PortalRoute.FromProject(target));
            IProjectWorker selected;
            if (!workers.TryGetValue(target, out selected))
            { selected = factory(target); workers.Add(target, selected); }
            else if (tool == "tia_connect" && (!selected.IsAlive || Text(selected.Describe(), "worker_state") == "faulted"))
            { selected.Dispose(); selected = factory(target); workers[target] = selected; }
            var scoped = new Dictionary<string, object>(arguments); scoped["project_path"] = target;
            var result = selected.Call(tool, scoped);
            VerifyProject(Data(result), target);
            Data(result)["project_path"] = target;
            return result;
        }
        public void Dispose() { foreach (var worker in workers.Values) worker.Dispose(); workers.Clear(); foreach(var worker in discoveryWorkers.Values) worker.Dispose(); discoveryWorkers.Clear(); discoveryRetry.Clear(); }
    }
}
