# 生命周期子系统

子系统是引擎按固定作用域自动创建、更新和销毁的单例。每个具体类型在一个作用域内只有一个实例，
不同 Engine、Play 会话和 World 相互隔离。实例通过所属对象查询，不保存在进程全局 `static Instance` 中。
子系统集合只提供查询和状态读取；创建、更新和销毁由对应所有者管理。

| 基类 | 所有者 | 生命周期 |
| --- | --- | --- |
| `EngineSubsystem` | `EngineLifetime` | 当前 Editor/Game 宿主启动到关闭 |
| `EditorSubsystem` | `EditorRuntime.EditorSubsystems` | 当前编辑器会话；暂停和 Stop 后继续存在 |
| `InstanceSubsystem` | `RuntimeInstance` | 一次游戏运行或 Play 会话；可跨多个 World 保留 |
| `WorldSubsystem` | `RuntimeWorld.Subsystems` | 单个运行时 World；卸载或替换 World 时释放 |

运行时基类位于 `SharpEngine.Subsystems`，编辑器基类位于 `SharpEngine.Editor`。
Runtime Module 可以声明 Engine、Instance 和 World 子系统；Editor 子系统必须在 Editor Module 中声明。
模块宿主在调用模块 `Startup` 前扫描该模块程序集，自动注册所有具体子系统；不需要手写创建代码。
类型必须公开、非抽象、不含开放泛型，具有公开无参构造函数和稳定 ID、正数版本：

```csharp
[Subsystem("mygame.match-session", 1)]
public sealed class MatchSessionSubsystem : InstanceSubsystem
{
    private double ElapsedSeconds;

    protected override void OnInitialize()
    {
        ElapsedSeconds = 0;
    }

    protected override void OnUpdate(float DeltaSeconds)
    {
        ElapsedSeconds += DeltaSeconds;
    }

    protected override JsonObject CaptureState()
    {
        return new JsonObject
        {
            ["elapsedSeconds"] = ElapsedSeconds,
        };
    }

    protected override void OnUninitialize()
    {
        // 释放本实例拥有的订阅、句柄等资源。
    }
}
```

构造函数只初始化默认值；订阅和外部资源的获取放在 `OnInitialize` 中，便于失败时完整清理。

`World.Subsystems.Get<MatchSessionSubsystem>()` 沿 World → Instance → Engine 查找，
`World.Instance.Subsystems.Get<MatchSessionSubsystem>()` 可以直接定位 Instance 作用域。
Editor 通过 `EditorSubsystems.Get<T>()` 查询 Editor → Engine。
模块通过 `Context.Subsystems.SubsystemLifetime` 和当前可空的 `Context.Subsystems.Instance` 查询。
未注册或尚未创建对应作用域时返回 `null`；查询不会延迟构造实例。

独立宿主可使用以下所有权结构。一次 `Instance.Tick` 推进 Instance 和所有 World，
同一帧不再单独重复调用这些 World 的 `Tick`：

```csharp
using var Engine = new EngineLifetime();
using var Registration = Engine.Registry.RegisterAssembly(
    typeof(MatchSessionSubsystem).Assembly, "mygame.runtime", "Runtime");
using var Instance = new RuntimeInstance(Assets, Engine);
using var World = Instance.CreateWorld(Scene, Components);

Engine.Update(DeltaSeconds);
Instance.Tick(DeltaSeconds);
```

Editor/Game 宿主已经接入这些调用。Editor 暂停时只推进 Engine/Editor，单步会推进 Instance 和 World。
Server 同样先启动模块再创建 World，每帧先推进 Engine，再推进 Instance/World，关闭时先释放运行世界再卸载模块。
`DedicatedServerSession` 未传入 Engine 时自行推进其私有 Engine；传入共享 Engine 时由调用者统一推进，避免多会话重复 Tick。
直接构造 `RuntimeWorld` 时会创建其私有的 Instance/Engine 所有者，并在释放 World 时一起释放；
直接调用 `World.Tick` 只推进该 World。需要跨场景状态时显式持有 `RuntimeInstance`，通过它创建 World。

World 子系统先于组件初始化，并在组件更新前收到 `OnUpdate`。初始化时可以读取初始场景快照，
但实体实例图尚未完成；组件 `OnInitialize` 可以查询已初始化的 World 子系统。
同作用域的子系统不得依赖彼此更新顺序。生命周期回调中不能增删子系统定义、创建或销毁作用域；
World 的实体/组件变化使用现有调度 API，在安全边界提交。所有访问与修改都在所有者线程执行。

销毁按组件 → World → Instance → Editor/Engine 进行。停止 Play 会清除 World 和 Instance，
下次 Play 创建新实例。模块注册参加 `ModuleRegistrationScope` 的清理：模块卸载会销毁其活跃实例，
移除类型引用，再允许下一代注册相同 ID。Editor 热重载在切换前预检子系统描述和 ID 冲突，
并遵循现有的 Stop 后应用规则。初始化失败会回滚整个注册批次，清理已开始初始化的部分实例；
更新失败会产生结构化诊断，其他子系统继续更新。关闭时的清理异常记录到日志，继续释放剩余实例。

子系统对象、委托和句柄属于可重建的瞬态运行对象。业务运行状态通过纯观察的 `CaptureState` 返回
JSON；它与类型 ID、状态版本、module ID、作用域 ID、状态与诊断一起发布。该状态不自动写入场景、
Prefab 或 Cook 资产，跨运行持久化的权威数据仍使用已有组件或资源契约。

`SubsystemCollection.ToJson()` 读取最近提交的不可变快照，不调用业务回调。根快照 schemaVersion 为 1，
`stateRevision` 仅在内容变化时增长，各子系统使用声明的状态版本。
`SubsystemRegistry.ToJson()` 发布类型目录，包括尚未创建作用域的类型；Editor 观察中的 `catalog` 提供同一目录。
World 的子系统快照随 `RuntimeWorld.Frame` 一起冻结；Editor 的 `components.describe.subsystems` 暴露四种作用域。
快照不能包含原生句柄、委托或凭据。`CaptureState` 必须只读取状态，不推进或修改世界。

验证入口：

```powershell
dotnet run --project Tools/SubsystemValidation/SharpEngine.SubsystemValidation.csproj -- D:\SharpEngine
```

覆盖作用域隔离、自动模块注册、失败回滚、卸载与重新注册、线程限制、冻结快照、
组件与子系统销毁顺序，以及 Editor 的 Play、暂停、单步、Stop 和新会话重建。
这些是托管生命周期与状态验证，不代表原生窗口或打包运行验收。
