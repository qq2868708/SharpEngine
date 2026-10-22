# SharpEngine 架构复盘：2026-10-22

## 基线与结论

- 日期：2026-10-22，Asia/Shanghai。
- 分支：`main`。
- 已执行远端同步和 fast-forward：`358c92f` → `3e2df4a2393952d9f009c102882ead90aad09f3e`，增加 35 个提交。
- 本次 fetch 后，HEAD 与 `origin/main` 一致。
- 保留原有 `External/RmlUi` 工作区变更和未跟踪的 `Documentation/ProjectReview-2026-10-18.md`。
- 初次审查只做分析与独立复现。用户随后要求修复，当前工作区实现与验证见下节；未提交或推送。

## 修复跟进：当前工作区

以下三个问题已修复。后文保留修复前的证据与源码行号，便于理解原因，不代表当前实现仍有同样行为。

| 问题 | 实际处理 |
| --- | --- |
| A-01 Authoring 事务绕过 | 文档接入唯一 Authoring 会话时隔离先前可变引用；Data/Components 返回副本，直接 setter 拒绝；提交候选再次隔离。层级/Transform 编辑共用候选事务，拒绝嵌套提交与历史/保存点操作 |
| 扩展调用迁移 | 增加 EditorRuntime.EditWorld 与 EditorCommandContext.EditSelectedEntity；模板和 README 使用同一事务入口，成功编辑参与 Undo/Redo |
| A-02 Server 生命周期 | Session 支持先创建 Instance、再 StartWorld；Host 先启动模块后创建 World，先释放 World/Instance 后卸载模块；私有 Engine 由 Session 推进，共享 Engine 由 Host 每帧推进一次 |
| A-03 导航双写 | 启用的 IMovementAuthority 接管同实体后，NavAgent 暂停 Transform 写入并暴露 NAV_MOVEMENT_AUTHORITY；权威停用或移除后恢复规划。当前没有增加导航到角色意图的自动适配 |

正式回归位于：

- `Tools/WorldAuthoringValidation/AuthoringBoundaryValidation.cs`
- `Tools/NetworkValidation/ServerLifecycleValidation.cs`
- `Tools/CharacterMovementValidation/NavigationAuthorityValidation.cs`
- `Tools/EditorValidation/EditorExtensionValidation.cs` 新增扩展命令 revision、Undo、Redo 验收。

修复后的验证：

| 验证 | 通过断言数 |
| --- | ---: |
| WorldAuthoringValidation | 43 |
| ComponentValidation | 47 |
| WorldValidation | 64 |
| SpatialValidation | 155 |
| GpuSceneValidation | 18 |
| SubsystemValidation | 44 |
| CharacterMovementValidation | 35 |
| EditorValidation | 227 |
| NetworkValidation | 42 |
| 合计 | 675 |

WorldValidation 原有断言仍要求示例含 6 个实体，已改为校验当前 8 个实体的明确 ID 集合和 3 个网格；其余世界、裁剪、拾取和光照断言继续执行。日志位于 `Saved/Validation/BoundaryFixes`。

额外验证：

- `Tools/SharpEngineBuild.ps1` 的 Editor、Game、Server Debug 托管构建均通过。
- Server 标准原生构建通过；示例 Server 项目通过正式项目构建和发布。
- `RunDedicatedServerValidation.ps1` 启动真实 DedicatedServer.exe 与两个独立客户端；两客户端得到位置 7、11，并确认重复命令返回幂等结果，Server 正常退出。
- 从更新后的 BasicProject 模板创建 `Saved/Validation/BoundaryFixes/BoundaryTemplateValidation`，通过正式 ProjectBuild 工具完成 Runtime/Editor 两模块编译和发布，0 warning、0 error。
- 当前版本用户手册、API 索引、专题说明与开发技能约束已更新；手册结构验证通过（4 个 HTML、82 个本地链接）。
- `git diff --check` 与 Server target 输出布局检查通过。

尚未覆盖：Editor/Game 原生构建都在 RmlUi CMake 配置阶段失败，原因是既有子模块工作区缺失 `CMakeLists.txt`。保留该工作区原有状态；本轮没有以托管验证代替真实图形、UI 输入或 GPU 生命周期验收。

