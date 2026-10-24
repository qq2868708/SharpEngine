# RML UI 资产编辑器

固定布局、控件引用和可编辑参数由 RML 保存；私有 RCSS 保存在同一 `.sui` 的 `rcss` 成员中，共享规则使用独立 `.sglobalcss` 资产；C# 负责行为与运行时状态。

`globalStyleSheets` 是稳定资产 URI 数组，例如 `["project://ui/reuse-lab/controls-style"]`。目标必须为 `GlobalStyleSheet`，文件后缀 `.sglobalcss`，importer `RmlUi.GlobalStyleSheet`。不会因为文件存在或资产已注册而自动加载；只收集当前 UI 和其子定义显式声明的引用。

Styles 的 PRIVATE STYLES 编辑当前包私有声明，Add private rule 创建规则；修改与结构编辑共用 Undo/Redo、dirty 和 Save。EXPLICIT SHARED STYLES 可添加/移除 URI，Edit 打开独立共享样式资产。私有编辑不改写共享源，共享规则编辑使用独立文档历史。保存/回读同时保留 RML、私有 RCSS 原文和引用顺序。共享规则先于私有规则进入级联；选择器优先级仍按 RCSS 计算。旧 `styleSheets` 路径声明保持可读，新的独立共享资产使用显式 URI。

可组合的 `.sui` 使用 `<template values='{"Title":"Default"}'>`。父布局通过 `<ui-use id="First" asset="project://ui/reuse-lab/counter-card" values='{"Id":"first","Title":"Instance A"}' />` 声明一次固定使用，`class` 和 `style` 保存父级布局覆盖。引用通过资产清单解析，禁止引用环；暂不支持 repeat 中的 ui-use。动态内容仍可通过既有 ui-slot 与实例 API 提供，不会自动保存执行状态。

Design 展开子结构用于显示，点击子结构选择父文档中的 ui-use。Inspector 的 REUSABLE CONTROL 分组编辑资产 URI 和本次参数；Edit shared definition 打开原定义。TEMPLATE DEFAULTS 编辑模板默认参数，Add UI asset 输入已注册的 UiDocument URI 后插入新使用。父文档保存时只写引用与覆盖，不把展开结构烘焙进父 RML；子定义保持独立。新增、移动、尺寸、文本、class、enabled、参数等修改参与文档 Undo/Redo，Save 后重新打开恢复。

Styles 的 Edit 按钮打开已注册的共享 RCSS。StyleSheet 的 Rules & Preview 面板以规则和属性字段编辑现有声明；修改进入该 RCSS 文档自己的 Undo/Redo，Save 原子写回源文件。源区间编辑保留无关注释、选择器、声明和嵌套规则；当前结构化面板不编辑 @ 规则内部，Source 仍保留完整原文。保存后引用画布检测依赖变化并刷新，不把共享变化写成父元素的行内覆盖。

项目可注册 `EditorUiAssetEditor.Create(Context, IEditorUiPreview)`，运行预览与 Game 共用 `UiScreenComposer` 和项目行为代码，但创建独立实例。Design 为默认制作入口，Runtime Preview 为辅助 Tab；预览参数使用当前未保存源文本，不回写源。画布样式解析支持后代和子选择器，运行预览由原生 RmlUi 解析完整 RCSS。

项目 UI 画面的权威源格式是 `.sui`：一个版本化 JSON 资产包，`rml` 成员保存权威 RML 文本。一个画面就是一个资产、一个身份、一次原子保存。早期直接注册的 `.rml`（`RmlDocument`）仍可打开编辑，行为不变。

`UiAssetEditorInstance` 把 `UiDocument` 与 `RmlDocument` 都打开为独立的顶层资产编辑器根。`.sui` 的 RML、私有 RCSS 和共享引用构成权威数据；选择、级联结果、诊断和拖动过程是 transient/derived 状态，不保存到资产。

## 可视化布局与预览环境

