using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using TiaPortalSourceBridge.Mcp;

internal static class VmPipeChecks
{
    private static int checks;

    private static void Check(bool condition, string name)
    {
        checks++;
        if (!condition) throw new Exception(name);
    }

    private static void TestMode(bool asyncMode)
    {
        string pipeName = "TiaVmPipeCheck-" + Guid.NewGuid().ToString("N");
        using (NamedPipeServerStream server = LocalPipeHelper.CreateLocalServerPipe(pipeName, 65536, asyncMode))
        {
            PipeSecurity acl = server.GetAccessControl();
            Check(acl.AreAccessRulesProtected, "ACL protection enabled (" + asyncMode + ")");
            SecurityIdentifier currentUser = WindowsIdentity.GetCurrent().User;
            SecurityIdentifier networkSid = new SecurityIdentifier(WellKnownSidType.NetworkSid, null);
            bool hasCurrentAllow = false;
            bool hasNetworkDeny = false;
            foreach (PipeAccessRule rule in acl.GetAccessRules(true, false, typeof(SecurityIdentifier)))
            {
                if (rule.IdentityReference == currentUser && rule.AccessControlType == AccessControlType.Allow)
                    hasCurrentAllow = true;
                if (rule.IdentityReference == networkSid && rule.AccessControlType == AccessControlType.Deny)
                    hasNetworkDeny = true;
            }
            Check(hasCurrentAllow, "Current user allowed (" + asyncMode + ")");
            Check(!hasNetworkDeny, "NetworkSid is not explicitly denied so local SSH sessions work (" + asyncMode + ")");

            Exception serverError = null;
            Thread t = new Thread(delegate()
            {
                try
                {
                    if (asyncMode)
                    {
                        IAsyncResult ar = server.BeginWaitForConnection(null, null);
                        server.EndWaitForConnection(ar);
                    }
                    else
                    {
                        server.WaitForConnection();
                    }

                    using (StreamReader reader = new StreamReader(server, new UTF8Encoding(false), false, 4096, true))
                    using (StreamWriter writer = new StreamWriter(server, new UTF8Encoding(false), 4096, true))
                    {
                        writer.AutoFlush = true;
                        string line = reader.ReadLine();
                        if (line != "ping") throw new Exception("Expected ping, got: " + line);
                        writer.WriteLine("pong");
                    }
                }
                catch (Exception ex)
                {
                    serverError = ex;
                }
            });
            t.IsBackground = true;
            t.Start();

            using (NamedPipeClientStream client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                client.Connect(5000);
                using (StreamReader reader = new StreamReader(client, new UTF8Encoding(false), false, 4096, true))
                using (StreamWriter writer = new StreamWriter(client, new UTF8Encoding(false), 4096, true))
                {
                    writer.AutoFlush = true;
                    writer.WriteLine("ping");
                    string reply = reader.ReadLine();
                    Check(reply == "pong", "Pipe roundtrip reply (" + asyncMode + ")");
                }
            }

            Check(t.Join(5000), "Server thread finishes (" + asyncMode + ")");
            if (serverError != null) throw serverError;
        }
    }

    private static int Main()
    {
        TestMode(true);
        TestMode(false);
        Console.WriteLine("PASS: " + checks + " local SSH-compatible named pipe checks.");
        return 0;
    }
}
