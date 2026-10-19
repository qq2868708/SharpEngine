using System.Numerics;
using System.Text.Json.Nodes;
using SharpEngine.Components;
using SharpEngine.Editor;
using SharpEngine.Spatial;
using SharpEngine.World;

var Count = 0;
void Check(bool Condition, string Message)
{
    if (!Condition)
    {
        throw new Exception(Message);
    }
    Count++;
}
void Reject(Action Action, string Message, string? Code = null)
{
    try
    {
        Action();
    }
    catch (Exception Error)
    {
        Check(Code is null || Error is ComponentValidationException Validation && Validation.Code == Code, Message + ": diagnostic");
        return;
    }
    throw new Exception("Expected rejection: " + Message);
}
bool Near(float First, float Second) { return MathF.Abs(First - Second) < 0.001f; }
var Registry = ComponentRegistry.CreateBuiltin();
Registry.RegisterAssembly(typeof(FixedObstacle).Assembly, "spatial-validation", "1");
var TypeIds = new[]
{
    "engine.box-collider",
    "engine.sphere-collider",
    "engine.capsule-collider",
    "engine.nav-surface",
    "engine.nav-agent",
};
var Data = new SceneData();
var Root = Data.CreateEntity(Registry, "Spatial root");
foreach (var TypeId in TypeIds)
{
    var Owner = Data.CreateEntity(Registry, TypeId, Root.EntityId);
    var Component = Data.AddComponent(Registry, Owner.EntityId, TypeId).Last();
    var Descriptor = Registry.Find(TypeId)!;
    Check(Descriptor.SchemaVersion == 1 && Descriptor.Schema(Registry)["$schema"]!.GetValue<string>().Contains("2020-12"), TypeId + " schema discovery");
    Check(Component.State.Count == Descriptor.Fields.Count && Descriptor.Fields.All(Field => Component.State.ContainsKey(Field.Name)), TypeId + " explicit defaults");
}
var Defaults = Data.Serialize();
Check(SceneData.Parse(Defaults, Registry).Serialize() == Defaults, "default deterministic round trip");
using (var Runtime = new RuntimeWorld(Data, Registry))
{
    Check(Runtime.Snapshot().Serialize() == Defaults, "default authoring-to-runtime reconstruction");
}
var Box = Data.Entities.SelectMany(Entity => Entity.Components).Single(Component => Component.TypeId == TypeIds[0]);
Box.State["Center"] = new JsonArray(1f, 2f, 3f);
Box.State["Size"] = new JsonArray(2f, 3f, 4f);
Box.State["Layer"] = 31;
Box.State["IsTrigger"] = true;
Box.State["BlocksNavigation"] = false;
var Sphere = Data.Entities.SelectMany(Entity => Entity.Components).Single(Component => Component.TypeId == TypeIds[1]);
Sphere.State["Radius"] = 1.2f;
var Capsule = Data.Entities.SelectMany(Entity => Entity.Components).Single(Component => Component.TypeId == TypeIds[2]);
Capsule.State["Radius"] = 0.7f;
Capsule.State["Height"] = 3.1f;
var Surface = Data.Entities.SelectMany(Entity => Entity.Components).Single(Component => Component.TypeId == TypeIds[3]);
Surface.State["Size"] = new JsonArray(16f, 24f);
Surface.State["CellSize"] = 0.5f;
Surface.State["ObstacleMask"] = 8u;
var Agent = Data.Entities.SelectMany(Entity => Entity.Components).Single(Component => Component.TypeId == TypeIds[4]);
Agent.State["Surface"] = Surface.ComponentId;
Agent.State["Radius"] = 0.4f;
Agent.State["Height"] = 2f;
Agent.State["Speed"] = 5f;
Agent.State["StoppingDistance"] = 0.2f;
Agent.State["Destination"] = new JsonArray(4f, 0f, 3f);
Agent.State["HasDestination"] = true;
Data.Validate(Registry, cook: true);
var NonDefaults = Data.Serialize();
Check(SceneData.Parse(NonDefaults, Registry).Serialize() == NonDefaults && Data.Serialize() == NonDefaults, "non-default deterministic round trip and Cook prevalidation");
using (var Runtime = new RuntimeWorld(Data, Registry))
{
    Check(Runtime.Snapshot().Serialize() == NonDefaults, "non-default runtime reconstruction preserves fields and stable IDs");
    Check(((NavAgentComponent)Runtime.FindComponent(Agent.ComponentId)!).Surface!.ComponentId == Surface.ComponentId, "typed navigation reference resolves");
    var Copy = Runtime.Instantiate(Data.ExportPrefab(Registry, Root.EntityId)).Value;
    var CopyIds = Runtime.Snapshot().Subtree(Copy.EntityId).SelectMany(Entity => Entity.Components).ToArray();
    Check(CopyIds.Single(Component => Component.TypeId == TypeIds[4]).State["Surface"]!.GetValue<string>() ==
        CopyIds.Single(Component => Component.TypeId == TypeIds[3]).ComponentId, "Prefab remaps navigation references");
}
foreach (var TypeId in TypeIds)
{
    var Future = Data.Clone();
    Future.Entities.SelectMany(Entity => Entity.Components).Single(Component => Component.TypeId == TypeId).SchemaVersion = 2;
    Reject(() => Future.Validate(Registry), TypeId + " rejects future schema");
    var Old = Data.Clone();
    Old.Entities.SelectMany(Entity => Entity.Components).Single(Component => Component.TypeId == TypeId).SchemaVersion = 0;
    Reject(() => Old.Validate(Registry), TypeId + " has no pre-v1 migration");
}
void Invalid(string TypeId, string Field, JsonNode Value, string Code)
{
    var Descriptor = Registry.Find(TypeId)!;
    var State = Descriptor.CreateDefaultState(Registry);
    State[Field] = Value;
    Reject(() => Registry.ValidateState(Descriptor, State), TypeId + "/" + Field, Code);
}
Invalid(TypeIds[0], "Size", new JsonArray(1f, 0f, 1f), "COLLIDER_SIZE");
Invalid(TypeIds[0], "Layer", JsonValue.Create(32)!, "FIELD_RANGE");
Invalid(TypeIds[1], "Radius", JsonValue.Create(-1f)!, "COLLIDER_RADIUS");
Invalid(TypeIds[2], "Height", JsonValue.Create(0.5f)!, "COLLIDER_HEIGHT");
Invalid(TypeIds[3], "CellSize", JsonValue.Create(0f)!, "NAV_GRID_SIZE");
Invalid(TypeIds[3], "CellSize", JsonValue.Create(0.001f)!, "NAV_GRID_BUDGET");
Invalid(TypeIds[4], "Height", JsonValue.Create(0.1f)!, "NAV_AGENT_SHAPE");
Invalid(TypeIds[4], "Speed", JsonValue.Create(-1f)!, "NAV_AGENT_MOVEMENT");
Invalid(TypeIds[4], "StoppingDistance", JsonValue.Create(-1f)!, "NAV_AGENT_MOVEMENT");
var InvalidReference = Data.Clone();
InvalidReference.Entities.SelectMany(Entity => Entity.Components).Single(Component => Component.ComponentId == Agent.ComponentId).State["Surface"] = Box.ComponentId;
Reject(() => InvalidReference.Validate(Registry, cook: true), "Cook rejects wrong typed surface reference");

