# 骨骼编辑器：Socket 与虚拟骨骼

在 Content Manager 双击 Skeleton 打开独立骨骼编辑器。项目中的可写 Skeleton
支持以下操作；引擎只读资产只查看。导入骨骼的层级、绑定姿态及网格蒙皮索引保持只读。

1. 在 Skeleton Tree 选择父骨，点击工具栏 **Add Socket**。
2. Details 编辑名称、Parent Bone、Position、Rotation (degrees) 和 Scale。
   Transform 组由 Position(Vector)、Rotation(Rotator)、Scale(Vector) 组合，各有 X/Y/Z 三个输入框；旋转为 X pitch / Y yaw / Z roll，保存时使用四元数。
   缩放三个分量必须大于零。
   选择可写 Socket 后，也可以在工具栏切换 **Move / Rotate / Scale**，
   左键拖动预览中的箭头、圆环或方框。坐标轴跟随 Socket 局部方向；移动会通过父骨矩阵
   转换回局部位置，支持虚拟父骨和带旋转、非均匀缩放的父骨。
   拖动过程中显示临时预览，松开后一次提交，整次拖拽只需一次 Undo/Redo。
   **Esc**、失焦、丢失捕获、切换选择或工具会取消未完成的拖拽；点击空白不修改文档。
3. **Add Virtual Bone** 新增派生骨。初始 Source 为首个导入骨，Target 为所选骨；
   选择 Socket 时使用其父骨。Details 可重选 Source / Parent 和 Target。
4. **Duplicate Selected** 创建独立身份的副本。**Remove Selected** 删除当前自定义定义；
   被 Socket 或其他虚拟骨引用的虚拟骨不能删除，先修改引用。
5. Undo/Redo 属于当前文档。编辑不会自动写文件；**Save** 验证完整资产并原子保存。
   外部文件变化时拒绝覆盖，保留未保存内容。树选择、相机导航不进入文档历史。

预览中导入骨为金色连线，虚拟骨为青色源到目标连线，Socket 为紫色父骨连线及 RGB 局部轴。
兼容 Preview Mesh 可叠加显示这些辅助线及 Socket Gizmo。导入骨骼和派生虚拟骨不显示可写
Gizmo；虚拟骨通过 Source/Target 定义。尚不提供预览挂载物、IK 或蒙皮权重编辑。Skeleton 预览显示绑定姿态；
动画编辑器在打开相应预览模型时使用同一求值器，辅助线随采样姿态变化。

界面沿用通用资产窗口、Dock、命令工具栏、字段控件、文档事务和 3D 视口。
RCSS 负责颜色、间距、尺寸等表现；骨骼引用选择、矩阵求值、校验、历史和保存由可复用的
语义逻辑负责。Socket 与主 World 编辑器复用同一个 `TransformGizmo` 绘制、拾取和旋转
拖动算法；基础 Gizmo 只接收模型矩阵、观察相机和视口矩形，不要求创建实体或伪造 World。
每个骨骼窗口独立持有工具和拖拽状态。

## 数据与求值

Skeleton schema **v3** 在已有根字段之外，显式保存：

| 字段 | 每条定义的字段 |
| --- | --- |
| `sockets` | `socketId`, `name`, `parentBoneId`, `position[3]`, `rotation[4]`, `scale[3]` |
| `virtualBones` | `boneId`, `name`, `sourceBoneId`, `targetBoneId` |

`bones` 仍只包含导入骨。v1/v2 读入后将两组定义确定性迁移为空；
v1 的 `previewMeshId` 同时迁移为 null。打开旧资产不修改源文件，首次作者编辑和保存使用 v3。
当前版本要求显式数组；未知字段、未来版本、重复 ID/作者名称、缺失引用、虚拟骨依赖环、
非有限或不可用的 TRS 均拒绝。Socket/虚拟骨重命名只改显示名称，稳定 ID 不变。
两类定义的 ID 与导入骨 ID 共同唯一；名称分别在 Socket 与虚拟骨集合内唯一。

`SkeletonRigPose(SkeletonAsset, SkeletalPose?)` 是独立的、只读的每次求值结果。
省略 Pose 时计算绑定姿态。行向量矩阵约定下：

- 导入骨模型矩阵 = 局部 TRS × 父骨模型矩阵。
- 虚拟骨局部矩阵 = Target 模型矩阵 × Source 模型矩阵的逆；其模型矩阵等于 Target。
  Source 是虚拟父骨；两端可引用其他虚拟骨，依赖顺序由求值器解析。局部结果保留矩阵，
  不强行拆解为可能丢失剪切的 TRS。虚拟骨是派生约束帧，不是可写动画轨道。
