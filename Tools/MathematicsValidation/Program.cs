using System.Numerics;
using SharpEngine.Mathematics;

var Count = 0;
void Check(bool Condition, string Message)
{
    if (!Condition)
    {
        throw new Exception(Message);
    }
    Count++;
}
void Reject(Action Action, string Message)
{
    try
    {
        Action();
    }
    catch (ArgumentException)
    {
        Count++;
        return;
    }
    throw new Exception("Expected rejection: " + Message);
}
bool Near(float First, float Second, float Tolerance = 0.0001f) { return Math.Abs((double)First - Second) <= Tolerance; }
bool VectorNear(Vector3 First, Vector3 Second, float Tolerance = 0.0001f) { return VectorMath.Length(First - Second) <= Tolerance; }

Check(Mathf.Lerp(0, 10, 2) == 10 && Mathf.LerpUnclamped(0, 10, 2) == 20, "clamped and unclamped interpolation");
Check(Mathf.Lerp(-float.MaxValue, float.MaxValue, 0.5f) == 0, "interpolation avoids intermediate float overflow");
Check(Mathf.InverseLerp(-float.MaxValue, float.MaxValue, 0) == 0.5f && Mathf.InverseLerp(1, 1, 2) == 0, "inverse interpolation extremes and degenerate range");
Check(Mathf.Repeat(-1, 3) == 2 && Mathf.Repeat(6, 3) == 0, "negative and exact cycle wrapping");
Check(Mathf.DeltaAngle(350, 10) == 20 && Mathf.DeltaAngle(10, 350) == -20 && Mathf.LerpAngle(350, 10, 0.5f) == 360, "shortest angle path");
Check(Mathf.MoveTowards(1, 2, 5) == 2 && Mathf.MoveTowards(1, -1, 0.5f) == 0.5f, "scalar movement without overshoot");
Check(Mathf.SmoothStep(0, 1, -1) == 0 && Mathf.SmoothStep(0, 1, 2) == 1 && Mathf.SmoothStep(0, 1, 0.5f) == 0.5f, "smoothstep endpoints");
Check(!Mathf.Approximately(float.NaN, float.NaN) && !Mathf.Approximately(float.PositiveInfinity, float.PositiveInfinity) && Mathf.Approximately(100000, 100000.05f), "finite approximate comparison and relative tolerance");
Check(Near(Mathf.Damp(0, 10, 2, 1), Mathf.Damp(Mathf.Damp(0, 10, 2, 0.5f), 10, 2, 0.5f)), "damping frame subdivision invariance");
Reject(() => Mathf.Repeat(1, 0), "zero repeat length");
Reject(() => Mathf.MoveTowards(1, 2, -1), "negative movement budget");
Reject(() => Mathf.Damp(1, 2, 1, float.NaN), "nonfinite delta");
Check(VectorMath.NormalizeOrZero(Vector3.Zero) == Vector3.Zero, "zero normalization");
Check(VectorNear(VectorMath.NormalizeOrZero(new(float.MaxValue, float.MaxValue, 0)), new(MathF.Sqrt(0.5f), MathF.Sqrt(0.5f), 0)), "large normalization remains finite");
Check(VectorMath.NormalizeOrZero(new(float.Epsilon, 0, 0)) == Vector3.UnitX, "subnormal normalization");
Check(VectorNear(VectorMath.Project(new(1, 2, 3), new(0, 5, 0)), new(0, 2, 0)) && VectorMath.Project(Vector3.One, Vector3.Zero) == Vector3.Zero, "nonunit and zero projection normals");
Check(VectorNear(VectorMath.ProjectOnPlane(new(1, 2, 3), Vector3.UnitY), new(1, 0, 3)) && Near(VectorMath.Angle(Vector3.UnitX, Vector3.UnitY), 90), "plane projection and vector angle");
Check(VectorMath.MoveTowards(Vector3.Zero, Vector3.One, 100) == Vector3.One, "vector move has no overshoot");
Check(VectorMath.IsFinite(VectorMath.MoveTowards(new(-float.MaxValue, 0, 0), new(float.MaxValue, 0, 0), float.MaxValue)), "vector movement avoids difference overflow");
Check(Near(VectorMath.Angle(new(2, 3, 5), new(2, 3, 5)), 0), "identical vector angle is zero");
Reject(() => VectorMath.NormalizeOrZero(new(float.NaN, 0, 0)), "nonfinite vector");
Reject(() => QuaternionMath.Normalize(default), "zero quaternion");
Check(VectorNear(Vector3.Transform(Vector3.UnitZ, QuaternionMath.Euler(new(0, 90, 0))), Vector3.UnitX), "engine yaw orientation");
var SameRotation = QuaternionMath.Euler(new(20, 40, 60));
Check(Near(QuaternionMath.Angle(SameRotation, SameRotation), 0) && Near(QuaternionMath.Angle(SameRotation, -SameRotation), 0), "identical and antipodal quaternion angle is zero");
Check(Near(QuaternionMath.Angle(Quaternion.Identity, QuaternionMath.RotateTowards(Quaternion.Identity, QuaternionMath.Euler(new(0, 90, 0)), 30)), 30, 0.001f), "quaternion rotation budget");

