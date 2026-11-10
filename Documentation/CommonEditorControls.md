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

`UiValueEditors.Input/Text/Multiline/Number/Range/Boolean/Choice/Reference/Vector/Rotator/Color/Transform` 提供共同外观与语义绑定。`ValueField.rml` 组合字段标签和编辑器，`ValueAxis.rml` 组合轴标签和标量输入，`VectorEditor.rml` 组合两个至四个分量。Rotator 复用三轴编辑器，以度表示 X pitch / Y yaw / Z roll。Transform 通过 `TransformEditor.rml` 组合 Position(Vector)、Rotation(Rotator)、Scale(Vector)，共九个输入框。Color 组合 RGBA 四分量布局与 `ColorPicker.rml` 共享色盘；Range 在文本输入下方复用带约束的 Slider；Reference 为资产、Entity 和 Component 选择提供语义样式，选项解析仍由领域编辑器负责。

Color 的色块按钮展开饱和度/明度面板、Hue 色相条和 Opacity 滑条。拖动只更新可观察 DOM 中的临时预览，Apply 将整个 RGBA 数组交给调用方一次提交；Cancel、Esc 或再次点击展开按钮丢弃草稿。主场景 Inspector、DataAsset 与 Game 使用相同的原生 `RmlColorPicker`，UI 基础层不持有领域事务。`Color` 的 `CommitAction` 必须提供整色提交前缀；`PickerId` 可指定稳定身份，默认取第一个分量 ID 加 `-picker`。只读分量或未提供整色动作时禁用色盘按钮。色盘以 0–1 范围选色，数值输入仍保留所属领域允许的范围，打开或取消色盘不会截断已提交数值。

类型呈现是 UI 契约，不修改源资产的 JSON 字段、坐标系或四元数存储，不把所有 Quaternion 自动解释成 Rotator。主场景 Transform 和 Socket 旋转使用各自既有的领域转换；修改一个分量保留其他分量。

Choice 与 Reference 共用单一 28px 下拉外框，原生值区和箭头在内部对齐，不重复绘制文本框边框。展开列表的宽度包含自身留白和边框；只读态保留同样尺寸并禁用选择。

统一入口包括主场景组件 Inspector（含嵌套值和列表元素）、StaticMesh/Skeleton Socket、共享资产字段（Material、Input、DataAsset、StyleSheet、动画状态机）、编辑器设置、源码/资源创建表单、UI 属性/样式/布局/参考尺寸，以及动画状态机预览参数。媒体源预览的只读信息也复用相同字段。枚举使用 Choice，AssetRef/Entity/Component 使用 Reference，布尔使用 Boolean；DataAsset 的 Vector/Quaternion/Color 和带 Minimum/Maximum 的标量复用对应公共控件。没有明确运行时类型的对象不会按字段名猜测成 Transform；原始源文本、复杂 JSON 及 UI 布局长度（如 px、%、auto）保留其文本语义。

## 数组与列表

`UiCollectionEditor` 组合 `CollectionEditor.rml`、`CollectionItem.rml` 与已有基础值编辑器。公共层显示字段名、数量、折叠、索引、Add、Remove、Up、Down；第一项 Up 和最后一项 Down 禁用。空集合显示 `0`，null 集合显示 `null`，展开查看不修改数据。只读集合保留相同结构和折叠，禁用内部输入和修改按钮并移除动作绑定。公共层只消费 `UiCollectionItem` 的内容与调用方动作，不持有 World、资产数据或撤销栈。

主 Inspector 的一维数组／`List<T>`、DataAsset 属性和 Engine／Project Settings 的有序集合使用同一布局。元素复用 Text、Number、Boolean、Choice、Reference、Vector、Color；支持已有契约允许的嵌套集合。Inspector 继续使用已注册的数据描述符展开对象；Settings 使用带 Setting 标注的对象字段；DataAsset 自定义结构体按既有序列化成员递归展开，其他复杂对象保留 JSON 值编辑。

## 结构体分组

`UiStructEditor.Render(Id, Label, TypeName, Content, ToggleAction, Expanded, ReadOnly)` 使用 `StructEditor.rml` 和共享样式显示字段名、类型、展开箭头与成员区。公共控件不反射成员、不拥有数据或事务；Inspector、DataAsset 和 Settings 各自提供成员与折叠状态。嵌套结构体、列表元素及 Map 值继续组合相同控件，Vector/Color 等基础类型使用已有专用布局。

