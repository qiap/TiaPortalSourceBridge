# TIA Portal Source Bridge 代码评审报告

评审时间：2026-11-01  
评审对象：`E:\WORK\TiaPortalSourceBridge`（C# / .NET Framework 4.8 / x64，51 个源文件，约 12.4k 行）  
评审方式：全量源码走查 + 静态证据 + 一次 Framework 编译器全量编译验证（未启动 TIA Portal，未启动看板服务）

> 说明：评审过程中 `Program.cs` 被并发修改（前后两次读取行数不一致）。由于项目没有版本控制，这种改动无法追溯，也是本报告 P0-2 的直接例证。文中行号以阅读时的文件内容为准，可能小幅漂移。

---

## 1. 总体判断

这个项目的**功能纵深**明显超过一般"脚本工具"：MCP 70 个工具、两阶段工程索引、GRAPH/LAD/HMI 生成、硬件组态、PLCSIM Advanced、S7 在线只读监视、经典 WinCC 报警读写，并且有备份、内容指纹、导入后回读、超时不重放等工程化设计。

但它同时保留了很强的**"个人工作台"特征**：没有版本控制、没有 CI、没有自动化测试，看板没有任何鉴权，进程级没有异常兜底，文档和版本号多处漂移。最大的风险**不在 PLC 侧**（在线写入被"仿真模式 + 确认 + 白名单 + 租期 + 审计 + 回读"层层约束，做得相当克制），而在**本机 Web 看板的攻击面**和**工程可维护性**。

| 维度 | 评分 | 说明 |
|---|---|---|
| 功能完整性 | 8/10 | 覆盖面很广，边界（不下载/不启停/只读在线）划分清楚 |
| 安全 | 5/10 | 写操作门禁设计好，但看板零鉴权 + CORS 通配 |
| 可靠性 | 5/10 | 有备份/回读/不重放，但 68 处静默吞异常、缓存不失效 |
| 可维护性 | 4/10 | 无 VCS、巨型文件、400 行 `if` 链、273 个一次性脚本 |
| 可测试性 | 2/10 | 仅 1 个 PowerShell 冒烟 + 反射式性能检查，无单测无 CI |
| 文档一致性 | 5/10 | 文档丰富但版本号与工具清单多处对不上 |

---

## 2. P0：应立即处理

### P0-1 看板无鉴权 + CORS 通配 → 任意网页可驱动工程写入、读取本机任意文件

证据：

- `Web/WebServer.cs:194-196`：所有响应都带 `Access-Control-Allow-Origin: *`
- `Web/WebServer.cs:1206-1215`：`POST /api/action/run` 把请求里的 `action` 原样交给 `ActionRunner`
- `Web/ActionRunner.cs:200-211`：非白名单分支把 `action` 直接拼进 `Arguments`，无引号、无校验
- `Web/WebServer.cs:1056`：`newPath = Path.Combine(sourceDir, sourceFile)`，`sourceFile` 来自查询串
- `Web/WebServer.cs:765-776`：`POST /api/shutdown` 直接 `Environment.Exit(0)`

可复现的攻击链（服务仅监听 localhost，攻击者=用户浏览的任意网页或本机低权限进程）：

1. **读取本机任意文件**：`GET http://127.0.0.1:18980/api/diff?file=C%3A%5CUsers%5Cme%5C.npmrc`
   `Path.Combine(base, "C:\\...")` 会返回绝对路径本身，`DiffHelper` 读到内容后按 diff 行返回；配上 `ACAO: *`，攻击页面可以直接读到结果（`.npmrc`、`config.json`、SSH 配置等）。
2. **驱动工程写入**：`POST /api/action/run`，body `{"action":"import"}`，用 `Content-Type: text/plain` 属于 CORS 简单请求，**不触发预检**，请求照样执行——对当前已打开的博途工程执行导入。
3. **参数注入**：`action` 未加引号，`{"action":"export \" --project \"C:\\other.ap17\" --"}` 这类输入可以改写子进程参数，把操作引到另一个工程。
4. **拒绝服务**：`/api/shutdown` 直接退出进程；`/api/diff` 可并发触发大数组分配（见 P2-3）。

缓解事实：只绑定 `localhost`/`127.0.0.1`；新版 Chrome 的 Private Network Access 会拦截部分公网→本地请求。这只是"部分缓解"，Firefox/Safari/旧版 Chrome 与本机进程不受限。

建议：

