# Inspector、组件与 Runtime 设计共识

状态：本轮组件、Inspector、Runtime 与 Prefab 主线已实现；验证与交付范围见 [实现记录](InspectorComponentImplementation.md)。本文保留讨论中确认的设计规则，后续发现实际问题时再明确修订。

## 1. 目标与范围

本次覆盖 Inspector 添加组件、自动展示配置字段、修改配置、保存，以及进入 Play 后构建并运行组件。同时包含 Runtime 动态实体/组件创建与移除，以及独立 Prefab 资产和编辑文档。

- 第一版 Inspector 仅支持单实体选择。
- 引擎内建组件与项目组件使用统一机制。
- Authoring 与 Runtime 分离，UI 与 AI 使用共享的语义修改命令。
- 当前没有完成的组件系统、Prefab 或热重载能力，不因本文出现设计名称而视为已有实现。

## 2. 实体与组件

- 实体是组件容器，不再以互斥的 Mesh/Camera/Light/Empty 种类作为权威模型。
- 每个实体必须且只能拥有一个 TransformComponent；不可删除，不可单独禁用。
- Transform 不要求在 Inspector 第一位。
- MeshRendererComponent、CameraComponent、DirectionalLightComponent 和项目组件按需组合。
- 所有组件统一继承 SharpComponent，不保留 SharpBehaviour 或数据/行为组件的基类分层。
- 组件均具有完整生命周期，未重写的回调为空实现。
- 默认允许同类型组件重复；显式标记 DisallowMultipleComponent 才禁止重复。
- 组件排列只表示展示顺序，可以调整并保存；Authoring 重排进入 Undo/Redo。
- 初始化、更新、依赖解析和组件查询均不得将面板顺序解释成执行优先级。
- 第一版不提供 Runtime 面板重排。
- GetComponent<T>()：无匹配返回 null，一个匹配返回实例，多个匹配报歧义错误。
- GetComponents<T>() 返回全部匹配；特定目标使用实例 ID 或显式引用。

## 3. 类型发现与身份

- Runtime Module 自动发现符合要求的 SharpComponent 派生类型，不要求项目手写注册代码。
- 可实例化组件类型公开、非抽象、非泛型，位于 Runtime Module。
- Editor Module 提供编辑器扩展，不定义运行时场景组件。
- componentTypeId 是稳定类型身份，以 ComponentTypeId 特性保存在源码中，不依赖 CLR 名称或文件路径。
- 创建组件脚本的工具自动生成类型 UUID；开发者不手填。
- 缺少类型 ID 或重复 ID 产生构建诊断；复制源码后必须为新类型生成新的 ID。
- componentId 是实例身份，由引擎添加组件时自动生成。
- Authoring 重建出的 Runtime 对象保留对应身份；运行时新建对象获得新的唯一 ID，Stop 后不持久化。
- 暂不引入 .cs.meta 源码身份系统。
- 类型名称、显示名、菜单分类与稳定身份分离。

## 4. 公开配置字段

采用 Unity 风格字段筛选规则：

- 支持类型的 public 实例字段默认参与序列化和 Inspector。
- 非 public 字段可用 SerializeField 显式加入。
- 普通属性、static、const、readonly 默认不参与。
- NonSerialized 排除序列化。
- HideInInspector 只隐藏 UI，不删除序列化状态。
- 不支持的序列化字段类型产生构建错误，不静默忽略。
- Enabled 等引擎公共状态由基类管理，不属于项目字段扫描范围。

第一版支持：

- bool、整数、float、double、string、enum。
- Vector2/3/4、Quaternion、Color。
- 可空实体引用、可空组件引用、AssetRef<T>。
- Serializable 嵌套数据类和结构体。
- 受支持元素类型的一维数组与 List<T>。

第一版不支持 Dictionary、集合接口、任意自定义集合、object/接口/多态字段、委托、事件、任务、流、线程、原生句柄、多维数组及 SerializeReference 式托管引用图。

嵌套数据按值保存，允许 null，拒绝循环对象图。两个字段即使在内存中指向同一个嵌套数据对象，重建后也不保证共享身份。实体、组件和资产引用另按稳定 ID 处理。

## 5. 构建期生成

使用 Roslyn Source Generator 提供 Unity 式字段声明体验，并在构建期生成组件元数据。

生成内容包括组件/字段描述、schema、默认状态、字段访问代码、序列化适配、Runtime 工厂与注册入口。Inspector 控件、事务和生命周期调度由引擎统一实现，不逐组件生成 UI。