var Document = new WorldDocument(Data.Clone(), Registry);
var Authoring = new WorldAuthoringSession(Document);
var BoxOwner = Data.Entities.Single(Entity => Entity.Components.Contains(Box));
var Before = Authoring.DocumentRevision;
var Edit = Authoring.PatchComponent(BoxOwner.EntityId, Box.ComponentId, new()
{
    ["Size"] = new JsonArray(7f, 3f, 4f),
}, Before);
Check(Edit.Success && Edit.Transaction is not null && Authoring.DocumentRevision > Before && Authoring.IsDirty, "shape edit joins authoring transaction");
Check(!Authoring.PatchComponent(BoxOwner.EntityId, Box.ComponentId, new()
{
    ["Size"] = new JsonArray(0f, 3f, 4f),
}, Authoring.DocumentRevision).Success, "invalid shape patch rejected atomically");
Check(!Authoring.PatchComponent(BoxOwner.EntityId, Box.ComponentId, new()
{
    ["Layer"] = 2,
}, Before).Success, "stale shape patch rejected");
Check(Authoring.Undo(Authoring.DocumentRevision).Success && !Authoring.IsDirty && Document.Serialize() == NonDefaults, "shape Undo restores savepoint");
Check(Authoring.Redo(Authoring.DocumentRevision).Success && Authoring.IsDirty, "shape Redo restores committed edit");
var Output = Path.GetFullPath("Saved/Validation/Spatial");
Directory.CreateDirectory(Output);
Authoring.Save(Path.Combine(Output, "shapes.sworld"));
Check(!Authoring.IsDirty && WorldDocument.Load(Path.Combine(Output, "shapes.sworld"), Registry).Serialize() == Document.Serialize(), "explicit Save preserves all shape/Nav state");
foreach (var TypeId in TypeIds)
{
    var Session = new WorldAuthoringSession(new WorldDocument(Data.Clone(), Registry));
    var Owner = Session.Document.Data.Entities.Single(Entity => Entity.Components.Any(Component => Component.TypeId == TypeId));
    var Component = Owner.Components.Single(Component => Component.TypeId == TypeId);
    var Field = TypeId == "engine.nav-agent" ? "Speed" : TypeId == "engine.nav-surface" ? "CellSize" : "Layer";
    JsonNode Value = Field == "Layer" ? JsonValue.Create(2)! : JsonValue.Create(2f)!;
    var Original = Session.Document.Serialize();
    Check(Session.PatchComponent(Owner.EntityId, Component.ComponentId, new()
    {
        [Field] = Value,
    }, Session.DocumentRevision).Success && Session.IsDirty, TypeId + " atomic tracked patch");
    Check(Session.Undo(Session.DocumentRevision).Success && Session.Document.Serialize() == Original, TypeId + " Undo fidelity");
    Check(Session.Redo(Session.DocumentRevision).Success && Session.Document.Serialize() != Original, TypeId + " Redo fidelity");
}