后续架构讨论建议从 RuntimeWorld 的职责边界开始：保留世界身份、固定阶段、结构变更队列和帧发布；核对控制、导航、动画、移动的领域计算怎样通过类型化契约组合；最后再讨论渲染提交协议和增量快照。此处只列讨论顺序，没有提前实施模块拆分。

当前架构已经具有实际可复用的基础设施：独立视口、按实例隔离的 UI 组合、明确作用域的子系统，以及不依赖 Editor 的网络模块。下一阶段优先级应是统一生命周期和状态写入边界。继续增加接口或拆程序集，并不能直接解决下面三个已经复现的问题。

## 一、已确认问题

### A-01 [P1] Authoring 状态仍可绕过事务，事务候选也会泄露为活对象

源码入口：

- `Source/SharpEngine.Editor/Editor/EditorRuntime.cs:138`：公开当前 `WorldDocument`。
- `Source/SharpEngine.Runtime/World/WorldDescription.cs:103`：`WorldEntity.Position` 有公共 setter。
- 同文件第 185 行：`Data` 返回可变 `SceneData`。
- `Source/SharpEngine.Editor/World/WorldAuthoring.cs:375`：把 staging 交给外部编辑回调。
- 同文件第 378 行：提交时直接把同一个 staging 实例装入文档。

复现一：新建 Authoring Session 后直接修改实体位置，输出：

```text
REPRO authoring-bypass: x=7; revision=1; canUndo=False; dirty=True
```

内容已经改变，revision 没有增加，Undo 没有记录。全量比较能发现 dirty，但不能补回版本一致性和历史。

复现二：通过正常 `Edit` 回调保留候选对象，事务完成后再修改这个对象：

```text
REPRO staging-alias: name=Changed after commit; revision=2
```

第二次修改进入当前文档，却没有形成新事务。因此，仅移除 `Position` 的公共 setter 不够；`Data`、嵌套 JSON/列表和事务候选的别名都需要纳入边界。

影响：AI 的 expectedRevision、插件编辑、Undo/Redo 和 Save 无法依靠同一权威版本判断状态。该问题发生在 Authoring；普通 Runtime 字段赋值是当前允许的设计，不属于本项问题。

建议：

1. 对外读取使用只读视图或与活文档脱离的快照。
2. 编辑通过同一语义事务入口进入，完整验证后提交。
3. 提交后的权威对象不能继续由回调持有可写别名；可采用提交时隔离候选或受控编辑器。
4. 验收覆盖直接写入、集合/JSON 写入和保留候选后写入，分别验证版本、撤销和失败原子性。

### A-02 [P1] Server 没有遵守与 Game 相同的子系统生命周期

源码入口：

- `Source/SharpEngine.Host/ServerEntrypoint.cs:52`：首先构造 `DedicatedServerSession`。
- `Source/SharpEngine.Networking/DedicatedServerSession.cs:49`：构造 Session 时立即创建 RuntimeWorld，执行组件初始化。
- `Source/SharpEngine.Host/ServerEntrypoint.cs:57`、第 59 行：随后才启动 Engine/Project Modules。
- `Source/SharpEngine.Runtime/Hosting/ProjectModuleHost.cs:259`：模块启动时才自动注册子系统。
- `Source/SharpEngine.Host/ServerEntrypoint.cs:92`：每帧只进入 NetworkHost.Tick。
- `Source/SharpEngine.Networking/DedicatedServerSession.cs:104`：最终只调用 Instance.Tick。

对照 `Source/SharpEngine.Runtime/Hosting/GameSession.cs:60` 起的流程，Game 先启动模块，再创建 World；第 97 行明确调用 `SubsystemLifetime.Update`。

按照 Server 的构造、注册和 Tick 顺序执行托管复现：

```text
REPRO server-lifecycle: sawSubsystemAtInitialize=False; subsystemAvailableNow=True; serverTicks=2; engineUpdates=0
```

两个独立问题：

1. 组件 OnInitialize 时，项目 WorldSubsystem 尚未注册；稍后它才出现，破坏“组件初始化可以查询已初始化 World 子系统”的契约。
2. Server 的完整调用链没有推进 EngineLifetime，EngineSubsystem 可以初始化成功，却一直不执行 OnUpdate。

