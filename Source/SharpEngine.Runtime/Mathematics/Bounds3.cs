using System.Numerics;

namespace SharpEngine.Mathematics;

/// <summary>Finite axis-aligned bounds, including point and flat bounds. Boundary contact counts as intersection.</summary>
public readonly struct Bounds3
{
    public Vector3 Min
    {
        get;
    }
    public Vector3 Max
    {
        get;
    }
    public Vector3 Center
    {
        get
        {
            return Min * 0.5f + Max * 0.5f;
        }
    }
    public Vector3 Size
    {
        get
        {
            return Max - Min;
        }
    }

    public Bounds3(Vector3 Min, Vector3 Max)
    {
        if (!VectorMath.IsFinite(Min) || !VectorMath.IsFinite(Max) || Min.X > Max.X || Min.Y > Max.Y || Min.Z > Max.Z)
        {
            throw new ArgumentException("Bounds require finite, ordered minimum and maximum.");
        }
        this.Min = Min;
        this.Max = Max;
    }

    public bool Contains(Vector3 Point) { return Point.X >= Min.X && Point.X <= Max.X && Point.Y >= Min.Y && Point.Y <= Max.Y && Point.Z >= Min.Z && Point.Z <= Max.Z; }
    public bool Intersects(Bounds3 Other) { return Min.X <= Other.Max.X && Max.X >= Other.Min.X && Min.Y <= Other.Max.Y && Max.Y >= Other.Min.Y && Min.Z <= Other.Max.Z && Max.Z >= Other.Min.Z; }
    public Bounds3 Encapsulate(Vector3 Point) { return new(Vector3.Min(Min, Point), Vector3.Max(Max, Point)); }
    public Vector3 ClosestPoint(Vector3 Point) { return Vector3.Clamp(Point, Min, Max); }

    public Bounds3 Transform(Matrix4x4 Matrix)
    {
        if (!TransformMath.IsAffine(Matrix))
        {
            throw new ArgumentException("Bounds require a finite affine transform.", nameof(Matrix));
        }
        var First = Vector3.Transform(Min, Matrix);
        var Result = new Bounds3(First, First);
        for (var Index = 1; Index < 8; Index++)
        {
            var Corner = new Vector3((Index & 1) == 0 ? Min.X : Max.X, (Index & 2) == 0 ? Min.Y : Max.Y, (Index & 4) == 0 ? Min.Z : Max.Z);
            Result = Result.Encapsulate(Vector3.Transform(Corner, Matrix));
        }
        return Result;
    }

    public bool IntersectRay(Ray3 Ray, out float Distance, float MaxDistance = float.MaxValue)
    {
        if (Ray.Direction == Vector3.Zero || !float.IsFinite(MaxDistance) || MaxDistance < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxDistance));
        }
        Distance = 0;
        double Enter = 0;
        double Exit = MaxDistance;
        for (var Axis = 0; Axis < 3; Axis++)
        {
            var Origin = (double)Ray.Origin[Axis];
            var Direction = (double)Ray.Direction[Axis];
            if (Direction == 0)
            {
                if (Origin < Min[Axis] || Origin > Max[Axis])
                {
                    return false;
                }
                continue;
            }
            var First = (Min[Axis] - Origin) / Direction;
            var Second = (Max[Axis] - Origin) / Direction;
            Enter = Math.Max(Enter, Math.Min(First, Second));
            Exit = Math.Min(Exit, Math.Max(First, Second));
            if (Enter > Exit)
            {
                return false;
            }
        }
        Distance = (float)Enter;
        return true;
    }
}