foreach (var TypeId in TypeIds.Take(3))
{
    var Scene = new SceneData();
    var Owner = Scene.CreateEntity(Registry);
    var Shape = Scene.AddComponent(Registry, Owner.EntityId, TypeId).Last();
    using var Runtime = new RuntimeWorld(Scene, Registry);
    var Hit = Runtime.Collision.Raycast(new(0, 0, -5), Vector3.UnitZ * 7, 10);
    Check(Hit?.ComponentId == Shape.ComponentId && Near(Hit.Distance, 4.5f) && Hit.Normal.Z < -0.99f, TypeId + " world-distance ray hit and normal");
    Check(Runtime.Collision.Raycast(Vector3.Zero, Vector3.UnitZ, 0)?.Distance == 0, TypeId + " inside ray returns zero");
    Check(Runtime.Collision.Raycast(new(0, 0, -5), Vector3.UnitZ, 4) is null, TypeId + " finite ray range");
    Check(Runtime.Collision.OverlapSphere(Vector3.Zero, 0).Count == 1, TypeId + " point containment");
    Check(Runtime.Collision.OverlapSphere(new(4, 4, 4), 0.1f).Count == 0, TypeId + " separated overlap");
    Check(Runtime.Collision.OverlapSphere(new(0.5f, 0, 0), 0).Count == 1, TypeId + " tangent point contact");
    if (TypeId == "engine.capsule-collider")
    {
        Check(Near(Runtime.Collision.Raycast(new(0, 5, 0), -Vector3.UnitY, 10)!.Distance, 4), "capsule top hemisphere");
        Check(Near(Runtime.Collision.Raycast(new(0, 0.9f, -5), Vector3.UnitZ, 10)!.Distance, 4.7f), "capsule oblique cap slice");
    }
    var Snapshot = Runtime.Collision;
    var Transform = Runtime.FindEntity(Owner.EntityId)!.GetComponent<TransformComponent>()!;
    Runtime.Patch(Transform.ComponentId, new()
    {
        ["Scale"] = new JsonArray(2f, 1f, 0.5f),
    }, Runtime.Revision);
    Check(Near(Runtime.Collision.Raycast(new(0, 0, -5), Vector3.UnitZ, 10)!.Distance, 4.75f), TypeId + " nonuniform scale ray");
    Check(Near(Snapshot.Raycast(new(0, 0, -5), Vector3.UnitZ, 10)!.Distance, 4.5f) && Snapshot.Revision < Runtime.Revision, "collision snapshot retains its committed revision");
    Runtime.Patch(Shape.ComponentId, new()
    {
        ["Layer"] = 31,
        ["IsTrigger"] = true,
    }, Runtime.Revision);
    Check(Runtime.Collision.Raycast(new(0, 0, -5), Vector3.UnitZ, 10) is null, "trigger excluded by default");
    Check(Runtime.Collision.Raycast(new(0, 0, -5), Vector3.UnitZ, 10, 1u << 31, true) is not null &&
        Runtime.Collision.Raycast(new(0, 0, -5), Vector3.UnitZ, 10, 1, true) is null, "layer 31 and trigger filtering");
    Check(Runtime.Collision.OverlapSphere(Vector3.Zero, 1, uint.MaxValue, true, Owner.EntityId).Count == 0, "self entity exclusion");
    Runtime.FindComponent(Shape.ComponentId)!.Enabled = false;
    Check(Runtime.Collision.Colliders.Count == 0, "disabled collider removed from query");
    Runtime.FindComponent(Shape.ComponentId)!.Enabled = true;
    Runtime.FindEntity(Owner.EntityId)!.ActiveSelf = false;
    Check(Runtime.Collision.Colliders.Count == 0, "inactive collider owner removed from query");
}

