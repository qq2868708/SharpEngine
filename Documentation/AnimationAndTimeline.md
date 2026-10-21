# Playable、通用时间轴与骨骼动画

`SharpEngine.Playables` 是跨领域的底层运行时协议。`IPlayable` 只有稳定的 `PlayableId`、时长、播放状态、寻址、推进和评估操作；它不依赖动画、骨骼或任何具体输出类型。节点把结果写入 `IPlayableOutputSink`，输出使用 `TargetId + PropertyPath + JsonNode` 表示，因此运行时句柄、渲染资源和委托不会进入资产。`PlayableGraph` 负责组合任意 Playable，并提供 Play/Pause/Stop/Seek、固定步推进和逐帧评估。音频、对白、事件、粒子参数等未来系统可以直接实现 `IPlayable`，或者复用 `PlayableBase` 的状态机。

骨骼动画资产是 `Content/Assets/Animations/RobotWalk.skeletalanimation.json`，由 `SkeletalAnimationAsset` 加载。资产保存骨骼稳定 ID、父子引用、绑定姿态、按骨骼排序的关键帧、时长、采样率和 Notify 事件；加载时会校验重复 ID、父引用、有限数值、四元数、关键帧单调性、Notify 唯一性和时间范围。`Sample` 产生派生姿态，`SkeletalAnimationPlayable` 将姿态作为 `skeleton.pose` 输出。资产通过普通 `assets.json` 注册，加载入口是：

```csharp
var Animation = AssetContent.LoadSkeletalAnimation(
    Registry, "engine://animations/robot-walk");
var Playable = new SkeletalAnimationPlayable(
    "robot-walk-instance", Animation, "robot");
```

`SharpEngine.Timeline` 是通用编排与编辑层，不属于 Animation。时间轴资产由 `TimelineDocument` 表示，轨道和片段只保存稳定 ID、目标、时间范围、速度、源起点和循环模式，不绑定骨骼、音频或其他领域类型。`TimelinePlayable` 通过 `IPlayableResolver` 在运行时解析片段，并把片段局部时间映射到任意 `IPlayable`。示例资产是 `Content/Assets/Timelines/RobotWalk.timeline.json`；它使用骨骼动画只是因为当前示例需要一个具体的 Playable。

Timeline 是 Engine 内置功能，由 `Source/SharpEngine.Timeline.Runtime/SharpEngine.Timeline.Runtime.csproj`
和 `Source/SharpEngine.Timeline.Editor/SharpEngine.Timeline.Editor.csproj` 分别提供 Runtime
和 Editor Module。Editor Module 依赖 Runtime Module；Game Target 不加载 Editor Module。

`TimelineView` 是 renderer-neutral 的视图层。`BuildSnapshot(width, height)` 返回轨道、片段矩形、标尺刻度和 playhead 的语义快照；`BuildMarkup` 返回可嵌入 RmlUi 的标记。`TimelineViewState` 只保存缩放、滚动、当前时间和选择，是瞬态编辑器状态，不会写入时间轴资产。

动画资产编辑器是按 `AssetId` 创建的顶层编辑器根，拥有自己的 Menu、Toolbar 和 Dock 布局。
Toolbar 提供 Play、Pause、Stop、定位、Add Notify、Undo、Redo 和 Save。它会从当前
skeletal-animation Clip 采样骨骼姿态，通过 `SkeletonInstance` 计算 CPU 蒙皮顶点，并把带深度的
三角形提交到该资产编辑器 Host 的 3D viewport；Preview 与 Timeline 默认同时可见，采样时间和
进度显示在 Preview 侧栏。
预览结束时会明确显示完成状态。时间轴修改由
`TimelineAuthoringSession` 管理，使用递增 `DocumentRevision`、dirty 状态和可逆历史；
Save 先重新解析序列化内容，再用临时文件原子替换项目源资产。引擎来源资产保持只读，
因此 Save 在项目 Timeline 资产上才可用。

