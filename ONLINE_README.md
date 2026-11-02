# OP270 在线监视与仿真测试（2.3.0）

当前版本提供独立的在线通信通道，不依赖 Openness 在线变量 API。原有工程连接窗口可以继续保持。新增窗口使用单独的本机命名管道；默认不连接、不写入任何 PLC。

## 已验证与尚待验证

- 已在本机普通 S7-PLCSIM V17、CPU1513 仿真上验证 S7ONLINE 连接、标准 DB 批量读取、GRAPH 状态及后台变化记录。
- S7 TCP 通道已用分段响应模拟服务器测试；尚未连接实体 PLC。实体/TCP 通道在此版本只读。
- 专用 DB32021、FC32021 测试覆盖和 FC32022 逐扫描记录已在当前独立 V17 工程中导入、添加调用并编译保存（2026-10-16，0错误，2个原有警告）。尚需操作者下载到 **PLCSIM** 后做运行验证。其他工程仍须按下文安装流程执行。
- 当前驱动使用标准 DB 绝对地址。它不支持直接按符号读取优化 DB；无需为了此版本关闭现有业务 DB 的优化访问。可改用独立标准诊断 DB，或另行开发 OPC UA / 符号驱动。

2026-10-16 运行验证补充：用户下载到 PLCSIM 后，PLC 工步记录、测试位写入回读、共享字节邻位保持、3秒无故障租期激活及自动关闭均已通过。验证结束恢复只读。详见 [ONLINE_VALIDATION_20261016.md](ONLINE_VALIDATION_20261016.md)。尚未进行实体连接或实际故障路径行为测试。

## 启动

运行 `build-online.ps1`，再运行 `StartOnlineMcp.bat` 并保持窗口打开。它不修改 PG/PC 接口、不占用 TCP 102 端口、不停止 Siemens 服务，也不停止仿真 CPU。

MCP 客户端配置：command 指向本目录 `online-bin/TiaPortalSourceBridge.exe` 的绝对路径，args 为 `["online-client"]`。这是新增的在线服务；原有工程服务仍用 `mcp-client`。

也可以直接调用 `online-bin/TiaPortalSourceBridge.exe mcp --config <本目录mcp-config.json绝对路径>`，使用同一进程中的完整工程与在线工具。工程 API、在线通道均按需连接。当前服务名与版本从 MCP initialize 返回。

辅助调用脚本 `Invoke-Online.ps1` 供本机自动化使用，`-Tool` 是工具名，`-ArgumentsFile` 是 JSON 参数文件。添加 `-Engineering` 则调用原有工程服务管道。所有协议消息是 UTF-8 JSON 行。

## 接口

| 接口 | 用途 |
| --- | --- |
| tia_online_connect | 选择 s7online 或 s7tcp、明确 IP/rack/slot、载入地址 profile；默认只读 |
| tia_online_status | 目标、连接会话、最近读取时间、错误、采样及测试租期 |
| tia_online_read | 按 profile 名或标准 DB 绝对地址批量读取，逐值附质量与 UTC 时间 |
| tia_online_graph_status | OP270 活动步、模式、错误、初始化相关支路与诊断提示 |
| tia_online_watch_start/read/stop | 后台记录采样变化；JSONL 文件持久保存 |
| tia_online_trace_read | 读取已安装的 PLC 环形记录，验证签名、版本与一致性 |
| tia_online_write_test | 仅写确认过的仿真测试 DB 白名单 Bool 字段，审计并回读 |
| tia_online_disconnect | 停止记录和测试租期，关闭在线通信；不停止 CPU |

普通 PLCSIM 当前连接示例：

```json
{"transport":"s7online","ip":"192.168.6.90","rack":0,"slot":1,"target_mode":"simulation"}
```

读取示例：

```json
{"names":["graph.step","graph.active","graph.auto","io.lift_extended","io.leak_ok"]}
```

实体 PLC 的 s7tcp 使用 CPU IPv4 与 102 端口。CPU 必须允许该通信方式，变量所在 DB 必须可按绝对地址访问。若权限不符，工具返回明确错误，不会自行修改 CPU 安全设置或下载硬件配置。连接握手成功不等于变量读取权限验证成功。

## 仿真写入与故障注入

