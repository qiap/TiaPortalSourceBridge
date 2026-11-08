using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;

namespace TiaPortalSourceBridge.Web
{
    // Minimal OOXML workbook. Every cell is explicit text, including strings beginning
    // with =, +, -, or @; source comments must never become Excel formulas.
    internal static class IoWorkbook
    {
        private const string Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private static XmlWriter Writer(ZipArchive zip, string path)
        { return XmlWriter.Create(zip.CreateEntry(path).Open(), new XmlWriterSettings { Encoding = new UTF8Encoding(false), CloseOutput = true, NewLineHandling = NewLineHandling.Entitize }); }
        private static string Clean(string value)
        {
            value = value ?? "";
            if (value.Length > 32767) throw new InvalidOperationException("单元格超过 Excel 的 32767 字符限制，请缩小导出范围。");
            var b = new StringBuilder();
            for (int i = 0; i < value.Length; i++) {
                if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) { b.Append(value[i]).Append(value[++i]); }
                else if (XmlConvert.IsXmlChar(value[i])) b.Append(value[i]);
            }
            return b.ToString();
        }
        private static string Column(int i) { string s = ""; for (i++; i > 0; i = (i - 1) / 26) s = (char)('A' + (i - 1) % 26) + s; return s; }
        private static void Sheet(ZipArchive zip, int number, IEnumerable<string[]> rows)
        {
            using (var w = Writer(zip, "xl/worksheets/sheet" + number + ".xml")) {
                w.WriteStartElement("worksheet", Ns);
                w.WriteStartElement("sheetViews"); w.WriteStartElement("sheetView"); w.WriteAttributeString("workbookViewId", "0");
                w.WriteStartElement("pane"); w.WriteAttributeString("ySplit", "1"); w.WriteAttributeString("topLeftCell", "A2"); w.WriteAttributeString("activePane", "bottomLeft"); w.WriteAttributeString("state", "frozen"); w.WriteEndElement(); w.WriteEndElement(); w.WriteEndElement();
                w.WriteStartElement("cols"); w.WriteStartElement("col"); w.WriteAttributeString("min", "1"); w.WriteAttributeString("max", "16"); w.WriteAttributeString("width", "24"); w.WriteAttributeString("customWidth", "1"); w.WriteEndElement(); w.WriteEndElement();
                w.WriteStartElement("sheetData"); int count = 0, columns = 0;
                foreach (var row in rows) {
                    if (++count > 1048576) throw new InvalidOperationException("导出超过 Excel 单表行数上限，请缩小筛选范围。");
                    columns = Math.Max(columns, row.Length);
                    w.WriteStartElement("row"); w.WriteAttributeString("r", count.ToString());
                    for (int i = 0; i < row.Length; i++) {
                        w.WriteStartElement("c"); w.WriteAttributeString("r", Column(i) + count); w.WriteAttributeString("t", "inlineStr"); w.WriteAttributeString("s", count == 1 ? "1" : "0");
                        w.WriteStartElement("is"); w.WriteStartElement("t"); w.WriteAttributeString("xml", "space", null, "preserve"); w.WriteString(Clean(row[i])); w.WriteEndElement(); w.WriteEndElement(); w.WriteEndElement();
                    }
                    w.WriteEndElement();
                }
                w.WriteEndElement();
                if (count > 0) { w.WriteStartElement("autoFilter"); w.WriteAttributeString("ref", "A1:" + Column(columns - 1) + count); w.WriteEndElement(); }
                w.WriteEndElement();
            }
        }
        private static IEnumerable<string[]> IoRows(IEnumerable<IoRow> rows)
        {
            yield return new[] { "变量名", "地址", "区域", "数据类型", "注释", "变量表", "组路径", "软件单元", "常量值", "引用证据条数", "映射程序组", "映射块", "物理字节变量", "点位来源" };
            foreach (var r in rows) yield return new[] { r.name, r.address, r.area, r.data_type, r.comment, r.table, r.group, r.unit, r.value, r.references.Count.ToString(), r.mapping_group, r.mapping_fc, r.carrier, r.origin };
        }
        private static IEnumerable<string[]> ReferenceRows(IEnumerable<IoRow> rows)
        {
            yield return new[] { "变量名", "地址", "变量表", "软件单元", "引用块", "块路径", "读写/关系", "位置", "证据", "来源", "状态" };
            foreach (var r in rows) foreach (var reference in r.references) yield return new[] { r.name, r.address, r.table, r.unit, reference.block, reference.group, reference.access, reference.location, reference.evidence, reference.source, reference.state };
        }
        internal static byte[] Create(IoSnapshot snapshot, IoRow[] rows)
        {
            var info = new List<string[]> { new[] { "项目", "说明" }, new[] { "工程", snapshot.project_path }, new[] { "PLC", snapshot.plc_name },
                new[] { "读取开始 UTC", snapshot.read_started_utc }, new[] { "读取完成 UTC", snapshot.read_completed_utc }, new[] { "变量表", snapshot.tables_read + "/" + snapshot.tables_total },
                new[] { "导出范围", rows.Length + " / " + snapshot.rows.Count + " 个变量（与当前页面筛选一致，包含所有分页）" },
                new[] { "引用来源", snapshot.reference_source }, new[] { "引用覆盖", snapshot.reference_coverage },
                new[] { "说明", "本表来自 PLC 已声明变量表，不包含未分配变量的硬件通道；不包含在线实时值。读取是时间区间快照。" } };
            info.AddRange(snapshot.warnings.Select(s => new[] { "警告", s }));
            return CreateSheets(new[] { "IO表", "交叉引用", "读取说明" }, new[] { IoRows(rows), ReferenceRows(rows), info });
        }
        internal static byte[] CreateSheets(string[] names, IEnumerable<string[]>[] sheets)
        {
            if (names == null || sheets == null || names.Length == 0 || names.Length != sheets.Length) throw new ArgumentException("缺少导出工作表。");
            using (var memory = new MemoryStream()) {
                using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true)) {
                    using (var w = Writer(zip, "[Content_Types].xml")) {
                        w.WriteStartElement("Types", "http://schemas.openxmlformats.org/package/2006/content-types");
                        foreach (var item in new[] { new[] { "rels", "application/vnd.openxmlformats-package.relationships+xml" }, new[] { "xml", "application/xml" } }) { w.WriteStartElement("Default"); w.WriteAttributeString("Extension", item[0]); w.WriteAttributeString("ContentType", item[1]); w.WriteEndElement(); }
                        foreach (var item in new[] { new[] { "/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml" }, new[] { "/xl/styles.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml" } }.Concat(Enumerable.Range(1, names.Length).Select(n => new[] { "/xl/worksheets/sheet" + n + ".xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml" }))) { w.WriteStartElement("Override"); w.WriteAttributeString("PartName", item[0]); w.WriteAttributeString("ContentType", item[1]); w.WriteEndElement(); } w.WriteEndElement();
                    }
                    using (var w = Writer(zip, "_rels/.rels")) {
                        w.WriteStartElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships"); Relation(w, "rId1", "officeDocument", "xl/workbook.xml"); w.WriteEndElement();
                    }
                    using (var w = Writer(zip, "xl/workbook.xml")) {
                        w.WriteStartElement("workbook", Ns); w.WriteStartElement("sheets");
                        for (int n = 1; n <= names.Length; n++) { w.WriteStartElement("sheet"); w.WriteAttributeString("name", names[n - 1]); w.WriteAttributeString("sheetId", n.ToString()); w.WriteAttributeString("r", "id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships", "rId" + n); w.WriteEndElement(); }
                        w.WriteEndElement(); w.WriteEndElement();
                    }
                    using (var w = Writer(zip, "xl/_rels/workbook.xml.rels")) {
                        w.WriteStartElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships");
                        for (int n = 1; n <= names.Length; n++) Relation(w, "rId" + n, "worksheet", "worksheets/sheet" + n + ".xml");
                        Relation(w, "rId" + (names.Length + 1), "styles", "styles.xml"); w.WriteEndElement();
                    }
                    using (var w = Writer(zip, "xl/styles.xml")) w.WriteRaw("<styleSheet xmlns=\"" + Ns + "\"><fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts><fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills><borders count=\"1\"><border/></borders><cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs><cellXfs count=\"2\"><xf numFmtId=\"49\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyAlignment=\"1\"><alignment vertical=\"top\" wrapText=\"1\"/></xf><xf numFmtId=\"49\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/></cellXfs><cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>");
                    for (int i = 0; i < sheets.Length; i++) Sheet(zip, i + 1, sheets[i]);
                }
                return memory.ToArray();
            }
        }
        private static void Relation(XmlWriter w, string id, string type, string target)
        { w.WriteStartElement("Relationship"); w.WriteAttributeString("Id", id); w.WriteAttributeString("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/" + type); w.WriteAttributeString("Target", target); w.WriteEndElement(); }
    }
}
