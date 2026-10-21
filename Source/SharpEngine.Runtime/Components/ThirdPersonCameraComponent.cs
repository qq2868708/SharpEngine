using System.Numerics;
using System.Text.Json.Nodes;
using SharpEngine.Mathematics;

namespace SharpEngine.Components;

[ComponentTypeId("engine.third-person-camera"), ComponentSchemaVersion(1), DisallowMultipleComponent,
    AddComponentMenu("Gameplay/Third Person Camera"), RequireComponent(typeof(CameraComponent)),
    RequireComponent(typeof(TransformComponent))]
public sealed partial class ThirdPersonCameraComponent : SharpComponent
{
    public SharpEntity? Target;
    public Vector3 TargetOffset = new(0, 0.4f, 0);
    [Range(0.25, 20)] public float Distance = 3.5f;
    public float Yaw;
    [Range(-80, 75)] public float Pitch = -15;
    [Range(0.001, 2)] public float LookSensitivity = 0.15f;

    private string Code = "CAMERA_TARGET_EMPTY";

    internal void AddLookInput(Vector2 Delta)
    {
        Yaw = Mathf.Repeat(Yaw - Delta.X * LookSensitivity + 180, 360) - 180;
        Pitch = Math.Clamp(Pitch - Delta.Y * LookSensitivity, -80, 75);
    }

    protected override void OnLateUpdate(float DeltaSeconds) { PrepareCamera(); }

    protected override void OnPrepareRender() { PrepareCamera(); }

    private void PrepareCamera()
    {
        if (Target is null || !Target.IsAlive)
        {
            Code = "CAMERA_TARGET_EMPTY";
            return;
        }
        if (Target == Entity)
        {
            Code = "CAMERA_TARGET_SELF";
            return;
        }
        var TargetMatrix = EntityTransformMath.WorldMatrix(Target);
        var Pivot = Vector3.Transform(TargetOffset, TargetMatrix);
        var Rotation = QuaternionMath.Euler(new Vector3(Pitch, Yaw, 0));
        var Position = Pivot + Vector3.Transform(Vector3.UnitZ * Distance, Rotation);
        EntityTransformMath.SetWorldMatrix(Entity, TransformMath.Trs(Position, Rotation, Vector3.One));
        Code = "CAMERA_READY";
    }

    protected internal override ComponentFrameState CaptureFrameState()
    {
        return new ThirdPersonCameraFrameState(new JsonObject
        {
            ["schemaVersion"] = 1,
            ["entityId"] = Entity.EntityId,
            ["componentId"] = ComponentId,
            ["targetEntityId"] = Target?.EntityId,
            ["code"] = Code,
            ["yaw"] = Yaw,
            ["pitch"] = Pitch,
            ["distance"] = Distance,
        });
    }

    public static void Validate(in GeneratedState State, ComponentDiagnosticSink Diagnostics)
    {
        if (State.Distance < 0.25f || State.Distance > 20 ||
            State.Pitch < -80 || State.Pitch > 75 || State.LookSensitivity <= 0)
        {
            Diagnostics.Error("THIRD_PERSON_CAMERA_PARAMETERS", nameof(Distance),
                "Third-person camera distance, pitch, and sensitivity must be within their supported ranges.");
        }
    }
}

public sealed class ThirdPersonCameraFrameState(JsonObject State) : ComponentFrameState
{
    public override JsonObject ToJson() { return (JsonObject)State.DeepClone(); }
}
