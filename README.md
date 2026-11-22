# TIA Portal Source Bridge

**简体中文** | [English](README.en.md)

[![Offline regression](https://github.com/qiap/TiaPortalSourceBridge/actions/workflows/checks.yml/badge.svg)](https://github.com/qiap/TiaPortalSourceBridge/actions/workflows/checks.yml)

连接 **Siemens TIA Portal** 与 **AI / 自动化工作流** 的本地桥接工具。

通过 TIA Portal Openness，提供 PLC 源码读写、工程索引、编译诊断、HMI 与硬件组态等能力。既可以在浏览器看板中操作，也可以通过 MCP（Model Context Protocol）接入 AI 客户端。

Windows 上从同一个看板或 MCP 入口操作 TIA Portal V16–V21 工程。

## 功能

| 模块 | 主要能力 |
| --- | --- |
| Web 看板 | 查看工程与连接状态、执行工程任务、审查备份差异、检索索引、查看日志 |
| 程序体检 | 只读检查显式地址重叠、多处写入、模板参数缺线和实例复用；查看 OB 调用路径、上下游调用及定位证据，导出 Excel 报告 |
| 气缸执行记录 | 持久保存生成、核对和后续处理记录；显示逐块结果、编译警告、备份与保存状态；核对后继续或恢复失败任务 |
| 工程审查工作流 | 独立源码快照互比、显式调用方影响路径、电气点表审查写回、多语言文本回填与交付资料导出 |
| 在线监测与参数 | 标准 DB 只读采样、变化波形、参数归档、按名称和类型预览初始值回填、GRAPH 布尔条件核对 |
| PLC 源码 | 读取与导入 SCL、DB、UDT、SimaticML XML，支持单块及批量操作 |
| 工程索引 | 工程目录树、内容检索、显式引用查询、按任务提取上下文 |
| LAD / GRAPH | 生成 LAD、读取和编辑 GRAPH 模型、批量修改及导入 |
| HMI 与硬件 | 经典 HMI 画面、变量和报警处理，硬件与网络组态读取及修改 |
| 多项目连接 | 按工程路径分发请求，以独立进程隔离工程和版本相关 API |
| 在线与仿真 | 在线变量读取、采样记录及 PLCSIM 相关工具；具体能力取决于通道和版本 |
| V21 扩展 | SimaticSD 文档、Software Units、官方交叉引用、Unified HMI 读取与编译、AutomationML |

完整工具及参数以运行中的 **MCP 工具字典**或 `tools/list` 返回结果为准。各模块的使用方法见[文档导航](#文档导航)。

## 下载与使用

普通用户无需下载源码或自行编译，选择一种方式即可：

v4.16.0 同时提供 Windows 便携 ZIP 与 EXE 安装包。

v4.16.0 按工程版本选择 Openness API，支持 V16–V21。V17、V18、V21 做过原生 API 实测，其中 V17 实际发现了打开的工程。各版测试深度见下方[版本兼容性](#版本兼容性)。

| 方式 | 下载 | 使用方法 |
| --- | --- | --- |
| ZIP 免安装版 | [下载 v4.16.0 Portable.zip](https://github.com/qiap/TiaPortalSourceBridge/releases/download/v4.16.0/TiaPortalSourceBridge-4.16.0-Portable.zip) | 完整解压后双击 `WebDashboard.cmd` 或 `LaunchWeb.vbs` |
| EXE 安装版 | [下载 v4.16.0 Setup.exe](https://github.com/qiap/TiaPortalSourceBridge/releases/download/v4.16.0/TiaPortalSourceBridge-4.16.0-Setup.exe) | 运行安装程序，通过桌面或开始菜单打开 |

两种方式均包含 V16–V20 共用引擎和 V21 独立引擎、默认配置和运行模板。先在 TIA Portal 中打开工程，再在看板选择工程和 PLC；目标电脑需满足下面的运行环境要求。

[全部版本与发布说明](https://github.com/qiap/TiaPortalSourceBridge/releases) · [v4.16.0 版本说明](docs/Release-4.16.0.md) · [ZIP / EXE 的 SHA-256](https://github.com/qiap/TiaPortalSourceBridge/releases/download/v4.16.0/SHA256SUMS.txt)。

GitHub 的 **Code → Download ZIP** 和 Release 中自动生成的 **Source code** 是源码压缩包；需要解压即用，请选择上面的 **Portable.zip**。EXE 安装包目前未做代码签名。

刷新、切换或关闭浏览器标签页不会停止后台服务。需要退出时，点击看板右上角的“退出服务”；正在执行的工程任务会先完成。

### 执行记录与失败恢复

看板提供可收起的侧栏，窄屏使用横向导航。“任务中心”按当前工程与 PLC 汇总机构生成、点表写回、源码回填、HMI 联动、带完整目标身份的仿真记录和参数归档，并保留实时执行日志入口。可以筛选状态、搜索对象、直接打开原任务，不必手动记下编号。历史扫描有数量和大小上限，跳过项会提示；旧仿真记录缺少 PLC 身份时不会猜测归属。读取列表不会重新执行操作。

HMI 联动、外设仿真和在线监测变量默认使用表单，原始配置保留在“高级设置”中。监测变量仍需明确填写标准 DB 绝对地址，符号名不能证明地址正确。变量清单可按工程保存到当前浏览器。写入结果展示状态、保存情况、逐块结果和下一步提示，原始诊断证据可展开查看。

“文本与参数”独立于 Diff 页面，参数归档可从列表选择。I/O 页明确区分本地源码快照和从博途重新读取，连接状态刷新不会更新源码。录波支持时间刻度、指针或键盘查看数值、缩放及暂停画面；暂停画面不会停止后台采样，缺失或异常数据不会显示为正常值。

控制中心和运行日志中的“工程变更记录 · 气缸”可以重新打开历史任务。任务结束后刷新页面、重新打开浏览器或重启工具，记录仍保存在本机；普通调用日志最近 100 条的限制不影响这些工程变更记录。

气缸操作失败时保留现场，不自动回滚。先点击“核对当前工程”，工具只读检查后台是否仍在执行、程序与 I/O 声明是否变化，以及本次备份是否完整。核对通过后可查看“继续”或“恢复”方案，再明确确认执行；保存操作会保存整个当前工程，包括其他未保存修改。继续仅补未完成步骤，恢复会先备份当前现场，并处理本次变化涉及的程序块和关联实例 DB。

页面断线或请求超时只会查询原任务，不会再次提交写入。无法确认旧后台已结束、发现后续修改或备份证据不足时，自动继续与恢复保持禁用。旧版任务缺少完整恢复证据时仍可查看记录和备份，但需要人工核对处理。本功能操作博途中的离线工程，不下载到 PLC。

### 气缸模板与批量生成

在“气缸生成”中读取工程、选择已有实例，并核对各参数用途后，可以将模板规则保存到当前工程。模板保存在工具目录的 `project-templates/` 中；新增其他气缸不会使原样板规则失效，样板接线或功能块接口改变时则需要重新核对。模板规则可导出、导入，换工程使用时必须按目标工程重新确认接线和点位。

填写气缸名称、程序实例名、数组索引并选择空闲 I/O 后，可以加入批量清单。每批最多 20 个同模板气缸，检查重复名称、实例、索引和物理点位后，将修改合并为每个程序块一次写入，再统一编译、回读和保存。失败的批次同样保留完整执行记录。模板文件与工程执行记录均为本机数据，不随源码上传。

### 电气 Excel 点表对账

在“I/O 表与引用”页的“电气 Excel 点表对账”中，读取当前 PLC 点表，上传 `.xlsx` 文件，选择工作表、表头行以及地址、名称、类型和注释对应的列。地址和名称必选；类型与注释可以不比较。选择“原始变量表”或“映射后的 I/O 点位”，再指定软件单元及变量表范围，即可开始只读对账。

结果区分字段差异、两边各自列出的项、重复地址/名称、地址范围重叠、多个匹配及无效 Excel 数据；每项显示 Excel 原始行号和 PLC 候选值。地址按 I/Q/M 位、字节、字、双字统一格式比较，支持 `%`、德语 E/A 和前导零。映射点位按完整符号名称核对。字节/位重叠可能是合法别名，零条引用证据也不等于未使用。切换工程、重新读取点表或改变列对应关系后，需要重新对账。

对账本身只读。V16–V20 可选择单个普通原始变量表中的“字段差异”或“仅 Excel”行，生成审查方案；确认后重新核对全部变量表指纹、备份、导入、编译、回读，再保存整个离线工程。冲突、软件单元、映射 DB、类型宽度不明确或读取不完整的行不直接回写。重命名不自动改写 PLC/HMI 外部引用，需在预览中核对。失败保留记录和现场，恢复需先确认执行后内容未再改变。V21 保留只读对账。

支持不超过 8 MB、20 张工作表、总计 20000 个非空行和 128 列的 `.xlsx`；旧 `.xls` 请先另存。包含隐藏行，不应用 Excel 筛选；公式、错误值与合并数据单元格明确列为待修正，建议先粘贴为逐行普通值。比较的是已读取的快照，不包含未分配的硬件通道、在线值或外部设备占用。上传文件与报告只暂存于当前进程，各保留最近 3 份；需要留档请导出完整报告，包含概览、全部对账结果与 PLC 候选证据，不受页面筛选影响。

### 结构化差异审查

在“变更审查与 Diff”中选择历史备份，与当前工程、PLC 的已读取索引源码快照对比。支持变量表的名称、地址、类型和注释，块接口及 DB/UDT 成员的声明顺序、类型、初始值，以及按顺序对应的程序网络。可按范围筛选结构化变化、展开前后内容，或切换原始文本逐行核对。页面保留备份与快照的时间和 SHA256 指纹。

对比不连接或写入博途。需要最新状态时，先在 I/O 页面“从博途重新读取”，再返回对比。缺失、过期、指纹不一致或身份不唯一的源码会报错，不当作全部删除；备份列表仅显示当前工程与 PLC 最近 100 条，包含批量操作子目录。旧备份没有软件单元身份，暂不自动匹配单元内对象。

第一版是结构化 XML 对比：忽略导出外壳时间及已知对象 ID，受支持的 LAD/SCL 网络 UId 按定义与连接引用一起归一化，未知结构保守保留。网络按原始顺序对应，插入/移动可能产生多条变化；成员改名显示为删除和新增。GRAPH/STL 网络及未知结构仍可能包含编号噪声，纯文本与 SimaticSD 保留原始文本对比。不判断逻辑等价、DB 实际偏移、下载重新初始化或 HMI 影响。单份源码上限 8 MiB，完整响应上限 32 MiB，超限明确报错。

### 源码快照、影响路径与交付资料

“变更审查与 Diff”可把已读取索引保存为独立源码快照，再任选同一工程、PLC 的两份快照比较。快照保存完整源码与 SHA256；缺失、损坏或目标不一致时拒绝对比。影响路径来自两份快照中已解析的调用关系，列出受影响的上游调用方；不是 DB 布局、HMI 绝对地址或下载重新初始化的完整预测。

“交付资料”导出 ZIP，包含源码目录、原始 XML、调用关系 JSON/Mermaid、I/O 与交叉引用 Excel、翻译表及可解析的 GRAPH 结构图。覆盖说明随包提供，未读取的 HMI 不会被当作已交付。

### 名称、初始值与源码恢复 · V16–V20

- **多语言文本**：选择源码快照，导出翻译表；只编辑 `Translation` 并将待回填行的 `Action` 改成 `update`。重新上传后核对原文、语言、资源身份和源码指纹。“语言覆盖”页对照快照已出现的语言列出空文本、未声明与重复项，不自动新增工程语言。
- **参数归档**：在“在线监测与参数”按 `符号名称 | DB 地址 | 类型` 配置只读通信，读取并归档实际值。在审查页选源码快照，填写归档编号，预览回填。仅处理同名、同类型、同 DB 编号的标准全局 DB 标量；数组、优化 DB、实例 DB 与无法核对项明确排除。名称与物理偏移由用户配置，不能证明与在线工程符号一致；快照也不保证来自同一扫描周期。
- **审查写回**：当前直接写回范围为 1–50 个普通 PLC 块。逐项确认后备份、导入、编译、完整回读（含注释与初始值），全部通过后保存整个工程。变量表文本、UDT 等不通过此入口写入。
- **失败处理**：按执行编号查询持久记录。选择“核对并预览继续/恢复”会重新读取相关块，核对原操作后的指纹，再生成一个新方案；仍需确认后执行。缺失回读证据、原任务未结束或对象后续发生变化时拒绝覆盖，不会自动回滚或重放。

### 机构复用与经典 WinCC 联动 · V17

模板规则增加电机、阀门、真空机构、伺服外围使能类型。仍复用当前项目已有的直接 FB 多重实例调用，保留原有检查与最多 20 台同模板批量流程；每个 Bool 映射 I/O 参数需明确填写用途后缀。此功能不创建驱动硬件、运动轴或安全程序，也不会推断互锁逻辑。

机构页的 HMI 联动面板可以带入名称和实例，再配置完整经典 HMI 名称、报警编号、原报警模板编号、已声明的 16 位 HMI 触发变量及位号。报警文字支持 `{name}`、`{instance}`、`{index}`。文本列表可选，仅回填已存在的单值 `Value` 索引和目标语言纯文本；动态字段、范围或未知导出结构拒绝自动处理。先核对 PLC 生成结果，再预览 HMI 修改，确认后分步导入、回读、编译并保存整个工程。PLC 与 HMI 不是原子事务，HMI 失败保留独立记录与备份供人工核对，不撤回已完成的 PLC 修改。

经典报警操作使用博途原生界面导出/导入，需要桌面已登录且未锁屏。HMI 触发位与 PLC 数组位的对应关系需明确核对，不能仅凭同名认定绑定正确。此流程尚无真实工程自动回归证据。

### 只读录波与 GRAPH 条件核对

“在线监测与参数”支持 S7ONLINE / S7 TCP 标准 DB 绝对地址读取，最多 128 个变量、50–10000 ms 采样间隔和 30 分钟单次记录。页面显示最近 500 条变化事件及各通道波形，停止后导出完整 JSONL。质量不良显示未知；轮询不能保证捕获短脉冲，也不是 PLC 逐扫描 Trace。

GRAPH 面板选择已保存的源码快照、块与步，显示该步互锁/监视条件及可追踪的后续转换。仅计算全局标量常开/常闭串并联触点；比较、沿、计时、局部/数组/间接访问等显示未知。手动绑定监测名称并点击“读取一次”后可核对布尔值，超过 10 秒的样本失效。所选步并非自动检测到的活动步，条件为真也不代表设备可安全动作。

### 可配置外设仿真 · V17

“仿真驾驶舱”支持 Bool 命令到反馈的延迟规则、卡阻值、定时激励与时刻断言。仅允许本工具绑定当前工程、处于 RUN 的本机 PLCSIM Advanced Softbus 实例；不能切换为真实 PLC 通道。明确确认后执行，重复反馈写入和反馈环路会被拒绝。停止结束后续写入，保留仿真值，不自动复位或停止 CPU。

每次运行保存配置、检查结果和写入意图日志；时间窗口错过会判为失败，不用延迟结果冒充通过。页面展示最近 500 条事件，运行结束后可导出完整记录。离线假适配器测试只验证规则与保护流程，不能替代实际 SDK、下载和产线逻辑验证。

源码快照、参数归档、监测记录、仿真结果、HMI 联动备份和模板均为本机工程数据，不随 Git 源码上传。经典 WinCC 联动与外设仿真向导以 **V17** 为目标；V21 原有能力保持独立。

### 程序体检

选择工程和 PLC，打开“程序体检”。可以读取本地源码缓存后检查，也可以从博途重新读取后检查；已有完整读取快照时可直接检查。报告显示源码来源、时间、读取范围，以及程序块、网络和变量等定位证据，并可按规则或名称筛选。Excel 导出包含本次报告全部发现与覆盖说明，不受页面筛选影响。

检查变量表中受支持的显式 I/Q/M 地址、LAD 线圈与调用参数及部分 STL 写指令。模板参数检查只使用当前工程已确认且仍匹配的气缸模板。地址别名和多处写入可能是有意设计，结果需要结合调用顺序和设备用途核对。

“调用关系”按当前快照中的全部 OB 建立入口。选择程序块可查看一条已识别的 OB 调用路径，以及上游、下游调用的位置和实例；未解析的调用也保留在清单中。明确识别的同一背景 DB 或同宿主多重实例被多处调用时，会给出核对提示。不同软件单元、不同宿主的局部实例分别处理。“未发现 OB 路径”只说明当前识别范围内没有路径，不代表程序从未运行或可以删除。存在未读取源码、无法解析的目标或结果上限时，报告会标出覆盖缺口。Excel 报告增加“调用关系”和“OB调用路径”工作表。

SCL 检查读取完整缓存源码，识别受支持的直接赋值，以及根据被调用块接口核实的输出/双向参数写入；与 LAD/STL 的写入位置合并核对。注释、字符串和调用输入参数不会当作赋值。证据包含语句、行列及行号来源，可在页面查看或导出 Excel；行号对应导出文本或网络内重建文本，并非博途编辑器的绝对行号。

完整 SCL/GRAPH 控制流、Temp 初始化、动态寻址、硬件地址、DB 布局及 HMI/外部写入暂未覆盖；无法核实的调用、复杂目标和定时器/沿触发实例会注明覆盖缺口。不同分支的多处写入不一定构成冲突，未发现问题也不代表逻辑安全。分析不修改、编译、保存工程或下载 PLC。

报告暂存于当前服务进程，最多保留最近 4 次；需要留档请导出 Excel。

## 环境要求

- **系统**：Windows 10 / 11，64 位。
- **运行时**：.NET Framework 4.8。
- **工程操作**：对应版本的 TIA Portal 及 Openness 组件。
- **用户权限**：当前 Windows 用户加入 `Siemens TIA Openness` 本地用户组，变更后注销并重新登录。
- **源码构建与测试**：Windows PowerShell 5.1、.NET Framework C# 编译器、Python 3 及 Node.js 22（仅用于界面回归检查）。
- **可选功能**：在线通信需要相应 Siemens 通信组件；仿真需要对应的 PLCSIM / PLCSIM Advanced 环境。

本项目不包含 TIA Portal、Siemens DLL 或相关软件许可证。

### 版本兼容性

选择工程文件或在 MCP 请求中传入完整的 `project_path`。入口识别 `.ap16` 至 `.ap21` 及对应的 `.als` 文件，多项目按目标工程隔离执行。

| 工程版本 | 目标电脑需要的 API | 接口范围 |
| --- | --- | --- |
| V16、V17、V18、V19、V20 | 对应 Portal 主版本的 `Siemens.Engineering.dll` | 工程连接、普通 PLC 块和变量表读写、源码、索引、I/O、双工程对比及审查写回。I/O 页提供索引显式引用。 |
| V21 | .NET Framework 模块化 API，以 `Siemens.Engineering.Base.dll` 为入口 | 自动路由至 `engines/v21`，支持 Software Units、SimaticSD 和官方交叉引用。 |

在工具根目录运行 `.\src\TiaPortalSourceBridge\bin\Release\net48\TiaPortalSourceBridge.exe list-apis`，可查看这台电脑自动发现的各版本主程序集及其路径。该命令只读取安装信息。通过虚拟机使用博途时，请在运行服务的虚拟机内执行。

工程机须安装对应版本的 Portal、Openness 和所用产品选件。多用户本地会话还要求原生 API 提供 `LocalSessions`，V16 请使用普通 `.ap16` 工程。V16、V18、V19、V20 的 GRAPH 生成请提供同版工程导出的 `base_xml`。内置 GRAPH 模板覆盖 V17、V21。经典 WinCC 页面编辑和外设仿真向导面向 V17。离线回归覆盖路由与文件处理，各版本的工程编译和仿真联调须在相应安装环境验证。参见[西门子 Openness 安装信息](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/general-functions/information-about-installed-tia-portal-openness-versions) 和 [V21 API 变更说明](https://docs.tia.siemens.cloud/r/en-us/v21/readme-tia-portal-openness/major-changes-for-long-term-stability-in-tia-portal-openness-v21)。

#### 各版本实测记录

| 版本 | 本次实际验证 |
| --- | --- |
| V16 | 六版本离线回归；使用 V17 安装附带的 V16 兼容 XSD 校验生成的 LAD 接口和网络。未安装 V16 原生环境。 |
| V17 | 加载原生 Openness，核实 API 类型与方法，运行只读发现并识别到 1 个已打开的工程；通过 LAD XSD 校验。 |
| V18 | 加载原生 Openness，核实 API 类型与方法，通过 LAD XSD 校验。只读发现成功，当时没有打开的 V18 工程。 |
| V19 | 离线验证工程路由、XML 生成与版本检查，以及缺少 SDK 时的诊断。未安装 V19 原生环境。 |
| V20 | 离线验证工程路由、XML 生成与版本检查，以及缺少 SDK 时的诊断。未安装 V20 原生环境。 |
| V21 | 加载原生模块化 Openness，核实 API 类型与方法，通过 LAD XSD 校验。只读发现成功，当时没有打开的 V21 工程。 |

六个版本均有离线用例。本次没有在真实工程中执行导入、编译、保存或 PLC 下载，各版本的工程写入和仿真仍需在目标环境验收。

## 从源码运行

以下步骤在 **PowerShell** 中执行。

### 1. 获取代码

```powershell
git clone https://github.com/qiap/TiaPortalSourceBridge.git
cd TiaPortalSourceBridge
```

### 2. 创建本机配置

在仓库根目录创建 `config.json`，将工程路径和 PLC 名称替换为自己的配置：

```json
{
  "tiaPortalVersion": "V17",
  "projectPath": "D:\\Projects\\Demo\\Demo.ap17",
  "plcName": "PLC_1",
  "sourceDirectory": ".\\plc-src",
  "siemensEngineeringDll": "",
  "connectionMode": "AttachOnly",
  "withUserInterface": true,
  "saveProjectAfterImport": false,
  "compileBeforeExport": false,
  "includeSystemBlocks": false,
  "continueOnError": true,
  "allowProjectUpgrade": false
}
```

`siemensEngineeringDll` 留空时自动发现本机安装路径。默认配置仅连接已经打开的 TIA 实例，不自动保存或升级工程。

为 MCP 入口和 V21 引擎准备配置：

```powershell
Copy-Item config.json mcp-config.json

$v21 = Get-Content config.json -Raw | ConvertFrom-Json
$v21.tiaPortalVersion = 'V21'
$v21.projectPath = '.\SelectProject.ap21'
$v21 | ConvertTo-Json | Set-Content engines\v21\config.json -Encoding UTF8
Copy-Item engines\v21\config.json engines\v21\mcp-config.json
Copy-Item engines\v21\config.json mcp-config-v21.json
```

`SelectProject.ap21` 是引擎初始化占位路径；操作 V21 工程时应在看板选择真实工程，或在 MCP 调用中传入完整 `project_path`。如果主要使用 V21，也应将根目录配置改为真实的 V21 工程路径和版本。

### 3. 构建并打开看板

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Build-All.ps1 -NoTrust
.\WebDashboard.cmd
```

构建会编译两套引擎并运行离线回归检查，通过后更新运行文件。浏览器默认打开 [http://localhost:18980](http://localhost:18980)。

先在 TIA Portal 中打开目标工程，再在看板选择工程和 PLC，即可查看工程状态、建立索引或执行导出。

首次连接按 TIA Openness 提示授权。需要登记程序白名单时，使用看板中的“注册白名单”并确认 Windows 管理员提示。程序更新或位置变化后，可能需要重新登记。

## I/O 表、交叉引用与 Excel

在看板选择工程和 PLC，打开 **I/O 表与引用**，点击 **重新读取**。每次点击都会重新读取变量表和程序块引用，完成后显示读取时间；工程较大时请等待进度提示。

- 默认显示 I/Q 输入输出，也可查看全部变量和常量，按区域、变量表、名称、地址或注释筛选。
- 点击变量右侧的引用按钮，查看引用块、读写关系、位置及来源。
- **导出 Excel** 下载 `.xlsx`，包含当前筛选的全部分页、交叉引用明细和读取说明。
- 切换工程或 PLC 后需重新读取，避免混用旧结果；读取失败或覆盖不完整会明确提示。

数据来自 PLC 已声明变量表，不含未分配变量的硬件通道或在线实时值。V16–V20 显示索引提取的显式引用，V21 使用官方交叉引用接口；间接寻址、跨软件单元绑定等不能保证完整覆盖。零条引用不表示变量未使用，具体范围及警告会随 Excel 一并导出。

## 接入 MCP 客户端

### 连接看板的后台服务

看板启动时会自动启动 MCP 后台服务。客户端支持 `mcpServers` 格式时，可参考以下配置，将 `D:\\Tools\\TiaPortalSourceBridge` 替换为实际目录：

```json
{
  "mcpServers": {
    "tia-portal": {
      "command": "D:\\Tools\\TiaPortalSourceBridge\\src\\TiaPortalSourceBridge\\bin\\Release\\net48\\TiaPortalSourceBridge.exe",
      "args": ["mcp-client"]
    }
  }
}
```

### 独立运行

无需看板时，直接以 stdio 模式启动程序：

```powershell
.\src\TiaPortalSourceBridge\bin\Release\net48\TiaPortalSourceBridge.exe mcp --config .\mcp-config.json
```

在 MCP 客户端中使用相同的可执行文件，参数设为 `mcp`、`--config` 和配置文件的绝对路径。不同客户端的配置文件格式可能不同。

多工程场景中，调用应明确传入 `project_path`。推荐先获取工程树或任务上下文，再读取和修改相关对象，避免反复导出整个工程。连接管理详见 [MULTI_PROJECT.md](MULTI_PROJECT.md)。

## AI Skill

仓库提供 [tia-portal Skill](skills/tia-portal/SKILL.md)，指导 AI 使用现有 MCP 工具完成工程选择、索引检索、源码修改、备份与验证。Skill 不替代运行程序或 MCP 连接，也不会自动授予工程写入权限。

在本机 Codex 中使用时，将整个 `skills/tia-portal` 文件夹复制到技能目录（默认 `~/.codex/skills/`；设置了 `CODEX_HOME` 时为其下的 `skills/`），并按上文接好 MCP。示例请求：

```text
使用 $tia-portal 分析当前工程的气缸互锁逻辑，先不要修改工程。
```

本技能指导 AI 的工程操作流程；运行包继续支持独立解压使用或 EXE 安装。

## 构建 Windows 安装包

仓库包含 EXE 安装包构建脚本。构建机器还需安装 [Inno Setup 6](https://jrsoftware.org/isdl.php)：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build-Installer.ps1 `
  -CompilerPath 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
```

EXE 安装包、ZIP 免安装包和 SHA-256 校验文件输出到 `artifacts/installer/`。两种运行包使用同一份构建产物。脚本会重新编译双引擎并执行离线检查，使用独立生成的默认配置，不打包开发电脑的本机配置、日志或备份。

安装程序支持当前用户安装、桌面和开始菜单快捷方式、升级与卸载。升级保留配置；卸载保留使用过程中产生的配置、日志和备份。安装后的使用要求见 [安装说明](installer/INSTALL.txt)。

## 文档导航

| 文档 | 内容 |
| --- | --- |
| [工程索引](PROJECT_INDEX.md) | 建立索引、内容检索、引用查询与任务上下文 |
| [多项目连接](MULTI_PROJECT.md) | 目标选择、版本隔离、连接管理与超时处理 |
| [GRAPH、硬件与 HMI](CAPABILITIES.md) | 对象模型、生成及导入接口 |
| [HMI 导入校验](HMI_VALIDATION.md) | 预检查、回读验证、覆盖范围及限制 |
| [HMI 报警](HMI_ALARMS.md) | 经典 WinCC 离散报警处理 |
| [批量 XML 导入](BATCH_XML_IMPORT.md) | 批量备份、导入、编译与校验 |
| [在线监视](ONLINE_README.md) | 通信通道、采样记录和仿真测试入口 |
| [仿真](SIMULATION.md) | PLCSIM / PLCSIM Advanced 使用方式 |
| [工艺模板](PROCESS_LIBRARY.md) | 资料检索与参数化模板 |
| [性能说明](PERFORMANCE.md) | 批量处理、缓存及测量方式 |

专题文档包含特定版本的验证记录，使用时请同时核对当前工具参数与文档中的限制说明。

## 开发与测试

```text
src/TiaPortalSourceBridge/  主引擎、MCP 服务与 Web 后端
engines/v21/               V21 引擎及版本专属实现
shared/                    双引擎共用模块
web/                       浏览器看板
online-helper/             在线通信辅助程序
templates/                运行模板与工艺资料
tests/                    离线回归及安装流程测试
scripts/                  构建与辅助脚本
installer/                Windows 安装包定义
```

运行离线回归检查：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\Run-Checks.ps1
```

检查覆盖双引擎协议与路由、索引、HMI 校验、任务可靠性和浏览器生命周期，不连接 TIA 工程或写入 PLC。GitHub Actions 使用同一入口执行 Windows 检查。

欢迎通过 [Issues](https://github.com/qiap/TiaPortalSourceBridge/issues) 提交问题或功能建议，或通过 Pull Request 提交改进。问题报告请包含 TIA 版本、设备类型、复现步骤与脱敏日志；涉及工程写入的改动请说明备份、验证和失败处理方式。

## 使用边界

- 工程写入、工程保存和仿真下载是不同操作，请核对工具参数和目标工程。重要工程应先建立备份，并在副本上验证修改。
- 支持预览的接口优先使用 `dry_run`。例如经典 HMI 实际导入需要同时指定 `dry_run=false` 和 `confirm=true`。
- 工程操作超时后，先查询连接状态与延迟结果，避免直接重复提交写入。
- 在线读取与仿真控制有各自的适用范围；具体限制见在线监视和仿真文档。

## 许可

仓库目前尚未指定项目级开源许可证。第三方组件的许可声明保留在各自目录中；公开源码不代表已经授予统一的开源许可。

## PLCSIM Advanced V8

PLCSIM Advanced：V17 主引擎新增 V8.0 API 适配，按已安装 Runtime 版本加载接口，并处理 V8 的 CPU 注册重载与全局通信模式。接口与离线测试已通过；本机 Runtime 启动遇到 `0xC0000005`，实际运行验收仍待完成。详见 [仿真说明](SIMULATION.md)。

## HMI 页面编辑

本机看板新增 **HMI 页面编辑**：支持 V17 普通画面的文字、位置、尺寸和已有绑定编辑，提供差异校验、备份、编译与回读。报警及不支持的复合控件页面只读。详见 [使用说明与范围](docs/HMI-Page-Editor.md)。

页面预览使用读取快照，正式写入前统一核对最新工程；同一 HMI 最多 20 个页面可加入批量待办，共用一次编译与保存。单次调用复用依赖读取，无变化时跳过导入、编译与保存。写入界面显示执行阶段、等待时间及阶段耗时。线条修改同步起终点，导入前检查坐标和控件重名，保留备份与编译后回读核验。

## 虚拟机博图接入（SSH 隧道模式）

当 TIA Portal 安装在 Windows 虚拟机内、AI 与浏览器运行在宿主机时，建议工具在虚拟机内运行并通过 SSH 桥接：

1. **准备一次**：宿主机与虚拟机访问同一份 VMware 共享工具文件夹。新环境先在宿主机双击 `连接虚拟机看板.bat`，工具自动生成专用密钥；已准备好 `vm-access/host-identity.json` 的环境跳过这一步。私钥只保存在宿主机用户目录，不复制到共享目录。
2. **虚拟机端**：双击 `虚拟机启动看板.bat`。首次自动准备 OpenSSH、安装宿主机公钥，必要时仅弹出 Windows 管理员确认；以后直接启动看板，并更新当前 IP、用户名、端口、程序路径和 SSH 主机公钥。当前自动授权入口面向 Windows 本地管理员账户与默认 Windows OpenSSH 配置；特殊 SSH 配置会明确报错，不自动覆盖。
3. **宿主机端**：双击 `连接虚拟机看板.bat`，自动选择可达的虚拟机网卡、核对共享目录发布的 SSH 主机公钥、使用专用密钥建立隧道并打开浏览器。无需填写用户名、IP、端口、目录、密码或确认 SSH 指纹。已有本工具连接时复用并打开页面。虚拟机 IP 变化后重新运行虚拟机启动入口即可刷新。
4. **宿主机 AI**：连接时自动生成 `vm-access/tia-portal-vm.mcp.json`，包含已发现的实际程序路径和非交互密钥认证参数，可用于 AI 客户端的 MCP 配置。看板连接和 MCP 调用都通过 SSH，未将看板 HTTP 服务开放到外网。`-Configure`、显式 `-Target` 等高级参数保留原有手动模式。

两个启动窗口应保持运行；虚拟机需要通过 Windows Update 安装 OpenSSH 时仍需网络。共享目录的发布信息用于一次配对后的发现，不进行网络扫描或禁用 SSH 主机密钥校验。每份共享工具目录对应一台宿主机和一台虚拟机；多台机器请使用独立目录。


### MCP 文本结果摘要（源码更新）

MCP 的文本 `content` 现在附带目标、执行状态和结构化结果摘要，方便只读取文本的 Agent 使用。连接列表、仿真实例状态/IP、引用位置、逐项导入结果及备份路径会显示在摘要中；小段源码也可直接阅读。`structuredContent` 保持原样。

摘要最多约 16,000 字符，每个列表展示前 30 项，同项的简短属性合并为一行。`full` 中已经完整展示的源码/XML 不再重复；`compact` 的纯文本源码最多展示 12,000 字符，XML 片段上限为 2,400 字符，并受总预算限制。任何省略都会标记 `text_summary.truncated` 并在文本中提示，总预算不会直接截断半行列表结果。服务端原有 `offset`、`limit`、`next_offset` 与摘要截断是不同层次：摘要未显示的内容不代表不存在。完整 XML 仍通过 `full` 读取。

错误结果附带 `diagnostic`（类别、工具、目标、原始消息和建议）。超时不会自动重放操作，底层错误不能单凭文字推断为网卡故障或在线占用。摘要格式化失败也不会覆盖原始执行结果。此项不包含新的离线缓存回退、逻辑可读模型或仿真剧本功能。

### 4.11：混合语言读取、缓存证据与逻辑可读视图

`tia_read_blocks_batch` 一次读取 1–30 个程序块。默认 `read_mode: "live"`、`format: "auto"`：SCL/STL/DB 优先返回外部文本，LAD/GRAPH 返回 XML；文本导出失败时明确标出回退到同次实时 XML。每个对象分别返回成功或失败，不把未读到的对象当成已删除。总量上限 16 MiB，单份 XML 上限 8 MiB；大批次请拆分名称列表。

```json
{"block_names":["OP275_MES","OP275_Cylinder","OP275_自动流程"],"project_path":"C:\\Projects\\Line.ap17","plc_name":"完整 PLC 路径","format":"auto"}
```

`tia_read_block` 与 `tia_read_blocks_batch` 均支持 `read_mode: "cache"`，完全不连接博途、不重建或刷新索引。缓存读取必须指定完整 `plc_name`，可再用 `group_path`、`unit_name` 限定同名对象。结果带 `source_kind: "index_cache"`、`catalog_utc`、`indexed_utc`、对象指纹、`cache_state`、`freshness` 和 `write_baseline: false`。未采集、歧义、缺文件、指纹不符分别明确返回；已知过期快照仍可用于分析，但标为 `known_stale`。成功只说明请求的对象可读，不代表整个工程索引完整或与博途一致。**默认不会在实时读取失败后偷偷改用缓存，所有写入链路仍要求实时基准。**

两种工具均支持 `format: "readable"`。LAD 可读视图保留网络号、标题/注释、CompileUnit ID、UId、符号、触点常开/常闭、沿触点、S/R 线圈、调用实例/参数和端口连线。常规触点、线圈和显式 OR 节点可额外生成 AND/OR/NOT 条件树；调用、边沿、复杂地址和特殊拓扑只显示结构，不猜测等价布尔表达式。GRAPH 保留步骤、转换、属性与连接，条件中的 FlgNet 使用同一视图；未覆盖的动作、指令、开路或异常结构给出警告，未知节点附 `OPAQUE_NODE` 标记及局部原始 XML。

默认每块展示前 5 个网络，通过 `network_offset` / `network_limit`（1–20）继续读取。`view_summary` 明确显示分页、覆盖边界与警告，`view` 保存节点树，`readable_text` 供文本 Agent 直接阅读。这个版本是保留证据的结构化读取，不是完整反编译器，也不新增语义修改、自动导入排序或仿真剧本。

### 4.12：LAD / GRAPH 受限修改与双工程对比

MCP 新增以下入口（本次不新增 Web 页面）：

| 工具 | 用途 |
| --- | --- |
| `tia_read_logic_patch_plan` | 从实时源码生成触点修改预览；传 `plan_id` 可离线查询原方案及执行结果 |
| `tia_write_logic_patch` | 按审查过的 `plan_id` 应用修改，要求 `confirm: true` |
| `tia_compare_projects` | 指定两份工程、PLC 与程序组，分页比较块增删改、接口和网络结构 |

**受限修改支持 V16–V20 中可识别的 LAD/GRAPH 网络**：LAD 指定 `compile_unit_id`，GRAPH 指定 `sequence_index`（从 0 开始）和 `transition` 编号，再用 `contact_uid` 定位普通触点。`edit_json` 只能更换直接符号路径（`scope` 与 `components`）或设置 `negated`（true 为常闭）。例如 LAD：`{"compile_unit_id":"12","contact_uid":"23","scope":"GlobalVariable","components":["DB_Permit","Ready"],"negated":false}`；GRAPH 将 `compile_unit_id` 换为 `"sequence_index":0,"transition":"114"`。编号应取自本次实时读取，不能照抄示例。

方案绑定工程、PLC、完整块指纹与具体修改，保存在 `logs/logic-plans/`。应用时复核实时源码，全部备份后导入、编译并回读完整块。只有 `verified_unsaved` 表示这些检查通过，仍须用户检查逻辑；**不自动保存工程或下载 PLC**。失败保留现场与批次日志，不自动回滚或重试。同一方案有执行记录或未完成声明后不会再次写入；用 `tia_read_logic_patch_plan(plan_id)` 查询结果。过期方案须重新预览。共享操作数、沿触点、数组/切片/间接寻址、任意表达式替换、拓扑重排和动作改写均不在这一版范围内，V21 保持已有写入能力。

双工程对比必须提供 `project_path_a/plc_name_a` 与 `project_path_b/plc_name_b`，可用 `group_path_a/group_path_b` 选择不同组根，按**相对组路径＋同名块**配对；V21 可选 `unit_name_a/unit_name_b`。`read_mode` 默认为 `live`，也支持显式 `cache`。只有完整成功的实时目录才能确认对侧块不存在；未缓存、缓存目录缺失、读取失败或身份歧义分别标为未知/待核对，不能解释为删除。

默认每页 10 个块，`limit` 为 1–20，按 `next_offset` 继续并携带 `catalog_token`，目录变化则拒绝混页。每块最多展示 200 条结构化变化，`change_count/changes_truncated` 明示其余数量；需要完整证据时读取该块源码。返回源码指纹、采集时间、缓存新鲜度和解析警告。两侧是顺序读取，并非原子双工程快照；仅覆盖 PLC 程序块，不包含 HMI、硬件、调用影响面或下载重初始化判断。V21 现有目录接口缺少容器读取完整度证明，因此单侧缺失仍待核对；留空软件单元仅比较普通程序块，不混入其他单元。改名按增删处理；GRAPH 等未完整解析语义保留 XML 差异，不宣称逻辑等价。

### 4.13：仿真下载诊断与条件剧本

V17 的 `tia_simulation_download` 默认只读预检，检查受管本机实例身份、RUN/STOP 状态、Softbus 接口与目标唯一性、IP 唯一性、TIA `OnlineProvider.State` 和仿真编译设置。`blockers` 明确列出已确认的问题；查询失败会标为未知，不从 `InterfaceRemoved` 猜测为网卡故障。预检不编译、不改工程设置、不切换在线状态；`compile_status` 明示尚未编译。实际下载仍需 `dry_run:false`、`confirm:true`，编译成功后仅发起一次下载，不自动重试、不自动 RUN、不保存。阶段、编译诊断、下载决策、原始错误和是否已发起下载保存在 `simulation-downloads/`，未完成的 `download_intent` 记录须先核对实际状态。V21 保持原有能力。

`tia_simulation_peripheral_start` 的 `configuration_json` 现在支持 `steps` 条件剧本和 `watch` 变量列表，与原有外设延时反馈规则共用执行器：每步等待 `wait_until` 全部条件满足，延时 `delay_ms`，执行 `checks`，再写入本步 `writes`。条件类型为 `bool/dint/udint/time/real`，采用等值判断；写入限定 Bool。**本步 checks 在本步 writes 之前执行，写后反馈检查放在下一步。** 条件第一次满足后延时开始，即使随后条件下降也不会重新等待。每步 `timeout_ms` 包含等待及延时；总运行不能超过 `duration_ms`。

执行器逐次记录步号/监视值变化、条件满足、写入意图与成功结果。等待超时、检查失败、采样延迟超限或读写异常会停止后续动作，保留模拟变量现场，不自动复位或重放。`final_snapshot` / `failure_snapshot` 给出结束后逐变量读取的值、时间和错误，不能当作原子快照或恰好在故障发生瞬间的全部值。

通过 `tia_simulation_peripheral_status` 查询 `steps_completed/steps_total`、`max_tick_ms` 与最近 500 条事件；使用 `event_offset`、`event_limit`（1–500）和 `next_event_offset` 分页读取完整时间轴。报告与 `.jsonl` 保存在 `simulation-runs/`，包含结束/失败快照，重启后可查询。Web 外设规则页的配置框也可提交相同剧本并导出完整记录。详见 [仿真指南](SIMULATION.md)。

最小请求间隔为 20ms，Windows 调度及 SDK 调用不提供实时保证。单扫描脉冲应由 PLC 锁存或计数后再判断；不要以未采到推断脉冲没发生。最多 200 步、128 个合计变量、20,000 条事件；超限明确停止。已做模拟传输和离线回归，未据此宣称真实 OP275 工艺或硬件测试通过。另修复了双工程结果路径的多余换行，以及逻辑对比测试对启动目录的依赖。

### 4.14：Web 工作区接入读取、对比、修改与仿真闭环

- **工程索引与速查**：修复筛选变量错误与分类名不匹配；程序块可点击查看缓存/实时来源、可读网络/XML，按网络分页查看 Bool 条件树、未知节点原文和覆盖说明。缓存时间及过期状态始终可见。
- **变更审查与 Diff**：新增“双工程对比”和“受限触点修改 · V17”。对比支持两侧工程/PLC/组/软件单元、分页指纹、状态过滤及双侧证据；触点修改先实时预览前后接线及指纹，再人工确认应用，并保留方案编号及执行回查。
- **仿真驾驶舱**：新增受管下载预检、阻塞原因、仿真编译支持显式选项、确认装载和阶段凭证。外设表单支持监视变量、条件剧本步骤、写前检查及 Bool 激励，必须先预览当前配置再启动。显示步骤统计、事件分页及失败/结束快照。
- **任务中心**：可回查工作区修改、原生逻辑方案及下载凭证；未知结果醒目提示，不自动重放。新写入不自动保存工程、不下载物理 PLC、不改变 TIA 在线状态。
- **界面**：统一 13 个导航图标并分组，工具数量独立显示；退出收入“服务控制”菜单并保留确认。PID 移至悬停提示。I/O 增加来源徽章、空状态引导、主次按钮和无数据时隐藏分页；二级页签改为下划线样式，精简重复路径。

新接口位于同源的 /api/io/workbench/ 下，按固定操作调用白名单工具，要求工程/PLC 目标令牌；前端不能指定任意工具名。实际装载与触点写入使用已持久保存、绑定目标和指纹的预览，异步执行，在 logs/workbench/ 保留记录和提交标记。浏览器断开不代表任务未执行，应先回查。/api/io/peripheral/preview 绑定当前配置，配置或实例变更后必须重新预览。

### 4.15：I/O 阅读布局与白天 / 夜间主题

顶栏可切换白天和夜间主题，默认保留暗色，偏好保存在本浏览器。主题只改变控制台外观，不改变工程数据或 HMI 设计中的颜色。

I/O 页区分尚未读取、读取中、读取失败、读取为空和筛选无结果；没有数据时隐藏分页，首次进入只显示明确的读取引导。连接刷新与数据读取分开命名，工程快照来源和读取时间靠近标题，明确不含 PLC 在线实时值。本地缓存同时显示原有覆盖信息中的源码采集时间与本次加载时间；没有时间证据时显示“未提供”。

筛选采用固定标签与紧凑 Flex 布局，搜索框优先获得剩余宽度。表格填充剩余视口空间并内部滚动，固定列标题；可调整列宽、隐藏类型/来源列，复制变量名和地址。完整设备层级收进目标详情，相邻重名仅在展示时去重，复制与所有 API 参数保留原始身份。引用详情在宽屏侧边展开，窄屏放在表格下方；零显式引用显示“未发现引用”，不推断未使用。所有业务 API、读取范围和工程写入流程保持原有逻辑。

看板的博途工程检测改为后台执行，首次状态查询不再等待 Openness 检测完成。检测期间明确显示“检测中”，失败时保留上次列表并显示错误，不将失败当成工程已关闭。

## v4.15.2 连接与构建维护

- 同时运行宿主机看板和虚拟机看板：`连接虚拟机看板.bat -LocalPort 18981`。本地端口不会再被自动发现覆盖，虚拟机端口保持不变。
- 只在虚拟机内使用：`虚拟机启动看板.bat -LocalOnly -OpenBrowser`，无需宿主机配对，也不会配置 SSH。普通 `WebDashboard.cmd` 入口仍可使用。
- 多台虚拟机仍各用独立共享工具目录；宿主机按配对保存 known_hosts、隧道状态和日志，避免相互覆盖。
- 源码仓库清理：`powershell -ExecutionPolicy Bypass -File scripts/Clear-BuildCache.ps1` 默认只预览；加 `-Apply` 才删除超过 7 天的指定可再生成目录。可用 `-MinAgeDays` 调整保留天数。不会清理源码、配置、工程、恢复记录或发布安装包；链接目录和使用中的输出会跳过。
- `scripts/Build-Installer.ps1` 成功后默认删除本次暂存目录；需要排查打包内容时使用 `-KeepStaging`。
- 验证范围为双引擎离线回归、浏览器测试、ZIP 完整性及安装升级卸载测试；不代表已在每一种虚拟机账户、网卡或现场 PLC 环境完成联调。
