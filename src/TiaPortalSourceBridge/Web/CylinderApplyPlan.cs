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
    // Pure transformation of a freshly read snapshot. No client-supplied XML is used.
    internal sealed class CylinderApplyPlan
    {
        internal readonly List<Dictionary<string, object>> Items = new List<Dictionary<string, object>>();
        internal object Summary;
        private sealed class Pin
        {
            internal string Name, Type, Dir, Role;
            internal XmlElement Access, Wire;
            internal bool Open;
        }
        private sealed class Rename
        {
            internal string Pin, Address, Name;
            internal string[] Before, After;
        }
        private static IEnumerable<XmlElement> C(XmlNode n, string name = null)
        { return n == null ? Enumerable.Empty<XmlElement>() : n.ChildNodes.OfType<XmlElement>().Where(e => name == null || e.LocalName == name); }
        private static IEnumerable<XmlElement> D(XmlNode n, string name)
        { return n == null ? Enumerable.Empty<XmlElement>() : n.SelectNodes(".//*[local-name()='" + name + "']").Cast<XmlElement>(); }
        private static string A(XmlElement n, string name) { return n == null ? "" : n.GetAttribute(name); }
        private static string S(Dictionary<string, object> d, string key) { object v; return d != null && d.TryGetValue(key, out v) ? Convert.ToString(v, CultureInfo.InvariantCulture) : ""; }
        private static Dictionary<string, object> M(Dictionary<string, object> d, string key)
        { object v; Require(d.TryGetValue(key, out v) && v is Dictionary<string, object>, "缺少参数 " + key); return (Dictionary<string, object>)v; }
        private static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        private static bool Eq(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        private static XmlDocument Parse(string xml)
        {
            var doc = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { XmlResolver = null, DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 32 * 1024 * 1024 })) doc.Load(reader);
            return doc;
        }
        private static XmlElement Block(XmlDocument doc) { return C(doc.DocumentElement).SingleOrDefault(e => e.LocalName.StartsWith("SW.Blocks.", StringComparison.Ordinal)); }
        private static XmlElement Sections(XmlElement block) { return D(C(block, "AttributeList").FirstOrDefault(), "Sections").FirstOrDefault(); }
        private static XmlElement Static(XmlElement block) { return C(Sections(block), "Section").SingleOrDefault(e => A(e, "Name") == "Static"); }
        private static XmlElement Member(XmlNode parent, string name) { return C(parent, "Member").SingleOrDefault(e => Eq(A(e, "Name"), name)); }
        private static string[] Components(XmlElement access)
        { return C(C(access, "Symbol").SingleOrDefault(), "Component").Select(e => A(e, "Name")).ToArray(); }
        private static string Key(XmlElement access)
        {
            var symbol = C(access, "Symbol").FirstOrDefault();
            if (symbol == null) return "";
            return ContentIndexer.NormalizeSymbol(string.Join(".", C(symbol, "Component").Select(e => ContentIndexer.ComponentName(A(e, "Name")) +
                (A(e, "AccessModifier") == "Array" ? "[" + string.Join(",", D(e, "ConstantValue").Select(v => v.InnerText)) + "]" : ""))));
        }
        private static string Key(string[] parts) { return ContentIndexer.NormalizeSymbol(string.Join(".", parts.Select(ContentIndexer.ComponentName))); }
        private static bool Prefix(string a, string b) { return Eq(a, b) || a.StartsWith(b + ".", StringComparison.OrdinalIgnoreCase) || a.StartsWith(b + "[", StringComparison.OrdinalIgnoreCase); }
        private static void ValidateOperand(XmlElement access)
        {
            string scope = A(access, "Scope");
            if (scope == "LiteralConstant") {
                var values = D(access, "ConstantValue").ToArray();
                Require(values.Length == 1 && Regex.IsMatch(values[0].InnerText, @"^(true|false|-?\d+)$", RegexOptions.IgnoreCase), "不支持的常量参数"); return;
            }
            Require(scope == "GlobalVariable" && C(access).Count() == 1 && C(access, "Symbol").Count() == 1, "只支持全局符号和简单常量参数");
            var parts = C(C(access, "Symbol").Single()).ToArray();
            Require(parts.Length > 0, "参数缺少符号");
            foreach (var part in parts) {
                Require(part.LocalName == "Component" && A(part, "Name").Length > 0 && !Regex.IsMatch(A(part, "Name"), "[\r\n\"]"), "不支持的符号成员");
                string modifier = A(part, "AccessModifier");
                Require(modifier == "" && !C(part).Any() || modifier == "Array" && C(part).Any(), "不支持的符号修饰符");
                foreach (var index in C(part)) Require(index.LocalName == "Access" && A(index, "Scope") == "LiteralConstant" && D(index, "ConstantValue").Count() == 1 && Regex.IsMatch(D(index, "ConstantValue").Single().InnerText, @"^-?\d+$"), "不支持动态数组下标");
            }
        }
        private static List<Pin> ReadPins(XmlElement unit, out XmlElement call)
        {
            var nets = D(unit, "FlgNet").ToArray();
            Require(nets.Length == 1 && D(unit, "CallInfo").Count() == 1, "样板必须是单一直接 FB 调用网络");
            var parts = C(C(nets[0], "Parts").SingleOrDefault()).ToArray();
            Require(parts.Count(p => p.LocalName == "Call") == 1 && parts.All(p => p.LocalName == "Call" || p.LocalName == "Access"), "样板含附加逻辑，不能直接生成");
            Require(parts.All(p => A(p, "UId").Length > 0) && parts.Select(p => A(p, "UId")).Distinct().Count() == parts.Length, "样板元件编号重复");
            call = parts.Single(p => p.LocalName == "Call");
            Require(C(call).Count() == 1, "不支持带修饰的调用");
            var info = C(call, "CallInfo").SingleOrDefault();
            Require(A(info, "BlockType") == "FB" && C(info).All(n => n.LocalName == "Instance" || n.LocalName == "Parameter"), "不支持的 FB 调用选项");
            var pins = C(info, "Parameter").Select(p => new Pin { Name = A(p,"Name"), Type = A(p,"Type"), Dir = A(p,"Section") }).ToList();
            Require(pins.Count > 0 && pins.All(p => p.Name.Length > 0 && new[] { "Input", "Output", "InOut" }.Contains(p.Dir)) && pins.Select(p => p.Name.ToLowerInvariant()).Distinct().Count() == pins.Count, "样板形式参数不完整");
            bool en = false, eno = false;
            foreach (var wire in C(C(nets[0], "Wires").SingleOrDefault())) {
                var names = C(wire,"NameCon").ToArray();
                Require(wire.LocalName == "Wire" && C(wire).Count() == 2 && names.Length == 1 && A(names[0],"UId") == A(call,"UId"), "样板存在分支或无法还原的连线");
                string name = A(names[0],"Name");
                if (name == "en") { Require(!en && C(wire,"Powerrail").Count() == 1, "样板有使能条件或重复 EN"); en = true; continue; }
                if (name == "eno") { Require(!eno && C(wire,"OpenCon").Count() == 1, "样板 ENO 已连接其他逻辑"); eno = true; continue; }
                var pin = pins.SingleOrDefault(p => p.Name == name);
                Require(pin != null && pin.Wire == null, "样板参数缺失或重复连接：" + name); pin.Wire = wire;
                if (C(wire,"OpenCon").Count() == 1) { Require(pin.Dir != "InOut", "InOut 参数未连接"); pin.Open = true; continue; }
                var ident = C(wire,"IdentCon").SingleOrDefault();
                pin.Access = parts.SingleOrDefault(p => p.LocalName == "Access" && A(p,"UId") == A(ident,"UId"));
                Require(pin.Access != null, "参数没有可核对的符号：" + name); ValidateOperand(pin.Access);
            }
            Require(en && pins.All(p => p.Wire != null), "样板缺少 EN 或参数连线记录");
            return pins;
        }
        private static string Suffix(Pin pin, string role)
        {
            var known = new Dictionary<string,string[]> { { "i_Wpos", new[] {"input","伸出到位","伸出位"} }, { "i_Hpos",new[] {"input","缩回到位","缩回位"} },
                {"oOutput_W",new[] {"output","伸出阀","伸出电磁阀","伸出输出"}}, {"oOutput_H",new[] {"output","缩回阀","缩回电磁阀","缩回输出"}} };
            string[] expected; known.TryGetValue(pin.Name, out expected);
            Require(expected == null || expected[0] == role, "点位参数方向不正确：" + pin.Name);
            string leaf = Regex.Replace(Components(pin.Access).LastOrDefault() ?? "", @"\s*%?[IEQA]\s*\d+\.[0-7]\s*$", "", RegexOptions.IgnoreCase).Trim();
            var options = expected == null ? known.Values.Where(v => v[0] == role).SelectMany(v => v.Skip(1)) : expected.Skip(1);
            string suffix = options.OrderByDescending(s => s.Length).FirstOrDefault(s => leaf.EndsWith(s,StringComparison.Ordinal));
            Require(suffix != null || expected != null, "无法核对点位伸出/缩回方向：" + pin.Name);
            return suffix ?? expected[1];
        }
        private static string[] SplitSymbol(string symbol)
        {
            var tokens = Regex.Matches(symbol, "\"(?:[^\"]|\"\")*\"|[\\p{L}_][\\p{L}\\p{N}_]*").Cast<Match>().ToArray();
            Require(tokens.Length >= 3, "当前直接写入仅支持已核对的映射 DB 位点，请选择映射点位");
            var parts = tokens.Select(t => t.Value.StartsWith("\"") ? t.Value.Substring(1,t.Value.Length-2).Replace("\"\"","\"") : t.Value).ToArray();
            Require(Eq(Key(parts),ContentIndexer.NormalizeSymbol(symbol)), "点位符号包含不支持的数组或地址表达式"); return parts;
        }
        private static XmlElement FindField(XmlElement db, string[] parts)
        {
            XmlElement node = Static(db);
            for (int i=1;i<parts.Length;i++) { node = Member(node,parts[i]); Require(node != null, "找不到映射 DB 成员：" + Key(parts)); }
            return node;
        }
        private static XmlElement TemplateMember(XmlElement donor, string name, string display)
        {
            // Keep the exported typed declaration, including configured start values and
            // expanded member interfaces; TIA re-exports those for readback verification.
            // Exported declarations do not contain running PLC actual values.
            Require(A(donor,"Datatype").StartsWith("\"", StringComparison.Ordinal), "自动新增成员要求已命名 FB/UDT 类型");
            Require(donor.SelectNodes(".//@ID").Count == 0,"类型声明带有不能复制的对象标识");
            var member = (XmlElement)donor.CloneNode(true); member.SetAttribute("Name",name);
            foreach(var comment in C(member,"Comment")) foreach(var text in C(comment)) text.InnerText=name+" "+display;
            return member;
        }
        private static void SetSymbol(XmlElement access, string[] parts)
        {
            var symbol = C(access,"Symbol").SingleOrDefault();
            if(symbol==null) { foreach(var child in C(access).ToArray())access.RemoveChild(child); symbol=access.OwnerDocument.CreateElement("Symbol",access.NamespaceURI); access.AppendChild(symbol); }
            access.SetAttribute("Scope","GlobalVariable"); symbol.RemoveAll();
            foreach (string name in parts) { var c = access.OwnerDocument.CreateElement("Component",symbol.NamespaceURI); c.SetAttribute("Name",name); symbol.AppendChild(c); }
        }
        private static void CheckCompileUnitOrder(XmlNode objects)
        {
            bool seen=false, closed=false;
            foreach(var item in C(objects)) {
                bool unit=item.LocalName=="SW.Blocks.CompileUnit";
                Require(!unit || !closed,"程序网络的 CompileUnits 必须连续排列，不能被标题或注释分隔");
                if(unit) seen=true; else if(seen) closed=true;
            }
        }
        internal static CylinderApplyPlan Build(IoSnapshot snapshot, IndexEntry host, Func<IndexEntry,string> source, Dictionary<string,object> request)
        {
            Require(snapshot != null && snapshot.cylinderBlocks != null && host != null && snapshot.cylinderBlocks.Any(b => b.id == host.id), "宿主不属于当前工程快照");
            Require(string.IsNullOrEmpty(host.unit_name), "当前自动写入暂不支持软件单元内的气缸，请在普通 PLC 程序块中使用");
            string name = S(request,"instance_name"), display = S(request,"cylinder_name").Trim(), sample = S(request,"sample_id");
            Require(Regex.IsMatch(name,@"^[\p{L}_][\p{L}\p{N}_]{0,63}$"), "实例名应为 1–64 位字母、汉字、数字或下划线，首位不能是数字");
            Require(display.Length > 0 && display.Length <= 64 && !Regex.IsMatch(display,"[\x00-\x1f\x7f\"\\\\]"), "请填写有效的气缸名称");
            var rules = M(request,"rules"); var bindings = M(request,"bindings");
            var original = snapshot.cylinderBlocks.Where(b => b.unit_name == host.unit_name).ToDictionary(b => b.id,b => source(b));
            var docs = original.ToDictionary(p => p.Key,p => Parse(p.Value));
            var changed = new HashSet<string>();
            var hostDoc = docs[host.id]; var fb = Block(hostDoc);
            Require(fb != null && fb.LocalName == "SW.Blocks.FB", "宿主必须是 FB");
            var allCalls = D(fb,"CallInfo").ToArray();
            Require(!allCalls.Any(c => D(C(c,"Instance").FirstOrDefault(),"Component").Any(p => Eq(A(p,"Name"),name))), "宿主已调用同名实例：" + name);
            var donorUnits = D(fb,"SW.Blocks.CompileUnit").Where(u => D(u,"CallInfo").Any(c => C(C(c,"Instance").FirstOrDefault(),"Component").Any(p => Eq(A(p,"Name"),sample)))).ToArray();
            Require(donorUnits.Length == 1, "找不到唯一气缸样板，请重新读取");
            CheckCompileUnitOrder(donorUnits[0].ParentNode);
            var newUnit = (XmlElement)donorUnits[0].CloneNode(true); XmlElement call;
            var pins = ReadPins(newUnit,out call); var info = C(call,"CallInfo").Single(); var inst = C(info,"Instance").SingleOrDefault();
            var donorKeys = pins.Where(p=>!p.Open).ToDictionary(p=>p.Name,p=>Key(p.Access));
            Require(A(inst,"Scope") == "LocalVariable" && C(inst).Count() == 1 && Eq(A(C(inst,"Component").SingleOrDefault(),"Name"),sample), "样板不是直接 Static 多重实例");
            var stat = Static(fb); var donorDeclaration = Member(stat,sample);
            Require(donorDeclaration != null && Eq(A(donorDeclaration,"Datatype").Trim('"'),A(info,"Name")), "样板 Static 类型不一致");
            var existing = C(Sections(fb),"Section").SelectMany(s => C(s,"Member")).Where(m => Eq(A(m,"Name"),name)).ToArray();
            Require(existing.Length <= 1 && (existing.Length == 0 || existing[0].ParentNode == stat && Eq(A(existing[0],"Datatype"),A(donorDeclaration,"Datatype"))), "已有同名声明的区域或类型不一致");
            // Local accesses can reference a declared, not yet called instance; do not reuse it then.
            Require(!D(fb,"Access").Where(a => A(a,"Scope") == "LocalVariable").Any(a => Components(a).Any(p => Eq(p,name))), "宿主中的同名实例已被其他逻辑使用");
            if (existing.Length == 0) stat.AppendChild(TemplateMember(donorDeclaration,name,display));
            C(inst,"Component").Single().SetAttribute("Name",name);
            Require(rules.Count == pins.Count && rules.Keys.All(k => pins.Any(p => p.Name == k)), "模板规则与样板参数不一致，请重新读取");
            var usage = new CylinderIoUsage(snapshot,source); Require(usage.Complete,"工程引用读取不完整，不能验证空闲点，请从博途重新读取");
            var references = snapshot.cylinderBlocks.Where(b => b.unit_name == host.unit_name && b.language != "DB").SelectMany(b => ContentIndexer.Parse(original[b.id]).references).ToArray();
            var renames = new List<Rename>(); var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var newData = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sharedResets = new Dictionary<string,bool>(StringComparer.OrdinalIgnoreCase);
            Func<string,IndexEntry> dbEntry = dbName => { var entries = snapshot.cylinderBlocks.Where(b => b.unit_name == host.unit_name && b.language == "DB" && Eq(b.name,dbName)).ToArray(); Require(entries.Length == 1,"找不到唯一数据块："+dbName); return entries[0]; };
            // Separate each pin's access so a shared donor operand can be assigned different new values.
            var partsNode = C(D(newUnit,"FlgNet").Single(),"Parts").Single();
            int nextUid = D(newUnit,"FlgNet").Single().SelectNodes(".//@UId").Cast<XmlAttribute>().Select(a => { int n; return int.TryParse(a.Value,out n)?n:0; }).DefaultIfEmpty().Max()+1;
            foreach (var pin in pins.Where(p => !p.Open)) {
                var copy = (XmlElement)pin.Access.CloneNode(true); copy.SetAttribute("UId",(nextUid++).ToString(CultureInfo.InvariantCulture));
                partsNode.AppendChild(copy); C(pin.Wire,"IdentCon").Single().SetAttribute("UId",A(copy,"UId")); pin.Access=copy;
            }
            foreach (var pin in pins) {
                pin.Role = S(rules,pin.Name); string role=pin.Role;
                if(pin.Open) { Require(role=="open","未连接参数必须保持未连接："+pin.Name); continue; }
                var components=C(C(pin.Access,"Symbol").FirstOrDefault(),"Component").ToArray();
                var device=components.Length>=2 && Eq(A(components[1],"Name"),sample) && !C(components[1]).Any();
                var indexed=components.Where(c=>A(c,"AccessModifier")=="Array").ToArray();
                bool feedback=pin.Dir=="Input" && Eq(pin.Type,"Bool") && pins.Count(p=>p.Dir=="Output" && !p.Open && Eq(p.Type,"Bool") && donorKeys[p.Name]==donorKeys[pin.Name])==1;
                Require(role=="device" && device || role=="index" && indexed.Length==1 || role=="shared" && pin.Dir=="Input" && !device && !feedback || role=="feedback" && feedback ||
                    (role=="input" || role=="output") && Eq(pin.Type,"Bool") && pin.Dir==(role=="input"?"Input":"Output"),"不允许的参数规则："+pin.Name);
                if(role=="input" || role=="output") {
                    var row=snapshot.rows.SingleOrDefault(r=>r.id==S(bindings,pin.Name) && r.unit==host.unit_name && Eq(r.data_type,"Bool") && r.area==(role=="input"?"I":"Q"));
                    Require(row!=null,"所选点位已失效："+pin.Name); string address=IoTable.Address(row.address);
                    Require(Regex.IsMatch(address,@"^[IQ]\d+\.[0-7]$") && addresses.Add(address),"点位地址不明确或多个参数使用同一地址");
                    Require(usage.For(row).Availability=="unused","点位已使用或无法确认空闲："+address);
                    string preferred=usage.PreferredSymbol(row);
                    Require(preferred.Length>0,"当前直接写入仅支持已核对的映射 DB 点位："+address);
                    string[] before=SplitSymbol(preferred), after=(string[])before.Clone();
                    string newName=display+Suffix(pin,role)+address; Require(newName.Length<=128,"生成点位名称过长"); after[after.Length-1]=newName;
                    var entry=dbEntry(before[0]); var field=FindField(Block(docs[entry.id]),before);
                    Require(Eq(A(field,"Datatype"),"Bool") && !C(field,"Member").Any(),"点位不是直接 Bool 成员");
                    Require(!C(field.ParentNode,"Member").Any(m=>m!=field && Eq(A(m,"Name"),newName)),"已存在生成的点位名："+newName);
                    field.SetAttribute("Name",newName);
                    foreach(var text in D(field,"Comment").SelectMany(c=>D(c,"Text"))) text.InnerText=newName;
                    changed.Add(entry.id); SetSymbol(pin.Access,after);
                    renames.Add(new Rename {Pin=pin.Name,Address=address,Name=newName,Before=before,After=after});
                } else if(role=="device") {
                    var entry=dbEntry(A(components[0],"Name")); var section=Static(Block(docs[entry.id])); var donor=Member(section,sample); var member=Member(section,name);
                    Require(donor!=null && (member==null || Eq(A(member,"Datatype"),A(donor,"Datatype"))),"气缸数据成员不存在或新成员类型冲突");
                    string prefix=Key(new[]{entry.name,name});
                    Require(!references.Where(r=>new[]{"reads","writes","reads_writes","symbol_reference","token_reference","source_reference"}.Contains(r.relation))
                        .Any(r => Prefix(ContentIndexer.NormalizeSymbol(r.target),prefix) || Prefix(prefix,ContentIndexer.NormalizeSymbol(r.target))),"气缸数据成员或其父结构已被现有程序使用："+prefix);
                    Require(!references.Any(r=>Regex.IsMatch(ContentIndexer.NormalizeSymbol(r.target),@"^%?DB"+Regex.Escape(entry.number)+@"(?:\.|$)",RegexOptions.IgnoreCase)),"气缸数据块存在绝对地址引用，不能自动核对新成员："+entry.name);
                    if(member==null) { section.AppendChild(TemplateMember(donor,name,display)); changed.Add(entry.id); }
                    components[1].SetAttribute("Name",name); newData.Add(prefix);
                } else if(role=="index") {
                    int index; Require(Regex.IsMatch(S(request,"alarm_index"),@"^\d{1,9}$") && int.TryParse(S(request,"alarm_index"),out index),"请填写有效的新数组索引");
                    index=int.Parse(S(request,"alarm_index"),CultureInfo.InvariantCulture);
                    Require(components.Length>=2 && indexed[0]==components[1],"只支持可核对的顶层数组");
                    var entry=dbEntry(A(components[0],"Name")); var field=Member(Static(Block(docs[entry.id])),A(components[1],"Name"));
                    var bounds=Regex.Match(A(field,"Datatype"),@"^Array\s*\[\s*(-?\d+)\s*\.\.\s*(-?\d+)",RegexOptions.IgnoreCase);
                    Require(bounds.Success && index>=int.Parse(bounds.Groups[1].Value,CultureInfo.InvariantCulture) && index<=int.Parse(bounds.Groups[2].Value,CultureInfo.InvariantCulture),"数组索引越界或范围未核实");
                    string arrayRoot=Key(new[]{A(components[0],"Name"),A(components[1],"Name")});
                    string dbRoot=Key(new[]{A(components[0],"Name")});
                    // SCL references may have no inferred read/write direction. Reserve a
                    // cylinder's entire first dimension, not merely one output column.
                    var indexConflict=references.FirstOrDefault(r=> {
                        if(!new[]{"reads","writes","reads_writes","symbol_reference","token_reference","source_reference","address"}.Contains(r.relation))return false;
                        string target=ContentIndexer.NormalizeSymbol(r.target);
                        if(Regex.IsMatch(target,@"^%?DB"+Regex.Escape(entry.number)+@"(?:\.|$)",RegexOptions.IgnoreCase))return true;
                        if(Eq(target,dbRoot) && (r.relation=="token_reference" || r.relation=="source_reference"))return true;
                        if(Eq(target,arrayRoot))return true;
                        if(!target.StartsWith(arrayRoot+"[",StringComparison.OrdinalIgnoreCase))return false;
                        var first=Regex.Match(target.Substring(arrayRoot.Length),@"^\[\s*(-?\d+)\s*(?:,|\])");int occupied;
                        return !first.Success || !int.TryParse(first.Groups[1].Value,out occupied) || occupied==index;
                    });
                    Require(indexConflict==null,"数组第一维索引已被使用或存在无法核对的数组引用："+arrayRoot+"["+index+"]"+(indexConflict==null?"":"（"+indexConflict.target+"）"));
                    D(indexed[0],"ConstantValue").First().InnerText=index.ToString(CultureInfo.InvariantCulture);
                }
                if(pin.Dir!="Input" && role!="feedback") {
                    string target=Key(pin.Access); Require(target.Length>0 && targets.Add(target),"两个参数写入同一目标");
                    var conflict=references.FirstOrDefault(r=> {
                        if((r.relation!="writes" && r.relation!="reads_writes") || !(Prefix(target,ContentIndexer.NormalizeSymbol(r.target)) || Prefix(ContentIndexer.NormalizeSymbol(r.target),target))) return false;
                        // An existing, fully inspected aggregate/reset FC may own the alarm
                        // DB InOut. It is not another cylinder writer of a particular index.
                        if(role=="index" && components.Length>=2 && r.relation=="reads_writes" && Eq(ContentIndexer.NormalizeSymbol(r.target),Key(new[]{A(components[0],"Name")})) && (r.evidence??"").StartsWith("native pin ",StringComparison.Ordinal)) {
                            string resetKey=A(components[0],"Name")+"\n"+A(components[1],"Name");bool verified;
                            if(!sharedResets.TryGetValue(resetKey,out verified)) sharedResets[resetKey]=verified=CylinderAlarmReset.IsSharedReset(A(components[0],"Name"),A(components[1],"Name"),snapshot.cylinderBlocks,source);
                            if(verified)return false;
                        }
                        return true;
                    });
                    Require(conflict==null,"写入目标已被使用："+target+(conflict==null?"":"（"+conflict.target+"；"+conflict.evidence+"）"));
                }
            }
            Require(bindings.Keys.All(k=>pins.Any(p=>p.Name==k && (p.Role=="input" || p.Role=="output"))),"点位选择包含未知参数");
            foreach(var pin in pins.Where(p=>p.Role=="feedback")) {
                // Locate correspondence in the original donor, since new output symbols have already changed.
                XmlElement originalCall; var originalPins=ReadPins(donorUnits[0],out originalCall);
                string donorKey=Key(originalPins.Single(p=>p.Name==pin.Name).Access);
                var output=originalPins.SingleOrDefault(p=>p.Dir=="Output" && !p.Open && Eq(p.Type,"Bool") && Key(p.Access)==donorKey);
                Require(output!=null && pins.Single(p=>p.Name==output.Name).Role=="output","反馈参数必须绑定对应的新输出："+pin.Name);
                SetSymbol(pin.Access,Components(pins.Single(p=>p.Name==output.Name).Access));
            }
            Require(renames.Count>0,"请至少选择一个新的输入或输出点位");
            // Rewrite only structured exact mapped operands. Usage analysis already proved no business use.
            foreach(var entry in snapshot.cylinderBlocks.Where(b=>b.unit_name==host.unit_name && b.language!="DB")) {
                foreach(var access in D(docs[entry.id],"Access").Where(a=>A(a,"Scope")=="GlobalVariable")) {
                    var comps=Components(access); var rename=renames.SingleOrDefault(r=>r.Before.SequenceEqual(comps,StringComparer.OrdinalIgnoreCase));
                    if(rename==null)continue;
                    Require(C(C(access,"Symbol").Single(),"Component").All(c=>!C(c).Any()),"映射引用含不支持的数组修饰");
                    SetSymbol(access,rename.After); changed.Add(entry.id);
                }
                // Source-only text references cannot be safely renamed by text replacement.
                var remaining=ContentIndexer.Parse(docs[entry.id].OuterXml).references;
                Require(!remaining.Any(r=>renames.Any(n=>Eq(ContentIndexer.NormalizeSymbol(r.target),Key(n.Before)))),"存在无法结构化重命名的映射引用："+entry.name);
            }
            // Remove now-unused donor accesses, retaining only actual wire operands in the new network.
            var usedIds=new HashSet<string>(D(newUnit,"IdentCon").Select(n=>A(n,"UId")));
            foreach(var access in C(partsNode,"Access").Where(a=>!usedIds.Contains(A(a,"UId"))).ToArray()) partsNode.RemoveChild(access);
            long maxId=hostDoc.SelectNodes("//@ID").Cast<XmlAttribute>().Select(a=>long.Parse(a.Value,NumberStyles.HexNumber,CultureInfo.InvariantCulture)).DefaultIfEmpty().Max();
            foreach(var id in newUnit.SelectNodes(".//@ID").Cast<XmlAttribute>()) id.Value=(++maxId).ToString("X",CultureInfo.InvariantCulture);
            newUnit.SetAttribute("ID",(++maxId).ToString("X",CultureInfo.InvariantCulture));
            foreach(var title in D(newUnit,"MultilingualText").Where(t=>A(t,"CompositionName")=="Title")) foreach(var text in D(title,"Text")) text.InnerText=name+" "+display;
            foreach(var comment in D(newUnit,"MultilingualText").Where(t=>A(t,"CompositionName")=="Comment")) foreach(var text in D(comment,"Text")) text.InnerText="由气缸模板生成："+name+" "+display;
            // CompileUnits is an ordered composition. It must stay one contiguous
            // group: re-entering it after the block's trailing Title makes TIA reject
            // the import with "Objects modeled as IOrdered must be empty".
            var objectList=donorUnits[0].ParentNode;
            objectList.InsertAfter(newUnit,C(objectList,"SW.Blocks.CompileUnit").Last());
            CheckCompileUnitOrder(objectList); changed.Add(host.id);
            var plan=new CylinderApplyPlan();
            foreach(var entry in snapshot.cylinderBlocks.Where(b=>changed.Contains(b.id)).OrderBy(b=>b.id==host.id?2:b.language=="DB"?0:1).ThenBy(b=>b.name,StringComparer.Ordinal))
                plan.Items.Add(new Dictionary<string,object>{{"xml",docs[entry.id].OuterXml},{"group_path",string.IsNullOrEmpty(entry.group)?"/":entry.group},
                    {"expected_content_signature",TiaPortalSourceBridge.Mcp.XmlBatch.ContentSignature(original[entry.id])},
                    {"expected_recovery_signature",TiaPortalSourceBridge.Mcp.XmlBatch.RecoverySignature(original[entry.id])}});
            plan.Summary=new { instance_name=name,cylinder_name=display,host=host.name,blocks=plan.Items.Count,renames=renames.Select(r=>new {pin=r.Pin,address=r.Address,old_symbol=Key(r.Before),new_symbol=Key(r.After)}).ToArray(),
                shared_alarm_reset=sharedResets.Values.Any(v=>v),
                note="新增气缸调用及数据声明，保持样板公共条件。新增成员复制样板声明及配置的起始值，不复制在线实际值。仅修改离线工程；不下载 PLC。" +
                    (sharedResets.Values.Any(v=>v)?" 新报警索引沿用已核实的公共复位与汇总程序。":"") };
            return plan;
        }
    }
}
