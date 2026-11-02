# TIA Portal 统一工具 V4.1（V17 / V21）

关闭最后一个看板标签页后，约 4～6 秒自动退出看板及其启动的后台服务，释放日志文件；刷新页面和关闭部分标签页不会退出。正在执行的导入、编译会先完成，再退出。浏览器崩溃、未发送关页通知时，约 94 秒自动回收。可用 `退出全部看板后台.bat` 主动退出看板。TIA Portal 工程窗口继续保留。

已将独立 V21 工具合并到本目录。统一使用这里的看板、`OpenTool.bat`、`StartMcp.bat` 和 MCP 主程序；`.ap21/.als21` 自动交给 `engines/v21` 的独立进程，其余版本保留原引擎。不要单独启动内部引擎。

- 全量构建：`build.bat` 或 `Build-All.ps1`。
- 版本发现：`tia_list_open_projects(portal_version="V17"/"V21")`。
- 工程调用：明确传 `project_path`；多项目不会默认选择写入目标。
- 本地生成或仿真：可传 `portal_version`；无显式版本时按唯一连接工程或配置判断。V21 专属工具标有 `[V21]`。
- V21 保留 SimaticSD、Software Units、官方交叉引用、Unified、AML 等；原版保留看板及其在线/仿真扩展。V21 仿真提供 status/start/stop/download，原版专属 control/read/write_test 等尚不迁移到 V21。
- 原独立包与历史备份移入 `_archive/V21-standalone-20261102`，后续只修改 `engines/v21`。
- 已开启的旧看板/服务仍是旧进程，关闭后从本目录重新打开即可使用合并版。TIA 工程不必关闭或保存。

实测与限制见 [合并验证记录](MERGE_VERIFICATION.md)。以下保留原版功能说明。

---

# TIA Portal Source Bridge (v3.1 工程索引与经典 HMI 报警版)

新增 **v3.2 工艺资料候选版**：从指定 OP270 离线源码提炼 **7 个参数化模板、9 类知识**，增加本地目录检索、按需读取与模板渲染三个 MCP 工具，候选版共69项。外部资料约119 KiB，不复制完整工程、不引入数据库；已编译候选程序与使用方法见 **[PROCESS_LIBRARY.md](PROCESS_LIBRARY.md)**。当前运行宿主未替换，生成源码尚未在博途中导入或编译验证。

开发中的 **v3.1.0**：新增经典 WinCC HMI 离散量报警批量读取与写入（`tia_read_hmi_alarms`、`tia_write_hmi_alarms`），包含 UIA 界面桥接导出/导入与原生 `.xlsx` 包补丁，共 **66 个工具**。独立工程副本已完成实际导入和回读，但界面控件定位仍有偶发超时，尚未作为稳定版本替换已安装程序。用法见 [HMI_ALARMS.md](HMI_ALARMS.md)，当前验证状态见 [VALIDATION_3.1.md](VALIDATION_3.1.md)。

新增 **v3.0.0**：通用工程目录树、内容摘要和显式引用索引，覆盖 PLC/UDT/变量表、GRAPH、经典 HMI 和硬件。目录先建、内容按需分批读取，索引保存在工程旁，可随工程复制。新增 `tia_build_project_index`、`tia_get_project_tree`、`tia_find_references`、`tia_get_task_context`。连接返回索引状态，正常读取可积累内容，写入标记索引过期，MCP 初始化与接口说明引导 Agent 优先查询任务上下文。用法与限制见 **[PROJECT_INDEX.md](PROJECT_INDEX.md)**，验证见 **[VALIDATION_3.0.md](VALIDATION_3.0.md)**。

**建立完整工程索引**：双击本目录的 **`建立工程索引.bat`**，在文件选择框中选择工程，自动扫描目录并分批建立全部可读取内容，显示进度、汇总失败项，不编译、不保存、不下载。也支持 `BuildProjectIndex.cmd "E:\Projects\Line\Line.ap17"`，中断后可加 `-Resume` 继续。目标工程需先在博途中打开。

