# TIA Portal Source Bridge

**简体中文** | [English](README.en.md)

[![Offline regression](https://github.com/qiap/TiaPortalSourceBridge/actions/workflows/checks.yml/badge.svg)](https://github.com/qiap/TiaPortalSourceBridge/actions/workflows/checks.yml)

连接 **Siemens TIA Portal** 与 **AI / 自动化工作流** 的本地桥接工具。

通过 TIA Portal Openness，提供 PLC 源码读写、工程索引、编译诊断、HMI 与硬件组态等能力。既可以在浏览器看板中操作，也可以通过 MCP（Model Context Protocol）接入 AI 客户端。

目前采用统一入口与 V17 / V21 双引擎架构，运行于 Windows。

## 功能

| 模块 | 主要能力 |
| --- | --- |
| Web 看板 | 查看工程与连接状态、执行工程任务、审查备份差异、检索索引、查看日志 |
| 程序体检 | 只读检查显式地址重叠、多处写入、模板参数缺线和实例复用；查看 OB 调用路径、上下游调用及定位证据，导出 Excel 报告 |
| 气缸执行记录 | 持久保存生成、核对和后续处理记录；显示逐块结果、编译警告、备份与保存状态；核对后继续或恢复失败任务 |
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

当前仓库源码包含待发布的改进。请在 Releases 页面选择已发布版本的资产；安装包实际包含的功能以该版本发布说明为准。

| 方式 | 下载 | 使用方法 |
| --- | --- | --- |
| ZIP 免安装版 | [在 Releases 中选择 Portable.zip](https://github.com/qiap/TiaPortalSourceBridge/releases) | 完整解压后双击 `WebDashboard.cmd` 或 `LaunchWeb.vbs` |
| EXE 安装版 | [在 Releases 中选择 Setup.exe](https://github.com/qiap/TiaPortalSourceBridge/releases) | 运行安装程序，通过桌面或开始菜单打开 |

两种方式均包含 V17/V21 引擎、默认配置和运行模板。先在 TIA Portal 中打开工程，再在看板选择工程和 PLC；目标电脑需满足下面的运行环境要求。

[全部版本与发布说明](https://github.com/qiap/TiaPortalSourceBridge/releases) · 同一版本资产中的 `SHA256SUMS.txt` 提供 ZIP 和 EXE 校验值。

GitHub 的 **Code → Download ZIP** 和 Release 中自动生成的 **Source code** 是源码压缩包；需要解压即用，请选择上面的 **Portable.zip**。EXE 安装包目前未做代码签名。

刷新、切换或关闭浏览器标签页不会停止后台服务。需要退出时，点击看板右上角的“退出服务”；正在执行的工程任务会先完成。

### 执行记录与失败恢复

控制中心和运行日志中的“工程变更记录 · 气缸”可以重新打开历史任务。任务结束后刷新页面、重新打开浏览器或重启工具，记录仍保存在本机；普通调用日志最近 100 条的限制不影响这些工程变更记录。

气缸操作失败时保留现场，不自动回滚。先点击“核对当前工程”，工具只读检查后台是否仍在执行、程序与 I/O 声明是否变化，以及本次备份是否完整。核对通过后可查看“继续”或“恢复”方案，再明确确认执行；保存操作会保存整个当前工程，包括其他未保存修改。继续仅补未完成步骤，恢复会先备份当前现场，并处理本次变化涉及的程序块和关联实例 DB。

页面断线或请求超时只会查询原任务，不会再次提交写入。无法确认旧后台已结束、发现后续修改或备份证据不足时，自动继续与恢复保持禁用。旧版任务缺少完整恢复证据时仍可查看记录和备份，但需要人工核对处理。本功能操作博途中的离线工程，不下载到 PLC。

### 气缸模板与批量生成

在“气缸生成”中读取工程、选择已有实例，并核对各参数用途后，可以将模板规则保存到当前工程。模板保存在工具目录的 `project-templates/` 中；新增其他气缸不会使原样板规则失效，样板接线或功能块接口改变时则需要重新核对。模板规则可导出、导入，换工程使用时必须按目标工程重新确认接线和点位。

填写气缸名称、程序实例名、数组索引并选择空闲 I/O 后，可以加入批量清单。每批最多 20 个同模板气缸，检查重复名称、实例、索引和物理点位后，将修改合并为每个程序块一次写入，再统一编译、回读和保存。失败的批次同样保留完整执行记录。模板文件与工程执行记录均为本机数据，不随源码上传。

### 程序体检

选择工程和 PLC，打开“程序体检”。可以读取本地源码缓存后检查，也可以从博途重新读取后检查；已有完整读取快照时可直接检查。报告显示源码来源、时间、读取范围，以及程序块、网络和变量等定位证据，并可按规则或名称筛选。Excel 导出包含本次报告全部发现与覆盖说明，不受页面筛选影响。

检查变量表中受支持的显式 I/Q/M 地址、LAD 线圈与调用参数及部分 STL 写指令。模板参数检查只使用当前工程已确认且仍匹配的气缸模板。地址别名和多处写入可能是有意设计，结果需要结合调用顺序和设备用途核对。

“调用关系”按当前快照中的全部 OB 建立入口。选择程序块可查看一条已识别的 OB 调用路径，以及上游、下游调用的位置和实例；未解析的调用也保留在清单中。明确识别的同一背景 DB 或同宿主多重实例被多处调用时，会给出核对提示。不同软件单元、不同宿主的局部实例分别处理。“未发现 OB 路径”只说明当前识别范围内没有路径，不代表程序从未运行或可以删除。存在未读取源码、无法解析的目标或结果上限时，报告会标出覆盖缺口。Excel 报告增加“调用关系”和“OB调用路径”工作表。

完整 SCL/GRAPH 控制流、Temp 初始化、动态寻址、硬件地址、DB 布局及 HMI/外部写入暂未覆盖；无法核实身份的定时器与沿触发实例不作共用判断。未发现问题不代表逻辑安全。分析不修改、编译、保存工程或下载 PLC。

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

主要入口统一管理 V17 与 V21 工程。`.ap21` / `.als21` 工程由 `engines/v21` 子进程处理，无需单独启动内部引擎。

两个版本的功能范围并不完全一致：V21 专属接口及仿真能力以工具说明为准。其他 TIA 版本、设备和 HMI 类型需要分别验证，不应视为已经完整兼容。离线测试通过也不代表目标工程已经通过 TIA 编译或运行验证。

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

数据来自 PLC 已声明变量表，不含未分配变量的硬件通道或在线实时值。V17 显示索引提取的显式引用，V21 使用官方交叉引用接口；间接寻址、跨软件单元绑定等不能保证完整覆盖。零条引用不表示变量未使用，具体范围及警告会随 Excel 一并导出。

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
