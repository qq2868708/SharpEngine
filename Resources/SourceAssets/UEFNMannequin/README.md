# UEFN Mannequin Blender 源文件

主文件：[UEFNMannequin.blend](UEFNMannequin.blend)。使用 Blender 5.2.2 LTS 制作。
在底部 Action Editor 中选择 `Idle`、`Walk`、`Run`、`Jump`，将时间范围设为对应 Action 的范围后播放。
文件内的 `START_HERE` 文本也记录了这些说明。

| Action | 帧范围 | 时长（30 fps） | 根运动 |
| --- | --- | --- | --- |
| Idle | 1–61 | 2.000 秒 | 无位移，呼吸待机循环 |
| Walk | 1–33 | 1.067 秒 | 一周期向前 0.90 米，循环 |
| Run | 1–21 | 0.667 秒 | 一周期向前 1.80 米，循环 |
| Jump | 1–43 | 1.400 秒 | 原地起跳、腾空、落地，最高约 0.443 米 |

根运动集中在显式 `root` 骨骼；走、跑沿 Blender 的 **-Y** 方向移动，跳跃沿 **+Z** 方向移动。
骨架与网格对象变换已归一化为米制，缩放为 1。`pelvis` 保存躯干起伏，脚踝和四肢使用源骨长求解。
所有骨骼均烘焙为可编辑的逐帧 location / quaternion / scale 曲线；没有运行时脚本、驱动或外部动画依赖。
Idle、Walk、Run 的首尾姿态在去除根位移后衔接。带根运动的循环需要使用方累计每周期的根位移。

## 原始来源与绑定姿态

- `Original/skm_uefn_mannequin.FBX`：Epic Games 的 UEFN Mannequin 原文件副本，未修改。
- SHA-256：`0E5769FDF6E0B703785E6C849669EF5D0F04614440BDAB492C79D989C9D45E95`。
- 原始缓存目录：`C:/ProgramData/Epic/EpicGamesLauncher/VaultCache/FabLibrary/UEFN_Mannequin-18699cc7/fbx`。
- 原包只提供 FBX，没有 `.blend`，也没有动画；本目录的 `.blend` 和四段 Action 是本次创建的源资产。
- `UEFNMannequin_BindPose.blend`：先行保存的无动画绑定姿态基线。
- 原 FBX 的 87 根骨骼保留名称、全局绑定位置和旋转，另将隐含对象根节点转换为显式 `root` 骨骼，共 88 根。
- 保留全部 6009 个顶点、9324 个三角形和原始蒙皮权重，最多 9 个影响；没有截为四权重。
- `source.json`：来源、单位、归一化误差记录。
- `animations.json`：四段 Action 的范围、根运动、脚部接触采样记录。
- `validation.json`：重新打开主文件后逐帧求值的检查结果，并记录被检查 `.blend` 的哈希。

## 预览和检查

`Previews/Actions.png` 是四段动画的关键姿态对照；同目录的四个 GIF 可播放。
预览使用重新打开 `.blend` 后 Blender dependency graph 实际求值的蒙皮网格，相机跟随水平根位移，地面网格固定。

逐帧检查覆盖有限顶点、骨骼缩放、支撑脚踝漂移、地面穿透和循环端点。
当前结果无检查错误；最小顶点高度约 -0.000077 米，属于原 FBX 脚底约 0.077 毫米的微小越界。
预览和数值检查可帮助审阅动画，不替代项目最终的动作风格验收。

## 复现脚本

依次使用 Blender 的 `--background --factory-startup --python` 运行：

1. `Tools/Assets/BuildUEFNMannequinSource.py`：复制并校验原 FBX，保存绑定姿态基线。
2. `Tools/Assets/AuthorUEFNMannequinAnimations.py`：从基线生成四段 Action，保存主文件。
3. `Tools/Assets/ValidateUEFNMannequinSource.py`：重新打开主文件并逐帧检查，输出实际求值几何。

再使用带 numpy 和 Pillow 的 Python 运行 `Tools/Assets/PreviewUEFNMannequinSource.py` 生成预览。
生成脚本会拒绝覆盖已有 `.blend`；复现前须明确安排原文件保留与输出目录。

`Tools/Assets/ImportUEFNMannequinSource.py` 从保存后的主文件生成待验收资产，统一使用 Y-up、右手系、-Z 前方。
导入阶段以 240 Hz 重采样，常量轨道压缩为一个关键帧；另以 480 Hz 独立求值生成 Blender 对照数据。
`Tools/SkeletalReimportValidation` 使用引擎真实资产解析、动画采样和蒙皮链路检查这些对照数据，
并检查完整 9 个源影响在 12-lane GPU payload 与缓存回读中保留。
报告位于 `Saved/Validation/SkeletalReimport-20261020/engine-comparison.json`。
通过后才替换 `Content` 中相同稳定 ID 的骨骼、网格和四段动画。
根位移属于动画数据；角色实体的 Root Motion 提取与开关仍留待后续实现。
