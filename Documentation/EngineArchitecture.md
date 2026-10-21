# SharpEngine 基础设施与组合层

SharpEngine 使用自下而上的金字塔结构：通用基础设施提供稳定能力，功能通过组合基础设施和少量定制形成更高层能力。依赖必须向下；基础层不得反向认识某个资产编辑器、项目业务或启动流程。

## 分层规则

- 通用层只表达自身领域。例如 3D 视口处理 UI 区域、内容挂接、观察相机、输入导航与帧提交；它不认识 WorldDocument、资产注册表、Prefab 或 Timeline。
- 适配层把具体内容连接到通用协议。例如 WorldViewportContent 把 World 通过 SceneRenderer 渲染成通用 ViewportFrame。
- 组合层组装已有能力。例如 WorldPreview 只创建 Viewport3D、挂接 WorldViewportContent，不重复实现输入、相机或场景渲染。
- 功能层仅补充内容与少量专用行为。Mesh/Skeleton/Timeline/Prefab 组合 WorldPreview；Material 直接挂接 MaterialPreviewContent，无需制造一个用不到的 World。
- 文件拆分不能替代上述依赖方向。通用接口不能仍要求传入 World、资产编辑器上下文或业务回调后才有意义。每个基础能力应能独立运行、被替换和单独验证。
- 分层不要求每个类型单建程序集。现有 Runtime/Editor 的程序集边界继续遵循引擎 Module 契约。

## 3D 视口的实际组合

```text
SharpEngine.Runtime.Rendering
  Camera3D / ViewportVertex / ViewportFrame
                 ↓
SharpEngine.Runtime.UI
  Viewport3D + OrbitCameraController
  IViewport3DContent / IViewport3DHost
                 ↓
SharpEngine.Editor
  WorldViewportContent → 现有 SceneRenderer
  WorldPreview = Viewport3D + WorldViewportContent
                 ↓
  Mesh / Skeleton / Timeline / Prefab 的预览

MaterialPreviewContent → 直接挂接同一个 Viewport3D
```

观察相机归 Viewport3D 所有。输入经导航控制器解释为 Orbit、Pan、Zoom 或 Focus，渲染器使用相机快照计算投影。挂接 World 不要求它拥有 Camera 实体，也不会修改已有 Camera、ActiveCameraId 或其他 World 记录。同一 World 可被多个视口从不同角度观察。

内容生命周期由调用者管理。AddContent/RemoveContent 只负责挂接与脱离，不自动修改、保存或销毁内容。Prefab 为 authoring 文档生成的内容副本属于上层预览组合，通用视口不负责克隆文档。主场景 authoring/runtime 相机的事务与 Undo 路径保持独立。

## 人工维护入口

- `Source/SharpEngine.Runtime/UI/Viewport3D/Viewport3D.cs`：通用 UI 和内容组合。
- `Source/SharpEngine.Runtime/UI/Viewport3D/OrbitCameraController.cs`：可直接调用的相机导航。
- `Source/SharpEngine.Runtime/Rendering/Camera3D.cs`、`ViewportFrame.cs`：观察与帧协议。
- `Source/SharpEngine.Editor/Editor/Viewport/WorldViewportContent.cs`：World 适配。
- `Source/SharpEngine.Editor/Editor/Preview/WorldPreview.cs`：World 预览的组合示例。
- `Source/Engine/UI/RmlViewport3DInput.h`：RmlUi 输入适配；捕获状态按宿主隔离。
- `Content/UI/Rml/Viewport3D.rcss`：不依赖资产编辑器选择器的视口样式。

验证包括：仅引用 Runtime 的无 World 三角形渲染与导航；同一 World 的多视口相机隔离；无 Camera 实体的 World 渲染；真实输入下的资产窗口隔离与不修改源数据。

## UI 功能的逻辑与布局组合

专用骨骼动画编辑器是进一步的组合示例：自身仅维护 `SkeletalAnimationEditorExtension.cs` 和 `SkeletalAnimationEditor.rml`。布局文件声明默认 Dock、面板与 `ui-slot` 引用；逻辑文件组织使用实例、展示数据并绑定事件，不拼接 Viewport3D 或 Timeline 的内部控件。

骨骼动画和状态机编辑器归属于 `SharpEngine.Animation.Editor`，由 `AnimationEditorModule` 注册；它引用下层 `SharpEngine.Timeline.Editor`，后者不引用或注册具体动画编辑器。动画事务和历史留在动画所属模块，通用 Timeline 的候选编辑不代替上层资产提交。状态图使用自己的布局与只读数据，不为信息面板创建无内容的 World。

`IUiLayoutPart` 是 UI 领域契约，不是万能功能容器。Viewport3D、TimelineView 提供自己的布局定义；UiLayoutTemplate 只承担 UI 值与子布局展开，不推进子功能时间或执行资产操作。共享定义不可被渲染修改；JsonObject 值、插槽实例、播放状态、相机和 Dock 都按使用实例隔离。

代码可在 `UiLayoutInstance` 中向自己的布局容器添加控件或挂接 `IUiLayoutPart`，并绑定事件；它保存每次使用的瞬态数据，不回写共享定义。父级查询不穿透子功能的内部布局。节点删除使旧句柄和事件失效，挂接关系不转移子实例所有权。用法与验证见 [运行实例 UI 组合](RuntimeUiComposition.md)。
