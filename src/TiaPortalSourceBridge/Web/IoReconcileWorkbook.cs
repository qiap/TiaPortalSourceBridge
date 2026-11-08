using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaPortalSourceBridge.Web
{
    internal static class IoReconcileWorkbook
    {
        internal static byte[] Create(IoReconcileReport report)
        {
            var o = report.options;
            var info = new List<string[]> { new[] { "项目", "说明" }, new[] { "工程", report.project_path }, new[] { "PLC", report.plc_name }, new[] { "文件", report.file_name },
                new[] { "工作表", o.sheet }, new[] { "表头行", o.header_row.ToString() }, new[] { "范围", o.scope == "mapped" ? "映射后的 I/O 点位" : "原始变量表" },
                new[] { "软件单元", o.unit }, new[] { "变量表范围", string.IsNullOrEmpty(o.table) ? "全部变量表" : o.table.Replace("\n", " / ") },
                new[] { "快照来源", report.source_mode == "local_cache" ? "本地缓存源码" : "从博途读取" }, new[] { "读取开始 UTC", report.snapshot_started }, new[] { "读取完成 UTC", report.snapshot_completed },
                new[] { "源码范围", report.source_note }, new[] { "生成 UTC", report.generated_at }, new[] { "Excel 行 / PLC 项", report.excel_rows + " / " + report.plc_rows },
                new[] { "列对应", "地址=" + IoSheetImport.Column(o.address) + "；名称=" + IoSheetImport.Column(o.name) + "；类型=" + (o.type < 0 ? "未比较" : IoSheetImport.Column(o.type)) + "；注释=" + (o.comment < 0 ? "未比较" : IoSheetImport.Column(o.comment)) },
                new[] { "导出范围", "本次全部对账结果及 PLC 候选证据，不受页面筛选、搜索或分页影响。未写入工程。" } };
            info.AddRange(report.counts.Select(c => new[] { IoReconcile.Statuses[c.Key], c.Value.ToString() }));
            info.AddRange(report.warnings.Select(w => new[] { "覆盖说明", w }));
            return IoWorkbook.CreateSheets(new[] { "对账概览", "差异与匹配", "PLC证据" }, new[] { info, Rows(report), Evidence(report) });
        }
        private static IEnumerable<string[]> Rows(IoReconcileReport report)
        {
            yield return new[] { "结果编号", "结果", "Excel行", "Excel地址", "Excel名称", "Excel类型", "Excel注释", "差异与说明", "PLC候选数量" };
            int n = 0; foreach (var row in report.rows) yield return new[] { (++n).ToString(), IoReconcile.Statuses[row.status], row.excel_row == 0 ? "" : row.excel_row.ToString(), row.address, row.name, row.type, row.comment, string.Join("\n", row.issues), row.plc.Count.ToString() };
        }
        private static IEnumerable<string[]> Evidence(IoReconcileReport report)
        {
            yield return new[] { "结果编号", "结果", "Excel行", "Excel地址", "Excel名称", "Excel类型", "Excel注释", "PLC地址", "PLC名称", "PLC类型", "PLC注释", "软件单元", "变量表", "组路径", "映射块", "引用证据条数" };
            int n = 0; foreach (var row in report.rows) { n++; foreach (var p in row.plc) yield return new[] { n.ToString(), IoReconcile.Statuses[row.status], row.excel_row == 0 ? "" : row.excel_row.ToString(), row.address, row.name, row.type, row.comment, p.address, p.name, p.type, p.comment, p.unit, p.table, p.group, p.mapping, p.references.ToString() }; }
        }
    }
}
