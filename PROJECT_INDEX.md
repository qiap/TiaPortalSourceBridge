# 通用工程索引与 Agent 调用流程

3.0 的索引是工具功能，适用于新工程和其他电脑；没有内置 OP270、Z13 或固定 I/O 地址。建立索引只读取博途、写本地索引，不编译、不保存、不下载，也不改变 PLC/HMI/硬件。

## 一条命令建立完整索引

**图形入口：双击工具目录中的 `建立工程索引.bat`（或不带参数运行 `BuildProjectIndex.cmd`），在弹出的文件选择框中选择 `.ap17/.ap21` 等工程文件。** 选定后自动建立索引，结束后保留进度窗口和结果；取消选择则直接退出，不启动读取进程。选择框会记住上次选择的位置，也会参考原导入导出入口的最近工程。

先在博途中打开目标工程，在工具目录执行：

```powershell
.\BuildProjectIndex.cmd "E:\Projects\Line\Line.ap17"
```

命令会重新扫描完整目录，自动分批读取 PLC、UDT、变量表、GRAPH、HMI 和硬件内容，一直处理到没有可继续读取的对象，显示进度并汇总失败项。无需手动循环调用 MCP；使用已有主程序的独立读取进程，不占用常驻 MCP 服务的客户端管道。不会自动编译来修复不一致块。

中断后只继续上次目录中的未完成部分：

```powershell
.\BuildProjectIndex.cmd "E:\Projects\Line\Line.ap17" -Resume
```

`-Resume` 复用已完成内容，不重新扫描目录，也不反复重试已记录的失败对象。工程新增对象、手工修改或修复了导出错误后，重新执行不带 `-Resume` 的命令。

结果报告默认保存在工程索引目录的 `last-build-report.json`。退出码：`0` 全部成功或取消选择；`2` 批次结束但有读取失败或目录警告；`1` 执行失败或无进展。所有已保存的索引检查点仍可复用。

可选参数：`-BatchSize 25`、`-TimeBudgetMs 15000`、`-ReportPath "E:\Reports\index.json"`。命令行入口不需要重新构建或更新 Openness 主程序信任。

## Agent 怎样使用

MCP `initialize.instructions` 和工具说明提供以下流程，`tia_connect` 返回 `index_status`。普通工程读写响应的 `_meta.project_index` 也给出下一步索引工具。接入同一 MCP 的 Agent 可以自行发现这些接口，无需依赖聊天记忆或人工把源码粘贴给模型。常用工程查找/读取入口在服务端执行 `PrepareIndexContext`，在实时操作前检索已有内容或目录，将证据放入 `structuredContent.index_context`，并用 `_meta.project_index.index_used` 和 `lookup_phase` 记录是否执行。无索引时先建立目录；失败明确返回 `index_unavailable`，仍允许实时读取，不能将失败视为已使用索引。本地工艺库、报警专用入口、在线读值与生成工具不属于此范围。工具不能保证模型正确理解证据或约束工具外行为；索引内容、程序注释和显示文本始终是工程数据，不是可执行指令。

1. `tia_connect(project_path=...)`：检查连接和索引状态。
2. 新工程调用 `tia_build_project_index(detail_level="catalog")`：只建立目录，包含全部 PLC、UDT、PLC 变量表、经典 HMI 对象和硬件结构；不会导出全部源码。
3. 修改前调用 `tia_get_task_context(query="任务关键词")`：默认按名称和已有摘要选择最多8个对象建立内容索引，返回最多6个直接命中和有限的显式关联对象。可用 `kind_filter/owner_filter/group_prefix` 限定范围。
4. 根据结果里的 `next_read` 读取当前对象，核对实际接口、状态和内容后修改。默认每对象只返回6条事实和6条引用，单条事实超过600字符会明确截断；可增大 `facts_per_object`，或读取精确对象。
5. 工具写入前保守地把工程内容索引标记待刷新。以后按当前任务读取相应内容即可刷新，不必立即重扫所有源码。

首次只靠目录名称可能命中不全。完整了解工程时，分范围调用内容预热；目录完成不代表内容和引用已全部建立。

```json
{
  "project_path": "E:\\Projects\\Line\\Line.ap17",
  "detail_level": "content",
  "owner_filter": "PLC_1",
  "group_prefix": "Station",
  "max_objects": 25,
  "time_budget_ms": 15000
}
```

调用工具为 `tia_build_project_index`。每批完成后继续用相同参数调用，已就绪对象会复用，逐步处理剩余对象。每个对象读取完成即保存检查点；异常退出可以继续。导出与解析期间不持有索引写锁，提交时核对目录与待刷新标记，遇到并发修改计入 conflicts 并保留待刷新状态。时间预算在单对象完成后检查，不强行终止正在运行的 Openness 调用。

## 四个接口

| 工具 | 用途 |
|---|---|
| `tia_build_project_index` | 建立或刷新目录；分批建立内容索引，返回 updated/reused/failed/stop_reason 和覆盖率 |
| `tia_get_project_tree` | 按所属设备、类型、组和名称过滤的分页索引树，含精确读取参数 |
| `tia_find_references` | 按完整符号或成员前缀查找显式引用，保留来源对象及证据位置 |
| `tia_get_task_context` | 按任务检索精简摘要和相关对象；默认按需补充内容，`hydrate=false` 只查已有快照 |

