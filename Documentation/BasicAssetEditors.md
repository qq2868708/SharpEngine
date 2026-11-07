# 基础资产编辑器

## 性能验收

JSON 资产的 dirty 状态在文档变更时计算，未变化的源文本与 Source 预览复用派生缓存；
编辑、Undo/Redo 和 Save 后立即反映最新已提交内容，跨保存点仍按内容判断 dirty。
这些缓存不写入源文件，不改变资产版本或外部文件冲突检查。

性能是编辑器变更的验收条件。相同项目、资源、构建配置、后端、VSync、调试器和窗口条件下，
保留平均/P95 帧时间与真实导航响应；功能通过不能掩盖可复现的性能下降。
`Tools/Assets/ValidateEditorPerformance.py --record-baseline --output <目录>` 记录独立会话基线；
候选版本使用 `--baseline <基线目录/report.json> --output <新目录>` 对照，并保留完整 MCP 目录、
原始 timing、原生滚轮导航结果和匹配截图。默认目标为 Debug/OpenGL/VSync on，
平均帧时间不超过 18 ms、P95 不超过 25 ms；基线比较的 5% 只用于识别测量波动，
持续退化仍须定位修复。该工具仅启动与清理自己的测试会话，不替换用户编辑器。

## StaticMesh Socket

StaticMesh 编辑器左侧为 Sockets，中央为全高 Preview，右侧为 Details；
Mesh / StaticMesh / SkeletalMesh 均不注册 Source 面板，Diagnostics 默认隐藏，可通过 Window 打开。
Details 可在所属资产编辑器内停靠或脱离为浮动窗口，字段尺寸按其实际宿主测量。

- 层级树以不可修改的 **Mesh** 为根。根可选中，但不能重命名、删除、复制、改父级或使用变换 Gizmo。
- 选中 Mesh 后使用 **Add Socket** 创建根的子 Socket；选中 Socket 后创建它的子 Socket。树中按实际父引用缩进显示全部层级。
- Details 编辑名称、Position、Rotation (degrees) 和 Scale。Transform 组中的 Position、Rotation、Scale 各使用 X/Y/Z 三个输入框；
  旋转按 X pitch / Y yaw / Z roll 输入，保存为单位四元数；缩放每个分量必须大于零。
- Socket 的 TRS 相对其 Parent；Parent 可选 Mesh 根或另一个 Socket，不能选择自己或自己的子孙。
  修改父级保留原来的网格空间变换；旋转与非均匀缩放造成无法无损表示的局部 TRS 时，拒绝修改并显示诊断。
  父级变换会逐级影响所有子孙，子孙的局部数据不被改写。ID 在重命名、改父级后保持稳定，名称和 ID 在资产内分别唯一。
- **Move / Rotate / Scale** 使用与 Skeleton 共用的 Gizmo 绘制、拾取和拖动计算。
  操作手柄仅显示于选中的可写 Socket；选择 Mesh 根时隐藏。其他 Socket 保留父子连线，不绘制常驻 RGB 局部轴。
  轴跟随 Socket 局部方向；拖动只更新临时预览，松开一次提交。Esc、失焦或丢失捕获取消；
  点击空白、未移动的手柄点击不产生历史。修改与 Undo/Redo 保留当前预览相机。Focus 包含网格与 Socket 的网格空间位置。
  拖动候选只重新验证 Socket 图，不复制或解析几何；未变化的树和 Details 按文档/交互版本缓存；宿主也在布局或面板内容变化时才重新组合 Dock 文档。
- 文本编辑期间保留原生输入控件与焦点；Enter 或失焦校验后提交一次，Esc 放弃草稿，结束编辑后再刷新树与 Details。
  Socket 文本字段携带稳定目标 ID；失焦、Enter 或 Esc 结束文本编辑，不将旧字段事件应用到新选中的 Socket。
