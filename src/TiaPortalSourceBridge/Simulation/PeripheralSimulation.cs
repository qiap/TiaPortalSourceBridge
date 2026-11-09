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
            var config=json.Deserialize<PeripheralConfiguration>(Text(args,"configuration_json"));PeripheralModel.Validate(config);
            lock(peripheralGate){
                var lease=AcquireSimulationMutation();try{var instance=Owned(args);if(EnumText(Api.Get(instance,"OperatingState"))!="Run")throw new InvalidOperationException("请先让受管仿真 CPU 进入 RUN，再开始外设规则。");Api.Call(instance,"UpdateTagList");
                foreach(string tag in config.rules.SelectMany(r=>new[]{r.command,r.feedback}).Concat(config.stimuli.Select(s=>s.tag)).Concat(config.assertions.Select(a=>a.tag)).Distinct())Api.Call(instance,"ReadBool",tag);
                peripheralProject=ProjectRouter.Normalize(Text(args,"project_path"));peripheralInstance=Text(args,"instance_name");string id=Text(args,"run_id");Guid parsed;if(id.Length==0)id=Guid.NewGuid().ToString("N");if(!Guid.TryParseExact(id,"N",out parsed))throw new ArgumentException("无效仿真任务编号。");string file=Path.Combine(root,"simulation-runs",id+".json");Directory.CreateDirectory(Path.GetDirectoryName(file));if(File.Exists(file))throw new InvalidOperationException("仿真编号已使用，请先查询原记录。");
                peripheralResult=Map("id",id,"project_path",peripheralProject,"instance_name",peripheralInstance,"state","running","failure_count",0,"configuration",config,"note","仅受管本机 Softbus 仿真；停止后保留仿真变量现场，不自动复位。不是硬件验证。","file",file);var result=peripheralResult;
                peripheralCancel=new CancellationTokenSource();var token=peripheralCancel.Token;var bound=new Dictionary<string,object>(args);
                Save(file,result);peripheralTask=Task.Run(()=>{
                    var model=new PeripheralModel(config);var clock=Stopwatch.StartNew();string state="completed",error="";int flushed=0;long saved=-1000;
                    Action flush=()=>{while(flushed<model.Evidence.Count){var row=model.Evidence[flushed++];if(Convert.ToString(row["kind"])!="write_intent")File.AppendAllText(file+"l",json.Serialize(row)+Environment.NewLine);}};
                    try{
                        while(true){
                            token.ThrowIfCancellationRequested();var current=Owned(bound);if(EnumText(Api.Get(current,"OperatingState"))!="Run")throw new InvalidOperationException("仿真 CPU 已离开 RUN，规则已停止。");
                            model.Tick(clock.ElapsedMilliseconds,tag=>Convert.ToBoolean(Api.Call(current,"ReadBool",tag)),(tag,value)=>{token.ThrowIfCancellationRequested();File.AppendAllText(file+"l",json.Serialize(Map("kind","write_intent","elapsed_ms",clock.ElapsedMilliseconds,"tag",tag,"value",value))+Environment.NewLine);Api.Call(current,"WriteBool",tag,value);});
                            flush();
                            lock(peripheralGate){result["elapsed_ms"]=clock.ElapsedMilliseconds;result["failure_count"]=model.Failures;result["assertions_completed"]=model.AssertionsCompleted;result["event_count"]=model.Evidence.Count;result["events"]=model.Evidence.Skip(Math.Max(0,model.Evidence.Count-500)).ToArray();if(clock.ElapsedMilliseconds-saved>=500){Save(file,result);saved=clock.ElapsedMilliseconds;}}
                            if(clock.ElapsedMilliseconds>=config.duration_ms)break;if(token.WaitHandle.WaitOne(config.interval_ms))token.ThrowIfCancellationRequested();
                        }
                        if(model.Failures>0)state="failed";
                    }catch(OperationCanceledException){state="stopped";}catch(Exception ex){state="failed";error=ex.Message;}
                    finally{try{lock(peripheralGate){result["state"]=state;result["error"]=error;result["failure_count"]=model.Failures;result["assertions_completed"]=model.AssertionsCompleted;result["event_count"]=model.Evidence.Count;result["events"]=model.Evidence.Skip(Math.Max(0,model.Evidence.Count-500)).ToArray();result["completed_utc"]=DateTime.UtcNow.ToString("o");try{flush();Save(file,result);}catch(Exception persistence){result["state"]="failed";result["error"]="记录保存失败："+persistence.Message;}}}finally{lease.Dispose();}}
                });return Map("id",id,"state","running","instance_name",peripheralInstance); }catch{lease.Dispose();throw;}
            }
        }
        internal object PeripheralStatus(Dictionary<string,object> args,bool stop)
        {
            lock(peripheralGate){if(peripheralResult==null||Text(args,"run_id")!=Convert.ToString(peripheralResult["id"])){
                    if(stop)throw new InvalidOperationException("当前进程没有此运行中的任务，请查询记录并核对实例。");Guid id;if(!Guid.TryParseExact(Text(args,"run_id"),"N",out id))throw new ArgumentException("无效仿真任务编号。");string file=TiaPortalSourceBridge.Web.SnapshotDiff.Confined(Path.Combine(root,"simulation-runs"),id.ToString("N")+".json");if(!File.Exists(file)||new FileInfo(file).Length>16*1024*1024)throw new InvalidOperationException("仿真记录不存在或过大。");var record=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(file));if(Text(record,"project_path")!=ProjectRouter.Normalize(Text(args,"project_path"))||Text(record,"instance_name")!=Text(args,"instance_name"))throw new InvalidOperationException("仿真记录目标不匹配。");if(Text(record,"state")=="running"){record["state"]="unknown";record["error"]="原运行进程不可确认，请核对实例；不会自动重放。";}return record;
                }if(ProjectRouter.Normalize(Text(args,"project_path"))!=peripheralProject||Text(args,"instance_name")!=peripheralInstance)throw new InvalidOperationException("仿真任务身份不匹配。");if(stop&&peripheralCancel!=null)peripheralCancel.Cancel();return json.DeserializeObject(json.Serialize(peripheralResult));}
        }
        internal void StopPeripheral(){Task task;lock(peripheralGate){if(peripheralCancel!=null)peripheralCancel.Cancel();task=peripheralTask;}if(task!=null)task.Wait(30000);}
    }
}