// Every primitive pair is tested through the convex narrow phase.
foreach (var FirstType in TypeIds.Take(3))
{
    foreach (var SecondType in TypeIds.Take(3))
    {
        var Scene = new SceneData();
        var First = Scene.CreateEntity(Registry);
        var Second = Scene.CreateEntity(Registry);
        var FirstShape = Scene.AddComponent(Registry, First.EntityId, FirstType).Last();
        var SecondShape = Scene.AddComponent(Registry, Second.EntityId, SecondType).Last();
        Second.Components[0].State["Position"] = new JsonArray(0.6f, 0f, 0f);
        using var Runtime = new RuntimeWorld(Scene, Registry);
        Check(Runtime.Collision.OverlapShape(FirstShape.ComponentId).Single().ComponentId == SecondShape.ComponentId, FirstType + "/" + SecondType + " overlap");
        Runtime.Patch(Second.Components[0].ComponentId, new()
        {
            ["Position"] = new JsonArray(2f, 0f, 0f),
        }, Runtime.Revision);
        Check(Runtime.Collision.OverlapShape(FirstShape.ComponentId).Count == 0, FirstType + "/" + SecondType + " separation");
    }
}

// Compare narrow phase against independent analytic sphere/OBB distances.
var Random = new Random(1729);
var AnalyticScene = new SceneData();
var AnalyticOwner = AnalyticScene.CreateEntity(Registry);
AnalyticScene.AddComponent(Registry, AnalyticOwner.EntityId, "engine.box-collider");
AnalyticOwner.Components[0].State["Scale"] = new JsonArray(2f, 1f, 0.5f);
AnalyticOwner.Components[0].State["Rotation"] = new JsonArray(20f, 37f, 11f);
var Orientation = Matrix4x4.CreateFromYawPitchRoll(37 * MathF.PI / 180, 20 * MathF.PI / 180, 11 * MathF.PI / 180);
var AllAnalytic = true;
using (var Runtime = new RuntimeWorld(AnalyticScene, Registry))
{
    var Snapshot = Runtime.Collision;
    for (var Index = 0; Index < 300; Index++)
    {
        var Local = new Vector3((float)Random.NextDouble() * 4 - 2, (float)Random.NextDouble() * 4 - 2, (float)Random.NextDouble() * 4 - 2);
        var Radius = 0.05f + (float)Random.NextDouble() * 0.6f;
        var Nearest = Vector3.Clamp(Local, new(-1, -0.5f, -0.25f), new(1, 0.5f, 0.25f));
        var Expected = Vector3.Distance(Local, Nearest) <= Radius;
        AllAnalytic &= (Snapshot.OverlapSphere(Vector3.Transform(Local, Orientation), Radius).Count != 0) == Expected;
    }
    Check(AllAnalytic, "300 seeded sphere/rotated nonuniform box queries match analytic distances");
    Reject(() => Snapshot.Raycast(Vector3.Zero, Vector3.Zero, 1), "zero ray direction rejected");
    Reject(() => Snapshot.OverlapSphere(Vector3.Zero, -1), "negative overlap radius rejected");
}

