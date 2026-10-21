using System.Numerics;
using SharpEngine.Mathematics;

namespace SharpEngine.Components;

internal static class EntityTransformMath
{
    internal static Matrix4x4 WorldMatrix(SharpEntity Entity)
    {
        var Result = Matrix4x4.Identity;
        for (var Current = Entity; Current is not null; Current = Current.Parent)
        {
            var Transform = Current.GetComponent<TransformComponent>();
            if (Transform is not null)
            {
                Result *= TransformMath.Trs(Transform.Position, Transform.Rotation, Transform.Scale);
            }
        }
        return Result;
    }

    internal static void SetWorldMatrix(SharpEntity Entity, Matrix4x4 Matrix)
    {
        var Local = Matrix;
        if (Entity.Parent is not null)
        {
            var ParentInvertible = TransformMath.TryInverse(WorldMatrix(Entity.Parent), out var ParentInverse);
            if (!ParentInvertible)
            {
                throw new InvalidDataException("Entity parent transform must be invertible");
            }
            Local *= ParentInverse;
        }
        var Representable = TransformMath.TryDecompose(Local, out var Position, out var Rotation, out var Scale);
        if (!Representable)
        {
            throw new InvalidDataException("Entity world transform cannot be represented without shear");
        }
        var Transform = Entity.GetComponent<TransformComponent>() ??
            throw new InvalidDataException("Entity requires a Transform");
        Transform.Position = Position;
        Transform.Rotation = QuaternionMath.ToEuler(Rotation);
        Transform.Scale = Scale;
    }
}
