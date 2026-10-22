# Nav 与 Shape Collision

内置组件在 `SharpEngine.Runtime`，通过生成器注册，直接使用现有 Inspector 的添加、字段编辑、Reset、启用、删除、Undo/Redo 与 MCP `components.edit`。

| 组件 | 稳定 typeId | Inspector 菜单 |
| --- | --- | --- |
| BoxColliderComponent | `engine.box-collider` | Collision/Box |
| SphereColliderComponent | `engine.sphere-collider` | Collision/Sphere |
| CapsuleColliderComponent | `engine.capsule-collider` | Collision/Capsule |
| NavSurfaceComponent | `engine.nav-surface` | Navigation/Surface |
| NavAgentComponent | `engine.nav-agent` | Navigation/Agent |

这些新组件的 schemaVersion 均为 1，没有更早的已发布 schema；不接受版本 0 或未来版本。World/Prefab 继续使用现有 JSON v3。所有配置字段显式保存，实例 ID、引用、Prefab 重映射和 Cook 前数据校验复用现有组件契约。

## Shape 与查询

三个 Collider 共用 `Center`、`Layer`（0–31）、`IsTrigger`、`BlocksNavigation`。同一实体允许多个 Collider，均依赖 Transform。

- Box 的 `Size` 是局部完整尺寸，各轴必须大于零。
- Sphere 的 `Radius` 是局部半径，必须大于零。
- Capsule 沿局部 Y 轴；`Height` 包含两端半球，必须至少等于 `2 * Radius`。

查询保留完整层级矩阵，包括旋转、非均匀缩放和层级产生的剪切。射线使用局部解析求交，重叠使用凸体支持映射与 GJK；包围盒仅用于快速排除。接触容差为 0.0001 世界单位。

```csharp
var Collision = World.Collision;
var Hit = Collision.Raycast(Origin, Direction, MaxDistance: 100);
var Nearby = Collision.OverlapSphere(Center, Radius: 2);
var Overlapping = Collision.OverlapShape(ColliderComponentId);
```

`World.Collision` 获取当前已提交 Runtime revision 的不可变查询快照。可重用快照完成多次查询；世界变化后，需要重新获取才能看到新的状态。结果以稳定组件 ID 排序，射线优先按距离排序。

默认查询排除 Trigger，可指定 `LayerMask`、`IncludeTriggers`，射线和球查询还支持 `IgnoreEntityId`。停用组件、停用实体及其后代不参与查询。零方向、负距离、非有限参数会拒绝；方向会归一化，结果距离使用世界单位。射线起点位于 shape 内部时，返回距离 0，法线为射线反方向。

本轮提供几何查询，不包含刚体动力学、碰撞响应、接触事件或连续物理模拟。

## 平面导航

NavSurface 描述以实体原点为中心、位于局部 XZ 平面的可行走矩形。`Size` 是局部 X/Z 尺寸，`CellSize` 是局部网格尺寸，`ObstacleMask` 选择障碍层；每张表面最多 16384 个网格。世界表面必须保持水平，支持平移、Y 轴旋转、缩放和水平剪切；倾斜表面返回 `NAV_SURFACE_MUST_BE_HORIZONTAL`。

NavAgent 的实体原点位于脚底。`Radius`、`Height`、`Speed`、`StoppingDistance` 使用世界单位；`Destination` 是世界坐标。将 `Surface` 绑定到 NavSurface，设置目标并开启 `HasDestination` 后开始移动。取消目标或停用 Agent 会停止后续导航推进。Surface 丢失、停用或删除后不可继续使用旧路径；删除按现有引用规则将引用置空。

```csharp
var Added = World.AddComponent<NavAgentComponent>(EntityId,
    new NavAgentComponent.InitialState
    {
        Surface = Surface,
        Radius = 0.3f,
        Height = 1.8f,
        Speed = 3,
        Destination = Destination,
        HasDestination = true,
    });
```

在生命周期回调中使用上述动态添加时，结果可能延后到安全边界完成，应按现有 `RuntimeMutation` 契约读取。已有 Agent 的配置可通过 `World.Patch` 和预期 revision 原子修改。

所有 FixedUpdate 回调完成后，引擎统一规划并推进本步有资格更新的 Agent，先读取同一份阶段状态，再提交移动。固定调度使用双精度的 1/60 秒间隔，回调 delta 保持现有 float 契约。本帧动态添加的 Agent 从下一帧开始推进。

规划先检查直线净空，否则使用四邻接 A*，再做视线简化。每条连接检查整个 Agent 胶囊体的扫掠体，包含半径和身高；移动在路径转角处结束当前步，避免跨过拐角。脚底保留 0.002 世界单位的地面间隙。目标必须位于平面及考虑半径后的表面范围内；阻塞目标或不可达路线返回明确错误，不自动吸附到其他位置。

障碍来自启用的 Box/Sphere/Capsule Collider，要求 `BlocksNavigation = true`、层匹配、不是 Trigger，并排除 Agent 所在实体。其他实体的 Agent 只有带 Collider 时才可能作为障碍；没有人群避让或同时移动碰撞求解。路径缓存可重建，障碍、目标、表面、Agent 配置或外部位置变化会在下一固定步使缓存失效。

```csharp
var Navigation = World.Navigation;
var Path = Navigation.FindPath(SurfaceComponentId, Start, Destination);
var Observations = Navigation.Agents;
```

导航快照包含 revision 和只读 Agent 状态（Idle/Moving/Arrived/Blocked/Unavailable）、诊断代码、位置、速度、剩余路径长度和路径。`ToJson()` 提供 schemaVersion 1 的 Runtime JSON。编辑器 MCP `components.describe` 的 `navigation` 字段暴露相同观察；Authoring 中该字段为 null。

组件配置和移动后的 Transform 属于 Runtime 权威状态；路径、速度、状态及净空信息是派生观察，路径缓存是 Runtime 临时数据。它们不进入 World/Prefab，也不会在 Stop 时写回 Authoring。

这是平面网格导航基础，不包含 NavMesh 烘焙、坡面/楼层、跳跃连接、角色控制器或视口 shape 调试绘制。

## 验证

```powershell
dotnet run --project Tools/SpatialValidation/SharpEngine.SpatialValidation.csproj
```

专项验证覆盖默认/非默认序列化、确定性、版本拒绝、非法数据诊断、Authoring 事务与 Undo/Redo、显式保存、Runtime 重建、Prefab 引用重映射、shape 组合重叠、层级变换、射线、导航净空、动态障碍重规划、父子 Agent、动态添加和固定步长。另有 600 组固定随机种子的解析几何对照查询。

本轮结果：SpatialValidation 155 项、ComponentValidation 41 项、WorldValidation 52 项、WorldAuthoringValidation 26 项、EditorValidation 112 项均通过。Editor/Game Host 已分别在隔离输出目录完成托管编译，均为 0 warning、0 error。

验证输出位于忽略目录 `Saved/Validation/Spatial`：`shapes.sworld` 和 `navigation.json`。这些是托管几何/运行时和语义命令验证，不代表原生编辑器点击、渲染、完整 Cook 或打包游戏验收。
