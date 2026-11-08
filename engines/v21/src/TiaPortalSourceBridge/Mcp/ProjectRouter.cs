using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TiaPortalSourceBridge.Configuration;

namespace TiaPortalSourceBridge.Mcp
{
    internal sealed class ProjectRouter : IDisposable
    {
        private readonly Dictionary<string, IProjectWorker> workers = new Dictionary<string, IProjectWorker>(StringComparer.OrdinalIgnoreCase);
        private readonly Func<string, IProjectWorker> factory;
        private readonly Func<string> defaultVersion;
        private string initialProject;
        internal ProjectRouter(Func<string, IProjectWorker> factory, Func<string> defaultVersion) { this.factory = factory; this.defaultVersion = defaultVersion; }
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
            if (path == null) return defaultVersion();
            var config = new AppConfig(); config.OverrideProject(path, false); return config.TiaPortalVersion;
        }
        private Dictionary<string, object> Discover(Dictionary<string, object> arguments)
        {
            string version = Text(arguments, "portal_version");
            if (string.IsNullOrWhiteSpace(version)) version = defaultVersion();
            var existing = workers.Values.FirstOrDefault(w => w.IsAlive && !w.IsBusy && Text(w.Describe(), "worker_state") == "ready");
            if (existing != null && string.Equals(version, defaultVersion(), StringComparison.OrdinalIgnoreCase))
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
            using (IProjectWorker worker = factory("discovery:" + version))
                return worker.Call("tia_list_open_projects", new Dictionary<string, object> { { "portal_version", version } });
        }
        private string Resolve(string requested)
        {
            if (!string.IsNullOrWhiteSpace(requested)) return Normalize(requested);
            string current = SingleProject();
            if (current != null) return current;
            if (initialProject != null) return initialProject;
            var discovery = Discover(new Dictionary<string, object>());
            if (Error(discovery)) throw new InvalidOperationException("无法发现项目；请提供 project_path。" + new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(discovery));
            object rows; Data(discovery).TryGetValue("processes", out rows);
            var projects = (rows as object[] ?? new object[0]).OfType<Dictionary<string, object>>().Select(r => Text(r, "project_path"))
                .Where(p => !string.IsNullOrWhiteSpace(p)).Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (projects.Length != 1) throw new InvalidOperationException(projects.Length == 0 ? "未发现已打开的项目，请打开项目并提供 project_path。" : "发现多个已打开项目，请明确提供 project_path：" + string.Join("；", projects));
            return projects[0];
        }
        private Dictionary<string, object> Status(string path, IProjectWorker worker)
        {
            var transport = worker.Describe();
            var data = new Dictionary<string, object> { { "project_path", path }, { "connected", worker.IsAlive ? (object)null : false } };
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
            return new Dictionary<string, object> { { "connected", false }, { "project_path", path },
                { "worker_state", activity.Busy ? "busy_outcome_unknown" : activity.Unavailable || activity.Unresolved ? "outcome_unknown" : "unmanaged" },
                { "project_activity", activity }, { "recovery", activity.Reason } };
        }
        internal Dictionary<string, object> Call(string tool, Dictionary<string, object> arguments)
        {
            string requested = Text(arguments, "project_path");
            if (tool == "tia_list_open_projects") return Discover(arguments);
            if (tool == "tia_list_connections" || tool == "tia_connection_status")
            {
                if (!string.IsNullOrWhiteSpace(requested))
                {
                    string path = Normalize(requested); IProjectWorker worker;
                    return Result("指定项目的连接状态。", workers.TryGetValue(path, out worker) ? Status(path, worker) : UnmanagedStatus(path));
                }
                var states = workers.Select(w => Status(w.Key, w.Value)).ToArray();
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
                // Check all workers before disconnecting any, so a busy worker cannot cause partial teardown.
                if (workers.Values.Any(w => w.IsBusy)) throw new InvalidOperationException("仍有项目请求在执行，请等待完成并核实结果后再断开全部连接。");
                foreach (var worker in workers.Values) worker.Dispose(); workers.Clear();
                return Result("已释放全部连接；未保存、未关闭博途项目。", new { connected = false, connected_project_count = 0 });
            }
            if (tool == "tia_disconnect")
            {
                string path = string.IsNullOrWhiteSpace(requested) ? SingleProject() : Normalize(requested); IProjectWorker worker;
                if (path != null && workers.TryGetValue(path, out worker))
                {
                    if (worker.IsBusy) throw new InvalidOperationException("该项目仍在执行请求，请等待完成并查询状态后再断开。");
                    worker.Dispose(); workers.Remove(path);
                }
                return Result("已释放指定连接；其他项目连接保留，未保存、未关闭博途项目。", new { connected = false, project_path = path ?? "", remaining_project_count = workers.Count });
            }
            string target = Resolve(requested); IProjectWorker selected;
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
        public void Dispose() { foreach (var worker in workers.Values) worker.Dispose(); workers.Clear(); }
    }
}
