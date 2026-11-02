using System;
using System.Collections.Generic;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using PlcsimS7online;

// x86 worker isolates Siemens' installed 32-bit S7ONLINE DLL from the x64 MCP host.
internal static class OnlineWorker
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    private static PlcS7onlineMsgPumpS7 pump;
    private static Thread thread;
    private static readonly AutoResetEvent responseReady = new AutoResetEvent(false);
    private static byte[] response;
    private static string error;
    private static bool connected;

    [STAThread]
    private static int Main()
    {
        Console.InputEncoding = Console.OutputEncoding = new UTF8Encoding(false);
        JavaScriptSerializer json = new JavaScriptSerializer();
        string line;
        try
        {
            while ((line = Console.ReadLine()) != null)
            {
                try
                {
                    var args = json.Deserialize<Dictionary<string, object>>(line);
                    string op = Convert.ToString(args["op"]);
                    object value;
                    if (op == "connect")
                    {
                        if (pump != null) throw new InvalidOperationException("Worker already connected; create a fresh worker.");
                        IPAddress ip = IPAddress.Parse(Convert.ToString(args["ip"]));
                        int rack = Convert.ToInt32(args["rack"]), slot = Convert.ToInt32(args["slot"]);
                        var started = new ManualResetEvent(false);
                        thread = new Thread(delegate()
                        {
                            try
                            {
                                pump = new PlcS7onlineMsgPumpS7(ip, rack, slot);
                                IntPtr handle = pump.Handle;
                                pump.eventOnDataFromPlcsimReceived += OnResponse;
                                started.Set();
                                pump.Run();
                            }
                            catch (Exception ex) { error = ex.Message; started.Set(); responseReady.Set(); }
                        });
                        thread.IsBackground = true;
                        thread.SetApartmentState(ApartmentState.STA);
                        thread.Start();
                        if (!started.WaitOne(5000) || pump == null) throw new InvalidOperationException(error ?? "Worker startup timeout.");
                        responseReady.Reset(); error = null;
                        Send(PlcS7onlineMsgPump.WM_M_CONNECTPLCSIM, IntPtr.Zero);
                        Wait();
                        if (!connected) throw new InvalidOperationException(error ?? "S7ONLINE connection failed.");
                        value = true;
                    }
                    else if (op == "exchange")
                    {
                        if (!connected) throw new InvalidOperationException("Not connected.");
                        byte[] pdu = Convert.FromBase64String(Convert.ToString(args["pdu"]));
                        if (pdu.Length < 10 || pdu.Length > 960) throw new ArgumentException("Invalid PDU length.");
                        IntPtr buffer = Marshal.AllocHGlobal(pdu.Length + 4);
                        try
                        {
                            Marshal.WriteInt32(buffer, pdu.Length);
                            Marshal.Copy(pdu, 0, IntPtr.Add(buffer, 4), pdu.Length);
                            responseReady.Reset(); response = null; error = null;
                            Send(PlcS7onlineMsgPump.WM_M_SENDDATA, buffer);
                            Wait();
                            if (response == null) throw new InvalidOperationException(error ?? "No PDU received.");
                            value = Convert.ToBase64String(response);
                        }
                        finally { Marshal.FreeHGlobal(buffer); }
                    }
                    else if (op == "close") { Close(); value = true; }
                    else throw new ArgumentException("Unknown operation.");
                    Console.WriteLine(json.Serialize(new { ok = true, value = value }));
                }
                catch (Exception ex) { Console.WriteLine(json.Serialize(new { ok = false, error = ex.Message })); }
            }
        }
        finally { Close(); }
        return 0;
    }

    private static void Send(uint message, IntPtr data)
    {
        IntPtr result;
        if (SendMessageTimeout(pump.Handle, message, IntPtr.Zero, data, 2, 5000, out result) == IntPtr.Zero)
            throw new TimeoutException("S7ONLINE worker message timeout.");
    }
    private static void Wait()
    {
        if (!responseReady.WaitOne(5000)) throw new TimeoutException("S7ONLINE response timeout. Check S7ONLINE PG/PC interface and simulated CPU.");
        if (error != null) throw new InvalidOperationException(error);
    }
    private static void OnResponse(PlcS7onlineMsgPump.MessageFromPlcsim msg)
    {
        if (msg.type == PlcS7onlineMsgPump.MessageFromPlcsimType.ConnectSuccess) { connected = true; responseReady.Set(); }
        else if (msg.type == PlcS7onlineMsgPump.MessageFromPlcsimType.Pdu) { response = msg.pdu; responseReady.Set(); }
        else if (msg.type == PlcS7onlineMsgPump.MessageFromPlcsimType.ConnectError || msg.type == PlcS7onlineMsgPump.MessageFromPlcsimType.ErrorMessage)
        { connected = false; error = msg.textmessage; responseReady.Set(); }
    }
    private static void Close()
    {
        connected = false;
        if (pump != null && thread != null && thread.IsAlive)
        { try { Send(PlcS7onlineMsgPump.WM_M_EXIT, IntPtr.Zero); thread.Join(2000); } catch { } }
        pump = null;
    }
}
