# 多个现有 XML 块的批量修改

2026-10-29 已新增并部署 `tia_import_blocks_batch`，用于一次修改多个现有程序块。原有单块导入与文本源码批量导入仍然可用。

## 执行流程

1. 解析整个请求（1～50 个块），拒绝空名称、重复名称和不支持的块 XML。
2. 刷新一次目录，实时导出所有目标，检查存在性、原组、TIA 版本、块类型和块号。可提供原始 XML 的 UTF-8 SHA256 指纹防止覆盖已变化的目标。
3. 在独立批次目录保存所有原块备份和请求 XML。任一目标检查或备份失败，整个批次不开始写入。
4. 按提交顺序导入，复用目录缓存，不再在每个导入前重复导出备份。依赖块必须排在引用块之前；实例 DB 排在 FB 之后。工具不猜测依赖顺序。
5. 发生导入异常时停止后续导入。只要尝试过导入，最后调用一次整台 PLC 编译，包括部分导入失败的情况。
6. 回读成功导入和结果不确定的块，保存回读 XML，比较程序源、接口、名称、块号和编程语言，返回差异所在的接口/网络。

不会自动保存、下载、创建新块、移动组、修改块号或回滚。该操作不是事务；`outcome_unknown` 表示导入调用抛出异常，TIA 可能已经修改了目标。后续未执行项保持 `backed_up`，便于区分。

## 调用

```json
{
  "name": "tia_import_blocks_batch",
  "arguments": {
    "project_path": "工程绝对路径.ap17",
    "plc_name": "目标 PLC 名称",
    "items_json": "[{\"xml\":\"完整单块 SimaticML XML\",\"group_path\":\"原组路径\"}]",
    "confirm": true
  }
}
```

`items_json` 每项的 `expected_hash` 可选，指原始完整导出 XML（不包含文件 BOM）的 UTF-8 SHA256，不是 `tia_read_graph_model` 的 GRAPH 指纹。组留空沿用原组；显式 `/` 表示根组。

新工具固定备份、编译和回读，不接受跳过备份或跳过编译参数。已有长期运行的 MCP 进程需重启后才能加载更新后的程序。

## 结果与回读范围

- `ok=true` 需要全部导入、编译无错误，以及所有已检查内容匹配。
- `phase=needs_review` 不等于所有导入失败：查看 `items[].status`、`verification`、`differences` 和 `compile_report`。
- `verification=matched` 表示规范化后的源/接口内容一致，不表示 PLC 运行测试通过，也不覆盖所有编辑器元数据、注释或块属性。
- 连线端点与指令保留。TIA 一致重编号时同步映射定义和引用，不能通过简单删除连线 ID 来隐藏接线变化。
- 忽略已验证的 SCL 空白变化、默认 SetPoint=false 属性、空动作编辑行、GRAPH 非执行顺序容器的排列差异。未知差异保留。
- GRAPH 编译可能重新生成静态接口计数、偏移和成员排列。这些仍会报告 `Interface[0]` 差异，需要检查；不会据此宣称程序全部匹配。

每批独立目录位于 `block-backups/batch-时间-唯一标识/`，返回 `journal` 路径。目录中有原块及其恢复元数据、按输入序号保存的 `NN.requested.xml`、`NN.after.xml` 和 `result.json`。日志在备份和导入阶段持续落盘，包含编译报告与逐项状态。失败时保留已有材料，不自动覆盖或删除。

## 验证

已通过 **78 项自动检查**，覆盖批次检查、备份门槛、失败后停止、部分写入状态、最多一次编译、编译错误、回读异常、连线变化、编号调整及 XML 换行。使用此前 OP275 的真实候选/回读样本检查四个支持块和 GRAPH 源；GRAPH 生成接口的差异按预期保留。

MCP 启动检查通过，目录中可发现 **70 个工具**，包含新接口。已更新 `src/TiaPortalSourceBridge/bin/Release/net48/` 和 `online-bin/` 中的主程序，部署前保留旧可执行文件副本。

本轮未在实际 TIA 工程执行新批次写入；批次顺序和故障处理使用替身回调验证，真实样本用于离线回读比较。因此没有实际批量导入耗时或速度提升百分比的测量结论。当前 OP275 工程未被本轮修改。

复核命令：

```powershell
.\tests\Test-Performance.ps1 -GraphFixture '.\artifacts\op275-retest-20261029\apply\OP275_自动流程.after.xml'
.\tests\SmokeMcp.ps1
```