新增 **v2.9.0**：面向所有 GRAPH 的通用批量修改接口 `tia_update_graph`，一次导出/解析处理整组修改，复用读取快照备份，支持无变化跳过、内容指纹核对、可选备份与编译。原有生成、模型读取和 XML 补丁接口复用解析结果；大流程连线与循环检查使用索引。该版共60个工具。调用方法、离线测速与启用状态见 **[VALIDATION_2.9.md](VALIDATION_2.9.md)**。

新增 **v2.8.0**：PLCSIM Advanced 本机实例启动、控制面板、Softbus 装载、RUN/STOP、优化 DB 符号读取及自动复位测试入口。提供 `Start-AutoResetSimulation.ps1` 串起测试流程；该版共59个工具。普通 PLCSIM 的 S7ONLINE 监视不等于 Advanced 自动控制。用法和真实验收状态见 **[SIMULATION.md](SIMULATION.md)**、**[VALIDATION_2.8.md](VALIDATION_2.8.md)**。

新增 **v2.7.0**：
- 按精确组读取程序块/UDT、按路径裁剪硬件发现、HMI 按文件夹直接读取，避免为局部任务扫描整工程。
- 硬件批量计划增量维护索引；HMI 批量导入每类只建立一次目录；未变化属性和严格相同的 HMI XML 可跳过重复写入，保留实际覆盖前的备份。
- 可选精简 XML/源码响应，GRAPH 可按需省略重复模型或基础 XML；UTF-8 传输；增加每次调用的耗时和访问计数。

调用方式、兼容性与测量方法见 **[PERFORMANCE.md](PERFORMANCE.md)**。

新增 **v2.6.0**：
- **多项目同时保持连接**：统一 MCP 入口按 `project_path` 分发，每个项目使用独立后台进程和缓存，可隔离 V17/V21 等版本的 Siemens API。
- **明确目标**：只管理一个项目时保留原调用方式；管理多个项目后，读写和生成必须明确提供 `project_path`，不会默认切换工程。
- **独立查看和释放**：`tia_list_connections` 查看全部连接；`tia_disconnect(project_path)` 仅释放一个项目；`tia_disconnect_all` 释放全部，不保存、不关闭博途项目。
- **超时不重放**：项目后台退出时不自动重试操作；超时后先通过连接状态检查延迟结果，再决定后续操作。

用法和验证边界见 **[MULTI_PROJECT.md](MULTI_PROJECT.md)**。当前统一入口顺序处理请求；本版实现多个项目同时保持连接，不承诺多项目写入并行执行。

新增 **v2.5.0**：
- **GRAPH 从零新建和完整修改**：新增 `tia_generate_graph`、`tia_read_graph_model`、`tia_write_graph`，支持步、动作、转换、互锁、监控、选择/并行分支、跳转、前后置逻辑、接口及完整 XML 模型往返。内置 V17/V21 独立骨架。
- **硬件与网络组态**：新增批量读取、构建计划和硬件编译接口，支持新建设备、插入模块、修改地址/属性、创建子网、IO 系统及物理端口连线。
- **经典 HMI 批量构建**：新增组态目录/内容读取、画面和变量表生成、批量写入与统一编译，完整节点模型可保留控件、事件和动态绑定。
- **修复目标和恢复问题**：PLC/HMI 精确匹配；批量导入失败正确报告；UDT、变量表及程序块恢复按类型分流；新备份保存原组、目标身份与内容指纹。
- **构建同步运行资产**：两个启动目录同步主程序、辅助程序及模板；构建失败明确返回错误，不再隐式请求管理员权限。

模型格式、调用示例、版本边界与验证方式见 **[CAPABILITIES.md](CAPABILITIES.md)**。

