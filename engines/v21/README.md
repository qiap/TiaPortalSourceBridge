# TiaPortalSourceBridge — TIA Portal V21 Dedicated Edition (v4.0.0)

专为 **Siemens TIA Portal V21 (Openness 21.0)** 与 **S7-PLCSIM V21 / PLCSIM Advanced Runtime API 8.0** 打造的独立轻量化源码桥接与 MCP (Model Context Protocol) 服务。

本仓库已彻底剥离历史 V17 与特定产线（OP270）耦合代码，全面启用 Openness V21 模块化程序集架构、原生 **SimaticSD 系统文档**、**ExclusiveAccess + Transaction 原子事务回滚**、**Software Units（软件单元）与命名空间**、**官方 CrossReferenceService 交叉引用引擎** 以及 **无头 WinCC Unified 读取/编译**。

---

## 核心 V21 专属升级与架构优化

1. **原生 SimaticSD 文档双向读写（Token 节省 70%~90%）**
   - 新增 `tia_read_document`、`tia_read_documents_batch`、`tia_import_document`、`tia_import_documents_batch`。
   - 调用 Openness V21 `ExportAsDocuments` / `ImportFromDocuments`，将 SCL、LAD、FBD、STL、DB、UDT 直接映射为紧凑可读的 `.s7dcl` 代码文档与 `.s7res` 多语言资源文件，摆脱冗长 SimaticML XML 的 `UId` 噪音。
2. **Openness V21 排他锁与原子事务回滚 (`ExclusiveAccess` + `ITransactionSupport`)**
   - 所有修改离线工程的操作（SimaticSD 导入、批量 XML/源码导入、GRAPH 补丁、HMI 组态写入、硬件构建计划、AutomationML 导入、块组整理、删除等）均自动包裹在 `ExclusiveAccess` 与 `Transaction` 中。
   - 批量操作中途任一对象失败将**自动回滚整批更改**，杜绝工程残留半成品状态。
3. **V21 模块化 PublicAPI 加载与 Schema 严格对齐**
   - 自动发现注册表 `Openness\21.0\PublicAPI` 及本机 `PublicAPI\V21\net48\Siemens.Engineering.Base.dll`（连同 `Step7.dll`、`WinCC.dll`、`WinCCUnified.dll`、`Safety.dll`），修复 `.resources` 卫星程序集递归解析问题。
   - 修复 V21 梯形图命名空间为 **`FlgNet/v5`**（对齐官方 `SW.PlcBlocks.LADFBD_v5.xsd`），GRAPH 对齐 **`Graph/v6`**，并原生支持 V21 `<Namespace>` 属性与 `DocumentInfoOptions.None` 纯净 XML 导出。
   - 支持 `.ap21` 单用户工程与 `.als21` 多用户本地会话（`Portal.LocalSessions`）。
4. **Software Units（软件单元）、Namespace 与 `PlcNamedValueType` 全面贯通**
   - 新增 `tia_list_software_units`（枚举 `Units`、`SafetyUnits`、`NamespacePreset` 与单元间 `Relations`）。
   - 所有块/UDT/NVT/变量表读写与批量导入导出工具均支持 `unit_name` 作用域隔离，且 XML 导入支持 `SWImportOptions`（`IgnoreStructuralChanges`、`IgnoreMissingReferencedObjects`、`IgnoreUnitAttributes`）。
5. **官方 `CrossReferenceService` 精确交叉引用与死代码检测**
   - 新增 `tia_query_cross_references`，支持 `plc` / `block` / `type` / `tag_table` 范围及 `ObjectsWithReferences` / `UnusedObjects` 过滤，精确区分 `Read` / `Write` 访问方向（快速定位双线圈与未使用符号）。
6. **无头 WinCC Unified (`Siemens.Engineering.HmiUnified`) 与 CAx AutomationML**
   - 新增 `tia_read_unified_hmi` 与 `tia_compile_unified_hmi`，无需抢占桌面鼠标焦点即可读取 Unified 画面、变量、离散/模拟量报警、脚本与连接。
   - 新增 `tia_search_hardware_catalog`（`HardwareCatalog.Find`）与 `tia_export_aml` / `tia_import_aml`（`CaxProvider`）。
7. **S7-PLCSIM V21 / Runtime API 8.0 深度适配**
   - 自动优先加载 PLCSIM Runtime API `8.0`，动态识别全部 78 种 V8 `ECPUType`（含 S7-1200 G2、ET200SP、T/F CPU）及 `6ES7` 订货号重载。
   - 下载流程自动处理 V21 安全 PLC 密码委托（通过环境变量 `TIA_SIMULATION_PLC_PASSWORD` 注入 `SecureString`，不落盘明文日志）及新增装载一致性校验项。
