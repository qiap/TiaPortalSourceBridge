# 本机仿真自动化（2.8）

## 默认版本选择修正

用户使用普通 S7-PLCSIM V17。通用脚本 `Start-AutoResetSimulation.ps1` 已改为默认 `-Backend Standard`，从 `.ap17` 匹配并启动普通 V17，不再自动改用 Advanced。`Start-StandardSimulation.ps1` 使用对应版本的安装快捷方式或安装目录；不会使用其他版本兜底。目前普通版路线仅验收启动，自动装载、RUN 和功能测试尚未通过。进程启动成功不等于测试通过。

下面的 Advanced 专用流程必须显式指定 `-Backend Advanced`。此前 Advanced 装载返回“连接到模块 PLC_CPU1513 失败”，还没有通过端到端运行验收。

新增 PLCSIM Advanced 官方 API 通道，可自动启动本机仿真 CPU、显示控制面板、通过 Openness 装载、切到 RUN、按符号读取优化 DB，并运行自动复位功能测试。

普通 S7-PLCSIM 的现有 S7ONLINE 监视接口继续保留；它不等于 Advanced 自动控制接口。本轮在本机 Advanced V5.0 API 上开发；API V8 合同变化，当前明确不自动加载 V8。

## 一次执行自动复位测试

目标工程先有 `FB_AutoReset_Test`。首次安装独立测试 DB 和循环 OB 时：

```powershell
.\Start-AutoResetSimulation.ps1 -Backend Advanced -ProjectPath 'E:\Projects\Test\Test.ap17' -InstallTestHarness -ShowSimulator
```

已有本轮测试块时，可省略 `-InstallTestHarness`。多个 PLC 时显式传 `-PlcName`。脚本默认创建独立的 `BridgeSim_...` 实例；要复用本工具创建并绑定同一工程的实例，传 `-InstanceName`。

过程依次确认目标 PLC 和功能块、可选安装测试调用、启动实例、检查 PLCSIM Softbus 路由、编译并装载、切到 RUN、执行测试并保存结果。每步参数与原始响应保存到 `simulation-results/workflow-*`。任何失败停止流程，不自动重放装载。重新执行流程会再次装载，因此超时后应先查询仿真和项目状态。

脚本不保存离线项目。测试安装新增：

- `Bridge_FunctionTests/Simulation/DB_BridgeSim_AutoReset`：独立输入与结果区，签名 `42525332`，测试延时500ms。
- `DB_BridgeSim_AutoResetInstance`：功能块实例 DB。
- `OB_BridgeSim_AutoReset`：独立 ProgramCycle OB；不改现有 Main。

测试仅改变该 DB 的 Enable、Trigger、ManualReset、ClearCount。结束时关闭使能、释放测试输入，保留仿真 CPU 的 RUN 状态和测试报告。没有运行测试时 Enable 默认 FALSE。

## MCP 工具

| 工具 | 用途 |
|---|---|
| `tia_simulation_status` | 检测本机 Advanced API 和只读列举实例 |
| `tia_simulation_start` | 创建、绑定工程并启动本机 Softbus CPU；`show_ui=true` 显示控制面板 |
| `tia_simulation_download` | 默认 `dry_run=true` 返回路由；实际装载须 `dry_run=false, confirm=true` |
| `tia_simulation_control` | 本工具受管实例的 run、stop、power_off |
| `tia_simulation_read` | 按符号读取 bool、udint、dint、time、real，支持优化 DB |
| `tia_simulation_write_test` | 仅写签名匹配的自动复位测试 DB 的四个布尔输入 |
| `tia_simulation_test_autoreset` | 执行测试并保存逐项结果 |

除状态查询外，传 `project_path` 和明确 `instance_name`（创建新实例时名称可省略）。实例凭据保存在 `simulation-sessions`，同时核对项目路径、名称、实例 ID 和 Softbus 模式。不能接管未知实例。仿真控制和符号读写由常驻工程入口管理；不要用执行完即退出的临时 stdio 进程代替常驻服务管理实例生命周期。

装载只接受精确 PLCSIM Softbus 接口，不回退为物理网卡或 PLCSIM TCP/IP 虚拟网卡；目标 IP 来自受管实例 API，装载前再次核对。如果其他运行中的本机实例有同一 IP，拒绝装载。首次需要仿真编译时，显式 `enable_simulation_support=true` 可打开离线工程的仿真编译支持，不保存。

支持已实现的装载决策：停止目标模块、装载全部块、覆盖系统数据、一致性装载、初始化内存、接受仿真硬件差异等。遇到密码、保护等级变更或其他未适配决策时停止，返回具体类型。装载前编译有错误则停止；装载不自动 RUN，运行是单独步骤。

## 验证边界

`tests/Test-Simulation.ps1` 用替身检查身份隔离、未知实例拒绝、非 Softbus 拒绝、重复地址拒绝、签名和输入白名单、状态操作及启动不自动重放。

本机实际启动和测试记录见 `tests/live-results/simulation-2.8/` 与 `VALIDATION_2.8.md`。不能仅根据模拟器启动成功就把程序装载和功能测试标记为通过。

## 升级授权

Openness 白名单绑定工具 EXE 的路径、修改时间及文件哈希。更新工具后，用管理员权限运行 `TrustThisTool.bat` 更新当前正式版本的授权，再恢复工程连接；日常 PLC 程序和测试脚本修改不重新编译桥接 EXE。

官方依据：

- [PLCSIM Advanced V5 功能手册](https://cache.industry.siemens.com/dl/files/110/109813110/att_1119149/v1/s7-plcsim_advanced_function_manual_en-US_en-US.pdf)
- [Openness 下载 API](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-the-data-of-a-plc-device/functions-for-downloading-data-to-plc-device/downloading-to-plc-devices)