新增 **v2.4.0 性能优化与全场景 MCP 扩展**：
- **减少重复扫描和调用**：引入会话级块/UDT 内存索引缓存、按路径直达查找（`Groups.Find` + `Blocks.Find`）、反射属性/方法与 DLL 路径缓存。实际提速取决于工程规模与操作，未给出统一倍率。
- **灵活编译策略与批量接口**：单块导入支持 `compile_mode: "plc" | "block" | "none"`（支持单块极速编译或连续改块时跳过中间编译）；新增 `tia_read_sources_batch` 与 `tia_import_sources_batch` 批量读写源码（自动按 `UDT -> FC/FB -> DB` 顺序导入并统一编译一次）。
- **补齐核心工程能力**：新增 `tia_list_types`（列出 UDT）、`tia_list_tag_tables` / `tia_read_tag_table` / `tia_import_tag_table`（读写全局 I/O 变量表与常量）、`tia_restore_backup`（一键回滚备份）、`tia_delete_block`（带强制备份的安全删除）及 `tia_compile_hmi`。
- **增强 `LadBuilder`**：支持生成 `FB`（含 `Static` 静态变量段）、数组下标访问（如 `Station[1].Sensor`）、上升/下降沿触点（`edge="p"|"n"`）与比较指令（`compare: "==" | "<>" | ">" | "<" | ">=" | "<="`）。
- **会话自愈与双目录同步构建**：博途重启后自动探测失效连接并重连；`build.bat` 与 `build-online.ps1` 自动同步 `TiaPortalSourceBridge.exe` 与 `S7OnlineWorker.exe`。

另有 **v2.3.0 在线监视扩展**：普通 PLCSIM V17 本地 S7ONLINE 读取已验证，另有实体 PLC 只读 S7 TCP 通道、变化记录和受限仿真测试入口。请见 [ONLINE_README.md](ONLINE_README.md)；使用 `StartOnlineMcp.bat` 启动独立在线服务。PLC 内测试/工步记录块需要另行导入、编译并下载到 PLCSIM。

一个面向 AI 助手（Gemini CLI / Claude / Cursor / Codex 等）与自动化工程工作流的西门子博途（Siemens TIA Portal V16～V21）本地桥接工具。通过 TIA Portal Openness API，支持**常驻本地 MCP 单块读写/编译服务**以及**整包 PLC/HMI 源码导出与回导**。

---

## 本次重构与修复亮点（v2.2）

1. **剥离产线硬编码，恢复核心通用性**：
   - 将原混入 `Program.cs` 与 `OpennessSession.cs` 中的数百行 `OP260/OP270` 特定产线画面/变量清理代码剥离至独立模块 `Openness/LegacyOp270Tools.cs`，核心会话与命令完全通用，同时支持多 HMI 选择（`--hmi`）与设备组（`DeviceGroups`）递归发现。
2. **修复非默认安装路径下 Openness DLL 版本匹配 Bug**：
   - 重构 `ReflectionApi.cs`：优先按目标版本（如 `V17` 或 `V21`）匹配进程、注册表（`_InstalledSW`）及各磁盘目录，避免博途装在 `D:\` / `E:\` 时被后台其他版本的进程劫持；同时将 `AssemblyResolve` 改为静态单例注册，消除长连接下的事件句柄泄漏。
3. **修复 `TrustThisTool.ps1` 免授权白名单脚本**：
   - 补齐西门子官方规范要求的 `\Entry` 标准子键及 `16.0`～`21.0` 全版本 `Whitelist` / `AllowList` 注册表路径，并支持自动检测本机已安装的 Openness 版本及在缺失 `.exe` 时自动触发构建，解决连接已运行博途时的弹窗超时问题（`EngineeringSecurityException`）。
4. **增强单块操作与新增单块纯文本源码 MCP 接口**：
   - 新增 `tia_read_source` 与 `tia_import_source`：无需整包导出，即可通过 MCP 直接读取和回导单个 `SCL`、`STL`、`DB` 或 `UDT` 的纯文本源码（`.scl` / `.db` / `.udt` / `.awl`），自动备份原块并触发 PLC 编译。
   - 修复 `BlockOperations.cs` 中 `group_path` 区分大小写和斜杠敏感问题；单块导入默认支持自动创建缺失的目标用户组（`create_group_if_missing=true`），且在省略 `group_path` 时能自动定位已存在同名块所在的原组。
   - 修正块号冲突检测：将 `GlobalDB`、`InstanceDB`、`ArrayDB` 统一纳入共享的 `DB` 编号空间检查，并兼容无显式 `<Number>` 的自动编号块。
5. **升级 `LadBuilder` 梯形图生成器**：
   - 支持通过 `interface_json` 定义 FC 块接口参数（`Input` / `Output` / `InOut` / `Temp` / `Constant`）。
   - 支持访问局部变量（`#Var` 或接口已声明的变量名自动生成 `Scope="LocalVariable"`）。
   - 支持置位线圈（`coil_type="set"` $\rightarrow$ `SCoil`）、复位线圈（`coil_type="reset"` $\rightarrow$ `RCoil`）与取反线圈（`coil_type="negated"`），并修正 V21+ `<Namespace/>` 版本判断与多支路空分支校验。
