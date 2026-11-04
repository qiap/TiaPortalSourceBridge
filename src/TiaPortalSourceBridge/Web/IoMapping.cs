using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using TiaPortalSourceBridge.Indexing;

namespace TiaPortalSourceBridge.Web
{
    internal static class IoMapping
    {
        private sealed class MemberBit { public string Path, Comment; }
        private static IEnumerable<XmlElement> Children(XmlNode n, string name)
        { return n.ChildNodes.OfType<XmlElement>().Where(e => e.LocalName == name); }

        // Standard DB only. Stop at the first unrecognised storage declaration:
        // every subsequent absolute offset would otherwise be a guess.
        private static bool Layout(XmlNode section, string prefix, ref int bit, Dictionary<int,MemberBit> fields)
        {
            foreach (var m in Children(section, "Member")) {
                string type = m.GetAttribute("Datatype"), name = ContentIndexer.ComponentName(m.GetAttribute("Name"));
                string path = prefix.Length == 0 ? name : prefix + "." + name;
                if (m.HasAttribute("At") || m.HasAttribute("Offset") || m.SelectSingleNode("*[local-name()='AttributeList']/*[@Name='AT']") != null) return false;
                if (type == "Bool") {
                    var comment = m.SelectNodes("*[local-name()='Comment']/*").Cast<XmlNode>().Select(n => n.InnerText);
                    fields.Add(bit++, new MemberBit { Path = path, Comment = string.Join(" / ", comment) });
                } else if (type == "Struct") {
                    bit = Align(bit, 16);
                    if (!Layout(m, path, ref bit, fields)) return false;
                    bit = Align(bit, 16);
                } else {
                    var array = Regex.Match(type, @"^Array\[\s*(-?\d+)\s*\.\.\s*(-?\d+)\s*\]\s+of\s+Byte$", RegexOptions.IgnoreCase);
                    if (array.Success) {
                        int count = checked(int.Parse(array.Groups[2].Value) - int.Parse(array.Groups[1].Value) + 1);
                        if (count <= 0) return false;
                        if (count > 32768) return false;
                        int first = int.Parse(array.Groups[1].Value);
                        bit = Align(bit,16);
                        string comment = IoDeclarations.Comment(m);
                        for (int index = 0; index < count; index++)
                            for (int n = 0; n < 8; n++) fields.Add(bit++, new MemberBit { Path = path + "[" + (first + index) + "].%X" + n, Comment = comment });
                        bit = Align(bit,16);
                    } else if (type == "Byte" || type == "Char" || type == "USInt" || type == "SInt") bit = Align(bit, 8) + 8;
                    else if (type == "Int" || type == "UInt" || type == "Word") bit = Align(bit, 16) + 16;
                    else if (type == "DInt" || type == "UDInt" || type == "Real" || type == "DWord") bit = Align(bit, 16) + 32;
                    else return false;
                }
            }
            return true;
        }
        private static int Align(int bits, int width) { return checked((bits + width - 1) / width * width); }
        internal static XmlDocument Load(string source)
        {
            var doc = new XmlDocument { XmlResolver = null };
            using (var r = XmlReader.Create(new StringReader(source), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) doc.Load(r);
            return doc;
        }
        private static Dictionary<int,MemberBit> Fields(IndexEntry db, string source)
        {
            var fields = new Dictionary<int,MemberBit>();
            var attributes = Load(source).SelectSingleNode("/Document/SW.Blocks.GlobalDB/AttributeList");
            if (attributes == null || attributes.SelectSingleNode("MemoryLayout[.='Standard']") == null ||
                attributes.SelectSingleNode("Number").InnerText != db.number || attributes.SelectSingleNode("Name").InnerText != db.name) return fields;
            var section = attributes.SelectSingleNode("Interface/*[local-name()='Sections']/*[local-name()='Section' and @Name='Static']");
            int bit = 0;
            if (section != null) Layout(section, "", ref bit, fields);
            return fields;
        }
        private static XmlElement SingleOperand(XmlElement statement, string op)
        {
            var tokens = Children(statement,"StlToken").ToArray();
            var accesses = Children(statement,"Access").ToArray();
            if (tokens.Length != 1 || tokens[0].GetAttribute("Text") != op || accesses.Length != 1 ||
                statement.ChildNodes.OfType<XmlElement>().Any(x => x.LocalName != "Access" && x.LocalName != "StlToken")) return null;
            return accesses[0];
        }
        private static string PhysicalKey(XmlElement access)
        {
            var addr = Children(access, "Address").FirstOrDefault();
            int offset;
            if (addr != null && addr.GetAttribute("Type") == "Byte" && int.TryParse(addr.GetAttribute("BitOffset"), out offset) && offset >= 0 && offset % 8 == 0)
                return "%" + addr.GetAttribute("Area") + "B" + offset / 8;
            var symbol = Children(access,"Symbol").SingleOrDefault();
            if (access.GetAttribute("Scope") != "GlobalVariable" || symbol == null) return "";
            var parts = Children(symbol,"Component").ToArray();
            return parts.Length == 1 && !parts[0].HasChildNodes ? "tag:" + parts[0].GetAttribute("Name") : "";
        }
        internal static void Expand(IoSnapshot result, IndexEntry[] entries, Func<IndexEntry,string> source)
        {
            var tags = result.rows.Where(r => r.data_type.Equals("Byte", StringComparison.OrdinalIgnoreCase) &&
                Regex.IsMatch(IoTable.Address(r.address), @"^[IQ]B\d+$")).ToArray();
            var lookup = tags.SelectMany(t => new[] { new { key = t.unit + "|tag:" + t.name, tag = t },
                new { key = t.unit + "|%" + IoTable.Address(t.address), tag = t } }).ToLookup(x => x.key, x => x.tag, StringComparer.OrdinalIgnoreCase);
            var dbs = entries.Where(e => e.language == "DB").ToLookup(e => e.unit_name + "|" + e.number);
            var layouts = new Dictionary<string,Dictionary<int,MemberBit>>();
            var mapped = new Dictionary<string,IoRow>();
            foreach (var fc in entries.Where(e => e.language != "DB")) {
                try {
                    // The container language can be LAD even when a network is STL.
                    string xml = source(fc);
                    if (xml.IndexOf("StatementList",StringComparison.Ordinal) < 0) continue;
                    var doc = Load(xml);
                    int network = 0;
                    foreach (XmlNode net in doc.SelectNodes("//*[local-name()='StatementList']")) {
                        network++;
                        XmlElement previous = null;
                        foreach (var statement in net.ChildNodes.OfType<XmlElement>()) {
                            var load = previous == null ? null : SingleOperand(previous,"L");
                            var transfer = SingleOperand(statement,"T");
                            previous = statement;
                            if (load == null || transfer == null) continue;
                            foreach (bool input in new[] { true, false }) {
                                var physical = input ? load : transfer;
                                var dbAccess = input ? transfer : load;
                                foreach (var tag in lookup[fc.unit_name + "|" + PhysicalKey(physical)].Where(t => t.area == (input ? "I" : "Q"))) {
                                    var addr = Children(dbAccess,"Address").SingleOrDefault();
                                    int bit = -1;
                                    string symbolic = dbAccess.GetAttribute("Scope") == "GlobalVariable" ? ContentIndexer.Operand(dbAccess) : "";
                                    IndexEntry[] candidates = new IndexEntry[0];
                                    if (addr != null && addr.GetAttribute("Type") == "Byte" && addr.GetAttribute("Area") == "DB" &&
                                        int.TryParse(addr.GetAttribute("BitOffset"),out bit) && bit >= 0 && bit % 8 == 0)
                                        candidates = dbs[fc.unit_name + "|" + addr.GetAttribute("BlockNumber")].ToArray();
                                    else if (symbolic.Length > 0)
                                        candidates = entries.Where(e => e.language == "DB" && e.unit_name == fc.unit_name &&
                                            symbolic.StartsWith(ContentIndexer.ComponentName(e.name) + ".",StringComparison.Ordinal)).ToArray();
                                    string why = "";
                                    int resolved = 0;
                                    if (candidates.Length == 0)
                                        why = "源/目标为 " + ContentIndexer.Operand(dbAccess) + "，未定位到标准 DB 的 Bool 成员";
                                    else {
                                        if (candidates.Length != 1) why = "DB 编号未唯一匹配";
                                        else {
                                            var db = candidates[0];
                                            Dictionary<int,MemberBit> fields;
                                            if (!layouts.TryGetValue(db.id,out fields)) layouts[db.id] = fields = Fields(db,source(db));
                                            if (symbolic.Length > 0) {
                                                string firstBit = symbolic + ".%X0";
                                                var startBits = fields.Where(f => ContentIndexer.ComponentName(db.name) + "." + f.Value.Path == firstBit).Take(2).ToArray();
                                                bit = startBits.Length == 1 ? startBits[0].Key : -1;
                                            }
                                            for (int i = 0; bit >= 0 && i < 8; i++) {
                                                MemberBit member; if (!fields.TryGetValue(bit + i,out member)) continue;
                                                string path = db.name + "." + member.Path, address = "%" + tag.area + IoTable.Address(tag.address).Substring(2) + "." + i;
                                                string id = IndexJson.Hash(string.Join("\n", new[] { tag.id,fc.id,db.id,member.Path,address }));
                                                IoRow row;
                                                if (!mapped.TryGetValue(id,out row)) {
                                                    row = new IoRow { id=id, name=path, address=address, area=tag.area, data_type="Bool", comment=member.Comment,
                                                        table=tag.table, group=tag.group, unit=tag.unit, origin=member.Path.Contains("].%X") ? "Byte 数组位映射" : null, mapping_group=fc.group, carrier=tag.name, mapping_fc=fc.name };
                                                    mapped.Add(id,row);
                                                }
                                                string dbBit = "DB" + db.number + ".DBX" + bit/8 + "." + i;
                                                row.references.Add(new IoReference { block=fc.name, group=fc.group, source="FC 字节传送与标准 DB 声明", state="snapshot",
                                                    location="网络 " + network, access=input ? "输入映射" : "输出映射", evidence=input ? address+" → "+dbBit+" → "+path : path+" → "+dbBit+" → "+address });
                                                resolved++;
                                            }
                                            if (resolved < 8) why = (8-resolved) + " 位没有可验证的 Bool 声明";
                                        }
                                    }
                                    if (why.Length > 0) result.warnings.Add(fc.group + "/" + fc.name + " 网络 " + network + " · " + tag.address + " · " + why);
                                }
                            }
                        }
                    }
                } catch (Exception ex) { result.warnings.Add(fc.group + "/" + fc.name + " 点位解析不完整：" + ex.Message); }
            }
            result.rows.AddRange(mapped.Values);
        }
    }
}
