using System.Numerics;

namespace SharpEngine.Components;

/// <summary>One committed animation root-motion delta, expressed in world space.</summary>
public readonly record struct RootMotionContribution(
    string AnimatorComponentId,
    string TargetEntityId,
    string MeshComponentId,
    Matrix4x4 TargetWorld,
    Matrix4x4 MeshRelative,
    Matrix4x4 WorldDelta,
    Vector3 WorldTranslation,
    double AdvanceSeconds,
    float DeltaSeconds);

/// <summary>
/// Composition point for movement implementations that replace the engine defaults.
/// The engine calls every enabled authority on an active entity at the frame boundary that runs
/// after all Evaluate callbacks and before any LateUpdate camera:
/// <list type="number">
/// <item><see cref="BeginMovementFrame"/> for every authority, ordered by component id.</item>
/// <item><see cref="ConsumeRootMotion"/> once per root-motion group whose target holds this authority.</item>
/// <item><see cref="EndMovementFrame"/>, where the authority commits its own transform changes.</item>
/// </list>
/// An entity that holds an authority does not receive the built-in root motion write, and its
/// built-in <see cref="MovementComponent"/> commit is skipped so only one writer owns the transform.
/// </summary>
public interface IMovementAuthority
{
    void BeginMovementFrame(float DeltaSeconds);
    bool ConsumeRootMotion(in RootMotionContribution Contribution);
    void EndMovementFrame(float DeltaSeconds);
}
