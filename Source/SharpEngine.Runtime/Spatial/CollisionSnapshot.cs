using System.Numerics;
using System.Text.Json.Nodes;
using SharpEngine.Components;

namespace SharpEngine.Spatial;

public sealed record CollisionHit(string EntityId, string ComponentId, float Distance,
    Vector3 Point, Vector3 Normal, int Layer, bool IsTrigger);

public sealed record ColliderDescription(string EntityId, string ComponentId, string TypeId,
    int Layer, bool IsTrigger, bool BlocksNavigation);

/// <summary>Immutable, derived queries over one committed runtime revision.</summary>
public sealed class CollisionSnapshot
{
    private readonly ShapeGeometry[] Shapes;
    public ulong Revision
    {
        get;
    }
    public IReadOnlyList<ColliderDescription> Colliders
    {
        get
        {
            return Array.AsReadOnly(Shapes.Select(Shape => Shape.Description).ToArray());
        }
    }

    internal CollisionSnapshot(SpatialScene Scene, ulong Revision)
    {
        this.Revision = Revision;
        foreach (var Component in Scene.Data.Entities.Where(Entity => Scene.Active.Contains(Entity.EntityId))
            .SelectMany(Entity => Entity.Components).Where(Component => Component.Enabled &&
                Component.TypeId is "engine.box-collider" or "engine.sphere-collider" or "engine.capsule-collider"))
        {
            Scene.Registry.ValidateState(Scene.Registry.Find(Component.TypeId)!, Component.State);
        }
        Shapes = Scene.Data.Entities.Where(Entity => Scene.Active.Contains(Entity.EntityId))
            .SelectMany(Entity => Entity.Components.Where(Component => Component.Enabled &&
                Component.TypeId is "engine.box-collider" or "engine.sphere-collider" or "engine.capsule-collider")
                .Select(Component => new ShapeGeometry(Entity.EntityId, Component, Scene.Matrices[Entity.EntityId])))
            .OrderBy(Shape => Shape.Description.ComponentId, StringComparer.Ordinal).ToArray();
    }

    public IReadOnlyList<CollisionHit> RaycastAll(Vector3 Origin, Vector3 Direction, float MaxDistance,
        uint LayerMask = uint.MaxValue, bool IncludeTriggers = false, string? IgnoreEntityId = null)
    {
        SpatialScene.RequireFinite(Origin);
        SpatialScene.RequireFinite(Direction);
        if (Direction.LengthSquared() <= 0 || !float.IsFinite(Direction.LengthSquared()) ||
            !float.IsFinite(MaxDistance) || MaxDistance < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Direction), "Ray requires a nonzero direction and finite nonnegative distance.");
        }
        Direction = Vector3.Normalize(Direction);
        var Hits = new List<CollisionHit>();
        foreach (var Shape in Filter(LayerMask, IncludeTriggers, IgnoreEntityId))
        {
            var Hit = Shape.Raycast(Origin, Direction, MaxDistance);
            if (Hit is not null)
            {
                Hits.Add(Hit);
            }
        }
        return Hits.OrderBy(Hit => Hit.Distance).ThenBy(Hit => Hit.ComponentId, StringComparer.Ordinal).ToArray();
    }

    public CollisionHit? Raycast(Vector3 Origin, Vector3 Direction, float MaxDistance,
        uint LayerMask = uint.MaxValue, bool IncludeTriggers = false, string? IgnoreEntityId = null)
    {
        return RaycastAll(Origin, Direction, MaxDistance, LayerMask, IncludeTriggers, IgnoreEntityId).FirstOrDefault();
    }

    public IReadOnlyList<ColliderDescription> OverlapSphere(Vector3 Center, float Radius,
        uint LayerMask = uint.MaxValue, bool IncludeTriggers = false, string? IgnoreEntityId = null)
    {
        SpatialScene.RequireFinite(Center);
        if (!float.IsFinite(Radius) || Radius < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Radius));
        }
        Vector3 Support(Vector3 Direction) { return Center + Vector3.Normalize(Direction) * Radius; }
        return Filter(LayerMask, IncludeTriggers, IgnoreEntityId).Where(Shape => Shape.Intersects(Support))
            .Select(Shape => Shape.Description).ToArray();
    }

    public IReadOnlyList<ColliderDescription> OverlapShape(string ComponentId,
        uint LayerMask = uint.MaxValue, bool IncludeTriggers = false)
    {
        var Source = Shapes.SingleOrDefault(Shape => Shape.Description.ComponentId == ComponentId)
            ?? throw new ArgumentException("Collider is absent or inactive in this snapshot.", nameof(ComponentId));
        return Filter(LayerMask, IncludeTriggers, null).Where(Shape => Shape != Source && Shape.Intersects(Source.Support))
            .Select(Shape => Shape.Description).ToArray();
    }

    private IEnumerable<ShapeGeometry> Filter(uint LayerMask, bool IncludeTriggers, string? IgnoreEntityId)
    {
        return Shapes.Where(Shape => (LayerMask & (1u << Shape.Description.Layer)) != 0 &&
            (IncludeTriggers || !Shape.Description.IsTrigger) && Shape.Description.EntityId != IgnoreEntityId);
    }

    internal ShapeGeometry[] Obstacles(uint LayerMask, string? IgnoreEntityId)
    {
        return Filter(LayerMask, false, IgnoreEntityId).Where(Shape => Shape.Description.BlocksNavigation).ToArray();
    }
}

