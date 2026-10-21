using System.Numerics;
using SharpEngine.Logging;
using SharpEngine.Mathematics;

namespace SharpEngine.Components;

public sealed partial class RuntimeWorld
{
    // One diagnostic per offending id keeps the per-frame report list bounded.
    private readonly HashSet<string> ReportedMovementConflicts = new(StringComparer.Ordinal);

    /// <summary>All intent velocities exist before sampling; motion commits before any LateUpdate camera.</summary>
    private void FinalizeAnimationAndMovement(float DeltaSeconds, SharpComponent[] Components)
    {
        var Movements = Components.OfType<MovementComponent>().ToArray();
        var Before = Movements.ToDictionary(Movement => Movement.ComponentId,
            Movement => EntityTransformMath.WorldMatrix(Movement.Entity).Translation, StringComparer.Ordinal);
        var Authorities = Components.OfType<IMovementAuthority>()
            .Select(Authority => (Authority, Component: (SharpComponent)Authority))
            .OrderBy(Entry => Entry.Component.ComponentId, StringComparer.Ordinal)
            .ToArray();
        var AuthorityEntities = new HashSet<string>(StringComparer.Ordinal);
        var ClaimedAuthorities = new List<(IMovementAuthority Authority, SharpComponent Component)>();
        foreach (var Entry in Authorities)
        {
            if (!AuthorityEntities.Add(Entry.Component.Entity.EntityId))
            {
                ReportMovementConflict("MOVEMENT_AUTHORITY_CONFLICT", Entry.Component.ComponentId,
                    "Only one movement authority may own an entity's transform; the extra authority is ignored.");
                continue;
            }
            ClaimedAuthorities.Add(Entry);
        }
        Authorities = ClaimedAuthorities.ToArray();
        foreach (var Movement in Movements)
        {
            if (!AuthorityEntities.Contains(Movement.Entity.EntityId))
            {
                continue;
            }
            // The authority owns the transform; a MovementComponent on the same entity must stay silent.
            ReportMovementNote("MOVEMENT_AUTHORITY_OVERRIDES_MOVEMENT", Movement.ComponentId,
                "engine.movement is ignored while a movement authority owns this entity.");
        }
        foreach (var Movement in Movements)
        {
            Movement.SuppressForAuthority(AuthorityEntities.Contains(Movement.Entity.EntityId));
        }
        foreach (var (Authority, Component) in Authorities)
        {
            try
            {
                Authority.BeginMovementFrame(DeltaSeconds);
            }
            catch (Exception Error)
            {
                Report("MOVEMENT_AUTHORITY_BEGIN_EXCEPTION", Component.ComponentId, "Finalize", Error);
            }
        }
        var Animators = Components.OfType<AnimatorComponent>().ToArray();
        foreach (var Animator in Animators)
        {
            try
            {
                Animator.PrepareAnimationFrame(DeltaSeconds);
            }
            catch (Exception Error)
            {
                // One broken animation must not abort movement or the rest of the frame.
                Report("ANIMATION_PREPARE_EXCEPTION", Animator.ComponentId, "Finalize", Error);
            }
        }
        var RootGroups = Animators.Where(Animator => Animator.RootMotionTarget is not null)
            .GroupBy(Animator => Animator.RootMotionTarget!.EntityId, StringComparer.Ordinal)
            .ToDictionary(Group => Group.Key, Group => Group.ToArray(), StringComparer.Ordinal);
        foreach (var TargetId in RootGroups.Keys.OrderBy(Key => Key, StringComparer.Ordinal))
        {
            var Sources = RootGroups[TargetId];
            if (Sources.Length != 1)
            {
                foreach (var Animator in Sources)
                {
                    Animator.RejectRootMotion("ROOT_MOTION_CONFLICT", "Multiple Animators drive root motion on the same entity.");
                }
                continue;
            }
            ApplyRootMotion(Sources[0], Authorities, Movements, DeltaSeconds);
        }
        foreach (var (Authority, Component) in Authorities)
        {
            try
            {
                Authority.EndMovementFrame(DeltaSeconds);
            }
            catch (Exception Error)
            {
                Report("MOVEMENT_AUTHORITY_END_EXCEPTION", Component.ComponentId, "Finalize", Error);
            }
        }
        foreach (var Movement in Movements)
        {
            if (AuthorityEntities.Contains(Movement.Entity.EntityId))
            {
                continue;
            }
            try
            {
                Movement.CommitMovement(RootGroups.ContainsKey(Movement.Entity.EntityId),
                    Before[Movement.ComponentId], DeltaSeconds);
            }
            catch (Exception Error)
            {
                // Commit is the single transform writer for intent movement; isolate its failures.
                Report("MOVEMENT_COMMIT_EXCEPTION", Movement.ComponentId, "Finalize", Error);
            }
        }
    }