另外，Server 关闭时先释放 Project/Engine Modules，再释放 Session；Game 则先释放 Instance。这使模块注册清理可能在组件 OnUninitialize 前移除其依赖的子系统。该销毁顺序风险来自源码，本轮未额外构造运行复现。

建议统一三个宿主的阶段契约：

```text
创建 Engine/Instance 所有者
→ 注册组件描述与子系统、启动模块
→ 创建 World
→ 每帧按宿主职责推进 Engine，再推进 Instance/World
→ 先销毁 World/Instance，再卸载模块，最后释放 Engine
```

应明确共享 Engine 的调用责任，避免多个 Session 各自重复推进同一个 Engine。不必让三种 Host 使用一个包含所有 UI/网络分支的巨大类；共享生命周期规则和相应测试即可。

证据边界：复现使用与 Server 相同的托管顺序；原生 DedicatedServer.exe 的启动验收未执行。

### A-03 [P2] NavAgent 绕过移动权威，组合后仍有第二个 Transform 写入者

源码入口：

- `Source/SharpEngine.Runtime/Components/RuntimeWorld.cs:325`：FixedUpdate 后调用 AdvanceNavigation。
- `Source/SharpEngine.Runtime/Components/RuntimeWorld.Spatial.cs:154`：导航直接写 Transform.Position。
- `Source/SharpEngine.Runtime/Components/RuntimeWorld.cs:334`：之后才运行统一动画/移动 finalize。
- `Source/SharpEngine.Runtime/Components/RuntimeWorld.Animation.cs:18`：收集移动权威。
- 同文件第 47 行和第 102 行：只抑制普通 Movement，没有处理 NavAgent。

在同一实体挂载 NavAgent 和一个“不移动且接管根运动”的最小 IMovementAuthority，运行一个固定步：

```text
REPRO navigation-bypass: before=<0, 0, 0>; authorityBegin=<0.050000004, 0, 0>; after=<0.050000004, 0, 0>; endCalls=1; diagnostics=0
```

进入权威 BeginMovementFrame 之前，实体已经被导航移动；没有组合冲突诊断。不是依赖随机调度的推测，而是固定阶段里已经发生的第二次写入路径。

影响：用户把导航和 CharacterMovement 等移动权威组合在同一实体时，最终位置、速度和导航观测可能不再由同一提交者决定。

建议让导航计算意图/期望速度，由移动权威最终提交；若当前不支持这种组合，应在加载或配置验证时明确拒绝。不要只增加接口，却继续保留两个实际写入位置。

## 二、当前架构地图

下表由 `.csproj` 引用、Host 入口和实际调用链核对，描述当前实现。

| 边界 | 当前职责与依赖 |
| --- | --- |
| Native Engine | Win32、OpenGL/Vulkan、RmlUi、输入适配、CLR/原生 ABI |
| SharpEngine.Host | 根据 Editor/Game/Server target 组合托管依赖和入口 |
| SharpEngine.Runtime | 组件描述、SceneData、RuntimeWorld/RuntimeFrame、子系统、输入、资产、渲染数据与通用 UI |
| SharpEngine.Editor | Authoring 事务、编辑命令、MCP、资产编辑器宿主、Dock、代码重载；依赖 Runtime |
| Timeline.Runtime | 播放和时间轴数据；依赖 Runtime |
| Timeline.Editor | Timeline UI 与编辑能力；依赖 Editor、Timeline.Runtime |
| Animation.Editor | 动画专用编辑器；依赖 Timeline.Editor、Editor |
| Networking | 命令、复制、传输和 DedicatedServerSession；只依赖 Runtime |
| CharacterMovement Plugin | 项目显式启用的移动模式/修饰器组合，接入 Runtime 的 IMovementAuthority |
| ComponentGenerator | 编译期生成描述符、默认值和字段适配；不是另一个运行世界 |

目前检查到的托管项目引用没有 Runtime → Editor 或 Timeline.Editor → Animation.Editor 反向引用。是否进一步拆分 Runtime，应由独立复用需求和变更耦合驱动，不能仅按目录大小判断。

核心状态流：