DataAsset 只展开原有 JSON 契约中的自定义值类型成员，遵守 JsonPropertyName、JsonInclude、JsonIgnore 和 getter/setter；不会因展开自动保存普通字段。只有 getter 的派生值保持禁用，并由当前结构体重新计算显示。成员编辑递归检查 DataProperty 约束与资产引用，走原有文档事务和显式 Save；结构体内 Map 仍按 key 确定排序。发布的 JSON Schema 同步列出成员、约束与 readOnly。Inspector 保持组件数据描述符规则；Settings 保持 Setting 标注规则，不扩展到任意 CLR 对象图。

折叠只改变本视图，不修改 revision、dirty、保存内容或 Undo 历史。只读结构体仍可展开查看，内部输入采用统一 disabled 样式；null 值保留原来的 null 表达，不隐式创建结构体。字段动作携带当前容器指纹，外层集合重排后拒绝过期输入。

## Map 与字典

`UiMapEditor.Render` / `UiMapEntry` 使用 `MapEditor.rml`、`MapEntry.rml` 和 `CommonControls.rcss` 呈现键、值、Add、Remove、折叠与可视顺序。控件只接收调用方提供的键/值布局和动作，不拥有 Map 数据、验证器、文档或 Undo 栈。运行时与持久数据仍是普通无序字典；首次显示按 key 的 Ordinal 顺序排列。上移/下移只修改当前编辑器实例的临时列表，不修改字典、不增加 revision/dirty，也不进入 Undo/Redo 或资产/Settings 序列化。只读 Map 禁用键和值输入以及 Add/Remove/Rename，但折叠与可视排序仍可用。

DataAsset 与 Settings 适配器分别通过原有文档事务和 `DevelopmentSettingsRegistry.TrySet` 处理真实键值变更。Add、Remove、Rename、值编辑每次是一次所属文档修改，Undo 可恢复；Map 指纹按键规范化，过期动作不会写到不同的键或值。字典字段的 JSON Schema 使用 object / `additionalProperties`；保存按 key 的 Ordinal 顺序序列化，和当前编辑器视图顺序无关。Map key 是 JSON object key，不是路径片段，因此 `/`、反斜杠等字符不需要转义成另一种业务 key。

null Map 显示为 `null`，空 Map 显示为 `0`；Add 将 null 初始化为 Map，并创建一个所属类型默认值。嵌套字典显示为子 Map，各自拥有独立的临时顺序和折叠状态。值复用 Boolean、Choice、Reference、Vector、Color 与文本编辑器；DataAsset 的 Vector2/3/4 与 Quaternion 按数组分量保存，Color 保持 RGBA 结构。Settings 不支持由 `[Setting]` 描述的类型以禁用 JSON 形式显示，不伪装成可编辑输入。DataAsset Map 内的数组/集合值遵守现有只读 JSON 复合值边界。

新增值沿用所属领域的默认值规则；null 集合通过 Add 创建并加入第一项，Undo 可恢复 null，与空数组区分。移除或上下移动只影响目标元素，每次操作是所属文档的一次事务，保存仍须显式执行。元素输入继续采用草稿、Enter／失焦提交、Esc 取消。集合动作携带渲染时的集合指纹，集合发生变化后拒绝旧索引事件，避免旧输入修改排序后的另一个元素；没有在资产中新增元素 ID 或改变存储格式。DataAsset 的集合引用逐项校验存在性与类型，`List<T>` schema 与数组一样发布 items。

DataAsset 按文档 revision 和类型描述符缓存 Properties 布局；折叠单独失效缓存，未修改的重复读取不克隆文档或重建所有元素。共享基础布局不持有消费者状态，两个集合的折叠、输入和权限互不影响。

## 提交、撤销与焦点

