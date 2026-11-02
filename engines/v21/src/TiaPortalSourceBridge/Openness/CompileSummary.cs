using System.Collections.Generic;
using System.Text;

namespace TiaPortalSourceBridge.Openness
{
    internal sealed class CompileSummary
    {
        public string State { get; set; }
        public int ErrorCount { get; set; }
        public int WarningCount { get; set; }
        public List<CompileMessage> Messages { get; private set; }

        public CompileSummary()
        {
            State = "";
            Messages = new List<CompileMessage>();
        }

        public string ToReport()
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("编译状态: " + State);
            text.AppendLine("错误: " + ErrorCount + "；警告: " + WarningCount);
            foreach (CompileMessage message in Messages)
            {
                text.Append(new string(' ', message.Depth * 2));
                text.Append("[").Append(message.State).Append("] ");
                if (!string.IsNullOrWhiteSpace(message.Path)) text.Append(message.Path).Append(" - ");
                text.AppendLine(message.Description);
            }
            return text.ToString().TrimEnd();
        }
    }

    internal sealed class CompileMessage
    {
        public int Depth { get; set; }
        public string Path { get; set; }
        public string State { get; set; }
        public string Description { get; set; }
        public int ErrorCount { get; set; }
        public int WarningCount { get; set; }
    }
}