右侧 **Layout** 页提供有限、可回读的布局编辑；Inspector 保留控件内容与详细样式字段。容器支持 Free、Horizontal、Vertical、Overlay。显式选择容器模式会在一个事务中更新该容器及直接子项的定位声明；转换到 Free 时采用当前原生测量保留子项位置和大小。未符合这些模式的手写布局显示为 Custom，不会因打开、选择或预览而被转换。选择一个预设才表示明确采用该模式。

子项的 Width / Height 可分别选择 Fixed（像素）、Content（auto）、Fill、Percent。Layout 同时显示作者值与原生计算后的像素尺寸。水平／纵向容器提供子项间距和主轴分布；子项可编辑交叉轴对齐。Free／Overlay 提供起点、居中、终点对齐以及四边距离。Slot spacing 对应子项 margin，Content padding 对应本节点内部 padding，两者不混用。

拖拽根据当前约束回写当前节点的 RML 内联样式，不修改共享 RCSS：

- 靠 right/bottom 定位时更新对应边距；百分比尺寸和偏移保留百分比。
- 双边拉伸且尺寸为 auto 时，调整拖动边的距离，保留拉伸语义。
- Overlay 拖拽编辑子项 margin，保留对齐；排列中的 Flow 控件也保持 Flow。
- 仅更新实际拖动的轴；内容尺寸在该轴被明确拉伸时转为固定尺寸。
- Flex 的 Fill 由父级分配空间，直接拉伸该轴会被拒绝；需编辑父级间距、主轴分布或先改变尺寸模式，避免写入一个被布局忽略的宽度。

拖动时 Layout／Inspector 的对应数值实时更新，松开只产生一次 Undo；Esc 取消。源的复杂表达式无法可靠换算时，拒绝该次拖动并保留原内容。已有 px、%、dp、em、rem、vw、vh、pt 值按对应参照换算；这不代表所有手写 RCSS 都能转换为有限预设。

Design 顶部可输入预览宽高，选择 720p／1080p／Wide／Portrait，并用 Fit／100% 调整显示缩放；右下角可拖动预览尺寸。宽高与缩放属于当前编辑器实例的临时环境，不改变资产 dirty、文档 revision 或 Undo，不写进 RML。缩放后的鼠标距离会换算回画布距离。预览工厂通过 `EditorUiPreviewSource.Width/Height` 获得这组尺寸。

布局意图保存为 `data-ui-layout="free|horizontal|vertical|overlay"` 与 `data-ui-align-x/y="start|center|end"`，全部为字面作者元数据；未知值拒绝加载。具体效果由同时保存的 RCSS 声明执行，Game 不需要另一套布局解释器。若手写样式与预设意图不一致，编辑器回到 Custom，而不会覆盖源。

当前有限编辑范围不包含可视化媒体查询、任意变换和平台安全区域。Design 仍与宿主共享 RmlUi 上下文；百分比与父容器布局可随预览宽高变化，`vw/vh` 和 `@media` 的完整屏幕预览一致性仍需独立上下文接入，不能把调整画布宽高当成这部分已完成。C# `UiScreenRequest` 的默认 1280×720 仍是组合调用参数，不是此处预览环境的持久化设置；有限布局直接使用 RML/RCSS 适配，避免依赖它生成固定几何。

Sandbox 的 `project://ui/controls-lab/responsive` 展示 Free 的右侧百分比定位／双边拉伸、Overlay 居中留白、Horizontal 的 Fixed／Content／Fill，以及 Vertical 页面。示例与默认启动界面隔离。

## 控件树与基础控件

Controls 面板提供 10 类基础控件：Container、Horizontal Layout、Vertical Layout、Text、Image、Button、Text Input、Checkbox、Slider、Progress Bar。先在 Widget Tree 选中目标容器，再点击 Controls 中的类型；选中的是叶子时插入到它的父级。创建、显示名称、专属属性和布局修改共用文档事务，Save 才写回源文件。重复创建生成独立节点与唯一 id。

