using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using TiaPortalSourceBridge.Indexing;

namespace TiaPortalSourceBridge.Web
{
    internal static class IoDeclarations
    {
        private const int Limit = 250000;
        private static readonly Regex ArrayType = new Regex(@"^Array\s*\[([^\]]+)\]\s+of\s+(.+)$", RegexOptions.IgnoreCase);
        private static readonly Regex Range = new Regex(@"^\s*(-?\d+)\s*\.\.\s*(-?\d+)\s*$");
        private const string Members = "*[local-name()='Member'] | *[local-name()='Sections']/*[local-name()='Section']/*[local-name()='Member']";

        internal static string Comment(XmlNode member)
        {
            if (member == null) return "";
            return string.Join(" / ", member.SelectNodes("*[local-name()='Comment']/*").Cast<XmlNode>().Select(n => n.InnerText).Where(t => t.Length > 0));
        }

        private static IEnumerable<string> Indices(string dimensions)
        {
            string[] bounds = dimensions.Split(',');
            int[] lower = new int[bounds.Length], upper = new int[bounds.Length];
            long count = 1;
            for (int i = 0; i < bounds.Length; i++) {
                var m = Range.Match(bounds[i]);
                if (!m.Success || !int.TryParse(m.Groups[1].Value,out lower[i]) || !int.TryParse(m.Groups[2].Value,out upper[i]) || upper[i] < lower[i])
                    throw new FormatException("数组边界无法确定：" + dimensions);
                count *= (long)upper[i] - lower[i] + 1;
                if (count > Limit) throw new InvalidOperationException("数组超过单次展开上限：" + dimensions);
            }
            int[] pos = (int[])lower.Clone();
            while (true) {
                yield return string.Join(",",pos.Select(n => n.ToString(CultureInfo.InvariantCulture)));
                int i = bounds.Length - 1;
                while (i >= 0 && pos[i] == upper[i]) { pos[i] = lower[i]; i--; }
                if (i < 0) yield break;
                pos[i]++;
            }
        }
        internal static void Add(IoSnapshot result, IndexEntry[] entries, Func<IndexEntry,string> source)
        {
            int start = result.rows.Count;
            foreach (var block in entries) {
                try {
                    var doc = IoMapping.Load(source(block));
                    var root = doc.SelectSingleNode("/Document/*[starts-with(name(),'SW.Blocks.')]");
                    if (root == null || root.SelectSingleNode("AttributeList/Name") == null ||
                        root.SelectSingleNode("AttributeList/Name").InnerText != block.name) continue;
                    bool db = root.LocalName.EndsWith("DB",StringComparison.Ordinal);
                    foreach (XmlElement section in root.SelectNodes("AttributeList/Interface/*[local-name()='Sections']/*[local-name()='Section']")) {
                        string scope = section.GetAttribute("Name");
                        foreach (XmlElement member in section.SelectNodes(Members))
                            Walk(result,block,member,ContentIndexer.ComponentName(block.name),db,scope,start);
                    }
                } catch (Exception ex) { result.warnings.Add(block.group + "/" + block.name + " 声明展开不完整：" + ex.Message); }
                if (result.rows.Count - start >= Limit) break;
            }
        }
        private static void Walk(IoSnapshot result, IndexEntry block, XmlElement member, string parent, bool db, string scope, int start)
        {
            string path = parent + "." + ContentIndexer.ComponentName(member.GetAttribute("Name"));
            string type = member.GetAttribute("Datatype");
            var array = ArrayType.Match(type);
            string element = array.Success ? array.Groups[2].Value.Trim() : type;
            var children = member.SelectNodes(Members).OfType<XmlElement>().ToArray();
            bool boolean = string.Equals(element,"Bool",StringComparison.OrdinalIgnoreCase);
            bool bytes = array.Success && string.Equals(element,"Byte",StringComparison.OrdinalIgnoreCase);
            if (!boolean && !bytes && children.Length == 0) return;
            string commonComment = Comment(member);
            var comments = new Dictionary<string,string>();
            if (array.Success) foreach (XmlElement item in member.SelectNodes("*[local-name()='Subelement']")) {
                string text = Comment(item); if (text.Length > 0) comments[item.GetAttribute("Path").Replace(" ","")] = text;
            }
            foreach (string index in array.Success ? Indices(array.Groups[1].Value) : new[] { "" }) {
                string symbol = index.Length == 0 ? path : path + "[" + index + "]";
                string comment;
                if (!comments.TryGetValue(index,out comment)) comment = commonComment;
                if (boolean || bytes) {
                    for (int bit = 0; bit < (bytes ? 8 : 1); bit++) {
                        if (result.rows.Count - start >= Limit) throw new InvalidOperationException("达到点位上限 " + Limit);
                        string name = bytes ? symbol + ".%X" + bit : symbol;
                        string kind = (db ? "DB" : "块 " + scope) + (bytes ? " · Byte 位" : array.Success ? " · Bool 数组元素" : " · Bool 声明");
                        result.rows.Add(new IoRow { id=IndexJson.Hash(block.id+"\n"+scope+"\n"+name), name=name, area=db ? "DB" : "LOCAL", address="", table="",
                            group=block.group, unit=block.unit_name, data_type="Bool", comment=comment, origin=kind });
                    }
                } else foreach (var child in children) Walk(result,block,child,symbol,db,scope,start);
            }
        }
    }
}
