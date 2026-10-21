using System.Numerics;
using System.Text.Json.Nodes;
using SharpEngine.Animation;
using SharpEngine.Assets;
using SharpEngine.Components;
using SharpEngine.Subsystems;
using SharpEngine.Input;
using SharpEngine.UI;
using SharpEngine.World;

var DefaultRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
    "..", "..", "..", "..", ".."));
var EngineRoot = Path.GetFullPath(args.Length > 0 ? args[0] : DefaultRoot);
var ProjectRoot = Path.GetFullPath(args.Length > 1 ? args[1] : EngineRoot);
var ProjectManifestPath = Directory.GetFiles(ProjectRoot, "*.sharpproject",
    SearchOption.TopDirectoryOnly).SingleOrDefault();

var Registry = ComponentRegistry.CreateBuiltin();
var PrefabPath = Path.Combine(ProjectRoot, "Content", "Prefabs", "CapsuleCharacter.sprefab");
var WorldPath = Path.Combine(ProjectRoot, "Content", "Worlds", "Main.sworld");
var PrefabSourceText = File.ReadAllText(PrefabPath);
var SourceText = File.ReadAllText(WorldPath);
var Prefab = WorldDocument.Load(PrefabPath, Registry).Data;
var World = SceneData.Parse(SourceText, Registry);
var AuthoringText = World.Serialize();
var Assertions = 0;

void Check(bool Condition, string Message)
{
    if (!Condition)
    {
        throw new InvalidOperationException(Message);
    }
    Assertions++;
}

Check(Prefab.IsPrefab && Prefab.Entities.Count == 3,
    "Animated character asset is a single-root Prefab");
var PrefabRoot = Prefab.Entities.Single(Entity => Entity.ParentId is null);
var PrefabSkeletalMesh = Prefab.Entities.SelectMany(Entity => Entity.Components).SingleOrDefault(Component =>
    Component.TypeId == "engine.skeletal-mesh");
var PrefabAnimator = PrefabRoot.Components.SingleOrDefault(Component =>
    Component.TypeId == "engine.animator");
Check(PrefabSkeletalMesh is not null &&
    PrefabSkeletalMesh.State["Mesh"]?.GetValue<string>() == "engine://meshes/uefn-mannequin" &&
    PrefabRoot.Components.All(Component => Component.TypeId != "engine.static-mesh" &&
        Component.TypeId != "engine.mesh-renderer"),
    "Prefab renders the registered UEFN mannequin skeletal mesh without a static capsule mesh");
Check(PrefabAnimator is not null &&
    PrefabAnimator.State["Target"]?.GetValue<string>() == PrefabSkeletalMesh!.ComponentId &&
    PrefabAnimator.State["Controller"]?.GetValue<string>() == "engine://animation-state-machines/uefn-mannequin-locomotion" &&
    PrefabAnimator.State["MovementSource"]?.GetValue<string>() == "capsule-character-movement" &&
    PrefabAnimator.State["Playing"]?.GetValue<bool>() == true,
    "Prefab drives its skeletal mesh with the registered locomotion state machine");
Check(PrefabRoot.Components.Any(Component => Component.TypeId == "engine.capsule-collider") &&
    PrefabRoot.Components.Any(Component => Component.TypeId == "engine.controllable") &&
    PrefabRoot.Components.Any(Component => Component.TypeId == "engine.movement") &&
    PrefabRoot.Components.Any(Component => Component.TypeId == "engine.player-controller"),
    "Prefab contains non-rendering collision, controllable, movement and player controller components");
var ScenePlayer = World.Entities.Single(Entity => Entity.EntityId == "player");
Check(World.Entities.Where(Entity => Entity.ParentId == ScenePlayer.EntityId).SelectMany(Entity => Entity.Components).Any(Component => Component.TypeId == "engine.skeletal-mesh" &&
    Component.State["Mesh"]?.GetValue<string>() == "engine://meshes/uefn-mannequin") &&
    ScenePlayer.Components.Any(Component => Component.TypeId == "engine.animator" &&
        Component.State["Controller"]?.GetValue<string>() == "engine://animation-state-machines/uefn-mannequin-locomotion") &&
    ScenePlayer.Components.Any(Component => Component.TypeId == "engine.player-controller"),
    "Main scene contains the animated character Prefab instance contents");
