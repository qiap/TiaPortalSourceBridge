# 性能改进与验证

3.0 工程索引将内容解析和引用定位放进工具，减少 Agent 反复读取整份 XML。当前真实 V17 工程296对象目录约1.48秒，293项内容首次分批建立约21.9秒，3项因不一致拒绝导出。索引就绪后的 HMI 任务上下文服务执行中位数16.47ms、响应约37.9KB，不再调用 Openness；不含最后序列化及客户端解析。通用方法与覆盖限制见 [PROJECT_INDEX.md](PROJECT_INDEX.md)，数据见 [VALIDATION_3.0.md](VALIDATION_3.0.md)。

## 推荐调用方式

- 已知程序块或 UDT 的组：在 `tia_list_blocks` / `tia_list_types` 传 `scope_path`，直接读取该组及子组；`/` 表示根组。`group_filter` 保留原有模糊筛选语义。局部扫描读取当前状态，不覆盖全工程缓存。
- 已知单块位置：在读取时传 `group_path`，根组明确传 `/`，避免先建立完整目录。
- 硬件：传 `path_prefix` 读取目标子树；仅需结构时设 `include_attributes=false`。无需扫描其他设备和无关网络服务。
- HMI：传 `group_prefix` 直接定位文件夹；仅需目录时保持 `include_xml=false, include_model=false`。
- 支持 MCP `structuredContent` 的客户端可传 `response_mode=compact`。完整 XML/源码仍在结构化结果里，文本内容不再重复整份载荷。默认 `full` 保持原有兼容行为。
- 生成 GRAPH 只需 XML 时传 `include_model=false`；读取完整模型可传 `include_base_xml=false`，避免同时传输整份基础 XML。修改同名现有 GRAPH 时写入器会读取原块作为基础；需要复制完整外层设置和注释时仍应保留并传入 `base_xml`。
- 连续修改用批量接口，最后统一编译。不要把 `compile_mode=none` 的导入结果当作编译验收通过。

示例：

```text
tia_list_blocks(project_path="E:\Projects\LineA\LineA.ap17", scope_path="Station/Sequence")
tia_read_hardware_configuration(project_path="E:\Projects\LineA\LineA.ap17", path_prefix="<读取到的实际节点路径>", include_attributes=false)
tia_read_block(project_path="E:\Projects\LineA\LineA.ap17", block_name="Main", group_path="/", response_mode="compact")
```

全工程块/UDT 缓存继续在项目会话中复用。用户在博途中手动修改目录后，可传 `refresh=true` 获取最新全目录。局部扫描不依赖该缓存。

## 批量写入

硬件计划保留执行前的完整属性和拓扑快照。创建硬件、模块、子网和 IO 系统后更新受影响的索引；地址、注释等稳定属性以及网络连线不再每步重扫全工程。重命名或其他可能改变结构的属性仍刷新索引。只有可能改变软件发现结果时才重新发现 PLC/HMI。

局部读取时，子网链接可以直接构建完整路径。若物理端口或 IO 系统指向读取范围之外，则额外建立一次完整拓扑索引，保留与完整读取相同的目标路径；不会为追求局部扫描速度退化为可能重名的对象名称。

已经读取的属性在类型转换后与当前值相同，则不重复写入；逐项返回 `changed_attribute_count`。仍检查实际写权限。未知或只写属性不会凭空判断为未变化。

HMI 一批对象按类别建立一次目录索引，并复用目标文件夹定位结果。实际覆盖仍先导出备份；失败仍停止后续导入。默认 `skip_unchanged=true`，仅当对象根 XML **严格一致，包括 ID 和空白内容** 时跳过导入，返回 `unchanged_count`；可设为 `false` 强制覆盖。严格比较可能漏掉语义相同但格式不同的情况，不会为了提高跳过率忽略可能有意义的数据。`compile=true` 时仍进行统一编译。

批量源码读取遇到多个不存在的名称时，共享一次缓存刷新机会，不再每个失败名称都重扫。每项错误仍保留，批次包含失败时返回 `isError=true`。

## 耗时和访问计数

工具响应的 `_meta.performance` 包含执行毫秒数、`counts` 和 `phases_ms`。网关转发的响应还保留 `worker`，其中是项目后台的执行数据。可观察 `hardware_scans`、`hmi_catalog_scans`、`block_catalog_scans`、`property_reads`、`method_calls` 等。

