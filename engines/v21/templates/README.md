# 构建模板（TIA Portal V21 专用版）

`process/catalog.json` 与 `process/v21/*.json` 是面向 TIA Portal V21 的通用工程模板和知识资料，通过本地 MCP 接口按需读取，不需要连接博途。

`graph/v21.xml` 从真实 TIA Portal V21 GRAPH 导出文件提取独立骨架（`GraphVersion 6.0`、`Graph/v6`、`Interface/v5`），保留标准 GRAPH 接口、运行数据、步/转换成员原型和设置，供 `GraphBuilder` 构建完整新 FB。

`hmi/common` 为经典 WinCC 控件/变量对象的 XML 默认片段，生成器使用 `V21` 创建外围文档并重新分配对象 ID。

模板是运行时资产，发布程序必须同时携带此目录。
