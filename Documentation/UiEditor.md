# RML UI 资产编辑器

项目 UI 画面的权威源格式是 `.sui`：一个版本化 JSON 资产包，`rml` 成员保存权威 RML 文本。一个画面就是一个资产、一个身份、一次原子保存。早期直接注册的 `.rml`（`RmlDocument`）仍可打开编辑，行为不变。

`UiAssetEditorInstance` 把 `UiDocument` 与 `RmlDocument` 都打开为独立的顶层资产编辑器根。RML 源文件是唯一权威数据；选择、级联样式、诊断和拖动事务属于该编辑器实例的 transient/derived 状态，不写入资产。

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
| Elements | 元素树与外观属性（id、`data-bind`、`data-command`、class），以及父级选择、上移/下移、缩进/反缩进、复制、删除 |
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
- 伪类、属性选择器、`+`/`~` 组合符和 `@media` 等规则不被猜测为等价规则，而是产生 `UI_STYLE_SELECTOR_UNSUPPORTED` 警告。设计画布因此不会静默展示错误的样式。
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

每个元素条目包含 `key`、`elementId`、`tag`、`previewElementId`、`depth`、`children`、`attributes` 和行内 `style`。选中元素还包含 `classes`、`text` 和 `computed`（每个生效声明的值及其来源：选择器文本或 `inline`）。

## 验证

```powershell
dotnet run --project Tools/McpUiValidation/SharpEngine.McpUiValidation.csproj -c Development -- Development --ui-editor-only
dotnet run --project Tools/EditorValidation/SharpEngine.EditorValidation.csproj -c Development -- .
```

`--ui-editor-only` 启动隔离的、真实渲染的 Editor，通过 `ui.pointer` 点击设计节点、拖动元素、撤销，再通过 `ui.key` 在检视器字段中输入并提交，最后检查源文件只在 Save 后变化。证据写入 `Saved/Validation/UiEditor`。

`--asset-editors-only` 需要 `SHARPENGINE_VALIDATION_PROJECT` 指向一个可构建的项目。

这些检查覆盖托管文档语义、真实 RmlUi 命中/捕获/焦点路径和渲染画面，不覆盖其他平台的输入后端、IMGUI 宿主或发布包。