internal sealed class ShapeGeometry
{
    internal readonly ColliderDescription Description;
    internal readonly string Fingerprint;
    private readonly Matrix4x4 Matrix;
    private readonly Matrix4x4 Inverse;
    private readonly Vector3 Center;
    private readonly Vector3 HalfSize;
    private readonly float Radius;
    private readonly float HalfSegment;
    private readonly Vector3 Minimum;
    private readonly Vector3 Maximum;

    internal ShapeGeometry(string EntityId, ComponentRecord Component, Matrix4x4 Matrix)
    {
        var State = Component.State;
        Description = new(EntityId, Component.ComponentId, Component.TypeId, State["Layer"]!.GetValue<int>(),
            State["IsTrigger"]!.GetValue<bool>(), State["BlocksNavigation"]!.GetValue<bool>());
        this.Matrix = Matrix;
        var Invertible = Matrix4x4.Invert(Matrix, out Inverse);
        if (!Invertible || !SpatialScene.IsFinite(Inverse))
        {
            throw new InvalidOperationException($"COLLIDER_TRANSFORM: {Component.ComponentId} has a singular world transform.");
        }
        Center = SpatialScene.Vector(State, "Center");
        HalfSize = Component.TypeId == "engine.box-collider" ? SpatialScene.Vector(State, "Size") / 2 : Vector3.Zero;
        Radius = Component.TypeId == "engine.box-collider" ? 0 : State["Radius"]!.GetValue<float>();
        HalfSegment = Component.TypeId == "engine.capsule-collider" ? State["Height"]!.GetValue<float>() / 2 - Radius : 0;
        Minimum = new(Support(-Vector3.UnitX).X, Support(-Vector3.UnitY).Y, Support(-Vector3.UnitZ).Z);
        Maximum = new(Support(Vector3.UnitX).X, Support(Vector3.UnitY).Y, Support(Vector3.UnitZ).Z);
        SpatialScene.RequireFinite(Minimum);
        SpatialScene.RequireFinite(Maximum);
        Fingerprint = Component.ToJson().ToJsonString() + SpatialScene.Json(Matrix).ToJsonString();
    }

    internal Vector3 Support(Vector3 Direction)
    {
        var Local = Vector3.TransformNormal(Direction, Matrix4x4.Transpose(Matrix));
        Vector3 Point;
        if (Description.TypeId == "engine.box-collider")
        {
            Point = new(MathF.CopySign(HalfSize.X, Local.X), MathF.CopySign(HalfSize.Y, Local.Y), MathF.CopySign(HalfSize.Z, Local.Z));
        }
        else
        {
            Point = Vector3.Normalize(Local) * Radius;
            Point.Y += MathF.CopySign(HalfSegment, Local.Y);
        }
        return Vector3.Transform(Center + Point, Matrix);
    }

