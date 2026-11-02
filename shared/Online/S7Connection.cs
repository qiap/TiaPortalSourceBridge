using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace TiaPortalSourceBridge.Online
{
    internal interface IPduTransport : IDisposable { byte[] Exchange(byte[] pdu); }

    internal sealed class S7OnlineTransport : IPduTransport
    {
        private readonly Process worker;
        private readonly JavaScriptSerializer json = new JavaScriptSerializer();
        private readonly int timeout;
        public S7OnlineTransport(string executable, string ip, int rack, int slot, int timeoutMs)
        {
            timeout = timeoutMs;
            var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false) };
            worker = Process.Start(info);
            worker.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) Console.Error.WriteLine("S7ONLINE: " + e.Data); };
            worker.BeginErrorReadLine();
            try { Request(new { op = "connect", ip = ip, rack = rack, slot = slot }); }
            catch { Dispose(); throw; }
        }
        private object Request(object request)
        {
            worker.StandardInput.WriteLine(json.Serialize(request)); worker.StandardInput.Flush();
            var read = worker.StandardOutput.ReadLineAsync();
            if (!read.Wait(timeout)) { Dispose(); throw new TimeoutException("S7ONLINE worker timed out; connection closed."); }
            if (read.Result == null) throw new IOException("S7ONLINE worker exited.");
            var result = json.Deserialize<Dictionary<string, object>>(read.Result);
            if (!(bool)result["ok"]) throw new IOException(Convert.ToString(result["error"]));
            return result["value"];
        }
        public byte[] Exchange(byte[] pdu) { return Convert.FromBase64String(Convert.ToString(Request(new { op = "exchange", pdu = Convert.ToBase64String(pdu) }))); }
        public void Dispose()
        {
            try { if (!worker.HasExited) { worker.StandardInput.Close(); if (!worker.WaitForExit(2000)) worker.Kill(); } } catch { }
            worker.Dispose();
        }
    }

    internal sealed class S7TcpTransport : IPduTransport
    {
        private readonly TcpClient client;
        private readonly NetworkStream stream;
        public S7TcpTransport(string ip, int port, int rack, int slot, int timeoutMs)
        {
            client = new TcpClient();
            try
            {
                var connect = client.ConnectAsync(ip, port);
                if (!connect.Wait(timeoutMs)) throw new TimeoutException("S7 TCP connection timed out.");
                client.NoDelay = true; client.ReceiveTimeout = timeoutMs; client.SendTimeout = timeoutMs;
                stream = client.GetStream();
                byte[] request = { 3,0,0,22,17,0xE0,0,0,0,1,0,0xC1,2,1,0,0xC2,2,1,(byte)(rack*32+slot),0xC0,1,10 };
                stream.Write(request, 0, request.Length);
                byte[] confirm = ReceiveTpkt();
                if (confirm.Length < 7 || confirm[1] != 0xD0) throw new IOException("Invalid COTP connection confirmation.");
            }
            catch { client.Close(); throw; }
        }
        private byte[] ReadExact(int count)
        {
            byte[] bytes = new byte[count]; int offset = 0;
            while (offset < count) { int n = stream.Read(bytes, offset, count-offset); if (n == 0) throw new EndOfStreamException("S7 peer disconnected."); offset += n; }
            return bytes;
        }
        private byte[] ReceiveTpkt()
        {
            byte[] header = ReadExact(4);
            int length = (header[2] << 8) | header[3];
            if (header[0] != 3 || header[1] != 0 || length < 7 || length > 65535) throw new IOException("Invalid TPKT header.");
            return ReadExact(length-4);
        }
        public byte[] Exchange(byte[] pdu)
        {
            int length = pdu.Length+7;
            byte[] packet = new byte[length]; packet[0]=3; packet[2]=(byte)(length>>8); packet[3]=(byte)length;
            packet[4]=2; packet[5]=0xF0; packet[6]=0x80; Buffer.BlockCopy(pdu,0,packet,7,pdu.Length);
            stream.Write(packet,0,packet.Length);
            using (var result = new MemoryStream())
            {
                for (int i=0;i<64;i++)
                {
                    byte[] fragment = ReceiveTpkt();
                    if (fragment.Length<3 || fragment[1]!=0xF0 || fragment[0]<2 || fragment[0]+1>fragment.Length) throw new IOException("Invalid COTP data packet.");
                    int start=fragment[0]+1; result.Write(fragment,start,fragment.Length-start);
                    if (result.Length>65535) throw new IOException("Oversized S7 response.");
                    if ((fragment[2]&0x80)!=0) return result.ToArray();
                }
                throw new IOException("Too many COTP fragments.");
            }
        }
        public void Dispose() { client.Close(); }
    }

    internal sealed class PlcAddress
    {
        public string Text; public int Db; public int Offset; public int Bit=-1; public byte Area=0x84;
        public string Type; public int Size;
        public static PlcAddress Parse(string text, string requestedType)
        {
            Match m=Regex.Match(text ?? "", @"^DB([1-9][0-9]{0,4})\.DB([XBWD])([0-9]+)(?:\.([0-7]))?$",RegexOptions.IgnoreCase);
            if (!m.Success) throw new ArgumentException("Use an absolute standard DB address, e.g. DB2180.DBW28 or DB12000.DBX10.7. Optimized DBs require a different driver or a standard diagnostic DB.");
            var a=new PlcAddress {Text=text.ToUpperInvariant(),Db=int.Parse(m.Groups[1].Value),Offset=int.Parse(m.Groups[3].Value)};
            if(a.Db>65535 || a.Offset>2097151) throw new ArgumentOutOfRangeException("address");
            string width=m.Groups[2].Value.ToUpperInvariant();
            if(width=="X") { if(!m.Groups[4].Success)throw new ArgumentException("DBX requires a bit index."); a.Bit=int.Parse(m.Groups[4].Value); }
            else if(m.Groups[4].Success) throw new ArgumentException("Only DBX has a bit index.");
            a.Type=string.IsNullOrWhiteSpace(requestedType) ? (width=="X"?"Bool":width=="B"?"Byte":width=="W"?"Word":"DWord") : requestedType;
            switch(a.Type.ToLowerInvariant())
            {
                case "bool":a.Size=1; if(width!="X")throw new ArgumentException("Bool requires DBX.");break;
                case "byte":case "sint":case "usint":a.Size=1; if(width!="B")throw new ArgumentException("8-bit type requires DBB.");break;
                case "word":case "int":case "uint":a.Size=2;if(width!="W")throw new ArgumentException("16-bit type requires DBW.");break;
                case "dword":case "dint":case "udint":case "real":case "time":a.Size=4;if(width!="D")throw new ArgumentException("32-bit type requires DBD.");break;
                default:throw new ArgumentException("Supported types: Bool, Byte, SInt, USInt, Word, Int, UInt, DWord, DInt, UDInt, Real, Time.");
            }
            if((long)a.Offset+a.Size>2097152)throw new ArgumentOutOfRangeException("address");
            return a;
        }
        public object Decode(byte[] bytes,int start)
        {
            switch(Type.ToLowerInvariant())
            {
                case "bool":return (bytes[start]&(1<<Bit))!=0;
                case "byte":case "usint":return bytes[start];
                case "sint":return unchecked((sbyte)bytes[start]);
                case "word":case "uint":return (ushort)((bytes[start]<<8)|bytes[start+1]);
                case "int":return unchecked((short)((bytes[start]<<8)|bytes[start+1]));
                default:
                    uint value=((uint)bytes[start]<<24)|((uint)bytes[start+1]<<16)|((uint)bytes[start+2]<<8)|bytes[start+3];
                    if(Type.Equals("Real",StringComparison.OrdinalIgnoreCase)) return BitConverter.ToSingle(BitConverter.GetBytes(value),0);
                    if(Type.Equals("DInt",StringComparison.OrdinalIgnoreCase)||Type.Equals("Time",StringComparison.OrdinalIgnoreCase))return unchecked((int)value);
                    return value;
            }
        }
    }

    internal sealed class S7Connection : IDisposable
    {
        private readonly IPduTransport transport; private ushort sequence;
        public S7Connection(IPduTransport transport)
        {
            this.transport=transport;
            try
            {
                byte[] response=Job(new byte[]{0xF0,0,0,1,0,1,1,0xE0},new byte[0]);
                if(response.Length<20 || response[12]!=0xF0 || Get16(response,18)<240)throw new IOException("Invalid S7 PDU negotiation.");
            }
            catch {transport.Dispose();throw;}
        }
        internal static int Get16(byte[] bytes,int offset) {return (bytes[offset]<<8)|bytes[offset+1];}
        private byte[] Job(byte[] parameter,byte[] data)
        {
            byte[] request=new byte[10+parameter.Length+data.Length]; ushort reference=++sequence;
            request[0]=0x32;request[1]=1;request[4]=(byte)(reference>>8);request[5]=(byte)reference;
            request[6]=(byte)(parameter.Length>>8);request[7]=(byte)parameter.Length;request[8]=(byte)(data.Length>>8);request[9]=(byte)data.Length;
            Buffer.BlockCopy(parameter,0,request,10,parameter.Length);Buffer.BlockCopy(data,0,request,10+parameter.Length,data.Length);
            byte[] result=transport.Exchange(request);
            if(result.Length<12 || result[0]!=0x32 || result[1]!=3 || Get16(result,4)!=reference || result.Length!=12+Get16(result,6)+Get16(result,8))throw new IOException("Invalid or mismatched S7 response.");
            if(result[10]!=0 || result[11]!=0)throw new IOException("PLC rejected request: 0x"+result[10].ToString("X2")+result[11].ToString("X2")+". Check CPU access rights / PUT-GET permission.");
            return result;
        }
        private static byte[] Variable(byte function,int db,int offset,int length,int bit)
        {
            if(db<1||db>65535||offset<0||offset>2097151||length<1||length>200)throw new ArgumentOutOfRangeException("DB read/write range");
            int address=checked(offset*8+(bit<0?0:bit));
            return new byte[]{function,1,0x12,0x0A,0x10,(byte)(bit<0?2:1),(byte)(length>>8),(byte)length,(byte)(db>>8),(byte)db,0x84,(byte)(address>>16),(byte)(address>>8),(byte)address};
        }
        public byte[] Read(int db,int offset,int length)
        {
            byte[] response=Job(Variable(4,db,offset,length,-1),new byte[0]);
            if(Get16(response,6)!=2 || response[12]!=4 || response[13]!=1 || Get16(response,8)<4)throw new IOException("Malformed ReadVar response.");
            int start=14;
            if(response[start]!=0xFF)throw new IOException("ReadVar item error 0x"+response[start].ToString("X2")+"; DB may be optimized, inaccessible, missing, or beyond its size.");
            if(response[start+1]!=4 || Get16(response,start+2)!=length*8 || response.Length!=start+4+length)throw new IOException("Unexpected ReadVar data size/type.");
            byte[] result=new byte[length];Buffer.BlockCopy(response,start+4,result,0,length);return result;
        }
        public void WriteBool(PlcAddress address,bool value)
        {
            if(address.Bit<0)throw new ArgumentException("Boolean DBX address required.");
            byte[] response=Job(Variable(5,address.Db,address.Offset,1,address.Bit),new byte[]{0,3,0,1,(byte)(value?1:0)});
            if(Get16(response,6)!=2||response[12]!=5||response[13]!=1||Get16(response,8)!=1||response[14]!=0xFF)throw new IOException("WriteVar rejected by CPU.");
        }
        public void Dispose(){transport.Dispose();}
    }
}