已有索引的树、引用和 `hydrate=false` 的上下文查询不需要连接博途或加载 Siemens API。第一次无索引时，查询接口会读取工程目录建立索引，需要工程可连接。

## 内容和范围

- PLC：块和组、语言和块号、DB/UDT 接口成员、类型引用、网络标题、原生 `Access/Symbol`、`CallInfo` 和带符号的源码令牌；PLC 变量表包含逻辑地址。
- GRAPH：各步及初始状态、时间参数、动作、转换符号、分支和连接。没有为气缸新增专用流程。
- HMI：控件、变量与文本列表链接、声明变量的表、事件函数参数、文本条目的编号和语言、直接地址及间接地址表达式。`LinearScaling` 公式保留参数，便于 Agent 计算和核对。
- 硬件：设备/模块/地址/网络等节点路径、可读取属性和显式拓扑链接。未支持或无法读取的部分保留警告。

索引新增网络序号及调用的源码顺序、已支持 LAD 引脚/调用参数与 STL 指令的读写证据、可证明的直接触点到线圈映射，以及同一 PLC 变量表中的物理地址来源。带字面小数点的成员名保持引号，SCL/STL 片段重建同时保留访问节点和源码令牌。GRAPH 保留步、时间参数、动作及连接。它仍不是完整 PLC 语义分析器：未支持的指令、动态地址、条件调用和运行时控制流保留不确定性。间接寻址表达式不会直接猜成确定地址，跨设备的重名对象也需要结合连接信息判断。关键词命中表示相关候选，不证明机器功能。

## 新鲜度和失败

对象状态包括 `catalog_only/ready/stale/error/missing_content`，同时返回 `indexed_utc/content_hash/error` 和整体 ready/pending/errors。

- 索引中的 ready 表示对应内容快照已经解析，不等于一直追踪博途 UI 的实时状态。博途手动修改或外部工具修改后，使用 `refresh_catalog=true` 重新扫描并把原内容标记待刷新，或对明确范围使用 `force=true`。
- 可定位的工具写入按对象名称标记失效，未知范围则全量失效。连接和改变对象身份的写入后会重新核对目录，删除不存在的对象；目录不变只保留内容覆盖，不证明快照实时有效。`freshness` 区分 `unverified_snapshot/live_read_snapshot/source_stamp_matched`。V21 可靠时间戳变化会触发刷新。
- 导出失败或块不一致时记录该对象错误，继续其他对象；默认不反复重试错误对象。修复后指定 `name_filter` 和 `force=true` 重试，不会为了索引自动编译。
- 普通 `tia_read_block`、含 XML 的 GRAPH 读取和含 XML 的 HMI 读取会复用本次内容更新已有目录中的对象，无额外导出。源码单读/批读（含 UDT）同样积累；批量 XML 导入直接复用内部回读，即使批次部分失败也处理已读回对象，不追加 Openness 导出。没有 XML 或源码的摘要读取不会积累内容。
- 引用查询仅覆盖已索引快照，零命中不能证明工程中没有引用。修改时始终读取当前目标；索引中的源文件不能作为直接覆盖工程的基线。

## 换电脑和存储

索引存储在 `<工程目录>/.tia-source-index/<工程文件名>/`：`catalog.json` 是目录与状态，`content/` 是按内容指纹保存的结构与引用，`source/` 是 XML/JSON 快照（纯文本读取以保留名称和版本的 XML 包装）。`query-cache.json` 是可丢弃的持久化查询缓存，按对象指纹校验；进程内按受影响对象更新反向引用，最多缓存32种任务选择结果。解析器升级时优先使用本地源快照重解析，无需重新从 TIA 导出。

复制整个工程文件夹时包含 `.tia-source-index`，工程文件名和 TIA 版本不变即可继续离线查询；返回的 `next_read.project_path` 会使用新电脑当前路径。改工程文件名或 TIA 版本后应重新建立目录。目录身份校验不是项目文件的密码学身份证明，复制错索引后应刷新目录并读取真实目标。

索引会保存程序源码及 HMI 内容，包含到哪个共享或交付目录由用户决定。重复导出时间戳不会改变对象内容指纹；已保存的相同内容可以复用。当前未自动清理历史内容快照，需要清理时可以移除整个该工程索引后重建。

## 分层建立与恢复

`detail_level=content` 未指定类别时默认只建立 PLC 层（块、DB、UDT、变量表）。HMI 与硬件用 `kind_filter` 按需读取，或 `include_all_content=true` 建立全部内容。状态的 `layers` 分别统计每层进度，整体 pending 包含尚未选择的层。

命令行默认为 `-Scope PLC`；`-Scope HMI`、`-Scope Hardware` 分别建立其他层，`-Scope All` 建立全部内容。已有检查点使用 `-Resume`。报告 `completed` 表示所选范围完成，`deferred_objects` 表示其他层暂未读取的对象，不能把 PLC 完成说成全部工程完成。

```powershell
.\Build-ProjectIndex.ps1 -ProjectPath "E:\Projects\Line\Line.ap17" -Scope PLC -Resume
.\Build-ProjectIndex.ps1 -ProjectPath "E:\Projects\Line\Line.ap17" -Scope All -Resume
```