- 组件声明为 partial；创建脚本工具自动提供该声明。
- 含有需序列化私有字段的嵌套数据类型也需要 partial，以便生成代码合法访问私有成员。
- 生成器追加编译输入，不改写用户源文件。
- 字段顺序记录源码声明顺序，不依赖 CLR Reflection 枚举顺序。
- 字段初始化器定义默认值，构建期生成确定性默认状态。
- 无初始化器的字段使用对应 CLR 默认值，并在资产中显式保存。
- 可接受的初始化表达式采用确定性支持范围；时间、随机数、文件 IO 和任意方法调用不能作为自动提取默认值的通用途径。
- Authoring 不构造项目组件，不通过执行项目构造函数探测默认值。
- 项目组件显式实例构造函数属于构建错误；初始化逻辑进入生命周期。
- ProjectBuild 元数据检查不执行项目代码。
- 生成清单和适配代码是派生产物；源码、源资产才是权威数据。

## 6. 格式与版本

- .sworld v3 使用严格 UTF-8 JSON，保留 .sworld 扩展名。
- 记录 World、实体、组件身份，以及组件类型 ID、组件 schemaVersion、公共状态和 state 字段。
- 配置字段显式保存，包括等于默认值的字段；输出确定性序列化结果。
- 拒绝重复 JSON 字段、已知 schema 中的缺失/未知/非法字段和不支持的版本。
- 预稳定阶段只支持最新资产格式。本次仓库内 World、模板和测试直接升级 v3。
- 不保留 v2 加载、迁移工具或自动修复，不把旧格式静默解释为新默认值。
- 组件同样使用显式 schema 版本；脚本工具生成初始版本 1，不兼容变化由作者提升版本。
- 预稳定阶段不提供组件迁移函数，不接受与当前描述符不匹配的组件版本。
- 未知组件类型的原始记录仍在文档层保留，不实例化、不执行、不显示成可用组件；与已知类型的非法字段区分处理。
- 本次已确认的预稳定政策已同步到开发技能；版本支持按具体格式声明，不套用全引擎统一迁移要求。

## 7. Authoring 与 Runtime

- Authoring 只有组件数据记录，不实例化项目组件，不执行组件生命周期。
- 添加、修改、Reset、删除和重排通过 World/Editor 线程上的共享事务实现。
- Authoring 修改带 expectedRevision，原子校验与提交，更新 Dirty/revision，并进入 Undo/Redo。
- 显式 Save 与修改分离，不自动保存。
- Play 从最新已提交的 Authoring 内存状态创建独立 Runtime World，不要求先保存文件。
- 创建对象后注入身份、所有字段及引用，再执行生命周期。
- Runtime 代码允许直接修改普通字段，不强迫项目使用 SetField API。
- 完整 Tick 是 Runtime 对外一致性边界；Inspector/AI 不读取执行到一半的帧状态。
- Runtime Inspector 修改和结构变化也产生独立提交边界与 revision。
- Runtime 修改不进入 Authoring 历史、不标记 Authoring Dirty、不自动回写源资产。
- Stop 丢弃 Runtime World，恢复 Authoring 视图。
- 第一版不提供 Apply Runtime Changes，也不允许 Play 时同时编辑 Authoring。

## 8. 生命周期与激活

固定回调：OnInitialize、OnEnable、OnStart、OnFixedUpdate、OnUpdate、OnLateUpdate、OnDisable、OnUninitialize。

对完整 World 或 Prefab 创建批次：

1. 构造全部实例，注入身份、字段并解析引用。
2. 完整图就绪后，对全部新组件执行 OnInitialize。
3. 全部初始化调用完成后，对实际激活组件执行 OnEnable。
4. 完成本阶段后，对首次激活组件执行 OnStart。
5. 从后续 Tick 参与更新。

- 同一阶段内不保证组件之间的执行先后，跨组件工作依赖阶段屏障。
- OnInitialize 与激活无关，组件进入 Runtime 后执行一次；不能假定其他组件在本阶段已经初始化。
- OnStart 前保证本批初始化阶段已执行完毕；不保证其他组件初始化没有抛异常。
- 后来动态添加的组件只能依赖当时已存在及同批创建的组件，不能假定未来组件已存在。
- 初始未激活组件仍完成 OnInitialize，并保持可查询状态；首次激活才执行 OnEnable/OnStart。
- OnStart 一生只调度一次；再次激活只执行 OnEnable。
- Pause 停止时间推进和更新，不调用 Disable 或清理回调。
- Stop/移除对实际激活组件执行 OnDisable，再执行 OnUninitialize。
- OnUninitialize 期间仍能访问清理所需的 Entity/World 上下文；之后断开注册关系。

