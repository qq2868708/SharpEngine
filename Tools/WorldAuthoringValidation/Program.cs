using System.Numerics;
using SharpEngine.Editor;
using SharpEngine.World;

var assertions = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    assertions++;
}

void Reject(string xml, string expectedText)
{
    try { _ = WorldDocument.Parse(xml); }
    catch (InvalidDataException exception) when (exception.Message.Contains(expectedText,
        StringComparison.OrdinalIgnoreCase))
    { assertions++; return; }
    throw new InvalidOperationException($"Expected world rejection containing '{expectedText}'");
}

Reject("{\"schemaVersion\":1,\"kind\":\"world\",\"worldId\":\"fixture\",\"name\":\"Fixture\",\"activeCameraId\":null,\"entities\":[]}", "schema");
Reject("{\"kind\":\"world\",\"worldId\":\"fixture\",\"name\":\"Fixture\",\"activeCameraId\":null,\"entities\":[]}", "fields");
Reject("{\"schemaVersion\":3,\"kind\":\"world\",\"worldId\":\"fixture\",\"name\":\"Fixture\",\"activeCameraId\":null,\"entities\":[{\"entityId\":\"sun\",\"name\":\"sun\",\"parentId\":null,\"activeSelf\":true,\"components\":[{\"componentId\":\"sun-transform\",\"typeId\":\"engine.transform\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Position\":[0,0,0],\"Rotation\":[0,0,0],\"Scale\":[1,1,1]}},{\"componentId\":\"sun-directional-light\",\"typeId\":\"engine.directional-light\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Intensity\":1}}]}]}", "fields");

var world = WorldDocument.Parse("{\"schemaVersion\":3,\"kind\":\"world\",\"worldId\":\"fixture\",\"name\":\"Authoring\",\"activeCameraId\":null,\"entities\":[{\"entityId\":\"a\",\"name\":\"a\",\"parentId\":null,\"activeSelf\":true,\"components\":[{\"componentId\":\"a-transform\",\"typeId\":\"engine.transform\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Position\":[0,0,0],\"Rotation\":[0,0,0],\"Scale\":[1,1,1]}},{\"componentId\":\"a-unknown\",\"typeId\":\"plugin.unknown\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"value\":42},\"extension\":\"preserve\"}]},{\"entityId\":\"b\",\"name\":\"b\",\"parentId\":null,\"activeSelf\":true,\"components\":[{\"componentId\":\"b-transform\",\"typeId\":\"engine.transform\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Position\":[2,0,0],\"Rotation\":[0,0,0],\"Scale\":[1,1,1]}}]},{\"entityId\":\"p\",\"name\":\"p\",\"parentId\":null,\"activeSelf\":true,\"components\":[{\"componentId\":\"p-transform\",\"typeId\":\"engine.transform\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Position\":[10,0,0],\"Rotation\":[0,0,0],\"Scale\":[1,1,1]}}]},{\"entityId\":\"d\",\"name\":\"d\",\"parentId\":\"p\",\"activeSelf\":true,\"components\":[{\"componentId\":\"d-transform\",\"typeId\":\"engine.transform\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Position\":[1,0,0],\"Rotation\":[0,0,0],\"Scale\":[1,1,1]}}]},{\"entityId\":\"c\",\"name\":\"c\",\"parentId\":null,\"activeSelf\":true,\"components\":[{\"componentId\":\"c-transform\",\"typeId\":\"engine.transform\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Position\":[4,0,0],\"Rotation\":[0,0,0],\"Scale\":[1,1,1]}}]}]}");
var session = new WorldAuthoringSession(world);
Check(session.DocumentRevision == 1 && session.SavepointRevision == 1 && !session.IsDirty,
    "New session must start at a clean savepoint");

var reparent = session.Reparent(new WorldReparentRequest("c", "p",
    WorldHierarchyPlacement.Inside, null, session.DocumentRevision));
Check(reparent.Success && reparent.BeforeRevision == 1 && reparent.AfterRevision == 2 && session.IsDirty,
    "Reparent must commit one revision and mark the document dirty");
var c = world.Entities.First(entity => entity.Id == "c");
Check(c.ParentId == "p" && MathF.Abs(c.Position.X + 6) < 0.0001f &&
    MathF.Abs(world.WorldMatrix(c).M41 - 4) < 0.0001f,
    "Inside reparent must preserve world transform by rewriting local transform");
