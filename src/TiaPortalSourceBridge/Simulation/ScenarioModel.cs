using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace TiaPortalSourceBridge.Simulation
{
    internal sealed class ScenarioTag { public string tag {get;set;} public string type {get;set;} }
    internal sealed class ScenarioCondition { public string tag {get;set;} public string type {get;set;} public object equals {get;set;} }
    internal sealed class ScenarioWrite { public string tag {get;set;} public bool value {get;set;} }
    internal sealed class ScenarioStep
    {
        public string name {get;set;} public int timeout_ms {get;set;} public int delay_ms {get;set;}
        public ScenarioCondition[] wait_until {get;set;} public ScenarioWrite[] writes {get;set;} public ScenarioCondition[] checks {get;set;}
        public ScenarioStep(){timeout_ms=5000;wait_until=new ScenarioCondition[0];writes=new ScenarioWrite[0];checks=new ScenarioCondition[0];}
    }
    // No expression evaluation and no transport. Conditions observe stable/latched PLC state;
    // sampling cannot establish that a one-scan pulse never occurred.
    internal sealed class ScenarioModel
    {
        readonly PeripheralConfiguration config;readonly PeripheralModel peripheral;
        readonly Dictionary<string,object> lastValues=new Dictionary<string,object>(StringComparer.OrdinalIgnoreCase);
        long previous=-1,entered,matched=-1;int copied;bool began;
        internal int StepIndex {get;private set;}
        internal bool Complete {get{return StepIndex==config.steps.Length;}}
        internal string Failure {get;private set;}
        internal readonly List<Dictionary<string,object>> Events=new List<Dictionary<string,object>>();
        internal int Failures {get{return peripheral.Failures+(Failure==null?0:1);}}
        internal int AssertionsCompleted {get{return peripheral.AssertionsCompleted;}}
        internal static bool ValidTag(string s){return !string.IsNullOrWhiteSpace(s)&&s.Length<=512&&!s.Any(char.IsControl);}
        internal static PeripheralConfiguration Parse(string text)
        {
            var json=new System.Web.Script.Serialization.JavaScriptSerializer{MaxJsonLength=1024*1024};
            ValidateShape(json.DeserializeObject(text),typeof(PeripheralConfiguration));
            var result=json.Deserialize<PeripheralConfiguration>(text);PeripheralModel.Validate(result);return result;
        }
        static void ValidateShape(object value,Type type)
        {
            if(type==typeof(object))return; // Typed condition values are checked by Value().
            if(type==typeof(string)){if(!(value is string))throw new ArgumentException("名称和类型必须为字符串。");return;}
            if(type==typeof(bool)){if(!(value is bool))throw new ArgumentException("布尔值必须为 true/false。");return;}
            if(type==typeof(int)){if(!(value is int))throw new ArgumentException("毫秒数必须为整数。");return;}
            if(type.IsArray){var rows=value as object[];if(rows==null)throw new ArgumentException("配置列表必须为数组。");foreach(var row in rows)ValidateShape(row,type.GetElementType());return;}
            var map=value as Dictionary<string,object>;if(map==null)throw new ArgumentException("配置项必须为对象。");
            var properties=type.GetProperties().ToDictionary(p=>p.Name,StringComparer.Ordinal);
            foreach(var pair in map){System.Reflection.PropertyInfo p;if(!properties.TryGetValue(pair.Key,out p))throw new ArgumentException("不支持的剧本字段："+pair.Key+"，不会静默忽略。");ValidateShape(pair.Value,p.PropertyType);}
            if(type==typeof(ScenarioWrite)&&(!map.ContainsKey("tag")||!map.ContainsKey("value")))throw new ArgumentException("步骤写入必须明确指定 tag 和 value。");
            if(type==typeof(ScenarioCondition)&&new[]{"tag","type","equals"}.Any(k=>!map.ContainsKey(k)))throw new ArgumentException("步骤条件必须指定 tag/type/equals。");
        }
        internal static object Value(string type,object value)
        {
            if(type=="bool"){if(!(value is bool))throw new ArgumentException("Bool 条件必须使用 JSON true/false。");return value;}
            if(value==null||value is bool||value is string)throw new ArgumentException("数值条件必须使用 JSON 数字。");
            double number=Convert.ToDouble(value,CultureInfo.InvariantCulture);
            if(double.IsNaN(number)||double.IsInfinity(number))throw new ArgumentException("不支持非有限数值。");
            if(type=="real"){float f=Convert.ToSingle(value,CultureInfo.InvariantCulture);if(float.IsInfinity(f))throw new ArgumentException("Real 超出范围。");return f;}
            if(number!=Math.Truncate(number))throw new ArgumentException("整数条件不能含小数。");
            if(type=="dint"||type=="time")return Convert.ToInt32(value,CultureInfo.InvariantCulture);
            if(type=="udint")return Convert.ToUInt32(value,CultureInfo.InvariantCulture);
            throw new ArgumentException("条件类型仅支持 bool/dint/udint/time/real。");
        }
        internal static ScenarioTag[] Tags(PeripheralConfiguration c)
        {
            return c.watch.Concat(c.steps.SelectMany(s=>s.wait_until.Concat(s.checks)).Select(p=>new ScenarioTag{tag=p.tag,type=p.type}))
                .Concat(c.rules.SelectMany(r=>new[]{r.command,r.feedback}).Concat(c.stimuli.Select(s=>s.tag)).Concat(c.assertions.Select(a=>a.tag)).Concat(c.steps.SelectMany(s=>s.writes).Select(w=>w.tag)).Select(t=>new ScenarioTag{tag=t,type="bool"}))
                .GroupBy(t=>t.tag,StringComparer.OrdinalIgnoreCase).Select(g=>{if(g.Select(t=>t.type).Distinct().Count()!=1)throw new ArgumentException("同一变量使用了不同类型："+g.Key);return g.First();}).ToArray();
        }
        internal static void Validate(PeripheralConfiguration c)
        {
            if(c.steps==null||c.watch==null||c.steps.Length>200||c.watch.Length>128)throw new ArgumentException("剧本最多 200 步、128 个监视变量。");
            var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var occupied=new HashSet<string>(c.rules.Select(r=>r.feedback).Concat(c.stimuli.Select(s=>s.tag)),StringComparer.OrdinalIgnoreCase);
            foreach(var s in c.steps){
                if(s==null||!ValidTag(s.name)||!names.Add(s.name)||s.wait_until==null||s.writes==null||s.checks==null||s.wait_until.Length>32||s.writes.Length>32||s.checks.Length>32||s.timeout_ms<c.interval_ms||s.timeout_ms>c.duration_ms||s.delay_ms<0||s.delay_ms>s.timeout_ms)throw new ArgumentException("剧本步骤名称、时间或动作数无效。");
                if(s.writes.Any(w=>w==null||!ValidTag(w.tag)||occupied.Contains(w.tag))||s.writes.Select(w=>w.tag).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=s.writes.Length)throw new ArgumentException("步骤不能重复写同一点，也不能与外设反馈或定时激励抢写。");
                foreach(var p in s.wait_until.Concat(s.checks)){if(p==null||!ValidTag(p.tag))throw new ArgumentException("条件变量无效。");Value(p.type,p.equals);}
            }
            foreach(var t in c.watch)if(t==null||!ValidTag(t.tag)||!new[]{"bool","dint","udint","time","real"}.Contains(t.type))throw new ArgumentException("监视变量或类型无效。");
            if(Tags(c).Length>128)throw new ArgumentException("剧本涉及的所有变量合计最多 128 个。");
        }
        internal ScenarioModel(PeripheralConfiguration config){PeripheralModel.Validate(config);this.config=config;peripheral=new PeripheralModel(config);}
        void Event(string kind,long time,params object[] data){if(Events.Count>=20000)throw new InvalidOperationException("剧本时间轴达到 20000 条，已停止。");var row=SimulationTools.Map("kind",kind,"elapsed_ms",time,"step_index",StepIndex,"step_name",Complete?"":config.steps[StepIndex].name);for(int i=0;i<data.Length;i+=2)row.Add((string)data[i],data[i+1]);Events.Add(row);}
        void Fail(string reason,long time){Failure=reason;Event("scenario_failed",time,"reason",reason);}
        bool Matches(ScenarioCondition p,Dictionary<string,object> sample){return Equals(sample[p.tag],Value(p.type,p.equals));}
        internal void Tick(long elapsed,Func<ScenarioTag,object> read,Action<string,bool> write)
        {
            if(Failure!=null)return;
            if(elapsed<previous)throw new ArgumentException("时钟不能倒退。");
            if(previous>=0&&elapsed-previous>config.max_lateness_ms){Fail("采样中断超过允许延迟，未补写错过的激励。",elapsed);return;}
            previous=elapsed;
            // Finish by the configured deadline without replaying overdue writes on a late wakeup.
            if(elapsed>config.duration_ms+config.max_lateness_ms){Fail("运行已超过总时限。",elapsed);return;}
            peripheral.Tick(elapsed,tag=>Convert.ToBoolean(read(new ScenarioTag{tag=tag,type="bool"})),write);
            while(copied<peripheral.Evidence.Count){if(Events.Count>=20000)throw new InvalidOperationException("时间轴已满。");Events.Add(peripheral.Evidence[copied++]);}
            var sample=new Dictionary<string,object>(StringComparer.OrdinalIgnoreCase);
            foreach(var tag in Tags(config)){object value=Value(tag.type,read(tag));sample[tag.tag]=value;object last;if(!lastValues.TryGetValue(tag.tag,out last)||!Equals(last,value)){Event("value_change",elapsed,"tag",tag.tag,"type",tag.type,"value",value);lastValues[tag.tag]=value;}}
            if(peripheral.Failures>0){Fail("外设检查失败或错过激励时限。",elapsed);return;}
            if(Complete)return;
            var step=config.steps[StepIndex];
            if(!began){entered=elapsed;began=true;Event("step_enter",elapsed);}
            if(elapsed-entered>step.timeout_ms){Fail("等待步骤超时："+step.name,elapsed);return;}
            if(matched<0&&step.wait_until.All(p=>Matches(p,sample))){matched=elapsed;Event("condition_met",elapsed);}
            if(matched<0||elapsed-matched<step.delay_ms){if(elapsed>=config.duration_ms)Fail("总时限内未完成步骤："+step.name,elapsed);return;}
            foreach(var p in step.checks){bool pass=Matches(p,sample);Event(pass?"check_pass":"check_fail",elapsed,"tag",p.tag,"actual",sample[p.tag],"expected",p.equals);if(!pass){Fail("步骤断言失败："+step.name+" / "+p.tag,elapsed);return;}}
            foreach(var w in step.writes){Event("write_intent",elapsed,"tag",w.tag,"value",w.value);write(w.tag,w.value);Event("step_write",elapsed,"tag",w.tag,"value",w.value);}
            Event("step_complete",elapsed);StepIndex++;matched=-1;entered=elapsed;began=true;
            if(!Complete)Event("step_enter",elapsed);
            if(elapsed>=config.duration_ms&&!Complete)Fail("总时限内剧本未完成。",elapsed);
        }
        internal Dictionary<string,object>[] Snapshot(Func<ScenarioTag,object> read)
        {
            return Tags(config).Select(tag=>{var row=SimulationTools.Map("tag",tag.tag,"type",tag.type,"read_utc",DateTime.UtcNow.ToString("o"));try{row["value"]=Value(tag.type,read(tag));row["ok"]=true;}catch(Exception ex){row["ok"]=false;row["error"]=ex.Message;}return row;}).ToArray();
        }
    }
}