- 只监听 `127.0.0.1`，去掉 `Access-Control-Allow-Origin: *`（同源页面不需要它）。
- 启动时生成一次性随机 token，注入首页或走 `?token=`，所有 `/api/*` 校验。
- `action` 改成枚举白名单，`Arguments` 用逐个参数+显式引号构造。
- `/api/diff` 的 `file` 只接受 `SourceDirectory` 内的相对路径，并用 `Path.GetFullPath` + 前缀比较。
- 写操作类接口加 `Origin`/`Sec-Fetch-Site` 校验。

### P0-2 无版本控制、无 CI、无自动化测试

- 目录下**没有 `.git`**（`.gitignore` 存在但没有任何仓库）。整个工程的状态只能靠时间戳和 `*.exe.old`、`*.before-20261029` 这类手工副本判断。
- 仓库 1448 个文件 / 50 MB：根目录 131 个散落文件（`cu1_3.xml`、`db_op3000_iomapping.xml`、`all_104_alarms.txt`…，2.4 MB），`scripts/` 下 **273 个一次性 Python 脚本**，`artifacts/` 33 MB，`block-backups/` 181 个文件。
- 测试只有 `tests/SmokeMcp.ps1`（94 行，协议冒烟，写得不错）和 `tests/PerformanceChecks.cs`（靠反射调用 internal 成员）。二者都没有纳入任何自动流程；`LadBuilder`/`GraphBuilder`/`ContentIndexer`/`XmlBatch` 这些纯函数逻辑完全没有单测——而它们恰恰是最该被回归保护的。
- 后果：本次评审期间 `Program.cs` 就被并发改动且无法追溯；任何回归都只能靠"用户复现"。

建议：`git init` + 收紧 `.gitignore`（`artifacts/`、`*.exe.old`、`samples/` 用 LFS 或移出）；一次性脚本归档到 `tools/archive/`；CI 跑 `build.bat` + `SmokeMcp.ps1`；给 4 个纯函数模块补 xUnit（net48 可直接跑）。

---

## 3. P1：高优先级

### P1-1 静态文件服务缺规范化校验

`Web/WebServer.cs:240-241`：`path.TrimStart('/')` 后直接 `Path.Combine(webDir, relativePath)`，没有 `GetFullPath` 前缀比较。目前 `RawUrl` 未做 URL 解码、`HTTP.sys` 又会规范化 `..`，所以**暂不可利用**，但这是典型的纵深防御缺口——一旦有人加上 `Uri.UnescapeDataString` 或换 `HttpListener` 之外的宿主就会立刻变成任意文件读。建议统一走一个 `ResolveUnder(root, relative)` 帮助方法（`PlcImporter.cs:112-116`、`HmiImporter.cs:81-84` 已经有正确写法，应复用）。

### P1-2 进程级无异常兜底，看板可能"整体消失"

`Web/WebServer.cs:174` 用 `ThreadPool.QueueUserWorkItem` 分发请求，`ProcessRequest` 的 `catch`（`:219-226`）里**又调用一次 `SendJson`**。如果客户端在响应前断开，`SendJson` 里的 `OutputStream.Write` 会抛 `IOException`，该异常从 `catch` 块逃逸到线程池 → .NET Framework 下**未捕获的池线程异常会终止整个进程**。代码里没有任何 `AppDomain.UnhandledException` / `Application.ThreadException` 兜底，唯一的退出路径是 `:774` 的 `Environment.Exit(0)`（从请求线程里直接杀进程，可能打断正在进行的写操作）。

建议：`ProcessRequest` 整体包 try/catch，`SendJson` 内部再兜一层；所有 `QueueUserWorkItem` 回调都包 try/catch；加全局 `UnhandledException` 记录日志。

### P1-3 缓存不过期、不清空，切换工程后显示旧数据

- `Web/WebServer.cs:29, 379-383`：`_cachedPlcList` 一旦有值就永久返回，只有 `/api/project/switch`（`:960`）才清空。切换 PLC、TIA 重启、外部重新导出备份都不会刷新。
- `:585-586`：`_cpuHwCache` / `_cpuOrderCache` 以 `projectPath::plcName` 为键，**永不清除**，长跑进程内存只增不减。
- `GetPlcsFast` 直接把静态 `List` 引用返回给调用方，任何调用方误改都会污染缓存。

### P1-4 非交互路径里出现交互提示，导致"假成功"

`Openness/OpennessSession.cs:47-62`：项目版本与 API 版本不一致时会 `Console.Write` + `Console.ReadLine()` 询问是否升级。这条路径在 `web`/看板 action 子进程里没有可用的 stdin，`ReadLine()` 返回 `null` → 抛 `OperationCanceledException` → `Program.cs` 的 `catch (OperationCanceledException)` **返回 0**。`ActionRunner` 只按退出码判定，于是看板显示"任务执行成功"，实际什么都没做。

