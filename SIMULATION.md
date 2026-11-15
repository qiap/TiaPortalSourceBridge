# 本机仿真自动化（2.8）

## 默认版本选择修正

用户使用普通 S7-PLCSIM V17。通用脚本 `Start-AutoResetSimulation.ps1` 已改为默认 `-Backend Standard`，从 `.ap17` 匹配并启动普通 V17，不再自动改用 Advanced。`Start-StandardSimulation.ps1` 使用对应版本的安装快捷方式或安装目录；不会使用其他版本兜底。目前普通版路线仅验收启动，自动装载、RUN 和功能测试尚未通过。进程启动成功不等于测试通过。

下面的 Advanced 专用流程必须显式指定 `-Backend Advanced`。此前 Advanced 装载返回“连接到模块 PLC_CPU1513 失败”，还没有通过端到端运行验收。

新增 PLCSIM Advanced 官方 API 通道，可自动启动本机仿真 CPU、显示控制面板、通过 Openness 装载、切到 RUN、按符号读取优化 DB，并运行自动复位功能测试。

普通 S7-PLCSIM 的现有 S7ONLINE 监视接口继续保留；它不等于 Advanced 自动控制接口。主引擎已增加 Advanced V8.0 适配，同时保留旧版 API 路线。API 优先匹配本机 Runtime 的产品主版本，不再优先选择遗留的 V5 DLL；未知的新主版本不会自动加载。

## Advanced V8.0 适配（2026-11-10）

- 明确调用 `RegisterInstance(ECPUType, string)`，避免 V8 的订货号重载误收 CPU 名称。
- V8 的实例 `CommunicationInterface` 为只读，改用管理器 `NetworkMode` 选择 Softbus。已有其他注册实例且模式不是 Softbus 时拒绝修改全局模式；不会停止或接管已有实例。
- 保留受管实例名称、ID、工程路径核验、Softbus 下载限制、符号读取、测试输入白名单及外设反馈测试。
- 状态查询显示 API 路径、版本和 `api_available`，区分接口已安装与 Runtime 已就绪。查询不会启动管理器；明确启动实例时才按需启动管理器。原生初始化异常立即返回，不循环重试。
- 已通过本机 V5/V8 DLL 的接口签名核验、反射调用替身测试和外设模型回归。实际 V8 启动检查遇到本机西门子 `Siemens.Simatic.Simulation.Runtime.Manager.exe` 崩溃，Windows 事件记录异常 `0xC0000005`；尚未创建测试 CPU，也没有下载 PLC 程序。因此尚未完成本机 V8 的启动、下载、RUN、实际变量读写端到端验收。此错误原因尚未查明，不能据此判断为许可证问题。

离线检查：`tests/Test-AdvancedRuntime.ps1`。增加 `-ContractDll <DLL完整路径>` 只检查已安装 SDK 的签名，不运行仿真。增加 `-Live` 会创建独立空白测试实例、尝试 RUN/STOP 并清理该实例；不会连接或下载工程，也不代表业务程序功能测试通过。实测日志位于本次工作目录 `.work/plcsim-v8/`。

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

`tests/Test-AdvancedRuntime.ps1` 检查 API 选择、V8 重载、全局通信模式保护和符号读写分派；`tests/Test-Peripheral.ps1` 用替身检查受管外设反馈、运行状态、目标隔离、失败停止及并发保护。

本机实际启动和测试记录见 `tests/live-results/simulation-2.8/` 与 `VALIDATION_2.8.md`。不能仅根据模拟器启动成功就把程序装载和功能测试标记为通过。

## 升级授权

Openness 白名单绑定工具 EXE 的路径、修改时间及文件哈希。更新工具后，用管理员权限运行 `TrustThisTool.bat` 更新当前正式版本的授权，再恢复工程连接；日常 PLC 程序和测试脚本修改不重新编译桥接 EXE。

官方依据：

- [PLCSIM Advanced V5 功能手册](https://cache.industry.siemens.com/dl/files/110/109813110/att_1119149/v1/s7-plcsim_advanced_function_manual_en-US_en-US.pdf)
- [Openness 下载 API](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-the-data-of-a-plc-device/functions-for-downloading-data-to-plc-device/downloading-to-plc-devices)
## 条件触发剧本（V17，4.13 起）

先用 `tia_simulation_download` 默认预检查看 `blockers`；预检不编译、切在线/离线或下载。实际下载需单独传 `dry_run:false, confirm:true`，失败时按诊断和持久凭证核对，不直接重试。下载后由调用方明确把受管实例设为 RUN，再调用 `tia_simulation_peripheral_start`。

下例为剧本形状示例，变量名与步号必须替换为所测工程的实际声明；并非已经验证的 OP275 配置。通过 MCP 的 `configuration_json` 传入序列化 JSON，或粘贴到 Web 外设规则页配置框：

```json
{
  "duration_ms": 20000,
  "interval_ms": 20,
  "max_lateness_ms": 500,
  "watch": [{"tag": "DB_Test.AutoStep", "type": "dint"}],
  "rules": [],
  "steps": [
    {
      "name": "首次检测返回 NG",
      "timeout_ms": 5000,
      "delay_ms": 200,
      "wait_until": [{"tag": "DB_Test.AutoStep", "type": "dint", "equals": 15}],
      "writes": [{"tag": "DB_Test.FirstNG", "value": true}]
    },
    {
      "name": "重测返回 NG",
      "timeout_ms": 5000,
      "wait_until": [{"tag": "DB_Test.AutoStep", "type": "dint", "equals": 25}],
      "writes": [{"tag": "DB_Test.SecondNG", "value": true}]
    },
    {
      "name": "确认停站",
      "timeout_ms": 5000,
      "wait_until": [{"tag": "DB_Test.AutoStep", "type": "dint", "equals": 99}],
      "checks": [{"tag": "DB_Test.Stopped", "type": "bool", "equals": true}]
    }
  ]
}
```

条件是等值且全部满足，不执行字符串表达式。首次满足后开始延时，不要求条件一直为真；若动作前必须再次核对条件，在 `checks` 中重复列出。`checks` 先于同一步的 `writes`；要等写入后反馈，另起下一步。时间数值为整数毫秒，Bool 使用 JSON 布尔值；数值条件不接受数字字符串。未知配置字段会拒绝，避免把拼错的条件静默忽略。

启动返回 `id`；向 `tia_simulation_peripheral_status` 传原工程、实例和 `run_id`。结果含步骤进度、每次变量变化、写入前意图、成功写入、失败原因和逐变量现场读取。默认最近 500 条；从 `event_offset:0,event_limit:200` 开始，按 `next_event_offset` 继续到 null。Web 的导出完整记录包含结束快照。未完成任务重启后标为 unknown，不自动补写。

时序边界：20ms 只是最小请求采样间隔；SDK 阻塞和操作系统调度可能导致漏采。短脉冲须在 PLC 锁存/计数后观察。超出 `max_lateness_ms` 时停止后续动作，不补写过期刺激。结束快照为故障后逐点读取，含每点时间和错误，不是原子故障瞬间截图。所有步骤完成后仍运行既定外设规则和监视到总时长结束；需要提前终止可调用原有 stop。