在 Content Manager 中单击资源只改变选择状态；双击 `SkeletalAnimation` 资源会通过
`EditorRuntime.OpenAsset` 打开顶层动画资产编辑器。同一 `AssetId` 重复打开时聚焦原实例；不同
资产各自拥有独立实例，即使它们共享 `skeletal-animation-editor` 的 `EditorId`。Preview 是不可游离的主
Pane，Timeline 与 Details 是同一 `EditorRootId` 下的普通 Pane，可以在该根内停靠或成为游离
Tab，但不能跨到主编辑器或另一个资产编辑器。每个实例拥有自己的播放头、Notify 轨道、
Undo/Redo 历史和 dirty 状态。
`EditorAssetOpenerRegistry` 先匹配具体 `AssetId`，再匹配 `AssetType`，并按优先级解析；
项目或插件可注册高优先级打开器覆写引擎默认打开器，释放注册句柄后恢复原打开器。

`SkeletonAsset` schema v2 的显式可空 `previewMeshId` 是动画编辑器的默认 Preview Mesh。
schema v1 读取后迁移为 null。一个 Skeleton 可以被多个 Skeletal Mesh 共享，因此编辑器不再
遍历资产注册表并选择第一个匹配项。Details 中的临时 Mesh 选择只改变当前编辑器实例的瞬态
预览状态，不修改动画或 Skeleton；候选 Mesh 仍必须通过
`SkeletalAnimationCompatibility.Validate`。

骨骼动画只位于 `SharpEngine.Animation`：`SkeletalAnimationAsset` 是骨骼关键帧资产，`SkeletalAnimationPlayable` 是把该资产适配到通用 Playable 协议的领域实现。它输出 `skeleton.pose`，并不改变 Playable 或 Timeline 的通用契约。

`Tools/TimelineValidation` 覆盖资产清单加载、确定性 round-trip、姿态插值、循环采样、Playable 输出、骨骼动画时间轴组合、非动画事件 Playable 的时间轴编排、视图标尺/playhead 和语义片段选择：

```powershell
dotnet run --project Tools/TimelineValidation/SharpEngine.TimelineValidation.csproj -- D:\SharpEngine
```

## Animator 组件与骨骼帧数据

`engine.animator`（schemaVersion 3，`AnimatorComponent`）使用显式的可空 `Target` 引用驱动
`SkeletalMeshComponent`。目标以稳定组件 ID 保存，支持父实体控制子实体模型、切换目标、
Prefab 内部引用重映射和删除时置空。Animator 每实体最多一个，不自动添加骨骼网格。
版本 1 和版本 2 场景、Prefab 在加载时迁移为版本 3，将原同实体骨骼网格记录为 `Target`；
不完整的旧状态、缺失旧目标和未来版本会拒绝加载。

权威字段为 `Target`（可空的骨骼网格组件 ID）、`Clip`（可空的 SkeletalAnimation 资产 ID）、
`Time`（非负秒数）、`Speed`（非负倍率，默认 1）、`Playing`（默认 true）和 `Loop`（默认 true）。
字段显式保存，支持 Inspector/MCP patch、事务、Undo/Redo 和运行时重建。
暂停使用 `Playing=false`；定位使用 `Time`；停止在同一个 patch 设置 `Playing=false, Time=0`；
从头播放设置 `Playing=true, Time=0`。一次播放到末尾后 `Playing=false`，保留末帧；
零速率保留播放状态。循环推进与 Seek 由通用 Playable 归一化，组件 `Time` 本身要求非负。

运行时更新链路为：

1. 所有组件完成 `Update` 后进入通用 `OnEvaluate(DeltaSeconds)` 阶段。
   全部 Evaluate 完成后，世界统一调用 Animator 管理的 `SkeletalAnimationPlayable`，复用 Play/Pause/Seek/Advance/Evaluate，
   将采样得到的局部姿态写给 `Target`，提取根运动并提交实体/Movement 位移，然后才执行 LateUpdate 相机。
2. 全部 `LateUpdate` 完成后，在发布边界执行零增量求值，协调当帧定位、切换目标等修改。
   已采样且未变化的时间复用姿态。新加入的 Animator 在本帧准备初始姿态，从下个 Tick 开始推进。
3. 通用 `OnPrepareRender()` 阶段由骨骼网格组件更新自己持有的 `SkeletonInstance`：
   合并局部姿态与绑定姿态，计算层级模型矩阵、蒙皮矩阵和当前 CPU 蒙皮顶点。
