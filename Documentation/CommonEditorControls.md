# 通用树与基础类型编辑控件

`SharpEngine.UI` 提供不依赖 World、资产编辑器或项目业务的通用控件。固定布局在 `Content/UI/Controls` 的 RML，外观在 `CommonControls.rcss`；编辑器仅提供数据、事件绑定、权限和事务适配，不再私建树节点或逗号分隔的 TRS 输入。

## 树

`UiTreeView`（`Describe` 返回 schema v1 的行状态）/ `UiTreeRow` 使用同一 `TreeView.rml`：展开箭头、祖先引导线、图标、名称/说明和整行选中。整行是点击区域，缩进作用于内容；分支切换是独立按钮。`UiTreeProjection.Visible` 验证键与父引用，生成按父子顺序展开的可见节点；循环、重复身份和悬空父引用拒绝。折叠、选中和搜索属于各视图的临时状态，不保存到资产。

| 入口 | 数据与特定操作仍由谁负责 |
| --- | --- |
| 主场景 Hierarchy | World authoring；保留拖拽与上下文属性 |
| UI Widget Tree | UiEditorDocument；保留搜索、展开、选中、重排和组合边界 |
| StaticMesh Socket | 网格 Socket 文档；保留不可修改根与父引用规则 |
| SkeletalMesh 骨骼树 | 网格只读骨骼数据 |
| Skeleton 骨骼/虚拟骨/Socket 树 | Skeleton 文档与 rig 引用 |
| Prefab Hierarchy | Prefab 的实体层级与选中 |
| 项目浏览器最近项目 | 无父级的同一 TreeView 节点展示 |

Content Manager 的文件夹/资源浏览是目录列表，不把它伪装成递归树。搜索、筛选和命令工具栏也不属于树节点。

控件实例缓存未变化的行布局；消费者可复用同一 `UiTreeView` 并调用 `SetRows`。共享定义只读，节点身份、折叠和内容不能跨视图共享。所有交互仍通过原生输入、命中、焦点与现有语义命令。

## 基础类型

`UiValueEditors.Input/Text/Multiline/Number/Boolean/Choice/Vector/Rotator/Transform` 提供共同外观与语义绑定。`ValueField.rml` 组合字段标签和编辑器，`ValueAxis.rml` 组合轴标签和标量输入，`VectorEditor.rml` 组合两个至四个分量。Rotator 复用三轴编辑器，以度表示 X pitch / Y yaw / Z roll。Transform 通过 `TransformEditor.rml` 组合 Position(Vector)、Rotation(Rotator)、Scale(Vector)，共九个输入框。RGBA 使用相同四分量布局，颜色选择器继续负责其专用连续预览与确认。

类型呈现是 UI 契约，不修改源资产的 JSON 字段、坐标系或四元数存储，不把所有 Quaternion 自动解释成 Rotator。主场景 Transform 和 Socket 旋转使用各自既有的领域转换；修改一个分量保留其他分量。

统一入口包括主场景组件 Inspector（含嵌套值和列表元素）、StaticMesh/Skeleton Socket、共享资产字段（Material、Input、DataAsset、StyleSheet、动画状态机）、编辑器设置、源码/资源创建表单、UI 属性/样式/布局/参考尺寸，以及动画状态机预览参数。媒体源预览的只读信息也复用相同字段。枚举和引用使用 Choice，布尔使用 Boolean；搜索仍即时响应，原始源文本、复杂 JSON 及 UI 布局长度（如 px、%、auto）保留其文本语义，不猜测成 Vector。

## 提交、撤销与焦点

- 输入框中的中间文本仅是原生临时草稿；输入负号、小数点不会发作者变更。
- 单行输入按 Enter 或失焦时交给原有领域命令校验后提交一次；多行文本保留 Enter 换行，Ctrl+Enter 或失焦提交一次。无效值不改变文档；结束编辑后回显最后提交值，领域诊断仍可见。
- Esc 恢复进入字段时的值，丢弃当前草稿，不回滚此前已经成功提交的修改。
- 每个分量单独提交，一次 Undo 恢复这次提交；其他分量不被旧展示值覆盖。
- 点击另一个控件导致失焦时，提交可先执行，但面板刷新延后到鼠标释放，避免破坏本次点击。文本编辑期间不替换正在使用的输入控件。
- 主场景保留组件 revision 检查；在相邻分量的输入草稿保持焦点时，仅当当前字段仍等于控件捕获的原值，才会按最新文档 revision 提交，避免覆盖并发修改；Socket 分量绑定稳定 Socket ID，旧事件不能写入新选中的 Socket。Save、只读保护和 Runtime/Authoring 边界仍由原有领域持有。

输入通过 `UiValueInput` 声明 `Id`、`Value`、`CommitAction`、`Kind` 和 `ReadOnly`；原生 `data-value-editor` / `data-value-commit` 只负责草稿提交时机，校验、事务与保存不搬进 UI 基础层。编辑器和 Game 共用原生 `RmlValueEditor` 草稿适配器；通用 RML 宿主通过 `ui.value-*` 协调焦点和刷新，未改变原生 ABI。使用这些控件的宿主显式引用 `CommonControls.rcss`，稳定资源路径可由 `UiCommonControls.StyleSheetUri` 发现。

## 验证入口

`Tools/Viewport3DValidation` 只引用 Runtime，覆盖独立树/输入组合、转义、实例隔离与非法父引用。`Tools/EditorValidation` 验证适配后的领域语义与缓存。`Tools/McpUiValidation` 的 `--static-mesh-sockets-only`、`--skeleton-gizmo-only`、`--inspector-only` 和 `--ui-editor-only` 验证真实输入、焦点、草稿/提交/撤销及匹配 PNG；完整的 tools/resources/templates 目录仍保留于 `Saved/Validation/McpUiCatalogs`。界面和性能检查是不同证据，不能用语义测试代替原生可见/可点击验收。

Game 原生宿主同样使用 `RmlValueEditor` 并将调用方提供的 commit action 交给 `IUiActionHandler`；基础控件不引用 Editor 或 World。`--game-ui-only` 包含实际 Game 数值草稿、Enter 提交与 Esc 取消验证。

Game 的 UI 观察对 input/select/textarea 同样发布 `value`，可读取原生草稿；真实提交值仍由所属项目状态提供，不混同两种状态。

共享 RML 通过 EmbeddedResource 编入 SharpEngine.Runtime；修改这些布局后需要重建对应配置并重启宿主。共享 RCSS 仍由宿主显式加载，不隐式改写业务 RML 或源资产。

## 可编辑与只读的统一呈现

只读内容与 disabled 状态使用同一套禁用表现：保持对应类型的输入布局与当前值，统一灰色文字、背景、边框和轴标签；取消输入/焦点反馈，不参与 Tab 导航。Text、Number、Multiline、Choice、Boolean、Vector、Rotator 和 Transform 均遵守同一规则，混合 Vector 仅禁用对应分量。

`UiValueInput.ReadOnly` 生成 disabled / aria-disabled / tabindex=-1，并移除值修改绑定；Choice/Boolean 同样不公开改变动作。原生草稿适配器还会拒绝 disabled 目标事件。领域文档权限继续负责阻止直接语义命令修改，视觉状态不能替代权限校验。

统计、Asset ID、Stable ID 等永久只读值通过同一 `ReadOnlyField` 适配器生成通用禁用控件；Input 绑定的只读展示复用可写时相同的字段/二维 Scale/布尔结构。可写资源的统计字段仍禁用，其可写字段保持启用。树的选择和展开是查看操作，不因资产只读而关闭。
