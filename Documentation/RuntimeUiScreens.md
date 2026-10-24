# 运行时 UI：代码选择界面

游戏运行时显示的界面由**项目代码**决定，界面本身是**项目 UI 资产**。编辑器里改的 `.sui`，就是玩家看到的那个界面。

## 为什么要由代码选择

引擎不再硬编码某个示例文档。Runtime Module 在自己的 `Startup` 里声明要显示哪个资产：

```csharp
public sealed class GameUiModule : ISharpEngineModule
{
    private IUiRuntime? Ui;

    public void Startup(ModuleContext Context)
    {
        Ui = Context.Ui;
        if (Ui is null)
        {
            return;
        }

        var Result = Ui.ShowScreen("project://ui/main");
        if (!Result.Success)
        {
            Context.Logger.Warning($"[{Result.Code}] {Result.Message}");
        }
    }

    public void Shutdown()
    {
        Ui?.ClearScreen();
        Ui = null;
    }
}
```

这条路径上没有任何引擎示例文档参与，所以“编辑器和玩家看到的不是同一个界面”这种问题不会发生。

## 契约

`IUiRuntime` 新增：

| 成员 | 说明 |
| --- | --- |
| `ShowScreen(string AssetId)` | 解析项目资产并呈现；替换当前界面 |
| `ShowScreen(UiScreenRequest)` | 带本次使用的值与 UI 部件呈现；模板定义每次渲染 |
| `HandleScreenAction(string Action)` | 把一个原生 UI 动作路由到当前界面的部件，命中后重新组合并呈现 |
| `ClearScreen()` | 移除当前界面，UI 上下文保持存活 |
| `ActiveScreenId` | 当前呈现的资产 id，未呈现时为 `null`；瞬态状态，不写入资产 |

## 组合：一个定义，多个实例

`rml` 成员可以是完整的 `<rml>` 文档，也可以是 `<template>` 定义。模板是可复用的**定义**，本身不能被直接呈现；每次使用都要提供自己的值与 UI 部件：

```csharp
var Button = UiLayoutTemplate.Parse(UiDocumentAsset.Load(ButtonPath).Rml);

var Primary = new ReusableButton(Button, "primary", "Primary Action", "Play.tga", "#3aa78f");
var Secondary = new ReusableButton(Button, "secondary", "Secondary Action", "Style.tga", "#c98b3a");

Ui.ShowScreen(new UiScreenRequest(
    "project://ui/composed",
    new JsonObject { ["Title"] = "Composed Screen" },
    new Dictionary<string, IUiLayoutPart>
    {
        ["Primary"] = Primary,
        ["Secondary"] = Secondary,
    }));
```

屏幕模板用 `<ui-slot name="Primary" />` 与 `<ui-slot name="Secondary" />` 引用这两个部件。要点：

- **定义不可变**：`UiLayoutTemplate` 每次渲染都产出新节点，`${...}` 用本次的值展开，两个实例互不影响。
- **每实例状态**：上例的 `Clicks` 属于 `ReusableButton`（使用方），不属于定义。点击只改自己那一个。
- **包内私有样式**：`.sui` 的 `rcss` 保存私有 RCSS；`globalStyleSheets` 显式引用独立 `.sglobalcss` 资产 URI。组合收集子控件依赖，共享规则先于私有规则；相对图标等资源按各自源资产目录解析。旧 `styleSheets` 相对 RCSS 路径继续可读。

部件要实现 `IUiActionHandler`，并在 `action` 属性里带上自己的身份（`ui.button:${Id}:click`）。动作由原生 shell 收集，**在事件派发之外**（下一帧 `Update` 开头）交给托管侧；因此重新组合并替换文档不会在 RmlUi 派发中途卸载文档。

## 诊断

```
项目代码  Ui.ShowScreen("project://ui/main")
             ↓  托管侧解析资产（.sui → 内联 RML；.rml → 文件文本）
          RML 文本 + 权威源文件路径
             ↓  原生 API v19 SetUiDocument
          GameUiShell::SetDocument → RmlUi LoadDocumentFromMemory(sourceUrl)
             ↓  相对 <link href="Main.rcss"> 按源文件目录解析
          RmlUi 完成布局、级联、命中与绘制
```

关键点：**源路径必须一起传下去**。`.sui` 是 JSON，原生侧无法直接读；由托管侧读出 RML，再把包文件路径作为 source URL 交给 RmlUi，相对样式表链接才能落在正确目录。

`IUiScreenHost` 是这个契约的实现边界，由宿主提供；Editor 不实现它，因此 `ShowScreen` 在 Editor 内返回 `UI_SCREEN_UNAVAILABLE` 而不是静默成功。

## 宿主默认样式

RmlUi 的 `font-family` 默认值是空字符串，无法解析的字体会让文本宽度为 0——即使是合法的 flex 布局也会“看不见”。因此 `GameUiShell` 在载入时注入一个用户代理样式：

```html
<style>body { font-family: Segoe UI; }</style>
```

它插在文档自身样式**之前**，所以项目声明的任何属性都按顺序覆盖它。这相当于浏览器的默认样式表：提供保底，不争夺优先级。

## 验证

```powershell
dotnet run --project Tools/McpUiValidation/SharpEngine.McpUiValidation.csproj -c Development -- Development --game-ui-only
```

该模式启动真实的 Game 可执行文件和一个夹具项目，通过 Game 的 MCP 管道读取原生快照并注入真实指针输入。夹具内容：

- **组合定义** `Button.sui`（`<template>`）：图标 `<img>`、边框与 `box-shadow` 装饰、标签、计数与一个带 `action` 的按钮。
- **组合屏幕** `Composed.sui`（`<template>` + 两个 `<ui-slot>`），`styleSheets` 声明两个 RCSS。
- Runtime Module 用**同一个** `UiLayoutTemplate` 创建两个实例，分别传不同参数。

断言（16 条）覆盖：

- 呈现的是项目界面而非引擎示例；第二次 `ShowScreen` 替换了第一个；
- 模板值按本次传入展开（标题）；
- 两个 `<ui-slot>` 各自渲染出独立元素（`primary-*` / `secondary-*`）；
- 两个实例的标签参数不同、图标元素都存在、计数各自从 0 开始；
- 两个实例的按钮都有可命中的 bounds；
- 点击 `primary` 后 `primary-count` 变 1 而 `secondary-count` 保持 0；
- 再点一次 `primary` 累加到 2；
- 缺失资产被拒绝且不改变当前界面。

证据写入 `Saved/Validation/GameUi`：`game-ui.json`、点击后的 `game-ui-after-click.json`、`game-ui.png`。

## 边界

- 该模式验证 Windows + OpenGL 的 Game target，不覆盖其他渲染后端与发布包。
- 目前一个宿主只呈现一个界面（`ShowScreen` 替换而非叠加）；多屏/弹窗叠加尚未定义。
- `data-bind` 仍不驱动运行时数值：ViewModel 契约当前只发布 JSON Schema 元数据，没有建立到 RmlUi DataModel 的通路。
- `<ui-slot>` 之外的嵌套部件（部件里再挂部件）由使用方自行组合，目前没有递归深度限制测试。
