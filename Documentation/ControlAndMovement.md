# 控制、可控制目标、第三人称与移动

PlayerController 负责输入和朝向，Controllable 仲裁控制来源，Movement 生成移动意图；世界在全部 Evaluate 完成后统一采样动画并提交位移，再进入 LateUpdate 相机跟随。

`ControllerComponent.ControlledEntity` 使用稳定实体 ID。目标必须具备 `engine.controllable`；多来源按 Priority 降序、组件 ID 稳定排序仲裁。`AiControllerComponent` 或没有输入服务的程序化运行可继续调用 `SetMoveInput`。

`PlayerControllerComponent` schema v2 保存 `InputContext`、`MoveAction`、`LookAction`、`ViewCamera`、`FacingMesh`、`FaceMoveDirection`、`TurnSpeed`，以及原有 `ControlledEntity` / `Priority`。旧 v1 迁移为默认 Player Context/MoveAction，LookAction 与 ViewCamera 为 null。Move/Look 必须是所选 Gameplay Context 内注册的 Axis2D 动作，Look 要求 ViewCamera 实体有 ThirdPersonCamera。无效绑定在组件帧中报告 `PLAYER_INPUT_BINDING_INVALID`。

Editor/Game 将 `IInputRuntime` 和当前 host ID 注入 `RuntimeInstance`。Player 组件在启用时持有 Context activation 和 Action registrations，停用、删除或停止运行时释放。一个世界按 Priority 与组件 ID 选取有效的 LocalPlayerController；只接受其 Context 和 host 的事件，输入不再广播给所有 Player。控制能力停用、失焦或捕获丢失都会清空输入。

输入链路：

1. Win32 键鼠事件先经过 RmlUi 的焦点、命中、文本编辑和事件传播；消费的事件停止。
2. 未消费事件进入 InputRuntimeService，按 Editor、RuntimeUi、Gameplay 路由，再按 Context priority 和稳定 ID 处理；已消费 Control 不再流向较低路由。
3. Player 组件激活 `engine://input/contexts/player`。WASD 组合成 `engine://input/actions/player.move`，相对鼠标位移形成 `player.look`。组件引用的是资产 ID，不依赖 demo Context。
4. Move 缓存持续 Axis2D，Look 累加当帧 Performed 增量。相机 Yaw 将 WASD 转成世界 XZ 意图；Controller 提交给 Controllable，Movement 按速度和加减速生成位移。
5. 内置 Player 使用 `FaceMoveDirection=true`：鼠标只调整观察朝向，站立时不转动角色；WASD 始终按相机水平 Yaw 生成世界移动意图，有输入时根实体按 `TurnSpeed` 转向该方向。模型保持其原有局部旋转，随根实体一起转动，不再次独立转向。`FaceMoveDirection=false` 显式选择原相机朝向模式，根实体跟随镜头 Yaw，FacingMesh 可独立转向输入方向。第三人称相机不把俯仰施加给角色身体。

`engine.third-person-camera` schema v1 保存 Target、TargetOffset、Distance、Yaw、Pitch、LookSensitivity。LateUpdate 与帧准备阶段在目标世界位置计算相机；运行帧公开目标和诊断。相机没有新增遮挡避让或物理碰撞能力。

内置 `engine://prefabs/capsule-character` 的根在胶囊中心，局部 Mesh 子实体 Y=-1；根上的 Animator.Target 引用该 Mesh。相机子实体的 ThirdPersonCamera.Target 指向根，PlayerController.ViewCamera 引用相机组件，FacingMesh 引用模型组件，实例化自动重映射内部实体与组件 ID。Main Scene 使用同样配置；Runtime 自动选用 LocalPlayer 的 ViewCamera，Stop 恢复原 Authoring 相机。

