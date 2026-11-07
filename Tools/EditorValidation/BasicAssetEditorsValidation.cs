using System.Text.Json.Nodes;
using SharpEngine.Editor;

internal static class BasicAssetEditorsValidation
{
    internal static void Run(EditorRuntime Runtime, string ProjectRoot, Action<bool, string> Check)
    {
        var ExtensionType = typeof(SharpEngine.Animation.AnimationEditorModule).Assembly.GetType(
            "SharpEngine.Animation.AnimationStateMachineEditorExtension", true)!;
        using var Registration = Runtime.RegisterExtension(
            (IEditorExtension)Activator.CreateInstance(ExtensionType, true)!);
        foreach (var Id in new[]
        {
            BasicAssetFixture.StateMachineId,
        })
        {
            var Opened = Runtime.OpenAsset(Id);
            Check(Opened.Success, $"Basic asset editor opens {Id}: {Opened.Message}");
        }
        JsonObject Editor(string Id)
        {
            return JsonNode.Parse(Runtime.DescribeEditorJson())!["assetTools"]!["editors"]!.AsArray()
                .Single(Node => Node!["assetId"]!.GetValue<string>() == Id)!.AsObject();
        }
        JsonObject State() => Editor(BasicAssetFixture.StateMachineId)["state"]!.AsObject();
        void Action(string Operation)
        {
            var Root = Editor(BasicAssetFixture.StateMachineId)["rootId"]!.GetValue<string>();
            Runtime.InvokeUiAction($"editor.host-action:{Root}:editor.state-machine:{Operation}");
        }
        void Command(string Id)
        {
            var Root = Editor(BasicAssetFixture.StateMachineId)["rootId"]!.GetValue<string>();
            Runtime.InvokeUiAction($"editor.host-action:{Root}:editor.asset-command:{Id}");
        }
        var ControllerPath = Path.Combine(ProjectRoot, "Content", "Media", "Controller.json");
        var Original = File.ReadAllText(ControllerPath);
        Check(!State()["readOnly"]!.GetValue<bool>(), "Project state machines expose writable authoring");
        Check(State()["document"]!["schemaVersion"]!.GetValue<int>() == 2 && !State()["dirty"]!.GetValue<bool>(),
            "Legacy controller opens as explicit v2 blend settings without dirtying the document");
        Action("transition:0:blendDuration:0.5");
        Action("transition:0:blendCurve:SmoothStep");
        Check(State()["document"]!["transitions"]![0]!["blendDuration"]!.GetValue<double>() == 0.5 &&
            State()["document"]!["transitions"]![0]!["blendCurve"]!.GetValue<string>() == "SmoothStep",
            "Duration and curve commit through the same validated authoring transaction");
        var PreviewRevision = State()["documentRevision"]!.GetValue<ulong>();
        var PreviewSource = State()["document"]!.ToJsonString();
        var PreviewRoot = Editor(BasicAssetFixture.StateMachineId)["rootId"]!.GetValue<string>();
        Runtime.InvokeUiAction($"editor.host-action:{PreviewRoot}:editor.state-machine-preview:parameter:Speed:1");
        Command("step");
        Check(State()["preview"]!["playback"]!["blending"]!.GetValue<bool>() &&
            State()["preview"]!["playback"]!["stateId"]!.GetValue<string>() == "Walk",
            "Preview parameters drive the shared controller evaluator");
        Command("step");
        Check(State()["preview"]!["playback"]!["blendWeight"]!.GetValue<double>() > 0 &&
            State()["documentRevision"]!.GetValue<ulong>() == PreviewRevision &&
            State()["document"]!.ToJsonString() == PreviewSource,
            "Stepped pose blending is observable without dirtying authoring or entering its history");
        Command("stop");
        Command("undo");
        Command("undo");
        Check(State()["document"]!["transitions"]![0]!["blendDuration"]!.GetValue<double>() == 0 &&
            !State()["dirty"]!.GetValue<bool>(), "Undo restores both migrated blend defaults and the clean savepoint");
        Action("state:1:clipId:engine://animations/robot-walk");
        Check(State()["preview"]!["playback"] is null &&
            State()["preview"]!["diagnostic"]!.GetValue<string>().Length != 0,
            "Incompatible state clips keep the authoring document open with an unavailable-preview diagnostic");
        var UnavailableRevision = State()["documentRevision"]!.GetValue<ulong>();
        Runtime.InvokeUiAction($"editor.host-action:{PreviewRoot}:editor.state-machine-preview:state:Walk");
        Check(State()["preview"]!["diagnostic"]!.GetValue<string>().Contains("compatible preview mesh", StringComparison.Ordinal) &&
            State()["documentRevision"]!.GetValue<ulong>() == UnavailableRevision,
            "Unavailable preview actions report failure without authoring mutation");
        Command("undo");
        Check(State()["preview"]!["playback"] is not null && !State()["dirty"]!.GetValue<bool>(),
            "Undoing incompatible dependencies rebuilds the usable preview and restores the savepoint");
        Action("state:0:stateId:Rest");
        Check(State()["document"]!["initialStateId"]!.GetValue<string>() == "Rest" &&
            State()["document"]!["transitions"]![0]!["fromStateId"]!.GetValue<string>() == "Rest",
            "Renaming a state atomically updates the initial state and transition references");
        Action("parameter:0:parameterId:MoveSpeed");
        Check(State()["document"]!["transitions"]![0]!["conditions"]![0]!["parameterId"]!.GetValue<string>() == "MoveSpeed",
            "Parameter rename updates all condition references");
        var BeforeInvalid = State()["document"]!.ToJsonString();
        var BeforeRevision = State()["documentRevision"]!.GetValue<ulong>();
        foreach (var Invalid in new[]
        {
            "state:0:stateId:Walk", "state:0:speed:0", "state:0:clipId:project://missing",
            "transition:0:toStateId:Rest", "transition:0:exitTime:1.5",
            "condition:0:0:comparison:invalid", "condition:0:0:value:NaN", "remove-parameter:0",
            "state:0", "condition:0:0",
            "transition:0:blendDuration:-1", "transition:0:blendDuration:NaN", "transition:0:blendCurve:invalid",
        })
        {
            Action(Invalid);
            Check(State()["document"]!.ToJsonString() == BeforeInvalid &&
                State()["documentRevision"]!.GetValue<ulong>() == BeforeRevision &&
                State()["diagnostic"]!.GetValue<string>().Length != 0,
                "Invalid authoring is rejected without mutation or history: " + Invalid);
        }
        Command("add-transition");
        Action("transition:1:transitionId:Fallback");
        Action("move-up:1");
        Check(State()["document"]!["transitions"]![0]!["transitionId"]!.GetValue<string>() == "Fallback",
            "Transition order is editable and visible in the authoritative document");
        Command("undo");
        Check(State()["document"]!["transitions"]![0]!["transitionId"]!.GetValue<string>() == "StartWalking",
            "Transition reordering participates in document Undo");
        Command("redo");
        Action("add-condition:0");
        Check(State()["document"]!["transitions"]![0]!["conditions"]!.AsArray().Count == 1,
            "Conditions can be added using a declared parameter");
        Action("remove-condition:0:0");
        Command("add-state");
        Action("remove-state:0");
        Check(State()["document"]!["states"]!.AsArray().Count == 2 &&
            State()["document"]!["transitions"]!.AsArray().Count == 0 &&
            State()["document"]!["initialStateId"]!.GetValue<string>() == "Walk",
            "State removal atomically removes incident transitions and repairs the initial state");
        Command("undo");
        Check(State()["document"]!["transitions"]!.AsArray().Count == 2,
            "One Undo restores a state and all of its incident transitions");
        Check(File.ReadAllText(ControllerPath) == Original, "Authoring does not save implicitly");
        var RootId = Editor(BasicAssetFixture.StateMachineId)["rootId"]!.GetValue<string>();
        File.WriteAllText(ControllerPath, Original + "\n");
        Runtime.InvokeUiAction($"editor.host-action:{RootId}:editor.asset-save");
        Check(State()["dirty"]!.GetValue<bool>() && File.ReadAllText(ControllerPath) == Original + "\n",
            "State machine Save preserves dirty state and external changes on conflict");
        File.WriteAllText(ControllerPath, Original);
        Runtime.InvokeUiAction($"editor.host-action:{RootId}:editor.asset-save");
        var Saved = File.ReadAllText(ControllerPath);
        Check(!State()["dirty"]!.GetValue<bool>() && Saved.Contains("MoveSpeed", StringComparison.Ordinal),
            "State machine Save persists validated authoring and advances its savepoint");
        Command("undo");
        Check(State()["dirty"]!.GetValue<bool>(), "Undo after Save compares against the saved document");
        Command("redo");
        Check(!State()["dirty"]!.GetValue<bool>(), "Redo restores the saved document exactly");
        Check(Runtime.OpenAsset(BasicAssetFixture.StateMachineId).Code == "ASSET_EDITOR_REUSED",
            "Project state machines reuse their document root");

    }
}
