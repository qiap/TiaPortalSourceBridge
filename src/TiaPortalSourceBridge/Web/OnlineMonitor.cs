using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using TiaPortalSourceBridge.Mcp;
using TiaPortalSourceBridge.Online;

namespace TiaPortalSourceBridge.Web
{
    // The dashboard owns a separate read-only connection. No arbitrary MCP tool
    // names, profile paths, or write flags are accepted from HTTP callers.
    internal sealed class OnlineMonitor : IDisposable
    {
        private readonly string root;
        private readonly Func<string,Dictionary<string,object>,object> call;
        private readonly Action close;
        private string project,plc,session;
        private OnlineProfile profile;
        internal OnlineMonitor(string root,Func<string,Dictionary<string,object>,object> call=null,Action close=null)
        {
            this.root=root;
            if(call==null){var online=new OnlineTools(root);this.call=online.Call;this.close=online.Dispose;}else{this.call=call;this.close=close??(()=>{});}
        }
        private Dictionary<string,object> Invoke(string name,Dictionary<string,object> args)
        {
            var reply=(Dictionary<string,object>)call(name,args);var data=CylinderWritePipeline.Data(reply);
            if(CylinderWritePipeline.True(reply,"isError")&&!(name=="tia_online_read"&&data.ContainsKey("values")))throw new InvalidOperationException(CylinderWritePipeline.Error(reply));return data;
        }
        internal object Connect(string targetProject,string targetPlc,OnlineProfile requested,string transport,string mode)
        {
            if(requested==null||requested.Tags==null||requested.Tags.Length>128||requested.Tags.Any(t=>t==null||t.TestWritable))throw new ArgumentException("请选择 1–128 个只读变量，不允许在线写入标记。");
            OnlineTools.ValidateProfile(requested);
            if(transport!="s7online"&&transport!="s7tcp")throw new ArgumentException("请选择 S7ONLINE 或 S7 TCP。");
            if(mode!="hardware"&&mode!="simulation")throw new ArgumentException("请选择实际 PLC 或仿真目标。");
            if(!string.IsNullOrEmpty(session))throw new InvalidOperationException("已有监控连接，请先断开后再切换目标。");
            string id=Guid.NewGuid().ToString("N"),dir=Path.Combine(root,"online-records","profiles");Directory.CreateDirectory(dir);string path=Path.Combine(dir,id+".json");
            File.WriteAllText(path,CylinderWritePipeline.Json().Serialize(requested),new UTF8Encoding(false));
            var status=Invoke("tia_online_connect",new Dictionary<string,object>{{"profile_path",path},{"transport",transport},{"target_mode",mode},{"enable_simulation_writes",false}});
            project=Path.GetFullPath(targetProject);plc=targetPlc;profile=requested;session=id;
            return new{session_id=id,status,project_path=project,plc_name=plc,read_only=true};
        }
        private void Target(string targetProject,string targetPlc,string id)
        {
            if(session==null||id!=session||!string.Equals(project,Path.GetFullPath(targetProject),StringComparison.OrdinalIgnoreCase)||plc!=targetPlc)throw new InvalidOperationException("在线监控会话不属于当前工程或 PLC，请重新连接。");
        }
        internal object Execute(string action,string targetProject,string targetPlc,string id,Dictionary<string,object> parameters)
        {
            Target(targetProject,targetPlc,id);var args=new Dictionary<string,object>();
            if(action=="disconnect"){var result=Invoke("tia_online_disconnect",args);session=null;profile=null;return result;}
            if(action=="status")return Invoke("tia_online_status",args);
            if(action=="stop")return Invoke("tia_online_watch_stop",args);
            if(action=="events")return Invoke("tia_online_watch_read",new Dictionary<string,object>{{"limit",500}});
            args["names"]=profile.Tags.Select(t=>t.Name).ToArray();
            if(action=="start"){
                object interval,duration;
                if(!parameters.TryGetValue("interval_ms",out interval)||!(interval is int)||(int)interval<50||(int)interval>10000||!parameters.TryGetValue("duration_seconds",out duration)||!(duration is int)||(int)duration<1||(int)duration>1800)throw new ArgumentException("采样间隔为 50–10000 毫秒，时长为 1–1800 秒。");
                args["interval_ms"]=interval;args["duration_seconds"]=duration;return Invoke("tia_online_watch_start",args);
            }
            if(action=="read"||action=="snapshot"){
                var data=Invoke("tia_online_read",args);if(action=="read")return data;
                if(!CylinderWritePipeline.True(data,"complete"))throw new InvalidOperationException("部分变量读取失败，不能保存为完整参数快照。");
                string snapshotId=Guid.NewGuid().ToString("N");var record=new Dictionary<string,object>{{"schema",1},{"id",snapshotId},{"project_path",project},{"plc_name",plc},{"created_utc",DateTime.UtcNow.ToString("o")},{"profile",profile},{"sample",data},{"atomic",false},{"note","按明确的绝对 DB 地址读取；各组可能来自不同扫描周期。名称由用户指定，不能证明在线符号或工程版本一致。"}};
                string dir=Path.Combine(root,"parameter-snapshots");Directory.CreateDirectory(dir);File.WriteAllText(Path.Combine(dir,snapshotId+".json"),CylinderWritePipeline.Json().Serialize(record),new UTF8Encoding(false));return record;
            }
            throw new ArgumentException("不支持的只读监控操作。");
        }
        internal byte[] Export(string targetProject,string targetPlc,string id)
        {
            Target(targetProject,targetPlc,id);var status=Invoke("tia_online_watch_read",new Dictionary<string,object>{{"limit",1}});var watch=CylinderWritePipeline.Map(status["watch"]);
            if(CylinderWritePipeline.True(watch,"running"))throw new InvalidOperationException("请先停止录波，再导出完整记录。");
            string file=CylinderWritePipeline.Text(watch,"file"),folder=Path.GetFullPath(Path.Combine(root,"online-records"))+Path.DirectorySeparatorChar;
            string full=Path.GetFullPath(file);if(!full.StartsWith(folder,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("录波记录路径无效。");
            full=SnapshotDiff.Confined(folder,full.Substring(folder.Length));if(new FileInfo(full).Length>64*1024*1024)throw new InvalidOperationException("录波记录超过 64 MiB，请缩短下一次采集时长。");return File.ReadAllBytes(full);
        }
        public void Dispose(){close();session=null;profile=null;}
    }
}
