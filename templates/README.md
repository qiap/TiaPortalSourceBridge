# 构建模板

`process/catalog.json` 与 `process/op270/*.json` 是外部工艺模板和知识资料，通过本地 MCP 接口按需读取；包含来源和适用限制，不需要连接博途。详见 [PROCESS_LIBRARY.md](../PROCESS_LIBRARY.md)。它们与下面的 GRAPH/HMI XML 构建骨架用途不同，不会由现有生成器自动套用。

`graph/v17.xml`、`graph/v21.xml` 从本工作区各版本的真实 GRAPH 导出文件提取独立骨架，移除了原工程名称、业务符号、自定义变量、步骤和动作。保留版本对应的标准 GRAPH 接口、运行数据、步/转换成员原型和设置，供 `GraphBuilder` 构建完整新 FB。模板只包含一个无业务输出的循环流程。

`hmi/v17` 和 `hmi/common` 为经典 WinCC 控件/变量对象的 XML 默认片段，无项目、设备、连接或外部业务引用，也不包含 Engineering 文档版本。生成器使用调用目标版本创建外围文档并重新分配对象 ID。通用节点模型可以直接保留/修改目标版本的真实导出内容，不依赖这些常用控件片段。

模板是运行时资产，发布程序必须同时携带此目录。没有对应版本 GRAPH 骨架时不会自动更改 XML 版本，须提供同版本 `base_xml`。