var Random = new Random(74219);
float Next(float Min, float Max) { return Min + Random.NextSingle() * (Max - Min); }
for (var Index = 0; Index < 500; Index++)
{
    var Euler = new Vector3(Next(-89, 89), Next(-180, 180), Next(-180, 180));
    var Rotation = QuaternionMath.Euler(Euler);
    var RoundTrip = QuaternionMath.Euler(QuaternionMath.ToEuler(Rotation));
    Check(Math.Abs(Quaternion.Dot(Rotation, RoundTrip)) > 0.99999f, "seeded Euler rotation round trip");
    var Position = new Vector3(Next(-100, 100), Next(-100, 100), Next(-100, 100));
    var Scale = new Vector3(Next(0.2f, 4), Next(0.2f, 4), Next(0.2f, 4));
    if ((Index & 1) == 0)
    {
        Scale.X = -Scale.X;
    }
    var Matrix = TransformMath.Trs(Position, Rotation, Scale);
    var Decomposed = TransformMath.TryDecompose(Matrix, out var ActualPosition, out var ActualRotation, out var ActualScale);
    Check(Decomposed && VectorNear(Position, ActualPosition), "seeded TRS decomposition including reflection");
    var Rebuilt = TransformMath.Trs(ActualPosition, ActualRotation, ActualScale);
    var Point = new Vector3(2, 3, -1);
    Check(VectorNear(Vector3.Transform(Point, Matrix), Vector3.Transform(Point, Rebuilt)), "decomposition reconstructs point mapping");
    var Invertible = TransformMath.TryInverse(Matrix, out var Inverse);
    Check(Invertible && VectorNear(Vector3.Transform(Vector3.Transform(Point, Matrix), Inverse), Point, 0.0002f), "seeded TRS inverse");
    var Normal = TransformMath.TransformNormal(Vector3.UnitY, Matrix);
    Check(Near(Vector3.Dot(Normal, Vector3.TransformNormal(Vector3.UnitX, Matrix)), 0), "inverse transpose preserves normal orthogonality");
}
foreach (var Pitch in new[]
{
    -90f,
    -89.99f,
    89.99f,
    90f,
})
{
    var Rotation = QuaternionMath.Euler(new(Pitch, 70, -25));
    var RoundTrip = QuaternionMath.Euler(QuaternionMath.ToEuler(Rotation));
    Check(Math.Abs(Quaternion.Dot(Rotation, RoundTrip)) > 0.99999f, "pitch pole rotation equivalence");
}
var Shear = Matrix4x4.Identity;
Shear.M12 = 0.5f;
Shear.M41 = 1e12f;
Check(!TransformMath.TryDecompose(Shear, out _, out _, out _), "large translation must not hide shear");
var TinyShear = Matrix4x4.CreateScale(1e-8f);
TinyShear.M12 = 5e-9f;
Check(!TransformMath.TryDecompose(TinyShear, out _, out _, out _), "small basis must not hide relative shear");
Check(!TransformMath.TryInverse(Matrix4x4.CreateScale(0), out _) && !TransformMath.TryDecompose(Matrix4x4.CreateScale(0), out _, out _, out _), "singular matrix rejected");
Check(!TransformMath.TryDecompose(Matrix4x4.CreatePerspectiveFieldOfView(1, 1, 0.1f, 10), out _, out _, out _), "perspective is not TRS");
var Parent = TransformMath.Trs(new(10, 0, 0), new Vector3(0, 90, 0), new(2));
var Child = TransformMath.Trs(new(0, 0, 1), Quaternion.Identity, Vector3.One);
Check(VectorNear(Vector3.Transform(Vector3.Zero, Child * Parent), new(12, 0, 0)), "child local times parent world convention");

var Bounds = new Bounds3(new(-1), new(1));
Check(Bounds.Contains(Vector3.One) && Bounds.Intersects(new(Vector3.One, new(2))), "bounds closed boundary contact");
Check(Bounds.ClosestPoint(new(5, 0, 0)) == Vector3.UnitX, "bounds closest point");
var Hit = Bounds.IntersectRay(new(new(-3, 0, 0), new(10, 0, 0)), out var Distance);
Check(Hit && Distance == 2, "normalized ray distance uses world units");
Check(!Bounds.IntersectRay(new(new(-3, 2, 0), Vector3.UnitX), out _), "parallel slab miss");
Check(!Bounds.IntersectRay(new(new(-3, 0, 0), -Vector3.UnitX), out _), "half ray excludes hits behind origin");
Check(!Bounds.IntersectRay(new(new(-3, 0, 0), Vector3.UnitX), out _, 1.9f), "ray maximum distance");
Check(Bounds.IntersectRay(new(Vector3.Zero, Vector3.UnitY), out Distance) && Distance == 0, "inside ray starts at zero");
Check(Bounds.IntersectRay(new(new(-3, 1, 0), Vector3.UnitX), out Distance) && Distance == 2, "tangent boundary hit");
Reject(() => new Ray3(Vector3.Zero, Vector3.Zero), "zero ray direction");
Reject(() => Bounds.IntersectRay(default, out _), "invalid default ray");
Reject(() => new Bounds3(Vector3.One, Vector3.Zero), "reversed bounds");
var Transformed = Bounds.Transform(Parent);
Check(VectorNear(Transformed.Min, new(8, -2, -2)) && VectorNear(Transformed.Max, new(12, 2, 2)), "AABB transformed by all corners");
Console.WriteLine($"[MathematicsValidation] PASS: {Count} assertions; seed=74219; 500 TRS/Euler cases");