(SceneData Scene, EntityRecord SurfaceOwner, ComponentRecord Surface, EntityRecord Owner, ComponentRecord Agent, EntityRecord Wall) NavigationScene()
{
    var Scene = new SceneData();
    var SurfaceOwner = Scene.CreateEntity(Registry, "Surface");
    var Surface = Scene.AddComponent(Registry, SurfaceOwner.EntityId, "engine.nav-surface", new()
    {
        ["Size"] = new JsonArray(12f, 12f),
        ["CellSize"] = 0.5f,
    }).Last();
    var Owner = Scene.CreateEntity(Registry, "Agent");
    Owner.Components[0].State["Position"] = new JsonArray(-4f, 0f, 0f);
    var Agent = Scene.AddComponent(Registry, Owner.EntityId, "engine.nav-agent", new()
    {
        ["Surface"] = Surface.ComponentId,
        ["Speed"] = 6f,
        ["Destination"] = new JsonArray(4f, 0f, 0f),
        ["HasDestination"] = true,
    }).Last();
    var Wall = Scene.CreateEntity(Registry, "Wall");
    Wall.Components[0].State["Position"] = new JsonArray(0f, 1f, 0f);
    Scene.AddComponent(Registry, Wall.EntityId, "engine.box-collider", new()
    {
        ["Size"] = new JsonArray(1f, 2f, 5f),
    });
    return (Scene, SurfaceOwner, Surface, Owner, Agent, Wall);
}
var Fixture = NavigationScene();
using (var Runtime = new RuntimeWorld(Fixture.Scene, Registry))
{
    var Snapshot = Runtime.Navigation;
    var PathResult = Snapshot.FindPath(Fixture.Surface.ComponentId, new(-4, 0, 0), new(4, 0, 0));
    Check(PathResult.Success && PathResult.Points.Count > 2 && PathResult.Points.Any(Point => MathF.Abs(Point.Z) > 2.8f), "A* detours around shape with agent clearance");
    Check(Snapshot.FindPath(Fixture.Surface.ComponentId, new(-4, 0, 0), new(4, 0, 0)).Points.SequenceEqual(PathResult.Points), "deterministic path ordering");
    Check(!Snapshot.FindPath(Fixture.Surface.ComponentId, new(-6, 0, 0), new(4, 0, 0)).Success, "agent radius keeps path inside surface bounds");
    Check(Snapshot.FindPath(Fixture.Surface.ComponentId, new(-4, 0, 0), new(4, 1, 0)).Code == "NAV_ENDPOINT_OFF_PLANE", "off-plane goal diagnosed");
    var Safe = true;
    var MaximumZ = 0f;
    for (var Index = 0; Index < 240; Index++)
    {
        Runtime.Step();
        var Observation = Runtime.Navigation.Agents.Single();
        MaximumZ = MathF.Max(MaximumZ, MathF.Abs(Observation.Position.Z));
        Safe &= Runtime.Collision.OverlapSphere(Observation.Position + Vector3.UnitY * 0.9f, 0.299f).Count == 0;
    }
    var Final = Runtime.Navigation.Agents.Single();
    Check(Safe && MaximumZ > 2.8f && Final.Status == NavAgentStatus.Arrived && Final.RemainingDistance <= 0.051f, "fixed-step agent arrives without penetrating wall");
    Check(Final.Velocity == Vector3.Zero && Final.Path.Count > 2, "navigation exposes committed path and stopped velocity");
    Check(Fixture.Owner.Components[0].State["Position"]!.ToJsonString() == "[-4,0,0]", "navigation never mutates authoring records");
    var Json = Runtime.Navigation.ToJson();
    Check(Json["schemaVersion"]!.GetValue<int>() == 1 && Json["revision"]!.GetValue<ulong>() == Runtime.Revision &&
        Json["agents"]![0]!["position"]!.AsArray().Count == 3, "runtime navigation JSON observation is versioned and revision bound");
    File.WriteAllText(Path.Combine(Output, "navigation.json"), Json.ToJsonString(new()
    {
        WriteIndented = true,
    }));
    Check(!Runtime.Snapshot().Serialize().Contains("remainingDistance"), "derived path state is not persisted");
    using var Rebuilt = new RuntimeWorld(Runtime.Snapshot(), Registry);
    Rebuilt.Step();
    Check(Rebuilt.Navigation.Agents.Single().Status == NavAgentStatus.Arrived, "runtime path reconstructs from serialized goal and transform");
    Runtime.FindEntity(Fixture.SurfaceOwner.EntityId)!.ActiveSelf = false;
    Runtime.Step();
    Check(Runtime.Navigation.Agents.Single().Code == "NAV_SURFACE_UNAVAILABLE", "inactive surface diagnosed");
    Runtime.FindEntity(Fixture.SurfaceOwner.EntityId)!.ActiveSelf = true;
    Runtime.DestroyEntity(Fixture.SurfaceOwner.EntityId);
    Runtime.Step();
    Check(((NavAgentComponent)Runtime.FindComponent(Fixture.Agent.ComponentId)!).Surface is null &&
        Runtime.Navigation.Agents.Single().Code == "NAV_SURFACE_UNAVAILABLE", "deleted surface reference nullifies and cannot keep using cached path");
}

