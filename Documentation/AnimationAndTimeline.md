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

编辑器的 Animation Timeline 由引擎创建，Timeline Editor Module 在固定的
`EditorCustomBarRegistry.TimelineSurfaceId` CustomBar 中提供 Play、Pause、Stop、定位、
Add Notify、Undo、Redo 和 Save。它会从当前 skeletal-animation Clip 采样骨骼姿态，
按父子层级累积局部变换，并在预览区按整套骨架范围自动取景后显示骨骼连线、骨骼点、
采样时间和进度；预览结束时会明确显示完成状态。时间轴修改由
`TimelineAuthoringSession` 管理，使用递增 `DocumentRevision`、dirty 状态和可逆历史；
Save 先重新解析序列化内容，再用临时文件原子替换项目源资产。引擎来源资产保持只读，
因此 Save 在项目 Timeline 资产上才可用。

在 Content Manager 中单击资源只改变选择状态；双击 `SkeletalAnimation` 资源会通过
`EditorRuntime.OpenAsset` 打开对应的 Animation Timeline 文档。每个资源拥有独立的
播放头、Notify 轨道、Undo/Redo 历史和 dirty 状态，多个已打开资源会以文档标签切换。
`EditorAssetOpenerRegistry` 先匹配具体 `AssetId`，再匹配 `AssetType`，并按优先级解析；
项目或插件可注册高优先级打开器覆写引擎默认工具，释放注册句柄后恢复原打开器。

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
   Animator 自己管理 `SkeletalAnimationPlayable`，复用 Play/Pause/Seek/Advance/Evaluate，
   将采样得到的局部姿态写给 `Target`。世界负责阶段调度，动画行为封装在 Animator 内。
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
`MovementSpeedParameter` 把 Movement 的最终速度写入数值参数。状态切换在全部 Movement
求值完成后的 LateUpdate 阶段执行，组件 ID 顺序不会改变决策结果。状态图会在加载时解析
全部 Clip 依赖，任一依赖无效则整张图报告绑定失败；切换采用直接采样，当前不做姿态混合、
重定向或根运动。状态机控制器的运行时状态（当前状态、过渡 ID、参数和播放时间）进入
Animator 帧观察，图资产和组件状态均可从最新提交快照重建。

`engine://animation-state-machines/uefn-mannequin-locomotion` 是引擎提供的只读
builtin/preset 资产，依赖 UEFN mannequin 骨骼和四个 Clip。Content Manager 双击
`AnimationStateMachine` 资产时沿用 `EditorAssetOpenerRegistry` 与
`EditorRuntime.OpenAsset` 的统一打开入口，由 `engine.animation-state-machine-editor`
在现有 Animation Timeline 面板中显示状态和过渡图；它不会创建另一套资产打开或文档路由。

验证入口：

```powershell
dotnet run --project Tools/AnimationValidation/SharpEngine.AnimationValidation.csproj -p:SharpEngineTarget=AnimationValidation -- D:\SharpEngine
```

覆盖组件序列化、v1/v2 到 v3 迁移、事务与重建、Playable 语义、显式目标生命周期、
Prefab 重映射、状态机依赖与条件、Movement 驱动的 Idle/Walk 切换、帧末姿态与几何、
冻结快照、状态机引擎预设依赖和统一编辑器打开路由，以及绘制/拾取/观察不采样。
帧几何验证不代表原生窗口或打包运行验收。