- Socket 模型矩阵 = Socket 局部 TRS × 父骨模型矩阵；父骨可为导入骨或虚拟骨。

运行时 `SkeletalMeshComponent.RigPose` 和已提交的 `SkeletonFrameState.RigPose`
公开 `BoneModelMatrices`、`VirtualBoneLocalMatrices`、`SocketModelMatrices`。
字典按稳定 ID 查询；矩阵处于网格模型空间，挂接方自行与网格实体的 World 矩阵组合。
这些值在 render preparation 提交边界刷新，不承诺在更早的组件 Update 阶段已是本帧结果，
也不会自动移动任何挂接实体。

`SkeletonInstance(Mesh, Definition)` 验证 SkeletonId 与绑定一致后同时生成蒙皮和辅助帧。
单参数构造保留原有仅网格求值兼容入口，其 `RigPose` 为 null。引擎运行时和作者预览会加载
规范 Skeleton 定义。`SkeletonFrameState` JSON 升级为 **v2**，追加可空 `rigPose`；
该对象自身 schema v1 保存三组 ID→16 元素矩阵映射。所有结果都是派生快照，不写入 World/Prefab。
保存 Skeleton 会使动画资产缓存失效，下次准备重建使用实例，旧提交帧保持冻结。

编辑器快照 schema **v3** 包含 `document`、`selectedKind`、`selectedId`、`diagnostic`、`rigPose`、
`stateRevision` 和 `gizmo`。已提交的 `document` / `rigPose` 在拖拽松开前保持不变；
`gizmo` schema v1 公开工具、可写性、轴、开始文档 revision、`startPointer` / `startHandle`、局部预览 TRS、临时
`previewRigPose` 和可见 handle 路径。路径为视口局部像素，`bounds` 为所在 host 的矩形。
`editor.skeleton:select:{bone|socket|virtual}:{id}` 选择；`add-socket`、`add-virtual-bone`、
`duplicate`、`remove` 和 `field:{key}:{value}` 为同前缀下的语义操作，
通过通用 `editor.host-action:{rootId}:...` 路由。新增/删除/字段更新共享完整验证事务、
revision、Undo/Redo 和显式 Save；UI 与 MCP 使用同一入口。字段旋转值为三轴角度。
`editor.asset-command:{move|rotate|scale}` 切换工具；
`ui.viewport3d:skeletonPreviewViewport:pointer-{down|move|up}:{x}:{y}` 与
`ui.viewport3d:skeletonPreviewViewport:pointer-cancel` 为共享拖拽入口，坐标为 host 局部像素。
原生 `data-viewport3d-pointer="true"` 显式启用左键捕获，事件延后到 RmlUi 分发完成后提交；
右键环绕、中键平移、滚轮缩放和 Focus 继续使用通用视口导航。
语义入口可用于自动化设置；交互验收应使用 `ui.pointer` / `ui.key` 的真实命中路径。
拖拽不改变 Skeleton schema v3、保存格式或运行时组件契约。

## 验证入口

标准 Editor Development/Both 构建后，运行骨骼专项真实输入检查：

```powershell
dotnet build Tools/McpUiValidation/SharpEngine.McpUiValidation.csproj -c Development
dotnet Tools/McpUiValidation/bin/Development/net8.0/SharpEngine.McpUiValidation.dll Development --skeleton-gizmo-only --render-backend=opengl --render-vsync=off
dotnet Tools/McpUiValidation/bin/Development/net8.0/SharpEngine.McpUiValidation.dll Development --skeleton-gizmo-only --render-backend=vulkan --render-vsync=off
```

专项复用基础资产套件的骨骼操作，覆盖三种工具、临时预览、单次历史、取消、空白/零位移点击、
相机导航、虚拟父骨、预览网格和显式保存，并保存对应原生 PNG 与语义帧。
`Tools/EditorValidation` 另覆盖带旋转/非均匀缩放父矩阵的局部变换，
`Tools/WorldValidation` 回归主 World 的共享 Gizmo 入口。

UEFN 重导入脚本保留已有 Socket/虚拟骨及默认预览模型；新的导入骨若破坏引用，
后续规范资产验证拒绝发布。规范 Skeleton 的 FBX 语义导出仍没有内置 writer。

树节点样式与基础类型输入统一复用 [通用控件](CommonEditorControls.md)，Enter/失焦校验后提交，Esc 放弃当前草稿。
