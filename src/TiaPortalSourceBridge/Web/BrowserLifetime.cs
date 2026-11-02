using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace TiaPortalSourceBridge.Web
{
    // Multiple tabs share one server. Navigation has a grace period; a lost browser expires.
    internal sealed class BrowserLeases
    {
        private readonly Dictionary<string, Tuple<string, DateTime>> clients = new Dictionary<string, Tuple<string, DateTime>>();
        private bool seen;
        private DateTime? emptySince;
        internal int Count { get { return clients.Count; } }
        internal void Heartbeat(string id, DateTime now) { Heartbeat(id, id, now); }
        internal void Heartbeat(string id, string pageId, DateTime now) { seen = true; clients[id] = Tuple.Create(pageId, now); emptySince = null; }
        internal void Release(string id, DateTime now)
        { Release(id, id, now); }
        internal void Release(string id, string pageId, DateTime now)
        {
            Tuple<string, DateTime> current;
            if (clients.TryGetValue(id, out current) && current.Item1 == pageId && clients.Remove(id) && clients.Count == 0) emptySince = now;
        }
        internal bool ShouldExit(DateTime now)
        {
            foreach (var id in clients.Where(c => now - c.Value.Item2 > TimeSpan.FromSeconds(90)).Select(c => c.Key).ToArray()) clients.Remove(id);
            if (seen && clients.Count == 0 && emptySince == null) emptySince = now;
            return seen && clients.Count == 0 && emptySince != null && now - emptySince.Value >= TimeSpan.FromSeconds(4);
        }
    }

    internal sealed partial class WebServer
    {
        private readonly BrowserLeases browserLeases = new BrowserLeases();
        private readonly object browserLifetimeGate = new object();
        private volatile bool shutdownRequested;
        private int BrowserClientCount() { lock (browserLifetimeGate) return browserLeases.Count; }
        private void StartBrowserLifetime()
        {
            var watcher = new Thread(() => {
                while (_running && !shutdownRequested)
                {
                    Thread.Sleep(500);
                    lock (browserLifetimeGate)
                        if (browserLeases.ShouldExit(DateTime.UtcNow)) { RequestDashboardShutdown(); return; }
                }
            }) { IsBackground = true, Name = "DashboardBrowserLifetime" };
            watcher.Start();
        }
        private void RequestDashboardShutdown()
        {
            lock (browserLifetimeGate)
            {
                if (shutdownRequested) return;
                shutdownRequested = true;
            }
            ThreadPool.QueueUserWorkItem(_ => {
                // Do not interrupt an import/compile when the last browser tab disappears.
                while (ActionRunner.AnyBlockingRunning) Thread.Sleep(500);
                lock (DashboardGate)
                {
                    if (DashboardServer != null) { DashboardServer.Dispose(); DashboardServer = null; }
                }
                _running = false;
                try { _listener.Stop(); } catch { }
                try { _listener.Close(); } catch { }
                // Returning from ListenLoop exits the web process; its owned MCP host then exits too.
            });
        }
    }
}
