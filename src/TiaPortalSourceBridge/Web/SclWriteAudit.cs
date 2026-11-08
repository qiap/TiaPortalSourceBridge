using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using TiaPortalSourceBridge.Indexing;

namespace TiaPortalSourceBridge.Web
{
    internal sealed class SclAuditResult
    {
        public bool present { get; set; }
        public bool partial { get; set; }
        public bool truncated { get; set; }
        public int regions { get; set; }
        public int regions_analyzed { get; set; }
        public List<AuditLocation> writes { get; set; }
        public List<string> warnings { get; set; }
        public SclAuditResult() { writes = new List<AuditLocation>(); warnings = new List<string>(); }
    }

    // This is an explicit-write inventory, not a compiler or control-flow proof.
    // In particular, named call arguments are never interpreted as assignments
    // unless the callee's native interface and instance identity are verified.
    internal sealed class SclWriteAudit
    {
        internal const int MaxRegionCharacters = 4 * 1024 * 1024, MaxTokens = 200000, MaxRegions = 200,
            MaxWrites = 10000, MaxStatementCharacters = 8192, MaxWarnings = 100, MaxBlocks = 5000;
        private readonly List<IndexEntry> entries;
        private readonly Func<IndexEntry, string> source;
        private readonly List<Declaration> declarations = new List<Declaration>();
        private bool loaded, declarationLimit;
        private sealed class Declaration
        {
            internal IndexEntry Entry;
            internal string Kind, InstanceOf, InstanceType;
            internal readonly Dictionary<string, string> Pins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            internal readonly Dictionary<string, string> Statics = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            internal readonly HashSet<string> DuplicatePins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            internal readonly HashSet<string> DuplicateStatics = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        private sealed class Token
        {
            internal string Text, Kind;
            internal int Start, End, Line, Column, Pair = -1, Parent = -1;
        }
        private sealed class Region
        {
            internal string Text, Network, Basis;
            internal int Number;
        }
        private static string N(string value) { return value ?? ""; }
        private static bool Same(string a, string b) { return string.Equals(N(a), N(b), StringComparison.OrdinalIgnoreCase); }
        private static string A(XmlNode node, string name) { var e = node as XmlElement; return e == null ? "" : e.GetAttribute(name); }
        private static string Child(XmlNode node, string name) { var c = node.SelectSingleNode("*[local-name()='" + name + "']"); return c == null ? "" : c.InnerText; }
        private static string Unquote(string value) { value = N(value).Trim(); return value.Length > 1 && value[0] == '"' && value[value.Length - 1] == '"' ? value.Substring(1, value.Length - 2).Replace("\"\"", "\"") : value; }
        private static string Component(string value) { return ContentIndexer.ComponentName(value); }
        private static void Warn(SclAuditResult result, string text, bool limit = false)
        {
            result.partial = true;
            if (limit) result.truncated = true;
            if (result.warnings.Contains(text)) return;
            if (result.warnings.Count >= MaxWarnings) { result.truncated = true; return; }
            result.warnings.Add(text);
        }
        internal SclWriteAudit(IEnumerable<IndexEntry> blocks, Func<IndexEntry, string> source)
        {
            this.source = source;
            entries = (blocks ?? Enumerable.Empty<IndexEntry>()).Where(b => b != null).Take(MaxBlocks + 1).ToList();
            if (entries.Count > MaxBlocks) { entries.RemoveAt(MaxBlocks); declarationLimit = true; }
        }
        internal SclAuditResult Analyze(IndexEntry block, XmlDocument document)
        {
            var result = new SclAuditResult();
            if (block == null || document == null) { Warn(result, "SCL 源码或块身份缺失。"); return result; }
            int ordinal = 0;
            foreach (XmlNode node in document.SelectNodes("//*[local-name()='StructuredText' or local-name()='SourceText']")) {
                if (Ancestor(node, "StructuredText") || Ancestor(node, "SourceText")) continue;
                if (Ancestor(node, "Graph") || Ancestor(node, "GRAPH") || Same(block.language, "GRAPH")) {
                    Warn(result, "GRAPH 内部 SCL 动作未纳入显式写入检查：" + block.name); continue;
                }
                if (node.LocalName == "SourceText" && !Same(block.language, "SCL") && !SclLanguage(node)) {
                    Warn(result, "SourceText 未声明为 SCL，未猜测其语言：" + block.name); continue;
                }
                result.present = true; result.regions++; ordinal++;
                if (ordinal > MaxRegions) { Warn(result, "SCL 源码区域数量超过检查上限。", true); continue; }
                var region = new Region { Network = Network(node), Number = ordinal };
                bool native = node.ChildNodes.Cast<XmlNode>().Any(n => n is XmlElement);
                region.Basis = native ? "reconstructed_scl" : "exported_text";
                if (native) {
                    var text = new StringBuilder();
                    try { Render(node, text, result, 0); }
                    catch (InvalidOperationException) { Warn(result, "SCL 重建源码超过大小或嵌套上限，未分析该区域。", true); continue; }
                    region.Text = text.ToString();
                } else region.Text = node.InnerText;
                if (region.Text.Length > MaxRegionCharacters) { Warn(result, "SCL 源码超过检查大小上限，未分析该区域。", true); continue; }
                List<Token> tokens;
                if (!Lex(region.Text, result, out tokens)) continue;
                if (AnalyzeTokens(block, document, region, tokens, result)) result.regions_analyzed++;
            }
            if (Same(block.language, "SCL") && !result.present) Warn(result, "SCL 块没有可读取的完整 StructuredText/SourceText：" + block.name);
            return result;
        }
        private static bool Ancestor(XmlNode node, string name)
        { for (var p = node.ParentNode; p != null; p = p.ParentNode) if (p.LocalName == name) return true; return false; }
        private static bool SclLanguage(XmlNode node)
        {
            for (var p = node.ParentNode; p != null; p = p.ParentNode) {
                var a = p.SelectSingleNode("*[local-name()='AttributeList']/*[local-name()='ProgrammingLanguage']");
                if (a != null) return Same(a.InnerText, "SCL");
            }
            return false;
        }
        private static string Network(XmlNode node)
        {
            for (var p = node.ParentNode; p != null; p = p.ParentNode) if (p.LocalName == "SW.Blocks.CompileUnit") {
                int count = 1;
                for (var prior = p.PreviousSibling; prior != null; prior = prior.PreviousSibling) if (prior.LocalName == p.LocalName) count++;
                return count.ToString(CultureInfo.InvariantCulture);
            }
            return "";
        }
        private static void Append(StringBuilder builder, string value)
        { if (builder.Length + N(value).Length > MaxRegionCharacters) throw new InvalidOperationException(); builder.Append(value); }
        private static void Unknown(StringBuilder text, SclAuditResult result, string reason)
        { Warn(result, reason); Append(text, " @unsupported_scl@ "); }
        private static void Render(XmlNode node, StringBuilder text, SclAuditResult result, int depth)
        {
            if (depth > 128) throw new InvalidOperationException();
            if (node is XmlText || node is XmlCDataSection) { Append(text, node.Value); return; }
            if (!(node is XmlElement)) return;
            switch (node.LocalName) {
                case "Token": Append(text, A(node, "Text")); return;
                case "NewLine": case "Blank":
                    int count;
                    if (!int.TryParse(A(node, "Num"), out count)) count = 1;
                    if (count < 0 || count > 10000) { Unknown(text, result, "SCL 空白/换行计数无效，无法完整重建源码。"); return; }
                    Append(text, new string(node.LocalName == "NewLine" ? '\n' : ' ', count)); return;
                case "LineComment":
                    // NewLine nodes after the comment preserve the exported line count.
                    Append(text, "// " + node.InnerText.Replace("\r", " ").Replace("\n", " ")); return;
                case "Access":
                    if (Same(A(node, "Scope"), "Call")) { RenderChildren(node, text, result, depth); return; }
                    var symbol = node.SelectSingleNode("*[local-name()='Symbol']");
                    if (symbol != null) {
                        string operand;
                        if (!RenderSymbol(node, symbol, out operand)) { Unknown(text, result, "SCL 存在数组/切片/间接或未知结构访问，未猜测其符号前缀。"); return; }
                        Append(text, operand); return;
                    }
                    var address = node.SelectSingleNode("*[local-name()='Address']");
                    if (address != null) {
                        string value = ContentIndexer.Operand(node), normalized;
                        if (!Absolute(value, out normalized)) { Unknown(text, result, "SCL 绝对地址格式不受支持。"); return; }
                        Append(text, normalized); return;
                    }
                    var constant = node.SelectSingleNode("*[local-name()='Constant']/*[local-name()='ConstantValue']");
                    var namedConstant = node.SelectSingleNode("*[local-name()='Constant']");
                    if (constant == null && namedConstant != null && A(namedConstant, "Name").Length > 0 &&
                        (Same(A(node, "Scope"), "LocalConstant") || Same(A(node, "Scope"), "GlobalConstant"))) {
                        Append(text, (Same(A(node, "Scope"), "LocalConstant") ? "#" : "") + Component(A(namedConstant, "Name"))); return;
                    }
                    if (constant != null) {
                        string value = constant.InnerText;
                        if (Same(A(node, "Scope"), "LocalConstant") || Same(A(node, "Scope"), "GlobalConstant")) Append(text, (Same(A(node, "Scope"), "LocalConstant") ? "#" : "") + Component(value));
                        else if (value.Length > 0 && (value[0] == '\'' || Regex.IsMatch(value, @"^(?i:TRUE|FALSE|[+-]?[0-9][0-9a-fA-F_#.:+-]*|[A-Za-z_][A-Za-z0-9_]*#[A-Za-z0-9_.:+-]+)$"))) Append(text, value);
                        else Unknown(text, result, "SCL 常量编码不受支持，未将常量内容作为语句解析。");
                        return;
                    }
                    Unknown(text, result, "SCL Access 缺少可识别的静态操作数。"); return;
                case "Instruction": case "Parameter":
                    string name = A(node, "Name");
                    if (name.Length == 0) { Unknown(text, result, "SCL 调用/形参缺少名称。"); return; }
                    Append(text, name); RenderChildren(node, text, result, depth); return;
                case "NamelessParameter": case "StructuredText": case "SourceText":
                    RenderChildren(node, text, result, depth); return;
                case "TemplateValue":
                    if (Same(A(node, "Type"), "Type")) return;
                    Unknown(text, result, "SCL 调用模板元数据尚未支持。"); return;
                default: Unknown(text, result, "SCL XML 节点尚未支持：" + node.LocalName); return;
            }
        }
        private static void RenderChildren(XmlNode node, StringBuilder text, SclAuditResult result, int depth)
        { foreach (XmlNode child in node.ChildNodes) Render(child, text, result, depth + 1); }
        private static bool RenderSymbol(XmlNode access, XmlNode symbol, out string value)
        {
            value = "";
            if (!new[] { "LocalVariable", "GlobalVariable", "GlobalConstant", "LocalConstant" }.Any(s => Same(s, A(access, "Scope")))) return false;
            if (access.ChildNodes.Cast<XmlNode>().Any(n => n is XmlElement && n != symbol)) return false;
            var children = symbol.ChildNodes.Cast<XmlNode>().Where(n => n is XmlElement).ToArray();
            var components = children.Where(n => n.LocalName == "Component").ToArray();
            if (components.Length == 0 || components.Length > 64) return false;
            // Different export revisions either include separator tokens or put
            // Component nodes directly adjacent. A separator cannot stand alone.
            for (int i = 0; i < children.Length; i++) if (children[i].LocalName != "Component" &&
                !(children[i].LocalName == "Token" && A(children[i], "Text") == "." && i > 0 && i + 1 < children.Length && children[i - 1].LocalName == "Component" && children[i + 1].LocalName == "Component")) return false;
            foreach (var component in components) if (component.LocalName != "Component" || A(component, "Name").Length == 0 ||
                component.ChildNodes.Cast<XmlNode>().Any(n => n is XmlElement) || A(component, "AccessModifier").Length > 0 || A(component, "SliceAccessModifier").Length > 0) return false;
            value = (Same(A(access, "Scope"), "LocalVariable") || Same(A(access, "Scope"), "LocalConstant") ? "#" : "") + string.Join(".", components.Select(c => Component(A(c, "Name"))));
            return true;
        }
        private static bool Letter(char ch) { return ch == '_' || char.IsLetter(ch); }
        private static bool IdentifierChar(char ch) { return Letter(ch) || char.IsDigit(ch); }
        private static void Advance(string sourceText, ref int p, ref int line, ref int column)
        { if (sourceText[p] == '\n') { line++; column = 1; } else column++; p++; }
        private static bool Lex(string text, SclAuditResult result, out List<Token> tokens)
        {
            tokens = new List<Token>();
            var stack = new Stack<int>();
            int p = 0, line = 1, column = 1;
            while (p < text.Length) {
                char ch = text[p];
                if (char.IsWhiteSpace(ch)) { Advance(text, ref p, ref line, ref column); continue; }
                if (ch == '/' && p + 1 < text.Length && text[p + 1] == '/') {
                    while (p < text.Length && text[p] != '\n') Advance(text, ref p, ref line, ref column);
                    continue;
                }
                if (ch == '(' && p + 1 < text.Length && text[p + 1] == '*') {
                    int nesting = 1; Advance(text, ref p, ref line, ref column); Advance(text, ref p, ref line, ref column);
                    while (p < text.Length && nesting > 0) {
                        if (p + 1 < text.Length && text[p] == '(' && text[p + 1] == '*') { nesting++; Advance(text, ref p, ref line, ref column); }
                        else if (p + 1 < text.Length && text[p] == '*' && text[p + 1] == ')') { nesting--; Advance(text, ref p, ref line, ref column); }
                        Advance(text, ref p, ref line, ref column);
                    }
                    if (nesting != 0) { Warn(result, "SCL 块注释未闭合，该源码区域未分析。"); return false; }
                    continue;
                }
                if (tokens.Count >= MaxTokens) { Warn(result, "SCL 词法单元超过检查上限，该源码区域未分析。", true); return false; }
                var token = new Token { Start = p, Line = line, Column = column, Parent = stack.Count == 0 ? -1 : stack.Peek(), Kind = "punctuation" };
                if (ch == '\'' || ch == '"') {
                    char quote = ch; bool closed = false; token.Kind = quote == '\'' ? "string" : "identifier";
                    Advance(text, ref p, ref line, ref column);
                    while (p < text.Length) {
                        if (quote == '\'' && text[p] == '$' && p + 1 < text.Length) { Advance(text, ref p, ref line, ref column); Advance(text, ref p, ref line, ref column); continue; }
                        if (text[p] == quote) {
                            Advance(text, ref p, ref line, ref column);
                            if (p < text.Length && text[p] == quote) { Advance(text, ref p, ref line, ref column); continue; }
                            closed = true; break;
                        }
                        if (text[p] == '\r' || text[p] == '\n') break;
                        Advance(text, ref p, ref line, ref column);
                    }
                    if (!closed) { Warn(result, "SCL 字符串/符号引号未闭合，该源码区域未分析。"); return false; }
                } else if (Letter(ch)) {
                    token.Kind = "identifier";
                    do { Advance(text, ref p, ref line, ref column); } while (p < text.Length && IdentifierChar(text[p]));
                } else if (char.IsDigit(ch)) {
                    token.Kind = "number";
                    do { Advance(text, ref p, ref line, ref column); } while (p < text.Length && char.IsDigit(text[p]));
                } else if (ch == '%') {
                    token.Kind = "address";
                    do { Advance(text, ref p, ref line, ref column); } while (p < text.Length && (IdentifierChar(text[p]) || text[p] == '.'));
                } else {
                    Advance(text, ref p, ref line, ref column);
                    if (p < text.Length && (ch == ':' && text[p] == '=' || ch == '=' && text[p] == '>' || ch == '<' && (text[p] == '=' || text[p] == '>') || ch == '>' && text[p] == '=')) Advance(text, ref p, ref line, ref column);
                    if ("#.,;:()+-*/[]=<>^".IndexOf(ch) < 0) token.Kind = "unknown";
                }
                token.End = p; token.Text = text.Substring(token.Start, p - token.Start);
                int index = tokens.Count; tokens.Add(token);
                if (token.Text == "(" || token.Text == "[") {
                    if (stack.Count >= 128) { Warn(result, "SCL 括号嵌套超过检查上限。", true); return false; }
                    stack.Push(index);
                } else if (token.Text == ")" || token.Text == "]") {
                    if (stack.Count == 0 || tokens[stack.Peek()].Text != (token.Text == ")" ? "(" : "[")) { Warn(result, "SCL 括号不匹配，该源码区域未分析。"); return false; }
                    int begin = stack.Pop(); token.Pair = begin; tokens[begin].Pair = index;
                }
            }
            if (stack.Count > 0) { Warn(result, "SCL 括号未闭合，该源码区域未分析。"); return false; }
            return true;
        }
        private static bool Absolute(string value, out string normalized)
        {
            normalized = "";
            var match = Regex.Match(N(value), @"^%(?<area>[IQMEA])(?<width>[BWD]?)(?<byte>\d+)(?:\.(?<bit>[0-7]))?$", RegexOptions.IgnoreCase);
            if (!match.Success) return false;
            long address;
            if (!long.TryParse(match.Groups["byte"].Value, out address) || address > int.MaxValue) return false;
            string width = match.Groups["width"].Value.ToUpperInvariant(), bit = match.Groups["bit"].Value;
            if (width.Length > 0 && bit.Length > 0 || width.Length == 0 && bit.Length == 0) return false;
            string area = match.Groups["area"].Value.ToUpperInvariant(); if (area == "E") area = "I"; if (area == "A") area = "Q";
            normalized = "%" + area + width + address.ToString(CultureInfo.InvariantCulture) + (bit.Length > 0 ? "." + bit : ""); return true;
        }
        private static bool Target(List<Token> tokens, int start, int end, out string value)
        {
            value = ""; if (start < 0 || end <= start || end - start > 128) return false;
            if (end == start + 1 && tokens[start].Kind == "address") return Absolute(tokens[start].Text, out value);
            var target = new StringBuilder(); int p = start;
            if (tokens[p].Text == "#") { target.Append('#'); p++; }
            if (p >= end || tokens[p].Kind != "identifier") return false;
            while (p < end) {
                if (tokens[p].Kind != "identifier" || new[] { "TRUE", "FALSE", "IF", "THEN", "ELSE", "ELSIF", "END_IF", "CASE", "OF", "END_CASE", "FOR", "TO", "BY", "DO", "END_FOR", "WHILE", "END_WHILE", "REPEAT", "UNTIL", "END_REPEAT", "BEGIN", "END_VAR", "RETURN" }.Any(k => Same(k, tokens[p].Text))) return false;
                string component = Unquote(tokens[p++].Text); if (component.Length == 0) return false;
                target.Append(Component(component));
                while (p < end && tokens[p].Text == "[") {
                    p++; target.Append('['); int indices = 0;
                    while (p < end) {
                        bool minus = tokens[p].Text == "-"; if (minus) p++;
                        long n;
                        if (p >= end || tokens[p].Kind != "number" || !long.TryParse(tokens[p++].Text, out n) || n > int.MaxValue) return false;
                        if (indices++ > 0) target.Append(','); target.Append(minus ? -n : n);
                        if (p < end && tokens[p].Text == ",") { p++; continue; }
                        break;
                    }
                    if (indices == 0 || p >= end || tokens[p++].Text != "]") return false;
                    target.Append(']');
                }
                if (p == end) { value = target.ToString(); return value.Length <= 1024; }
                if (tokens[p++].Text != ".") return false;
                target.Append('.');
            }
            return false;
        }
        private static bool Boundary(Token token)
        { return token == null || new[] { ";", "THEN", "ELSE", "DO", "BEGIN", "OF", ":", "FOR" }.Any(t => Same(t, token.Text)); }
        private static int TargetBefore(List<Token> tokens, int end, out string value)
        {
            value = ""; int best = -1;
            for (int start = Math.Max(0, end - 128); start < end; start++) {
                string candidate;
                if (!Target(tokens, start, end, out candidate)) continue;
                best = start; value = candidate; break;
            }
            return best;
        }
        private bool AnalyzeTokens(IndexEntry block, XmlDocument document, Region region, List<Token> tokens, SclAuditResult result)
        {
            var headers = tokens.Select((t, i) => new { Token = t, Index = i }).Where(t => Same(t.Token.Text, "FUNCTION") || Same(t.Token.Text, "FUNCTION_BLOCK") || Same(t.Token.Text, "ORGANIZATION_BLOCK")).ToArray();
            if (headers.Length > 1 || headers.Length == 1 && (headers[0].Index + 1 >= tokens.Count || !Same(Unquote(tokens[headers[0].Index + 1].Text), block.name))) {
                Warn(result, "SCL 整文件包含多个程序定义或定义名称与快照块不符，未混用块身份。"); return false;
            }
            if (headers.Length == 1 && tokens.Count(t => Same(t.Text, "END_" + headers[0].Token.Text)) != 1) {
                Warn(result, "SCL 程序定义缺少唯一的结束标记，未推断执行语句。"); return false;
            }
            if (headers.Length == 0 && tokens.Any(t => Same(t.Text, "TYPE") || Same(t.Text, "DATA_BLOCK"))) {
                Warn(result, "SCL 数据声明文件未作为运行时语句分析。"); return false;
            }
            var declarationsMask = new bool[tokens.Count]; bool inside = false, fullSource = headers.Length == 1;
            bool body = !fullSource;
            for (int i = 0; i < tokens.Count; i++) {
                string word = tokens[i].Text.ToUpperInvariant();
                if (new[] { "VAR", "VAR_INPUT", "VAR_OUTPUT", "VAR_IN_OUT", "VAR_TEMP", "VAR_STATIC", "VAR_CONSTANT", "VAR_GLOBAL", "VAR_EXTERNAL" }.Contains(word)) inside = true;
                declarationsMask[i] = inside || !body;
                if (word == "END_VAR") inside = false;
                if (word == "BEGIN" && !inside) body = true;
                if (word == "END_FUNCTION" || word == "END_FUNCTION_BLOCK" || word == "END_ORGANIZATION_BLOCK") body = false;
            }
            if (inside) { Warn(result, "SCL 变量声明未闭合，该源码区域未分析。"); return false; }
            if (fullSource && !tokens.Any(t => Same(t.Text, "BEGIN"))) { Warn(result, "SCL 程序定义缺少 BEGIN，未推断执行语句。"); return false; }
            var callArguments = new HashSet<int>();
            for (int i = 0; i < tokens.Count; i++) {
                if (declarationsMask[i] || tokens[i].Text != "(") continue;
                string callee; int begin = TargetBefore(tokens, i, out callee);
                if (begin < 0 || (begin > 0 && (tokens[begin - 1].Text == "." || tokens[begin - 1].Text == "^" || tokens[begin - 1].Text == "#"))) continue;
                // An IF/WHILE condition parenthesis is not a function invocation.
                if (new[] { "IF", "ELSIF", "WHILE", "UNTIL", "NOT", "AND", "OR", "XOR", "CASE" }.Any(k => Same(k, callee))) continue;
                AnalyzeCall(block, document, region, tokens, i, callee, callArguments, result);
            }
            for (int i = 0; i < tokens.Count; i++) {
                var op = tokens[i]; if (declarationsMask[i] || (op.Text != ":=" && op.Text != "=>") || callArguments.Contains(i)) continue;
                if (op.Text == "=>") { Warn(result, "SCL 输出实参所属调用无法验证，未计作普通赋值：网络 " + region.Network + " 行 " + op.Line); continue; }
                if (op.Parent >= 0) { Warn(result, "SCL 括号内存在未验证的赋值/调用实参，未计作普通写入：网络 " + region.Network + " 行 " + op.Line); continue; }
                string target; int begin = TargetBefore(tokens, i, out target);
                if (begin < 0 || !Boundary(begin == 0 ? null : tokens[begin - 1])) {
                    Warn(result, "SCL 赋值目标含动态下标、切片、指针或未支持语法：网络 " + region.Network + " 行 " + op.Line); continue;
                }
                int end = i + 1;
                while (end < tokens.Count && !(tokens[end].Parent == -1 && tokens[end].Text == ";") && !Same(tokens[end].Text, "THEN") && !Same(tokens[end].Text, "DO")) end++;
                if (end == i + 1 || end == tokens.Count || tokens.Skip(i + 1).Take(end - i - 1).Any(t => t.Kind == "unknown" || t.Parent == -1 && (t.Text == ":=" || t.Text == "=>"))) {
                    Warn(result, "SCL 赋值语句不完整或包含未知词法单元，未推断写入：网络 " + region.Network + " 行 " + op.Line); continue;
                }
                AddWrite(block, region, tokens[begin], tokens[end - 1], target, "writes", ":=", result);
            }
            return true;
        }
        private void AnalyzeCall(IndexEntry block, XmlDocument document, Region region, List<Token> tokens, int open, string callee, HashSet<int> consumed, SclAuditResult result)
        {
            int close = tokens[open].Pair;
            var parameters = new List<Tuple<int, int, int>>(); int start = open + 1;
            for (int end = start; end <= close; end++) if (end == close || tokens[end].Parent == open && tokens[end].Text == ",") {
                if (start < end) {
                    int op = start + 1;
                    if (op < end && tokens[start].Kind == "identifier" && (tokens[op].Text == ":=" || tokens[op].Text == "=>")) { consumed.Add(op); parameters.Add(Tuple.Create(start, op, end)); }
                    else Warn(result, "SCL 调用使用未支持的位置参数或参数结构：" + callee + " / 网络 " + region.Network + " 行 " + tokens[open].Line);
                }
                start = end + 1;
            }
            var declaration = Resolve(block, document, callee, result);
            if (declaration == null) { Warn(result, "SCL 调用接口/实例身份无法唯一验证，未推断实参写入：" + callee); return; }
            var duplicates = new HashSet<string>(parameters.GroupBy(p => Unquote(tokens[p.Item1].Text), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key), StringComparer.OrdinalIgnoreCase);
            foreach (var parameter in parameters) {
                string name = Unquote(tokens[parameter.Item1].Text), direction;
                if (duplicates.Contains(name)) { Warn(result, "SCL 同一形参被重复连接，未推断该实参写入：" + callee + "." + name); continue; }
                if (!declaration.Pins.TryGetValue(name, out direction) || declaration.DuplicatePins.Contains(name)) {
                    Warn(result, "SCL 调用形参在已验证接口中缺失或不唯一：" + callee + "." + name); continue;
                }
                string op = tokens[parameter.Item2].Text;
                bool writable = direction == "InOut" || direction == "Output" || direction == "Return";
                if (!writable) { if (op != ":=") Warn(result, "SCL 输入形参连接方向不符：" + callee + "." + name); continue; }
                if (op != (direction == "InOut" ? ":=" : "=>")) { Warn(result, "SCL 输出/双向形参连接方向不符：" + callee + "." + name); continue; }
                string target;
                if (!Target(tokens, parameter.Item2 + 1, parameter.Item3, out target)) {
                    Warn(result, "SCL 输出/双向实参不是受支持的静态目标：" + callee + "." + name); continue;
                }
                string ignored; int callStart = TargetBefore(tokens, open, out ignored);
                AddWrite(block, region, tokens[parameter.Item2 + 1], tokens[parameter.Item3 - 1], target,
                    direction == "InOut" ? "reads_writes" : "writes", "call " + callee + "." + name + " (" + direction + ")", result, tokens[callStart], tokens[close]);
            }
        }
        private static void AddWrite(IndexEntry block, Region region, Token first, Token last, string target, string access, string evidence, SclAuditResult result, Token statementFirst = null, Token statementLast = null)
        {
            if (result.writes.Count >= MaxWrites) { Warn(result, "SCL 写入位置数量超过检查上限。", true); return; }
            int statementStart = (statementFirst ?? first).Start, statementEnd = (statementLast ?? last).End;
            int length = statementEnd - statementStart;
            string statement = region.Text.Substring(statementStart, Math.Min(length, MaxStatementCharacters));
            if (length > MaxStatementCharacters) { statement += " … [证据过长已截断]"; Warn(result, "SCL 语句证据超过显示上限，证据已明确截断。", true); }
            result.writes.Add(new AuditLocation {
                block = block.name, group = block.group, unit = block.unit_name, network = region.Network,
                location = "SCL region " + region.Number + " line " + first.Line + " column " + first.Column,
                symbol = target, address = "", access = access, evidence = evidence, language = "SCL",
                line = first.Line, column = first.Column, statement = statement, line_basis = region.Basis
            });
        }
        private static XmlDocument Parse(string xml)
        {
            var document = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32L * 1024 * 1024 })) document.Load(reader);
            return document;
        }
        private static Declaration ReadDeclaration(IndexEntry entry, XmlDocument document)
        {
            var roots = document.SelectNodes("//*[local-name()='SW.Blocks.FC' or local-name()='SW.Blocks.FB' or local-name()='SW.Blocks.OB' or local-name()='SW.Blocks.InstanceDB']");
            if (roots.Count != 1) return null;
            var root = roots[0]; var attributes = root.SelectSingleNode("*[local-name()='AttributeList']");
            if (attributes == null || !Same(Child(attributes, "Name"), entry.name)) return null;
            string ns = Child(attributes, "Namespace");
            if (ns.Length > 0 && !Same(ns, entry.object_namespace)) return null;
            var declaration = new Declaration { Entry = entry, Kind = root.LocalName.Substring("SW.Blocks.".Length), InstanceOf = Unquote(Child(attributes, "InstanceOfName")), InstanceType = Child(attributes, "InstanceOfType") };
            foreach (XmlNode section in attributes.SelectNodes("*[local-name()='Interface']/*[local-name()='Sections']/*[local-name()='Section']")) {
                string direction = A(section, "Name");
                foreach (XmlNode member in section.SelectNodes("*[local-name()='Member']")) {
                    string name = A(member, "Name"); if (name.Length == 0) continue;
                    if (direction == "Static") {
                        if (declaration.Statics.ContainsKey(name)) declaration.DuplicateStatics.Add(name); else declaration.Statics[name] = Unquote(A(member, "Datatype"));
                    } else if (new[] { "Input", "Output", "InOut", "Return" }.Contains(direction)) {
                        if (declaration.Pins.ContainsKey(name)) declaration.DuplicatePins.Add(name); else declaration.Pins[name] = direction;
                    }
                }
            }
            return declaration;
        }
        private void LoadDeclarations(IndexEntry caller, XmlDocument document)
        {
            if (loaded) return; loaded = true; long total = 0;
            foreach (var entry in entries) {
                try {
                    XmlDocument parsed;
                    if (Same(entry.id, caller.id)) parsed = document;
                    else {
                        string xml = source == null ? null : source(entry);
                        if (string.IsNullOrWhiteSpace(xml)) continue;
                        total += xml.Length;
                        if (xml.Length > 32 * 1024 * 1024 || total > 128L * 1024 * 1024) { declarationLimit = true; break; }
                        parsed = Parse(xml);
                    }
                    var declaration = ReadDeclaration(entry, parsed);
                    if (declaration != null) declarations.Add(declaration);
                } catch (Exception ex) { if (!(ex is XmlException || ex is IOException || ex is InvalidOperationException || ex is ArgumentException || ex is UnauthorizedAccessException || ex is KeyNotFoundException)) throw; }
            }
        }
        private Declaration Resolve(IndexEntry caller, XmlDocument document, string callee, SclAuditResult result)
        {
            LoadDeclarations(caller, document);
            if (declarationLimit) { Warn(result, "SCL 接口快照超过检查上限，无法验证调用身份唯一性。", true); return null; }
            var local = declarations.Where(d => Same(d.Entry.unit_name, caller.unit_name)).ToArray();
            if (callee.StartsWith("#", StringComparison.Ordinal)) {
                string instance = Unquote(callee.Substring(1));
                if (callee.Substring(1).Contains("[") || callee.Substring(1).Contains(".")) return null;
                var hosts = local.Where(d => Same(d.Entry.id, caller.id) && d.Kind == "FB").ToArray();
                string type;
                if (hosts.Length != 1 || !hosts[0].Statics.TryGetValue(instance, out type) || hosts[0].DuplicateStatics.Contains(instance)) return null;
                return ResolveType(local, type, caller.object_namespace);
            }
            var candidates = local.Where(d => NameMatches(d.Entry, callee, caller.object_namespace) && (d.Kind == "FC" || d.Kind == "InstanceDB")).ToArray();
            // An unread duplicate with the same identity must not be hidden by the
            // successfully parsed candidate. The catalog participates in identity.
            var catalog = entries.Where(e => Same(e.unit_name, caller.unit_name) && NameMatches(e, callee, caller.object_namespace)).ToArray();
            if (candidates.Length != 1 || catalog.Length != 1) return null;
            var candidate = candidates[0];
            if (candidate.Kind == "FC") return candidate;
            if (!Same(candidate.InstanceType, "FB") || candidate.InstanceOf.Length == 0) return null;
            return ResolveType(local, candidate.InstanceOf, candidate.Entry.object_namespace);
        }
        private Declaration ResolveType(Declaration[] local, string type, string ns)
        {
            var matches = local.Where(d => d.Kind == "FB" && NameMatches(d.Entry, type, ns)).ToArray();
            var catalog = entries.Where(e => Same(e.unit_name, local.Length == 0 ? "" : local[0].Entry.unit_name) && NameMatches(e, type, ns)).ToArray();
            return matches.Length == 1 && catalog.Length == 1 ? matches[0] : null;
        }
        private static bool NameMatches(IndexEntry entry, string text, string callerNamespace)
        {
            text = Unquote(text);
            string qualified = N(entry.object_namespace).Length == 0 ? entry.name : entry.object_namespace + "." + entry.name;
            if (N(entry.object_namespace).Length > 0 && Same(text, qualified)) return true;
            return Same(text, entry.name) && Same(entry.object_namespace, callerNamespace);
        }
    }
}
