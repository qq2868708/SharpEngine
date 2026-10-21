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
5. `FaceMoveDirection=false` 时根实体水平朝向与镜头同步，`FacingMesh` 子模型独立转向 WASD 的世界移动方向，A/D 时转向左右并播放前向动画；true 时根实体也转向移动方向。第三人称相机只绕目标旋转，不把俯仰施加给角色身体。

`engine.third-person-camera` schema v1 保存 Target、TargetOffset、Distance、Yaw、Pitch、LookSensitivity。LateUpdate 与帧准备阶段在目标世界位置计算相机；运行帧公开目标和诊断。相机没有新增遮挡避让或物理碰撞能力。

内置 `engine://prefabs/capsule-character` 的根在胶囊中心，局部 Mesh 子实体 Y=-1；根上的 Animator.Target 引用该 Mesh。相机子实体的 ThirdPersonCamera.Target 指向根，PlayerController.ViewCamera 引用相机组件，FacingMesh 引用模型组件，实例化自动重映射内部实体与组件 ID。Main Scene 使用同样配置；Runtime 自动选用 LocalPlayer 的 ViewCamera，Stop 恢复原 Authoring 相机。

点击 Play 自动聚焦视口、隐藏并限制光标；Shift+F1 通过 Editor Context 的 `player.release-pointer` 释放，点击运行视口重新捕获。暂停、停止、窗口失焦及意外丢失捕获也释放。捕获状态经 `editor.describe.playInputCapture` 与 `input.describe.capture` 可读。物理鼠标使用 Raw Input 相对增量；MCP 的 ui.key/ui.pointer 仍经过真实 RmlUi 路径。隔离 Editor 默认隐藏，真实焦点验收需加 `--mcp-visible`。

动画资产 Root Motion 开启时由 Animator 提取根增量到角色实体，并从模型姿态剔除根平移和旋转。Player 的根位移通过子 Mesh 朝向转换到实体空间，保留源水平距离和竖直轨迹，根实体的朝向仍由镜头控制；无水平移动意图时抑制水平根位移。普通 Animator 使用源轨迹。同一实体的普通输入位移在该帧不重复应用。关闭时仅输出原地姿态，由 Movement 移动。Movement 分别公开意图速度与实际速度，状态机使用意图速度以避免根运动反馈使状态卡死。胶囊 Trace 使用移动后的实体变换；当前 collider 提供查询，不增加阻挡解算。

验证：`dotnet run --project Tools/PrefabMovementValidation -- D:/SharpEngine` 覆盖 Prefab、引用、重建、输入、动画与 Trace；`Tools/AnimationValidation` 覆盖根运动循环、旋转、Seek、冲突和资产重新绑定；`dotnet run --project Tools/McpUiValidation -- Debug --player-only --render-backend=vulkan` 验证真实坐标 Play 和键鼠路径。测试创建自己的隔离窗口并在结束时关闭，不连接用户 Editor。