var Destination = new SceneData
{
    Name = "Prefab Destination",
};
var Copies = Destination.Instantiate(Prefab, Registry);
Destination.Validate(Registry);
var CopiedRoot = Copies.Single(Entity => Entity.ParentId is null);
var CopiedController = CopiedRoot.Components.Single(Component =>
    Component.TypeId == "engine.player-controller");
Check(CopiedController.State["ControlledEntity"]?.GetValue<string>() == CopiedRoot.EntityId,
    "Prefab instantiation remaps the self ControlledEntity reference");
var CopiedMesh = Copies.SelectMany(Entity => Entity.Components).Single(Component => Component.TypeId == "engine.skeletal-mesh");
var CopiedAnimator = CopiedRoot.Components.Single(Component => Component.TypeId == "engine.animator");
Check(CopiedAnimator.State["Target"]?.GetValue<string>() == CopiedMesh.ComponentId &&
    CopiedMesh.ComponentId != PrefabSkeletalMesh!.ComponentId,
    "Prefab instantiation remaps Animator.Target to the new skeletal mesh component");

var Assets = AssetRegistry.Load(EngineRoot, ProjectRoot, ProjectManifestPath);
using (var CopiedRuntime = new RuntimeWorld(Destination, Registry, Assets))
{
    var LiveAnimator = (AnimatorComponent)CopiedRuntime.FindComponent(CopiedAnimator.ComponentId)!;
    Check(LiveAnimator.Target?.ComponentId == CopiedMesh.ComponentId &&
        CopiedRuntime.Frame.Get<SkeletonFrameState>(CopiedMesh.ComponentId)?.Code == "SKELETON_READY",
        "Reconstructed Prefab resolves the remapped live skeleton and prepares its first frame");
}
var Input = InputRuntimeService.Load(Assets);
using var Instance = new RuntimeInstance(Assets, null, Input, "main");
var Runtime = Instance.CreateWorld(World, Registry);
Input.SetRouteEnabled(InputRoute.Gameplay, true);
var Player = Runtime.FindEntity("player") ?? throw new InvalidOperationException("Main scene player missing");
var Transform = Player.GetComponent<TransformComponent>() ??
    throw new InvalidOperationException("Animated player Transform missing");
var Animator = Player.GetComponent<AnimatorComponent>()!;
var Mesh = Animator.Target!;
var Collider = Player.GetComponent<CapsuleColliderComponent>()!;
var InitialSkeleton = Runtime.Frame.Get<SkeletonFrameState>(Mesh.ComponentId)!;
Check(Animator.Target?.ComponentId == Mesh.ComponentId && InitialSkeleton.Code == "SKELETON_READY" &&
    InitialSkeleton.Positions.Count > 0,
    "Scene Animator explicitly targets a prepared skeletal mesh before the first Tick");
var Start = Transform.Position;
var Collision = Runtime.Collision;
var CollisionHit = Collision.Raycast(Start + new Vector3(0, 3, 0), -Vector3.UnitY, 10);
Check(CollisionHit is not null && CollisionHit.ComponentId ==
    Collider.ComponentId &&
    CollisionHit.EntityId == Player.EntityId,
    "The non-rendering capsule collision component participates in runtime Trace queries");
Input.ProcessNative("main", NativeInputEventKind.KeyDown, (uint)'D', 0, 0, 0, 0, 0,
    false, true);
Runtime.Tick(0.1f);
Runtime.Tick(0.1f);
Runtime.Tick(0.1f);
var AfterMove = Transform.Position;
Check(AfterMove.X > Start.X && MathF.Abs(AfterMove.Z - Start.Z) < 0.0001f,
    "Play input reaches PlayerController and changes the animated character position");
Input.ProcessNative("main", NativeInputEventKind.KeyUp, (uint)'D', 0, 0, 0, 0, 0,
    false, true);
