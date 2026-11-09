using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaPortalSourceBridge.Simulation
{
    internal sealed class PeripheralRule
    {
        public string command {get;set;} public string feedback {get;set;} public int on_delay_ms {get;set;} public int off_delay_ms {get;set;}
        public bool invert {get;set;} public bool stuck {get;set;} public bool stuck_value {get;set;}
    }
    internal sealed class SimulationStimulus {public int at_ms {get;set;} public string tag {get;set;} public bool value {get;set;}}
    internal sealed class SimulationAssertion {public int at_ms {get;set;} public string tag {get;set;} public bool expected {get;set;}}
    internal sealed class PeripheralConfiguration
    {
        public int duration_ms {get;set;} public int interval_ms {get;set;} public int max_lateness_ms {get;set;}
        public PeripheralRule[] rules {get;set;} public SimulationStimulus[] stimuli {get;set;} public SimulationAssertion[] assertions {get;set;}
        public PeripheralConfiguration(){duration_ms=10000;interval_ms=50;max_lateness_ms=500;rules=new PeripheralRule[0];stimuli=new SimulationStimulus[0];assertions=new SimulationAssertion[0];}
    }
    // Deterministic tick model with injected clock/read/write functions. It has no
    // transport and cannot connect to hardware; only its owned Softbus adapter writes.
    internal sealed class PeripheralModel
    {
        private sealed class State {internal bool? Command,Written;internal long Changed;}
        private readonly PeripheralConfiguration config;private readonly State[] states;private readonly HashSet<int> stimuli=new HashSet<int>(),assertions=new HashSet<int>();private long last=-1;
        internal readonly List<Dictionary<string,object>> Evidence=new List<Dictionary<string,object>>();
        internal int Failures {get;private set;}
        internal static void Validate(PeripheralConfiguration config)
        {
            if(config==null||config.rules==null||config.stimuli==null||config.assertions==null||config.rules.Length>32||config.stimuli.Length>1000||config.assertions.Length>1000||config.rules.Length+config.stimuli.Length+config.assertions.Length==0)throw new ArgumentException("请配置最多 32 条外设规则、1000 个激励和 1000 个检查点。");
            if(config.duration_ms<100||config.duration_ms>1800000||config.interval_ms<20||config.interval_ms>1000||config.max_lateness_ms<config.interval_ms||config.max_lateness_ms>10000)throw new ArgumentException("运行时长、采样间隔或允许延迟超出范围。");
            Func<string,bool> valid=s=>!string.IsNullOrWhiteSpace(s)&&s.Length<=512&&!s.Any(char.IsControl);
            if(config.rules.Any(r=>r==null||!valid(r.command)||!valid(r.feedback)||r.on_delay_ms<0||r.on_delay_ms>60000||r.off_delay_ms<0||r.off_delay_ms>60000))throw new ArgumentException("外设规则名称或延时无效。");
            var outputs=new HashSet<string>(config.rules.Select(r=>r.feedback),StringComparer.OrdinalIgnoreCase);
            if(outputs.Count!=config.rules.Length||config.rules.Any(r=>outputs.Contains(r.command)))throw new ArgumentException("反馈不能多处写入，也不能形成反馈到命令的循环。");
            if(config.stimuli.Any(s=>s==null||!valid(s.tag)||s.at_ms<0||s.at_ms>config.duration_ms||outputs.Contains(s.tag))||config.stimuli.GroupBy(s=>s.at_ms+"\n"+s.tag,StringComparer.OrdinalIgnoreCase).Any(g=>g.Count()>1))throw new ArgumentException("激励时刻、变量或重复写入无效；不能同时激励外设反馈变量。");
            if(config.assertions.Any(s=>s==null||!valid(s.tag)||s.at_ms<0||s.at_ms>config.duration_ms))throw new ArgumentException("检查点变量或时刻无效。");
        }
        internal PeripheralModel(PeripheralConfiguration config){Validate(config);this.config=config;states=config.rules.Select(r=>new State()).ToArray();}
        private void Record(string kind,long time,string tag,object value,string reason=""){if(Evidence.Count>=20000)throw new InvalidOperationException("仿真证据达到 20000 条上限。");Evidence.Add(SimulationTools.Map("kind",kind,"elapsed_ms",time,"tag",tag,"value",value,"reason",reason));}
        internal void Tick(long elapsed,Func<string,bool> read,Action<string,bool> write)
        {
            if(elapsed<last)throw new ArgumentException("仿真时钟不能倒退。");last=elapsed;
            for(int i=0;i<config.stimuli.Length;i++){var item=config.stimuli[i];if(elapsed<item.at_ms||stimuli.Contains(i))continue;stimuli.Add(i);if(elapsed-item.at_ms>config.max_lateness_ms){Failures++;Record("late_stimulus",elapsed,item.tag,null,"激励错过时限，未补写");continue;}Record("write_intent",elapsed,item.tag,item.value);write(item.tag,item.value);Record("stimulus",elapsed,item.tag,item.value);}
            for(int i=0;i<config.rules.Length;i++){
                var rule=config.rules[i];var state=states[i];bool command=read(rule.command);
                if(state.Command!=command){state.Command=command;state.Changed=elapsed;Record("command",elapsed,rule.command,command);}
                bool desired=rule.stuck?rule.stuck_value:rule.invert?!command:command;int delay=command?rule.on_delay_ms:rule.off_delay_ms;
                if((rule.stuck||elapsed-state.Changed>=delay)&&state.Written!=desired){Record("write_intent",elapsed,rule.feedback,desired);write(rule.feedback,desired);state.Written=desired;Record("feedback",elapsed,rule.feedback,desired);}
            }
            for(int i=0;i<config.assertions.Length;i++){var item=config.assertions[i];if(elapsed<item.at_ms||assertions.Contains(i))continue;assertions.Add(i);bool actual=read(item.tag),late=elapsed-item.at_ms>config.max_lateness_ms;bool pass=!late&&actual==item.expected;if(!pass)Failures++;Record(pass?"pass":"fail",elapsed,item.tag,actual,late?"检查错过时限":"expected="+item.expected);}
        }
        internal int AssertionsCompleted {get{return assertions.Count;}}
    }
}
