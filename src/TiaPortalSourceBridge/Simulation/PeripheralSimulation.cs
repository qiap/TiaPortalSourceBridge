using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TiaPortalSourceBridge.Mcp;

namespace TiaPortalSourceBridge.Simulation
{
    internal sealed partial class SimulationTools
    {
        private readonly object peripheralGate=new object();private CancellationTokenSource peripheralCancel;private Task peripheralTask;
        private Dictionary<string,object> peripheralResult;private string peripheralProject,peripheralInstance;
        private void SaveRun(string path,object value)
        {string temp=path+".tmp";File.WriteAllText(temp,json.Serialize(value),new System.Text.UTF8Encoding(false));if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);}
        private object PeripheralPage(Dictionary<string,object> args,Dictionary<string,object> record)
        {
            if(!args.ContainsKey("event_offset"))return record;
            int offset=Convert.ToInt32(args["event_offset"]),limit=args.ContainsKey("event_limit")?Convert.ToInt32(args["event_limit"]):200;
            if(offset<0||offset>20000||limit<1||limit>500)throw new ArgumentException("event_offset 为 0..20000，event_limit 为 1..500。");
            string file=TiaPortalSourceBridge.Web.SnapshotDiff.Confined(Path.Combine(root,"simulation-runs"),Text(record,"id")+".jsonl");
            if(File.Exists(file)&&new FileInfo(file).Length>32*1024*1024)throw new IOException("时间轴文件超限。");
            int total=record.ContainsKey("event_count")?Convert.ToInt32(record["event_count"]):0;
            var rows=File.Exists(file)?File.ReadLines(file).Take(total).Skip(offset).Take(limit).Select(line=>json.DeserializeObject(line)).ToArray():new object[0];
            record["events"]=rows;record["events_offset"]=offset;record["next_event_offset"]=offset+rows.Length<total?(object)(offset+rows.Length):null;record["events_truncated"]=offset>0||offset+rows.Length<total;return record;
        }
        internal void EnsurePeripheralIdle(){lock(peripheralGate)if(peripheralTask!=null&&!peripheralTask.IsCompleted)throw new InvalidOperationException("外设仿真仍在运行，请先停止规则运行，再控制实例。");}
        internal IDisposable AcquireSimulationMutation()
        {
            EnsurePeripheralIdle();
            string directory=Path.Combine(root,"simulation-sessions");Directory.CreateDirectory(directory);
            try{return new FileStream(Path.Combine(directory,"mutation.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
            catch(IOException ex){throw new InvalidOperationException("Another simulation operation is active. Stop or finish it before changing the instance.",ex);}
        }
        internal object StartPeripheral(Dictionary<string,object> args)
        {
            if(!Flag(args,"confirm"))throw new InvalidOperationException("请明确确认仅在受管本机仿真实例中写入激励与反馈。");
            if(TiaPortalSourceBridge.Configuration.AppConfig.DetectProjectVersion(Text(args,"project_path"))!="V17")throw new InvalidOperationException("外设规则目前仅支持 V17 本机仿真。");
            string configuration=Text(args,"configuration_json");if(configuration.Length>1024*1024)throw new ArgumentException("剧本配置超过 1 MiB。");
            var config=ScenarioModel.Parse(configuration);
            lock(peripheralGate){
                var lease=AcquireSimulationMutation();try{var instance=Owned(args);if(EnumText(Api.Get(instance,"OperatingState"))!="Run")throw new InvalidOperationException("请先让受管仿真 CPU 进入 RUN，再开始外设规则。");Api.Call(instance,"UpdateTagList");
                foreach(var tag in ScenarioModel.Tags(config))ScenarioModel.Value(tag.type,ReadValue(instance,tag.tag,tag.type));
                peripheralProject=ProjectRouter.Normalize(Text(args,"project_path"));peripheralInstance=Text(args,"instance_name");string id=Text(args,"run_id");Guid parsed;if(id.Length==0)id=Guid.NewGuid().ToString("N");if(!Guid.TryParseExact(id,"N",out parsed))throw new ArgumentException("无效仿真任务编号。");string file=Path.Combine(root,"simulation-runs",id+".json");Directory.CreateDirectory(Path.GetDirectoryName(file));if(File.Exists(file))throw new InvalidOperationException("仿真编号已使用，请先查询原记录。");
                using(var claim=new FileStream(file,FileMode.CreateNew,FileAccess.Write,FileShare.Read)){}
                peripheralResult=Map("id",id,"project_path",peripheralProject,"instance_name",peripheralInstance,"plc_name",Text(args,"plc_name"),"state","running","failure_count",0,"configuration",config,"note","仅受管本机 Softbus 仿真；20ms 是最小请求间隔，不是实时保证。单扫描脉冲需 PLC 锁存/计数，采样非原子；停止后保留现场，不自动复位。不是硬件验证。","file",file,"started_utc",DateTime.UtcNow.ToString("o"),"steps_total",config.steps.Length);var result=peripheralResult;
                peripheralCancel=new CancellationTokenSource();var token=peripheralCancel.Token;var bound=new Dictionary<string,object>(args);
                SaveRun(file,result);peripheralTask=Task.Run(()=>{
                    var model=new ScenarioModel(config);var clock=Stopwatch.StartNew();string state="completed",error="";int flushed=0,events=0;long saved=-1000,maxTick=0;
                    var recent=new Queue<Dictionary<string,object>>();
                    Action<Dictionary<string,object>> append=row=>{if(events>=20000)throw new IOException("时间轴达到 20000 条上限。");var copy=new Dictionary<string,object>(row);copy["event_index"]=events;copy["recorded_utc"]=DateTime.UtcNow.ToString("o");File.AppendAllText(file+"l",json.Serialize(copy)+Environment.NewLine);events++;recent.Enqueue(copy);while(recent.Count>500)recent.Dequeue();};
                    Action flush=()=>{while(flushed<model.Events.Count){var row=model.Events[flushed];if(Convert.ToString(row["kind"])!="write_intent")append(row);flushed++;}};
                    Action update=()=>{result["elapsed_ms"]=clock.ElapsedMilliseconds;result["max_tick_ms"]=maxTick;result["failure_count"]=state=="failed"?Math.Max(1,model.Failures):model.Failures;result["assertions_completed"]=model.AssertionsCompleted;result["steps_completed"]=model.StepIndex;result["event_count"]=events;result["events"]=recent.ToArray();result["events_offset"]=Math.Max(0,events-recent.Count);result["events_truncated"]=events>recent.Count;};
                    try{
                        while(true){
                            token.ThrowIfCancellationRequested();long start=clock.ElapsedMilliseconds;var current=Owned(bound);if(EnumText(Api.Get(current,"OperatingState"))!="Run")throw new InvalidOperationException("仿真 CPU 已离开 RUN，规则已停止。");
                            long sampled=clock.ElapsedMilliseconds;
                            model.Tick(sampled,tag=>ReadValue(current,tag.tag,tag.type),(tag,value)=>{token.ThrowIfCancellationRequested();if(clock.ElapsedMilliseconds-start>config.max_lateness_ms)throw new TimeoutException("采样耗时超过允许延迟，停止后续激励。");flush();append(Map("kind","write_intent","elapsed_ms",clock.ElapsedMilliseconds,"step_index",model.StepIndex,"tag",tag,"value",value));Api.Call(current,"WriteBool",tag,value);});
                            maxTick=Math.Max(maxTick,clock.ElapsedMilliseconds-start);
                            if(clock.ElapsedMilliseconds-start>config.max_lateness_ms)throw new TimeoutException("采样读取超时，本次结果不能确认为及时观察。");
                            flush();
                            lock(peripheralGate){update();if(clock.ElapsedMilliseconds-saved>=500){SaveRun(file,result);saved=clock.ElapsedMilliseconds;}}
                            if(model.Failure!=null){state="failed";error=model.Failure;break;}
                            if(sampled>=config.duration_ms){if(!model.Complete){state="failed";error="总时限内剧本未完成。";}break;}
                            int wait=(int)Math.Max(1,config.interval_ms-(clock.ElapsedMilliseconds-start));if(token.WaitHandle.WaitOne(wait))token.ThrowIfCancellationRequested();
                        }
                        if(model.Failures>0)state="failed";
                    }catch(OperationCanceledException){state="stopped";}catch(Exception ex){state="failed";error=ex.Message;}
                    finally{try{
                        object current=null;string snapshotError=null;try{current=Owned(bound);}catch(Exception ex){snapshotError=ex.Message;}
                        var snapshot=model.Snapshot(tag=>{if(current==null)throw new InvalidOperationException(snapshotError);return ReadValue(current,tag.tag,tag.type);});
                        lock(peripheralGate){result["state"]=state;result["error"]=error;result["completed_utc"]=DateTime.UtcNow.ToString("o");result["final_snapshot"]=snapshot;result["snapshot_complete"]=snapshot.All(r=>Equals(r["ok"],true));if(state=="failed")result["failure_snapshot"]=snapshot;result["automatic_retry"]=false;result["writes_reverted"]=false;
                            try{flush();append(Map("kind",state=="failed"?"failure_snapshot":"final_snapshot","elapsed_ms",clock.ElapsedMilliseconds,"values",snapshot));append(Map("kind","run_end","elapsed_ms",clock.ElapsedMilliseconds,"state",state,"error",error));update();SaveRun(file,result);}
                            catch(Exception persistence){result["state"]="failed";result["failure_count"]=Math.Max(1,model.Failures);result["error"]="记录保存失败："+persistence.Message;try{SaveRun(file,result);}catch{}}
                        }
                    }finally{lease.Dispose();}}
                });return Map("id",id,"state","running","instance_name",peripheralInstance); }catch{lease.Dispose();throw;}
            }
        }
        internal object PeripheralStatus(Dictionary<string,object> args,bool stop)
        {
            lock(peripheralGate){if(peripheralResult==null||Text(args,"run_id")!=Convert.ToString(peripheralResult["id"])){
                    if(stop)throw new InvalidOperationException("当前进程没有此运行中的任务，请查询记录并核对实例。");Guid id;if(!Guid.TryParseExact(Text(args,"run_id"),"N",out id))throw new ArgumentException("无效仿真任务编号。");string file=TiaPortalSourceBridge.Web.SnapshotDiff.Confined(Path.Combine(root,"simulation-runs"),id.ToString("N")+".json");if(!File.Exists(file)||new FileInfo(file).Length>16*1024*1024)throw new InvalidOperationException("仿真记录不存在或过大。");var record=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(file));if(Text(record,"project_path")!=ProjectRouter.Normalize(Text(args,"project_path"))||Text(record,"instance_name")!=Text(args,"instance_name"))throw new InvalidOperationException("仿真记录目标不匹配。");if(Text(record,"state")=="running"){record["state"]="unknown";record["error"]="原运行进程不可确认，请核对实例；不会自动重放。";}return PeripheralPage(args,record);
                }if(ProjectRouter.Normalize(Text(args,"project_path"))!=peripheralProject||Text(args,"instance_name")!=peripheralInstance)throw new InvalidOperationException("仿真任务身份不匹配。");if(stop&&peripheralCancel!=null)peripheralCancel.Cancel();return PeripheralPage(args,json.Deserialize<Dictionary<string,object>>(json.Serialize(peripheralResult)));}
        }
        internal void StopPeripheral(){Task task;lock(peripheralGate){if(peripheralCancel!=null)peripheralCancel.Cancel();task=peripheralTask;}if(task!=null)task.Wait(30000);}
    }
}
