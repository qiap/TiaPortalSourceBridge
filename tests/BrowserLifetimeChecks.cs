using System;
using TiaPortalSourceBridge.Web;
internal static class BrowserLifetimeChecks
{
    private static int checks;
    private static void Check(bool condition) { checks++; if (!condition) throw new Exception("Browser lifetime check " + checks + " failed"); }
    private static int Main()
    {
        var t = new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc);
        var leases = new BrowserLeases();
        Check(!leases.ShouldExit(t.AddHours(1)));
        leases.Heartbeat("tab-a", t); leases.Heartbeat("tab-b", t);
        leases.Release("tab-a", t.AddSeconds(1));
        Check(!leases.ShouldExit(t.AddSeconds(6)));
        leases.Release("tab-b", t.AddSeconds(7));
        Check(!leases.ShouldExit(t.AddSeconds(10)));
        leases.Heartbeat("tab-c", t.AddSeconds(10));
        Check(!leases.ShouldExit(t.AddSeconds(12)));
        leases.Release("unknown", t.AddSeconds(13)); Check(leases.Count == 1);
        leases.Release("tab-c", t.AddSeconds(14)); Check(leases.ShouldExit(t.AddSeconds(18)));
        leases.Heartbeat("crashed-tab", t.AddSeconds(20));
        Check(!leases.ShouldExit(t.AddSeconds(110)));
        Check(!leases.ShouldExit(t.AddSeconds(111)));
        Check(leases.ShouldExit(t.AddSeconds(115)));
        leases.Heartbeat("stable-tab", "old-page", t.AddSeconds(120));
        leases.Heartbeat("stable-tab", "new-page", t.AddSeconds(121));
        leases.Release("stable-tab", "old-page", t.AddSeconds(122));
        Check(!leases.ShouldExit(t.AddSeconds(127)));
        Check(leases.Count == 1);
        Console.WriteLine("PASS: " + checks + " browser lifetime checks (multi-tab, reload, close, browser crash).");
        return 0;
    }
}