这些计数是桥接层的访问次数，**不是 Siemens 内部物理 IPC 次数**。`execution_ms` 不包含该层最终响应序列化与写出耗时；客户端实测端到端耗时才包括传输、解析等待等开销。整个协议按 UTF-8 输出，中文无需转换为六字节的 Unicode 转义。

## 验证入口

- `tests/Test-EngineeringSafety.ps1`：包含 27 项性能回归检查，另有原有工程目标、模型和备份检查。硬件替身测试验证九步构建只做一次初始全扫描、局部读取、相同属性不重复写入；HMI 替身验证 20 个对象只建立一次同类目录索引。
- `tests/Test-MultiProject.ps1`：项目隔离、超时不重试和后台生命周期回归。
- `tests/Benchmark-Bridge.ps1`：对保持连接的真实工程重复测量，中位数报告保留在指定文件中。使用 `-Optimized` 启用明确的组作用域读取；其余任务保持相同。脚本全程只读。
- `tests/Validate-PerformanceWrites.ps1`：在独立名称与测试组里创建 4 个 HMI 画面、1 张变量表和 1 个未连接子网，验证批量导入、严格不变跳过、重命名路径刷新和属性不变跳过；不保存、不下载。

本机测量数据位于 `tests/live-results/performance-2.7/`。实际测得的前后对照会单独记录；不根据减少的代码调用次数宣称固定的整机提速倍数。博途自身的导入、编译、授权等待和工程规模仍影响总耗时。当前统一入口仍顺序处理工程请求，多个项目保持连接不表示并行写入。

## 2.9 通用 GRAPH 优化

已有 GRAPH 修改优先使用 `tia_update_graph`，一次提交同一块的全部补丁，复用导出快照备份，避免完整 XML/模型多次往返。生成、读取完整模型和离线补丁共享解析后的文档；连线与循环校验使用索引。新增 `graph_xml_parses`、`graph_imports` 计数，以及 `graph_read/graph_parse/graph_patch/graph_generate/backup/import` 阶段计时。结构读取可关闭 `include_raw_xml` 和 `include_logic`。

离线 MCP 对照（预热1次，重复5次，中位数）：20步 XML 生成73.606→12.330ms，60步1355.337→33.236ms，120步11514.469→60.765ms。13步实际导出样本单项补丁11.583→12.083ms，基本持平。生成耗时不包含博途导入和编译；不代表所有 GRAPH 修改的固定倍数。完整方法、限制、调用示例和运行版本状态见 [VALIDATION_2.9.md](VALIDATION_2.9.md)。

## 本机 V17 实测

2026-10-24，同一工程、测试对象创建之前，各任务重复 3 次的端到端耗时中位数：

| 任务 | 2.6 / ms | 2.7 / ms | 前后耗时比 |
|---|---:|---:|---:|
| 指定程序组 | 470.56 | 18.90 | 24.90 |
| 全部程序块，强制刷新 | 432.12 | 187.60 | 2.30 |
| 局部硬件结构 | 313.30 | 19.81 | 15.82 |
| 局部硬件属性 | 347.72 | 34.77 | 10.00 |
| 指定 HMI 文件夹目录 | 62.78 | 7.37 | 8.52 |
| 单块 XML，精简响应 | 173.76 | 76.27 | 2.28 |

块 XML 响应由 44,805 字节减少到 22,710 字节，完整 XML 仍在结构化结果中。其余小响应因增加性能指标，字节数可能略增。原始样本及全部九项任务见 `tests/live-results/performance-2.7/comparison.md`。

真实写入另行验证：4 个独立画面和 1 张变量表共 5 个对象，仅建立 2 次类别目录索引；导入并统一编译约 23.61 秒，编译 0 错误、53 项诊断警告（指向原有画面和文本列表等）。4 个画面原样写回约 200 ms，全部跳过导入。新建、重命名独立子网及相同名称属性跳过通过。写入没有旧版同工作负载基线，不能给出写入提速倍数；不变写回与创建并编译也不是等价任务。

测速后另补充了范围外端口和 IO 系统完整路径保护，替身回归通过；当前真实工程没有可验证的这两类外部连线，不能声称已在真实设备拓扑中验收。验证记录与最终运行版本状态见 `VALIDATION_2.7.md`。

## 2026-10-29 增量优化

索引查询状态快照、API 查找提前返回、在线采样计划缓存已完成。对照数据、验证范围和可复现脚本见 [本轮验证记录](VALIDATION_PERFORMANCE_20261029.md)。
