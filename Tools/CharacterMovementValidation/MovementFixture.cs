using System.Text.Json.Nodes;
using SharpEngine.Assets;
using SharpEngine.Components;

/// <summary>Entity and type ids used by the plugin validation.</summary>
internal static class CharacterMovementIds
{
    internal const string CharacterMovement = "6f0c3d1a-8b2e-4f7c-9a51-2c4e8d3b7a10";
    internal const string WalkingMode = "1b7e9c42-5d38-4a06-8f2b-73e1c9d45b21";
    internal const string FallingMode = "a3d24f68-7c19-4e85-b027-6f9d1e3c8a32";
    internal const string JumpModifier = "c58a7e13-2f46-4b90-8d7a-5e3b9f16c043";
    internal const string SprintModifier = "0e9b5d76-4a13-42cf-9b68-d27f4a8e2154";
    internal const string SpeedLimitModifier = "5c1f8a74-9d26-4e38-b1a5-8f2d6c47e065";
}

/// <summary>
/// One scripted character on a floor: capsule body, AI controller for intent, both modes, the
/// speed limit and jump/sprint modifiers, and the movement authority. The animator targets a child
/// skeletal mesh so root motion resolves onto the character entity.
/// </summary>
internal sealed class MovementFixture : IDisposable
{
    internal MovementFixture(ComponentRegistry Registry, AssetRegistry Assets, string MotionAssetId)
    {
        var Scene = new SceneData { WorldId = "validation://character-movement", Name = "Character Movement" };
        var Floor = Scene.CreateEntity(Registry, "Floor");
        Scene.AddComponent(Registry, Floor.EntityId, "engine.box-collider", new JsonObject
        {
            ["Size"] = new JsonArray(40, 1, 40),
            ["Center"] = new JsonArray(0, -0.5f, 0),
        });
        var CharacterEntity = Scene.CreateEntity(Registry, "Character");
        Scene.AddComponent(Registry, CharacterEntity.EntityId, "engine.ai-controller", new JsonObject
        {
            ["ControlledEntity"] = CharacterEntity.EntityId,
        });
        Scene.AddComponent(Registry, CharacterEntity.EntityId, "engine.capsule-collider", new JsonObject
        {
            ["Radius"] = 0.5f,
            ["Height"] = 2,
            ["Center"] = new JsonArray(0, 1, 0),
        });
        Scene.AddComponent(Registry, CharacterEntity.EntityId, CharacterMovementIds.WalkingMode);
        Scene.AddComponent(Registry, CharacterEntity.EntityId, CharacterMovementIds.FallingMode);
        Scene.AddComponent(Registry, CharacterEntity.EntityId, CharacterMovementIds.JumpModifier);
        Scene.AddComponent(Registry, CharacterEntity.EntityId, CharacterMovementIds.SprintModifier);
        Scene.AddComponent(Registry, CharacterEntity.EntityId, CharacterMovementIds.SpeedLimitModifier);
        Scene.AddComponent(Registry, CharacterEntity.EntityId, CharacterMovementIds.CharacterMovement);
        var Model = Scene.CreateEntity(Registry, "Model", CharacterEntity.EntityId);
        var Mesh = Scene.AddComponent(Registry, Model.EntityId, "engine.skeletal-mesh").Single();
        Mesh.State["Mesh"] = "engine://meshes/robot-skeletal";
        var AnimatorRecord = Scene.AddComponent(Registry, CharacterEntity.EntityId, "engine.animator").Single();
        AnimatorRecord.State["Target"] = Mesh.ComponentId;
        AnimatorRecord.State["Clip"] = MotionAssetId;
        var Camera = Scene.CreateEntity(Registry, "Camera");
        Scene.AddComponent(Registry, Camera.EntityId, "engine.camera");
        Scene.ActiveCameraId = Camera.EntityId;
        Scene.Validate(Registry);

        Runtime = new RuntimeWorld(Scene, Registry, Assets);
        Entity = Runtime.FindEntity(CharacterEntity.EntityId)!;
        Transform = Entity.GetComponent<TransformComponent>()!;
        Controllable = Entity.GetComponent<ControllableComponent>()!;
        Controller = Entity.GetComponent<AiControllerComponent>()!;
        Character = Entity.GetComponent<SharpEngine.CharacterMovement.CharacterMovementComponent>()!;
        Animator = Entity.GetComponent<AnimatorComponent>()!;
    }

    internal RuntimeWorld Runtime { get; }
    internal SharpEntity Entity { get; }
    internal TransformComponent Transform { get; }
    internal ControllableComponent Controllable { get; }
    internal AiControllerComponent Controller { get; }
    internal SharpEngine.CharacterMovement.CharacterMovementComponent Character { get; }
    internal AnimatorComponent Animator { get; }

    public void Dispose()
    {
        Runtime.Dispose();
    }
}
