using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace TiaPortalSourceBridge.Mcp
{
    internal sealed partial class McpServer
    {
        private Dictionary<string, object> ExecuteLoggedTool(string name, Dictionary<string, object> args)
        {
            // Only the client-facing server records the call. Project workers execute
            // the same RPC internally and would otherwise report it a second time.
            ExecutionLog log = null;
            if (_projects != null) {
                var target = new[] { StringArg(args, "project_path"), StringArg(args, "plc_name"),
                    StringArg(args, "block_name"), StringArg(args, "table_name") };
                log = ExecutionLog.Begin(_configPath, "MCP", name, string.Join(" · ", target.Where(s => !string.IsNullOrWhiteSpace(s))));
            }
            try {
                preparedIndexContext = null;
                InvalidateIndexBeforeMutation(name, args);
                var result = CallTool(name, args);
                AttachIndexHint(name, args, result);
                if (log != null) {
                    bool failed = Equals(Value(result, "isError"), true);
                    var data = Value(result, "structuredContent") as Dictionary<string, object>;
                    bool warnings = data != null && (Equals(Value(data, "needs_review"), true) ||
                        Convert.ToString(Value(data, "state")) == "needs_review" ||
                        (Value(data, "warnings") is ICollection && ((ICollection)data["warnings"]).Count > 0) ||
                        (Value(data, "warning_count") is int && (int)data["warning_count"] > 0));
                    string message = failed ? "工具返回错误" : warnings ? "调用完成，有警告需检查" : "调用完成";
                    if (failed) {
                        var content = Value(result, "content") as IEnumerable;
                        var first = content == null ? null : content.OfType<Dictionary<string, object>>().FirstOrDefault();
                        if (first != null && Value(first, "text") != null) message = Convert.ToString(first["text"]);
                    }
                    log.Update(failed ? "failed" : warnings ? "warning" : "completed", message, true);
                }
                return result;
            } catch (Exception ex) {
                string message = RootMessage(ex);
                if (log != null) log.Update(ex is TimeoutException ? "unknown" : "failed", message, true);
                return ToolResult("操作失败：" + message, true, null);
            }
        }
    }
}