点击 Play 自动聚焦视口、隐藏并限制光标；默认 Esc（`sharpengine.editor-shortcuts/releasePointer`，可改键）通过 Editor Context 的 `player.release-pointer` 释放，点击运行视口重新捕获。暂停、停止、窗口失焦及意外丢失捕获也释放。捕获状态经 `editor.describe.playInputCapture`、`editor.describe.shortcuts` 与 `input.describe.capture` 可读。游戏捕获期间真实 Win32 指针消息只驱动游戏，不再让编辑器界面 hover 或拖动分割条；捕获释放后编辑器会重新取得窗口激活。物理鼠标使用 Raw Input 相对增量；MCP 的 ui.key/ui.pointer 仍经过真实 RmlUi 路径。隔离 Editor 默认隐藏，真实焦点验收需加 `--mcp-visible`。

动画资产 Root Motion 开启时由 Animator 提取根增量到角色实体，并从模型姿态剔除根平移和旋转。Player 的根位移通过子 Mesh 朝向转换到实体空间，保留源水平距离和竖直轨迹，根实体的朝向由 Player 所选转向策略控制；无水平移动意图时抑制水平根位移。普通 Animator 使用源轨迹。同一实体的普通输入位移在该帧不重复应用。关闭时仅输出原地姿态，由 Movement 移动。Movement 分别公开意图速度与实际速度，状态机使用意图速度以避免根运动反馈使状态卡死。胶囊 Trace 使用移动后的实体变换；当前 collider 提供查询，不增加阻挡解算。

验证：`dotnet run --project Tools/PrefabMovementValidation -- D:/SharpEngine` 覆盖 Prefab、引用、重建、输入、动画与 Trace；`Tools/AnimationValidation` 覆盖根运动循环、旋转、Seek、冲突和资产重新绑定；`dotnet run --project Tools/McpUiValidation -- Debug --player-only --render-backend=vulkan` 验证真实坐标 Play 和键鼠路径。快捷键（含 `Ctrl+S` 保存）属于 Editor 路由，并按获得焦点的文档作用域冒泡：资产编辑器先消费 Save/Undo/Redo，不消费才落到全局 World 层。`--player-only` 的断言描述内置 Prefab/World 配置，因此在未指定项目时每次都从当前引擎内容重建 `Saved/Validation/PlayerUi` 测试项目（含模块构建），始终按最新的 World 校验；当 `SHARPENGINE_VALIDATION_PROJECT` 指向的项目 World 快照与当前 `Content/Worlds/Main.sworld` 不一致（或控制器不是 `FaceMoveDirection=true`）时，会在启动前以 `PLAYER_FIXTURE_STALE` 拒绝，而不是用过期数据得出错误结论。测试创建自己的隔离窗口并在结束时关闭，不连接用户 Editor。

## 移动权威与 Character Movement 插件

`engine.movement` 是低层基础能力：它消费 Controllable 的意图，按单一加减速模型生成位移。需要在同一实体上组合“模式 + 修饰器 + 根运动归属”时，使用引擎提供的移动组合点 `SharpEngine.Components.IMovementAuthority`。

帧末边界（全部 Evaluate 之后、任何 LateUpdate 相机之前）按以下顺序运行，且每个权威都按组件 ID 排序：

1. `BeginMovementFrame(DeltaSeconds)`：解析意图、模式与修饰器，暂存本帧位移。
2. `ConsumeRootMotion(in RootMotionContribution)`：每个根运动分组调用一次；返回 true 表示权威接管。
3. `EndMovementFrame(DeltaSeconds)`：权威提交位移，是实体 Transform 的唯一写入者。

持有权威的实体不再收到引擎内建的根运动写入；同一实体上的 `engine.movement` 被静默并发布 `MOVEMENT_OVERRIDDEN`（每次加载只记一条 warning，不进入错误诊断）。同一实体出现两个权威时报告一次 `MOVEMENT_AUTHORITY_CONFLICT` 并保留组件 ID 较小的一个。权威在模式不可用时可以让 `ConsumeRootMotion` 返回 false，把该帧交还引擎默认路径。