8. **基于 V21 时间戳的增量工程索引与按需工具集 (`toolProfiles`)**
   - 工程索引自动比对 V21 `ModifiedDate` / `CodeModifiedDate` / `InterfaceModifiedDate` / `IsConsistent`，目录刷新时保留未改动对象的 `ready` 快照，大幅提速大中型 V21 项目检索。
   - 支持在 `mcp-config.json` 的 `toolProfiles` 中按需裁剪暴露给 AI 客户端的工具分组，降低上下文开销。

---

## 快速开始

### 1. 编译构建
无需额外安装 .NET SDK，直接使用系统内置 `.NET Framework 4.8` 编译器一键构建：
```bat
build.bat
```
构建产物位于：
- `src\TiaPortalSourceBridge\bin\Release\net48\TiaPortalSourceBridge.exe` (x64, v4.0.0.0)
- `src\TiaPortalSourceBridge\bin\Release\net48\S7OnlineWorker.exe` (x86)

### 2. 注册 TIA Portal V21 Openness 白名单（免弹窗授权）
右键以管理员身份运行：
```bat
TrustThisTool.bat
```
脚本会自动计算当前 `TiaPortalSourceBridge.exe` 的 SHA256 与修改时间，写入 `HKLM\SOFTWARE\Siemens\Automation\Openness\AllowList` 与 `21.0\Whitelist`（自动去重）。同时请确保当前 Windows 用户已在 `Siemens TIA Openness` 用户组中（修改用户组后需重新登录 Windows 生效）。

### 3. 运行冒烟与 V21 XSD 校验测试
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\SmokeMcp.ps1
```

### 4. 配置 MCP 客户端
可通过持久管道守护模式（`StartMcp.bat` 配合 `mcp-client`）或直连 `mcp` 模式接入 Claude Code / Codex / Cursor / Antigravity：
```json
{
  "mcpServers": {
    "tia-portal-v21": {
      "command": "E:\\WORK\\TiaPortalSourceBridge-V21\\src\\TiaPortalSourceBridge\\bin\\Release\\net48\\TiaPortalSourceBridge.exe",
      "args": [
        "mcp",
        "--config",
        "E:\\WORK\\TiaPortalSourceBridge-V21\\mcp-config.json"
      ]
    }
  }
}
```

---

## `toolProfiles` 工具分组说明

在 `mcp-config.json` 中可通过 `toolProfiles` 控制启用的模块（留空或包含 `"all"` 时默认启用全部）：
- `core`: 连接管理、多项目进程状态、列出打开工程/PLC/HMI、显式保存
- `simaticsd`: V21 首选 `.s7dcl` / `.s7res` 单块与批量原子读写
- `plc`: 经典块/UDT/NVT/变量表列表、XML/外部源码读写、原子批量 XML 导入、编译、备份还原
- `units`: V21 Software Units 软件单元与关系查询
- `xref`: V21 官方 `CrossReferenceService` 交叉引用查询
- `graph`: V21 GRAPH (`Graph/v6` + `FlgNet/v5`) 生成、模型读取与局部补丁
- `unified`: WinCC Unified 无头组态读取与编译
- `hmi`: 经典 WinCC Comfort/Advanced 画面、变量、文本列表与报警工作簿
- `hardware`: 硬件与网络拓扑读取、事务构建计划与 V21 `HardwareCatalog` 搜索
- `cax`: AutomationML (`.aml`) 导出与事务导入
- `simulation`: PLCSIM V21 / Runtime API 8.0 实例控制与受控虚拟下载
- `online`: S7ONLINE / S7 TCP 标准 DB 在线采样诊断
- `index`: 增量工程索引树、符号引用与任务上下文检索
- `process_library`: 本地 V21 参数化模板与 SimaticSD 规范库

---

## 安全红线

1. **绝不自动保存工程**：所有导入、修改操作均停留在 TIA Portal 内存会话中（返回 `saved: false`），必须由人工确认并显式传入 `confirm: true` 调用 `tia_save_project` 才会落盘。
2. **强制修改前自动备份 & 事务保护**：覆盖或删除已有对象前默认生成带哈希校验的快照备份于 `block-backups/`；事务异常自动回滚。
3. **绝不向物理 PLC 下载**：`tia_simulation_download` 严格校验本机运行中的虚拟 PLCSIM 实例与 IP 一致性，拒绝物理网卡与未知目标。