建议：交互提示只在 `interactive` 命令下启用；非交互模式一律走 `AllowProjectUpgrade` 判定并给出明确错误退出码。

### P1-5 68 处空/吞异常，故障表现为"数据缺失"而非"报错"

全仓 `catch {}` / 空 `catch(...)` 约 68 处（`WebServer.cs` 15、`BlockOperations.cs` 5、`ProjectWorker.cs` 6、`ReflectionApi.cs` 6…）。其中会影响结论正确性的包括：

- `Web/WebServer.cs:428, 473, 660, 715`：PLC/HMI 探测失败被完全吞掉 → 页面显示"没有 PLC"，而不是"探测失败"。
- `Open/OpennessSession.cs:309-312`：`CompileTarget` 吞掉单块编译异常，**静默退化为整 PLC 编译**，用户看到的是"变慢了"而不是"单块编译不支持"。
- `Mcp/ProjectIndexTools.cs:81`：索引失效标记失败只打一行 stderr，后续查询会基于过期索引给出"没有引用"的结论。

建议：至少把 `catch {}` 换成记录日志 + 在返回结构里带 `warnings`；对"看起来成功但实际降级"的路径（编译回退、探测回退）显式标记。

### P1-6 超时/取消语义不一致

- `Mcp/ProjectWorker.cs:108`：固定 600 s `pending.Wait(timeoutMs)`，而 MCP 客户端通常 60~120 s 就超时。超时后 worker 停在 `busy_outcome_unknown`（`:100`），**该项目后续所有调用都会被拒绝**，只能显式 `tia_connect` 重来。
- `Mcp/McpServer.cs:90`：`notifications/*` 一律忽略，包括 `notifications/cancelled`，长任务无法取消。
- `HttpListener` 侧没有任何请求超时/并发上限。

这是"宁可不重放也不重复写"的**正确取舍**，但缺少恢复通道：建议在 `tia_connection_status` 里给出"如何安全恢复"的一步式操作，并把 worker 超时做成可配置（与客户端超时对齐）。

### P1-7 版本号四套并存，二进制无版本信息

- `Mcp/McpServer.cs:99`：`serverInfo.version = "3.3.0"`
- `Web/WebServer.cs:849`：`"tool_version": "v3.2.0"`
- `PrintUsage` 里仍写 `(v2.2)`，且漏列 `online-host/online-client/mcp-host/mcp-client/mcp-project-worker` 等命令
- README 标注 v3.1 / v3.2 并存
- 实测 `bin\Release\net48\TiaPortalSourceBridge.exe` 的 `FileVersion` = **0.0.0.0**（无 `AssemblyVersion`/`AssemblyInformationalVersion`）

后果：无法从日志/二进制判断线上跑的是哪一版，与"备份/指纹/审计"的严谨程度不匹配。建议单一 `Directory.Build.props` 定义版本，`/api/status`、MCP `serverInfo`、`--help` 全部读取同一常量。

### P1-8 `confirm` 由调用方自签，等于"模型自批"

有 16 处 `RequireConfirmation`，实现正确（必须是 JSON `true`，字符串不算），删除前强制备份。但唯一门禁是**调用方自己传的参数**：`initialize` 的 instructions 里已经写了"导入和保存必须 confirm=true"，模型完全可以在被提示词注入后自行设置。对 `import/delete/save/restore/hardware/simulation` 这类高危操作，建议改成"预演 → 返回一次性 nonce → 二次调用带 nonce"，或按会话显式打开"允许写入"开关。

### P1-9 文档与实现漂移（可精确量化）

- README 宣称 **66 个工具**，实际 `Tool("tia_*")` 定义 **70 个**。
- 看板"工具字典"（`WebServer.cs:1279-1331`）列出 **40 条**，其中 **7 条工具根本不存在**：`tia_simulation_create`、`tia_simulation_stop`、`tia_simulation_read_symbol`、`tia_simulation_write_symbol`、`tia_simulation_auto_reset`、`tia_online_read_db`、`tia_online_diagnose_graph`。
- 同时有 **37 个真实工具**没进字典（`tia_connect`、`tia_save_project`、`tia_write_graph`、`tia_read_hmi_configuration`、`tia_online_*` 系列等）。
- README 称"核心会话与命令完全通用"，但 `Open/OpennessSession.cs` 之外的 `Openness/LegacyOp270Tools.cs`（329 行、5 个 OP260/OP270 产线专用命令）仍从 `Program.cs` 分发，且不出现在 `--help` 里。