- Socket 菜单或工具栏的 **Duplicate Selected / Remove Selected** 复制或删除所选 Socket；复制生成独立 ID 和唯一名称，保留同一父级，仅复制当前节点。
  **非叶子 Socket 禁止删除**：先移走或删除其直接子级，再删除父节点。UI 禁用删除，语义命令同样原子拒绝；不级联删除，也不自动提升子级。
- Undo/Redo 与 dirty 属于当前文档；**Save** 才原子写入文件，外部修改冲突时拒绝覆盖。
  引擎只读资产不可编辑。该功能提供 Socket 定义与预览，不新增运行时对象挂接接口。

资产 `StaticMeshAsset` schema v3 显式保存 `sockets` 数组。每项包括
`socketId`、`name`、`parentSocketId`、`position[3]`、`rotation[4]`、`scale[3]`；`parentSocketId: null` 表示 Mesh 根，字符串引用同资产内的父 Socket ID。
读取 v1（含旧 Mesh JSON）时视为空集合；读取 v2 时，原有 Socket 全部迁移为 Mesh 根的子级。
读取不修改源文件或 dirty。StaticMesh 编辑后或显式保存时写入 v3；普通 Mesh 的默认材质编辑保留其既有格式。
未来版本、缺失父字段、悬空父引用、自引用/循环、重复 ID/名称、未知字段、非有限变换或非法缩放拒绝加载，不静默丢弃。
`StaticMeshSocket.LocalMatrix` 为相对父节点的局部矩阵；`StaticMeshSocketHierarchy.ModelMatrices` 为只读网格空间累计矩阵，使用 `local * parent`。
Mesh 根始终为恒等矩阵；Socket 不改变几何数据。层级求值不依赖几何，可被运行时及编辑器独立使用，不新增对象挂接生命周期。

网格编辑器快照 schema v3 公开 `document`、`socketCount`、`selectedSocketId`、
`stateRevision`、`documentRevision`、`diagnostics`、层级 schema v1 的不可修改根和扁平父引用节点，以及 `socketPose` schema v1 的累计矩阵。
Gizmo schema v2 增加父引用，以 `previewPose` 替代旧 `previewDocument`，仅描述候选 Socket 矩阵；
`coordinateSpace: socket-local` 表示手柄轴方向，`transformSpace: parent-local` 表示被编辑的局部 TRS 空间。
`editor.mesh-socket:select-root` / `select:<id>` / `add-socket` / `duplicate` / `remove` / `field:<name|parentSocketId|position|rotation|scale>:<value>`
与携带 URI 编码目标 ID 的 `field-for:<id>:<name|position|rotation|scale>:<value>`
与 UI 共用文档编辑入口；工具选择与 Undo/Redo 使用既有 `editor.asset-command:*`。
`gizmo.previewTransform` / `previewPose` 仅是拖动期间的已验证候选，不进入作者文档。

聚焦真实输入验证（测试工具创建并清理可见的隔离 Editor，不附加到用户会话）：

```powershell
dotnet build Tools/McpUiValidation/SharpEngine.McpUiValidation.csproj -c Development
dotnet Tools/McpUiValidation/bin/Development/net8.0/SharpEngine.McpUiValidation.dll Development --static-mesh-sockets-only --render-backend=opengl --render-vsync=off
```

将 backend 改为 `vulkan` 可验证另一后端。功能输入和 PNG 捕获完成后，先预热 30 秒清除 240 帧历史并稳定运行，再以 5 秒间隔采样三次；
嵌套 Socket 窗口的帧预算为平均 18 ms / P95 25 ms，不能把功能 RPC/截图阶段的开销当成闲置渲染性能。
追加 `--functional-only` 仅运行功能输入与像素验证，输出明确标记不包含性能；该结果不能替代帧预算验收。匹配 UI 快照、编辑器状态与 PNG 保存于
`Saved/Validation/BasicAssetEditors/staticmesh-gizmo-*`；连续运行两后端时分别保留副本。
语义验证使用 `Tools/MeshAssetValidation` 和 `Tools/EditorValidation`，不代替 Cook 或发布包验证。
MCP UI 验证在附加会话并收到目录变更通知后，重新读取完整 tools / resources / templates 目录，
原始 JSON 与会话身份保存在 `Saved/Validation/McpUiCatalogs/<sessionId>`，不压缩目录或省略注册信息。