Runtime.Tick(0.1f);
Check(Transform.Position.X >= AfterMove.X,
    "Released input is consumed by Movement without reversing the animated character");
var Animation = Runtime.Frame.Get<AnimatorFrameState>(Animator.ComponentId);
Check(Animation?.ToJson()["code"]?.GetValue<string>() == "ANIMATOR_READY",
    "Play evaluates the skeletal Animator through its Playable-backed sampling path");
var Skeleton = Runtime.Frame.Get<SkeletonFrameState>(Mesh.ComponentId)!;
Check(Skeleton.Code == "SKELETON_READY" &&
    !Skeleton.Positions.SequenceEqual(InitialSkeleton.Positions),
    "Advancing the walk animation changes the committed skinned vertices");
var MovedHit = Runtime.Collision.Raycast(Transform.Position + new Vector3(0, 3, 0),
    -Vector3.UnitY, 10);
Check(MovedHit?.ComponentId == Collider.ComponentId &&
    Runtime.Collision.Raycast(Start + new Vector3(0, 3, 0), -Vector3.UnitY, 10) is null,
    "Trace uses the moved capsule position and no longer hits its old position");
var Bounds = new UiRect(0, 0, 800, 500);
var Rendered = SceneRenderer.BuildFrame(new WorldDocument(Runtime.Frame, Registry), Bounds, Assets);
Check(Rendered.MeshCount == 3 && Rendered.TriangleCount > 9000 && Rendered.Diagnostics.Count == 0,
    "Scene renders mannequin, ground and crate without skeletal preparation diagnostics");
Collider.Enabled = false;
var WithoutCollision = SceneRenderer.BuildFrame(new WorldDocument(Runtime.Frame, Registry), Bounds, Assets);
Check(Runtime.Collision.Raycast(Transform.Position + new Vector3(0, 3, 0), -Vector3.UnitY, 10) is null &&
    WithoutCollision.Triangles.SequenceEqual(Rendered.Triangles),
    "Disabling the capsule removes its Trace hit without changing rendered geometry");
Collider.Enabled = true;
var RuntimeText = Runtime.Snapshot().Serialize();
Check(RuntimeText.Contains("player-controller", StringComparison.Ordinal) &&
    !RuntimeText.Contains("CONTROLLER_READY", StringComparison.Ordinal) &&
    !RuntimeText.Contains("localPose", StringComparison.Ordinal) &&
    !RuntimeText.Contains("skinMatrices", StringComparison.Ordinal),
    "Runtime snapshot retains component configuration without derived animation or frame status");
Check(File.ReadAllText(WorldPath) == SourceText &&
    File.ReadAllText(PrefabPath) == PrefabSourceText && World.Serialize() == AuthoringText,
    "Play animation and movement preserve source Prefab, World and authoring data");

var Controller = Player.GetComponent<PlayerControllerComponent>()!;
var Camera = Controller.ViewCamera!.Entity.GetComponent<ThirdPersonCameraComponent>()!;
Check(!Controller.FaceMoveDirection && Runtime.Frame.Snapshot().ActiveCameraId == Camera.Entity.EntityId,
    "Player selects its third person view and uses the camera-facing strafe policy");
var InitialScale = Transform.Scale;
Input.ProcessNative("main", NativeInputEventKind.PointerMove, 0, 100, 30, 100, 30, 0, false, true);
Runtime.Tick(0.1f);
Check(Math.Abs(Camera.Yaw + 15) < 0.001f && Math.Abs(Camera.Pitch + 19.5f) < 0.001f &&
    Math.Abs(Transform.Rotation.Y - Camera.Yaw) < 0.001f,
    "Look consumes mouse delta once and synchronizes character yaw without tilting the body");
var BeforeStrafe = Transform.Position;
Input.ProcessNative("main", NativeInputEventKind.KeyDown, 'D', 0, 0, 0, 0, 0, false, true);
Runtime.Tick(0.2f);
var Strafe = Transform.Position - BeforeStrafe;
var Facing = Vector3.Transform(-Vector3.UnitZ,
    Quaternion.CreateFromAxisAngle(Vector3.UnitY, Camera.Yaw * MathF.PI / 180));
