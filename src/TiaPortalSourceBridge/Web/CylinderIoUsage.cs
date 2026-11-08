using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using TiaPortalSourceBridge.Indexing;

namespace TiaPortalSourceBridge.Web
{
    // Availability is scoped to the captured, completely read PLC program. Exact I/O
    // transport is removed before looking for uses; application logic in the SAME
    // mapping block is still inspected. No address gaps are invented as channels.
    internal sealed class CylinderIoUsage
    {
        internal sealed class State
        {
            internal string Availability, Reason, Bank;
            internal string[] Blocks;
        }
        private readonly Dictionary<string, HashSet<string>> aliases = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<string>> used = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<string>> banks = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<string>> mappedBanks = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<string>> mappedSymbols = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, State> states = new Dictionary<string, State>(StringComparer.OrdinalIgnoreCase);
        internal readonly bool Complete;
        internal readonly string Note;
        private static string Key(string s) { return ContentIndexer.NormalizeSymbol((s ?? "").Trim()); }
        private static string RowSymbol(IoRow r) { return r.mapping_fc == null && r.origin == null ? ContentIndexer.ComponentName(r.name) : r.name; }
        private static string Scope(string unit, string value) { return (unit ?? "") + "\n" + value; }
        private static IEnumerable<XmlElement> Children(XmlNode n, string name)
        { return n.ChildNodes.OfType<XmlElement>().Where(e => e.LocalName == name); }
        private static IEnumerable<string> Bits(string address)
        {
            var m = Regex.Match(IoTable.Address(address), @"^([IQ])([BWD]?)(\d+)(?:\.([0-7]))?$");
            int number;
            if (!m.Success || !int.TryParse(m.Groups[3].Value,out number) || number > 1000000) yield break;
            string width = m.Groups[2].Value;
            if (width.Length == 0 && !m.Groups[4].Success || width.Length > 0 && m.Groups[4].Success) yield break;
            int start = number * 8 + (m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : 0), count = width == "B" ? 8 : width == "W" ? 16 : width == "D" ? 32 : 1;
            for (int n=0;n<count;n++) yield return m.Groups[1].Value + (start+n)/8 + "." + (start+n)%8;
        }
        private void Alias(string unit, string symbol, IEnumerable<string> physical)
        {
            string key = Scope(unit,Key(symbol)); HashSet<string> set;
            if (!aliases.TryGetValue(key,out set)) aliases[key] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var bit in physical) set.Add(bit);
        }
        private string[] Resolve(string unit, string symbol)
        {
            HashSet<string> set;
            var result=Bits(symbol).Concat(aliases.TryGetValue(Scope(unit,Key(symbol)),out set) ? set : Enumerable.Empty<string>()).ToList();
            var db=Regex.Match(Key(symbol),@"^DB(\d+)\.DB([BWD])(\d+)$",RegexOptions.IgnoreCase);int number;
            if(db.Success && int.TryParse(db.Groups[3].Value,out number) && number<=1000000) {
                int width=db.Groups[2].Value.ToUpperInvariant()=="B"?8:db.Groups[2].Value.ToUpperInvariant()=="W"?16:32;
                for(int n=0;n<width;n++) if(aliases.TryGetValue(Scope(unit,"DB"+db.Groups[1].Value+".DBX"+(number+n/8)+"."+(n%8)),out set)) result.AddRange(set);
            }
            return result.Distinct().ToArray();
        }
        private void Mapped(string unit,string bit,string symbol,string group)
        {
            string key=Scope(unit,bit);HashSet<string> names,groups;
            if(!mappedSymbols.TryGetValue(key,out names)) mappedSymbols[key]=names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if(!mappedBanks.TryGetValue(key,out groups)) mappedBanks[key]=groups=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            names.Add(Key(symbol));groups.Add(group ?? "");
        }
        internal string PreferredSymbol(IoRow row)
        {
            HashSet<string> names;string bit=Bits(row.address).SingleOrDefault();
            return bit!=null && mappedSymbols.TryGetValue(Scope(row.unit,bit),out names) && names.Count==1?names.Single():"";
        }
        internal string[] Aliases(IoRow row)
        {
            string bit=Bits(row.address).SingleOrDefault(),prefix=(row.unit ?? "")+"\n";
            return aliases.Where(a=>a.Key.StartsWith(prefix,StringComparison.OrdinalIgnoreCase) && a.Value.Count==1 && a.Value.Contains(bit))
                .Select(a=>a.Key.Substring(prefix.Length)).ToArray();
        }
        private void Bank(string unit, string bit, string bank)
        {
            HashSet<string> set; string key=Scope(unit,bit);
            if (!banks.TryGetValue(key,out set)) banks[key]=set=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(bank)) set.Add(bank);
        }
        private static bool DeclaredDbBool(IoSnapshot snapshot,string unit,string symbol)
        {
            return snapshot.rows.Any(r => r.unit==unit && r.area=="DB" && r.data_type=="Bool" && string.Equals(Key(r.name),Key(symbol),StringComparison.OrdinalIgnoreCase));
        }
        private bool MapBit(IoSnapshot snapshot, IndexEntry block, string from, string to)
        {
            var input=Resolve(block.unit_name,from); var output=Resolve(block.unit_name,to);
            if (input.Length==1 && input[0].StartsWith("I") && DeclaredDbBool(snapshot,block.unit_name,to)) {
                Alias(block.unit_name,to,input); Mapped(block.unit_name,input[0],to,block.group); return true;
            }
            if (output.Length==1 && output[0].StartsWith("Q") && DeclaredDbBool(snapshot,block.unit_name,from)) {
                Alias(block.unit_name,from,output); Mapped(block.unit_name,output[0],from,block.group); return true;
            }
            return false;
        }
        private static XmlElement Operand(XmlElement statement,string op)
        {
            var children=statement.ChildNodes.OfType<XmlElement>().ToArray();
            var tokens=Children(statement,"StlToken").ToArray();var operands=Children(statement,"Access").ToArray();
            return children.Length==2 && tokens.Length==1 && tokens[0].GetAttribute("Text")==op && operands.Length==1 ? operands[0] : null;
        }
        private bool MapByte(IoSnapshot snapshot,IndexEntry block,XmlElement from,XmlElement to)
        {
            string a=ContentIndexer.Operand(from),b=ContentIndexer.Operand(to);
            foreach (bool input in new[] {true,false}) {
                string physical=input?a:b,db=input?b:a;
                var bits=Resolve(block.unit_name,physical);
                if (bits.Length!=8 || bits.Any(p=>!p.StartsWith(input?"I":"Q"))) continue;
                var rows=snapshot.rows.Where(r=>r.unit==block.unit_name && r.mapping_fc==block.name && r.mapping_group==block.group && bits.Contains(IoTable.Address(r.address))).ToArray();
                bool verified=rows.Any(r=>r.references.Any(reference=> {
                    // These aliases were proved by IoMapping's standard DB layout.
                    var absolute=Regex.Match(reference.evidence ?? "",@"DB(\d+)\.DBX(\d+)\.([0-7])");
                    string expected=absolute.Success?"DB"+absolute.Groups[1].Value+".DBB"+absolute.Groups[2].Value:"";
                    return expected.Length>0 && string.Equals(expected,db,StringComparison.OrdinalIgnoreCase) ||
                        Key(r.name).StartsWith(Key(db)+".%X",StringComparison.OrdinalIgnoreCase);
                }));
                if (verified) return true;
            }
            return false;
        }
        private void RemoveTransfers(IoSnapshot snapshot,IndexEntry block,XmlDocument doc)
        {
            foreach (XmlElement net in doc.SelectNodes("//*[local-name()='FlgNet']").Cast<XmlElement>().ToArray()) {
                var parts=net.SelectNodes("*[local-name()='Parts']/*");var wires=net.SelectNodes("*[local-name()='Wires']/*");
                if (parts.Count!=4 || wires.Count!=4) continue;
                var maps=ContentIndexer.Parse(net.OuterXml).mappings;
                if (maps.Count==1 && MapBit(snapshot,block,maps[0].source,maps[0].target)) net.ParentNode.RemoveChild(net);
            }
            foreach (XmlElement net in doc.SelectNodes("//*[local-name()='StatementList']").Cast<XmlElement>().ToArray()) {
                var statements=net.ChildNodes.OfType<XmlElement>().ToArray();
                for(int i=0;i+1<statements.Length;i++) {
                    var a=Operand(statements[i],"L");var b=Operand(statements[i+1],"T");
                    bool transfer=a!=null && b!=null && MapByte(snapshot,block,a,b);
                    if(!transfer) {
                        a=Operand(statements[i],"A") ?? Operand(statements[i],"U");b=Operand(statements[i+1],"=");
                        bool rloStart=i==0 || Operand(statements[i-1],"=")!=null;
                        transfer=rloStart && a!=null && b!=null && MapBit(snapshot,block,ContentIndexer.Operand(a),ContentIndexer.Operand(b));
                    }
                    if(transfer) { net.RemoveChild(statements[i]);net.RemoveChild(statements[i+1]);i++; }
                }
            }
        }
        internal CylinderIoUsage(IoSnapshot snapshot,Func<IndexEntry,string> source)
        {
            foreach(var r in snapshot.rows.Where(r=>r.area=="I" || r.area=="Q")) {
                var bits=Bits(r.address).ToArray();Alias(r.unit,RowSymbol(r),bits);
                foreach(var bit in bits) Bank(r.unit,bit,r.mapping_group ?? r.group);
                if(r.mapping_fc!=null) foreach(var reference in r.references) {
                    var m=Regex.Match(reference.evidence ?? "",@"DB\d+\.DBX\d+\.[0-7]");
                    if(m.Success) Alias(r.unit,m.Value,bits);
                }
                if(r.mapping_fc!=null && bits.Length==1) Mapped(r.unit,bits[0],r.name,r.mapping_group);
            }
            var program=snapshot.cylinderBlocks.Where(b=>b.language!="DB").ToArray();
            var docs=new List<KeyValuePair<IndexEntry,XmlDocument>>();int failed=0;
            foreach(var b in program) try { var d=IoMapping.Load(source(b));RemoveTransfers(snapshot,b,d);docs.Add(new KeyValuePair<IndexEntry,XmlDocument>(b,d)); } catch { failed++; }
            foreach(var db in snapshot.cylinderBlocks.Where(b=>b.language=="DB")) try {
                foreach(var pair in IoMapping.AbsoluteBits(db,source(db))) {
                    var physical=Resolve(db.unit_name,pair.Key);
                    if(physical.Length>0) Alias(db.unit_name,pair.Value,physical);
                }
            } catch { failed++; }
            // Scan after building all direct-map aliases, so ordering of blocks is irrelevant.
            foreach(var item in docs) try {
                var c=ContentIndexer.Parse(item.Value.OuterXml);
                foreach(var r in c.references.Where(r=>new[]{"reads","writes","reads_writes","symbol_reference","token_reference","source_reference","address"}.Contains(r.relation))) {
                    if(r.target.StartsWith("#") || (r.evidence ?? "").Contains("scope=LocalVariable")) continue;
                    var targets=Resolve(item.Key.unit_name,r.target).ToList();
                    // Whole-structure/byte/word uses reserve all known overlapping bits.
                    string prefix=Scope(item.Key.unit_name,Key(r.target));
                    foreach(var alias in aliases.Where(a=>a.Key.StartsWith(prefix+".",StringComparison.OrdinalIgnoreCase) || a.Key.StartsWith(prefix+"[",StringComparison.OrdinalIgnoreCase))) targets.AddRange(alias.Value);
                    foreach(var bit in targets.Distinct()) {
                        string key=Scope(item.Key.unit_name,bit);HashSet<string> found;
                        if(!used.TryGetValue(key,out found)) used[key]=found=new HashSet<string>();
                        found.Add(item.Key.name);
                    }
                }
            } catch { failed++; }
            Complete=snapshot.cylinderProgramsTotal>0 && snapshot.cylinderProgramsRead==snapshot.cylinderProgramsTotal &&
                program.Length==snapshot.cylinderProgramsTotal && failed==0 && snapshot.tables_read==snapshot.tables_total;
            Note="按本次源码快照检查全部 PLC 程序的显式引用，纯 I/O 映射传送不算业务占用；不包含间接寻址、HMI 或外部设备的使用。"+
                (Complete?" 未发现业务引用的已声明点位可选。":" 程序或变量表读取不完整，未查到引用的点位标记为待确认，请从博途重新读取。");
            foreach(var r in snapshot.rows.Where(r=>r.data_type=="Bool" && (r.area=="I" || r.area=="Q"))) {
                string bit=Bits(r.address).SingleOrDefault();HashSet<string> found,bank;
                bool occupied=bit!=null && used.TryGetValue(Scope(r.unit,bit),out found);used.TryGetValue(Scope(r.unit,bit),out found);
                if(!mappedBanks.TryGetValue(Scope(r.unit,bit),out bank)) banks.TryGetValue(Scope(r.unit,bit),out bank);
                HashSet<string> names;bool ambiguous=r.area=="Q" && mappedSymbols.TryGetValue(Scope(r.unit,bit),out names) && names.Count>1;
                states[r.id]=new State { Availability=occupied?"used":Complete && bit!=null && !ambiguous?"unused":"unknown",
                    Reason=occupied?"已有程序引用":ambiguous?"同一输出对应多个映射来源":Complete && bit!=null?"本次完整程序快照未发现业务引用":"引用读取不完整或地址无法核实",
                    Blocks=found==null?new string[0]:found.OrderBy(n=>n).ToArray(),Bank=bank==null?"":string.Join("|",bank.OrderBy(n=>n)) };
            }
        }
        internal State For(IoRow row) { State state;return states.TryGetValue(row.id,out state)?state:new State { Availability="unknown",Reason="未核实点位",Blocks=new string[0],Bank="" }; }
    }
}