## 动画状态机

Animation.Editor 模块注册 `engine.animation-state-machine-editor`。
保留已有 `timeline-editor` 窗口身份及 Preview / timeline / Details 面板 ID，
其中 timeline 标签显示为 States & Transitions；Preview 显示兼容模型的实际动画。
Details 提供名称、初始状态、状态 Clip、循环与速度、参数默认值、有序转换、条件及混合设置。

- 工具栏可添加状态、参数和转换；转换至少需要两个状态，条件至少需要一个参数。
- 状态重命名同步更新初始状态和转换端点；参数重命名同步更新条件引用。
- Remove State + Transitions 一次删除状态及关联转换。若删除初始状态，则以剩余首个状态为初始状态；
  不能删除最后一个状态。被条件引用的参数不能直接删除。
- 转换按列表顺序首次匹配。Move Up / Move Down 修改真实的执行优先级。
- `exitTime` 可留空表示 null；比较支持 Greater、GreaterOrEqual、Less、LessOrEqual、Equal、NotEqual。
- `blendDuration` 是混合时长（秒），`0` 表示瞬切；点击 `blendCurve` 按钮在 `Linear` 和 `SmoothStep` 之间切换。
  新增过渡默认 0.2 秒 SmoothStep；内置 mannequin locomotion 同样配置为 0.2 秒。
- 编辑先验证完整 schema、引用和 Clip 依赖；错误显示在 Details 中且不改变文档、revision 或历史。
- Undo/Redo、dirty、Save 都属于当前文档。Save 才原子写入文件；外部文件变化时拒绝覆盖并保留未保存内容。
  引擎自带只读预设仍只能查看。

试调时，先在 States & Transitions 顶部的 **Preview Parameters** 输入临时数值，再点击
Play 或 **Step 1/60s**，条件满足后会开始切换。当前状态高亮，Preview 显示源→目标和混合进度条。
Pause 冻结进度，Reset 返回初始状态并取消过渡。
**Switch Preview State** 使用首条对应过渡，**Test Transition** 测试指定边；
这两种手动试播忽略自动条件和退出时间，且要求当前未混合、源状态匹配。
Details 的 Preview Mesh 只列出兼容所有状态 Clip 的模型。预览复用通用 Viewport3D，
支持相机旋转、平移、缩放与 Focus；相机不随每帧动画自动重新取景。
预览不会执行 Notify；混合不会打断正在进行的过渡。
播放、参数、模型和相机属于每个窗口独立的临时会话，不进入资产或 Undo/Redo。
作者修改、撤销或重做后暂停并重建预览，临时参数恢复文档默认值。
无兼容模型时显示诊断，文档仍可编辑。引擎只读预设可试播但不能修改作者数据。

