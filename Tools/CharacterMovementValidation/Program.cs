using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Text.Json.Nodes;
using SharpEngine.Animation;
using SharpEngine.Assets;
using SharpEngine.CharacterMovement;
using CharacterMovementValidation;
using SharpEngine.Components;

var Root = Path.GetFullPath(args.Length > 0 ? args[0] : FindEngineRoot());
var Assertions = 0;

void Check(bool Condition, string Message)
{
    Assertions++;
    if (!Condition)
    {
        throw new InvalidOperationException("FAILED: " + Message);
    }
    Console.WriteLine("PASS " + Message);
}

void Reject(Action Action, string Message)
{
    Assertions++;
    try
    {
        Action();
    }
    catch (Exception Error) when (Error is InvalidDataException or InvalidOperationException or
        ComponentValidationException or ArgumentException)
    {
        Console.WriteLine("PASS " + Message);
        return;
    }
    throw new InvalidOperationException("FAILED (accepted invalid input): " + Message);
}

// Registration: the plugin catalog registers the whole movement set under stable engine type ids.
var Registry = ComponentRegistry.CreateBuiltin();
var PluginAssembly = typeof(CharacterMovementComponent).Assembly;
Registry.RegisterAssembly(PluginAssembly, "SharpEngine.CharacterMovement.Runtime", "validation");
Registry.RegisterAssembly(typeof(ProbeMovementAuthorityComponent).Assembly, "validation", "probe");
Registry.ValidateRegistrations();
NavigationAuthorityValidation.Run(Registry, Check);
Check(PluginAssembly.GetCustomAttribute<SharpEngine.Modules.SharpEngineModuleAttribute>() is
    { Type: "Runtime" },
    "the runtime plugin declares a Runtime module identity");
foreach (var TypeId in new[]
{
    CharacterMovementIds.CharacterMovement, CharacterMovementIds.WalkingMode,
    CharacterMovementIds.FallingMode, CharacterMovementIds.JumpModifier,
    CharacterMovementIds.SprintModifier, CharacterMovementIds.SpeedLimitModifier,
})
{
    var Descriptor = Registry.Find(TypeId);
    Check(Descriptor is not null && Descriptor.SchemaVersion == 1 &&
        Descriptor.CreateDefaultState(Registry).Count > 0,
        "component " + TypeId + " registers schema v1 with complete defaults");
}
var CharacterDescriptor = Registry.Get(typeof(CharacterMovementComponent));
Check(CharacterDescriptor.Fields.Any(Field => Field.Name == "GroundModeId") &&
    CharacterDescriptor.Fields.Any(Field => Field.Name == "AirModeId") &&
    CharacterDescriptor.Fields.All(Field => Field.Name != "SprintEnabled"),
    "authoritative state carries mode ids and never carries session-only flags");
Check(CharacterMovementModes.ModeIdOf(typeof(WalkingMovementModeComponent)) == "walking" &&
    CharacterMovementModes.ModeIdOf(typeof(FallingMovementModeComponent)) == "falling",
    "mode identity is declared once as metadata on the component type");