4. `RuntimeWorld.Frame` 将权威场景与不可变的派生组件数据按同一 revision 一起发布。
   Editor Play、Step、运行时 Prefab 实例化与 Game Session 将整帧交给 `WorldDocument`。
   绘制和拾取只读取准备好的几何，不查询 Animator，也不采样动画或计算骨骼。

组件、实体或目标网格停用时冻结 playhead；解除目标、停用或删除 Animator 会释放旧姿态输入。
启用且绑定到目标的 Animator 使用空 Clip 时明确选择绑定姿态；无活动输入时，骨骼网格保留
`Animation/AnimationTime/LoopAnimation` 字段的旧采样能力。同一目标有多个活动来源时，
使用绑定姿态并报告 `SKELETON_POSE_CONFLICT`，避免执行顺序决定最终姿态。
绑定校验 skeleton ID 和骨骼 ID/父子关系，支持兼容的局部骨骼动画。

`RuntimeWorld` 的可选第三参数为通用 `AssetRegistry` 服务，Editor Play 和 Game Session 均提供。
`new AnimationSnapshot(Runtime.Frame).ToJson()` 只投影已提交数据：根 schemaVersion 为 2，
包含 revision、animators 和 skeletons；各条目带 schemaVersion 1。Animator 条目记录目标、clip、
播放状态、诊断、时长、采样时间和采样次数；骨骼条目记录局部姿态、模型矩阵与蒙皮矩阵。
Editor `components.describe.animation` 暴露同一观察。观察不会加载资产、推进或采样，旧帧保持冻结。
Playable、骨骼实例和资产绑定缓存是可重建的瞬态对象；局部姿态、矩阵与蒙皮顶点是只读派生结果，
均不保存到权威场景。现有 `SceneRenderer.InvalidateAssets` 同时使组件的资产绑定失效。

Authoring 预览通过显式 `SceneRenderPreparation.Prepare(World, Assets)` 在绘制前准备，
不推进时间。运行时文档已有提交帧，该调用直接复用。独立调用渲染器且未准备帧时显示绑定几何。
本阶段在兼容单 Clip 播放的基础上增加最小动画状态机。状态机资产使用
`.sanimationstatemachine` 文本格式，保存参数默认值、状态顺序、Clip 引用和过渡条件；
当前内置 locomotion 图包含 `idle`、`walk`、`run`、`jump` 四个状态。`AnimatorComponent`
可以通过 `Controller` 绑定状态机，通过 `MovementSource` 和
`MovementSpeedParameter` 把 Movement 的意图速度写入数值参数，避免根运动的实际速度反馈。状态切换在全部 Movement
求值完成后、LateUpdate 之前执行，组件 ID 顺序不会改变决策结果。状态图会在加载时解析
全部 Clip 依赖，任一依赖无效则整张图报告绑定失败；切换采用直接采样，当前不做姿态混合；根运动使用各 Clip 的资产设置。状态机控制器的运行时状态（当前状态、过渡 ID、参数和播放时间）进入
Animator 帧观察，图资产和组件状态均可从最新提交快照重建。

`engine://animation-state-machines/uefn-mannequin-locomotion` 是引擎提供的只读
builtin/preset 资产，依赖 UEFN mannequin 骨骼和四个 Clip。Content Manager 双击
`AnimationStateMachine` 资产时沿用 `EditorAssetOpenerRegistry` 与
`EditorRuntime.OpenAsset` 的统一打开入口，由 `engine.animation-state-machine-editor`
在独立顶层动画资产编辑器中显示状态和过渡图；它复用同一套资产打开和 editor-root 路由。

验证入口：

```powershell
dotnet run --project Tools/AnimationValidation/SharpEngine.AnimationValidation.csproj -- D:\SharpEngine
```

覆盖组件序列化、v1/v2 到 v3 迁移、事务与重建、Playable 语义、显式目标生命周期、
Prefab 重映射、状态机依赖与条件、Movement 驱动的 Idle/Walk 切换、帧末姿态与几何、
冻结快照、状态机引擎预设依赖和统一编辑器打开路由，以及绘制/拾取/观察不采样。
帧几何验证不代表原生窗口或打包运行验收。

## 动画资产 Root Motion