建议：工具清单改为从 `ToolDefinitions()` 直接生成文档/字典（单一数据源），Legacy 命令单独做成 `legacy` 子命令并标注"仅 OP270 工程"。

---

## 4. P2：中优先级

1. **两套构建链互相漂移**：`.csproj` 声明 `LangVersion 7.3` 且用 `dotnet build`；本机**没有 .NET SDK**（`dotnet --version` 直接失败），`build.bat` 回退到 `Framework64 csc.exe`——实测该编译器 `/langversion:7.3` 报 `CS1617`（只支持到 C# 5）。好消息是我用该 csc 全量编译 51 个文件**通过（0 error 0 warning）**，说明现有代码是 C#5 干净的；坏消息是这套 csproj 在本机实际不可用，且两套配置内容不同（csproj 不含 `S7OnlineWorker`，模板/web 的拷贝方式也不同，`build.bat` 用 xcopy）。任何 C#6+ 语法都会在"能写、不能编译"之间产生陷阱。
2. **构建脚本有系统级副作用**：`build.bat:76` 每次构建都执行 `TrustThisTool.ps1`，即写 `HKLM\SOFTWARE\Siemens\Automation\Openness\...\Whitelist`。编译不该改注册表，建议拆成显式安装步骤。
3. **`DiffHelper` 的 LCS 有内存放大**：`DiffHelper.cs:111-123` 分配 `int[2001,2001]` ≈ 16 MB/请求 且 O(n·m)。配合 P0-1 的无鉴权，可并发打满内存。建议换 Myers/分块 diff，或对小文件才用矩阵。
4. **`ReadBody` 无长度上限**（`WebServer.cs:1245-1251`）：单请求可读入任意大小 body。
5. **`recent.json` 非原子写、无锁**（`Interactive/RecentSettings.cs:43-46`）：多线程 API 并发写会截断，`Load` 里 `catch { settings = null; }` 会静默重置用户偏好。
6. **备份/索引无保留策略**：`block-backups` 已 4.4 MB/181 文件，`artifacts` 33 MB，无清理、无归档。索引锁用 `FileShare.None`（`Indexing/ProjectIndexStore.cs:27-31`），第二个进程立即失败而非等待。
7. **批量 XML 导入不支持跨组同名块**：`Mcp/BlockOperations.cs:888-889` 只按 `block_name` 唯一匹配，`matches.Length != 1` 直接抛错。大工程里 `DB_Data`、`Main` 重名很常见，应优先用 `group_path` 精确定位。
8. **错误响应回传堆栈**：`WebServer.cs:224` 把 `ex.StackTrace` 返回给 HTTP 调用方。
9. **日志覆盖式写固定文件名**（`last-error.log` / `last-compile.log` / `last-mcp-host.log`）：上一次错误被下一次覆盖，排查历史问题没有依据。
10. **许可证风险**：`online-helper/vendor/` 声明 LGPL-3.0-or-later（`PROVENANCE.md`），但同目录放的是 `COPYING-GPL-3.0.txt`；这些 `.cs` 被 `build.bat:57` **静态编译进** `S7OnlineWorker.exe`，LGPL-3 对静态链接有"可重新链接"的要求。仓库自身也没有 `LICENSE`。
11. **模板目录出现重复嵌套**：`artifacts/performance-baseline/templates/templates/graph/v17.xml` 等，是 `xcopy /e /i` 重复叠加的残留；`templates/` 与 `online-bin/templates/` 双份维护。
12. **配置默认值比 config.json 更"激进"**：`AppConfig` 构造函数默认 `WithUserInterface=true`、`SaveProjectAfterImport=true`、`CompileBeforeExport=true`、`AllowProjectUpgrade=true`。配置里少写一个字段，就会静默变成"带界面 + 自动保存 + 自动升级"。
13. **UI Automation 报警桥接是架构性脆弱点**：`Alarms/NativeAutomation.cs` 手写 COM 封装，靠窗口标题、控件 `AutomationId`、列头文字定位（README 也承认"偶发超时"）。TIA 换语言包或小版本升级就会失效，且没有任何可回放的诊断快照。

---

## 5. 做得好的地方（不要改坏）