// A fixture asset root gives the animator a root-motion clip without touching engine content.
var Fixture = Directory.CreateTempSubdirectory("SharpEngine-CharacterMovement-");
AssetRegistry Assets;
string MotionId;
try
{
    (Assets, MotionId) = BuildAssetFixture(Root, Fixture.FullName);
    using var Movement = new MovementFixture(Registry, Assets, MotionId);
    var Transform = Movement.Transform;
    var Character = Movement.Character;
    // Isolate intent locomotion first; root motion is exercised on its own below.
    Character.ApplyRootMotion = false;
    Movement.Runtime.Tick(0.1f);
    var Start = Transform.Position;

    // Walking: intent accelerates toward the mode speed, braking settles the character.
    Movement.Controller.SetMoveInput(new Vector2(0, 1));
    Movement.Runtime.Tick(0.2f);
    var Walked = Transform.Position - Start;
    Check(Walked.Z > 0.2f && Math.Abs(Walked.X) < 0.001f,
        "walking integrates planar intent through the movement authority: " + Walked);
    Movement.Controller.SetMoveInput(Vector2.Zero);
    Movement.Runtime.Tick(0.2f);
    Movement.Runtime.Tick(0.2f);
    var Settled = Transform.Position;
    Movement.Runtime.Tick(0.2f);
    Check(Transform.Position == Settled, "braking settles the character instead of drifting");

    // Dead zone.
    Character.InputDeadZone = 0.5f;
    Movement.Controller.SetMoveInput(new Vector2(0.2f, 0));
    var BeforeDeadZone = Transform.Position;
    Movement.Runtime.Tick(0.2f);
    Check((Transform.Position - BeforeDeadZone).Length() < 0.001f,
        "intent below the configured dead zone produces no locomotion");
    Character.InputDeadZone = 0.1f;
    Movement.Controller.SetMoveInput(Vector2.Zero);
    Movement.Runtime.Tick(0.2f);

    // Root motion: the plugin consumes the delta and the engine reports the hand-off exactly once.
    Character.ApplyRootMotion = true;
    var RootBefore = Transform.Position;
    Movement.Runtime.Tick(0.5f);
    var RootFrame = Movement.Runtime.Frame.Get<AnimatorFrameState>(Movement.Animator.ComponentId)!
        .ToJson()["rootMotion"]!.AsObject();
    Check(RootFrame["code"]!.GetValue<string>() == "ROOT_MOTION_CONSUMED" &&
        RootFrame["consumedBy"]!.GetValue<string>()!.Contains("CharacterMovementComponent",
            StringComparison.Ordinal),
        "the animator names the plugin authority that consumed root motion");
    Check(Transform.Position.Z < RootBefore.Z - 0.5f,
        "root motion advances the character without input: " + (Transform.Position - RootBefore));
    Check(Character.LinearVelocity.Length() > 0,
        "the authority publishes the linear velocity derived from the same displacement");

    // Root motion scale is authoritative.
    Character.RootMotionScale = 0;
    var BeforeScale = Transform.Position;
    Movement.Runtime.Tick(0.5f);
    Check(Math.Abs((Transform.Position - BeforeScale).Z) < 0.05f,
        "root motion scale 0 stops root-driven advance");
    Character.RootMotionScale = 1;

    // Jump: one queued request becomes an arc; the ground probe re-grounds a resolved position.
    var Jump = Movement.Entity.GetComponents<JumpMovementModifierComponent>().Single();
    Jump.RequestJump();
    Movement.Runtime.Tick(0.1f);
    Check(!Jump.HasPendingJump, "the jump request is consumed exactly once");
    Check(Transform.Position.Y > 0.05f, "the jump imparts upward displacement: " + Transform.Position.Y);
    Movement.Runtime.Tick(0.6f);
    Check(!Character.IsGrounded && Character.LinearVelocity.Y < 0,
        "gravity takes over while airborne: " + Character.LinearVelocity.Y);
    // The engine does not resolve penetration; an external mover restores the ground contact.
    Transform.Position = new(Transform.Position.X, 0, Transform.Position.Z);
    Movement.Runtime.Tick(0.1f);
    Check(Character.IsGrounded, "the ground probe re-grounds the character once it is back on the floor");

    // Modifier composition: sprint scales the mode speed, the speed limit caps the result.
    Character.ApplyRootMotion = false;
    var Sprint = Movement.Entity.GetComponents<SprintMovementModifierComponent>().Single();
    var Limit = Movement.Entity.GetComponents<CharacterSpeedLimitModifierComponent>().Single();
    Limit.MaxPlanarSpeed = 12;
    Movement.Controller.SetMoveInput(new Vector2(1, 0));
    Movement.Runtime.Tick(0.5f);
    var SprintSpeed = Character.LinearVelocity.Length();
    Sprint.SetSprinting(false);
    Movement.Runtime.Tick(0.5f);
    var NormalSpeed = Character.LinearVelocity.Length();
    Check(SprintSpeed > NormalSpeed + 0.01f,
        "sprint raises the mode speed before the cap: " + SprintSpeed + " vs " + NormalSpeed);
    Limit.MaxPlanarSpeed = 1.2f;
    Movement.Runtime.Tick(0.5f);
    Check(Character.LinearVelocity.Length() <= 1.25f,
        "the speed limit caps the composed result: " + Character.LinearVelocity.Length());
    Limit.MaxPlanarSpeed = 6;
    Movement.Controller.SetMoveInput(Vector2.Zero);
    Movement.Runtime.Tick(0.4f);

    // Coexistence: engine.movement on the same entity reports override and never double-applies.
    var Legacy = Movement.Entity.AddComponent<MovementComponent>().Value;
    Movement.Controller.SetMoveInput(new Vector2(0, 1));
    var BeforeLegacy = Transform.Position;
    Movement.Runtime.Tick(0.2f);
    var LegacyFrame = Movement.Runtime.Frame.Get<MovementFrameState>(Legacy.ComponentId)!.ToJson();
    Check(LegacyFrame["code"]!.GetValue<string>() == "MOVEMENT_OVERRIDDEN" &&
        (Transform.Position - BeforeLegacy).Z > 0.05f,
        "engine.movement reports MOVEMENT_OVERRIDDEN while the authority still moves the character");
    Movement.Controller.SetMoveInput(Vector2.Zero);
    Movement.Runtime.Tick(0.3f);

    // Two different authorities on one entity are a reported conflict rather than a race.
    var Probe = Movement.Entity.AddComponent<ProbeMovementAuthorityComponent>().Value;
    Movement.Runtime.Tick(0.1f);
    Check(Movement.Runtime.Diagnostics.Any(Diagnostic =>
        Diagnostic.Code == "MOVEMENT_AUTHORITY_CONFLICT"),
        "a second movement authority on one entity reports MOVEMENT_AUTHORITY_CONFLICT");
    _ = Movement.Runtime.RemoveComponent(Movement.Entity.EntityId, Probe.ComponentId).Value;
    Movement.Runtime.Tick(0.1f);

    // A disabled mode is a published state, not a silent no-op.
    var Walking = Movement.Entity.GetComponents<WalkingMovementModeComponent>().Single();
    Walking.Enabled = false;
    Movement.Controller.SetMoveInput(new Vector2(0, 1));
    Movement.Runtime.Tick(0.1f);
    var Unavailable = Movement.Runtime.Frame.Get<CharacterMovementFrameState>(Character.ComponentId)!
        .ToJson();
    Check(Unavailable["code"]!.GetValue<string>() == "CHARACTER_MOVEMENT_MODE_UNAVAILABLE" &&
        Unavailable["message"]!.GetValue<string>()!.Contains("walking", StringComparison.Ordinal),
        "a disabled mode publishes CHARACTER_MOVEMENT_MODE_UNAVAILABLE with the missing id");
    Walking.Enabled = true;
    Movement.Controller.SetMoveInput(Vector2.Zero);
    Movement.Runtime.Tick(0.2f);

    // Authoritative state round trips; invalid configuration is rejected with diagnostics.
    var Serialized = Movement.Runtime.Snapshot().Serialize();
    Check(SceneData.Parse(Serialized, Registry).Serialize() == Serialized,
        "character movement state round trips deterministically");
    Reject(() => Registry.ValidateState(CharacterDescriptor, CharacterState(DeadZone: 2)),
        "an out-of-range dead zone is rejected with a component diagnostic");
    Reject(() => Registry.ValidateState(CharacterDescriptor, CharacterState(GroundModeId: "")),
        "an empty mode id is rejected instead of silently defaulting");
    var Rebuilt = Registry.Find(CharacterMovementIds.CharacterMovement)!.CreateDefaultState(Registry);
    Check(Rebuilt["RootMotionScale"]!.GetValue<float>() == 1 &&
        Rebuilt["ApplyRootMotion"]!.GetValue<bool>(),
        "defaults enable root motion consumption so a new character needs no setup");
}
finally
{
    Fixture.Delete(true);
}