6. **修复构建脚本与测试脚本**：
   - `build.bat` 在回退使用系统自带 `csc.exe` 时自动排除 `obj/` 与 `bin/` 目录，并強制指定 `/codepage:65001` UTF-8 编码，杜绝中文环境下乱码与重复特性报错。
   - 修复 `tests/SmokeMcp.ps1` 中写死旧版本号 `2.0.0` 导致自检失败的问题。

---

## 快速开始

### 1. 启动现代化 Web 可视化控制看板（推荐）

双击根目录下的 **`打开可视化看板.bat`**（或运行 `WebDashboard.cmd`）：
- 自动启动嵌入式轻量 HTTP 服务，并在浏览器中打开控制面板（`http://localhost:18980/`）。
- **博途运行状态**：实时检测本机运行的博途进程（PID、版本 V16～V21、内存占用）、目标 PLC 与当前项目。
- **变更审查与 Diff 对比**：左侧一览历史备份，右侧展示逐行新增（绿色）/删除（红色）差异对比，修改代码更安全放心。
- **工程两阶段索引与速查**：秒级模糊搜索程序块、UDT 数据类型、全局变量表及 HMI 画面。
- **PLCSIM Advanced 仿真驾驶舱**：虚拟 PLC 实例启停、RUN/STOP 状态监控与自动复位闭环测试。
- **快捷指令一键执行**：一键白名单注册、导出 PLC/HMI 源码、导回博途、PLC 编译。
- **MCP 工具字典与实时日志**：完整浏览 66+ 个 MCP 工具能力说明，实时滚动查看 MCP 请求日志与编译诊断。

### 2. 首次免授权白名单注册（可选命令行）

首次使用或重新构建后，右键以管理员身份运行或双击 **`TrustThisTool.bat`**：
- 若尚未编译 `.exe`，脚本会自动调用 `build.bat` 完成编译。
- 自动计算当前 `TiaPortalSourceBridge.exe` 的 SHA256 摘要与 UTC 修改时间，写入本机 V16～V21 的 Openness 白名单，后续连接博途不再反复弹出确认框。

### 3. 最简单的交互式导出/导入（适合网页版 AI 协作）

1. 双击 **`OpenTool.bat`**（或 **`TIA源码工具.bat`** / **`双击这里打开.bat`**），或者直接把 `.ap17` / `.ap20` / `.ap21` 项目文件拖到 `TIA源码工具.bat` 上。
2. 在弹窗中选择博途工程文件，随后输入 `1` 选择**导出 PLC 与 HMI 源码**。
3. 导出完成后，项目旁会生成 `<项目名>_PLC_Source` 文件夹（包含 `.scl`、`.db`、`.udt`、`.xml` 与 `manifest.json`）。
4. 将需要分析或修改的源码文件发给 AI 修改，覆盖回该目录后再次运行工具并输入 `2` **导回 TIA Portal**。

---

## 常驻 MCP 服务模式（V17 / V21）

供本地支持 MCP 协议的 AI 客户端（如 Gemini CLI、Claude Desktop、Cursor、Codex 等）直接调用博途：