Check(Strafe.Length() > 0.1f && Math.Abs(Vector3.Dot(Strafe, Facing)) < 0.001f &&
    Math.Abs(Transform.Rotation.Y - Camera.Yaw) < 0.001f,
    "D strafes perpendicular to the camera direction without turning the character");
var ModelForward = Vector3.Normalize(Vector3.TransformNormal(-Vector3.UnitZ,
    EntityTransformMath.WorldMatrix(Mesh.Entity)));
Check(Vector3.Dot(ModelForward, Vector3.Normalize(Strafe)) > 0.999f && Controller.FacingMesh == Mesh,
    "the independent Mesh turns toward A/D movement while the root keeps camera yaw");
Input.ProcessNative("main", NativeInputEventKind.GameplayCaptureLost, 0, 0, 0, 0, 0, 0, false, false);
Runtime.Tick(0.5f);
var ReleasedPosition = Transform.Position;
Runtime.Tick(0.2f);
Check(Transform.Position == ReleasedPosition, "capture loss cancels held move input");
Input.ProcessNative("other-host", NativeInputEventKind.KeyDown, 'W', 0, 0, 0, 0, 0, false, true);
Runtime.Tick(0.2f);
Check(Transform.Position == ReleasedPosition, "a Player ignores events from another host");
Input.ProcessNative("other-host", NativeInputEventKind.KeyUp, 'W', 0, 0, 0, 0, 0, false, true);
Input.ProcessNative("main", NativeInputEventKind.KeyDown, 'W', 0, 0, 0, 0, 0, true, true);
Runtime.Tick(0.2f);
Check(Transform.Position == ReleasedPosition, "UI-consumed move keys never reach Player movement");
Input.ProcessNative("main", NativeInputEventKind.KeyUp, 'W', 0, 0, 0, 0, 0, false, true);
Player.GetComponent<ControllableComponent>()!.ControlEnabled = false;
Runtime.Tick(0.1f);
Check(!Runtime.Frame.Get<ControllerFrameState>(Controller.ComponentId)!.ToJson()["input"]!["bound"]!.GetValue<bool>(),
    "disabling control releases the Player input registrations");
Player.GetComponent<ControllableComponent>()!.ControlEnabled = true;
Runtime.Tick(0.1f);
Check(Transform.Position == ReleasedPosition && Transform.Scale == InitialScale,
    "control recovery clears input and repeated view yaw updates preserve character scale");
var CameraType = Registry.Get(typeof(ThirdPersonCameraComponent));
Check(CameraType.SchemaVersion == 1 && CameraType.CreateDefaultState(Registry).Count == 6 &&
    Registry.Get(typeof(PlayerControllerComponent)).SchemaVersion == 2,
    "camera and Player schemas expose explicit configuration defaults");
var Rebuilt = SceneData.Parse(Runtime.Snapshot().Serialize(), Registry);
Check(Rebuilt.Serialize() == Runtime.Snapshot().Serialize(), "third person authoritative state round trips deterministically");
var Legacy = World.Clone();
var LegacyController = Legacy.Entities.SelectMany(Entity => Entity.Components).Single(Component =>
    Component.TypeId == "engine.player-controller");
LegacyController.SchemaVersion = 1;
LegacyController.State = new JsonObject
{
    ["ControlledEntity"] = "player",
    ["Priority"] = 0,
};
using (var Migrated = new RuntimeWorld(Legacy, Registry, Assets))
{
    var MigratedController = Migrated.FindEntity("player")!.GetComponent<PlayerControllerComponent>()!;
    Check(MigratedController.InputContext.Id == "engine://input/contexts/player" &&
        MigratedController.LookAction.Id is null && MigratedController.ViewCamera is null,
        "v1 Player migrates explicitly without inventing a camera reference");
}

Console.WriteLine($"[PrefabMovementValidation] PASS: {Assertions} assertions; skeletal Prefab, Play movement, animation and non-rendering Trace collision");
