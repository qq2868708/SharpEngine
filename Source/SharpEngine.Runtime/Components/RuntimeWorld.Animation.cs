using System.Numerics;
using SharpEngine.Mathematics;

namespace SharpEngine.Components;

public sealed partial class RuntimeWorld
{
    /// <summary>All intent velocities exist before sampling; motion commits before any LateUpdate camera.</summary>
    private void FinalizeAnimationAndMovement(float DeltaSeconds, SharpComponent[] Components)
    {
        var Movements = Components.OfType<MovementComponent>().ToArray();
        var Before = Movements.ToDictionary(Movement => Movement.ComponentId,
            Movement => EntityTransformMath.WorldMatrix(Movement.Entity).Translation, StringComparer.Ordinal);
        var Animators = Components.OfType<AnimatorComponent>().ToArray();
        foreach (var Animator in Animators)
        {
            Animator.PrepareAnimationFrame(DeltaSeconds);
        }
        var RootGroups = Animators.Where(Animator => Animator.RootMotionTarget is not null)
            .GroupBy(Animator => Animator.RootMotionTarget!.EntityId, StringComparer.Ordinal)
            .ToDictionary(Group => Group.Key, Group => Group.ToArray(), StringComparer.Ordinal);
        foreach (var Sources in RootGroups.Values)
        {
            if (Sources.Length != 1)
            {
                foreach (var Animator in Sources)
                {
                    Animator.RejectRootMotion("ROOT_MOTION_CONFLICT", "Multiple Animators drive root motion on the same entity.");
                }
                continue;
            }
            var Source = Sources[0];
            var Target = Source.RootMotionTarget!;
            var Player = LocalPlayerController;
            var PlayerMovement = Movements.SingleOrDefault(Movement => Movement.Entity == Target);
            var PlayerOwnsFacing = Player?.ControlledEntity == Target &&
                Player.FacingMesh == Source.RootMotionMesh && PlayerMovement is not null;
            try
            {
                var TargetWorld = EntityTransformMath.WorldMatrix(Target);
                var BeforePosition = TargetWorld.Translation;
                if (Source.RootMotionDelta != Matrix4x4.Identity)
                {
                    var TargetInvertible = TransformMath.TryInverse(TargetWorld, out var TargetInverse);
                    if (!TargetInvertible)
                    {
                        throw new InvalidDataException("Root motion target transform must be invertible");
                    }
                    var MeshRelative = EntityTransformMath.WorldMatrix(Source.RootMotionMesh!.Entity) * TargetInverse;
                    var RelativeInvertible = TransformMath.TryInverse(MeshRelative, out var RelativeInverse);
                    if (!RelativeInvertible)
                    {
                        throw new InvalidDataException("Root motion mesh transform must be invertible");
                    }
                    var Delta = RelativeInverse * Source.RootMotionDelta * MeshRelative;
                    var ProposedWorld = Delta * TargetWorld;
                    if (PlayerOwnsFacing)
                    {
                        var Translation = ProposedWorld.Translation - BeforePosition;
                        var Intent = PlayerMovement!.AnimationIntentVelocity;
                        if (new Vector2(Intent.X, Intent.Z).LengthSquared() <= 0.000001f)
                        {
                            Translation.X = 0;
                            Translation.Z = 0;
                        }
                        // MeshRelative already orients source motion; the Player owns entity facing.
                        ProposedWorld = TargetWorld;
                        ProposedWorld.Translation = BeforePosition + Translation;
                    }
                    EntityTransformMath.SetWorldMatrix(Target, ProposedWorld);
                }
                Source.CompleteRootMotion(EntityTransformMath.WorldMatrix(Target).Translation - BeforePosition,
                    PlayerOwnsFacing);
            }
            catch (Exception Error) when (Error is InvalidDataException or ArgumentException or InvalidOperationException)
            {
                Source.RejectRootMotion("ROOT_MOTION_TRANSFORM_INVALID", Error.Message);
            }
        }
        foreach (var Movement in Movements)
        {
            Movement.CommitMovement(RootGroups.ContainsKey(Movement.Entity.EntityId),
                Before[Movement.ComponentId], DeltaSeconds);
        }
    }
}