1. 在 TIA Portal 中打开目标工程。
2. V17 双击 `StartTiaMcp.bat`；V21 双击 `StartTiaMcpV21.bat`（也可把 `.apXX` 文件直接拖到 `StartMcp.bat` 上，服务会自动适配项目版本）。
3. 保持服务命令行窗口打开。客户端退出或重连不会释放 TIA Openness 连接。

**看板会顺带带起 MCP（v3.3 起）**：运行 `web`（`打开可视化看板.bat` / `WebDashboard.cmd` / `LaunchWeb.vbs`）时，程序会在后台自动启动一个**无窗口**的 `mcp-host`，所以不必再单独开服务窗口，客户端按下面的 `mcp-client` 配置直接接入即可。

- 已有服务窗口（或上一次看板启动的宿主）在运行时不会重复启动，只会提示"MCP 服务已经在运行"。
- 该后台宿主**随看板退出而结束**：看板进程消失后它会在约 1.5 秒内发现并自行退出；如果此刻正好有请求在跑，会先等它做完（最多 10 分钟）再退，避免把写到一半的导入打断。手动启动的服务窗口不受影响，仍然常驻到关窗口为止。
- 进程号记录在 `logs\mcp-host.pid`；需要立刻结束时运行 `StopTiaMcp.bat`（或 `TiaPortalSourceBridge.exe stop-mcp --config config.json`）。
- 后台宿主不会在启动时连接博途，只有调用 `tia_connect`/首个工程操作时才按需连接。

### MCP 客户端配置示例

- **连接已打开的常驻服务窗口（推荐，响应最快）**：
  - `command`: `E:\你的路径\TiaPortalSourceBridge\src\TiaPortalSourceBridge\bin\Release\net48\TiaPortalSourceBridge.exe`
  - `args`: `["mcp-client"]`
- **直接以独立 stdio 进程启动**：
  - `command`: `E:\你的路径\TiaPortalSourceBridge\src\TiaPortalSourceBridge\bin\Release\net48\TiaPortalSourceBridge.exe`
  - `args`: `["mcp", "--config", "E:\\你的路径\\TiaPortalSourceBridge\\mcp-config-v21.json"]`

### MCP 工具列表（共 66 个：49 个离线工程/索引/报警工具 + 7 个 PLCSIM Advanced 仿真工具 + 10 个在线工具）

