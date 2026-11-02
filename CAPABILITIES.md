# GRAPH、硬件网络与 HMI 组态

3.0 新增统一工程目录、内容与显式引用索引：`tia_build_project_index`、`tia_get_project_tree`、`tia_find_references`、`tia_get_task_context`。连接时提供索引状态，接口说明引导 Agent 先取任务上下文。PLC/GRAPH/HMI/硬件均可分批建立内容索引，工程旁存储，可随工程迁移。调用方法、覆盖状态和解析限制见 [PROJECT_INDEX.md](PROJECT_INDEX.md)。

2.6 增加多项目连接和版本隔离，具体用法及验证边界见 [MULTI_PROJECT.md](MULTI_PROJECT.md)。管理多个项目时，以下工程操作和依赖版本的生成操作必须明确提供 `project_path`。

所有写入均针对离线工程。工具不下载程序、不切换 CPU 状态。写入与保存仍是不同操作；写入必须 `confirm=true`，保存另用 `tia_save_project`。下文的 JSON 是模型内容，调用时序列化一次后传入 `model_json`、`objects_json` 或 `plan_json` 字符串参数。

## GRAPH：从零构建和完整修改

2.9 通用快速修改：`tia_update_graph(block_name, patches_json, group_path?, expected_hash?, dry_run?, backup_mode?, compile_mode?, confirm?)`。在工具内部对整个块执行一组 XPath 补丁，适用于任意 GRAPH 流程；支持步骤/动作/互锁/监控/转换/分支/连线/时间及多个顺序。新增、删除节点需要在同一批补丁中补齐连线；工具同步步/转换 Static 接口、重命名的局部引用和重新编号的连线。详细示例见 [VALIDATION_2.9.md](VALIDATION_2.9.md)。从零构建和完整模型写入仍使用以下接口。

- `tia_generate_graph(model_json, base_xml?)`：生成完整 FB XML，不连接或修改工程。
- `tia_write_graph(model_json, base_xml?, group_path?, compile_mode?, confirm)`：新建或覆盖 GRAPH FB，备份后导入并编译。
- `tia_read_graph_model(block_name, group_path?)`：返回可往返编辑的 `model` 与 `base_xml`。
- `tia_patch_graph`：仍支持对任意 Graph 内部节点做 XPath 局部补丁。

内置 V17、V21 的独立 GRAPH 骨架，无需先在博途中手工新建参考块。其他版本可传入相同版本的 GRAPH `base_xml`。完整修改已有块时可直接编辑读取到的模型；传回 `base_xml` 可以保留接口和块设置。写入同名现有 GRAPH 而未提供 `base_xml` 时，工具会先读取现有块作为基础，避免丢失原接口。

```json
{
  "block_name": "MotorSequence",
  "block_number": 32150,
  "interface_members": [
    {"section":"Input", "name":"Start", "datatype":"Bool"},
    {"section":"Input", "name":"Reset", "datatype":"Bool"},
    {"section":"Output", "name":"Motor", "datatype":"Bool"}
  ],
  "sequences": [{
    "title":"启动与复位",
    "steps": [
      {"number":1, "name":"Idle", "initial":true,
       "actions":[{"qualifier":"R", "operand":"#Motor"}]},
      {"number":2, "name":"Running", "maximum_step_time":"T#10S", "warning_time":"T#7S",
       "actions":[{"qualifier":"N", "operand":"#Motor"}]}
    ],
    "transitions": [
      {"number":1, "name":"StartMotor", "condition":{"branches":[[{"symbol":"#Start"}]]}},
      {"number":2, "name":"ResetMotor", "condition":{"branches":[[{"symbol":"#Reset"}]]}}
    ],
    "branches": [],
    "connections": [
      {"from":{"kind":"Step","number":1},"to":{"kind":"Transition","number":1}},
      {"from":{"kind":"Transition","number":1},"to":{"kind":"Step","number":2}},
      {"from":{"kind":"Step","number":2},"to":{"kind":"Transition","number":2}},
      {"from":{"kind":"Transition","number":2},"to":{"kind":"Step","number":1}}
    ]
  }]
}
```

完整模型支持：