    internal bool Intersects(Func<Vector3, Vector3> Other)
    {
        var Tolerance = ConvexDistance.ContactTolerance;
        if (Other(Vector3.UnitX).X < Minimum.X - Tolerance || Other(-Vector3.UnitX).X > Maximum.X + Tolerance ||
            Other(Vector3.UnitY).Y < Minimum.Y - Tolerance || Other(-Vector3.UnitY).Y > Maximum.Y + Tolerance ||
            Other(Vector3.UnitZ).Z < Minimum.Z - Tolerance || Other(-Vector3.UnitZ).Z > Maximum.Z + Tolerance)
        {
            return false;
        }
        return ConvexDistance.Intersects(Support, Other);
    }

    internal CollisionHit? Raycast(Vector3 Origin, Vector3 Direction, float MaxDistance)
    {
        var LocalOrigin = Vector3.Transform(Origin, Inverse) - Center;
        var LocalDirection = Vector3.TransformNormal(Direction, Inverse);
        var Distance = float.PositiveInfinity;
        var Normal = Vector3.Zero;
        if (Description.TypeId == "engine.box-collider")
        {
            var Near = 0f;
            var Far = MaxDistance;
            var Axes = new[]
            {
                Vector3.UnitX,
                Vector3.UnitY,
                Vector3.UnitZ,
            };
            foreach (var Axis in Axes)
            {
                var Start = Vector3.Dot(LocalOrigin, Axis);
                var Delta = Vector3.Dot(LocalDirection, Axis);
                var Extent = Vector3.Dot(HalfSize, Axis);
                if (Delta == 0)
                {
                    if (MathF.Abs(Start) > Extent)
                    {
                        return null;
                    }
                    continue;
                }
                var First = (-Extent - Start) / Delta;
                var Last = (Extent - Start) / Delta;
                var EntryNormal = -Axis * MathF.Sign(Delta);
                if (First > Last)
                {
                    (First, Last) = (Last, First);
                }
                if (First > Near)
                {
                    Near = First;
                    Normal = EntryNormal;
                }
                Far = MathF.Min(Far, Last);
                if (Near > Far)
                {
                    return null;
                }
            }
            Distance = Near;
        }
        else
        {
            var Nearest = new Vector3(0, Math.Clamp(LocalOrigin.Y, -HalfSegment, HalfSegment), 0);
            if (Vector3.DistanceSquared(LocalOrigin, Nearest) <= Radius * Radius)
            {
                Distance = 0;
            }
            else
            {
                foreach (var CapY in new[]
                {
                    -HalfSegment,
                    HalfSegment,
                })
                {
                    var Offset = LocalOrigin - Vector3.UnitY * CapY;
                    var Root = EntryRoot(Vector3.Dot(LocalDirection, LocalDirection), Vector3.Dot(Offset, LocalDirection), Offset.LengthSquared() - Radius * Radius);
                    if (Root >= 0 && Root < Distance)
                    {
                        Distance = Root;
                        Normal = LocalOrigin + LocalDirection * Root - Vector3.UnitY * CapY;
                    }
                }
                if (HalfSegment > 0)
                {
                    var Root = EntryRoot(LocalDirection.X * LocalDirection.X + LocalDirection.Z * LocalDirection.Z,
                        LocalOrigin.X * LocalDirection.X + LocalOrigin.Z * LocalDirection.Z,
                        LocalOrigin.X * LocalOrigin.X + LocalOrigin.Z * LocalOrigin.Z - Radius * Radius);
                    var Y = LocalOrigin.Y + LocalDirection.Y * Root;
                    if (Root >= 0 && Root < Distance && MathF.Abs(Y) <= HalfSegment)
                    {
                        Distance = Root;
                        Normal = LocalOrigin + LocalDirection * Root;
                        Normal.Y = 0;
                    }
                }
            }
        }
        if (!float.IsFinite(Distance) || Distance > MaxDistance)
        {
            return null;
        }
        var WorldNormal = Normal == Vector3.Zero ? -Direction :
            Vector3.Normalize(Vector3.TransformNormal(Normal, Matrix4x4.Transpose(Inverse)));
        return new(Description.EntityId, Description.ComponentId, Distance, Origin + Direction * Distance,
            WorldNormal, Description.Layer, Description.IsTrigger);
    }

    private static float EntryRoot(double A, double B, double C)
    {
        var Discriminant = B * B - A * C;
        if (A <= 0 || Discriminant < 0)
        {
            return float.PositiveInfinity;
        }
        return (float)((-B - Math.Sqrt(Discriminant)) / A);
    }
}