SkeletalAnimation schema v2 显式保存 `rootMotion: { enabled, boneId }`；v1 读取时迁移为关闭，并在唯一根骨存在时记录其 ID。`boneId` 必须引用无父骨，启用时不可为空；未知字段、未来版本和无效根骨拒绝加载。`WithRootMotion`、`WithNotifies` 和重定向保留源轨道及根设置，Blender 重导入脚本保留已有设置。

项目动画的 Details 提供 Root Motion 按钮，工具栏和语义命令 `root-motion` 使用同一修改入口。根设置与 Notify 共用 JsonAssetAuthoringDocument 的 Undo/Redo、documentRevision、dirty 和显式 Save；外部源 hash 冲突拒绝覆盖。保存原子替换后使动画缓存失效。Engine 来源资产仍只读，需使用项目动画变体进行编辑。

`Sample` / `SampleLooped` 始终返回源姿态；`SamplePreview` 与 Playable 输出遵守开关：关闭时根平移/旋转恢复 bind，开启时资产预览显示源根轨迹。Runtime Animator 开启时计算连续根增量并驱动 MovementSource 所在实体（未指定时是 Animator 实体），模型必须属于目标子树；在模型输出中剔除根运动以避免重复位移。模型相对变换及父实体变换参与换算。Player 的 FacingMesh 转向 WASD 方向，根位移沿模型当前朝向应用，保留源距离与竖直轨迹，同时保留由镜头控制的根实体朝向；没有水平意图时不再前进。普通 Animator 不执行这项 Player 定向策略。跳跃的竖直根轨迹同样驱动实体。

循环支持一次跨越多个周期；非循环在终点停止，暂停与 Seek 不移动实体。`RootMotionSampler.Delta` 把循环展开为一条绝对曲线：从周期中段开始并跨过边界的一帧，按“剩余周期 + 新部分周期”的顺序合成，因此与逐帧累积一致；旧实现只取末帧部分周期，跨边界那一帧会丢失前半段位移。同一实体有多个驱动源时报告 `ROOT_MOTION_CONFLICT`，不按执行顺序选择。Animator 组件 schema 仍为 v3，帧 schema v4 新增 `rootMotion`（enabled、boneId、targetEntityId、applied、redirectedByPlayer、consumedBy、code、worldTranslation）。状态机速度参数使用 Movement 意图速度，实际 LinearVelocity 从已提交位移计算。

根运动还可以交给引擎的移动组合点：实现了 `IMovementAuthority` 的组件接管实体后，该帧不再走内建写入，animator 帧改为 `ROOT_MOTION_CONSUMED` 并记录 `consumedBy`。详见 [控制与移动专题](ControlAndMovement.md) 的 Character Movement 插件一节。

`Tools/AnimationValidation/RootMotionValidation.cs` 验证源轨道保留、v1/v2、根骨拒绝、平移与旋转多周期一致性、跨边界合成、父子变换、暂停/Seek、缓存重载和多源冲突。`Tools/McpUiValidation --root-motion-only` 在独立项目副本中验证坐标开关、共享 Undo/Redo、显式 Save 和 Runtime 开关结果。

## 资产预览布局与时间轴事件

Mesh、Skeleton、动画编辑器各自持有临时 AssetPreviewWorld，包含内容、Sky Light 和方向光；WorldPreview 使用通用 Viewport3D，并挂接 WorldViewportContent。观察相机属于 Viewport3D，不注入或修改 World 相机实体。动画姿态通过 SkeletonInstance 准备到临时 World 的派生渲染状态，观察相机只在打开、切换预览网格或 Focus 时取景。右键环绕、中键平移、滚轮缩放。主 World、源资产和 Cook 结果不受预览操作影响。

TimelineView 拥有当前时间和播放推进；Play、Pause、Stop、Seek、Update 统一触发时间/播放事件。TimeChanged 包含前后时间与 Playback/Seek/Stop 来源，外部编辑器订阅后更新预览。播放控件属于 Timeline 面板，标尺支持连续拖拽和箭头拖拽，不吸附到刻度或帧。拖拽立即暂停，释放后保持暂停。播放至末尾停止，再次播放从头开始。

动画编辑器完全忽略 Notify 执行，播放和定位都只采样姿态；Notify 的编辑、Undo/Redo 和保存仍保留。技能等插件在自己的编辑器里订阅时间事件并实现特殊预览，不向通用时间轴注入业务执行。播放数据只属于会话，不进入 Timeline 或动画资产。

