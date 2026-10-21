# Character Movement 插件

`sharpengine.character-movement` 用引擎的移动组合点提供整套角色移动：一个移动权威、
可替换的地面/空中模式，以及按优先级组合的修饰器。移动与根运动的归属都在同一处提交，
实体的 Transform 只有这一个写入者。

## 组成

| 组件 | 类型 ID | 说明 |
| --- | --- | --- |
| `CharacterMovementComponent` | `6f0c3d1a-8b2e-4f7c-9a51-2c4e8d3b7a10` | 移动权威（schema v1） |
| `WalkingMovementModeComponent` | `1b7e9c42-5d38-4a06-8f2b-73e1c9d45b21` | 地面模式，`ModeId = "walking"` |
| `FallingMovementModeComponent` | `a3d24f68-7c19-4e85-b027-6f9d1e3c8a32` | 空中模式，`ModeId = "falling"` |
| `JumpMovementModifierComponent` | `c58a7e13-2f46-4b90-8d7a-5e3b9f16c043` | 队列式起跳 |
| `SprintMovementModifierComponent` | `0e9b5d76-4a13-42cf-9b68-d27f4a8e2154` | 会话级冲刺倍率 |
| `CharacterSpeedLimitModifierComponent` | `5c1f8a74-9d26-4e38-b1a5-8f2d6c47e065` | 绝对平面速度上限 |

帧顺序（全部 Evaluate 之后、LateUpdate 相机之前）：

1. 权威 `BeginMovementFrame`：读取 `ControllableComponent.MovementIntent`，解析模式与修饰器，暂存位移。
2. 权威 `ConsumeRootMotion`：每个根运动分组一次，返回 true 表示接管。
3. 权威 `EndMovementFrame`：把位移写回 Transform。

## 启用

插件是项目级 opt-in。在项目清单的 `plugins` 数组登记：

```json
"plugins": ["engine://Plugins/CharacterMovement/CharacterMovement.sharpplugin"]
```

然后用 `Tools/SharpEngineProject.ps1 -Command Build` 重建项目，Editor 与 Game 都会加载它。

角色实体最小配置（同一实体）：

```text
engine.transform
engine.controllable          ControlEnabled = true
engine.capsule-collider      可选，提供地面与碰撞查询
<controller>                 PlayerController 或 AiControllerComponent，ControlledEntity 指向本实体
engine.character-movement    GroundModeId = walking, AirModeId = falling
engine.walking-mode
engine.falling-mode
（可选）engine.jump-modifier / engine.sprint-modifier / engine.speed-limit
```

再加一个子实体承载 `engine.skeletal-mesh`，把 `engine.animator.Target` 指向它，并在动画资产上开启
Root Motion。根运动会经权威按 `RootMotionScale` 转成角色位移，姿态中的根平移已被剔除。

**不要**在同一实体同时保留 `engine.movement`：它会转为 `MOVEMENT_OVERRIDDEN` 并只保留一条
warning，位移由权威独占。

## 扩展

新增模式：实现 `ICharacterMovementMode` 的组件，声明
`[CharacterMovementModeId("你的 id")]`，再把 `GroundModeId` 或 `AirModeId` 指向它。
新增修饰器：实现 `ICharacterMovementModifier` 并按 `Priority` 排序，修饰器只改
`CharacterMovementFrame`，不写 Transform。

编辑器菜单 `Window → Character Movement → Describe Character Movement` 会报告选中角色的
模式、修饰器与是否被 `engine.movement` 覆盖。

## 验证

```powershell
dotnet run --project Tools/CharacterMovementValidation -- D:\SharpEngine
```

该工具自行构建插件程序集并在内存中注册组件目录，覆盖模式选择、修饰器优先级、根运动归属、
`MOVEMENT_OVERRIDDEN`、双权威冲突、失效模式诊断、状态往返和非法配置拒绝。