Check(world.Entities.Where(entity => entity.ParentId == "p").Select(entity => entity.Id)
    .SequenceEqual(["d", "c"]), "Inside placement must append to the destination siblings");

var rejectedRevision = session.DocumentRevision;
var rejectedState = world.Serialize();
var cycle = session.Reparent(new WorldReparentRequest("p", "c",
    WorldHierarchyPlacement.Inside, null, session.DocumentRevision));
Check(!cycle.Success && cycle.Code == "PARENT_CYCLE" &&
    session.DocumentRevision == rejectedRevision && world.Serialize() == rejectedState,
    "Cycle rejection must leave the world and revision unchanged");
var missing = session.Reparent(new WorldReparentRequest("c", "missing",
    WorldHierarchyPlacement.Inside, null, session.DocumentRevision));
Check(!missing.Success && missing.Code == "PARENT_NOT_FOUND" &&
    session.DocumentRevision == rejectedRevision,
    "Missing parent rejection must be atomic");

var undoReparent = session.Undo(session.DocumentRevision);
Check(undoReparent.Success && c.ParentId.Length == 0 && MathF.Abs(c.Position.X - 4) < 0.0001f,
    "Undo must restore reparent parent and local transform");
var redoReparent = session.Redo(session.DocumentRevision);
Check(redoReparent.Success && c.ParentId == "p" && MathF.Abs(c.Position.X + 6) < 0.0001f,
    "Redo must restore the complete reparent transaction");

var before = session.Reparent(new WorldReparentRequest("b", null,
    WorldHierarchyPlacement.Before, "a", session.DocumentRevision));
Check(before.Success && world.Entities.Where(entity => entity.ParentId.Length == 0)
    .Take(2).Select(entity => entity.Id).SequenceEqual(["b", "a"]),
    "Before placement must define stable sibling order by entity id");
Check(world.Entities.First(entity => entity.Id == "b").Position == new WorldVector3(2, 0, 0),
    "Same-parent ordering must not decompose or rewrite local transform values");
var root = session.Reparent(new WorldReparentRequest("c", null,
    WorldHierarchyPlacement.Root, null, session.DocumentRevision));
Check(root.Success && c.ParentId.Length == 0 && world.Entities.Where(entity =>
    entity.ParentId.Length == 0).Last().Id == "c",
    "Root placement must unparent and append to root siblings");

var group = Guid.NewGuid().ToString("N");
var startPosition = c.Position;
var transform1 = session.SetTransform(new WorldTransformRequest("c",
    new WorldLocalTransform(new WorldVector3(7, 0, 0), c.Rotation, c.Scale),
    session.DocumentRevision, group));
var transform2 = session.SetTransform(new WorldTransformRequest("c",
    new WorldLocalTransform(new WorldVector3(9, 0, 0), c.Rotation, c.Scale),
    session.DocumentRevision, group));
Check(transform1.Success && transform2.Success &&
    transform1.Transaction?.TransactionId == transform2.Transaction?.TransactionId,
    "A caller-provided transform group must merge continuous updates into one history item");
var undoTransform = session.Undo(session.DocumentRevision);
Check(undoTransform.Success && c.Position == startPosition,
    "One Undo must restore the state before the entire transform group");
var redoTransform = session.Redo(session.DocumentRevision);
Check(redoTransform.Success && c.Position == new WorldVector3(9, 0, 0),
    "One Redo must restore the last transform in the group");

session.MarkSavepoint();
Check(!session.IsDirty && session.SavepointRevision == session.DocumentRevision,
    "MarkSavepoint must clear dirty without resetting document revision");
session.Undo(session.DocumentRevision);
Check(session.IsDirty, "Undo away from saved content must mark the document dirty");
session.Redo(session.DocumentRevision);
Check(!session.IsDirty, "Redo back to saved content must clear dirty");

var preserved = world.Serialize();
Check(preserved.Contains("plugin.unknown") && preserved.Contains("\"extension\": \"preserve\""),
    "Serialization retains unknown component records during unrelated edits");
Check(WorldDocument.Parse(preserved).Serialize() == preserved,
    "Repeated v3 serialization must be deterministic");
Check(world.ToJson().Contains("\"schemaVersion\": 3", StringComparison.Ordinal),
    "AI scene JSON must identify its v3 schema");