```text
Authoring 源文件 / SceneData
    → 事务候选 → 验证 → 当前文档 + revision + Undo/Redo
    → Play / Game / Server 重建 RuntimeWorld
    → 固定阶段更新 → 完整发布 RuntimeFrame
    → 渲染、Inspector/MCP、网络复制
```

| 状态 | 分类和所有者 |
| --- | --- |
| Authoring 组件配置、资产内容 | authoritative，所属文档；事务修改，显式保存 |
| Runtime 组件当前状态 | 当前模拟的活状态，完整 Tick/外部操作提交边界发布；不自动回写 Authoring |
| 子系统状态、UI 实例、视口相机 | 所属作用域或使用实例的 transient 状态 |
| RuntimeFrame、空间查询、渲染数据 | derived，基于已提交状态供消费者读取 |
| 原生句柄、GPU payload/cache | 可重建的运行资源/缓存，不是 Authoring 资产 |

## 三、已经落地的架构进步

### 通用能力能独立使用

`Viewport3D` 只依赖 UI/Rendering 契约。World 通过 `WorldViewportContent` 适配，`WorldPreview` 负责组合；观察相机属于视口实例。本轮 world-free 验证通过，说明下层能力不再必须制造一个临时 World 才能使用。

`UiLayoutTemplate` 与 `UiLayoutInstance` 分开，`IUiLayoutPart`/`IUiActionHandler` 表达 UI 领域的插槽和事件。骨骼动画编辑器组合 Viewport 与 Timeline，专用注册留在 Animation.Editor，依赖方向正确。

### 生命周期作用域有明确所有者

Engine、Editor、Instance、World 的子系统按所属生命周期隔离。注册、失败清理、冻结快照和 Play/Stop 已有实测。本轮 SubsystemValidation 的 44 项通过；A-02 说明当前缺口在 Server 接入，不能因此否定整个子系统实现。

### 运行结果能够被多个消费者复用

RuntimeFrame 冻结组件数据与子系统状态。网络复制从 Frame 读取，渲染也读取同一帧对应的数据，减少各消费者自行读取 Tick 中间态的机会。Networking 独立于 Editor，传输与权威 Session 分开，方向合理。

### Module、Plugin、组件和普通对象没有被合并成万能容器

构建/注册、分发/启用、World 生命周期和普通功能组合各自有边界。当前无需为每个组合节点再建 Module、Plugin 或 MCP 能力。

## 四、需要继续关注的架构风险

以下项目尚未测得性能退化或发布失败，不作为已复现故障。

1. **GPU 场景能力还没有进入通用视口协议。** 主 Editor/Game 路径可提交 retained GPU scene；`Viewport3D` 接口仍接收展开后的三角形/线数组，World 预览调用 `SceneRenderer.BuildFrame` 做 CPU 几何准备。打开多个复杂资产预览时可能放大开销。应考虑渲染中立的场景/绘制描述及宿主能力协商，不能把 World 重新塞回通用视口接口。
2. **发布、复制与 dirty 查询仍有全量处理。** RuntimeWorld.Capture、RuntimeFrame 构造和 Snapshot 会复制场景；Game 再应用 Frame.Snapshot；每连接网络快照分别构建状态；WorldAuthoringSession.IsDirty 序列化整个文档。应先测实体/组件规模、连接数、每帧分配与耗时，再选择共享不可变结构或增量缓存。
3. **RuntimeWorld 仍集中编排具体领域。** 控制选择、导航、动画和移动都由它直接协调。固定阶段由引擎所有是正确的；后续可把具体领域执行器封装在下层公共契约之后，同时保留统一阶段和唯一提交边界。不要引入项目可任意重排的第二套组件调度。
4. **Cook 仍是独立未完成交付面。** 文档明确包装/Cook 另行建设；局部 `Validate(cook: true)` 不能证明依赖闭包、输入指纹、确定性输出和发布流程已经完成。

## 五、与 10 月 18 日审查对照

