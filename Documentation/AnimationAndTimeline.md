# Playable、通用时间轴与骨骼动画

`SharpEngine.Playables` 是跨领域的底层运行时协议。`IPlayable` 只有稳定的 `PlayableId`、时长、播放状态、寻址、推进和评估操作；它不依赖动画、骨骼或任何具体输出类型。节点把结果写入 `IPlayableOutputSink`，输出使用 `TargetId + PropertyPath + JsonNode` 表示，因此运行时句柄、渲染资源和委托不会进入资产。`PlayableGraph` 负责组合任意 Playable，并提供 Play/Pause/Stop/Seek、固定步推进和逐帧评估。音频、对白、事件、粒子参数等未来系统可以直接实现 `IPlayable`，或者复用 `PlayableBase` 的状态机。

骨骼动画资产是 `Content/Assets/Animations/RobotWalk.skeletalanimation.json`，由 `SkeletalAnimationAsset` 加载。资产保存骨骼稳定 ID、父子引用、绑定姿态、按骨骼排序的关键帧、时长和采样率；加载时会校验重复 ID、父引用、有限数值、四元数、关键帧单调性和时间范围。`Sample` 产生派生姿态，`SkeletalAnimationPlayable` 将姿态作为 `skeleton.pose` 输出。资产通过普通 `assets.json` 注册，加载入口是：

```csharp
var animation = AssetContent.LoadSkeletalAnimation(
    registry, "engine://animations/robot-walk");
var playable = new SkeletalAnimationPlayable(
    "robot-walk-instance", animation, "robot");
```

`SharpEngine.Timeline` 是通用编排与编辑层，不属于 Animation。时间轴资产由 `TimelineDocument` 表示，轨道和片段只保存稳定 ID、目标、时间范围、速度、源起点和循环模式，不绑定骨骼、音频或其他领域类型。`TimelinePlayable` 通过 `IPlayableResolver` 在运行时解析片段，并把片段局部时间映射到任意 `IPlayable`。示例资产是 `Content/Assets/Timelines/RobotWalk.timeline.json`；它使用骨骼动画只是因为当前示例需要一个具体的 Playable。

Timeline 是 Engine 内置功能，由 `Source/SharpEngine.Timeline.Runtime/SharpEngine.Timeline.Runtime.csproj`
和 `Source/SharpEngine.Timeline.Editor/SharpEngine.Timeline.Editor.csproj` 分别提供 Runtime
和 Editor Module。Editor Module 依赖 Runtime Module；Game Target 不加载 Editor Module。

`TimelineView` 是 renderer-neutral 的视图层。`BuildSnapshot(width, height)` 返回轨道、片段矩形、标尺刻度和 playhead 的语义快照；`BuildMarkup` 返回可嵌入 RmlUi 的标记。`TimelineViewState` 只保存缩放、滚动、当前时间和选择，是瞬态编辑器状态，不会写入时间轴资产。

骨骼动画只位于 `SharpEngine.Animation`：`SkeletalAnimationAsset` 是骨骼关键帧资产，`SkeletalAnimationPlayable` 是把该资产适配到通用 Playable 协议的领域实现。它输出 `skeleton.pose`，并不改变 Playable 或 Timeline 的通用契约。

`Tools/TimelineValidation` 覆盖资产清单加载、确定性 round-trip、姿态插值、循环采样、Playable 输出、骨骼动画时间轴组合、非动画事件 Playable 的时间轴编排、视图标尺/playhead 和语义片段选择：

```powershell
dotnet run --project Tools/TimelineValidation/SharpEngine.TimelineValidation.csproj -- D:\SharpEngine
```
