using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;

namespace TiaPortalSourceBridge.Mcp
{
    // Offline evidence only. A connection-to-PLC map must come from the caller's
    // verified engineering context; classic Openness cannot enumerate integrated connections.
    internal static class HmiPlcBindingValidation
    {
        internal sealed class Field
        {
            internal string Name, Type;
            internal int Bit, Size;
            internal bool Accessible;
        }
        internal sealed class Layout
        {
            internal string Name;
            internal int Number, Bits;
            internal bool Standard;
            internal readonly List<Field> Fields = new List<Field>();
        }
        internal static string DbName(XmlNode tag)
        {
            string symbol = HmiValidation.Value(tag, "LinkList/ControllerTag/Name");
            return symbol;
        }
        internal static int? DbNumber(XmlNode tag)
        {
            var m = Regex.Match(HmiValidation.Value(tag, "AttributeList/LogicalAddress"), @"^%?DB(\d+)\.DB[XBWD]\d+(?:\.[0-7])?$", RegexOptions.IgnoreCase);
            int number;
            return m.Success && int.TryParse(m.Groups[1].Value, out number) ? (int?)number : null;
        }
        internal static Layout ReadLayout(string xml)
        {
            var doc = HmiValidation.Parse(xml);
            var db = doc.SelectSingleNode("/Document/SW.Blocks.GlobalDB");
            if (db == null) throw new InvalidOperationException("只支持可导出的全局 DB；实例 DB 尚不能核验。");
            var layout = new Layout { Name = HmiValidation.Value(db, "AttributeList/Name"), Number = int.Parse(HmiValidation.Value(db, "AttributeList/Number")),
                Standard = HmiValidation.Value(db, "AttributeList/MemoryLayout") == "Standard" };
            var sections = db.SelectNodes("AttributeList/Interface/*[local-name()='Sections']/*[local-name()='Section' and @Name='Static']");
            if (sections.Count != 1) throw new InvalidOperationException("无法唯一解析 DB 静态接口。");
            int offset = 0;
            foreach (XmlNode member in sections[0].SelectNodes("*[local-name()='Member']")) Add(member, layout.Name, ref offset, true, layout.Fields, 0);
            layout.Bits = Align(offset, 16);
            return layout;
        }
        private static int Align(int bit, int boundary) { return checked((bit + boundary - 1) / boundary * boundary); }
        private static int Size(string type)
        {
            switch (type.ToLowerInvariant())
            {
                case "bool": return 1;
                case "byte": case "char": case "sint": case "usint": return 8;
                case "word": case "int": case "uint": case "date": case "s5time": return 16;
                case "dword": case "dint": case "udint": case "real": case "time": case "tod": case "time_of_day": return 32;
                case "lword": case "lint": case "ulint": case "lreal": return 64;
                default: return 0;
            }
        }
        private static void Add(XmlNode member, string prefix, ref int bit, bool accessible, List<Field> fields, int depth)
        {
            if (depth > 32 || fields.Count > 100000) throw new InvalidOperationException("DB 接口超过核验范围。");
            string name = prefix + "." + member.Attributes["Name"].Value, type = member.Attributes["Datatype"].Value;
            accessible &= !member.SelectNodes("*[local-name()='AttributeList']/*[local-name()='BooleanAttribute' and (@Name='ExternalAccessible' or @Name='ExternalVisible')]")
                .Cast<XmlNode>().Any(n => n.InnerText.Equals("false", StringComparison.OrdinalIgnoreCase));
            if (type.Equals("Struct", StringComparison.OrdinalIgnoreCase))
            {
                bit = Align(bit, 16);
                foreach (XmlNode child in member.SelectNodes("*[local-name()='Member']")) Add(child, name, ref bit, accessible, fields, depth + 1);
                bit = Align(bit, 16); return;
            }
            var array = Regex.Match(type, @"^Array\[(-?\d+)\.\.(-?\d+)\]\s+of\s+(\w+)$", RegexOptions.IgnoreCase);
            if (array.Success)
            {
                int lo = int.Parse(array.Groups[1].Value), hi = int.Parse(array.Groups[2].Value), size = Size(array.Groups[3].Value);
                long length = (long)hi - lo + 1;
                if (size == 0 || length <= 0 || length > 100000 || length + fields.Count > 100000) throw new InvalidOperationException("暂不支持此数组布局：" + type);
                bit = Align(bit, 16);
                for (long index = lo; index <= hi; index++)
                { fields.Add(new Field { Name = name + "[" + index + "]", Type = array.Groups[3].Value, Bit = bit, Size = size, Accessible = accessible }); bit = checked(bit + size); }
                bit = Align(bit, 16); return;
            }
            int width = Size(type);
            if (width == 0) throw new InvalidOperationException("暂不支持此成员布局，不能推测偏移：" + name + " / " + type);
            bit = Align(bit, width == 1 ? 1 : width == 8 ? 8 : 16);
            fields.Add(new Field { Name = name, Type = type, Bit = bit, Size = width, Accessible = accessible }); bit = checked(bit + width);
        }
        internal static string Check(XmlNode tag, Layout layout)
        {
            if (tag.SelectNodes("ObjectList/Hmi.Tag.TagStructureMember").Count > 0) return "新增或修改的结构体 PLC 绑定仍需完整 UDT 核验；本解析器仅核验基础类型。";
            string type = HmiValidation.Value(tag, "LinkList/DataType/Name"), mode = HmiValidation.Value(tag, "AttributeList/AddressAccessMode");
            if (mode == "Symbolic")
            {
                string symbol = HmiValidation.Value(tag, "LinkList/ControllerTag/Name");
                // Do not remove arbitrary quotes: quoted identifiers containing dots need a proper parser.
                var matches = layout.Fields.Where(f => f.Name.Equals(symbol, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length != 1) return "PLC 符号无法唯一核验：" + symbol;
                return matches[0].Accessible && matches[0].Type.Equals(type, StringComparison.OrdinalIgnoreCase) ? null : "PLC 符号类型不符或 HMI 不可访问：" + symbol;
            }
            if (mode != "Absolute" || !layout.Standard) return "绝对地址仅允许已核验标准访问 DB；优化 DB 不按推算偏移放行。";
            var match = Regex.Match(HmiValidation.Value(tag, "AttributeList/LogicalAddress"), @"^%?DB(\d+)\.DB([XBWD])(\d+)(?:\.([0-7]))?$", RegexOptions.IgnoreCase);
            if (!match.Success || int.Parse(match.Groups[1].Value) != layout.Number) return "DB 地址格式或 DB 号不匹配。";
            string unit = match.Groups[2].Value.ToUpperInvariant(); int offset = int.Parse(match.Groups[3].Value), width = Size(type);
            int expected = unit == "X" ? 1 : unit == "B" ? 8 : unit == "W" ? 16 : 32;
            if (width != expected || (unit == "X") != match.Groups[4].Success) return "地址宽度与变量类型不符。";
            long start = (long)offset * 8 + (match.Groups[4].Success ? int.Parse(match.Groups[4].Value) : 0);
            if (start + width > layout.Bits || offset > 2097151) return "地址超出 DB 已核验范围。";
            var overlaps = layout.Fields.Where(f => f.Bit < start + width && f.Bit + f.Size > start).ToArray();
            if (overlaps.Length == 0 || overlaps.Any(f => !f.Accessible)) return "地址未指向可访问的 PLC 成员。";
            if (overlaps.Length == 1 && overlaps[0].Bit == start && overlaps[0].Size == width && overlaps[0].Type.Equals(type, StringComparison.OrdinalIgnoreCase)) return null;
            // Alarm words are intentionally raw unsigned views over packed Bool flags.
            if ((type == "Byte" || type == "Word" || type == "DWord") && overlaps.All(f => f.Type.Equals("Bool", StringComparison.OrdinalIgnoreCase)) && overlaps.Min(f => f.Bit) == start) return null;
            return "地址未对齐到同类型 PLC 成员，且不是 Bool 报警位组的无符号视图。";
        }
    }
}
