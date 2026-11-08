using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using TiaPortalSourceBridge.Indexing;

namespace TiaPortalSourceBridge.Web
{
    internal sealed class AuditCallGraph
    {
        public List<AuditCallNode> nodes { get; set; }
        public List<AuditCallSite> calls { get; set; }
        public bool partial { get; set; }
        public bool truncated { get; set; }
        public int calls_total { get; set; }
        public int calls_resolved { get; set; }
        public int instances_resolved { get; set; }
        public List<string> limitations { get; set; }
        public AuditCallGraph() { nodes = new List<AuditCallNode>(); calls = new List<AuditCallSite>(); limitations = new List<string>(); }
    }
    internal sealed class AuditCallNode
    {
        public string id { get; set; }
        public string name { get; set; }
        public string group { get; set; }
        public string unit { get; set; }
        public string kind { get; set; }
        public string source_status { get; set; }
        public string status { get; set; }
        public string root_id { get; set; }
        public List<string> path { get; set; }
        public string note { get; set; }
        public AuditCallNode() { path = new List<string>(); root_id = ""; note = ""; status = "unknown"; }
    }
    internal sealed class AuditCallSite
    {
        public string id { get; set; }
        public string caller_id { get; set; }
        public string callee_id { get; set; }
        public string callee_name { get; set; }
        public string callee_type { get; set; }
        public string instance { get; set; }
        public string instance_scope { get; set; }
        public string instance_status { get; set; }
        public string resolution { get; set; }
        public AuditLocation location { get; set; }
    }

