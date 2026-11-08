using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using TiaPortalSourceBridge.Indexing;

namespace TiaPortalSourceBridge.Web
{
    internal sealed class AuditReport
    {
        public int schema { get; set; }
        public string id { get; set; }
        public string project_path { get; set; }
        public string plc_name { get; set; }
        public string snapshot_started { get; set; }
        public string snapshot_completed { get; set; }
        public string generated_at { get; set; }
        public List<AuditFinding> findings { get; set; }
        public List<string> warnings { get; set; }
        public Dictionary<string, object> coverage { get; set; }
        public AuditCallGraph call_graph { get; set; }
        public AuditReport() { schema = 2; findings = new List<AuditFinding>(); warnings = new List<string>(); coverage = new Dictionary<string, object>(); }
    }
    internal sealed class AuditFinding
    {
        public string id { get; set; }
        public string rule { get; set; }
        public string severity { get; set; }
        public string confidence { get; set; }
        public string title { get; set; }
        public string subject { get; set; }
        public string explanation { get; set; }
        public string suggestion { get; set; }
        public List<AuditLocation> locations { get; set; }
        public AuditFinding() { locations = new List<AuditLocation>(); }
    }
    internal sealed class AuditLocation
    {
        public string block { get; set; }
        public string group { get; set; }
        public string unit { get; set; }
        public string network { get; set; }
        public string location { get; set; }
        public string symbol { get; set; }
        public string address { get; set; }
        public string access { get; set; }
        public string evidence { get; set; }
    }
    internal sealed class AuditTemplate
    {
        public string host_id { get; set; }
        public string fb { get; set; }
        public string sample_id { get; set; }
        public string[] parameters { get; set; }
    }

