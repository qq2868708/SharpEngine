# 网格与 Socket 资产编辑器

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
