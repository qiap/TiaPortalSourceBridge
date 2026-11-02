using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace TiaPortalSourceBridge.Online
{
    internal sealed class OnlineTag
    {
        public string Name {get;set;} public string Address {get;set;} public string Type {get;set;}
        public string Description {get;set;} public bool TestWritable {get;set;}
    }
    internal sealed class OnlineProfile
    {
        public string Name {get;set;} public string Ip {get;set;} public int Rack {get;set;} public int Slot {get;set;}
        public int TestDb {get;set;} public uint TestSignature {get;set;}
        public string AddressEvidence {get;set;} public OnlineTag[] Tags {get;set;}
        public int EffectiveTestDb { get { return TestDb > 0 ? TestDb : 32021; } }
        public uint EffectiveTestSignature { get { return TestSignature != 0 ? TestSignature : 0x4F503237u; } }
    }

    internal sealed class OnlineTools : IDisposable
    {
        private readonly object gate=new object();
        private readonly string root;
        private readonly JavaScriptSerializer json=new JavaScriptSerializer {MaxJsonLength=32*1024*1024};
        private S7Connection connection;
        private OnlineProfile profile;
        private sealed class ReadItem { internal string Name; internal OnlineTag Tag; internal PlcAddress Address; }
        private sealed class ReadGroup { internal int StartIndex, EndIndex, Db, Offset, Length; }
        private sealed class ReadPlan { internal ReadItem[] Items; internal ReadGroup[] Groups; }
        // Bounded per-session cache. Keys preserve requested spelling and order for response compatibility.
        private readonly Dictionary<string, ReadPlan> readPlans = new Dictionary<string, ReadPlan>(StringComparer.Ordinal);
        private ReadPlan Plan(string[] names)
        {
            string key = string.Concat(names.Select(n => n.Length + ":" + n));
            ReadPlan plan;
            if (readPlans.TryGetValue(key, out plan)) return plan;
            var items = names.Select(n => new ReadItem { Name = n, Tag = Resolve(n) }).ToArray();
            foreach (var item in items) item.Address = PlcAddress.Parse(item.Tag.Address, item.Tag.Type);
            items = items.OrderBy(v => v.Address.Db).ThenBy(v => v.Address.Offset).ToArray();
            var groups = new List<ReadGroup>();
            for (int i = 0; i < items.Length;)
            {
                int end = i + 1, start = items[i].Address.Offset, limit = start + items[i].Address.Size, db = items[i].Address.Db;
                while (end < items.Length && items[end].Address.Db == db && items[end].Address.Offset + items[end].Address.Size - start <= 200)
                { limit = Math.Max(limit, items[end].Address.Offset + items[end].Address.Size); end++; }
                groups.Add(new ReadGroup { StartIndex = i, EndIndex = end, Db = db, Offset = start, Length = limit - start });
                i = end;
            }
            plan = new ReadPlan { Items = items, Groups = groups.ToArray() };
            if (readPlans.Count >= 32) readPlans.Clear();
            readPlans.Add(key, plan);
            return plan;
        }
        private string transportName,ip,targetMode,sessionId;
        private int rack,slot;
        private bool writesEnabled;
        private DateTime connectedAt,lastRead;
        private string lastError;
        private CancellationTokenSource watchCancel;
        private Task watchTask;
        private string watchId,watchFile;
        private string[] watchNames;
        private int samples,changes;
        private bool watchRunning;
        private CancellationTokenSource leaseCancel;
        private Task leaseTask;
        private DateTime leaseUntil;
        private readonly Queue<object> events=new Queue<object>();
        public OnlineTools(string root){this.root=root;}
        internal static Dictionary<string,object> Map(params object[] pairs)
        {
            var map=new Dictionary<string,object>();for(int i=0;i<pairs.Length;i+=2)map.Add((string)pairs[i],pairs[i+1]);return map;
        }
        private static object Value(Dictionary<string,object> args,string name,object fallback=null){object value;return args.TryGetValue(name,out value)?value:fallback;}
        private static string Text(Dictionary<string,object> args,string name,string fallback=""){return Convert.ToString(Value(args,name,fallback));}
        private static int Number(Dictionary<string,object> args,string name,int fallback){return Convert.ToInt32(Value(args,name,fallback));}
        private static bool Flag(Dictionary<string,object> args,string name){object value=Value(args,name,false);if(!(value is bool))throw new ArgumentException(name+" must be boolean.");return (bool)value;}
        private static string Stamp(){return DateTime.UtcNow.ToString("o");}
        private static string[] Names(Dictionary<string,object> args)
        {
            var values=Value(args,"names") as IEnumerable;
            if(values==null || values is string)throw new ArgumentException("names must be an array of profile tag names or DB addresses.");
            var result=values.Cast<object>().Select(Convert.ToString).ToArray();
            if(result.Length<1||result.Length>128||result.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=result.Length)throw new ArgumentException("Provide 1..128 unique names.");
            return result;
        }
        private object Result(string text,object data,bool error=false){return Map("content",new[]{Map("type","text","text",text)},"isError",error,"structuredContent",data);}
        public object Call(string name,Dictionary<string,object> args)
        {
            if(name=="tia_online_watch_stop") {StopWatch();lock(gate)return Result("在线记录已停止。",WatchState());}
            if(name=="tia_online_disconnect"){StopLease();StopWatch();lock(gate){Disconnect();return Result("在线连接已关闭；没有停止 CPU 或修改程序。",Status());}}
            if(name=="tia_online_connect") {StopLease();StopWatch(); lock(gate)return Connect(args);}
            lock(gate)
            {
                if(name=="tia_online_status")return Result("在线通信状态",Status());
                if(name=="tia_online_watch_read")return Result("采样记录（不能证明捕获了所有扫描周期或快速工步）",Map("watch",WatchState(),"events",events.Skip(Math.Max(0,events.Count-NumberBounded(args,"limit",50,1,500))).ToArray()));
                EnsureConnected();
                if(name=="tia_online_read") {var read=Read(Names(args));return Result("在线采样；多组数据可能来自不同扫描周期。",read,!(bool)read["complete"]);}
                if(name=="tia_online_graph_status")return GraphStatus();
                if(name=="tia_online_trace_read")return TraceRead(args);
                if(name=="tia_online_write_test")return WriteTest(args);
                if(name=="tia_online_watch_start")return StartWatch(args);
                throw new ArgumentException("Unknown online tool: "+name);
            }
        }
        private object Connect(Dictionary<string,object> args)
        {
            Disconnect();
            string profilePath=Text(args,"profile_path",Path.Combine(root,"profiles","op270.json"));
            profile=json.Deserialize<OnlineProfile>(File.ReadAllText(profilePath,System.Text.Encoding.UTF8));
            ValidateProfile(profile);
            ip=Text(args,"ip",profile.Ip); System.Net.IPAddress parsed;
            if(!System.Net.IPAddress.TryParse(ip,out parsed)||parsed.AddressFamily!=System.Net.Sockets.AddressFamily.InterNetwork)throw new ArgumentException("An explicit IPv4 address is required.");
            rack=NumberBounded(args,"rack",profile.Rack,0,7);slot=NumberBounded(args,"slot",profile.Slot,0,31);
            int timeout=NumberBounded(args,"timeout_ms",10000,1000,30000);
            targetMode=Text(args,"target_mode","hardware");
            if(targetMode!="hardware"&&targetMode!="simulation")throw new ArgumentException("target_mode must be hardware or simulation.");
            transportName=Text(args,"transport","s7online");
            writesEnabled=Flag(args,"enable_simulation_writes");
            if(writesEnabled&&(targetMode!="simulation"||!Flag(args,"confirm_simulation_target")))throw new ArgumentException("Simulation writes require target_mode=simulation and confirm_simulation_target=true. Target classification is operator supplied, not automatically proven.");
            // First release deliberately restricts writes to local S7ONLINE simulation test DBs.
            if(writesEnabled&&transportName!="s7online")throw new ArgumentException("TCP connections are read-only in this release.");
            try
            {
                IPduTransport transport;
                if(transportName=="s7online")transport=new S7OnlineTransport(ResolveWorkerPath(),ip,rack,slot,timeout);
                else if(transportName=="s7tcp")transport=new S7TcpTransport(ip,NumberBounded(args,"port",102,1,65535),rack,slot,timeout);
                else throw new ArgumentException("transport must be s7online or s7tcp.");
                connection=new S7Connection(transport); sessionId=Guid.NewGuid().ToString("N");connectedAt=DateTime.UtcNow;lastRead=DateTime.MinValue;lastError=null;
                return Result("在线连接和 S7 协议握手成功。请读取已知变量确认地址映射；连接成功不代表变量权限已验证。",Status());
            }
            catch{Disconnect();throw;}
        }
        private string ResolveWorkerPath()
        {
            string[] candidates=new[]{
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"S7OnlineWorker.exe"),
                Path.Combine(root,"online-bin","S7OnlineWorker.exe"),
                Path.Combine(root,"src","TiaPortalSourceBridge","bin","Release","net48","S7OnlineWorker.exe")
            };
            string found=candidates.FirstOrDefault(File.Exists);
            return found??candidates[0];
        }
        internal static void ValidateProfile(OnlineProfile p)
        {
            if(p==null||p.Tags==null||p.Tags.Length==0||p.Tags.Length>2048)throw new ArgumentException("Invalid online profile.");
            var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int expectedTestDb=p.EffectiveTestDb;
            foreach(var tag in p.Tags)
            {
                if(string.IsNullOrWhiteSpace(tag.Name)||!names.Add(tag.Name))throw new ArgumentException("Profile tag names must be nonempty and unique.");
                var a=PlcAddress.Parse(tag.Address,tag.Type);
                if(tag.TestWritable&&(a.Db!=expectedTestDb||!a.Type.Equals("Bool",StringComparison.OrdinalIgnoreCase)||a.Offset<4||a.Offset>7))throw new ArgumentException("Test writes are limited to Bool fields in DB"+expectedTestDb+" bytes 4..7. No direct I/O, safety, mode, or GRAPH internal writes.");
            }
        }
        private static int NumberBounded(Dictionary<string,object> args,string name,int fallback,int min,int max)
        {int value=Number(args,name,fallback);if(value<min||value>max)throw new ArgumentOutOfRangeException(name,"Expected "+min+".."+max);return value;}
        private OnlineTag Resolve(string name)
        {
            OnlineTag tag=profile.Tags.FirstOrDefault(t=>t.Name.Equals(name,StringComparison.OrdinalIgnoreCase));
            if(tag!=null)return tag;
            var a=PlcAddress.Parse(name,null);return new OnlineTag{Name=name,Address=a.Text,Type=a.Type};
        }
        private void EnsureConnected(){if(connection==null)throw new InvalidOperationException("Call tia_online_connect first; Openness connection is separate.");}
        private Dictionary<string,object> Read(string[] names)
        {
            var plan=Plan(names);var requests=plan.Items;
            var values=new Dictionary<string,object>();bool complete=true;
            string started=Stamp();
            foreach(var group in plan.Groups)
            {
                int i=group.StartIndex,end=group.EndIndex,start=group.Offset,db=group.Db,limit=start+group.Length;
                try
                {
                    byte[] bytes=connection.Read(db,start,limit-start);string timestamp=Stamp();
                    for(int j=i;j<end;j++)values[requests[j].Name]=Map("value",requests[j].Address.Decode(bytes,requests[j].Address.Offset-start),"quality","good","timestamp_utc",timestamp,"address",requests[j].Tag.Address,"type",requests[j].Tag.Type);
                    lastRead=DateTime.UtcNow;
                }
                catch(Exception ex)
                {
                    complete=false;lastError=ex.Message;
                    for(int j=i;j<end;j++)values[requests[j].Name]=Map("value",null,"quality","bad","timestamp_utc",Stamp(),"address",requests[j].Tag.Address,"error",ex.Message);
                    // Protocol/transport errors must not leave a poisoned stream in service.
                    if(!(ex is IOException)||!ex.Message.StartsWith("ReadVar item error",StringComparison.Ordinal))
                    {Disconnect();for(int j=end;j<requests.Length;j++)values[requests[j].Name]=Map("value",null,"quality","bad","timestamp_utc",Stamp(),"error","Connection closed after communication failure.");break;}
                }
            }
            return Map("started_utc",started,"completed_utc",Stamp(),"complete",complete,"atomic_across_groups",false,"values",values);
        }
        private object GraphStatus()
        {
            string[] names={"graph.step","graph.hmi_step","graph.active","graph.auto","graph.manual","graph.error","mode.station_to_home","mode.auto_initial","graph.init_done","io.auto_selector"};
            var snapshot=Read(names);var values=(Dictionary<string,object>)snapshot["values"];
            var hints=new List<string>();
            Func<string,object> val=n=>{var row=(Dictionary<string,object>)values[n];return (string)row["quality"]=="good"?row["value"]:null;};
            if(!(bool)snapshot["complete"])hints.Add("部分变量读取失败，不能据此确定流程状态。请核对 DB 下载版本、偏移及访问权限。");
            if(object.Equals(val("mode.station_to_home"),true))hints.Add("回原点请求为 TRUE，它参与 OFF_SQ。OFF_SQ 还受 FirstScan 影响，此快照未采集 FirstScan。");
            if(object.Equals(val("graph.active"),false)&&object.Equals(val("graph.auto"),true))hints.Add("自动模式但没有活动步；检查初始化请求及顺控器控制，不自动强制跳步。");
            if(object.Equals(val("mode.auto_initial"),true)||object.Equals(val("graph.init_done"),true))hints.Add("INIT_SQ 的至少一条输入支路为 TRUE；初始化采用上升沿请求，不能仅凭当前电平认定正在反复初始化。");
            if(object.Equals(val("graph.manual"),true))hints.Add("GRAPH 当前处于手动模式。");
            return Result("GRAPH 在线快照；提示是根据已读变量推断，不能替代完整转换条件诊断。",Map("snapshot",snapshot,"hints",hints.ToArray(),"step_number_note","graph.step 是 GRAPH 步号；graph.hmi_step 是画面工步，两者不同。"),!(bool)snapshot["complete"]);
        }
        private object WriteTest(Dictionary<string,object> args)
        {
            if(!writesEnabled||targetMode!="simulation"||transportName!="s7online"||!Flag(args,"confirm"))throw new InvalidOperationException("Write is disabled. Only an explicitly confirmed local simulation session may write allowlisted test DB fields.");
            string name=Text(args,"name");OnlineTag tag=profile.Tags.FirstOrDefault(t=>t.Name==name);
            if(tag==null||!tag.TestWritable)throw new InvalidOperationException("Variable is not on the simulation test write allowlist.");
            if(name=="test.heartbeat")throw new InvalidOperationException("Heartbeat is maintained by a bounded test lease, not directly writable through this tool.");
            object desired=Value(args,"value");if(!(desired is bool))throw new ArgumentException("value must be a JSON boolean.");
            int leaseSeconds=NumberBounded(args,"lease_seconds",30,1,300);
            int testDb=profile.EffectiveTestDb; uint expectedSig=profile.EffectiveTestSignature;
            // Verify the dedicated DB's fixed signature, never infer its presence from a successful connection.
            byte[] signature=connection.Read(testDb,0,4);
            if(UInt32(signature,0)!=expectedSig)throw new InvalidOperationException("DB"+testDb+" test schema signature is missing or mismatched; no write performed.");
            PlcAddress address=PlcAddress.Parse(tag.Address,tag.Type);
            string auditPath=Path.Combine(root,"online-records","writes.jsonl");Directory.CreateDirectory(Path.GetDirectoryName(auditPath));
            object previous=address.Decode(connection.Read(address.Db,address.Offset,1),0);
            // Persist intent BEFORE sending a write, so an audit failure prevents an unaudited write.
            File.AppendAllText(auditPath,json.Serialize(Map("timestamp_utc",Stamp(),"session_id",sessionId,"target",ip,"operator_classification",targetMode,"name",name,"address",tag.Address,"previous",previous,"requested",desired,"stage","intent"))+Environment.NewLine);
            bool sent=false;
            try
            {
                connection.WriteBool(address,(bool)desired);sent=true;
                object observed=address.Decode(connection.Read(address.Db,address.Offset,1),0);
                bool matched=object.Equals(desired,observed);
                var result=Map("timestamp_utc",Stamp(),"session_id",sessionId,"name",name,"previous",previous,"requested",desired,"observed",observed,"matched_at_readback",matched,"stage","completed","write_acknowledged",true);
                File.AppendAllText(auditPath,json.Serialize(result)+Environment.NewLine);
                if(name=="test.enable")
                {
                    if((bool)desired&&matched)StartLease(leaseSeconds);
                    else if(leaseCancel!=null)leaseCancel.Cancel();
                    result["lease_expires_utc"]=(bool)desired&&matched?(object)leaseUntil.ToString("o"):null;
                }
                return Result(matched?"仿真测试变量已写入并回读一致。":"写入已被 CPU 确认，但回读不同；变量可能被程序覆盖。未自动重试。",result,!matched);
            }
            catch(Exception ex)
            {
                try{File.AppendAllText(auditPath,json.Serialize(Map("timestamp_utc",Stamp(),"stage","failed","write_acknowledged",sent,"error",ex.Message))+Environment.NewLine);}catch{}
                Disconnect();throw new IOException("Write/readback failed; actual state may have changed. Reconnect and read before another write. "+ex.Message,ex);
            }
        }
        private void StartLease(int seconds)
        {
            // Called under gate. Existing lease is cancelled and guarded by a unique token.
            if(leaseCancel!=null)leaseCancel.Cancel();
            leaseCancel=new CancellationTokenSource();var cancel=leaseCancel;var token=cancel.Token;string id=sessionId;int testDb=profile.EffectiveTestDb;
            leaseUntil=DateTime.UtcNow.AddSeconds(seconds);DateTime until=leaseUntil;
            leaseTask=Task.Run(delegate
            {
                bool beat=false;
                try
                {
                    while(!token.WaitHandle.WaitOne(1000)&&DateTime.UtcNow<until)
                    {
                        lock(gate)
                        {
                            if(connection==null||sessionId!=id||!writesEnabled||leaseCancel!=cancel)return;
                            beat=!beat;connection.WriteBool(PlcAddress.Parse("DB"+testDb+".DBX4.1","Bool"),beat);
                        }
                    }
                }
                catch(Exception ex){lock(gate){lastError="Test heartbeat failed; PLC watchdog expires within 5s: "+ex.Message;Disconnect();}}
                finally
                {
                    lock(gate)
                    {
                        if(connection!=null&&sessionId==id&&writesEnabled&&leaseCancel==cancel)
                        {
                            try
                            {
                                connection.WriteBool(PlcAddress.Parse("DB"+testDb+".DBX4.0","Bool"),false);
                                File.AppendAllText(Path.Combine(root,"online-records","writes.jsonl"),json.Serialize(Map("timestamp_utc",Stamp(),"session_id",id,"stage","lease_ended","requested_enable",false))+Environment.NewLine);
                            }
                            catch(Exception ex){lastError="Lease cleanup failed; PLC watchdog expires within 5s: "+ex.Message;Disconnect();}
                        }
                    }
                }
            });
        }
        private void StopLease()
        {
            Task task;lock(gate){if(leaseCancel!=null)leaseCancel.Cancel();task=leaseTask;}
            if(task!=null&&!task.Wait(35000))throw new TimeoutException("Test lease has not stopped; do not reconnect yet.");
        }
        private object TraceRead(Dictionary<string,object> args)
        {
            int limit=NumberBounded(args,"limit",100,1,256);
            int testDb=profile.EffectiveTestDb; uint expectedSig=profile.EffectiveTestSignature;
            for(int attempt=0;attempt<3;attempt++)
            {
                byte[] header=connection.Read(testDb,0,36);
                if(UInt32(header,0)!=expectedSig||S7Connection.Get16(header,24)!=1||S7Connection.Get16(header,26)!=256)throw new InvalidOperationException("DB"+testDb+" trace schema mismatch or not installed.");
                uint begin=UInt32(header,28),end=UInt32(header,32);
                if(begin!=end||(begin&1)!=0)continue;
                int count=unchecked((int)UInt32(header,12)),head=S7Connection.Get16(header,16);
                if(count<0||head>=256)throw new IOException("Invalid trace ring header.");
                int available=Math.Min(count,256),take=Math.Min(available,limit),first=(head-take+256)%256;
                var entries=new List<object>();
                for(int i=0;i<take;)
                {
                    int index=(first+i)%256,n=Math.Min(Math.Min(take-i,256-index),16);
                    byte[] block=connection.Read(testDb,36+index*12,n*12);
                    for(int j=0;j<n;j++)entries.Add(Map("order",unchecked((int)UInt32(block,j*12)),"cycle",UInt32(block,j*12+4),"step",unchecked((short)S7Connection.Get16(block,j*12+8)),"hmi_step",unchecked((short)S7Connection.Get16(block,j*12+10))));
                    i+=n;
                }
                byte[] after=connection.Read(testDb,28,8);
                if(UInt32(after,0)!=begin||UInt32(after,4)!=begin)continue;
                lastRead=DateTime.UtcNow;
                return Result("PLC 内每次调用后的工步变化记录；同次 GRAPH 调用内部的瞬时转换仍可能不出现。",Map("timestamp_utc",Stamp(),"consistent",true,"total_records_since_reset",count,"ring_capacity",256,"older_records_overwritten",Math.Max(0,count-256),"records_omitted_by_limit",available-take,"entries",entries.ToArray(),"capture","post_graph_call_step_changes","may_miss_intra_call_transitions",true));
            }
            throw new IOException("Trace changed during each read; no consistent snapshot returned. Retry with a smaller limit.");
        }
        private static uint UInt32(byte[] bytes,int offset){return ((uint)bytes[offset]<<24)|((uint)bytes[offset+1]<<16)|((uint)bytes[offset+2]<<8)|bytes[offset+3];}
        private object StartWatch(Dictionary<string,object> args)
        {
            if(watchRunning)throw new InvalidOperationException("Stop the existing watch before starting another.");
            string[] names=Names(args);foreach(string name in names)Resolve(name);
            int interval=NumberBounded(args,"interval_ms",200,50,10000),duration=NumberBounded(args,"duration_seconds",300,1,1800);
            watchId=Guid.NewGuid().ToString("N");Directory.CreateDirectory(Path.Combine(root,"online-records"));
            watchFile=Path.Combine(root,"online-records",DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+watchId+".jsonl");
            watchNames=names;samples=changes=0;events.Clear();watchCancel=new CancellationTokenSource();var token=watchCancel.Token;
            File.WriteAllText(watchFile,json.Serialize(Map("kind","metadata","watch_id",watchId,"target",ip,"session_id",sessionId,"started_utc",Stamp(),"interval_ms",interval,"capture","sampled_changes","may_miss_fast_steps",true,"profile",profile.Name))+Environment.NewLine);
            watchRunning=true;
            watchTask=Task.Run(delegate
            {
                var lastValues=new Dictionary<string,string>();var until=DateTime.UtcNow.AddSeconds(duration);
                try
                {
                    while(!token.IsCancellationRequested&&DateTime.UtcNow<until)
                    {
                        lock(gate)
                        {
                            if(connection==null)throw new IOException("Online connection lost.");
                            var sample=Read(names);samples++;
                            var all=(Dictionary<string,object>)sample["values"];var changed=new Dictionary<string,object>();
                            foreach(var pair in all)
                            {
                                var row=(Dictionary<string,object>)pair.Value;
                                string fingerprint=json.Serialize(Map("value",row["value"],"quality",row["quality"]));string previous;
                                if(!lastValues.TryGetValue(pair.Key,out previous)||fingerprint!=previous){changed.Add(pair.Key,row);lastValues[pair.Key]=fingerprint;}
                            }
                            if(changed.Count>0)AppendEvent(Map("kind","change","timestamp_utc",Stamp(),"sample",samples,"values",changed));
                            if(!(bool)sample["complete"])throw new IOException("Watch stopped after a failed read; see variable qualities.");
                            if(changes>=100000)break;
                        }
                        if(token.WaitHandle.WaitOne(interval))break;
                    }
                }
                catch(Exception ex){lock(gate){lastError=ex.Message;try{AppendEvent(Map("kind","error","timestamp_utc",Stamp(),"error",ex.Message));}catch{}}}
                finally{lock(gate){watchRunning=false;try{AppendEvent(Map("kind","stopped","timestamp_utc",Stamp(),"samples",samples));}catch{}}}
            });
            return Result("后台变化记录已启动；采样记录可能漏掉快速步骤，不能作为完整执行轨迹。",WatchState());
        }
        private void AppendEvent(object entry)
        {File.AppendAllText(watchFile,json.Serialize(entry)+Environment.NewLine);events.Enqueue(entry);while(events.Count>500)events.Dequeue();changes++;}
        private object WatchState(){return Map("watch_id",watchId,"running",watchRunning,"samples",samples,"events",changes,"file",watchFile,"names",watchNames,"capture","sampled_changes","may_miss_fast_steps",true);}
        private object Status(){return Map("connected",connection!=null,"transport",transportName,"target",ip,"rack",rack,"slot",slot,"target_mode_operator_supplied",targetMode,"session_id",sessionId,"connected_at_utc",connection==null?null:connectedAt.ToString("o"),"last_successful_read_utc",lastRead==DateTime.MinValue?null:lastRead.ToString("o"),"simulation_writes_enabled",connection!=null&&writesEnabled,"test_lease_expires_utc",leaseUntil==DateTime.MinValue?null:leaseUntil.ToString("o"),"profile",profile,"last_error",lastError,"watch",WatchState(),"limitations",new[]{"Standard absolute DB access only; optimized DBs are not supported by this driver.","No forcing, program download, CPU mode control, direct output write, or safety bypass.","Hardware and TCP sessions are read-only.","Target simulation classification requires operator confirmation; not automatically proven."});}
        private void Disconnect(){readPlans.Clear();if(connection!=null){try{connection.Dispose();}catch{}connection=null;}writesEnabled=false;}
        private void StopWatch()
        {
            Task task;lock(gate){if(watchCancel!=null)watchCancel.Cancel();task=watchTask;}
            if(task!=null&&!task.Wait(35000))throw new TimeoutException("Watch has not stopped; do not reconnect yet.");
        }
        public void Dispose(){StopLease();StopWatch();lock(gate)Disconnect();}

        public static object[] Definitions()
        {
            var str=Map("type","string");var boolean=Map("type","boolean");var integer=Map("type","integer");
            var names=Map("type","array","minItems",1,"maxItems",128,"items",str);
            return new[]{
                Tool("tia_online_status","查看独立在线通道、质量、目标分类和记录状态，不读取实时变量。",Map(),new string[0],true),
                Tool("tia_online_connect","连接普通 PLCSIM 的 S7ONLINE 或实体/Advanced 的 S7 TCP。默认只读；绝对地址仅支持标准 DB。simulation 分类必须由操作者确认。",Map("transport",Map("type","string","enum",new[]{"s7online","s7tcp"}),"ip",str,"port",integer,"rack",integer,"slot",integer,"timeout_ms",integer,"profile_path",str,"target_mode",Map("type","string","enum",new[]{"simulation","hardware"}),"enable_simulation_writes",boolean,"confirm_simulation_target",boolean),new string[0],false),
                Tool("tia_online_disconnect","关闭在线读取及采样，不停止 CPU。",Map(),new string[0],false),
                Tool("tia_online_read","批量读取 profile 变量名或标准 DB 绝对地址；返回质量和采样时间，多组数据不是同一扫描快照。",Map("names",names),new[]{"names"},true),
                Tool("tia_online_graph_status","读取 OP270 GRAPH 步号、HMI工步、模式和启停请求，提供基于快照的诊断提示。",Map(),new string[0],true),
                Tool("tia_online_write_test","仅修改明确授权的仿真 DB32021 白名单 Bool 字段。enable=TRUE 时建立默认30秒、最多300秒心跳租期，到期关闭测试覆盖。校验签名、审计并回读。实体及 TCP 拒绝写入。",Map("name",str,"value",boolean,"confirm",boolean,"lease_seconds",integer),new[]{"name","value","confirm"},false),
                Tool("tia_online_trace_read","读取专用 PLC DB32021 工步环形记录（须先安装记录块），验证版本和一致性，报告旧记录覆盖及单次调用内部瞬时步限制。",Map("limit",integer),new string[0],true),
                Tool("tia_online_watch_start","后台记录指定变量的采样变化（50ms..10s）；快速工步可能遗漏。默认5分钟，最多30分钟。",Map("names",names,"interval_ms",integer,"duration_seconds",integer),new[]{"names"},false),
                Tool("tia_online_watch_read","读取最近采样变化和记录文件路径。",Map("limit",integer),new string[0],true),
                Tool("tia_online_watch_stop","停止采样记录并保留文件。",Map(),new string[0],false)
            };
        }
        private static Dictionary<string,object> Tool(string name,string description,object properties,string[] required,bool readOnly)
        {return Map("name",name,"description",description,"inputSchema",Map("type","object","properties",properties,"required",required,"additionalProperties",false),"annotations",Map("readOnlyHint",readOnly,"destructiveHint",name=="tia_online_write_test","openWorldHint",false));}
    }
}