Enabled 是基类拥有的受控属性。setter 记录期望状态，在明确的安全边界处理，不在当前回调栈嵌套执行生命周期；同一边界内多次赋值按最终状态转换。

实体提供可序列化 ActiveSelf 和派生 ActiveInHierarchy。实际激活条件由 IsAlive、Enabled 和 ActiveInHierarchy 共同决定；父节点激活影响整棵子树，但不推迟 OnInitialize。

第一版固定步长为 1/60 秒，可设置能力后续实现，本次不增加 World 设置项。累积时间决定每帧执行零次或多次 FixedUpdate，随后 Update、LateUpdate。暂停调试单步推进一个固定步长并执行一轮 Update/LateUpdate。

## 9. Runtime 动态结构

- 支持 CreateEntity、DestroyEntity、AddComponent 和 RemoveComponent。
- 可以先创建只有 Transform 的有效实体，再逐个添加组件，不强制把实体的最终组件集合一次提交。
- 动态 Add 默认使用描述符默认状态，也可在 OnInitialize 前提供类型化 Initial State。
- 在 Tick 之间执行的外部 Runtime 命令可以直接完成；生命周期内部请求排队到安全边界，避免嵌套生命周期和遍历重入。
- 排队操作通过 mutation handle 返回结果，成功提交后才可取得正式存活实例。
- 结构校验在提交前完成，冲突显式失败。
- DestroyEntity 连同整棵子树销毁；需要保留的子实体应先显式移出。
- 子树清理完成后统一移除，并清空外部序列化引用。
- Runtime-only 对象在 Hierarchy/Inspector 可见，Stop 后丢弃；其选择清空，已有 Authoring 对应对象可按 ID 恢复选择。

## 10. 依赖、引用与回收

RequireComponent 是本次正式范围，仅支持静态类型依赖。

- 添加前计算传递依赖，补齐缺失组件并校验重复限制、类型可用性。
- 注册时拒绝循环和自依赖。
- 添加及依赖补齐属于同一次结构操作；Authoring 对应一次 Undo 事务。
- 仍被 RequireComponent 依赖的组件不可单独移除，并显示原因。

项目代码使用 SharpEntity? 和具体组件的可空直接引用，资产使用 AssetRef<T>。实体/组件引用序列化为稳定 ID，Runtime 重建时解析成实际对象。

- 不提供 EntityRef/ComponentRef 包装器。
- 删除目标时，引擎清空已序列化字段中的对应引用；Authoring 清空与删除进入同一事务，Undo 恢复两者。
- 不采用引用字段的 Restrict/Nullify/Cascade 策略，普通引用不阻止目标删除。
- NonSerialized 字段和项目自有缓存由项目代码负责。
- 逻辑移除后 IsAlive 为 false，目标不能再从 World 查询，受控引擎操作拒绝已移除实例。
- 引擎释放自身注册和强引用，实际内存回收交给 CLR，不调用 GC.Collect，不依赖终结器完成生命周期。
- 普通 C# 字段/方法仍受 CLR 语义约束，引擎不能阻止缓存持有者调用任意项目方法。
- 原生资源和事件订阅应在 OnUninitialize 处理。

## 11. 校验与异常

提供无实例的静态 Validator，接收生成的只读状态和诊断输出，供跨字段校验使用。Validator 契约要求无副作用，不访问文件、网络、时间或随机数。

- 默认值、加载、Add、Reset、外部 Patch、Play 重建及 Cook 使用通用和自定义校验。
- 输入校验失败时拒绝提交，不执行生命周期，不悄悄 clamp 或部分接受字段。
- 项目 Runtime 直接字段写入不逐次或逐帧调用 Validator；Range 不自动限制普通 C# 赋值。
- Inspector/快照展示 Runtime 真实状态，运行时业务不变量由项目负责。

生命周期异常策略已修订为：

- 捕获并记录对象、回调阶段、异常及堆栈，不让异常越过托管宿主边界。
- 不因回调异常回滚状态、撤销副作用、删除新对象、自动禁用组件或标记 Faulted。
- 初始化不自动重试，后续 Enable/Start/Update 等回调仍按正常状态调度尝试。
- 不保证抛异常后的业务表现正确，由用户感知并修复。
- Disable/Uninitialize 抛异常也只记录，其他对象的清理继续。
- 以上策略替代此前讨论中的“初始化异常回滚、Tick 异常隔离停用”建议。

## 12. Inspector 交互