    private void ApplyRootMotion(AnimatorComponent Source,
        (IMovementAuthority Authority, SharpComponent Component)[] Authorities,
        MovementComponent[] Movements, float DeltaSeconds)
    {
        var Target = Source.RootMotionTarget!;
        try
        {
            var TargetWorld = EntityTransformMath.WorldMatrix(Target);
            var BeforePosition = TargetWorld.Translation;
            if (Source.RootMotionDelta == Matrix4x4.Identity)
            {
                Source.CompleteRootMotion(Vector3.Zero);
                return;
            }
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
            var Authority = FindAuthority(Target, Authorities);
            if (Authority is not null)
            {
                var Contribution = new RootMotionContribution(Source.ComponentId, Target.EntityId,
                    Source.RootMotionMesh!.ComponentId, TargetWorld, MeshRelative, Delta,
                    ProposedWorld.Translation - BeforePosition, Source.RootMotionAdvance, DeltaSeconds);
                if (Authority.ConsumeRootMotion(in Contribution))
                {
                    var Consumed = EntityTransformMath.WorldMatrix(Target).Translation - BeforePosition;
                    Source.CompleteRootMotion(Consumed, false, Authority.GetType().FullName);
                    return;
                }
            }
            var Player = LocalPlayerController;
            var PlayerMovement = Movements.SingleOrDefault(Movement => Movement.Entity == Target);
            var PlayerOwnsFacing = Player?.ControlledEntity == Target &&
                Player.FacingMesh == Source.RootMotionMesh && PlayerMovement is not null;
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
            Source.CompleteRootMotion(EntityTransformMath.WorldMatrix(Target).Translation - BeforePosition,
                PlayerOwnsFacing);
        }
        catch (Exception Error) when (Error is InvalidDataException or ArgumentException or InvalidOperationException)
        {
            Source.RejectRootMotion("ROOT_MOTION_TRANSFORM_INVALID", Error.Message);
        }
    }

    private void ReportMovementConflict(string Code, string ComponentId, string Message)
    {
        if (ReportedMovementConflicts.Add(Code + "\n" + ComponentId))
        {
            Report(Code, ComponentId, "Finalize", new InvalidOperationException(Message));
        }
    }

    /// <summary>A supported composition decision: recorded once as a warning, never as a failure.</summary>
    private void ReportMovementNote(string Code, string ComponentId, string Message)
    {
        if (!ReportedMovementConflicts.Add(Code + "\n" + ComponentId))
        {
            return;
        }
        SharpLog.Warning("Component", Message,
            new Dictionary<string, string>
            {
                ["componentId"] = ComponentId,
                ["code"] = Code,
                ["stage"] = "Finalize",
            });
    }

    private static IMovementAuthority? FindAuthority(SharpEntity Target,
        (IMovementAuthority Authority, SharpComponent Component)[] Authorities)
    {
        foreach (var (Authority, Component) in Authorities)
        {
            if (Component.Entity == Target)
            {
                return Authority;
            }
        }
        return null;
    }
}
