# Inspector 组件实现记录

本轮实现沿用 `InspectorComponentDesign.md` 中已确认的决策。

## 实现入口

| 内容 | 代码入口 |
| --- | --- |
| 组件基类、特性、实体与初始状态契约 | `Source/SharpEngine.Runtime/Components/ComponentContracts.cs` |
| 组件描述、JSON Schema、校验与字段编码 | `Source/SharpEngine.Runtime/Components/ComponentRegistry.cs` |
| v3 World/Prefab 记录、身份和引用处理 | `Source/SharpEngine.Runtime/Components/SceneData.cs` |
| Runtime 世界、阶段调度、动态创建和清理 | `Source/SharpEngine.Runtime/Components/RuntimeWorld.cs` |
| 编译期字段、默认值、访问器及工厂生成 | `Source/SharpEngine.ComponentGenerator/ComponentGenerator.cs` |
| World 与共享 Authoring 事务 | `Source/SharpEngine.Runtime/World/WorldDescription.cs`、`Source/SharpEngine.Editor/World/WorldAuthoring.cs` |
| Inspector 控件与 UI/MCP 共享修改 | `Source/SharpEngine.Editor/Editor/EditorComponentInspector.cs` |
| Prefab 文档选择、历史、保存及实例化 | `Source/SharpEngine.Editor/Editor/EditorPrefabDocuments.cs` |
| 真实输入、编辑提交和拖放 | `Source/Engine/UI/RmlUiShell.cpp` |

Inspector 支持组件搜索添加、依赖补齐、启用切换、字段编辑、Reset、Remove、组件重排及实体创建/销毁。字段元数据来自构建期生成器，包含公开和显式标记的私有字段。常见控件包括数值、Range 滑条、布尔、枚举、引用选择、向量、颜色预设及 RGBA 输入、列表和嵌套数据。

文本编辑在 Enter/失焦时提交，Esc 恢复已提交值；字段错误不修改模型。滑条手势合并 Authoring Undo。失焦切换选择时，提交目标仍是原组件所属实体。程序重建控件产生的事件不会被当作用户输入。

Play 从最新 Authoring 内存状态生成独立 Runtime 实例。Runtime 支持动态实体和组件、可选类型化 InitialState、固定生命周期阶段、60 Hz FixedUpdate、暂停与单步。回调异常只记录，后续正常调度继续，不回滚或自动停用组件。Stop 不保存 Runtime 改动。

Prefab 在独立原生窗口中以文档 Tab 编辑，拥有独立选择、Dirty 与 Undo/Redo。支持新建、从实体子树导出、显式保存，以及从 Content Manager 拖到 Hierarchy/Viewport 实例化。导出拒绝外部场景引用；实例化重建 ID 和内部引用。Play 始终运行主 World。

`.sworld` 已直接升级为 JSON v3，不提供旧 XML/v2 兼容或升级工具。未知组件记录保留在文档层。完整 Cook 管线不属于此次新增内容；数据层提供已知 schema、引用及未知组件的 Cook 前校验。

## 验证结果

| 验证 | 结果 |
| --- | --- |
| Debug/x64 完整解决方案构建 | 通过；第三方 RmlUi Windows 头仍有既有 UNICODE 宏重定义警告 |
| ComponentGeneratorValidation | 14 项编译断言通过 |
| ComponentValidation | 41 项断言通过 |
| WorldValidation | 52 项断言通过 |
| WorldAuthoringValidation | 26 项断言通过 |
| EditorValidation | 104 项断言通过 |
| EditorLayoutValidation | 40 项断言通过 |
| ProjectSelectorValidation | 18 项断言通过 |
| 完整 MCP/UI 集成 | 33 项断言通过 |
| Inspector 项目组件与 Prefab UI 集成 | 111 项断言通过 |
| 新建 Runtime Module 组件并统一构建发布 | 通过；已确认程序集包含生成的组件目录 |

UI 验证使用隔离、完整渲染的 Editor，通过 `ui.pointer` / `ui.key` 的命中、焦点和事件路径执行。覆盖添加/删除/Reset、输入缓冲与校验、选择切换时的失焦提交、项目公开/私有字段、Prefab 窗口、保存以及拖入 Hierarchy。

渲染证据：`Saved/Validation/Inspector/component-inspector.png`。测试项目与输出在 `Saved/Validation/ComponentProject`，不进入版本库。

工作中出现的临时原生验证项目已移除；完整解决方案的布局检查随后恢复并通过。并行开发中的设置、热重载、源码创建和 Timeline 改动保留，没有执行 Git commit/push。