状态机源文件 schema v2 支持显式读取 v1，迁移为零秒 Linear 后保留原行为，保存时写出 v2。
编辑器快照 schema v4 新增 `preview` schema v1，可观察独立 revision、参数、混合状态、兼容模型候选和派生姿态。
预览错误同时以 `diagnostic` 文本和结构化 `diagnostics` 数组提供，
使用 `ANIMATION_PREVIEW_UNAVAILABLE` 或 `ANIMATION_PREVIEW_INVALID`，不修改作者数据。
运行时与预览共用 `AnimationStateMachinePlayer`；根运动与打断边界见
[动画与时间轴](AnimationAndTimeline.md#状态过渡与混合)。

针对状态机的真实界面输入与匹配 PNG 验证：

```powershell
dotnet build Tools/McpUiValidation/SharpEngine.McpUiValidation.csproj -c Development
dotnet Tools/McpUiValidation/bin/Development/net8.0/SharpEngine.McpUiValidation.dll Development --state-machine-only --render-backend=opengl --render-vsync=off
dotnet Tools/McpUiValidation/bin/Development/net8.0/SharpEngine.McpUiValidation.dll Development --state-machine-only --render-backend=vulkan --render-vsync=off
```

先用标准 `Tools/SharpEngineBuild.ps1 -Target Editor -Configuration Development -RenderBackends Both`
构建当前 Editor；验证工具只创建并清理自己的隔离进程。
`Tools/AnimationValidation` 覆盖实际 RuntimeWorld 的混合姿态和热重载；
这些检查不代替完整 Cook 或发布包验收。

## 图片与音频源文件

音频首次播放时建立临时副本，避开旧式 Windows 播放驱动的文件名长度限制；关闭或重新加载时释放设备并删除副本，原始源文件保持只读。

已登记的 Texture/Audio 通过普通资产打开器进入预览。未登记的 Content 图片和音频
也能双击预览，单击只选择。预览不自动修改资产清单。

未登记文件使用临时 `source-file://<engine|project>/<编码后的浏览路径>` 身份，
只存在于当前编辑器会话及 `editor.describe.assetTools.editors`；
它不进入 `assets.describe`，不作为运行时资产引用。插件 Content 使用所属浏览器的路径解析。
`EditorAssetOpenContext.IsSourceFile` 可供打开器判断这一边界。
公开快照仅包含逻辑相对路径，不包含临时预览文件或本机根目录。

图片支持 Windows PNG/JPEG/BMP 解码和无色表的 24/32 位 TGA（未压缩或 RLE）。
原始尺寸单独保留；预览最长边最多 1024 像素，源文件限制 256 MiB / 64 megapixels。
为兼容 OpenGL/Vulkan 的 RmlUi 图片加载器，生成编辑器持有的临时 32 位 TGA，
关闭或 Reload File 时清理。切换明暗背景有助于检查透明区域；原图不被改写。

WAV 显示时长、声道、采样率、位深；PCM 8/16/24/32 位及 IEEE float32 提供
128 栏采样波形（最多检查 65536 个均匀分布的采样帧，并非精确峰值分析）。
Play From Start / Stop 使用各预览实例自己的 Windows 媒体设备别名，关闭或重新加载时释放。
WAV/MP3 试播依赖本机媒体驱动；MP3 时长在打开播放设备后取得。
OGG 目前只显示文件格式和大小，不解码、不播放。格式损坏、缺失源文件和播放失败显示明确诊断；
修复文件后可以 Reload File 恢复。

## 验证

语义测试入口：

```powershell
dotnet run --project Tools/EditorValidation/SharpEngine.EditorValidation.csproj -c Development -- .
```

真实输入与渲染测试使用自己创建的隔离项目及 Editor，不绑定用户会话：

```powershell
./Tools/SharpEngineBuild.ps1 -Target Editor -Configuration Development -RenderBackends Both
dotnet run --project Tools/McpUiValidation/SharpEngine.McpUiValidation.csproj -c Development -- Development --basic-asset-editors-only --render-backend=opengl --render-vsync=off
```

可将 backend 改为 `vulkan`。`--media-only` 仅运行图片、音频和未登记源预览检查。
完整模式验证状态添加、Undo、字段键盘输入、Save、图片像素、
背景切换、波形像素、播放与停止、未登记源文件选择及双击、关闭。匹配的 PNG 和 UI 快照
保存在 `Saved/Validation/BasicAssetEditors`。两个后端连续运行时需分别保存证据副本。
语义与 UI 测试不覆盖新音视频编解码器、运行时 Texture/Audio、Cook 或项目模块启动。

树节点样式与基础类型输入统一复用 [通用控件](CommonEditorControls.md)，Enter/失焦校验后提交，Esc 放弃当前草稿。
