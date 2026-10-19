# 控制、可控制目标与移动

控制器和可控制目标是两个不同的组件职责。

```text
PlayerControllerComponent / AiControllerComponent
                │  提交移动意图
                ▼
       ControllableComponent
                │  提供当前帧意图
                ▼
         MovementComponent
                │  Evaluate 阶段更新 Transform
```

`ControllableComponent` 是目标实体的能力声明。它不决定输入来源，也不持有控制器的持久引用；运行时会记录当前绑定的控制器和本帧移动意图。一个目标同时被多个控制器指向时，先比较 `Priority`，再按控制器组件 ID 稳定排序，只有获胜者可以提交意图。

`ControllerComponent` 是控制来源的共同基类，序列化的 `ControlledEntity` 使用稳定实体 ID。绑定时必须找到目标实体上的 `engine.controllable`，否则控制器在 Frame 中发布 `CONTROLLER_TARGET_NOT_CONTROLLABLE`。`PlayerControllerComponent` 和 `AiControllerComponent` 共享这条绑定路径；输入模块或 AI 行为通过 `SetMoveInput` 写入当前运行时意图。

控制器不直接修改目标的 Transform。`MovementComponent` 要求同一实体具备 `TransformComponent` 和 `ControllableComponent`，在 `Evaluate` 阶段消费已经提交的二维输入，把它映射到 XZ 平面，并按 `MaxSpeed`、`Acceleration`、`Deceleration` 更新速度和位置。这样控制器更新顺序不会决定移动结果，也为将来的导航移动、物理移动和其他 Processable 能力保留了边界。

这组控制意图和速度属于运行时状态。`RuntimeFrame` 会发布控制器绑定、目标状态和移动速度；场景序列化只保存组件配置和 `ControlledEntity` 的稳定引用，不保存控制器对象、委托、缓存或当前速度。

最小用法：

```csharp
var Controller = Entity.GetComponent<PlayerControllerComponent>()!;
Controller.SetMoveInput(new Vector2(1, 0));
```

输入绑定应由模块或宿主持有，并在同一引擎线程调用 `SetMoveInput`；控制器组件不直接依赖 `InputRuntimeService`。AI 控制器可以由 AI 行为每帧提交相同的移动意图，后续也可以在同一目标侧增加攻击、交互等独立 Processable 能力，而无需把这些逻辑塞进 Controller 基类。

引擎默认示例角色资源为 `engine://prefabs/capsule-character`，源文件是引擎目录下的 `Content/Prefabs/CapsuleCharacter.sprefab`。它的根实体包含 UEFN mannequin 骨骼网格、locomotion Animator、胶囊碰撞体、可控制能力、移动和玩家控制器，并把 `ControlledEntity` 指向自身；Animator 通过 `MovementSource` 读取最终移动速度，场景实例化时由 `SceneData.Instantiate` 重映射内部组件引用。引擎的 `Content/Worlds/Main.sworld` 已放置同样的实例。

`D:\SharpEngineProjects\SharpEngineSandbox` 的 `project://prefabs/capsule-character` 和项目主场景使用骨骼角色变体：移除静态胶囊网格，添加 `SkeletalMeshComponent`（`engine://meshes/uefn-mannequin`）及 Animator schema v3，绑定 `engine://animation-state-machines/uefn-mannequin-locomotion`。`Animator.Target` 和 `MovementSource` 显式引用组件 ID，Prefab 实例化同时重映射这些引用和控制器的实体引用。角色原点在脚底，胶囊的 `Center=[0,1,0]`、`Radius=0.5`、`Height=2`。`CapsuleColliderComponent` 不生成渲染几何，参与 `RuntimeWorld.Collision.Raycast`；状态机按最终移动速度切换 Idle、Walk、Run，Jump 状态保留独立参数入口。

在编辑器 Play 时，宿主只把 `DemoMove` Axis2D 动作转发给场景中的玩家控制器，并在停止 Play 时释放绑定。按 WASD 前先点击 Viewport 获取焦点，角色和它的胶囊 Trace 体应在 XZ 平面移动。Sandbox 的 Animator 复用 `SkeletalAnimationPlayable` 采样并输出姿态，骨骼网格在帧末 `PrepareRender` 阶段计算骨骼矩阵和蒙皮顶点。运行时的 Transform、速度和骨骼帧属于 Runtime 状态，Stop 后源 World 文件和 Authoring 数据保持不变。

Sandbox 验证入口：`dotnet run --project Tools/PrefabMovementValidation -- D:\SharpEngine D:\SharpEngineProjects\SharpEngineSandbox`。它覆盖 Prefab 引用重映射、输入桥接移动、动画采样、移动后的 Trace、禁用碰撞不影响渲染和源文件隔离。程序化输入验证不代表桌面物理键盘操作验收。