    // All identities are resolved within the supplied source snapshot. No name
    // suggests a root or shared state unless its native declaration agrees.
    internal static class CallGraphAudit
    {
        internal const int MaxBlocks = 5000, MaxCalls = 20000, MaxPath = 64, MaxPathSteps = 128000, MaxEvidence = 100;
        private sealed class Block
        {
            internal IndexEntry Entry;
            internal AuditCallNode Node;
            internal string Id, Kind, InstanceOf = "", InstanceOfType = "";
            internal Dictionary<string, string> StaticTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            internal HashSet<string> DuplicateMembers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        private sealed class RawCall
        {
            internal Block Caller;
            internal AuditCallSite Site;
            internal string[] Components;
            internal string Namespace;
            internal bool DirectInstance, Builtin;
            internal string StateKey;
            internal Block Target;
            internal bool ShortTypeUnique;
        }
        private sealed class Context
        {
            internal readonly AuditCallGraph Graph = new AuditCallGraph();
            internal readonly List<Block> Blocks = new List<Block>();
            internal readonly List<RawCall> Raw = new List<RawCall>();
            internal readonly HashSet<string> Warnings = new HashSet<string>(StringComparer.Ordinal);
            internal Action<string> Warning;
            internal Action<AuditFinding> Finding;
            internal int PathSteps;
            internal void Warn(string text) { Graph.partial = true; if (Warnings.Contains(text)) return; if (Warnings.Count >= 200) { Limit(); return; } Warnings.Add(text); Warning(text); }
            internal void Limit() { Graph.partial = Graph.truncated = true; }
        }
        private static string N(string text) { return text ?? ""; }
        private static string A(XmlNode node, string name) { var e = node as XmlElement; return e == null ? "" : e.GetAttribute(name); }
        private static string Child(XmlNode node, string name) { var child = node.SelectSingleNode("*[local-name()='" + name + "']"); return child == null ? "" : child.InnerText; }
        private static string TypeName(string value)
        {
            value = N(value).Trim();
            return value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"' ? value.Substring(1, value.Length - 2).Replace("\"\"", "\"") : value;
        }
        private static bool Same(string a, string b) { return string.Equals(N(a), N(b), StringComparison.OrdinalIgnoreCase); }
        private static XmlDocument Parse(string xml)
        {
            var doc = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32L * 1024 * 1024 })) doc.Load(reader);
            return doc;
        }
        private static string Network(XmlNode call)
        {
            for (var p = call; p != null; p = p.ParentNode) if (p.LocalName == "SW.Blocks.CompileUnit") {
                int index = 1;
                for (var prior = p.PreviousSibling; prior != null; prior = prior.PreviousSibling) if (prior.LocalName == "SW.Blocks.CompileUnit") index++;
                return index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            return "";
        }
        private static bool InExecutableSource(XmlNode call)
        {
            for (var p = call.ParentNode; p != null; p = p.ParentNode)
                if (p.LocalName == "FlgNet" || p.LocalName == "StatementList" || p.LocalName == "StructuredText" || p.LocalName == "Graph") return true;
            return false;
        }
        private static bool DirectInstance(XmlNode instance, string[] components)
        {
            if (instance == null || components.Length != 1 || components[0].Length == 0) return false;
            return instance.ChildNodes.Cast<XmlNode>().Where(n => n is XmlElement).All(n => n.LocalName == "Component") &&
                instance.SelectNodes(".//*[local-name()='Component']").Cast<XmlNode>().All(c => !c.ChildNodes.Cast<XmlNode>().Any(n => n is XmlElement) && A(c, "AccessModifier").Length == 0 && A(c, "SliceAccessModifier").Length == 0);
        }
        private static void Extract(Context context, Block block, XmlNode root)
        {
            var attributes = root.SelectSingleNode("*[local-name()='AttributeList']");
            if (attributes == null) throw new InvalidOperationException("缺少块属性");
            if (!Same(Child(attributes, "Name"), block.Entry.name)) throw new InvalidOperationException("源码块名称与快照对象不符");
            block.InstanceOf = Child(attributes, "InstanceOfName"); block.InstanceOfType = Child(attributes, "InstanceOfType");
            foreach (XmlNode member in attributes.SelectNodes("*[local-name()='Interface']/*[local-name()='Sections']/*[local-name()='Section' and @Name='Static']/*[local-name()='Member']")) {
                string name = A(member, "Name");
                if (block.StaticTypes.ContainsKey(name)) block.DuplicateMembers.Add(name);
                else block.StaticTypes[name] = TypeName(A(member, "Datatype"));
            }
            if (block.Node == null) return;
            block.Node.source_status = "parsed";
            if (root.SelectSingleNode(".//*[local-name()='StructuredText' or local-name()='SourceText' or local-name()='Graph']") != null ||
                new[] { "SCL", "GRAPH" }.Any(l => Same(l, block.Entry.language))) {
                block.Node.source_status = "unsupported";
                block.Node.note = "仅提取显式 CallInfo；SCL/GRAPH 文本及内部控制流调用未完整解析。";
                context.Warn("调用覆盖不完整：" + block.Entry.name + "；" + block.Node.note);
            }
            int ordinal = 0;
            foreach (XmlNode call in root.SelectNodes(".//*[local-name()='CallInfo']")) {
                if (!InExecutableSource(call)) { context.Warn("未纳入非原生程序区的 CallInfo：" + block.Entry.name); continue; }
                ordinal++;
                AddCall(context, block, call, ordinal, false);
            }
            foreach (XmlNode part in root.SelectNodes(".//*[local-name()='FlgNet']/*[local-name()='Parts']/*[local-name()='Part'][*[local-name()='Instance']]")) {
                ordinal++;
                AddCall(context, block, part, ordinal, true);
            }
            if (root.SelectSingleNode(".//*[local-name()='SourceText']") != null) return;
            // Native STL may encode indirect block calls without a CallInfo.
            foreach (XmlNode statement in root.SelectNodes(".//*[local-name()='StlStatement']")) {
                var token = statement.SelectSingleNode("*[local-name()='StlToken']");
                if (new[] { "CALL", "UC", "CC" }.Any(t => Same(t, A(token, "Text"))) && statement.SelectSingleNode(".//*[local-name()='CallInfo']") == null)
                    context.Warn("STL 调用缺少可解析 CallInfo，未推断目标：" + block.Entry.name + " / 网络 " + Network(statement));
            }
        }
        private static void AddCall(Context context, Block caller, XmlNode call, int ordinal, bool builtin)
        {
            context.Graph.calls_total++;
            if (context.Raw.Count >= MaxCalls) { context.Limit(); return; }
            var instance = call.SelectSingleNode("*[local-name()='Instance']");
            var components = instance == null ? new string[0] : instance.SelectNodes("*[local-name()='Component']").Cast<XmlNode>().Select(n => A(n, "Name")).ToArray();
            string name = A(call, "Name"), type = builtin ? "instruction" : A(call, "BlockType"), network = Network(call);
            string scope = A(instance, "Scope"), symbol = (scope == "LocalVariable" ? "#" : "") + string.Join(".", components.Select(ContentIndexer.ComponentName));
            var site = new AuditCallSite { id = IndexJson.Hash(caller.Id + "\n" + ordinal), caller_id = caller.Id, callee_id = "", callee_name = name, callee_type = type,
                instance = instance == null ? "" : symbol, instance_scope = scope, instance_status = instance == null ? "none" : "unsupported", resolution = "unresolved",
                location = new AuditLocation { block = caller.Entry.name, group = caller.Entry.group, unit = caller.Entry.unit_name, network = network,
                    location = (network.Length == 0 ? "源码" : "网络 " + network) + " / 调用 " + ordinal, symbol = name, address = "", access = "call",
                    evidence = (builtin ? "原生指令 " : "CallInfo ") + type + " " + name + (instance == null ? "" : "；实例 " + symbol + "（" + scope + "）") } };
            context.Raw.Add(new RawCall { Caller = caller, Site = site, Components = components, Namespace = A(call, "Namespace"), DirectInstance = DirectInstance(instance, components), Builtin = builtin });
            context.Graph.calls.Add(site);
        }
        private static void Resolve(Context context, RawCall raw)
        {
            var site = raw.Site;
            if (raw.Builtin || new[] { "SFC", "SFB" }.Any(t => Same(t, site.callee_type))) site.resolution = "external";
            else if (site.callee_type == "FC" || site.callee_type == "FB") {
                var candidates = context.Blocks.Where(b => b.Node != null && Same(b.Entry.unit_name, raw.Caller.Entry.unit_name) && Same(b.Entry.name, site.callee_name) &&
                    (raw.Namespace.Length == 0 || Same(b.Entry.object_namespace, raw.Namespace)) && (b.Kind == site.callee_type || b.Kind == "unknown")).ToArray();
                if (candidates.Length == 1 && candidates[0].Kind == site.callee_type && candidates[0].Node.source_status != "unread") {
                    raw.Target = candidates[0]; site.resolution = "resolved"; site.callee_id = candidates[0].Id; context.Graph.calls_resolved++;
                    raw.ShortTypeUnique = context.Blocks.Count(b => b.Node != null && Same(b.Entry.unit_name, raw.Caller.Entry.unit_name) && Same(b.Entry.name, site.callee_name) && (b.Kind == "FB" || b.Kind == "unknown")) == 1;
                } else {
                    site.resolution = candidates.Length > 1 ? "ambiguous" : "unresolved";
                    context.Warn("调用目标未唯一解析：" + raw.Caller.Entry.name + " → " + site.callee_type + " " + site.callee_name + "；未跨软件单元或按块名猜测。");
                }
            } else context.Warn("调用类型未支持：" + raw.Caller.Entry.name + " → " + site.callee_type + " " + site.callee_name);
            if (site.callee_type != "FB" && site.callee_type != "SFB" && !raw.Builtin) return;
            if (site.callee_type == "FB" && raw.Target == null) { InstanceUnknown(context, raw, "FB 目标源码或类型未唯一确认"); return; }
            if (!raw.DirectInstance) { InstanceUnknown(context, raw, "缺少直接单成员实例，数组、结构路径和间接实例暂未解析"); return; }
            string member = raw.Components[0], declared = "";
            if (site.instance_scope == "LocalVariable") {
                if (raw.Caller.Kind != "FB" || !raw.Caller.StaticTypes.TryGetValue(member, out declared) || raw.Caller.DuplicateMembers.Contains(member)) {
                    InstanceUnknown(context, raw, "宿主 Static 中没有唯一实例声明"); return;
                }
                if (!TypeMatches(raw, declared, raw.Caller.Entry.object_namespace)) { InstanceUnknown(context, raw, "实例声明类型或命名空间与调用不匹配或未支持"); return; }
                raw.StateKey = "local\n" + raw.Caller.Id + "\n" + member.ToUpperInvariant();
            } else if (site.instance_scope == "GlobalVariable") {
                var dbs = context.Blocks.Where(b => b.Kind == "InstanceDB" && Same(b.Entry.unit_name, raw.Caller.Entry.unit_name) && Same(b.Entry.name, member)).ToArray();
                if (dbs.Length != 1 || !Same(dbs[0].InstanceOfType, "FB") || !TypeMatches(raw, dbs[0].InstanceOf, dbs[0].Entry.object_namespace)) {
                    InstanceUnknown(context, raw, "没有匹配且唯一的背景 DB 声明"); return;
                }
                raw.StateKey = "global\n" + dbs[0].Id;
            } else { InstanceUnknown(context, raw, "实例作用域未支持"); return; }
            site.instance_status = "resolved"; context.Graph.instances_resolved++;
        }
        private static bool TypeMatches(RawCall raw, string declared, string declarationNamespace)
        {
            if (!raw.Builtin && raw.Site.callee_type != "SFB") {
                if (raw.Target == null) return false;
                string targetNamespace = N(raw.Target.Entry.object_namespace);
                return raw.ShortTypeUnique && Same(declared, raw.Target.Entry.name) && Same(declarationNamespace, targetNamespace) ||
                    targetNamespace.Length > 0 && Same(declared, targetNamespace + "." + raw.Target.Entry.name);
            }
            if (new[] { "TON", "TOF", "TP" }.Any(n => Same(n, raw.Site.callee_name))) return Same(declared, "IEC_TIMER") || Same(declared, "IEC_LTIMER");
            if (new[] { "R_TRIG", "F_TRIG" }.Any(n => Same(n, raw.Site.callee_name))) return Same(declared, raw.Site.callee_name);
            return false;
        }
        private static void InstanceUnknown(Context context, RawCall raw, string reason)
        {
            raw.Site.instance_status = "unsupported";
            raw.Site.location.evidence += "；实例未核实：" + reason;
            context.Warn("实例复用检查未覆盖：" + raw.Caller.Entry.name + " / " + raw.Site.location.location + " / " + raw.Site.callee_name + "；" + reason + "。");
        }
        private static void Reachability(Context context)
        {
            var graph = context.Graph;
            var byId = graph.nodes.ToDictionary(n => n.id, StringComparer.Ordinal);
            var edges = graph.calls.Where(c => c.resolution == "resolved").ToLookup(c => c.caller_id);
            var queue = new Queue<AuditCallNode>();
            foreach (var root in graph.nodes.Where(n => n.kind == "OB" && n.source_status != "unread")) { root.status = "root"; root.root_id = root.id; root.path.Add(root.id); context.PathSteps++; queue.Enqueue(root); }
            bool hasRoot = queue.Count > 0;
            if (!hasRoot) context.Warn("当前快照没有可确认的 OB 入口，未判断程序块的 OB 可达路径。");
            while (queue.Count > 0) {
                var caller = queue.Dequeue();
                foreach (var edge in edges[caller.id]) {
                    AuditCallNode node; if (!byId.TryGetValue(edge.callee_id, out node) || node.status == "root" || node.status == "reachable") continue;
                    node.status = "reachable"; node.root_id = caller.root_id;
                    if (caller.path.Count >= MaxPath || caller.path.Count == 0 || context.PathSteps + caller.path.Count + 1 > MaxPathSteps) { context.Limit(); node.note += " 路径显示达到长度或总步数上限，仅标记可达，完整路径未展示。"; }
                    else { node.path.AddRange(caller.path); node.path.Add(node.id); context.PathSteps += node.path.Count; }
                    queue.Enqueue(node);
                }
            }
            foreach (var node in graph.nodes.Where(n => n.status == "unknown")) {
                if (!hasRoot || node.source_status != "parsed" || node.kind != "FB" && node.kind != "FC") {
                    node.note += " 读取或调用识别范围不足，未作无入口路径判断。"; continue;
                }
                node.status = "no_ob_path";
                node.note = "在当前已识别的显式调用关系中，未发现从任一已读取 OB 到此块的路径；不等于死代码或从未执行。";
                context.Finding(new AuditFinding { rule = "no_ob_path", severity = "info", confidence = "review", title = "已识别调用中未发现 OB 路径", subject = node.name,
                    explanation = node.note + (graph.partial ? "本次调用覆盖不完整，遗漏调用可能改变结论。" : "运行条件和入口是否启用未判断。"),
                    suggestion = "核对中断、启动等所有 OB、受保护或未读取块及尚未识别的调用；不要仅凭本项删除程序块。",
                    locations = new List<AuditLocation> { new AuditLocation { block = node.name, group = node.group, unit = node.unit, network = "", location = "程序块 / " + node.kind,
                        symbol = node.name, address = "", access = "call_path", evidence = "已读取 OB 入口 " + graph.nodes.Count(n => n.status == "root") + " 个；只沿已唯一解析的显式调用关系搜索。" } } });
            }
        }
        private static void ReusedInstances(Context context)
        {
            foreach (var group in context.Raw.Where(r => r.StateKey != null).GroupBy(r => r.StateKey, StringComparer.Ordinal).Where(g => g.Count() > 1)) {
                var first = group.First(); int count = group.Count();
                if (count > MaxEvidence) context.Limit();
                context.Finding(new AuditFinding { rule = "instance_reuse", severity = "info", confidence = "review", title = "同一状态实例存在多处调用", subject = first.Site.instance,
                    explanation = "根据实例声明，发现 " + count + " 个调用位置使用同一" + (first.Site.instance_scope == "LocalVariable" ? "宿主内的局部多重实例" : "全局背景 DB") + "。调用条件、执行顺序及不同 OB 的运行时调度未判断，不直接认定为冲突。",
                    suggestion = "核对是否有意复用状态，尤其是定时器和沿触发；本地实例按宿主作用域隔离，同名实例不自动视为相同。",
                    locations = group.Take(MaxEvidence).Select(r => { var l = r.Site.location; return new AuditLocation { block = l.block, group = l.group, unit = l.unit, network = l.network,
                        location = l.location, symbol = r.Site.instance, address = "", access = "instance_call", evidence = l.evidence + "；声明身份已核实" }; }).ToList() });
            }
        }
        internal static AuditCallGraph Run(IoSnapshot snapshot, Func<IndexEntry, string> source, Action<AuditFinding> addFinding, Action<string> warn)
        {
            if (snapshot == null || source == null || addFinding == null || warn == null) throw new ArgumentException("缺少调用分析输入。");
            var context = new Context { Warning = warn, Finding = addFinding };
            context.Graph.limitations.AddRange(new[] {
                "调用关系来自当前快照中的原生 CallInfo，以及带实例声明的受支持定时器/沿指令；不推断实际运行次数、调用条件或调度顺序。",
                "所有已确认的 OB 都作为候选入口；路径只展示一条显式路线，未发现路径不等于块未使用或可以删除。",
                "SCL/GRAPH 文本、间接调用、跨软件单元绑定、数组或结构路径实例尚未完整解析；沿触点的存储位不作为已验证 FB 实例。",
                "局部实例只在同一宿主 FB 内比较，不展开每个上层实例；全局背景 DB 必须有匹配的 InstanceDB 声明；同名不同命名空间的 FB 不按短类型名猜测实例。"
            });
            var entries = snapshot.cylinderBlocks ?? new IndexEntry[0];
            if (entries.Length > MaxBlocks) { context.Limit(); context.Warn("调用分析块数量超过上限，仅处理前 " + MaxBlocks + " 个快照对象。"); }
            if (snapshot.cylinderProgramsRead < snapshot.cylinderProgramsTotal) context.Warn("工程源码读取不完整，未读取的程序调用不在本次分析范围内。");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            int ordinal = 0;
            foreach (var entry in entries.Take(MaxBlocks).Where(e => e != null)) {
                ordinal++;
                string id = N(entry.id);
                if (id.Length == 0 || !ids.Add(id)) { id = IndexJson.Hash(entry.unit_name + "\n" + entry.group + "\n" + entry.name + "\n" + ordinal); ids.Add(id); context.Warn("快照对象身份缺失或重复，使用本次读取中的独立身份：" + entry.name); }
                var block = new Block { Entry = entry, Id = id, Kind = "unknown" };
                context.Blocks.Add(block);
                try {
                    string xml = source(entry); if (string.IsNullOrWhiteSpace(xml)) throw new InvalidOperationException("源码为空");
                    var doc = Parse(xml);
                    var roots = doc.SelectNodes("/*[local-name()='Document']/*[starts-with(local-name(),'SW.Blocks.') and local-name()!='SW.Blocks.CompileUnit']").Cast<XmlNode>().ToArray();
                    if (roots.Length != 1) throw new InvalidOperationException("未找到唯一原生块对象");
                    block.Kind = roots[0].LocalName.Substring("SW.Blocks.".Length);
                    if (new[] { "OB", "FB", "FC" }.Contains(block.Kind)) {
                        block.Node = new AuditCallNode { id = id, name = entry.name, group = entry.group, unit = entry.unit_name, kind = block.Kind, source_status = "parsed" };
                        context.Graph.nodes.Add(block.Node);
                    } else if (block.Kind != "InstanceDB" && block.Kind != "GlobalDB") context.Warn("调用分析未支持的块类型：" + entry.name + " / " + block.Kind);
                    Extract(context, block, roots[0]);
                } catch (Exception ex) {
                    if (block.Node == null && !Same(entry.language, "DB")) { block.Node = new AuditCallNode { id = id, name = entry.name, group = entry.group, unit = entry.unit_name, kind = "unknown" }; context.Graph.nodes.Add(block.Node); }
                    if (block.Node != null) { block.Node.source_status = "unread"; block.Node.note = "源码未完成解析：" + ex.Message; }
                    context.Warn("调用分析未读取对象：" + entry.name + "；" + ex.Message);
                }
            }
            foreach (var raw in context.Raw) Resolve(context, raw);
            ReusedInstances(context); Reachability(context);
            if (context.Graph.truncated) {
                string message = "调用报告达到上限：最多 " + MaxBlocks + " 个快照对象、" + MaxCalls + " 个调用、一条路径最多 " + MaxPath + " 个节点、路径总计 " + MaxPathSteps + " 步、每项复用最多 " + MaxEvidence + " 条证据、200 条诊断说明；结果不完整。";
                context.Graph.limitations.Add(message); warn(message);
            }
            return context.Graph;
        }
    }
}