| 旧项目 | 本轮判断 |
| --- | --- |
| Vulkan 丢失 Z | 父仓源码已传递 Z 并调用 RenderGeometryWithDepth；retained Vulkan 管线启用深度测试。本轮未做原生画面对比，不能直接宣布跨后端验收完成 |
| Authoring 绕过事务 | 仍存在，本轮补充运行复现，并确认 staging 别名路径 |
| Robot FBX 换行导致哈希失败 | 当前 Robot mesh/skeleton 已自包含；MeshAssetValidation 787 项通过，旧失败不再重现。不是证明所有源资产跨检出哈希都已解决 |
| 全量处理性能风险 | 仍有多处复制/序列化，但不能沿用“所有主渲染都在 CPU”的旧结论，主路径已经有 retained GPU scene |
| 缺少通用版本迁移、引用删除策略 | 最新契约已明确 v3/current-version 策略及普通引用删除时清空；不能继续把不支持旧版本或缺少 restrict/cascade 当作违约问题 |

## 六、验证记录

引擎源码基线为上面的 `3e2df4a`；原生子模块工作区另列限制。验证配置为 Debug，使用仓库既有项目引用和构建输出规则。

| 验证 | 结果 |
| --- | --- |
| Viewport3DValidation | 42 项通过 |
| SubsystemValidation | 44 项通过；输出中的 expected update failure 是故障注入用例 |
| GpuSceneValidation | 18 项通过；只证明托管 payload/cache，不证明 GPU 原生资源生命周期 |
| MeshAssetValidation | 787 项通过 |
| NetworkValidation | 34 项通过；含 UDP 故障注入和独立进程服务器/客户端验证 |
| WorldAuthoringValidation | 26 项通过；正常事务路径通过不代表无法绕过事务 |
| 本轮 ArchitectureReview 复现 | 5 项检查确认上述缺陷行为，属于缺陷复现成功，不是修复通过 |

六组既有验证合计 951 项通过。

复现命令：

```powershell
dotnet run --project Tools/Viewport3DValidation/SharpEngine.Viewport3DValidation.csproj -c Debug -- D:/SharpEngine
dotnet run --project Tools/SubsystemValidation/SharpEngine.SubsystemValidation.csproj -c Debug -- D:/SharpEngine
dotnet run --project Tools/GpuSceneValidation/SharpEngine.GpuSceneValidation.csproj -c Debug -- D:/SharpEngine
dotnet run --project Tools/MeshAssetValidation/SharpEngine.MeshAssetValidation.csproj -c Debug -- D:/SharpEngine
dotnet run --project Tools/NetworkValidation/SharpEngine.NetworkValidation.csproj -c Debug
dotnet run --project Tools/WorldAuthoringValidation/SharpEngine.WorldAuthoringValidation.csproj -c Debug -- D:/SharpEngine
dotnet run --project Saved/Validation/ArchitectureReview/ArchitectureReview.csproj -c Debug
```

本轮独立复现代码保存在 `Saved/Validation/ArchitectureReview`，属于本地验证产物，没有进入正式验证工程或修改现有测试。

原生验证边界：

- 父仓当前要求 RmlUi commit `d1247d6c94d8192dbbcb9cd4cc82e023bb9e6117`。
- 本地 RmlUi 仍在 `821cbd229d804cb1c905b729af86c21133abceee`，且保留原有大量删除状态。
- `External/RmlUi/CMakeLists.txt` 和 `Backends/RmlUi_Renderer_VK.cpp` 在当前工作区缺失。
- 本轮没有恢复或重置子模块，也没有执行原生 Editor/Game/Server 构建、真实 UI 输入或 GPU 画面对比。
- 没有性能基准、完整 Cook 验收或全仓安全审计。

## 七、建议处理顺序

1. 统一 Server 与 Game/Editor 的生命周期契约，补三个 target 共用的初始化、更新和释放顺序验收。
2. 收紧 Authoring 对外可变对象和 staging 别名，确保版本、Undo/Redo、Save 观察的是同一套已提交状态。
3. 明确 NavAgent 与 IMovementAuthority 的组合策略，消除或拒绝第二个 Transform 写入者。
4. 对多视口、复杂场景和多连接做规模测试，再决定 GPU 预览协议与增量快照的设计。
5. 把 Cook 作为单独的交付闭环推进。

以上为修复前的审查建议。前三项的本轮实现、验证及手册更新见本文开头“修复跟进”；性能和 Cook 工作尚未实施。
