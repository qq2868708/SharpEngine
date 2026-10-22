# 基础数学库

`SharpEngine.Mathematics` 位于 Runtime 模块，直接使用 `System.Numerics` 的
`Vector3`、`Quaternion` 和 `Matrix4x4`，保持现有组件序列化格式。

| API | 用途 |
| --- | --- |
| `Mathf` | 常量、限幅插值、反插值、周期与最短角差、MoveTowards、SmoothStep、近似比较、指数阻尼 |
| `VectorMath` | 有限值检查、双精度长度计算、稳定归一化、投影/平面投影、夹角、无越界移动 |
| `QuaternionMath` | 稳定归一化、Euler 转换、旋转夹角和限制角速度的 RotateTowards |
| `TransformMath` | TRS、有限/仿射检查、可逆性、拒绝 shear 的分解、逆转置法线变换 |
| `Ray3` | 单位方向的半射线，距离使用世界单位 |
| `Bounds3` | 闭区间 AABB、包含/相交、最近点、扩张、仿射变换和射线相交 |

角度统一用度：X pitch、Y yaw、Z roll，与 `Quaternion.CreateFromYawPitchRoll`
一致。Euler 反解只返回一个等价表示，pitch 极点附近把 roll 归零；它不是唯一表示，
不能用来保留编辑器多圈旋转意图。矩阵采用 System.Numerics 行向量约定：
`Local = Scale * Rotation * Translation`，`World = Local * ParentWorld`。
现有空间场景、导航世界矩阵与骨骼局部矩阵已使用共同 TRS 入口。

`VectorMath.NormalizeOrZero` 对零向量返回零，对非有限向量报错；归一化、长度与移动使用
double 中间值处理很大或很小的有限 float。四元数归一化拒绝零四元数。
`TransformMath.TryDecompose` 拒绝透视、奇异矩阵和不能表示为 TRS 的 shear；
允许镜像缩放。分解比较按每条基向量进行，较大的平移不会掩盖 shear。
法线使用逆转置矩阵后归一化；普通点和向量变换继续使用 `Vector3.Transform` /
`Vector3.TransformNormal`。

`Bounds3` 接受平面或点边界，边界接触算相交；射线从盒内起步的距离为零，平行于 slab
的轴明确处理，默认 `MaxDistance=float.MaxValue`。构造器拒绝非有限/反向边界和零射线方向。
`default(Ray3)` 没有有效方向，查询时拒绝；`default(Bounds3)` 是原点处的点边界。
计算结果仍受 float 表示范围限制，数学值超出 float 范围时不保证有限输出。

```csharp
var Local = TransformMath.Trs(Position, EulerDegrees, Scale);
var World = Local * ParentWorld;
var Ray = new Ray3(Origin, Direction);
var Hit = Bounds.IntersectRay(Ray, out var Distance, 100);
```

验证入口：

```powershell
dotnet run --project Tools/MathematicsValidation/SharpEngine.MathematicsValidation.csproj
```

覆盖标量和几何边界、极大/极小向量、pitch 极点、镜像 TRS、shear/奇异矩阵，
以及固定随机种子的 500 组 Euler/TRS/逆矩阵/法线性质检查。
