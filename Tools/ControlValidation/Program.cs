using System.Numerics;
using System.Text.Json.Nodes;
using SharpEngine.Components;

var Registry = ComponentRegistry.CreateBuiltin();
var Scene = new SceneData
{
    Name = "Control validation",
};
var Target = Scene.CreateEntity(Registry, "Target");
Scene.AddComponent(Registry, Target.EntityId, "engine.controllable");
Scene.AddComponent(Registry, Target.EntityId, "engine.movement", new JsonObject
{
    ["MaxSpeed"] = 5,
    ["Acceleration"] = 20,
    ["Deceleration"] = 30,
});
var Player = Scene.CreateEntity(Registry, "Player controller");
var PlayerController = Scene.AddComponent(Registry, Player.EntityId, "engine.player-controller", new JsonObject
{
    ["ControlledEntity"] = Target.EntityId,
    ["Priority"] = 0,
}).Single();
Scene.Validate(Registry);

var Assertions = 0;
void Check(bool Condition, string Message)
{
    if (!Condition)
    {
        throw new InvalidOperationException(Message);
    }
    Assertions++;
}

using var Runtime = new RuntimeWorld(Scene, Registry);
var TargetEntity = Runtime.FindEntity(Target.EntityId)!;
var Controller = (PlayerControllerComponent)Runtime.FindComponent(PlayerController.ComponentId)!;
var Controllable = TargetEntity.GetComponent<ControllableComponent>()!;
var Movement = TargetEntity.GetComponent<MovementComponent>()!;
Check(Controller.ControlledEntity == TargetEntity, "Controller entity references are reconstructed from stable entity IDs");
Check(Runtime.Frame.Get<ControllerFrameState>(Controller.ComponentId)!.ToJson()["code"]!.GetValue<string>() == "CONTROLLER_UNBOUND",
    "A controller reports an unbound state before its first update");

Controller.SetMoveInput(Vector2.UnitX);
Runtime.Tick(0.1f);
var PositionAfterMove = TargetEntity.GetComponent<TransformComponent>()!.Position;
var ControllerState = Runtime.Frame.Get<ControllerFrameState>(Controller.ComponentId)!.ToJson();
var ControllableState = Runtime.Frame.Get<ControllableFrameState>(Controllable.ComponentId)!.ToJson();
var MovementState = Runtime.Frame.Get<MovementFrameState>(Movement.ComponentId)!.ToJson();
Check(PositionAfterMove.X > 0.19f && PositionAfterMove.X < 0.21f && MathF.Abs(PositionAfterMove.Z) < 0.0001f,
    "Movement consumes controller intent during Evaluate and advances the target transform");
Check(ControllerState["code"]!.GetValue<string>() == "CONTROLLER_READY" &&
    ControllerState["targetComponentId"]!.GetValue<string>() == Controllable.ComponentId,
    "Controller binds only through the target's explicit ControllableComponent");
Check(ControllableState["controllerComponentId"]!.GetValue<string>() == Controller.ComponentId &&
    MovementState["code"]!.GetValue<string>() == "MOVEMENT_MOVING",
    "Target and movement publish immutable committed runtime observations");
Check(!Runtime.Snapshot().Serialize().Contains("CONTROLLER_READY", StringComparison.Ordinal),
    "Controller and movement runtime state stay out of authoritative scene serialization");

Controller.ClearMoveInput();
Runtime.Tick(0.1f);
Check(MathF.Abs(TargetEntity.GetComponent<TransformComponent>()!.Position.X - PositionAfterMove.X) < 0.0001f,
    "Clearing controller intent decelerates the movement without teleporting the target");

var Unprocessable = Scene.CreateEntity(Registry, "Unprocessable");
var Ai = Scene.AddComponent(Registry, Unprocessable.EntityId, "engine.ai-controller", new JsonObject
{
    ["ControlledEntity"] = Target.EntityId,
    ["Priority"] = 10,
}).Single();
var MissingControllerEntity = Scene.CreateEntity(Registry, "Missing controllable controller");
var MissingControllerRecord = Scene.AddComponent(Registry, MissingControllerEntity.EntityId, "engine.ai-controller", new JsonObject
{
    ["ControlledEntity"] = Unprocessable.EntityId,
    ["Priority"] = 0,
}).Single();
var ReboundScene = Scene.Clone();
using var ReboundRuntime = new RuntimeWorld(ReboundScene, Registry);
var ReboundTarget = ReboundRuntime.FindEntity(Target.EntityId)!;
var AiController = (AiControllerComponent)ReboundRuntime.FindComponent(Ai.ComponentId)!;
AiController.SetMoveInput(Vector2.UnitY);
ReboundRuntime.Tick(0.1f);
var ReboundControllable = ReboundTarget.GetComponent<ControllableComponent>()!;
Check(ReboundControllable is not null &&
    ReboundRuntime.Frame.Get<ControllerFrameState>(AiController.ComponentId)!.ToJson()["code"]!.GetValue<string>() == "CONTROLLER_READY" &&
    ReboundRuntime.Frame.Get<ControllableFrameState>(ReboundControllable.ComponentId)!.ToJson()["controllerComponentId"]!.GetValue<string>() == AiController.ComponentId,
    "AI and Player controllers share the same controllable binding and priority arbitration");

var MissingTarget = (AiControllerComponent)ReboundRuntime.FindComponent(MissingControllerRecord.ComponentId)!;
var MissingState = ReboundRuntime.Frame.Get<ControllerFrameState>(MissingTarget.ComponentId)!.ToJson();
Check(MissingState["code"]!.GetValue<string>() == "CONTROLLER_TARGET_NOT_CONTROLLABLE",
    "A controller with no controllable target publishes a structured availability code");

Console.WriteLine($"[ControlValidation] PASS: {Assertions} assertions; controllable targets, Player/AI controllers and movement evaluation");
