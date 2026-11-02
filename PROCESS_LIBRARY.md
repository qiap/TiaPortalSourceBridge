# 工艺模板与知识库（3.2 候选版）

已从用户指定的 `HD2303_OP270_Independent_test/HD2303_OP270_Independent_PLC_Source` 提炼 7 个参数化模板和 9 类知识。源目录全部 266 个文件已读取，170 个 XML 和 2 个 JSON 格式解析成功。提炼关注 OP270 的设备接口、调用顺序及复用限制，不代表逐行逻辑审计。

**来源时间是 2026-10-14 06:23 UTC（北京时间 14:23）**，依据源 `manifest.json`。manifest 中工程路径仍指向 `HD2303_OP270_Independent`，与提供的 `_test` 目录不同；保留原始记录，不推断它与当前博途工程一致。每项资料包含源相对路径、字节数、SHA256 和快照时间；完整哈希清单在 `op270.source-inventory`。

## 模板

| id | 生成内容 | 边界 |
|---|---|---|
| `op270.cylinder-type` | 气缸 UDT | 保留原字段顺序与类型 |
| `op270.cylinder-db` | 单气缸独立 DB | 指定气缸成员、UDT、超时初始值；不扩写旧 DB |
| `op270.cylinder-call` | FB21 气缸 SCL 调用片段 | 显式指定模式、互锁、传感器、阀、实例和报警索引 |
| `op270.graph-type` | GRAPH 输入输出 UDT | 不含流程逻辑 |
| `op270.graph-db` | 自动/复位 GRAPH 接口 DB | 不含工步或动作；阻止成员重名 |
| `op270.alarm-call` | FC11 报警汇总调用片段 | 保留原接口，并记录锁存及 Msg 排除行为 |
| `op270.leak-test-db` | 气密仪双向接口 DB | 不自动生成现场启动、结果处理或超时 |

类型和 DB 是外部源码草稿；`SCL_SNIPPET` 是宿主块 `BEGIN` 内的片段，不能单独作为完整块导入。气缸多重实例必须先在宿主 FB Static 中声明为 `FB21_CylinderV1.1`，或使用已存在的同类型实例 DB。本库不复制整套 FB21 标准块；目标中缺少依赖时应按来源读取并核对后单独处理。

## 知识

工程结构与调用顺序、气缸接口与例外、报警汇总语义、模式与复位边界、自动 GRAPH 快照、复位 GRAPH 快照、气密仪接口、气缸 HMI 关联、完整来源清单。

提炼时保留以下重要差异，避免把历史工程直接变成通用标准：

- 源气缸 DB 定义 Z01～Z32，但 `OP270_Cylinder` 只有 Z05～Z11 七个 FB21 调用；预留字段不代表设备已接入。
- Z05 有反相推导反馈及开路输出，不能直接套用双传感器/双输出模板。
- W/H 表示工作位/原位，不能统一等同伸出/缩回；原点网络使用 Z09.Wpos_SE。
- FB21 超时依靠 1 秒脉冲计数，TimeOut_Set 是 DInt；不能把数值当毫秒或 TIME。
- FC11 报警汇总有锁存行为；Sensor 参与 Hardware 汇总，Msg 未计入 Total。记录现状，不擅自修正既有规则。
- GRAPH 资料保存步骤名称/编号，不把 XML 存储顺序解释为执行顺序，也不据此生成完整动作逻辑。
- 非优化 DB/HMI 的地址与字段布局须实时核对；不按 Z 编号推算 I/O、HMI 偏移或报警位。

## 如何调用

新增三个 MCP 工具均只操作本地资料，不需要连接博途：

1. `tia_list_process_library`：`{"kind":"all","query":"气缸","limit":10}`。只读小目录；关键词匹配标题、摘要、标签和 id，以空格分隔，任一关键词匹配后按命中数排序。支持 offset/limit 分页。
2. `tia_read_process_library`：`{"id":"op270.cylinder-db"}`。按需读取单条详情，包括 parameters、dependencies、review_notes、sources。
3. `tia_render_process_template`：

