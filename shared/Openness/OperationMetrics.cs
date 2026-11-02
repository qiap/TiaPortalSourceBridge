using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace TiaPortalSourceBridge.Openness
{
    internal sealed class OperationMetrics : IDisposable
    {
        [ThreadStatic] private static OperationMetrics current;
        private readonly OperationMetrics parent;
        private readonly Stopwatch timer = Stopwatch.StartNew();
        private readonly Dictionary<string, long> counts = new Dictionary<string, long>();
        private readonly Dictionary<string, double> phases = new Dictionary<string, double>();
        private bool disposed;
        private OperationMetrics() { parent = current; current = this; }
        internal static OperationMetrics Begin() { return new OperationMetrics(); }
        internal static void Count(string name)
        {
            if (current == null) return;
            long value; current.counts.TryGetValue(name, out value); current.counts[name] = value + 1;
        }
        internal long Value(string name) { long value; counts.TryGetValue(name, out value); return value; }
        internal static IDisposable Measure(string name) { return current == null ? (IDisposable)Empty.Instance : new Phase(current, name); }
        private sealed class Empty : IDisposable { internal static readonly Empty Instance = new Empty(); public void Dispose() { } }
        private sealed class Phase : IDisposable
        {
            private readonly OperationMetrics owner; private readonly string name; private readonly Stopwatch watch = Stopwatch.StartNew();
            internal Phase(OperationMetrics owner, string name) { this.owner = owner; this.name = name; }
            public void Dispose() { watch.Stop(); double old; owner.phases.TryGetValue(name, out old); owner.phases[name] = old + watch.Elapsed.TotalMilliseconds; }
        }
        internal void Attach(Dictionary<string, object> result)
        {
            object raw; var meta = result.TryGetValue("_meta", out raw) ? raw as Dictionary<string, object> : null;
            if (meta == null) { meta = new Dictionary<string, object>(); result["_meta"] = meta; }
            var metrics = new Dictionary<string, object> { { "execution_ms", Math.Round(timer.Elapsed.TotalMilliseconds, 3) }, { "counts", new Dictionary<string, long>(counts) }, { "phases_ms", new Dictionary<string, double>(phases) } };
            if (meta.TryGetValue("performance", out raw)) metrics["worker"] = raw;
            meta["performance"] = metrics;
        }
        public void Dispose() { if (disposed) return; disposed = true; timer.Stop(); current = parent; }
    }
}