| 接口 | 用途 | 是否修改工程 |
|---|---|---|
| `tia_build_project_index` / `tia_get_project_tree` / `tia_find_references` / `tia_get_task_context` | **【v3.0 新增】** 建立可随项目迁移的两阶段索引、查询工程树、查找符号显式引用、按任务关键词提取上下文 | 否（仅写 `.tia-source-index`） |
| `tia_read_hmi_alarms` / `tia_write_hmi_alarms` | **【v3.1 新增】** 读取或批量新增/修改经典 WinCC 离散报警（支持 UIA 界面桥接与原生 `.xlsx` 直读/回读校验） | 写入需 `dry_run=false, confirm=true` |
| `tia_simulation_*`（共 7 个） | **【v2.8 新增】** 本机 PLCSIM Advanced 实例检测、创建启停、Softbus 装载、按符号读取与自动复位闭环测试 | 装载/控制需 `confirm=true` |
| `tia_generate_graph` / `tia_read_graph_model` / `tia_write_graph` | 从零构建 GRAPH、读取完整模型、完整修改并导入编译 | 写入需 `confirm=true` |
| `tia_update_graph` | 通用 GRAPH 批量补丁：步骤、动作、条件、分支、连线、时间、多个顺序 | 默认预览；写入需 `dry_run=false, confirm=true` |
| `tia_read_hardware_configuration` / `tia_apply_hardware_configuration` / `tia_compile_hardware_configuration` | 快速读硬件网络、执行设备/模块/地址/网络构建计划、编译设备 | 执行需 `dry_run=false, confirm=true` |
| `tia_generate_hmi_configuration` / `tia_read_hmi_configuration` / `tia_write_hmi_configuration` | 批量构建/读写经典 HMI 画面、控件、变量及完整对象模型 | 写入需 `confirm=true` |
| `tia_connection_status` / `tia_connect` / `tia_disconnect` | 查看、建立或释放指定项目的连接；多个项目连接可以同时保持 | 否 |
| `tia_list_connections` / `tia_disconnect_all` | 查看全部连接或释放全部连接，不保存、不关闭工程 | 否 |
| `tia_list_open_projects` | 列出本机正在运行的博途实例与已打开工程路径 | 否 |
| `tia_list_plcs` | 列出工程内的所有 PLC 与经典 WinCC HMI | 否 |
| `tia_list_blocks` | 快速列出 PLC 程序块（内置会话缓存，支持 `group_filter` / `name_filter` / `language_filter` / `refresh`） | 否 |
| `tia_list_types` | **【v2.4 新增】** 快速列出 PLC 所有 UDT 数据类型（内置缓存，支持按组/名称过滤） | 否 |
| `tia_list_tag_tables` | **【v2.4 新增】** 列出 PLC 所有变量表（Tag Tables）及表内变量/常量数量 | 否 |
| `tia_read_tag_table` | **【v2.4 新增】** 读取单个 PLC 变量表的 SimaticML `.xml`（含 `%I`/`%Q`/`%M` 符号与常量） | 否 |
| `tia_import_tag_table` | **【v2.4 新增】** 导入单个 PLC 变量表 `.xml`，自动备份原表 | 是（需 `confirm=true`） |
| `tia_list_block_groups` / `tia_organize_block_groups` | 列出或重命名/补齐程序块用户文件夹（含空组） | 组整理需 `confirm=true` |
| `tia_read_source` | 读取单个 `SCL`/`STL`/`DB` 块或 `UDT` 的纯文本源码（`.scl`/`.db`/`.udt`/`.awl`） | 否 |
| `tia_read_sources_batch` | **【v2.4 新增】** 批量一次读取多个 `SCL`/`STL`/`DB`/`UDT` 纯文本源码，大幅减少 AI 往返等待 | 否 |
| `tia_import_source` | 导入单个 `SCL`/`STL`/`DB`/`UDT` 纯文本源码，自动从源码提取名称备份原对象，支持 `compile_mode: "plc"\|"block"\|"none"` | 是（需 `confirm=true`） |
| `tia_import_sources_batch` | **【v2.4 新增】** 批量导入多个 `SCL`/`STL`/`DB`/`UDT` 源码，自动按 `UDT -> FC/FB -> DB` 排序导入并统一编译一次 | 是（需 `confirm=true`） |
| `tia_read_block` | 读取单个程序块的 SimaticML `.xml`（含 LAD/FBD 网络，提供 `group_path` 时极速直达） | 否 |
| `tia_import_block` | 导入单个程序块的 SimaticML `.xml`，支持自动创建组、自动备份原块及 `compile_mode` 控制 | 是（需 `confirm=true`） |
| `tia_restore_backup` | 从备份 XML 恢复程序块、UDT 或 PLC 变量表；新备份自动核对目标、指纹和原组位置 | 是（需 `confirm=true`） |
| `tia_delete_block` | **【v2.4 新增】** 安全删除指定程序块或 UDT（删除前强制导出 XML 备份） | 是（需 `confirm=true`） |
| `tia_generate_lad` | 生成 LAD `FC`/`FB` 的 SimaticML `.xml`（支持 `Static` 变量、数组下标 `Arr[1]`、边沿触点 `edge="p"\|"n"`、比较指令 `compare`、置位/复位线圈） | 否（生成后用 `tia_import_block` 导入） |
| `tia_read_graph` | 读取现有 GRAPH 顺控 FB，返回步/动作/转换条件/连线摘要（支持 `include_raw_xml=false` 省流加速） | 否 |
| `tia_patch_graph` | 通用 XPath 补丁修改 GRAPH FB，支持属性、文本、替换、追加、前后插入、删除 | 否（生成后用 `tia_import_block` 导入） |
| `tia_compile_plc` / `tia_compile_hmi` | 编译指定 PLC 或经典 WinCC HMI 并返回结构化错误与警告诊断 | 否 |
| `tia_export_plc_sources` | 整包导出 PLC 与经典 WinCC HMI 到源码目录 | 否（写入源码目录） |
| `tia_import_plc_sources` | 从源码目录按 `manifest.json` 整包回导 PLC 与 HMI | 是（需 `confirm=true`） |
| `tia_save_project` | 保存当前博途工程的未保存修改到磁盘 | 是（需 `confirm=true`） |
| `tia_online_*`（共 10 个） | 在线连接、批量读标准 DB、GRAPH 在线诊断、PLC 环形轨迹读取、后台采样记录与受限仿真故障注入（详见 [ONLINE_README.md](ONLINE_README.md)） | 默认只读 |