同实体的 NavAgent 也不能绕过移动权威：启用的权威存在时，导航停止写 Transform，
有目标的 Agent 在观察快照中返回 `Unavailable` / `NAV_MOVEMENT_AUTHORITY`，并只记录一次提示。
停用或移除权威后，导航从当前位置重新规划并恢复。当前不把导航路径自动转换成 CharacterMovement 意图；
需要这种组合时由上层明确提供意图适配。

`RootMotionContribution` 提供 animator 组件 ID、目标实体、网格组件、目标世界矩阵、网格相对矩阵、世界增量、世界位移、播放推进量和 DeltaSeconds。接管成功后 animator 帧发布 `ROOT_MOTION_CONSUMED`，并在 `rootMotion.consumedBy` 记录权威类型名。

`Plugins/CharacterMovement`（插件 id `sharpengine.character-movement`，manifest schemaVersion 1）用这套组合点提供完整角色移动。插件为项目级 opt-in：在 `.sharpproject` 的 `plugins` 数组登记 `engine://Plugins/CharacterMovement/CharacterMovement.sharpplugin`，构成工程与运行时会同时加载它；引擎默认 Prefab 与 Main Scene 不引用插件组件，因此两者可以并存，旧内容行为不变。

| 组件 | 类型 ID | 职责 |
| --- | --- | --- |
| `CharacterMovementComponent` | `6f0c3d1a-…-3b7a10` | 移动权威，schema v1 |
| `WalkingMovementModeComponent` | `1b7e9c42-…-5b21` | 地面模式（加速/制动），`walking` |
| `FallingMovementModeComponent` | `a3d24f68-…-8a32` | 空中模式（重力/终端速度/空中操控），`falling` |
| `JumpMovementModifierComponent` | `c58a7e13-…-1c043` | 队列式起跳，按需消费一次 |
| `SprintMovementModifierComponent` | `0e9b5d76-…-e2154` | 会话级冲刺倍率 |
| `CharacterSpeedLimitModifierComponent` | `5c1f8a74-…-47e065` | 绝对平面速度上限 |

组合方式与 UE 的 Character Movement 2.0 同构：组件声明“用哪个模式”，模式实现积分，修饰器只调整帧而不写 Transform。模式身份由 `CharacterMovementModeIdAttribute` 声明一次，`ModeId` 从该元数据解析，避免出现两份可能漂移的字符串。新增模式只需再声明一个实现 `ICharacterMovementMode` 的组件，把 `GroundModeId`/`AirModeId` 指向它即可，无需修改引擎或既有模式。

权威的权威字段是 `InputDeadZone`、`GroundProbeDistance`、`GroundMinNormalY`、`GroundLayerMask`、`GroundModeId`、`AirModeId`、`ApplyRootMotion`、`RootMotionScale`、`ApplyRootMotionRotation`、`RootMotionRequiresIntent`。会话状态（垂直速度、活跃速度、是否处于冲刺）不序列化；`SprintMovementModifierComponent.SetSprinting` 和 `JumpMovementModifierComponent.RequestJump` 属于运行时入口，不进入权威状态。地面判定复用 `RuntimeWorld.Collision.Raycast`，按 Revision 缓存一份查询快照，不新增物理求解。

`ConsumeRootMotion` 在权威接管后总是声明本帧根运动，因此“关闭根运动”与“完全不接管”是两种不同语义：前者保留权威对 Transform 的独占并抑制位移，后者把控制权交回引擎。跳跃的垂直位移由权威与模式共同维护，引擎不解决穿透，落地需要外部移动或碰撞解算把角色放回地面。

验证：`dotnet run --project Tools/CharacterMovementValidation -- D:/SharpEngine` 覆盖插件注册、模式选择、修饰器优先级、根运动归属、`MOVEMENT_OVERRIDDEN`、双权威冲突、失效模式诊断、状态往返和非法配置拒绝。该工具构建插件项目后加载其程序集，不依赖 Editor 窗口。
