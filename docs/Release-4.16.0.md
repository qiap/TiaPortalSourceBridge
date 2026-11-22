# TIA Portal Source Bridge v4.16.0

现在可以从同一个 Web 看板或 MCP 入口选择 TIA Portal V16、V17、V18、V19、V20 和 V21 工程。V17、V18、V21 已用本机安装的原生 Openness 验证，其中 V17 实际发现了打开的工程。

## 各版本实测记录

| 版本 | 本次实际验证 |
| --- | --- |
| V16 | 六版本离线回归；使用 V17 安装附带的 V16 兼容 XSD 校验生成的 LAD 接口和网络。未安装 V16 原生环境。 |
| V17 | 加载原生 Openness，核实 API 类型与方法，运行只读发现并识别到 1 个已打开的工程；通过 LAD XSD 校验。 |
| V18 | 加载原生 Openness，核实 API 类型与方法，通过 LAD XSD 校验。只读发现成功，当时没有打开的 V18 工程。 |
| V19 | 离线验证工程路由、XML 生成与版本检查，以及缺少 SDK 时的诊断。未安装 V19 原生环境。 |
| V20 | 离线验证工程路由、XML 生成与版本检查，以及缺少 SDK 时的诊断。未安装 V20 原生环境。 |
| V21 | 加载原生模块化 Openness，核实 API 类型与方法，通过 LAD XSD 校验。只读发现成功，当时没有打开的 V21 工程。 |

六个版本均有离线用例。本次没有在真实工程中执行导入、编译、保存或 PLC 下载，各版本的工程写入和仿真仍需在目标环境验收。

## 使用范围

- V16–V20 的工程进程分别加载对应版本的 `Siemens.Engineering.dll`。V21 通过独立引擎加载 .NET Framework 模块化 API，使用 `Siemens.Engineering.Base.dll` 作为入口。常规 PLC、索引、I/O 和双工程对比根据目标工程版本路由，V21 保留官方交叉引用、Software Units 和 SimaticSD 等接口。
- LAD XML 按目标版本生成。GRAPH 内置 V17 和 V21 模板，V16、V18、V19、V20 需提供从同版工程导出的 `base_xml`。
- 多用户本地会话依赖 SDK 提供的 `LocalSessions`。V16 使用普通 `.ap16` 工程。经典 WinCC 画面编辑和外设仿真向导面向 V17。

## 下载与安装

- [Windows 便携 ZIP](https://github.com/qiap/TiaPortalSourceBridge/releases/download/v4.16.0/TiaPortalSourceBridge-4.16.0-Portable.zip) — 完整解压后运行 `WebDashboard.cmd`。
- [Windows EXE 安装包](https://github.com/qiap/TiaPortalSourceBridge/releases/download/v4.16.0/TiaPortalSourceBridge-4.16.0-Setup.exe) — 安装后从开始菜单打开。安装包未作代码签名。
- [SHA-256 校验清单](https://github.com/qiap/TiaPortalSourceBridge/releases/download/v4.16.0/SHA256SUMS.txt) — 可用 PowerShell 的 `Get-FileHash -Algorithm SHA256` 检查下载文件。

工程机需要安装目标版本的 TIA Portal、Openness 和工程使用的产品选件。可在安装或解压目录中执行以下命令查看服务所在机器已检测到的 API 路径。该命令无需打开工程。

```powershell
.\src\TiaPortalSourceBridge\bin\Release\net48\TiaPortalSourceBridge.exe list-apis
```
