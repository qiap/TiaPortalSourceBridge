# HMI 导入校验（2026-11-03）

适用于 `tia_write_hmi_configuration` 的经典 HMI 路径，主引擎与 V21 共用校验代码。不是 Unified HMI 支持声明。

## 调用变化

`dry_run` 默认 `true`，只执行整批预检查。实际写入需要显式 `dry_run=false, confirm=true`。已有调用方必须同步调整；只传 `confirm=true` 不再执行导入。

预检查失败返回 `preflight.issues`，并明确 `mutation_started=false`。缺失类型、冲突对象、不能验证的引用会阻止整批写入。预检查与导入之间不要并发手工修改工程。

## 覆盖与局限

- 查询与索引提供 `coverage`，明确不包含集成连接及不可导出的 HMI 控件。空结果是“在当前覆盖范围未找到”，不证明不存在。HMI 查询另外提供各类别的 `scope_status`。
- 变量必须明确 `DataType`、`HmiDataType`；字符串必须明确 `Length`。模型生成支持 `data_type`、可选 `hmi_data_type`，以及 `attributes.Length`。`plc_tag` 输出为原生 `ControllerTag` 链接。
- 当前类型证据来自同一目标 HMI 的实时变量表导出，而非完整设备类型目录。尚未在目标出现的合法类型也可能被标记 `type_unverified` 并阻止导入；不会宣称“不支持”，不会通过省略类型绕过。
- 当前仅允许保留当前 HMI 导出已经存在的连接、PLC 符号和类型组合。新组合返回 `plc_binding_unverified` 并阻止导入。完整的原生连接端点解析和 PLC 符号/类型核验尚未实现；保留既有组合也不证明 PLC 端符号仍然存在或运行时通信正常。
- 检查整批对象冲突、跨表变量重名、传入页面的直接 Tag 引用。删除变量时额外扫描未被替换页面的可导出 Tag 引用。脚本间接引用和不可导出控件不在此检查范围。

## 回读与成功含义

每个实际导入对象立即重新导出，比较请求中的变量 `DataType/HmiDataType/Connection/ControllerTag/Length/AddressAccessMode/LogicalAddress` 以及可导出的页面 `Tag/TextList` 绑定。编号和缩进不参与比较。

不一致返回 `verification_differences`（字段、预期值、实际值、mismatch/unverified），该项失败并停止后续导入。只有整批未失败才执行可选编译。`verification_status=requested_fields_verified` 表示这些请求字段通过核对，不表示全部工程语义或运行时通信已验证。

保持原有严格 XML 相等判定用于跳过未变化的对象；没有把语义比较用于决定是否省略真实导入。

## 故障诊断

保留原生异常链、失败阶段、备份路径和 `may_have_changed`。主引擎不自动回滚。V21 此路径要求成功建立事务，不允许无事务降级写入；不能确认提交或事务结束失败时报告错误，不声称成功回滚。

`save_state=not_saved_by_this_operation` 不代表工程没有此前的未保存修改。`connection_state=not_probed_after_operation`、`rollback_state=not_verified` 明确尚未核验的状态。计数仅描述导入过程中的观察结果，不代表回滚后的最终状态。

## 验证和交付

`tests/Test-HmiGuard.ps1` 在两套引擎运行类型默认值、字符串长度、绑定丢失、编号/格式变化、名称冲突、未验证类型和新 PLC 绑定等离线回归测试，已纳入 `tests/Run-Checks.ps1`。

候选程序在 `.work/hmi-guard-build/main` 与 `.work/hmi-guard-build/v21`；未替换当前运行的程序，未连接或写入用户工程。真实工程导入及事务行为仍需在测试工程中验收。
