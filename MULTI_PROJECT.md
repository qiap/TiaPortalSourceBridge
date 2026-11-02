# 多项目连接（2.6）

同一个 MCP 服务可以连接多个已打开的博途项目。每个项目对应独立的后台进程、Openness 会话、程序块和 UDT 缓存。不同版本的 Siemens DLL 不会装入同一个项目后台进程；网关本身不加载 Siemens DLL。

服务启动后先监听客户端，不等待首个项目的 Openness 授权。拖入 `.apXX` 路径启动时，该路径记为默认目标；调用 `tia_connect` 或第一次工程操作才建立实际连接。在此之前状态为未连接，`preferred_project_path` 返回默认目标。默认目标不会覆盖管理多个项目时必须明确指定路径的规则。

## 使用方式

先在相应版本的博途中打开工程，然后依次调用：

```text
tia_connect(project_path="E:\Projects\LineA\LineA.ap17")
tia_connect(project_path="E:\Projects\LineB\LineB.ap21")
tia_list_connections()
tia_list_plcs(project_path="E:\Projects\LineA\LineA.ap17")
tia_list_plcs(project_path="E:\Projects\LineB\LineB.ap21")
```

第二次连接保留第一个项目的连接。读写、编译、保存、导出、恢复和生成 LAD/GRAPH/HMI 时使用同样的 `project_path` 选择目标。只管理一个项目时仍可省略路径；没有连接时，工程操作可选择配置版本中唯一打开的工程。纯生成操作不连接博途：未管理项目时使用配置版本，明确提供路径时按 `.apXX` 后缀选择版本。

**管理多个项目后，省略路径的工程操作或依赖版本的生成操作会报错。** 切换调用目标只需换 `project_path`，不必断开其他项目。生成后导入仍必须明确指定正确目标。与项目版本无关的 `tia_patch_graph` 保持原用法。

```text
tia_connection_status(project_path="E:\Projects\LineA\LineA.ap17")
tia_disconnect(project_path="E:\Projects\LineA\LineA.ap17")
tia_list_connections()
tia_disconnect_all()
```

状态查询不会连接新项目。`tia_connection_status()` 不传路径时返回全部项目状态；只管理一个项目时还保留顶层 `connected/project_path/portal_version` 字段。`projects[]` 提供每个项目的版本、后台进程 ID 和状态。

`tia_disconnect` 只释放指定连接，其他项目仍保持连接；`tia_disconnect_all` 释放全部连接。两者均不保存、不关闭用户的博途项目。有后台请求仍在执行时拒绝断开，避免把执行中的写入当作已取消。

## 发现不同版本的项目

```text
tia_list_open_projects(portal_version="V17")
tia_list_open_projects(portal_version="V21")
```

发现操作在临时独立进程中运行，不替换已有连接。省略 `portal_version` 使用服务配置的版本。跨版本连接以 `project_path` 的实际项目后缀为准，不升级工程。电脑需要安装相应版本及 Openness。

如果配置文件或 `SIEMENS_ENGINEERING_DLL` 环境变量固定指定某版本的 DLL，连接其他版本可能被版本检查拒绝。需要跨版本时应使用自动发现 DLL 的配置。新构建的可执行文件可能触发博途的 Openness 授权，请在博途中手动处理；普通构建不会自动修改白名单。

## 请求超时和故障

- 某项目后台退出后，其他项目连接保留。操作不会自动重放；核实状态后，对该项目显式调用 `tia_connect` 才会新建后台进程。
- 后台请求默认等待最多 10 分钟。超时不代表写入没有发生，也不会终止或重试写入。该项目后续请求被阻止，先查询 `tia_connection_status(project_path)`。
- 状态为 `busy_outcome_unknown` 时，仍在等待后台结果；`connected=null` 表示当前无法确认连接状态。待任务完成，再次查询会返回 `late_result`，包括原工具的完整结果和错误信息。
- 后台日志在配置文件旁的 `logs/project-workers/` 中；连接状态返回实际日志路径。进程结束时通过关闭输入让后台完成当前请求并释放会话，不强制杀死可能正在写入的后台进程。

统一入口仍按顺序执行收到的请求，常驻管道仍由一个客户端独占。**同时保持多个项目连接不等于多个项目写入并行执行。** 在线 `tia_online_*` 通道继续保持独立的一条在线连接，未纳入离线项目连接池。

## 验证

`tests/Test-MultiProject.ps1` 使用真实子进程及模拟工程操作，验证：V17/V21 路由隔离、中文路径传输、缓存和连接保留、歧义目标拒绝、独立断开、后台崩溃恢复、请求 ID 和项目身份核对、超时不重放、延迟结果读取及退出时保留执行中的操作。测试不访问博途，不代表两个真实版本工程已经验收。

2026-10-24 已通过两个真实 V17 工程同时连接、A/B/A 块目录读取、目标歧义拒绝和断开隔离验证，完成后保留两条连接。结果见 [VALIDATION_2.6.md](VALIDATION_2.6.md)。真实 V17/V21 混合连接尚未验收。

真实工程只读验证可运行：

```powershell
.\tests\SmokeMultiProject.ps1 -ExecutablePath '.\online-bin\TiaPortalSourceBridge.exe' -ProjectPaths @('E:\Projects\LineA\LineA.ap17','E:\Projects\LineB\LineB.ap21')
```

脚本连接、读取目标列表、检查独立后台及断开隔离，保存测试报告；不写工程、不保存、不下载。只有同时传入至少两个真实项目并通过，报告的 `multi_project_live_verified` 才为 `true`。
