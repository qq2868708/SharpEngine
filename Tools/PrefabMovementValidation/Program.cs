using System.Numerics;
using SharpEngine.Animation;
using SharpEngine.Assets;
using SharpEngine.Components;
using SharpEngine.Hosting;
using SharpEngine.Input;
using SharpEngine.UI;
using SharpEngine.World;

var DefaultRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
    "..", "..", "..", "..", ".."));
var EngineRoot = Path.GetFullPath(args.Length > 0 ? args[0] : DefaultRoot);
var ProjectRoot = Path.GetFullPath(args.Length > 1 ? args[1] : EngineRoot);
var ProjectManifestPath = Directory.GetFiles(ProjectRoot, "*.sharpproject",
    SearchOption.TopDirectoryOnly).SingleOrDefault();
if (ProjectManifestPath is null)
{
    throw new InvalidDataException($"Project manifest not found under '{ProjectRoot}'");
}
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

Check(Prefab.IsPrefab && Prefab.Entities.Count == 1,
    "Animated character asset is a single-root Prefab");
var PrefabRoot = Prefab.Entities.Single();
var PrefabSkeletalMesh = PrefabRoot.Components.SingleOrDefault(Component =>
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
Check(ScenePlayer.Components.Any(Component => Component.TypeId == "engine.skeletal-mesh" &&
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
var CopiedMesh = CopiedRoot.Components.Single(Component => Component.TypeId == "engine.skeletal-mesh");
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
using var Runtime = new RuntimeWorld(World, Registry, Assets);
var Input = InputRuntimeService.Load(Assets);
Input.SetRouteEnabled(InputRoute.Gameplay, true);
using var Bridge = PlayerControllerInputBridge.TryCreate(Runtime, Input) ??
    throw new InvalidOperationException("The sample Gameplay input bridge could not be created");
var Player = Runtime.FindEntity("player") ?? throw new InvalidOperationException("Main scene player missing");
var Transform = Player.GetComponent<TransformComponent>() ??
    throw new InvalidOperationException("Animated player Transform missing");
var Mesh = Player.GetComponent<SkeletalMeshComponent>()!;
var Animator = Player.GetComponent<AnimatorComponent>()!;
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

Console.WriteLine($"[PrefabMovementValidation] PASS: {Assertions} assertions; skeletal Prefab, Play movement, animation and non-rendering Trace collision");