- 输入框中的中间文本仅是原生临时草稿；输入负号、小数点不会发作者变更。
- 单行输入按 Enter 或失焦时交给原有领域命令校验后提交一次；多行文本保留 Enter 换行，Ctrl+Enter 或失焦提交一次。无效值不改变文档；结束编辑后回显最后提交值，领域诊断仍可见。
- Esc 恢复进入字段时的值，丢弃当前草稿，不回滚此前已经成功提交的修改。
- 每个分量单独提交，一次 Undo 恢复这次提交；其他分量不被旧展示值覆盖。
- 点击另一个控件导致失焦时，提交可先执行，但面板刷新延后到鼠标释放，避免破坏本次点击。文本编辑期间不替换正在使用的输入控件。
- 主场景保留组件 revision 检查；在相邻分量的输入草稿保持焦点时，仅当当前字段仍等于控件捕获的原值，才会按最新文档 revision 提交，避免覆盖并发修改；Socket 分量绑定稳定 Socket ID，旧事件不能写入新选中的 Socket。Save、只读保护和 Runtime/Authoring 边界仍由原有领域持有。

输入通过 `UiValueInput` 声明 `Id`、`Value`、`CommitAction`、`Kind` 和 `ReadOnly`；原生 `data-value-editor` / `data-value-commit` 只负责草稿提交时机，校验、事务与保存不搬进 UI 基础层。编辑器和 Game 共用原生 `RmlValueEditor` 草稿适配器；通用 RML 宿主通过 `ui.value-*` 协调焦点和刷新，未改变原生 ABI。使用这些控件的宿主显式引用 `CommonControls.rcss`，稳定资源路径可由 `UiCommonControls.StyleSheetUri` 发现。

## 验证入口

`Tools/Viewport3DValidation` 只引用 Runtime，覆盖独立树/输入组合、转义、实例隔离与非法父引用。`Tools/EditorValidation` 验证适配后的领域语义与缓存，并检查 Range/Color/Reference 的共享标记。`Tools/McpUiValidation` 的 `--static-mesh-sockets-only`、`--skeleton-gizmo-only`、`--inspector-only` 和 `--ui-editor-only` 验证真实输入、焦点、草稿/提交/撤销及匹配 PNG；`--structs-only --render-offscreen` 验证结构体原生布局、实际输入、折叠、Undo、保存及 Settings，核对实际后端并保留 `Saved/Validation/Structures` 同帧证据。完整的 tools/resources/templates 目录仍保留于 `Saved/Validation/McpUiCatalogs`。界面和性能检查是不同证据，不能用语义测试代替原生可见/可点击验收。

Game 原生宿主同样使用 `RmlValueEditor` 与 `RmlColorPicker` 并将调用方提供的 commit action 交给 `IUiActionHandler`；基础控件不引用 Editor 或 World。`--game-ui-only --render-offscreen` 包含实际 Game 数值草稿、Enter 提交、色盘展开/选色/Apply、Cancel、Esc 与禁用状态验证，并保留同帧 JSON/PNG。

Game 的 UI 观察对 input/select/textarea 同样发布 `value`，可读取原生草稿；真实提交值仍由所属项目状态提供，不混同两种状态。

共享 RML 通过 EmbeddedResource 编入 SharpEngine.Runtime；修改这些布局后需要重建对应配置并重启宿主。共享 RCSS 仍由宿主显式加载，不隐式改写业务 RML 或源资产。

## 可编辑与只读的统一呈现

只读内容与 disabled 状态使用同一套禁用表现：保持对应类型的输入布局与当前值，统一灰色文字、背景、边框和轴标签；取消输入/焦点反馈，不参与 Tab 导航。Text、Number、Multiline、Choice、Boolean、Vector、Rotator 和 Transform 均遵守同一规则，混合 Vector 仅禁用对应分量。

`UiValueInput.ReadOnly` 生成 disabled / aria-disabled / tabindex=-1，并移除值修改绑定；Choice/Boolean 同样不公开改变动作。原生草稿适配器还会拒绝 disabled 目标事件。领域文档权限继续负责阻止直接语义命令修改，视觉状态不能替代权限校验。

统计、Asset ID、Stable ID 等永久只读值通过同一 `ReadOnlyField` 适配器生成通用禁用控件；Input 绑定的只读展示复用可写时相同的字段/二维 Scale/布尔结构。可写资源的统计字段仍禁用，其可写字段保持启用。树的选择和展开是查看操作，不因资产只读而关闭。
