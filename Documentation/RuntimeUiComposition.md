# 在现有布局上用代码组合 UI

`UiLayoutTemplate` 是可共享的只读 RML 定义；`CreateInstance` 创建本次使用的 `UiLayoutInstance`。代码添加、删除、文本、属性和事件绑定都属于当前实例，不改写原始 RML，也不进入资产 Undo、dirty 或 Save。关闭实例后这些瞬态修改消失。

## 组合入口

已有布局提供父容器：

```xml
<template>
  <div id="panel"><span>${Caption}</span></div>
</template>
```

逻辑文件引用 `SharpEngine.UI` 和 `System.Text.Json.Nodes`，为每个使用方保留一个独立实例：

```csharp
using var Ui = UiLayoutTemplate.Parse(Source).CreateInstance("my-editor/preview");
var Panel = Ui.Find("panel")!;
var Button = new UiButton("extra", "增加");
var Label = new UiLabel("count", "0");
var Count = 0;
Button.Clicked += () =>
{
    Count++;
    Label.Text = Count.ToString();
};
Panel.Add(Button);
Panel.Add(Label);
var Input = new UiTextField("input");
Input.ValueChanged += Value => Label.Text = Value;
Panel.Add(Input);
var Markup = Ui.Render(new UiLayoutContext(640, 480), new JsonObject
{
    ["Caption"] = "现有布局",
});
```

宿主发布 `Markup` 到现有 RmlUi 宿主，并在自身的 `TryHandleAction` 中先调用 `Ui.TryHandleAction(Action)`；每次刷新从同一个实例 Render，不能每帧重新 CreateInstance。长期使用时将 Ui 保存为成员，在宿主 Dispose 时释放；示例中的 `using` 仅表达生命周期。输入、命中、焦点和事件仍经过原有 UI 路由。

`UiContainer`、`UiLabel`、`UiButton`、`UiTextField` 是当前提供的基础控件。`Text`、`Enabled`、`Visible` 和 `SetAttribute` 修改实例表现；RCSS 的 class/style 仍使用原有规则。文本与属性会转义，`${Literal}` 这样的代码传入值按字面量显示，不被当作模板绑定或 RML。

## 引用子功能

父容器用 `Panel.AddLayout("child", Part)` 挂接 `IUiLayoutPart`，例如 Viewport3D、TimelineView 或另一个 UiLayoutInstance。父布局的 `ui-slot` 仍可用于静态编排。子功能提供自己的布局与公共接口，父级 `Find` 不进入插槽、挂接子布局或 `ui-repeat` 内部；对子功能的定制应通过它公开的 API 完成。已知 UiLayoutInstance 引用环会被拒绝；自定义 IUiLayoutPart 也必须遵守无环契约。

挂接借用子实例，不取得其所有权。删除挂接或释放父实例不会 Dispose 被借用的子功能；组合方负责它自己拥有的功能实例。布局渲染不 Tick 子功能。

资产编辑器可通过 `AssetEditorLayoutDefinition.CreateInstance(EditorRootId)` 得到 `AssetEditorLayoutInstance`，再用 `GetPane(PaneId)` 操作自己的面板。它统一提供 `RenderPane`、`TryHandleAction`、`Describe` 和 `Dispose`。专用骨骼动画编辑器已使用此路径；父逻辑仍维护自己的一个布局文件，不复制 Viewport3D 或 Timeline 的内部布局。

## 身份、删除与观察

- 同一原生宿主内的 `InstanceId` 必须唯一。新增控件的逻辑 ID 在当前实例内唯一，原生 `ElementId` 自动添加实例作用域和生成序号。原布局已有 ID 保持不变，因此仍需遵守宿主内唯一约定。
- `Remove` 删除整个子树并使旧句柄失效；重新使用相同逻辑 ID 必须创建新控件。旧事件不能调用已删除控件的回调。不能删除实例 Root。
- `Clicked` / `ValueChanged` 不隐式覆盖已有的 `data-action` / `action` / `data-change-action`；确需替换时先显式清除对应属性。禁用或隐藏的控件及其后代不执行自己的回调。
- UI 实例在宿主 UI 线程上修改和渲染。Render 发布后，原生宿主按实例节点身份保留对应控件，包括输入框焦点和光标位置；移除节点后不再保留。
- `Describe` 返回 schemaVersion 1、instanceId、stateRevision、实例修改、控件身份及事件绑定状态；这是瞬态状态描述，没有保存/任意对象图恢复契约。最终可见性、裁剪、焦点与真实命中以现有 `ui.describe` 和匹配 frameId 的帧图像为准。

## 验证

`Tools/Viewport3DValidation/UiLayoutInstanceValidation.cs` 验证上述组合示例的控件、事件、转义、刷新、删除与实例隔离。真实 Editor 验证使用独立项目和进程：

```powershell
dotnet run --project Tools/McpUiValidation/SharpEngine.McpUiValidation.csproj -- Development --dynamic-ui-only
```

需先构建对应配置的 Editor。夹具逻辑见 `Tools/McpUiValidation/Fixtures/DynamicUiEditor.cs.txt`；测试通过真实 ui.pointer/ui.key 添加和点击控件、连续输入、在文本中部插入、刷新、第二实例隔离和删除，并检查源文件未变。证据输出位于 `Saved/Validation/DynamicUi/<run-id>/ui-evidence`。这证明当前基础控件与宿主路径可用，不表示覆盖 Unity/UE 的全部 UI 功能或发布包验证。
