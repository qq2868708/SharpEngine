using System.Text.Json.Nodes;
using SharpEngine.Components;
using SharpEngine.UI;
using SharpEngine.World;

internal static class AuthoringBoundaryValidation
{
    internal static void Run(Action<bool, string> Check)
    {
        var Registry = ComponentRegistry.CreateBuiltin();
        var Source = new SceneData();
        var SourceEntity = Source.CreateEntity(Registry, "Boundary probe");
        Source.AddComponent(Registry, SourceEntity.EntityId, "engine.directional-light");
        var Document = new WorldDocument(Source, Registry);
        var Entity = Document.Entities.Single();
        var OldData = Document.Data;
        var OldComponent = Entity.Components[0];
        var Session = new WorldAuthoringSession(Document);
        var Original = Document.Serialize();

        void Reject(Action Operation)
        {
            var Rejected = false;
            try
            {
                Operation();
            }
            catch (InvalidOperationException Error)
            {
                Rejected = Error.Message.StartsWith("AUTHORING_", StringComparison.Ordinal);
            }
            Check(Rejected && Document.Serialize() == Original && Session.DocumentRevision == 1 &&
                !Session.IsDirty && !Session.CanUndo, "Direct mutation rejects without changing state, revision or history");
        }

        Reject(() => Entity.Position = new WorldVector3(7, 0, 0));
        Reject(() => Entity.Rotation = new WorldVector3(0, 45, 0));
        Reject(() => Entity.Scale = new WorldVector3(2, 2, 2));
        Reject(() => Entity.Color = new UiColor(0, 0, 0, 255));
        Reject(() => Entity.Intensity = 2);
        Reject(() => _ = new WorldAuthoringSession(Document));
        using (var Runtime = new RuntimeWorld(Document.Data, Registry))
        {
            Reject(() => Document.ApplyRuntimeFrame(Runtime.Frame));
            Runtime.FindEntity(Entity.Id)!.GetComponent<TransformComponent>()!.Position = new(5, 0, 0);
            Runtime.Step();
            Check(Runtime.Snapshot().Entities[0].Components[0].State["Position"]![0]!.GetValue<float>() == 5 &&
                Document.Serialize() == Original, "Runtime field assignment remains supported and isolated");
        }

        OldData.Entities.Clear();
        OldComponent.State["Position"] = new JsonArray(9, 9, 9);
        SourceEntity.Name = "Pre-ownership alias";
        Document.Data.Entities.Clear();
        var PublicComponent = Entity.Components[0];
        PublicComponent.ComponentId = "mutated";
        PublicComponent.State["Position"] = new JsonArray(8, 8, 8);
        Check(Document.Serialize() == Original && !Session.IsDirty && !Session.CanUndo,
            "Pre-ownership and public nested data aliases cannot mutate an owned document");

        SceneData? Retained = null;
        var Result = Session.Edit("boundary.rename", 1, Candidate =>
        {
            Candidate.Name = "Committed";
            Retained = Candidate;
        }, [Entity.Id]);
        var Committed = Document.Serialize();
        Retained!.Name = "After commit";
        Retained.Entities[0].Components.Clear();
        Check(Result.Success && Session.DocumentRevision == 2 && Session.CanUndo &&
            Document.Serialize() == Committed, "Committed candidates cannot remain writable aliases");
        var Undo = Session.Undo(2);
        var Redo = Session.Redo(3);
        Check(Undo.Success && Redo.Success && Document.Serialize() == Committed,
            "Candidate isolation preserves complete Undo/Redo");

        Result = Session.Edit("boundary.invalid", 4, Candidate =>
        {
            Candidate.Entities[0].Components.Clear();
        }, [Entity.Id]);
        Check(!Result.Success && Session.DocumentRevision == 4 && Document.Serialize() == Committed,
            "Invalid candidates leave committed data and history intact");
        var WrongThread = Task.Run(() => Session.Edit("boundary.thread", 4,
            Candidate => Candidate.Name = "Wrong thread", [Entity.Id])).GetAwaiter().GetResult();
        Check(!WrongThread.Success && WrongThread.Code == "WRONG_THREAD" &&
            Session.DocumentRevision == 4 && Document.Serialize() == Committed, "Transactions require the owner thread");
        var Conflict = Session.Edit("boundary.stale", 1, Candidate => Candidate.Name = "Stale", [Entity.Id]);
        Check(!Conflict.Success && Conflict.Code == "REVISION_CONFLICT" &&
            Document.Serialize() == Committed, "Stale revisions reject before editing");
        Result = Session.Edit("boundary.outer", 4, Candidate =>
        {
            var Nested = Session.Edit("boundary.inner", 4, Inner => Inner.Name = "Nested", [Entity.Id]);
            var NestedUndo = Session.Undo(4);
            Check(Nested.Code == "TRANSACTION_IN_PROGRESS" && NestedUndo.Code == "TRANSACTION_IN_PROGRESS",
                "Callbacks cannot nest another commit or Undo");
            Candidate.Name = "Outer";
        }, [Entity.Id]);
        Check(Result.Success && Session.DocumentRevision == 5 && Document.Name == "Outer",
            "One outer callback commits exactly one revision");
        var BeforeFailure = Document.Serialize();
        Result = Session.Edit("boundary.savepoint", 5, Candidate =>
        {
            Candidate.Name = "Not committed";
            Session.MarkSavepoint();
        }, [Entity.Id]);
        Check(!Result.Success && Session.SavepointRevision == 1 && Session.DocumentRevision == 5 &&
            Document.Serialize() == BeforeFailure, "Callbacks cannot move the savepoint during an edit");
    }
}
