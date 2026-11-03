using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace TiaPortalSourceBridge.Web
{
    // Leases count active pages, not ownership of the server process. Refresh, browser
    // suspension and a closed tab are indistinguishable from missing heartbeats.
    internal sealed class BrowserLeases
    {
        private readonly Dictionary<string, Tuple<string, DateTime>> clients = new Dictionary<string, Tuple<string, DateTime>>();
        internal int Count { get { return clients.Count; } }
        internal void Heartbeat(string id, DateTime now) { Heartbeat(id, id, now); }
        internal void Heartbeat(string id, string pageId, DateTime now) { clients[id] = Tuple.Create(pageId, now); }
        internal void Release(string id, DateTime now)
        { Release(id, id, now); }
        internal void Release(string id, string pageId, DateTime now)
        {
            Tuple<string, DateTime> current;
            if (clients.TryGetValue(id, out current) && current.Item1 == pageId) clients.Remove(id);
        }
        internal void Expire(DateTime now)
        {
            foreach (var id in clients.Where(c => now - c.Value.Item2 > TimeSpan.FromSeconds(90)).Select(c => c.Key).ToArray()) clients.Remove(id);
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
                        browserLeases.Expire(DateTime.UtcNow);
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
                // Explicit shutdown still waits for active engineering work to finish.
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