var Dynamic = NavigationScene();
using (var Runtime = new RuntimeWorld(Dynamic.Scene, Registry))
{
    Runtime.Step();
    var Position = Runtime.Navigation.Agents.Single().Position;
    var WallShape = Dynamic.Wall.Components.Last();
    Runtime.Patch(WallShape.ComponentId, new()
    {
        ["Size"] = new JsonArray(1f, 2f, 12f),
    }, Runtime.Revision);
    Runtime.Step();
    Check(Runtime.Navigation.Agents.Single().Status == NavAgentStatus.Blocked && Runtime.Navigation.Agents.Single().Position == Position,
        "changed obstacle invalidates path and stops unreachable agent");
    Runtime.Patch(WallShape.ComponentId, new()
    {
        ["IsTrigger"] = true,
    }, Runtime.Revision);
    Runtime.Step();
    Check(Runtime.Navigation.Agents.Single().Status == NavAgentStatus.Moving && Runtime.Navigation.Agents.Single().Position.X > Position.X,
        "triggers do not block navigation and live obstacle edits resume movement");
    Runtime.Patch(Dynamic.Agent.ComponentId, new()
    {
        ["HasDestination"] = false,
    }, Runtime.Revision);
    Check(Runtime.Navigation.Agents.Single().Status == NavAgentStatus.Idle && Runtime.Navigation.Agents.Single().Velocity == Vector3.Zero,
        "goal cancellation observed at committed patch revision");
}

var Phased = NavigationScene();
Phased.Scene.AddComponent(Registry, Phased.Wall.EntityId, Registry.Get(typeof(FixedObstacle)).TypeId);
using (var Runtime = new RuntimeWorld(Phased.Scene, Registry))
{
    Runtime.Step();
    Check(Runtime.Navigation.Agents.Single().Path.Count == 2, "navigation sees obstacle motion after every FixedUpdate callback completed");
}
var Parented = NavigationScene();
Parented.Scene.DestroyEntity(Registry, Parented.Wall.EntityId);
var Parent = Parented.Scene.CreateEntity(Registry);
Parent.Components[0].State["Position"] = new JsonArray(1f, 0f, 0f);
Parent.Components[0].State["Rotation"] = new JsonArray(0f, 90f, 0f);
Parent.Components[0].State["Scale"] = new JsonArray(2f, 1f, 2f);
Parented.Owner.ParentId = Parent.EntityId;
Parented.Owner.Components[0].State["Position"] = new JsonArray(0f, 0f, -2.5f);
using (var Runtime = new RuntimeWorld(Parented.Scene, Registry))
{
    Runtime.Step();
    Check(Near(Runtime.Navigation.Agents.Single().Position.X, -3.9f), "world-speed navigation converts movement through rotated scaled parent");
}
var Horizontal = NavigationScene();
Horizontal.SurfaceOwner.Components[0].State["Rotation"] = new JsonArray(30f, 0f, 0f);
using (var Runtime = new RuntimeWorld(Horizontal.Scene, Registry))
{
    Check(Runtime.Navigation.FindPath(Horizontal.Surface.ComponentId, new(-4, 0, 0), new(4, 0, 0)).Code == "NAV_SURFACE_MUST_BE_HORIZONTAL", "unsupported tilted surface is explicit");
}

