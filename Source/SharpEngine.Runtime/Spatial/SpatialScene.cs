using System.Numerics;
using System.Text.Json.Nodes;
using SharpEngine.Components;
using SharpEngine.Mathematics;

namespace SharpEngine.Spatial;

// A derived view of one committed scene (or one engine-controlled phase boundary).
internal sealed class SpatialScene
{
    internal readonly SceneData Data;
    internal readonly ComponentRegistry Registry;
    internal readonly Dictionary<string, Matrix4x4> Matrices = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, EntityRecord> Entities;
    internal readonly HashSet<string> Active = new(StringComparer.Ordinal);

    internal SpatialScene(SceneData Data, ComponentRegistry Registry)
    {
        this.Data = Data;
        this.Registry = Registry;
        Entities = Data.Entities.ToDictionary(Entity => Entity.EntityId, StringComparer.Ordinal);
        foreach (var Entity in Data.Entities)
        {
            Build(Entity);
        }
    }

    private void Build(EntityRecord Entity)
    {
        if (Matrices.ContainsKey(Entity.EntityId))
        {
            return;
        }
        var Parent = Entity.ParentId is null ? null : Entities[Entity.ParentId];
        if (Parent is not null)
        {
            Build(Parent);
        }
        var State = Entity.Components.Single(Component => Component.TypeId == "engine.transform").State;
        var Position = Vector(State, "Position");
        var Rotation = Vector(State, "Rotation");
        var Scale = Vector(State, "Scale");
        var Local = TransformMath.Trs(Position, Rotation, Scale);
        var Matrix = Parent is null ? Local : Local * Matrices[Parent.EntityId];
        if (!IsFinite(Matrix))
        {
            throw new ComponentValidationException("SPATIAL_TRANSFORM", "transform", "World transform must remain finite.");
        }
        Matrices.Add(Entity.EntityId, Matrix);
        if (Entity.ActiveSelf && (Parent is null || Active.Contains(Parent.EntityId)))
        {
            Active.Add(Entity.EntityId);
        }
    }

    internal static Vector3 Vector(JsonObject State, string Name)
    {
        var Values = State[Name]!.AsArray();
        return new(Values[0]!.GetValue<float>(), Values[1]!.GetValue<float>(), Values[2]!.GetValue<float>());
    }

    internal static JsonArray Json(Vector3 Value) { return new(Value.X, Value.Y, Value.Z); }

    internal static JsonArray Json(Matrix4x4 Value)
    {
        return new(Value.M11, Value.M12, Value.M13, Value.M14, Value.M21, Value.M22, Value.M23, Value.M24,
            Value.M31, Value.M32, Value.M33, Value.M34, Value.M41, Value.M42, Value.M43, Value.M44);
    }

    internal static void RequireFinite(Vector3 Value)
    {
        if (!float.IsFinite(Value.X) || !float.IsFinite(Value.Y) || !float.IsFinite(Value.Z))
        {
            throw new ArgumentOutOfRangeException(nameof(Value), "Vector must be finite.");
        }
    }

    internal static bool IsFinite(Matrix4x4 Value)
    {
        return float.IsFinite(Value.M11) && float.IsFinite(Value.M12) && float.IsFinite(Value.M13) && float.IsFinite(Value.M14) &&
            float.IsFinite(Value.M21) && float.IsFinite(Value.M22) && float.IsFinite(Value.M23) && float.IsFinite(Value.M24) &&
            float.IsFinite(Value.M31) && float.IsFinite(Value.M32) && float.IsFinite(Value.M33) && float.IsFinite(Value.M34) &&
            float.IsFinite(Value.M41) && float.IsFinite(Value.M42) && float.IsFinite(Value.M43) && float.IsFinite(Value.M44);
    }
}