写入仅允许本机 s7online、`target_mode=simulation`，并显式设置 `enable_simulation_writes=true`、`confirm_simulation_target=true`。目标分类来自操作者确认，工具**不能自动证明目标一定是仿真 CPU**。连接实体时一律使用只读模式，不要错误标记为仿真。

写入只接受 profile 中 `TestWritable=true` 的 DB32021 字节 4..7 布尔字段，并先校验 DBD0 的 `4F503237` 签名。不能直接写实际输入、阀输出、安全、GRAPH 内部步或模式。profile 映射变化后必须复核下载版本。写入前持久记录意图；写入后回读，回读不一致不重试。通信失败时报告状态可能已改变。

测试块在 `plc-online/OP270_TestAndTrace_V17.scl`。测试覆盖在原仿真辅助块之后、业务逻辑之前调用；只支持托盘不水平、顶升不到位、气密 NG，以及初始化单脉冲。原仿真辅助块保持正常信号的生成权，测试层对明确选中的条件做覆盖。

安装流程（**只用于当前独立仿真工程**）：

1. 在 TIA 点击“转至离线”，保留工程和原 MCP 服务窗口。
2. 执行 `plc-online/Install-OP270Test.ps1 -ExpectedProjectPath <当前.ap17完整路径> -ConfirmSimulationProject`。
3. 脚本核对工程、块号和仿真调用，先备份 Main，再导入、编译、固定块号、添加调用并保存。不自动下载；导入未通过时不会继续添加调用。
4. 操作者在 TIA 下载到 PLCSIM。确认 DB32021 签名、SchemaVersion=1、Capacity=256 后再启用测试。

先设置故障字段，再写 `test.enable=true`，可给出 `lease_seconds`（默认30，最大300）。MCP 在租期内每秒维护心跳；正常到期/断开时尝试将 enable 复位。服务异常退出时，PLC 的5秒看门狗使测试覆盖失效。到期只取消测试覆盖，**不会停止 CPU，也不会停止原正常循环**。

`test.initialize` 由测试层转成一扫描初始化脉冲，不能持续强制顺控器初始化。`test.heartbeat` 由有期限的心跳任务管理，拒绝直接通过写工具修改。实体通道没有写入和故障注入能力。

**2026-10-16 分支测试发现的限制：** 当前已部署的测试覆盖调用位于 OP270_BasicSign 之前，FC5_ModeStaus 后续会改写 Mode.AutoInitial，因此自动初始化测试入口未能生效。已将 profile 中 test.initialize 改为只读，当前需使用 GRAPH 面板“初始化”恢复。修复需要离线调整测试调用位置并重新下载；尚未修改正在运行的 PLC 程序。顶升不到位的等待/反馈恢复、托盘不水平进入S29及等待人工确认已实测。气密NG和超时报警尚未验证。

## 工步记录的准确含义

主机采样记录保留值、质量、时间；至少50ms的采样仍会漏掉短步骤，不能用采样中不存在某步来证明程序跳过该步。

PLC 环形记录每次调用结束后的可见步变化：记录递增序号、扫描计数、GRAPH步号、HMI工步；容量256条。读取用前后序列号检查一致性，报告已覆盖的旧记录数量。一次 GRAPH 调用内部可能发生多个瞬时转换，这些内部步骤仍可能不出现在记录中；并行顺控也不能仅靠 S_NO 表示全部活动步。

## 维护与测试

- `tests/Test-Online.ps1`：协议错误、大小端、位写、TCP分片/COTP分段、地址边界、profile白名单与硬件写保护测试。
- `profiles/op270.json`：来自2026-10-16在线截图的标准 DB 地址。DB结构更改或重新下载后必须核对，名字是本地别名，不是在线符号解析。
- `online-records/*.jsonl`：变化记录与写入审计；包含地址及生产运行值，保存在本机。
- `online-helper/vendor`：NetToPLCsim LGPL-3.0-or-later 原始消息通道源码、许可证与来源；不分发 Siemens DLL。

上游参考：

- https://nettoplcsim.sourceforge.net/history.html （S7ONLINE 与 TIA S7-1200/1500 支持）
- https://svn.code.sf.net/p/nettoplcsim/code/trunk/nettoplcsim-s7o/src/ （上游源码）
- https://developer.siemens.com/s7-plcsim-advanced/overview.html （后续 Advanced API 路线；当前实现不要求 Advanced）