    // Evidence-only analysis of the supplied immutable snapshot. No engineering,
    // online, compilation, import or save API is called here.
    internal static class ProgramAudit
    {
        internal const int MaxFindings = 1000, MaxLocations = 100, MaxWriteSites = 100000;
        private sealed class Span
        {
            internal string Area, Address;
            internal long Start, End;
            internal string Key { get { return Area + ":" + Start + "-" + End; } }
        }
        private sealed class Declared
        {
            internal IoRow Row;
            internal Span Span;
        }
        private sealed class WriteSite
        {
            internal string Key, Identity;
            internal bool SetReset;
            internal AuditLocation Location;
        }
        private sealed class Scan
        {
            internal AuditReport Report;
            internal int Parsed, Read, Unsupported, Calls, Sites;
            internal bool Truncated;
            internal readonly Dictionary<string, List<WriteSite>> Writes = new Dictionary<string, List<WriteSite>>(StringComparer.OrdinalIgnoreCase);
            internal readonly HashSet<string> SiteIds = new HashSet<string>(StringComparer.Ordinal);
            internal readonly HashSet<string> WarningSet = new HashSet<string>(StringComparer.Ordinal);
            internal void Warn(string text)
            {
                if (Report.warnings.Count >= 200) { Truncated = true; return; }
                if (WarningSet.Add(text)) {
                    Report.warnings.Add(text);
                }
            }
            internal void Add(AuditFinding finding)
            {
                if (Report.findings.Count >= MaxFindings) { Truncated = true; return; }
                if (finding.locations.Count > MaxLocations) { finding.locations = finding.locations.Take(MaxLocations).ToList(); Truncated = true; }
                finding.id = IndexJson.Hash(finding.rule + "\n" + finding.subject + "\n" + string.Join("\n", finding.locations.Select(l => l.unit + "/" + l.group + "/" + l.block + "/" + l.location + "/" + l.symbol)));
                Report.findings.Add(finding);
            }
        }
        private static string N(string value) { return value ?? ""; }
        private static string A(XmlNode node, string name) { var e = node as XmlElement; return e == null ? "" : e.GetAttribute(name); }
        private static string Symbol(string value) { return ContentIndexer.NormalizeSymbol(N(value).Trim()); }
        private static bool StaticOperand(XmlNode operand)
        {
            if (operand.SelectSingleNode(".//*[@SliceAccessModifier]") != null) return false;
            if (operand.SelectSingleNode(".//*[local-name()='Component']//*[local-name()='Access' and @Scope!='LiteralConstant']") != null) return false;
            return !operand.SelectNodes(".//*[local-name()='Component' and @AccessModifier='Array']").Cast<XmlNode>()
                .Any(c => c.SelectSingleNode(".//*[local-name()='ConstantValue']") == null);
        }
        private static string OperandWarning(XmlNode operand)
        { return operand.SelectSingleNode(".//*[@SliceAccessModifier]") != null ? "切片目标未参与多写判断：" : "动态下标或间接目标未参与多写判断："; }
        private static XmlDocument Parse(string xml)
        {
            var doc = new XmlDocument { XmlResolver = null };
            using (var r = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32L * 1024 * 1024 })) doc.Load(r);
            return doc;
        }
        private static Span Address(string value)
        {
            string normalized = IoTable.Address(value);
            var m = Regex.Match(normalized, @"^([IQM])([BWD]?)([0-9]{1,9})(?:\.([0-7]))?$");
            long offset;
            if (!m.Success || !long.TryParse(m.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out offset)) return null;
            string width = m.Groups[2].Value;
            if ((width.Length == 0) != m.Groups[4].Success) return null;
            long start = offset * 8 + (width.Length == 0 ? int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) : 0);
            int bits = width == "D" ? 32 : width == "W" ? 16 : width == "B" ? 8 : 1;
            return new Span { Area = m.Groups[1].Value, Start = start, End = start + bits - 1,
                Address = m.Groups[1].Value + width + offset.ToString(CultureInfo.InvariantCulture) + (width.Length == 0 ? "." + m.Groups[4].Value : "") };
        }
        private static int ScalarBits(string dataType)
        {
            switch (N(dataType).Trim().ToUpperInvariant()) {
                case "BOOL": return 1;
                case "BYTE": case "CHAR": case "SINT": case "USINT": return 8;
                case "WORD": case "INT": case "UINT": case "WCHAR": case "DATE": case "S5TIME": return 16;
                case "DWORD": case "DINT": case "UDINT": case "REAL": case "TIME": case "TIME_OF_DAY": case "TOD": return 32;
                default: return 0;
            }
        }
        private static string AliasKey(IoRow row)
        { return N(row.unit) + "\n" + Symbol(ContentIndexer.ComponentName(N(row.name))); }
        private static string Range(string area, long start, long end)
        { return area + (start / 8) + "." + (start % 8) + (end == start ? "" : "～" + area + (end / 8) + "." + (end % 8)); }
        private static AuditLocation Declaration(Declared item)
        {
            var row = item.Row;
            return new AuditLocation { block = row.table, group = row.group, unit = row.unit, network = "", location = "变量表 / " + row.name,
                symbol = row.name, address = item.Span.Address, access = "declaration", evidence = "声明地址 " + row.address + "，覆盖 " + Range(item.Span.Area, item.Span.Start, item.Span.End) };
        }
        private static void Overlaps(Scan scan, IList<Declared> declarations)
        {
            foreach (var scope in declarations.GroupBy(d => N(d.Row.unit) + "\n" + d.Span.Area, StringComparer.OrdinalIgnoreCase)) {
                var active = new List<Declared>();
                foreach (var current in scope.OrderBy(d => d.Span.Start).ThenBy(d => d.Span.End).ThenBy(d => d.Row.name, StringComparer.Ordinal)) {
                    active.RemoveAll(d => d.Span.End < current.Span.Start);
                    foreach (var prior in active) {
                        if (scan.Report.findings.Count >= MaxFindings) { scan.Truncated = true; return; }
                        scan.Add(new AuditFinding { rule = "address_overlap", severity = "warning", confidence = "confirmed", title = "声明地址范围重叠，需核对用途",
                            subject = Range(current.Span.Area, Math.Max(prior.Span.Start, current.Span.Start), Math.Min(prior.Span.End, current.Span.End)),
                            explanation = "这两个声明使用了相同的存储位。字节/位别名可能是有意设计，本项只确认范围重叠，不直接判定为程序错误。",
                            suggestion = "核对是否属于同一数据的合法别名；如承担不同设备或控制功能，请检查分配和所有写入位置。",
                            locations = new List<AuditLocation> { Declaration(prior), Declaration(current) } });
                    }
                    active.Add(current);
                }
            }
        }
        private static string Resolve(string target, IndexEntry block, ILookup<string, Declared> aliases, Scan scan, out string address)
        {
            address = "";
            string symbol = Symbol(target), unit = N(block.unit_name);
            if (symbol.StartsWith("#", StringComparison.Ordinal)) return unit + "\nlocal:" + N(block.id) + "/" + N(block.group) + "/" + N(block.name) + ":" + symbol;
            // Absolute I/Q/M operands carry '%'. A valid PLC tag can itself be
            // named QB5 or MB10, so an unprefixed name must resolve by declaration.
            var direct = symbol.StartsWith("%", StringComparison.Ordinal) ? Address(symbol) : null;
            if (direct != null) { address = direct.Address; return unit + "\naddress:" + direct.Key; }
            var matches = aliases[unit + "\n" + symbol].Select(a => a.Span).GroupBy(s => s.Key).Select(g => g.First()).ToArray();
            if (matches.Length == 1) { address = matches[0].Address; return unit + "\naddress:" + matches[0].Key; }
            if (matches.Length > 1) scan.Warn("符号存在多个声明地址，未与物理地址合并：" + unit + "/" + symbol);
            return unit + "\nsymbol:" + symbol;
        }
        private static void AddWrite(Scan scan, IndexEntry block, ILookup<string, Declared> aliases, string target,
            string network, string site, string access, string evidence, bool setReset)
        {
            if (string.IsNullOrWhiteSpace(target)) return;
            if (scan.Sites >= MaxWriteSites) { scan.Truncated = true; return; }
            string address, key = Resolve(target, block, aliases, scan, out address);
            string identity = N(block.unit_name) + "/" + N(block.id) + "/" + N(block.group) + "/" + N(block.name) + "/" + site + "/" + key;
            if (!scan.SiteIds.Add(identity)) return;
            scan.Sites++;
            List<WriteSite> group;
            if (!scan.Writes.TryGetValue(key, out group)) scan.Writes[key] = group = new List<WriteSite>();
            group.Add(new WriteSite { Key = key, Identity = identity, SetReset = setReset, Location = new AuditLocation {
                block = block.name, group = block.group, unit = block.unit_name, network = network, location = site,
                symbol = target, address = address, access = access, evidence = evidence } });
        }
        private static void ScanBlock(Scan scan, IndexEntry block, string xml, ILookup<string, Declared> aliases, IList<AuditTemplate> templates)
        {
            // Parse maintains the shared structural coverage and mapping semantics.
            // Write-site identity uses actual endpoint positions: the generic index
            // deduplicates identical target/location pairs within a network.
            var content = ContentIndexer.Parse(xml);
            var doc = Parse(xml);
            var units = doc.SelectNodes("//SW.Blocks.CompileUnit").Cast<XmlNode>().ToArray();
            var networkNumbers = units.Select((n, i) => new { Node = n, Number = (i + 1).ToString(CultureInfo.InvariantCulture) }).ToDictionary(p => p.Node, p => p.Number);
            Func<XmlNode, string> network = n => { for (var p = n; p != null; p = p.ParentNode) if (networkNumbers.ContainsKey(p)) return networkNumbers[p]; return ""; };
            int netCount = 0;
            foreach (XmlNode net in doc.SelectNodes("//*[local-name()='FlgNet']")) {
                netCount++;
                string number = network(net), prefix = "网络 " + (number.Length == 0 ? "未标号" : number) + " / 图 " + netCount;
                var parts = net.SelectNodes("*[local-name()='Parts']/*[@UId]").Cast<XmlElement>().ToDictionary(n => A(n, "UId"));
                var wires = net.SelectNodes("*[local-name()='Wires']/*").Cast<XmlNode>().ToArray();
                var partPositions = parts.Values.Select((p, i) => new { Node = p, Number = i + 1 }).ToDictionary(p => p.Node, p => p.Number);
                foreach (XmlNode wire in wires) foreach (XmlNode ident in wire.SelectNodes("*[local-name()='IdentCon']")) {
                    XmlElement operand; if (!parts.TryGetValue(A(ident, "UId"), out operand) || operand.LocalName != "Access") continue;
                    string target = ContentIndexer.Operand(operand);
                    foreach (XmlNode con in wire.SelectNodes("*[local-name()='NameCon']")) {
                        XmlElement part; if (!parts.TryGetValue(A(con, "UId"), out part)) continue;
                        string pin = A(con, "Name"), op = A(part, "Name"), access = "", evidence = ""; bool setReset = false;
                        if (pin == "operand" && new[] { "Coil", "SCoil", "RCoil" }.Contains(op)) {
                            access = "writes"; setReset = op != "Coil"; evidence = "LAD " + op + " 的 operand 写入";
                        }
                        var parameter = part.SelectNodes("*[local-name()='CallInfo']/*[local-name()='Parameter']").Cast<XmlNode>().FirstOrDefault(p => A(p, "Name") == pin);
                        if (parameter != null && new[] { "Output", "Return", "InOut" }.Contains(A(parameter, "Section"))) {
                            access = A(parameter, "Section") == "InOut" ? "reads_writes" : "writes";
                            evidence = "调用参数 " + A(part.SelectSingleNode("*[local-name()='CallInfo']"), "Name") + "." + pin + "，方向 " + A(parameter, "Section");
                        }
                        if (access.Length == 0) continue;
                        if (!StaticOperand(operand)) { scan.Warn(OperandWarning(operand) + block.name + " / " + prefix); continue; }
                        if (content.mappings.Any(m => Symbol(m.target) == Symbol(target))) evidence += "；包含直接 I/O 映射传送，请核对是否为预期分工";
                        AddWrite(scan, block, aliases, target, number, prefix + " / 元件 " + partPositions[part] + " / " + pin, access, evidence, setReset);
                    }
                }
                foreach (var template in templates.Where(t => t.host_id == block.id)) foreach (var part in parts.Values.Where(p => p.LocalName == "Call")) {
                    var call = part.SelectSingleNode("*[local-name()='CallInfo']");
                    if (call == null || A(call, "BlockType") != "FB" || !string.Equals(A(call, "Name"), template.fb, StringComparison.OrdinalIgnoreCase)) continue;
                    scan.Calls++;
                    string instance = string.Join(".", call.SelectNodes("*[local-name()='Instance']/*[local-name()='Component']").Cast<XmlNode>().Select(c => A(c, "Name")));
                    foreach (string pin in (template.parameters ?? new string[0]).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct()) {
                        var connected = wires.Where(w => w.SelectNodes("*[local-name()='NameCon']").Cast<XmlNode>().Any(c => A(c, "UId") == A(part, "UId") && A(c, "Name") == pin)).ToArray();
                        bool declared = call.SelectNodes("*[local-name()='Parameter']").Cast<XmlNode>().Any(p => A(p, "Name") == pin);
                        var peers = connected.SelectMany(w => w.ChildNodes.Cast<XmlNode>()).Where(c => c is XmlElement && c.LocalName != "OpenCon" &&
                            !(c.LocalName == "NameCon" && A(c, "UId") == A(part, "UId") && A(c, "Name") == pin)).ToArray();
                        bool supplied = peers.Any(c => c.LocalName == "Powerrail" ||
                            (c.LocalName == "NameCon" && parts.ContainsKey(A(c, "UId"))) ||
                            (c.LocalName == "IdentCon" && parts.ContainsKey(A(c, "UId")) && parts[A(c, "UId")].LocalName == "Access"));
                        if (declared && supplied) continue;
                        if (declared && peers.Length > 0) {
                            scan.Warn("参数连接格式或对端无法核对，未判定为空接线：" + block.name + "/" + instance + "/" + pin + " / " + prefix);
                            continue;
                        }
                        scan.Add(new AuditFinding { rule = "template_pin", severity = "warning", confidence = "confirmed", title = "调用参数未按已确认模板连接",
                            subject = block.name + "/" + instance + "/" + pin,
                            explanation = "项目模板 " + template.sample_id + " 要求该参数有连接；当前调用" + (!declared ? "没有该参数声明。" : connected.Length == 0 ? "没有对应连线。" : "仅有开路或空连接。") + "本项为模板一致性检查，不根据参数名称推定安全功能。",
                            suggestion = "对照当前设备用途核对模板与调用，确认该参数应连接还是有意留空；体检不会自动补线。",
                            locations = new List<AuditLocation> { new AuditLocation { block = block.name, group = block.group, unit = block.unit_name, network = number,
                                location = prefix + " / 调用 " + instance + " / " + pin, symbol = instance + "." + pin, address = "", access = "connection",
                                evidence = "已确认模板 " + template.sample_id + "；FB " + template.fb + "；" + (!declared ? "缺少 Parameter" : connected.Length == 0 ? "缺少 NameCon" : "OpenCon / 无有效对端") } } });
                    }
                }
            }
            int statement = 0;
            foreach (XmlNode stl in doc.SelectNodes("//*[local-name()='StlStatement']")) {
                statement++; string op = A(stl.SelectSingleNode("*[local-name()='StlToken']"), "Text");
                if (!new[] { "T", "=", "S", "R" }.Contains(op)) continue;
                foreach (XmlNode operand in stl.SelectNodes("*[local-name()='Access']")) if (A(operand, "Scope") != "LiteralConstant")
                    if (StaticOperand(operand)) AddWrite(scan, block, aliases, ContentIndexer.Operand(operand), network(stl), "STL 语句 " + statement, "writes", "STL 操作码 " + op, op == "S" || op == "R");
                    else scan.Warn(OperandWarning(operand) + block.name + " / STL 语句 " + statement);
            }
            if (!string.Equals(block.language, "DB", StringComparison.OrdinalIgnoreCase) &&
                (netCount == 0 && statement == 0 || doc.SelectSingleNode("//*[local-name()='StructuredText' or local-name()='SourceText' or local-name()='Graph']") != null)) {
                scan.Unsupported++;
                scan.Warn("部分或全部程序行为未覆盖：" + N(block.unit_name) + "/" + N(block.group) + "/" + block.name + "（" + block.language + "）；未推断 SCL/GRAPH 完整写入和控制流。");
            }
            if (templates.Any(t => t.host_id == block.id) && netCount == 0) scan.Warn("模板宿主不是可解析的 FlgNet 调用格式，未检查模板参数：" + block.name);
        }
        internal static AuditReport Run(IoSnapshot snapshot, Func<IndexEntry, string> source, IList<AuditTemplate> templates)
        {
            if (snapshot == null || source == null) throw new ArgumentException("缺少可读取的工程快照。");
            templates = (templates ?? new List<AuditTemplate>()).Where(t => t != null).ToList();
            var report = new AuditReport { id = Guid.NewGuid().ToString("N"), project_path = snapshot.project_path, plc_name = snapshot.plc_name,
                snapshot_started = snapshot.read_started_utc, snapshot_completed = snapshot.read_completed_utc, generated_at = DateTime.UtcNow.ToString("o") };
            var scan = new Scan { Report = report };
            var limitations = new[] { "只分析当前源码快照，不连接 PLC，不修改、编译或保存工程。", "重复写入和地址重叠是待核对现象，未证明执行冲突或逻辑安全。",
                "写入证据覆盖受支持 LAD 线圈/调用参数及 STL 写指令；未完成 SCL/GRAPH 控制流、切片访问、动态寻址、HMI/外部写入分析。",
                "地址重叠仅检查 PLC 变量表显式 I/Q/M 声明，不包含硬件模块地址和 DB 布局重叠。",
                "声明范围须与已知标量类型宽度一致：Bool 1 位；Byte/Char/SInt/USInt 8 位；Word/Int/UInt/WChar/Date/S5Time 16 位；DWord/DInt/UDInt/Real/Time/TOD/Time_Of_Day 32 位。其他类型、数组、字符串和结构不参与地址范围及地址别名判断。",
                "模板检查仅使用当前工程已确认且仍匹配的模板；无发现不代表无问题。" };
            foreach (string warning in snapshot.warnings ?? new List<string>()) scan.Warn(warning);
            if (snapshot.cylinderProgramsRead < snapshot.cylinderProgramsTotal || snapshot.tables_read < snapshot.tables_total)
                scan.Warn("源码或变量表读取不完整，未读取对象不参与体检。");
            if (templates.Count == 0) scan.Warn("没有可用的已确认项目模板，模板参数连接检查未覆盖。");
            var native = (snapshot.rows ?? new List<IoRow>()).Where(r => r != null && string.IsNullOrEmpty(r.mapping_fc) && string.IsNullOrEmpty(r.origin)).ToArray();
            var declarations = new List<Declared>();
            var unresolvedAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in native.GroupBy(r => N(r.unit) + "\n" + N(r.group) + "\n" + N(r.table) + "\n" + N(r.name) + "\n" + IoTable.Address(r.address) + "\n" + N(r.data_type), StringComparer.OrdinalIgnoreCase).Select(g => g.First())) {
                var span = Address(row.address);
                int bits = ScalarBits(row.data_type);
                if (span != null && bits > 0 && bits == span.End - span.Start + 1) declarations.Add(new Declared { Row = row, Span = span });
                else {
                    unresolvedAliases.Add(AliasKey(row));
                    if (!string.IsNullOrWhiteSpace(row.address)) {
                        if (span == null) scan.Warn("未识别的声明地址未参与重叠检查及地址别名判断：" + row.name + " / " + row.address);
                        else scan.Warn("声明类型不支持或与地址宽度不一致，未参与重叠检查及地址别名判断：" + row.name + " / " + row.address + " / " + N(row.data_type));
                    }
                }
            }
            Overlaps(scan, declarations);
            var aliases = declarations.Where(d => !unresolvedAliases.Contains(AliasKey(d.Row))).ToLookup(d => AliasKey(d.Row), StringComparer.OrdinalIgnoreCase);
            var blocks = snapshot.cylinderBlocks ?? new IndexEntry[0];
            foreach (var template in templates.Where(t => !blocks.Any(b => b != null && b.id == t.host_id)))
                scan.Warn("模板宿主不在当前源码快照中，未检查模板参数：" + template.host_id + " / " + template.fb);
            foreach (var block in blocks.Where(b => b != null)) {
                try {
                    string xml = source(block); if (string.IsNullOrWhiteSpace(xml)) throw new InvalidOperationException("源码为空");
                    scan.Read++; ScanBlock(scan, block, xml, aliases, templates); scan.Parsed++;
                } catch (Exception ex) { scan.Warn("程序块未完成分析：" + N(block.unit_name) + "/" + N(block.group) + "/" + block.name + "；" + ex.Message); }
            }
            foreach (var group in scan.Writes.Values.Where(g => g.Count > 1).OrderBy(g => g[0].Key, StringComparer.OrdinalIgnoreCase)) {
                bool setResetOnly = group.All(s => s.SetReset);
                scan.Add(new AuditFinding { rule = "multiple_writes", severity = setResetOnly ? "info" : "warning", confidence = "confirmed",
                    title = setResetOnly ? "同一目标存在多处置位/复位" : "同一目标存在多处显式写入",
                    subject = string.IsNullOrEmpty(group[0].Location.address) ? group[0].Location.symbol : group[0].Location.address,
                    explanation = "发现 " + group.Count + " 个不同写入位置。" + (setResetOnly ? "这些均为 S/R 置复位，可能是有意设计。" : "包含普通赋值或调用输出/双向参数。") + "是否互斥、执行先后顺序和任务优先级未判定，不能据此直接认定冲突。",
                    suggestion = "核对所有写入者、执行条件与调用顺序；I/O 映射和设备控制的分工需结合项目设计确认。",
                    locations = group.Take(MaxLocations + 1).Select(s => s.Location).ToList() });
            }
            report.call_graph = CallGraphAudit.Run(snapshot, source, scan.Add, scan.Warn);
            scan.Truncated |= report.call_graph.truncated;
            if (scan.Truncated) { if (report.warnings.Count >= 200) report.warnings.RemoveAt(199); report.warnings.Add("结果或证据达到数量上限，报告不完整：体检最多 " + MaxFindings + " 条发现、每条 " + MaxLocations + " 个位置、" + MaxWriteSites + " 个写入证据；调用关系的限制见对应说明。请结合源码核对。"); }
            report.coverage = new Dictionary<string, object> { { "blocks_total", blocks.Length }, { "blocks_read", scan.Read }, { "blocks_parsed", scan.Parsed },
                { "programs_total", snapshot.cylinderProgramsTotal }, { "programs_read", snapshot.cylinderProgramsRead }, { "tag_tables_total", snapshot.tables_total }, { "tag_tables_read", snapshot.tables_read },
                { "native_tags", native.Length }, { "addresses_checked", declarations.Count }, { "templates", templates.Count }, { "template_calls_checked", scan.Calls },
                { "supported_write_sites", scan.Sites }, { "unsupported_blocks", scan.Unsupported }, { "truncated", scan.Truncated },
                { "limitations", limitations.Concat(report.call_graph.limitations).Distinct().ToArray() } };
            return report;
        }
    }
}