| 内容 | 字段 |
|---|---|
| 多顺控序列 | `sequences[]` |
| 步 | `number/name/initial/maximum_step_time/warning_time/actions` |
| 动作 | `qualifier/event/interlock/operand`；赋值等可用 `text`；复杂语法可直接提供 `tokens` 数组或整段 `actions_xml` |
| 转换 | `condition=true/false` 或 `condition.branches` 串并联触点、比较；完整 LAD/FBD 条件可用 `condition_xml` 和 `language` |
| 互锁、监控 | 步的 `interlocks/supervisions` 使用相同条件模型；或用 `interlocks_xml/supervisions_xml` 保留完整逻辑和报警文字 |
| 选择/并行分支 | `branches:[{number,type,cardinality}]`，`type` 为 `AltBegin/AltEnd/SimBegin/SimEnd` |
| 连线、跳转、结束 | `from/to` 的 `kind` 为 `Step/Transition/Branch/End`；分支引用用零起始 `port`；`link_type` 为 `Direct/Jump`。省略时自动把循环返回步骤的回边识别为 `Jump`；显式的普通直连循环会被拒绝 |
| 前置/后置逻辑、报警设置 | `pre_operations_xml/post_operations_xml/alarms_settings_xml` |
| 完整接口和块设置 | `interface_xml/block_attributes`；新增自定义接口用 `interface_members` |

读取模型还包含各步、转换和分支的 `xml`，用于保留注释、网络和其他扩展信息。明确提供 `actions` 或 `condition` 会覆盖相应的原始 XML 字段。删除、添加或重排步骤时同时修改连线；构建器会检查初始步、重复编号、悬空引用、分支类型和端口范围，并同步步与转换的 Static 接口成员。生成 XML 不等于工程编译通过；符号、调用和数据类型由实际 TIA 编译验收。

## PLC 硬件与网络

`tia_read_hardware_configuration` 返回设备、模块、地址区、子网、网络节点、IO 控制器/连接器、IO 系统及物理端口。默认包含当前值、实际 .NET 类型和读写能力；`include_attributes=false` 只读取结构，`path_prefix` 可以缩小返回范围。名称中的斜杠、空格和中文经过 URI 编码；构建计划直接使用读取结果的原始 `path`，不要手工猜路径。

`tia_apply_hardware_configuration(plan_json, dry_run, confirm)` 接受按顺序执行的计划。默认 `dry_run=true`，只检查格式，不声称已验证实际目录和未来对象路径。执行时用 `dry_run=false, confirm=true`。类型标识必须来自实际硬件目录或已有设备的 `TypeIdentifier`。

| `op` | 关键字段 | 行为 |
|---|---|---|
| `create_device` | `type_identifier/name/device_name` | 按目录型号新建设备及其子组件 |
| `plug_module` | `target/type_identifier/name/position` | 先检查 `CanPlugNew`，再在指定槽位插入模块 |
| `create_subnet` | `type_identifier/name` | 创建以太网等子网 |
| `set_attributes` | `target/attributes` | 批量写实际可写属性，按目标类型转换并检查数值范围；也支持公开可写的静态属性 |
| `connect_subnet` | `target/subnet` | 将网络节点接入子网 |
| `create_io_system` | `target/name` | 在 IO 控制器上建立 IO 系统 |
| `connect_io_system` | `target/io_system` | 将 IO 设备连接到指定 IO 系统 |
| `connect_ports` | `target/peer` | 构建物理端口拓扑 |

例如读取实际路径后批量设置节点 IP 和 I/O 起始地址：

```json
[
  {"op":"set_attributes", "target":"<读取到的网络节点路径>",
   "attributes":{"Address":"192.168.5.10", "SubnetMask":"255.255.255.0"}},
  {"op":"set_attributes", "target":"<读取到的地址区路径>",
   "attributes":{"StartAddress":128}}
]
```

创建和连接操作按计划顺序执行。每次结构变化后重新索引，后续操作可引用新对象。执行前保存属性与拓扑快照到 `configuration-backups`；该快照不是完整项目归档，也不是自动回滚包。发生错误会停止后续步骤，逐项标记成功、失败或跳过，已成功的步骤会保留在离线工程里。不自动重试可能已经修改工程的操作。

`tia_compile_hardware_configuration(target_path)` 使用目标设备实际提供的 `ICompilable` 服务编译并返回诊断；若设备不提供该服务则明确返回错误，不用软件编译结果代替硬件验收。

