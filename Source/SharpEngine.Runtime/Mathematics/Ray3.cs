using System.Numerics;

namespace SharpEngine.Mathematics;

/// <summary>A half ray with a unit direction; distances are world units. The default struct is invalid.</summary>
public readonly struct Ray3
{
    public Vector3 Origin
    {
        get;
    }
    public Vector3 Direction
    {
        get;
    }

    public Ray3(Vector3 Origin, Vector3 Direction)
    {
        if (!VectorMath.IsFinite(Origin))
        {
            throw new ArgumentException("Ray origin must be finite.", nameof(Origin));
        }
        this.Origin = Origin;
        this.Direction = VectorMath.NormalizeOrZero(Direction);
        if (this.Direction == Vector3.Zero)
        {
            throw new ArgumentException("Ray direction must be nonzero.", nameof(Direction));
        }
    }

    public Vector3 GetPoint(float Distance)
    {
        if (!float.IsFinite(Distance) || Distance < 0 || Direction == Vector3.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(Distance));
        }
        return Origin + Direction * Distance;
    }
}