foreach (var TypeId in TypeIds.Take(3))
{
    var Affine = new SceneData();
    var AffineParent = Affine.CreateEntity(Registry);
    AffineParent.Components[0].State["Scale"] = new JsonArray(2f, 1f, 0.5f);
    AffineParent.Components[0].State["Rotation"] = new JsonArray(20f, 13f, 5f);
    var AffineChild = Affine.CreateEntity(Registry, "Sheared shape", AffineParent.EntityId);
    AffineChild.Components[0].State["Rotation"] = new JsonArray(17f, 23f, 45f);
    AffineChild.Components[0].State["Position"] = new JsonArray(1f, 2f, 3f);
    Affine.AddComponent(Registry, AffineChild.EntityId, TypeId);
    var AffineDocument = new WorldDocument(Affine, Registry);
    var Matrix = AffineDocument.WorldMatrix(AffineDocument.Entities.Single(Entity => Entity.Id == AffineChild.EntityId));
    using var Runtime = new RuntimeWorld(Affine, Registry);
    var Collision = Runtime.Collision;
    var Match = true;
    for (var Index = 0; Index < 100; Index++)
    {
        var Local = new Vector3((float)Random.NextDouble() * 2 - 1, (float)Random.NextDouble() * 2 - 1, (float)Random.NextDouble() * 2 - 1);
        var Closest = new Vector3(0, TypeId == "engine.capsule-collider" ? Math.Clamp(Local.Y, -0.5f, 0.5f) : 0, 0);
        var Expected = TypeId == "engine.box-collider" ? MathF.Abs(Local.X) <= 0.5f && MathF.Abs(Local.Y) <= 0.5f && MathF.Abs(Local.Z) <= 0.5f :
            Vector3.Distance(Local, Closest) <= 0.5f;
        Match &= (Collision.OverlapSphere(Vector3.Transform(Local, Matrix), 0).Count != 0) == Expected;
    }
    Check(Match, TypeId + " 100 seeded containment queries preserve full affine/sheared hierarchy");
    var Direction = Vector3.TransformNormal(Vector3.UnitZ, Matrix);
    var Hit = Collision.Raycast(Vector3.Transform(new(0, 0, -5), Matrix), Direction, 30);
    Check(Hit is not null && Near(Hit.Distance, 4.5f * Direction.Length()) && Vector3.Dot(Hit.Normal, Direction) < 0,
        TypeId + " ray distance and inverse-transpose normal under sheared hierarchy");
    Runtime.FindEntity(AffineParent.EntityId)!.ActiveSelf = false;
    Check(Runtime.Collision.Colliders.Count == 0, "inactive ancestor excludes descendant collider");
}

var HeightFixture = NavigationScene();
HeightFixture.Wall.Components[0].State["Position"] = new JsonArray(0f, 1.5f, 0f);
HeightFixture.Wall.Components.Last().State["Size"] = new JsonArray(1f, 0.2f, 12f);
var Ground = HeightFixture.Scene.CreateEntity(Registry);
Ground.Components[0].State["Position"] = new JsonArray(0f, -0.5f, 0f);
HeightFixture.Scene.AddComponent(Registry, Ground.EntityId, "engine.box-collider", new()
{
    ["Size"] = new JsonArray(12f, 1f, 12f),
});
using (var Runtime = new RuntimeWorld(HeightFixture.Scene, Registry))
{
    Check(!Runtime.Navigation.FindPath(HeightFixture.Surface.ComponentId, new(-4, 0, 0), new(4, 0, 0)).Success, "tall agent blocked by low overhang");
    Check(Runtime.Navigation.FindPath(HeightFixture.Surface.ComponentId, new(-4, 0, 0), new(4, 0, 0), Height: 1).Success,
        "short agent passes under overhang and ground contact is walkable");
    Runtime.Patch(HeightFixture.Surface.ComponentId, new()
    {
        ["ObstacleMask"] = 0u,
    }, Runtime.Revision);
    Check(Runtime.Navigation.FindPath(HeightFixture.Surface.ComponentId, new(-4, 0, 0), new(4, 0, 0)).Points.Count == 2, "navigation obstacle layer mask");
}

var SpawnFixture = NavigationScene();
SpawnFixture.Scene.RemoveComponent(Registry, SpawnFixture.Owner.EntityId, SpawnFixture.Agent.ComponentId);
SpawnFixture.Scene.DestroyEntity(Registry, SpawnFixture.Wall.EntityId);
SpawnFixture.Scene.AddComponent(Registry, SpawnFixture.Owner.EntityId, Registry.Get(typeof(SpawnAgentOnFixed)).TypeId, new()
{
    ["Surface"] = SpawnFixture.Surface.ComponentId,
});
using (var Runtime = new RuntimeWorld(SpawnFixture.Scene, Registry))
{
    Runtime.Step();
    Check(Runtime.Navigation.Agents.Single().Position.X == -4, "agent added inside a tick waits until the following tick to move");
    Runtime.Step();
    Check(Runtime.Navigation.Agents.Single().Position.X > -4, "new agent moves on the following tick");
}