### `tia_generate_lad` 参数示例（含局部变量接口与置位线圈）

```json
{
  "block_name": "Motor_Control",
  "block_number": 32000,
  "interface_json": "{\"Input\":[{\"name\":\"Start\",\"type\":\"Bool\"},{\"name\":\"Stop\",\"type\":\"Bool\"}],\"Output\":[{\"name\":\"Motor\",\"type\":\"Bool\"}]}",
  "networks_json": "[{\"title\":\"启动自锁\",\"coil\":\"#Motor\",\"coil_type\":\"coil\",\"branches\":[[{\"symbol\":\"#Start\"},{\"symbol\":\"#Stop\",\"negated\":true}],[{\"symbol\":\"#Motor\"},{\"symbol\":\"#Stop\",\"negated\":true}]]}]"
}
```

---

## 环境要求与安全边界

1. **环境要求**：Windows 10/11 x64、对应版本的 TIA Portal 与 Openness 组件、.NET Framework 4.8 运行时，且当前 Windows 用户已加入本机 **`Siemens TIA Openness`** 用户组（加入后需注销重登 Windows）。
2. **安全边界**：本工具**仅操作离线博途工程**，绝不自动下载程序到真实 PLC，也不控制 CPU 启停。在首次对重要工程执行导入前，请务必备份工程文件夹或创建 `.zapXX` 归档。

## 可靠性与开发回归（2026-11-02）

- 看板操作固定到确认窗口显示的完整工程路径和 PLC 路径。请求携带本次服务签发的目标快照；其他标签页切换工程不会改变已确认目标。旧页面需要刷新；服务重启后快照失效。
- 工程任务和索引任务共用串行通道。看板等待超过 30 分钟时显示“超时，等待后台结果”，保留进程和占用状态，最终结果到达后自动更新。不会因为等待超时杀死导入或编译进程。退出看板仍会等待这些任务完成。
- 每个看板任务带 `task_id`、目标和阶段；`/api/action/status?task_id=...` 查询最近 100 项任务（当前服务进程内保存，重启后不保留）。取消退出码为 3，不会显示成功。这里没有增加对执行中 TIA 写入的强制取消按钮。
- 两个引擎共享 `shared/` 中 11 个模块，包括项目后台协议、备份身份验证、内容索引和模型构建；Siemens 版本相关实现和 STA 进程继续隔离。普通构建和 SDK 项目均直接编译共享源码。
- MCP 等待时间可用环境变量 `TIA_BRIDGE_REQUEST_TIMEOUT_SECONDS` 设置为 1～3600 秒，默认 600 秒。超时后查询 `tia_connection_status` 获取延迟结果，不要重放写入。
- 统一离线回归：`powershell -NoProfile -ExecutionPolicy Bypass -File tests/Run-Checks.ps1`。需要 Windows、.NET Framework 编译器和 Python 3；不要求连接 TIA 工程。覆盖双引擎生成路由、目标快照与 HTTP 请求、超时恢复、备份身份/指纹、批量部分失败和浏览器生命周期。
- `Build-All.ps1` 在替换现有程序之前执行完整回归；失败时保留现有程序。GitHub Actions 的 Windows 检查调用同一入口；远程运行需将仓库推送到 GitHub。
- 源码已纳入本地 Git；本机配置、运行记录、编译输出和工程备份排除在版本管理之外。未设置远程仓库。