- Stopped 编辑 Authoring；Playing/Paused 编辑 Runtime，并明确显示上下文。
- 文本/数值先进入本地输入缓冲，Enter 或失焦提交，Esc 放弃未提交内容。
- 无效输入留在控件并显示错误，不改变模型。
- checkbox、enum 和引用选择完成后立即提交。
- slider/拖拽连续更新，但一次手势合并成一个 Authoring Undo 操作。
- Vector/Color 一次复合编辑原子提交。
- 提交检查 expectedRevision，冲突不得静默覆盖。
- 支持 Header、Tooltip、Range、InspectorName、Multiline、HideInInspector、SerializeField 和 NonSerialized。
- 通用控件覆盖数值、枚举、向量、颜色、引用选择、可增删重排的列表，以及可折叠嵌套数据。
- 第一版不提供自定义 Inspector/Property Drawer。

Add Component 提供可搜索分类菜单，搜索显示名、CLR 类型名、Module 和菜单路径。AddComponentMenu 可选；未声明项目类型归入 Scripts/<ModuleName>。禁止重复的已有类型禁用，缺失依赖会列出并补齐，Transform 不进入添加菜单。

组件面板提供折叠、显示名、适用的 Enabled 和更多菜单。第一版菜单有 Reset/Remove，没有 Copy/Paste。Reset 使用描述符默认配置，并通过当前上下文的修改边界执行。折叠状态属于 Editor UI，不写入 World/Prefab。

## 13. Prefab 与文档隔离

- 引入独立 .sprefab JSON 资产，包含一个根实体及可选子树，复用 World 的实体/组件记录结构。
- 实例化时重建全部对象身份，重映射内部引用，完整图就绪后执行生命周期。
- 第一版为独立深拷贝实例，不维护实例实时链接、Override 或 Apply/Revert。
- Prefab 在独立 Dock/Tab 编辑，拥有独立 Authoring 上下文。
- World/Prefab 文档分别持有选择、Undo/Redo 和 Dirty；共用 Inspector 跟随活动文档，Save 保存活动文档。
- Prefab 的实体/组件选择器只展示自身对象；资产选择器可以使用外部资产。
- Play 运行当前打开的 World 并切到其 Runtime 视图，不独立运行 Prefab 编辑文档。

创建与实例化入口：

- Content Manager 新建空 Prefab，含根实体和 Transform，在独立 Tab 编辑。
- Hierarchy 将选中实体及子树保存为新 Prefab，原场景对象保持不变。
- 导出时若存在对子树外场景对象的引用，报错并定位字段，要求先解除，不静默清空。
- 加载/保存仍校验引用范围，不能只依赖选择器约束。
- 从 Content Manager 拖 Prefab 到 Hierarchy/Viewport；Stopped 创建可撤销、可保存的 Authoring 副本，Playing/Paused 创建 Runtime 临时实例。

## 14. 后续代码热重载

本次不实现代码热重载，但组件元数据和实例绑定必须能随 Module 代际释放。

- 已接受方向：方法体变化争取保留 Runtime 实例进行 Play 中热更新；字段/schema 结构变化停止 Runtime，以新 Module 代际重建。
- Registry 的持久身份使用 componentTypeId；Type、工厂委托及实例不能泄漏到长生命周期缓存而阻止旧代际释放。
- 描述符与程序集必须属于同一构建/代际。
- 未来独立加载上下文仍需共享引擎契约程序集，避免 Module 和组件基类的 CLR 类型身份分裂。
- 方法体热更新的可用范围需在实现时验证所用 .NET 工具链，不把本方向当作已通过的技术验证。

## 15. 实施与验证顺序

按依赖推进，不将已纳入范围的 Runtime/Prefab 能力误归为延期项：

1. Runtime 公共契约、组件描述符、字段/schema 与 Source Generator。
2. World v3 数据层、默认值、引用和静态校验、序列化。
3. Authoring 通用事务、Undo/Redo、增删/修改/重排与显式保存。
4. Runtime World 重建、生命周期、激活、固定步长、动态实体与组件、异常记录。
5. Inspector 输入/列表/引用/添加菜单及上下文切换。
6. Prefab 资产、独立文档 Tab、导出与拖放实例化。
7. UI/AI 共享命令和快照、内建组件与仓库资产升级、回归验证。

验收覆盖默认/非默认值往返、确定性输出、未知记录保留、严格版本拒绝、事务与 Undo、内存状态进入 Play、引用重映射与清空、初始化屏障、动态排队、异常后继续调度、Prefab 文档隔离，以及真实 UI 输入路径。

语义命令或单元测试通过不代表 Inspector 实际可点击；UI 验收需实际输入、同帧语义快照和渲染证据。Cook 相关数据校验遵守已有资产契约，不以本设计文档暗示完整 Cook 管线已经实现。
