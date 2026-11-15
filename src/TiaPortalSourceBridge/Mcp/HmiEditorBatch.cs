using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using TiaPortalSourceBridge.Openness;

namespace TiaPortalSourceBridge.Mcp
{
    internal sealed class HmiStageTimer
    {
        private readonly Stopwatch timer=Stopwatch.StartNew();
        private readonly Dictionary<string,double> finished=new Dictionary<string,double>();
        internal string Phase="checking";
        internal void Move(string phase) { finished[Phase]=finished.ContainsKey(Phase)?finished[Phase]+timer.Elapsed.TotalMilliseconds:timer.Elapsed.TotalMilliseconds;Phase=phase;timer.Restart(); }
        internal object Snapshot() { var copy=new Dictionary<string,double>(finished);copy[Phase]=(copy.ContainsKey(Phase)?copy[Phase]:0)+timer.Elapsed.TotalMilliseconds;return copy; }
    }
    internal static class HmiEditorBatch
    {
        internal static void Validate(HmiEditorRecord[] pages)
        {
            if(pages==null||pages.Length==0||pages.Length>20)throw new ArgumentException("每批需要 1–20 个页面。");
            var first=pages[0];if(first==null)throw new ArgumentException("页面方案缺失。");
            var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var page in pages) {
                if(page==null||page.pages!=null||page.project_path!=first.project_path||page.plc_name!=first.plc_name||page.hmi_name!=first.hmi_name)throw new ArgumentException("只能合并同一工程、PLC 和 HMI 的页面。");
                if(!seen.Add(page.kind+"|"+page.name))throw new ArgumentException("同一页面只能加入一次，请先合并该页字段修改。");
            }
        }
        // All live checks precede the single batch import; no automatic replay or rollback.
        internal static void Execute(HmiEditorRecord[] pages,Dictionary<string,object> result,
            Action<HmiEditorRecord> check,Func<Dictionary<string,object>> write,
            Func<Dictionary<string,object>> compile,Func<HmiEditorRecord,object[]> verify,Action save,Action<string> stage)
        {
            Validate(pages);stage("checking");foreach(var page in pages)check(page);
            stage("importing");result["mutation_started"]=true;var written=write();result["import_result"]=written;
            object mutation;if(written.TryGetValue("mutation_started",out mutation)&&mutation is bool&&!((bool)mutation))result["mutation_started"]=false;
            if(Convert.ToInt32(written["failure_count"])>0)throw new InvalidOperationException("页面预检查、导入或回读未通过；请查看逐项错误和备份。");
            bool changed=HmiOperations.NeedsCompile(written);result["changed_count"]=written["changed_count"];
            if(!changed){result["mutation_started"]=false;result["verified"]=true;result["unchanged"]=true;result["compile_skipped_reason"]="no_changes";result["save_skipped_reason"]="no_changes";return;}
            stage("compiling");var compiled=compile();foreach(var item in compiled)result[item.Key]=item.Value;result["compiled"]=true;
            if(Convert.ToInt32(compiled["error_count"])>0||!new[]{"Success","Warning"}.Contains(Convert.ToString(compiled["compile_state"])))throw new InvalidOperationException("HMI 编译未通过，尚未保存。");
            stage("verifying");var differences=new List<object>();
            foreach(var page in pages)foreach(var diff in verify(page))differences.Add(new{page=page.name,difference=diff});
            result["verification_differences"]=differences.ToArray();if(differences.Count>0)throw new InvalidOperationException("页面修改字段回读不一致，尚未保存。");
            result["verified"]=true;stage("saving");save();result["saved"]=true;
        }
    }
}