显示名称保存在 RML 的 `data-ui-name` 属性中，和行为绑定使用的 `id` 分开；`ui-use`、`ui-if`、`ui-repeat` 和 `ui-slot` 也允许该作者元数据，显示名称是字面文本，不参与 `${...}` 绑定，组合求值不会把它当作行为参数。未命名的旧节点回退到文字摘要、id、class 或类型名，打开旧资产不会自动改写源。树每行以名称为主，第二行显示类型与 id；匿名节点可按结构位置选中，不被误标为无效。`ui-use` 标为 Reusable Control / REF，搜索包含引用 URI，树中不展开或保存引用定义的派生节点。

每行名称前显示控件类型图标：蓝色表示布局，绿色表示内容，金色表示交互，紫色表示引用与插槽。每层缩进 18px，并显示纵向层级引导线；展开箭头与选择区域分开，点击图标或名称均选中该节点。行底色铺满面板宽度，名称过长时省略并可悬停查看完整名称；窄面板的工具栏自动换行，列表独立滚动，极深层级可横向滚动。

Controls 按面板宽度换行排列按钮，并在高度不足时独立滚动。控件树与 Controls 的原生滚动条都有明确尺寸，防止滚动条自动宽度占满内容区。

每个有子节点的分支可折叠，Expand / Collapse 操作整棵树。搜索按名称、控件类型、id、class 和引用 URI 匹配，保留命中节点的祖先路径。点击 Design 节点会选中同一源节点、展开祖先，并把选中行滚入树的可视范围；当前筛选会隐藏它时会清空筛选。折叠、搜索、选择均为当前文档实例的临时视图状态，不改变 dirty、document revision 或 Undo 历史，也不写进 RML。

Inspector 顶部提供 Display name 和类型专属属性：布局间距、文字内容、图片源、按钮文字、文本框初始文字、复选框初始选中与提交值、滑条 value/min/max/step，以及进度条 value/max。布尔属性按 RmlUi 的属性存在语义保存；取消选中会删除 `checked`，不会写入仍被视为选中的 `checked="false"`。数值要求有限、step 为正、最大值大于最小值、value 在范围内；非法修改不产生部分写入或新 revision，已有非法源会产生 `UI_CONTROL_VALUE_INVALID` 并阻止保存。模板占位值在组合时求值。

基础控件的默认外观位于 `engine://ui/basic-controls-style`（`Content/UI/Controls/BasicControls.sglobalcss`）。在 `.sui` 创建首个基础控件时，该引用与新节点在同一事务内加入 `globalStyleSheets`；后续创建去重，撤销首个创建也撤销本次新增的引用。每个控件使用 `se-control` 与 `se-control-<type>` class，项目可用包内 RCSS 覆盖。共享样式不会注入未引用它的 Game 界面。旧式 `.rml` 仍沿用自己显式链接的样式，不引入包字段。

Design 中的输入、复选框、滑条、进度和图片使用原生元素展示，外围选中框拥有设计手势；点击用于选中，不会把运行时输入当作作者编辑。图片的文档相对路径按 `.sui` 所在目录解析，预览不会把解析出的本机路径写回源。设计态可根据已保存的 `checked` 和 `disabled` 计算对应伪类；hover/active/focus 等运行时状态仍交由实际 RmlUi 运行预览处理。

## 包格式

```json
{
  "$schema": "https://sharpengine.dev/schemas/ui-document-v1.json",
  "schemaVersion": 1,
  "name": "Main UI",
  "rml": "<rml><head><link type=\"text/rcss\" href=\"Main.rcss\" /></head><body>...</body></rml>"
}
```

- `schemaVersion` 目前只接受 `1`；其他版本报 `SEUI_SCHEMA_UNSUPPORTED`。
- 顶层字段是封闭集合：多字段报 `SEUI_FIELD_UNKNOWN`，缺字段或空值报 `SEUI_FIELD_INVALID`。
- `rml` 必须是合法 XML、根为 `<rml>`、且恰有一个 `<body>`，否则报 `SEUI_RML_INVALID`；非法 JSON 报 `SEUI_JSON_INVALID`。
- `rml` 里的 `<link type="text/rcss">` 按包文件所在目录解析，因此 `Main.rcss` 与 `Main.sui` 同目录即可。
- 读取与写入都由 `SharpEngine.Assets.UiDocumentAsset` 负责，manifest 的 `type` 为 `UiDocument`，`importer` 为 `SharpEngine.UiDocumentJson`。

