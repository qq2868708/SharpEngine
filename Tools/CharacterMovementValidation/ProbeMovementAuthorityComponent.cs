using SharpEngine.Components;

namespace CharacterMovementValidation;

/// <summary>
/// A minimal third-party movement authority. It exists to prove the engine rejects two authorities
/// on one entity deterministically instead of letting them race for the transform.
/// </summary>
[ComponentTypeId("d41f6b29-3c80-4e17-9a6d-5b8e2f4c71a3"), ComponentSchemaVersion(1),
    DisallowMultipleComponent]
public sealed partial class ProbeMovementAuthorityComponent : SharpComponent, IMovementAuthority
{
    public void BeginMovementFrame(float DeltaSeconds)
    {
    }

    public bool ConsumeRootMotion(in RootMotionContribution Contribution)
    {
        return false;
    }

    public void EndMovementFrame(float DeltaSeconds)
    {
    }
}
