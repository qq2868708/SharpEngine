# Content Manager 右键菜单

Content Manager 在资产、文件夹和浏览区空白处提供自己的右键菜单。右击资产或文件夹先选择目标；右击空白处保留该浏览器已有选择，菜单上下文不绑定资产或文件夹。内置操作包括：

| 位置 | 操作 |
| --- | --- |
| 已注册资产 | Open、Browse Containing Folder、Delete Asset |
| 文件夹 | Open |
| 仅浏览区空白处 | Go Up、Refresh、Clear Filters、Tile View、List View |

没有可用资产打开器的文件禁用 Open。Go Up 在根目录禁用。Delete Asset 复用浏览器私有命令与既有资产删除流程：引擎、插件、只读资产及 Play 期间禁用，执行时还检查当前 World 和未保存的资产编辑器，确认后删除清单条目与源文件。浏览和视图操作是瞬态编辑器状态；只有既有 Save Layout 才保存浏览器配置，菜单本身不进入布局或资产。

## 所有权

- `SharpEngine.UI.UiMenu`：通用菜单布局，只接收项目 ID、标签、动作和显示状态；不依赖 World、AssetRegistry 或 Editor。
- `EditorPopupMenu`：每个窗口的菜单会话、动作绑定、单次执行和关闭。通过既有 Host API 发布到指定窗口。
- `ContentManagerCoordinator`：拥有四个浏览器视图及共用命令绑定，重建或释放时注销旧绑定。
- `ContentManagerView`：每个浏览器的 UI 使用实例，拥有模型引用、条目布局、自己的 `ContentManagerContextMenus` 及 scope 注册。
- `ContentManagerContextMenus`：由所属 ContentManagerView 创建，解释本实例收到的右键数据、选择目标并组装菜单。它通过 `IContentManagerMenuHost` 使用编辑器集成能力，不持有 EditorRuntime。
- `SharpEngine.UI.UiContextMenuRouter`：按 UI 布局的 scope 路径从内向外冒泡，某个处理器消费后停止。它不理解资产、文件夹或浏览器类型。
- `ContentManagerMenuHost`：EditorRuntime 的窄适配器，仅连接窗口位置、统一选择、刷新和资产打开服务。
- 原生 RmlUi：真实命中/输入路径、弹窗测量和窗口内定位、关闭通知；不判断资产或文件夹业务。

EditorRuntime 创建通用服务和 Content Manager 功能，只将右键传输消息交给通用路由器，不直接创建或调用 ContentManagerContextMenus。`SharpEngine.Assets.ContentManager` 保留浏览状态和私有删除命令定义；视图负责实际右键路由及弹窗组合。层级和场景右键暂时保留既有语义入口，复用弹窗宿主；这次没有宣称整个编辑器的全部输入都已迁移。

```text
命中元素 → 原生 UI 读取祖先 scope → UiContextMenuRouter
         → ContentManagerView 自己的处理器 → 自己的 ContextMenus
         → 通用 Popup → 领域命令
```

Content Manager 布局根声明 `data-ui-context-scope`，条目及空白区域通过 `data-ui-context` 提供所属功能定义的 JSON 字符串。原生层生成 `ui.context-menu:` 消息，其中 `route` 是由命中元素向根收集的 scope ID，`x/y` 是宿主坐标，`payload` 保持原样。业务类型只由视图解释。旧 `editor.context:` 语义调用由视图持有的兼容适配器处理，不参与真实 UI 命中证明。
项目视图通过 `EditorContext.ContextMenuRouter.Register` 接入，并在自身释放时 Dispose 注册句柄。内容管理器的核心命令由功能自身持有，不会因为某个项目 Module 触发布局重建而转交给该 Module 的卸载作用域。

## 命令与上下文

内置命令 ID：

```text
core.content-open
core.content-browse-parent
core.content-up
core.content-refresh
core.content-clear-filters
core.content-view-tile
core.content-view-list
```

所有菜单项最终通过所属 `EditorCommandRegistry` 执行，执行前重新检查 `CanExecute` 和注册实例。`asset-delete` 使用每个浏览器自己的私有注册表，不进入全局命令列表；弹窗绑定同时保存命令及所属注册表。项目现有 `EditorContextMenuKind.Asset` / `Folder` 注册继续有效；新增 `Content` 表示浏览区空白处。菜单中的 `EditorCommandContext` 固定右击时的 Selection、ContentPaneId 和 HostId；删除与扩展都读取该上下文，不使用后来改变的面板或全局选择。资产注册表重载同样重建视图并释放旧会话。

每次打开生成窗口内绑定的会话动作 `editor.menu:<sessionId>:<commandId>`。这些动作经原生输入或现有 MCP `editor.action` 进入同一处理器；它们不是可持久化的命令 ID。动作只执行一次，不能拿另一窗口的 token 执行。关闭菜单、替换布局/浏览器、移动或关闭面板、删除目标或卸载并重新注册命令，都会使旧绑定不可执行。

`content.describe` / `sharpengine://content` 保持根 schema v2，`contextMenus` schema v1 包含 `stateRevision` 与 `sessions`。会话公开 hostId、paneId、sessionId、targetKind、targetId、targetPath 和命令 ID；空白处 targetKind 为 None。新增 `contextRouting` schema v1 发布已注册 scope、stateRevision 和路由诊断。这些都是瞬态交互状态，不是 Authoring 文档版本。实际菜单的可见性、裁剪和点击目标以同帧 `ui.describe` 与渲染图像为准。

原生 ABI 版本不变；新增 `data-menu-session`、`data-anchor-x/y` 是 UI 内部挂接协议。原生宿主根据菜单实际布局和所属窗口尺寸定位，浮动 Dock 拥有自己的容器。Escape、外部点击和窗口失焦关闭菜单。

## 验证

```powershell
dotnet run --project Tools/EditorValidation -- D:/SharpEngine --content-only
dotnet run --project Tools/Viewport3DValidation
dotnet run --project Tools/McpUiValidation -- Debug --context-menus-only --render-backend=opengl --render-vsync=off
```

EditorValidation 验证目标捕获、多实例隔离、失效动作和扩展兼容；Viewport3DValidation 包含不依赖 Editor/World 的 UI 冒泡验证。MCP 右键专项需要当前源码对应的原生 Editor，通过真实坐标右击、菜单点击和 Escape 验证，并输出匹配帧证据。托管 RML 回调模拟不能替代原生验收。
未指定 `SHARPENGINE_VALIDATION_PROJECT` 时，`--content-only` 和 `--context-menus-only` 会通过正式模板与 ProjectBuild 创建自己的临时项目，不连接用户会话或注册系统文件关联。
旧的 `--content-only` 完整浏览器套件保留最后的双击用例；右键专项不包含该用例。2026-10-23 的完整套件运行在该旧用例处失败：MCP 逐帧输入间隔超过系统双击判定窗口，不能据右键专项通过宣称双击用例通过。

原生验证可使用当前标准 OpenGL-only 构建，不要求安装 Vulkan SDK：

```powershell
.\Tools\SharpEngineBuild.ps1 -Target Editor -Configuration Debug -RenderBackends OpenGL
```

2026-10-23 的工作区验证：EditorValidation 256 项、独立 UI/Viewport3D 验证 52 项、
SubsystemValidation 44 项、真实坐标右键专项 208 项通过。专项包含空白处视图/导航/筛选、
目标菜单范围、浮动窗口、Escape 和通过菜单打开资产编辑器；匹配帧保存在
`Saved/Validation/ContentManager`。该结果不包含上述旧双击时序用例。