后续版本可以增加成员（例如独立 parts 或 design 尺寸），但读取当前版本时不会猜测未知字段。RML 不额外注册成第二个资产，避免一个画面出现两个身份。

## 面板

| 面板 | 内容 |
| --- | --- |
| Design | 1280×720 的设计画布，按已链接 RCSS 的级联结果渲染每个元素，并提供选中、拖动、缩放手柄 |
| Controls | 创建布局、内容和交互类的 10 类基础控件 |
| Widget Tree | 显示名称、类型和 id，支持折叠、搜索、祖先路径及画布选中联动 |
| Inspector | 选中元素的属性、class 建议和按 Layout / Flex / Spacing / Text / Appearance 分组的样式字段 |
| Styles | 已链接 RCSS 的规则、选择器、优先级和声明 |
| Diagnostics | 文档、样式表和选择器求值的诊断 |
| Source | 当前文件文本（只读视图） |

## 文档模型

`UiEditorDocument` 保存一个 `XDocument`。选择键在元素有 id 时为 `#id`，否则为 `path:` 加 body 子元素索引路径，例如 `path:0-2`；因此元素身份不依赖内存地址，也不依赖无关节点的增删。

每次编辑都经过同一提交路径：先校验，再比较规范化 XML，有变化才写入一条 Undo 记录并递增 document revision。Undo/Redo 保存整份规范化 XML 和选择键，因此撤销后选择会回到编辑时的元素。

直接操作使用单独的事务：

```csharp
Document.Select("#headline");
Document.BeginGesture("move", X, Y, Width, Height, out var Message);
Document.ApplyGestureMove("move", DeltaX, DeltaY, Snap: false, out Message);
Document.CommitGesture();   // 整次拖动只产生一条 Undo 记录
```

`CancelGesture` 恢复手势开始时的文档和选择，不产生新增历史。原生命中测试在 `Tools/McpUiValidation/UiEditorUiValidation.cs` 中通过真实 `ui.pointer` / `ui.key` 完成。

## 样式求值

`UiEditorStyleSheet` 只服务设计画布预览，不替代 RmlUi：

- 按 RmlUi 的同一语义读取**两种**样式来源：`<link type="text/rcss">`、`<link type="text/css">` 和内联 `<style>` 块，全部按**文档出现顺序**进入同一个级联列表。RmlUi 内部就是把它们推进同一个 `rcss` 列表后顺序加载的。
- 解析支持标签、`#id`、`.class` 以及它们的组合、后代和 `>` 子选择器。
- 按选择器优先级加出现顺序计算级联，行内 `style` 属性最后覆盖；`!important` 声明优先于普通声明。
- 除基于源属性的 `:checked` / `:disabled` 之外的伪类、属性选择器、`+`/`~` 组合符和 `@media` 等规则不被猜测为等价规则，而是产生 `UI_STYLE_SELECTOR_UNSUPPORTED` 警告。设计画布因此不会静默展示错误的样式。
- 位于 `<head>` 之外的 `<style>` 会被 RmlUi 忽略，画布同样忽略，但会产生 `UI_STYLE_OUTSIDE_HEAD` 警告而不是静默丢弃。
- `styleSheet.files` 只列外部文件；`styleSheet.inlineBlocks` 记录参与级联的内联块数量。Styles 面板同时显示两者，因此“只有内联样式”的画面不会被误报成没有样式。

预览把生效声明扁平化为每个设计节点的一个 `style` 属性提交给 RmlUi，所以原生 hit-test、裁剪和 z-order 仍然是真实路径。

## 原生拖动

拖动由原生 `RmlUiShell` 处理指针，避免每次指针移动都替换标记：