- **在线写入的三级门禁**：`target_mode=simulation` + `confirm_simulation_target=true` + 仅 `s7online` + 白名单 DB32021 字段 + 30 s（最多 300 s）心跳租期 + `online-records/writes.jsonl` 审计 + 写后回读。实体 PLC / TCP 一律只读。这是全项目最严谨的部分。
- **写操作默认"先备份、再写、再回读"**：`expected_hash` 指纹校验、导入后 `matched/different` 比对、`backup_mode=skip` 需显式声明。
- **多项目隔离**：每项目一个独立 STA 子进程（隔离 Siemens DLL 版本）、命名管道 ACL 拒绝 `NetworkSid` 只允许当前用户、单实例 Mutex、**超时不重放**（宁可让用户核实也不重复写入）。
- **导入路径校验**：`PlcImporter.cs:112-116`（`IsInside`）与 `HmiImporter.cs:81-84`（前缀比较）都做了包含性检查，比多数同类工具严谨。
- **XML 解析安全**：主流加载点统一 `DtdProcessing.Prohibit + XmlResolver=null`，没有 XXE 面；少数遗留 `new XmlDocument()` 只用于解析本工具自己生成的 XML。
- **MCP 协议实现正确**：`initialize`/`tools/list`/`tools/call`、id 回显、未知工具报错、`isError` 语义都对；`tests/SmokeMcp.ps1` 覆盖了 id 校验和 LAD 关键结构（PContact/SCoil/数组下标/局部变量）。

---

## 6. 建议的整改顺序

| 阶段 | 内容 |
|---|---|
| 第 1 周 | `git init` + 收敛 `.gitignore` + 移动样本/脚本；看板加 token 鉴权、去 `ACAO:*`、`action` 白名单、`/api/diff` 路径约束；补全局异常兜底 |
| 第 2 周 | 抽公共层（Diff、安全 XML 加载、路径规范化、JSON 原子写）；`Program.cs` 的 400 行 `if` 链改成命令表；版本号单一数据源 |
| 第 3~4 周 | 给 `LadBuilder`/`GraphBuilder`/`ContentIndexer`/`XmlBatch` 补单测；`SmokeMcp.ps1` 进 CI；`DiffHelper` 换算法；`ReadBody` 限长；工具清单从 `ToolDefinitions()` 生成 |
| 之后 | `confirm` 升级为"预演 + 一次性 nonce"；报警 UIA 桥接可插拔 + 诊断快照；备份保留策略；LGPL 合规评估 |

---

## 7. 证据索引

| 问题 | 位置 |
|---|---|
| CORS 通配 | `src/TiaPortalSourceBridge/Web/WebServer.cs:194-196` |
| 任意文件读取 | `src/TiaPortalSourceBridge/Web/WebServer.cs:1056`、`:1013-1072` |
| 任意 action 分发 | `src/TiaPortalSourceBridge/Web/WebServer.cs:1206-1215`、`Web/ActionRunner.cs:200-234` |
| 进程退出 | `src/TiaPortalSourceBridge/Web/WebServer.cs:765-776` |
| 静态文件穿越缺口 | `src/TiaPortalSourceBridge/Web/WebServer.cs:240-241` |
| 线程池无兜底 | `src/TiaPortalSourceBridge/Web/WebServer.cs:174, 219-226` |
| 缓存不失效 | `src/TiaPortalSourceBridge/Web/WebServer.cs:29, 379-383, 585-586` |
| 交互提示进非交互路径 | `src/TiaPortalSourceBridge/Openness/OpennessSession.cs:47-62`；`Program.cs` 的 `OperationCanceledException` 返回 0 |
| 静默降级编译 | `src/TiaPortalSourceBridge/Openness/OpennessSession.cs:301-315` |
| worker 600s 超时 | `src/TiaPortalSourceBridge/Mcp/ProjectWorker.cs:100, 108` |
| confirm 门禁 | `src/TiaPortalSourceBridge/Mcp/McpServer.cs:819-826` |
| 工具字典幽灵条目 | `src/TiaPortalSourceBridge/Web/WebServer.cs:1279-1331` |
| LCS 内存放大 | `src/TiaPortalSourceBridge/Web/DiffHelper.cs:111-123` |
| body 无上限 | `src/TiaPortalSourceBridge/Web/WebServer.cs:1245-1251` |
| 非原子写 recent.json | `src/TiaPortalSourceBridge/Interactive/RecentSettings.cs:43-46` |
| 跨组同名块受限 | `src/TiaPortalSourceBridge/Mcp/BlockOperations.cs:888-889` |
| 构建副作用 | `build.bat:76`、`TrustThisTool.ps1` |
| 静态链接 LGPL 代码 | `build.bat:52-59`、`online-helper/vendor/PROVENANCE.md` |
| 版本号不一致 | `Mcp/McpServer.cs:99`、`Web/WebServer.cs:849`、`Program.cs` `PrintUsage`、`README.md` |