var Family = NavigationScene();
Family.Scene.DestroyEntity(Registry, Family.Wall.EntityId);
Family.Agent.State["Speed"] = 1f;
var ChildAgentOwner = Family.Scene.CreateEntity(Registry, "Child agent", Family.Owner.EntityId);
ChildAgentOwner.Components[0].State["Position"] = new JsonArray(1f, 0f, 0f);
Family.Scene.AddComponent(Registry, ChildAgentOwner.EntityId, "engine.nav-agent", new()
{
    ["Surface"] = Family.Surface.ComponentId,
    ["Speed"] = 1f,
    ["Destination"] = new JsonArray(3f, 0f, 0f),
    ["HasDestination"] = true,
});
using (var Runtime = new RuntimeWorld(Family.Scene, Registry))
{
    Runtime.Step();
    var Child = Runtime.Navigation.Agents.Single(Observation => Observation.EntityId == ChildAgentOwner.EntityId);
    Check(Near(Child.Position.X, -3 + RuntimeWorld.FixedDeltaSeconds), "parent and child agents each advance once in world space");
    var ChildView = new WorldDocument(Runtime.Snapshot(), Registry);
    Check(Near(ChildView.WorldMatrix(ChildView.Entities.Single(Entity => Entity.Id == ChildAgentOwner.EntityId)).M41, Child.Position.X),
        "child navigation observation matches actual committed hierarchical transform");
}

var Uniform = NavigationScene();
Uniform.Scene.DestroyEntity(Registry, Uniform.Wall.EntityId);
using (var Batched = new RuntimeWorld(Uniform.Scene, Registry))
using (var Stepped = new RuntimeWorld(Uniform.Scene, Registry))
{
    Batched.Tick(0.5f);
    for (var Index = 0; Index < 30; Index++)
    {
        Stepped.Step();
    }
    Check(Vector3.Distance(Batched.Navigation.Agents.Single().Position, Stepped.Navigation.Agents.Single().Position) < 0.0001f,
        "batched and individually stepped fixed updates agree");
}
var OwnCollider = NavigationScene();
OwnCollider.Scene.DestroyEntity(Registry, OwnCollider.Wall.EntityId);
OwnCollider.Scene.AddComponent(Registry, OwnCollider.Owner.EntityId, "engine.capsule-collider", new()
{
    ["Center"] = new JsonArray(0f, 0.9f, 0f),
    ["Radius"] = 0.3f,
    ["Height"] = 1.8f,
});
using (var Runtime = new RuntimeWorld(OwnCollider.Scene, Registry))
{
    Runtime.Step();
    Check(Runtime.Navigation.Agents.Single().Status == NavAgentStatus.Moving && Runtime.Navigation.Agents.Single().Path.Count == 2,
        "agent excludes its own collider from obstacle clearance");
}
Console.WriteLine($"Spatial validation passed: {Count} assertions (including 600 seeded analytic queries).");

[ComponentTypeId("83f7ce19-934a-4205-bfa5-1573ff4593be"), ComponentSchemaVersion(1)]
public sealed partial class FixedObstacle : SharpComponent
{
    protected override void OnFixedUpdate(float DeltaSeconds)
    {
        Entity.GetComponent<TransformComponent>()!.Position = new(0, 1, 20);
    }
}

[ComponentTypeId("5ffde336-5fb6-461c-87f9-ed4d8c7b3b3d"), ComponentSchemaVersion(1)]
public sealed partial class SpawnAgentOnFixed : SharpComponent
{
    public NavSurfaceComponent? Surface;
    private bool Spawned;

    protected override void OnFixedUpdate(float DeltaSeconds)
    {
        if (Spawned)
        {
            return;
        }
        Spawned = true;
        World.AddComponent<NavAgentComponent>(Entity.EntityId, new NavAgentComponent.InitialState
        {
            Surface = Surface,
            Destination = new(4, 0, 0),
            HasDestination = true,
        });
    }
}
