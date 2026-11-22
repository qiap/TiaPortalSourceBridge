using System;
using System.Collections.Generic;
using System.IO;
using TiaPortalSourceBridge.Configuration;
using TiaPortalSourceBridge.Web;

namespace TiaPortalSourceBridge.Mcp
{
    internal sealed partial class McpServer
    {
        private static bool IsRestrictedLogic(string name) { return name=="tia_read_logic_patch_plan" || name=="tia_write_logic_patch"; }
        private static void ValidateRestrictedTarget(Dictionary<string,object> args)
        {
            string project=StringArg(args,"project_path"),plc=StringArg(args,"plc_name");
            if(string.IsNullOrWhiteSpace(project)||!Path.IsPathRooted(project)||string.IsNullOrWhiteSpace(plc))throw new ArgumentException("受限修改必须明确指定工程绝对路径与 PLC 完整路径。");
            if(!PortalVersions.IsClassic(PortalVersions.FromProjectPath(project))||StringArg(args,"unit_name").Length>0)throw new ArgumentException("受限触点修改支持 V16–V20 普通 PLC 块中可识别的 LAD/GRAPH 网络。");
            if(StringArg(args,"read_mode").Length>0 && StringArg(args,"read_mode")!="live")throw new ArgumentException("修改预览与应用必须使用实时读取，不能使用缓存。");
        }
        private Dictionary<string,object> LogicPlanReply(LogicPlan p)
        {
            string receipt=Path.Combine(Path.GetDirectoryName(_configPath),"logs","logic-plans",p.id+".result.json");
            return ToolResult("受限修改方案：请核对触点变量及常开/常闭变化。应用前再次核对实时源码，不自动保存或下载。",false,
                Dict("plan_id",p.id,"project_path",p.project_path,"plc_name",p.plc_name,"block_name",p.block_name,"group_path",p.group_path,"created_utc",p.created_utc,
                    "source_kind","live_export_at_preview","before_signature",p.before_signature,"after_signature",p.after_signature,"edit",p.edit,
                    "contact_before",RestrictedLogic.Describe(p.before_xml,p.edit),"contact_after",RestrictedLogic.Describe(p.after_xml,p.edit),"diff",SemanticDiff.Compare(p.before_xml,p.after_xml),
                    "execution",File.Exists(receipt)?RestrictedLogic.Json().DeserializeObject(File.ReadAllText(receipt)):null,
                    "execution_state",File.Exists(receipt)?"recorded":File.Exists(Path.Combine(Path.GetDirectoryName(receipt),p.id+".claim"))?"needs_review_unknown_outcome":"not_started",
                    "limitations","Only the selected Contact operand/polarity changes. Compiler and full-block readback verification are required. No automatic retry, rollback, project save or PLC download."));
        }
        private static Dictionary<string,object> LogicPreviewSchema()
        {
            return ObjectSchema(new[]{"project_path","plc_name"},Property("project_path",StringProperty("V16–V20 工程绝对路径")),Property("plc_name",StringProperty("PLC 完整路径")),
                Property("block_name",StringProperty("预览时必填：现有 LAD 块或 GRAPH FB")),Property("group_path",StringProperty("精确组路径，根组用 /")),
                Property("edit_json",StringProperty("预览时必填：{compile_unit_id,contact_uid,scope,components:[成员名...],negated:true/false}。GRAPH 改用 {sequence_index:从0开始的序号,transition:转换编号,contact_uid,...}。components/scope 或 negated 至少提供一种；不支持数组、间接寻址和沿触点。")),
                Property("plan_id",StringProperty("可选：查询已持久保存的方案与执行结果，此时不连接博途")));
        }
        private static Dictionary<string,object> LogicApplySchema()
        {
            return ObjectSchema(new[]{"project_path","plc_name","plan_id","confirm"},Property("project_path",StringProperty("原方案的 V16–V20 工程绝对路径")),Property("plc_name",StringProperty("原方案 PLC 完整路径")),
                Property("plan_id",StringProperty("已审查的预览方案编号；源码变化则拒绝应用，同一方案不重复执行")),Property("confirm",BooleanProperty("确认把所审查的修改写入离线工程；不保存、不下载")));
        }
    }
}