1. 设计节点带 `data-ui-gesture="move"`，命中后原生记录起始屏幕坐标并捕获指针。
2. 移动超过双击阈值后派发 `editor.ui-drag-start:<handle>|<nodeKey>`，随后每次位置变化派发 `editor.ui-drag:<handle>:<dx>:<dy>:snap|free`。
3. 编辑器把偏移换算成 RML 声明（Flow 用 `margin-left`/`margin-top`，Absolute 用 `left`/`top`），并通过可选的 `IAssetEditorLivePreview` 把 `AssetEditorLiveProperty` 直接写入原生元素属性。
4. 释放派发 `editor.ui-drag-end`，整次拖动作为一条文档事务提交；Esc 或捕获丢失派发 `editor.ui-drag-cancel`，文档回到提交状态。

手势期间宿主不重发标记，因此被捕获的元素、焦点和指针身份保持有效。提交或取消后宿主强制重发一次标记，让检视器、诊断和画布与新文档一致。

原生 API 版本 18 追加 `SetHostUiProperty(hostId, elementId, name, value)`，只修改元素属性而不替换标记。

## 保存和诊断

- 重复 id 是 error，会阻止 Save；缺失 id 是 warning，元素仍可通过树位置选中并被赋予稳定 id。
- 叶子标签包含子元素、未知布局标签会产生 warning。
- 链接样式表缺失、越界或无法读取会产生 error 或指向具体文件的诊断。
- Save 前重新计算诊断，并校验打开时的源文件哈希；文件在编辑器之外被修改时拒绝覆盖。写入使用同目录临时文件加原子替换。
- `.sui` 保存时写出临时文件后立即用 `UiDocumentAsset` 重新解析，并比对 RML 规范化结果；包外字段集合不变，因此不会因为编辑器写入丢失 envelope。`.rml` 资产仍写 XML 本身。
- Source 面板显示的是 Save 会写出的内容（含未保存修改），不是磁盘上的旧字节，因此 `.sui` 会显示 JSON envelope 与内联 RML。

## 状态快照

`editor.describe` 的 `assetTools.editors[].state` 中，UI 编辑器报告 `schemaVersion: 3`：

```json
{
  "schemaVersion": 3,
  "assetId": "project://ui/main",
  "type": "UiDocument",
  "format": "package",
  "packageName": "Main UI",
  "readOnly": false,
  "dirty": false,
  "revision": 4,
  "canUndo": true,
  "canRedo": false,
  "gesturing": false,
  "selectedKey": "#headline",
  "selectedPreviewElementId": "ui-design-node-0-0",
  "viewModelContract": "validation.main@1",
  "elementCount": 3,
  "design": { "width": 1280, "height": 720 },
  "elements": [],
  "styleSheet": { "files": ["Main.rcss"], "inlineBlocks": 0, "ruleCount": 3, "classes": [], "tags": [] },
  "diagnostics": []
}
```

`format` 为 `package`（`.sui`）或 `rml`（旧式裸 RML 资产），`packageName` 是包内 `name`；裸 RML 资产的 `packageName` 等于资产名。

状态新增 `controls`（可创建类型目录）、`tree.search/collapsedKeys/visibleKeys`（临时视图）；选中节点增加 `controlProperties`（name/label/kind/value）。每个元素条目新增 `displayName`、`controlType`、`controlLabel`、`expanded`，并包含 `key`、`elementId`、`tag`、`previewElementId`、`depth`、`children`、`attributes` 和行内 `style`。选中元素还包含 `classes`、`text` 和 `computed`（每个生效声明的值及其来源：选择器文本或 `inline`）。

## 验证

```powershell
dotnet run --project Tools/McpUiValidation/SharpEngine.McpUiValidation.csproj -c Development -- Development --ui-editor-only
dotnet run --project Tools/EditorValidation/SharpEngine.EditorValidation.csproj -c Development -- .
```

`--ui-editor-only` 启动隔离的、真实渲染的 Editor，通过 `ui.pointer` 点击设计节点、拖动元素、撤销，再通过 `ui.key` 在检视器字段中输入并提交，最后检查源文件只在 Save 后变化。证据写入 `Saved/Validation/UiEditor`。

`--asset-editors-only` 需要 `SHARPENGINE_VALIDATION_PROJECT` 指向一个可构建的项目。

这些检查覆盖托管文档语义、真实 RmlUi 命中/捕获/焦点路径和渲染画面，不覆盖其他平台的输入后端、IMGUI 宿主或发布包。
