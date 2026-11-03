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

| 方式 | 下载 | 使用方法 |
| --- | --- | --- |
| ZIP 免安装版 | [下载 Portable.zip](https://github.com/qiap/TiaPortalSourceBridge/releases/latest/download/TiaPortalSourceBridge-4.1.1-Portable.zip) | 完整解压后双击 `WebDashboard.cmd` 或 `LaunchWeb.vbs` |
| EXE 安装版 | [下载 Setup.exe](https://github.com/qiap/TiaPortalSourceBridge/releases/latest/download/TiaPortalSourceBridge-4.1.1-Setup.exe) | 运行安装程序，通过桌面或开始菜单打开 |

两种方式均包含 V17/V21 引擎、默认配置和运行模板。先在 TIA Portal 中打开工程，再在看板选择工程和 PLC；目标电脑需满足下面的运行环境要求。

[全部版本与发布说明](https://github.com/qiap/TiaPortalSourceBridge/releases) · [SHA-256 校验文件](https://github.com/qiap/TiaPortalSourceBridge/releases/latest/download/SHA256SUMS.txt)

GitHub 的 **Code → Download ZIP** 和 Release 中自动生成的 **Source code** 是源码压缩包；需要解压即用，请选择上面的 **Portable.zip**。EXE 安装包目前未做代码签名。

## 环境要求

- **系统**：Windows 10 / 11，64 位。
- **运行时**：.NET Framework 4.8。
- **工程操作**：对应版本的 TIA Portal 及 Openness 组件。
- **用户权限**：当前 Windows 用户加入 `Siemens TIA Openness` 本地用户组，变更后注销并重新登录。
- **源码构建与测试**：Windows PowerShell 5.1、.NET Framework C# 编译器及 Python 3。
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