## 经典 WinCC HMI 组态

- `tia_read_hmi_configuration`：快速目录或批量内容读取。`kind` 可选 `all/screens/screen_templates/popup_screens/slidein_screens/tag_tables/scripts/text_lists/graphic_lists/cycles/connections`。支持 `name_filter/group_prefix`。默认不返回大 XML；需要内容时设置 `include_xml=true` 或 `include_model=true`。
- `tia_generate_hmi_configuration(model_json)`：从画面/变量表简明模型或通用节点模型生成一批 XML。
- `tia_write_hmi_configuration(objects_json, hmi_name, compile?, confirm)`：批量写入，先核对全部 XML、目标类型、名称和 TIA 版本，再按类别顺序导入；覆盖前备份到 `hmi-backups`，默认最后统一编译一次。

```json
{
  "tag_tables": [{
    "name":"PanelTags", "group_path":"MyPanel",
    "tags":[{"name":"Speed", "data_type":"Int"}]
  }],
  "screens": [{
    "name":"Dashboard", "number":100, "width":800, "height":480,
    "group_path":"MyPanel",
    "items":[
      {"type":"Button", "name":"StartButton", "left":20, "top":20,
       "width":120, "height":40, "text":"启动"},
      {"type":"IOField", "name":"SpeedDisplay", "left":20, "top":90,
       "width":120, "height":35, "tag":"Speed"}
    ]
  }]
}
```

常用控件包括 `Button/TextField/IOField/Rectangle/DateTimeField/GraphicIOField/SymbolicIOField`。控件可指定 `layer/attributes/text/language/tag/tag_property/text_list/events_xml`；变量可指定 `data_type/connection/address/plc_tag/acquisition_cycle/attributes`。`address` 使用目标 HMI 接受的逻辑地址格式。控件与变量名应在目标 HMI 中唯一。Basic、Comfort、Advanced 的具体可用控件和属性由对应设备与 TIA 校验。

完整读取返回的通用模型使用 `element/namespace_uri/attributes/content` 描述 XML 树，包含文本、链接、事件、字体、动态绑定和嵌套对象。可修改树后提交：

```json
[{"kind":"screens", "group_path":"MyPanel", "model":"<这里放读取到并修改的模型对象，不是字符串>"}]
```

也可以把完整 XML 放在 `xml` 字段。构建器的 `objects` 数组接受相同的通用模型格式，可用于新建画面模板、弹窗、脚本、文本/图形列表等已有明确 XML 定义的对象；不限制为常用控件简明字段。

当前目标为经典 WinCC，**不包含 WinCC Unified**。经典 Openness 未暴露的对象不能仅凭“完整 XML 模型”承诺可导入，最终以设备的公开能力和编译结果为准。

## 修复与备份

- PLC/HMI 名称必须匹配完整显示名或精确别名；不再用部分名称或唯一设备兜底。
- 批量源码导入只要有失败项就返回 `isError=true`，不会被“编译无错误”掩盖。编译异常时保留逐项结果和备份信息。
- `tia_restore_backup` 支持普通程序块、UDT 和 PLC 变量表。
- 新 PLC 备份包含 `.xml.meta.json`：记录工程、PLC、原组、对象类型与内容指纹。恢复时核对身份并使用原组，拒绝已被修改的备份或错误目标。
- 旧备份没有组元数据；恢复已删除对象时应明确提供原 `group_path`，根组用 `/`。
- 不再在写入过程中按全局 200 文件上限自动删除旧备份。

## 验证入口

- `tests/Test-EngineeringSafety.ps1`：目标核验、备份/恢复、批量失败状态、GRAPH 从零构建/修改和官方 XSD 验证、HMI 模型、硬件计划本地替身测试。
- `tests/SmokeConfiguration.ps1 -ExecutablePath <exe>`：检查发布包中 V17/V21 模板及 MCP 调用；不连接工程。
- `tests/LiveV17Validation.ps1`：显式指定项目的实际工程验证；在独立测试组中操作，不下载。
- `tests/Test-Online.ps1`：原有在线协议回归测试。

构建会同步主程序、在线辅助程序与 `templates` 到两个启动目录。运行中的旧桥接需要重新启动才会加载新版本。Openness 白名单仍由 `TrustThisTool.bat` 独立管理，普通构建不会隐式请求管理员权限。