var sheared = WorldDocument.Parse("{\"schemaVersion\":3,\"kind\":\"world\",\"worldId\":\"fixture\",\"name\":\"Fixture\",\"activeCameraId\":null,\"entities\":[{\"entityId\":\"source\",\"name\":\"source\",\"parentId\":null,\"activeSelf\":true,\"components\":[{\"componentId\":\"source-transform\",\"typeId\":\"engine.transform\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Position\":[0,0,0],\"Rotation\":[10,25,5],\"Scale\":[1,1,1]}}]},{\"entityId\":\"parent\",\"name\":\"parent\",\"parentId\":null,\"activeSelf\":true,\"components\":[{\"componentId\":\"parent-transform\",\"typeId\":\"engine.transform\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Position\":[0,0,0],\"Rotation\":[20,30,15],\"Scale\":[2,1,0.5]}}]}]}");
var shearSession = new WorldAuthoringSession(sheared);
var shearBefore = sheared.Serialize();
var shearResult = shearSession.Reparent(new WorldReparentRequest("source", "parent",
    WorldHierarchyPlacement.Inside, null, shearSession.DocumentRevision));
Check(!shearResult.Success && shearResult.Code == "TRANSFORM_NOT_DECOMPOSABLE" &&
    shearSession.DocumentRevision == 1 && sheared.Serialize() == shearBefore,
    "A world-preserving reparent that requires shear must reject atomically");

var rotated = WorldDocument.Parse("{\"schemaVersion\":3,\"kind\":\"world\",\"worldId\":\"fixture\",\"name\":\"Fixture\",\"activeCameraId\":null,\"entities\":[{\"entityId\":\"source\",\"name\":\"source\",\"parentId\":null,\"activeSelf\":true,\"components\":[{\"componentId\":\"source-transform\",\"typeId\":\"engine.transform\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Position\":[3,4,5],\"Rotation\":[17,-38,29],\"Scale\":[1.5,1.5,1.5]}}]},{\"entityId\":\"parent\",\"name\":\"parent\",\"parentId\":null,\"activeSelf\":true,\"components\":[{\"componentId\":\"parent-transform\",\"typeId\":\"engine.transform\",\"schemaVersion\":1,\"enabled\":true,\"state\":{\"Position\":[-2,7,1],\"Rotation\":[-21,42,13],\"Scale\":[2,2,2]}}]}]}");
var rotatedSession = new WorldAuthoringSession(rotated);
var rotatedSource = rotated.Entities.First(entity => entity.Id == "source");
var rotatedWorldBefore = rotated.WorldMatrix(rotatedSource);
var rotatedResult = rotatedSession.Reparent(new WorldReparentRequest("source", "parent",
    WorldHierarchyPlacement.Inside, null, rotatedSession.DocumentRevision));
Check(rotatedResult.Success && MatrixNear(rotatedWorldBefore, rotated.WorldMatrix(rotatedSource)),
    "World-preserving reparent must retain position, rotation, and scale under a rotated uniform parent");

var temporary = Path.Combine(Path.GetTempPath(), $"SharpEngine-WorldAuthoring-{Guid.NewGuid():N}.sworld");
try
{
    rotatedSession.Save(temporary);
    Check(!rotatedSession.IsDirty && WorldDocument.Load(temporary).Entities.Count == 2,
        "Session Save must validate the temporary output and advance the savepoint");
}
finally
{
    if (File.Exists(temporary)) File.Delete(temporary);
}

AuthoringBoundaryValidation.Run(Check);
Console.WriteLine($"[WorldAuthoringValidation] PASS: {assertions} assertions");

bool MatrixNear(Matrix4x4 left, Matrix4x4 right)
{
    var a = new[] { left.M11, left.M12, left.M13, left.M14, left.M21, left.M22,
        left.M23, left.M24, left.M31, left.M32, left.M33, left.M34,
        left.M41, left.M42, left.M43, left.M44 };
    var b = new[] { right.M11, right.M12, right.M13, right.M14, right.M21, right.M22,
        right.M23, right.M24, right.M31, right.M32, right.M33, right.M34,
        right.M41, right.M42, right.M43, right.M44 };
    return a.Zip(b).All(pair => MathF.Abs(pair.First - pair.Second) < 0.001f);
}