## 专用骨骼动画资产编辑器

`SkeletalAnimation` 由 `SharpEngine.Animation.Editor` 模块中的 `SkeletalAnimationEditorExtension` 注册专用打开入口，稳定 Opener ID 仍为 `engine.skeletal-animation-editor`，Editor ID 为 `skeletal-animation-editor`。`AnimationEditorModule` 同时负责骨骼动画与状态机扩展的注册和释放；`TimelineEditorModule` 只注册 Timeline 编辑器。依赖方向是 `Animation.Editor → Timeline.Editor → Timeline.Runtime`，Timeline 不引用上层动画编辑器模块。

这个编辑器专属维护文件只有两个：

- `Source/SharpEngine.Animation.Editor/SkeletalAnimationEditorExtension.cs`：注册、每资产实例、动画 Authoring 适配、预览内容组织、展示数据和事件绑定。
- `Content/UI/Rml/SkeletalAnimationEditor.rml`：Preview/Timeline/Details 面板、默认 Dock 结构、Details 控件和子布局插槽。

编辑器创建 Viewport3D，挂接临时 World 的 WorldViewportContent，再放入兼容骨骼模型；Timeline 的 TimeChanged 触发 SamplePreview 和 SetPose。观察相机属于 Viewport，动画预览只采样姿态，不执行 Notify。播放、Root Motion、Notify、预览网格、Undo/Redo、Save 和只读约束沿用现有领域能力。

骨骼动画的 JSON 事务与历史归属于该逻辑文件内部的 `SkeletalAnimationDocument`，Timeline 只是由动画生成的展示数据。`TimelineAssetToolDocument` 只处理 Timeline 资产，不能再通过资产类型分支编辑骨骼动画或状态机。通用 `TimelineNotifyEditing.TryAdd` 仅构建候选轨道数据，调用者决定提交到 Timeline 事务还是动画 JSON 事务，不取得历史与保存的所有权。

状态机使用同模块内的 `AnimationStateMachineEditorExtension.cs` 与 `Content/UI/Rml/AnimationStateMachineEditor.rml`，保留 Preview/Timeline/Details、状态列表、初始状态标记及只读行为。稳定 Opener ID 为 `engine.animation-state-machine-editor`；Editor ID 仍使用原 `timeline-editor`，用于兼容窗口身份和已保存 Dock 布局，不表示它属于 Timeline 模块。状态机状态快照升级为 schemaVersion 3：保留图、计数、revision 与只读状态，增加 runtimeUi；移除未使用的 previewWorld、viewport 和预览网格字段，不再创建空的 3D 预览 World。骨骼动画编辑器快照仍为 v2，源动画/状态机/Timeline 格式不变。

父布局使用 `<ui-slot name="Viewport" />`、`<ui-slot name="Timeline" />` 引用实现 `IUiLayoutPart` 的子实例。Viewport3D 和 TimelineView 的内部标记分别由 `Content/UI/Rml/Viewport3D.rml`、`Content/UI/Rml/TimelineView.rml` 维护；父编辑器不拼接这些标记。通用 `UiLayoutTemplate` 按本次使用的数据展开文本、条件和重复项，并对文本及属性进行 XML 转义；插槽只接收 UI 布局契约，不接收任意业务对象。

`AssetEditorLayoutDefinition` 解析 `asset-editor-layout` schema-version 1，把默认 Dock 定义转换成既有 EditorLayoutDocument；每次使用创建独立可变 Dock 数据。已有分割、游离、窗口归属和布局保存路径继续由资产编辑器宿主管理。

专用编辑器还持有 `AssetEditorLayoutInstance`，各 Pane 的 `UiLayoutInstance` 支持代码向自己的容器添加控件、绑定事件或挂接子布局。实例修改不会保存到原始 RML；`runtimeUi` 提供实例状态观察，最终显示与焦点仍通过原生 UI 快照验证。接入方法见 [运行实例 UI 组合](RuntimeUiComposition.md)。

这些内置 RML 定义作为所属模块的嵌入资源构建。修改内置布局后重建对应引擎模块并重启 Editor；不新增另一份布局 manifest，不自动改写用户保存的布局，也不声称支持内置模板热重载。