```json
{
  "id": "op270.cylinder-db",
  "parameters_json": "{\"block_name\":\"DB_ExampleCylinder\",\"cylinder_id\":\"Z14\",\"timeout_set\":15}"
}
```

以上示例数值仅展示渲染方式，不是现场超时推荐值。缺少必填参数、未知参数、越界整数、非法符号及重复 GRAPH 成员会返回错误。symbol 参数只允许变量引用；复杂互锁表达式先在目标程序中计算为中间 Bool，不接受任意 SCL 语句。

结果包含 `content`、依赖和来源，并明确返回 `imported=false/compiled=false/saved=false/downloaded=false`。渲染成功不等于博途编译通过；先实时读取目标并审查，再使用原有工程接口备份、导入、编译。新增模板接口本身不会执行这些操作。

七组具体参数与生成源码见 `examples/process-library/`，全部是示例名称，未写入 PLC/HMI 工程。

## 运行与版本

候选程序位于 `artifacts/process-library-3.2/TiaPortalSourceBridge.exe`，初始化版本为 3.2.0，共 69 个 MCP 工具。`mcp-client-process-library.json` 给出了此候选程序的独立 stdio 配置；按客户端的 MCP 配置格式使用并重连后才会显示新增工具。

独立启动参数：`mcp --config <本工具目录>/mcp-config.json`。只查询知识库和渲染模板不需要博途信任。若切换到这个新路径执行工程操作，仍需按既有 Openness 信任流程处理。此次没有替换运行中的宿主、修改信任注册表或修改博途工程。

现成程序与当前源码并非完全同步：原报警接口的 timeout_ms 描述与源码已有差异；候选构建包含构建前已有的源码状态。完整候选工程功能尚未做博途实测，不应直接视为正式发布。

构建候选版：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Build-ProcessLibraryCandidate.ps1
```

常规 `build.bat` 和 `.csproj` 已有整个 `templates/` 目录的打包规则，因此正式构建也会自动携带本库，无需新打包依赖。候选构建脚本不替换正式程序。

## 体积与性能

本版新增资料为 121,553 字节（约 119 KiB），原始导出为 9,288,872 字节（约 8.9 MiB）。库采用外部 JSON；不复制整个项目，不引入数据库、向量检索服务、Python 运行时或后台索引任务。

候选 exe 相比现存 exe 从 529,408 增至 541,696 字节，增加 12 KiB；这是当前候选构建与现存程序的整体差值，含源码中先前已有但未构建的改动，不能全部归因于本功能。后续增加模板/知识数据通常不增加 exe 大小。

启动服务和列 MCP 工具不会读取知识正文。查询只读取目录，读取/渲染才打开对应条目；无永久正文缓存。每个条目限制 2 MiB，列表支持分页。资料增多后会增加磁盘占用，当前方式也会随目录规模增加检索时间，但不会每次把整个库发给 AI。

## 验证与重新提炼

`tests/Test-ProcessLibrary.py` 使用 Python 标准库，通过实际 MCP 子进程检查查询、分页、全部条目读取、七模板渲染、错误参数、Unicode、依赖提示、GRAPH 成员冲突、外部配置路径、无工程 worker、原有 LAD 生成和旧工具结构兼容，并核对全部源文件哈希和重复提炼一致性。

```powershell
python tests/Test-ProcessLibrary.py --source "<导出目录>"
python scripts/Build-Op270ProcessLibrary.py "<导出目录>"
```

结果见 `tests/process-library-results.json`。目前是离线验证，生成结果未在 TIA Portal 中导入或编译；不宣称工艺和现场运行验证通过。

提炼脚本是作者工具，使用者运行桥接程序不需要安装 Python。该脚本针对这一份 OP270 导出约定，新增工程需单独审查规则；通用运行层不包含 OP270 设备硬编码。扩充库时新增 JSON 正文和目录条目即可；保持 schema_version、参数类型和来源记录一致。
