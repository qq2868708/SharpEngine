using System.Text.Json.Nodes;
using SharpEngine.Animation;
using SharpEngine.Assets;
using SharpEngine.Components;

internal static class AnimationStateMachineValidation
{
    internal static void Run(string Root, AssetRegistry Assets, ComponentRegistry Registry,
        Action<bool, string> Check)
    {
        var Controller = AnimationStateMachineContent.Load(Assets,
            "engine://animation-state-machines/uefn-mannequin-locomotion");
        var Descriptor = Assets.Find(Controller.AssetId);
        Check(Descriptor is { Source: AssetSource.Engine, ReadOnly: true, Exists: true } &&
            Descriptor.Tags.Contains("builtin", StringComparer.OrdinalIgnoreCase) &&
            Descriptor.Tags.Contains("preset", StringComparer.OrdinalIgnoreCase) &&
            Descriptor.Metadata.TryGetValue("preset", out var Preset) && Preset == "true" &&
            Descriptor.Metadata.TryGetValue("skeleton", out var Skeleton) &&
            Skeleton == "skeletons/uefn-mannequin",
            "locomotion controller is a read-only built-in engine preset");
        Check(Descriptor?.Metadata.TryGetValue("clips", out var ClipCount) == true && ClipCount == "4",
            "locomotion preset declares its complete Clip dependency count");
        Check(Controller.InitialStateId == "idle" && Controller.States.Count == 4 &&
            Controller.Transitions.Count == 8 && Controller.Parameters.Count == 2,
            "state machine asset loads its ordered graph");
        Check(Controller.ClipIds.SequenceEqual(new[]
        {
            "engine://animations/uefn-mannequin-idle",
            "engine://animations/uefn-mannequin-jump",
            "engine://animations/uefn-mannequin-run",
            "engine://animations/uefn-mannequin-walk",
        }), "state machine exposes deterministic clip dependencies");
        AnimationStateMachineContent.ValidateDependencies(Assets, Controller.AssetId,
            "engine://meshes/uefn-mannequin");
        Check(true, "all locomotion preset Clip dependencies bind to the mannequin mesh");
        var Idle = Controller.State("idle");
        var Walk = Controller.SelectTransition("idle", 0, new Dictionary<string, double>
        {
            ["speed"] = 1,
            ["jump"] = 0,
        });
        Check(Walk?.ToStateId == "walk", "speed selects idle to walk transition");
        var Jump = Controller.SelectTransition("walk", 0, new Dictionary<string, double>
        {
            ["speed"] = 1,
            ["jump"] = 1,
        });
        Check(Jump?.ToStateId == "jump", "jump parameter takes precedence by serialized transition order");
        Check(AnimationStateMachineAsset.Parse(Controller.Serialize(false)).Serialize(false) == Controller.Serialize(false),
            "state machine serialization round trips deterministically");
        var Invalid = Controller.ToJson();
        Invalid["transitions"]!.AsArray()[0]! ["conditions"]!.AsArray()[0]! ["parameterId"] = "missing";
        try
        {
            _ = AnimationStateMachineAsset.Parse(Invalid.ToJsonString());
            throw new InvalidOperationException("Expected state machine dependency rejection.");
        }
        catch (InvalidDataException)
        {
            Check(true, "unknown transition parameter is rejected");
        }

        var Data = new SceneData();
        var Entity = Data.CreateEntity(Registry, "Locomotion");
        var Mesh = Data.AddComponent(Registry, Entity.EntityId, "engine.skeletal-mesh", new JsonObject
        {
            ["Mesh"] = "engine://meshes/uefn-mannequin",
        }).Single();
        Data.AddComponent(Registry, Entity.EntityId, "engine.controllable");
        Data.AddComponent(Registry, Entity.EntityId, "engine.movement");
        var Animator = Data.AddComponent(Registry, Entity.EntityId, "engine.animator", new JsonObject
        {
            ["Controller"] = Controller.AssetId,
            ["Target"] = Mesh.ComponentId,
            ["MovementSpeedParameter"] = "speed",
        }).Single();
        var ControllerEntity = Data.CreateEntity(Registry, "Controller");
        var Player = Data.AddComponent(Registry, ControllerEntity.EntityId, "engine.player-controller", new JsonObject
        {
            ["ControlledEntity"] = Entity.EntityId,
        }).Single();
        Animator.State["MovementSource"] = Data.Entities.Single(Item => Item.EntityId == Entity.EntityId).Components
            .Single(Component => Component.TypeId == "engine.movement").ComponentId;
        Data.Validate(Registry);
        using var Runtime = new RuntimeWorld(Data, Registry, Assets);
        var LiveController = (PlayerControllerComponent)Runtime.FindComponent(Player.ComponentId)!;
        LiveController.SetMoveInput(System.Numerics.Vector2.UnitX);
        Runtime.Tick(0.2f);
        var Frame = Runtime.Frame.Get<AnimatorFrameState>(Animator.ComponentId)!.ToJson();
        Check(Frame["stateId"]?.GetValue<string>() == "walk" && Frame["code"]?.GetValue<string>() == "ANIMATOR_READY",
            "movement speed drives the runtime locomotion state");
        LiveController.ClearMoveInput();
        Runtime.Tick(0.3f);
        Check(Runtime.Frame.Get<AnimatorFrameState>(Animator.ComponentId)!.ToJson()["stateId"]!.GetValue<string>() == "idle",
            "deceleration returns locomotion to idle");
        Check(File.Exists(Path.Combine(Root, "Content", "Assets", "AnimationControllers", "UEFNMannequin.sanimationstatemachine")),
            "controller source remains a readable engine asset");
    }
}