Console.WriteLine($"[CharacterMovementValidation] PASS: {Assertions} assertions; " +
    "plugin composition, modes, modifiers, root motion consumption, and engine coexistence");

static JsonObject CharacterState(float DeadZone = 0.1f, string GroundModeId = "walking")
{
    return new JsonObject
    {
        ["InputDeadZone"] = DeadZone,
        ["GroundProbeDistance"] = 0.3,
        ["GroundMinNormalY"] = 0.5,
        ["GroundLayerMask"] = -1,
        ["GroundModeId"] = GroundModeId,
        ["AirModeId"] = "falling",
        ["ApplyRootMotion"] = true,
        ["RootMotionScale"] = 1,
        ["ApplyRootMotionRotation"] = true,
        ["RootMotionRequiresIntent"] = false,
    };
}

static (AssetRegistry Assets, string MotionId) BuildAssetFixture(string Root, string Fixture)
{
    File.WriteAllText(Path.Combine(Fixture, "engine.json"), "{}");
    var Content = Directory.CreateDirectory(Path.Combine(Fixture, "Content/Assets"));
    var Source = SkeletalAnimationAsset.Parse(File.ReadAllText(Path.Combine(Root,
        "Content/Assets/Animations/RobotWalk.skeletalanimation.json")));
    var Tracks = Source.Tracks.Select(Track => Track.BoneId == "root"
        ? new BoneTrack(Track.TrackId, Track.BoneId,
        [
            new(0, BoneTransform.Identity),
            new(1, new(new(0, 0, -2), Quaternion.Identity, Vector3.One)),
        ]) : Track).ToArray();
    var Motion = new SkeletalAnimationAsset(Source.AssetId, Source.Name, 1, 30, Source.Bones, Tracks,
        Source.SkeletonId, null, null, Source.Notifies, new AnimationRootMotionSettings(true, "root"));
    var Manifest = new JsonObject
    {
        ["schemaVersion"] = 2,
        ["assets"] = new JsonArray
        {
            Entry(Motion.AssetId[9..], "SkeletalAnimation", "clip.json"),
            Entry("meshes/robot-skeletal", "SkeletalMesh", "mesh.json"),
            Entry("skeletons/robot", "Skeleton", "skeleton.json"),
        },
    };
    File.WriteAllText(Path.Combine(Content.FullName, "assets.json"), Manifest.ToJsonString());
    File.WriteAllText(Path.Combine(Content.FullName, "clip.json"), Motion.Serialize(false));
    var Mesh = JsonNode.Parse(File.ReadAllText(Path.Combine(Root,
        "Content/Assets/Meshes/Robot.skeletalmesh.json")))!.AsObject();
    Mesh.Remove("sourcePath");
    Mesh.Remove("sourceSha256");
    File.WriteAllText(Path.Combine(Content.FullName, "mesh.json"), Mesh.ToJsonString());
    File.Copy(Path.Combine(Root, "Content/Assets/Skeletons/Robot.skeleton.json"),
        Path.Combine(Content.FullName, "skeleton.json"));
    return (AssetRegistry.Load(Fixture), Motion.AssetId);
}

static JsonObject Entry(string Id, string Type, string FileName)
{
    return new JsonObject
    {
        ["id"] = Id,
        ["type"] = Type,
        ["name"] = Id,
        ["path"] = "Assets/" + FileName,
    };
}

static string FindEngineRoot()
{
    var Configured = Environment.GetEnvironmentVariable("SHARPENGINE_ROOT");
    if (!string.IsNullOrWhiteSpace(Configured) && Directory.Exists(Configured))
    {
        return Configured!;
    }
    for (var Directory = new DirectoryInfo(AppContext.BaseDirectory); Directory is not null;
        Directory = Directory.Parent)
    {
        if (File.Exists(Path.Combine(Directory.FullName, "engine.json")))
        {
            return Directory.FullName;
        }
    }
    throw new InvalidOperationException("Pass the SharpEngine root as the first argument");
}
