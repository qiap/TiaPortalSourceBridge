# 经典 HMI 离散报警接口（3.1）

当前为开发候选版，尚未替换已安装程序。V17 中文界面的独立工程副本已完成实际导入及回读；窗口定位和控件查询仍有偶发超时，不能视为稳定的无人值守工程接口。详见 [VALIDATION_3.1.md](VALIDATION_3.1.md)。

`tia_read_hmi_alarms` 查询报警，`tia_write_hmi_alarms` 批量新增或修改。当前范围为经典 WinCC 的离散报警；不包含模拟量报警、PLC Program_Alarm 或 WinCC Unified。

V17 经典 HMI 的公开 Openness HmiTarget 没有离散报警集合。因此工程模式由工具内部的 Windows 自动化驱动操作博途原生导入/导出。Agent 一次调用即可提交整批操作，不需要逐条点击。工具不依赖 Codex 的电脑操作技能、Python、Excel 或第三方表格库。

工程必须已打开并处于项目视图，明确传入 `project_path` 和项目树中的 HMI 名称 `hmi_name`。窗口需要可交互；被对话框阻塞、锁屏、权限不同或控件结构不兼容时会返回错误。不会自动接受 Openness、UAC 或其他安全提示。多项目并存时按项目完整路径定位窗口，可附 `process_id`。

## 读取

```powershell
.\Invoke-HmiAlarms.ps1 -ProjectPath 'E:\工程\设备.ap17' -HmiName 'Panel' -Filter '气缸' -Refresh
```

工具参数：`filter` 关键字、`trigger_tag` 精确变量名、`offset/limit` 分页、`include_fields` 返回全部原生列。`max_age_ms` 默认 2000，0 表示不接受缓存，`refresh=true` 强制新导出。结果包含稳定的内容指纹和快照年龄。

## 批量修改

将操作数组保存为 UTF-8 JSON 文件，例如：

```json
[
  {"op":"update","id":73,"trigger_tag":"HD_31_Alarm_Timeout6","trigger_bit":0},
  {"op":"create","id":75,"template_id":73,"name":"Z13_Timeout_Extend","trigger_tag":"HD_31_Alarm_Timeout7","trigger_bit":8,"texts":{"zh-CN":"测试气缸13伸出超时"}}
]
```

```powershell
# 预览整批修改
.\Invoke-HmiAlarms.ps1 -ProjectPath 'E:\工程\设备.ap17' -HmiName 'Panel' -ChangesFile '.\changes.json'
# 执行整批修改
.\Invoke-HmiAlarms.ps1 -ProjectPath 'E:\工程\设备.ap17' -HmiName 'Panel' -ChangesFile '.\changes.json' -Apply
```

`op` 支持 create/update/upsert（默认 upsert）；使用稳定报警 ID 定位。`template_id` 只用于新增，复制原报警设置和未知列，随后应用明确提供的字段。新增必须提供唯一 name、trigger_tag、trigger_bit 和 texts。支持 alarm_class、确认变量和位、group、report，以及 `fields` 中按原生列名修改其他字段。未知字段名会拒绝执行，整批预检失败不产生导入。

可传 `expected_hash` 拒绝过期内容。写入始终重新导出当前报警，不使用读缓存。相同内容跳过导入。发生导入超时或失败不会自动重试，检查 `imported`、`verified`、`may_have_changed` 和错误信息，核实工程状态后再决定操作。

## 原生文件模式

提供 `source_file` 可直接查询或生成修改后的 xlsx，不需要打开博途。可指定一个尚不存在的 `output_file`。结果中 `requires_tia_import=true` 表示只生成文件，尚未写入工程。

```powershell
.\Invoke-HmiAlarms.ps1 -SourceFile '.\HMIAlarms.xlsx' -ChangesFile '.\changes.json' -OutputFile '.\updated.xlsx' -Apply
```

工具直接补丁修改原生 ZIP/XML 包，保留 `TIA_Version`、工作簿关系、富文本、其他工作表和未知部分；不会通过普通表格库重建文件。

## 性能与结果

一次批量工程写入最多执行一次导出、一次导入和一次回读导出。窗口控件按容器搜索并跳过报警数据表，元数据批量缓存，避免查询按钮时读取所有报警单元格。多个工程的界面任务共用互斥锁，防止抢焦点。

默认 dry_run=true；实际执行需要 dry_run=false、confirm=true。不自动编译、保存或下载。会在工具目录 `alarm-sessions` 留下本次导出、待导入和回读工作文件及计时。`verified=true` 才表示工程回读确认成功。

`timeout_ms` 默认 60000，约束一次完整导出或导入；工程写入包含最多三次界面操作。锁屏、权限不一致、前台焦点变化及控件查询超时可能导致失败。文件模式不需要交互桌面。当前仅实测 V17 中文经典 HMI；其他版本与界面语言尚未验证。
