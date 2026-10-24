using SharpEngine.Input;
using System.Text.Json;
using SharpEngine.Assets;
using SharpEngine.Host;

var root = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
var assertions = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    assertions++;
}

var engineRuntime = InputRuntimeService.Load(AssetRegistry.Load(root));
Check(engineRuntime.Actions.Where(value => value.Id.Contains("/editor.")).Select(value => value.Id).SequenceEqual(new[]
{
    "engine://input/actions/editor.play-pause",
    "engine://input/actions/editor.redo",
    "engine://input/actions/editor.save",
    "engine://input/actions/editor.stop",
    "engine://input/actions/editor.undo",
}), "Engine InputActions must load in deterministic ID order");
Check(engineRuntime.MappingContexts.Single(value => value.Id == "engine://input/contexts/editor").Bindings.Count == 7,
    "Engine InputMappingContext must load its validated bindings");
Check(engineRuntime.MappingContexts.Single(value => value.Id == "engine://input/contexts/editor")
        .Bindings.Any(binding => binding.ActionId == "engine://input/actions/editor.save" &&
            binding.Control == "Keyboard/S" && binding.ExactModifiers &&
            binding.Modifiers.SequenceEqual(["Keyboard/Control"])),
    "Ctrl+S must be declared as the Editor Save shortcut");
var undoEvents = new List<InputActionEvent>();
var redoEvents = new List<InputActionEvent>();
var PlayEvents = new List<InputActionEvent>();
using var undoHandler = engineRuntime.BindAction(InputRoute.Editor,
    "engine://input/actions/editor.undo", 0, value =>
    {
        undoEvents.Add(value);
        return true;
    });
using var redoHandler = engineRuntime.BindAction(InputRoute.Editor,
    "engine://input/actions/editor.redo", 0, value =>
    {
        redoEvents.Add(value);
        return true;
    });
using var PlayHandler = engineRuntime.BindAction(InputRoute.Editor,
    "engine://input/actions/editor.play-pause", 0, Value =>
    {
        PlayEvents.Add(Value);
        return true;
    });
engineRuntime.ProcessNative("main", NativeInputEventKind.KeyDown, 0x11,
    0, 0, 0, 0, 0, false, false);
engineRuntime.ProcessNative("main", NativeInputEventKind.KeyDown, 0x10,
    0, 0, 0, 0, 3, false, false);
engineRuntime.ProcessNative("main", NativeInputEventKind.KeyDown, 'Z',
    0, 0, 0, 0, 3, false, false);
Check(redoEvents.Any(value => value.Phase == InputActionPhase.Performed) &&
    undoEvents.Count == 0,
    "Exact modifiers must route Ctrl+Shift+Z to Redo without also firing Undo");
engineRuntime.ProcessNative("main", NativeInputEventKind.KeyUp, 'Z',
    0, 0, 0, 0, 0, false, false);
engineRuntime.ProcessNative("main", NativeInputEventKind.KeyUp, 0x10,
    0, 0, 0, 0, 0, false, false);
engineRuntime.ProcessNative("main", NativeInputEventKind.KeyUp, 0x11,
    0, 0, 0, 0, 0, false, false);
engineRuntime.ProcessNative("main", NativeInputEventKind.KeyDown, 0x74,
    0, 0, 0, 0, 0, false, true);
Check(PlayEvents.Any(Value => Value.Phase == InputActionPhase.Performed),
    "F5 must route to the editor Play/Pause action while the viewport is focused");
engineRuntime.ProcessNative("main", NativeInputEventKind.KeyUp, 0x74,
    0, 0, 0, 0, 0, false, true);
var UndoCount = undoEvents.Count;
engineRuntime.ProcessNative("main", NativeInputEventKind.KeyDown, 'Z',
    0, 0, 0, 0, 1, false, false);
Check(undoEvents.Count > UndoCount &&
    undoEvents[^1].Phase == InputActionPhase.Performed,
    "RmlUi Ctrl modifier bit must route Ctrl+Z to the editor Undo action");
engineRuntime.ProcessNative("main", NativeInputEventKind.KeyUp, 'Z',
    0, 0, 0, 0, 1, false, false);
var UndoAfterCtrlRelease = undoEvents.Count;
engineRuntime.ProcessNative("main", NativeInputEventKind.KeyDown, 'Z',
    0, 0, 0, 0, 2, false, false);
Check(undoEvents.Count == UndoAfterCtrlRelease,
    "Shift+Z must not satisfy the editor Ctrl+Z binding");
engineRuntime.ProcessNative("main", NativeInputEventKind.KeyUp, 'Z',
    0, 0, 0, 0, 2, false, false);
var UiHandledPlayCount = PlayEvents.Count;
engineRuntime.ProcessNative("main", NativeInputEventKind.KeyDown, 0x74,
    0, 0, 0, 0, 0, true, false);
Check(PlayEvents.Count > UiHandledPlayCount &&
    PlayEvents[^1].Phase == InputActionPhase.Performed,
    "Editor shortcuts must keep working while RmlUi owns the key");
engineRuntime.ProcessNative("main", NativeInputEventKind.KeyUp, 0x74,
    0, 0, 0, 0, 0, true, false);

// Shortcut scopes are part of the Editor route: the focused host's handler runs first
// and stops the gesture; declining lets the same action bubble to the global layer.
var scopeActions = new[]
{
    new InputActionDefinition(1, "project://input/actions/scope.save",
        InputActionValueType.Button, false),
};
var scopeContexts = new[]
{
    new InputMappingContextDefinition(1, "project://input/contexts/scope.editor",
        InputRoute.Editor, 1000, true, new[]
        {
            new InputBindingDefinition("project://input/actions/scope.save", "Keyboard/S",
                1, 0, new[] { "Keyboard/Control" }, true),
        }),
};
var scoped = InputRuntimeService.CreateForValidation(scopeActions, scopeContexts);
var scopeOrder = new List<string>();
var ScopedAction = "project://input/actions/scope.save";
using var scopedGlobal = scoped.BindAction(InputRoute.Editor, ScopedAction, 0, value =>
{
    if (value.Phase == InputActionPhase.Performed) scopeOrder.Add("global:" + value.HostId);
    return true;
});
var ScopeConsumes = true;
using var scopedFocus = scoped.BindAction(InputRoute.Editor, ScopedAction, 1000, value =>
{
    if (value.HostId != "asset-editor-a" || value.Phase != InputActionPhase.Performed)
    {
        return false;
    }
    scopeOrder.Add("scope");
    return ScopeConsumes;
});
void PressCtrlS(string Host)
{
    scoped.ProcessNative(Host, NativeInputEventKind.KeyDown, 'S', 0, 0, 0, 0, 1, false, true);
    scoped.ProcessNative(Host, NativeInputEventKind.KeyUp, 'S', 0, 0, 0, 0, 1, false, true);
}
PressCtrlS("asset-editor-a");
Check(scopeOrder.SequenceEqual(new[] { "scope" }),
    "The focused host scope must consume the shortcut before the global layer");
scopeOrder.Clear();
PressCtrlS("main");
Check(scopeOrder.SequenceEqual(new[] { "global:main" }),
    "A key event from another host must bubble to the global editor layer");
scopeOrder.Clear();
ScopeConsumes = false;
PressCtrlS("asset-editor-a");
Check(scopeOrder.SequenceEqual(new[] { "scope", "global:asset-editor-a" }),
    "A scope that declines must let the same action bubble to the global layer");

var actions = new[]
{
    new InputActionDefinition(1, "project://input/actions/ui.confirm",
        InputActionValueType.Button, false),
    new InputActionDefinition(1, "project://input/actions/gameplay.primary",
        InputActionValueType.Button, false),
    new InputActionDefinition(1, "project://input/actions/gameplay.move",
        InputActionValueType.Axis2D, true),
    new InputActionDefinition(1, "project://input/actions/gameplay.look",
        InputActionValueType.Axis2D, false),
};
var contexts = new[]
{
    new InputMappingContextDefinition(1, "project://input/contexts/ui",
        InputRoute.RuntimeUi, 100, true, new[]
        {
            new InputBindingDefinition("project://input/actions/ui.confirm",
                "Keyboard/Space", 1, 0, Array.Empty<string>(), false),
        }),
    new InputMappingContextDefinition(1, "project://input/contexts/gameplay",
        InputRoute.Gameplay, 0, true, new[]
        {
            new InputBindingDefinition("project://input/actions/gameplay.primary",
                "Keyboard/Space", 1, 0, Array.Empty<string>(), false),
            new InputBindingDefinition("project://input/actions/gameplay.move",
                "Keyboard/W", 0, 1, Array.Empty<string>(), false),
            new InputBindingDefinition("project://input/actions/gameplay.move",
                "Keyboard/D", 1, 0, Array.Empty<string>(), false),
            new InputBindingDefinition("project://input/actions/gameplay.look",
                "Mouse/Delta", 1, 1, Array.Empty<string>(), false),
        }),
};

var input = InputRuntimeService.CreateForValidation(actions, contexts);
input.SetRouteEnabled(InputRoute.RuntimeUi, true);
input.SetRouteEnabled(InputRoute.Gameplay, true);
var uiEvents = new List<InputActionEvent>();
var gameplayEvents = new List<InputActionEvent>();
using var uiHandler = input.BindAction(InputRoute.RuntimeUi,
    "project://input/actions/ui.confirm", 0, value =>
    {
        uiEvents.Add(value);
        return true;
    });
using var gameplayHandler = input.BindAction(InputRoute.Gameplay,
    "project://input/actions/gameplay.primary", 0, value =>
    {
        gameplayEvents.Add(value);
        return true;
    });

var consumed = input.ProcessNative("main", NativeInputEventKind.KeyDown, 0x20,
    0, 0, 0, 0, 0, false, true);
Check(consumed && uiEvents.Any(value => value.Phase == InputActionPhase.Performed) &&
    gameplayEvents.Count == 0,
    "Runtime UI must consume a semantic action before Gameplay sees it");
input.ProcessNative("main", NativeInputEventKind.KeyUp, 0x20,
    0, 0, 0, 0, 0, false, false);

uiHandler.Dispose();
input.ProcessNative("main", NativeInputEventKind.KeyDown, 0x20,
    0, 0, 0, 0, 0, false, true);
Check(gameplayEvents.Any(value => value.Phase == InputActionPhase.Performed),
    "An unhandled Runtime UI action must pass through to Gameplay");
input.ProcessNative("main", NativeInputEventKind.KeyUp, 0x20,
    0, 0, 0, 0, 0, false, false);
var beforeSuppressed = gameplayEvents.Count;
input.ProcessNative("main", NativeInputEventKind.KeyDown, 0x20,
    0, 0, 0, 0, 0, true, false);
input.ProcessNative("main", NativeInputEventKind.KeyUp, 0x20,
    0, 0, 0, 0, 0, true, false);
Check(gameplayEvents.Count == beforeSuppressed,
    "A raw event consumed by RmlUi must not create a Gameplay action");
var lookEvents = new List<InputActionEvent>();
using var lookHandler = input.BindAction(InputRoute.Gameplay,
    "project://input/actions/gameplay.look", 0, value =>
    {
        lookEvents.Add(value);
        return true;
    });
input.ProcessNative("main", NativeInputEventKind.PointerMove, 0,
    20, 30, 4, -3, 0, true, false);
Check(lookEvents.Count == 0,
    "RmlUi consumption must also suppress transient mouse axes");

var moveEvents = new List<InputActionEvent>();
using var moveHandler = input.BindAction(InputRoute.Gameplay,
    "project://input/actions/gameplay.move", 0, value =>
    {
        moveEvents.Add(value);
        return true;
    });
input.ProcessNative("main", NativeInputEventKind.KeyDown, 'W',
    0, 0, 0, 0, 0, false, true);
input.ProcessNative("main", NativeInputEventKind.KeyDown, 'D',
    0, 0, 0, 0, 0, false, true);
Check(moveEvents.Last(value => value.Phase == InputActionPhase.Performed).Value ==
    new InputActionValue(1, 1), "Axis2D bindings must combine deterministically");
input.AdvanceFrame(1f / 60f);
Check(moveEvents[^1].Phase == InputActionPhase.Performed && moveEvents[^1].FrameId == 1,
    "Continuous actions must perform on each engine frame");
input.ProcessNative("main", NativeInputEventKind.FocusLost, 0,
    0, 0, 0, 0, 0, true, false);
Check(moveEvents.Any(value => value.Phase == InputActionPhase.Canceled),
    "Focus loss must cancel active actions and clear held controls");
Check(moveEvents.All(value => value.DeviceId == "keyboard:0"),
    "Keyboard action events must carry a stable device ID");

input.ProcessNative("main", NativeInputEventKind.KeyDown, 'W', 0, 0, 0, 0, 0, false, true);
input.SetRouteEnabled(InputRoute.Gameplay, false);
Check(moveEvents[^1].Phase == InputActionPhase.Canceled, "Pause must cancel active movement");
var pausedCount = moveEvents.Count;
input.SetRouteEnabled(InputRoute.Gameplay, true);
input.AdvanceFrame(0);
Check(moveEvents.Count == pausedCount, "Resume must not resurrect the old held action");
input.ProcessNative("main", NativeInputEventKind.KeyUp, 'W', 0, 0, 0, 0, 0, false, true);
input.ProcessNative("main", NativeInputEventKind.PointerMove, 0, 20, 30, 12, -9, 0, false, true);
Check(lookEvents.Single(value => value.Phase == InputActionPhase.Performed).Value ==
    new InputActionValue(12, -9), "Mouse deltas must retain their full magnitude");
Check(lookEvents[^1].Phase == InputActionPhase.Canceled, "Impulse axes must return to neutral");

var prioritized = InputRuntimeService.CreateForValidation(actions, new[]
{
    contexts[1] with { Id = "project://input/contexts/high", Priority = 100, ActivateByDefault = false },
    contexts[1] with { Id = "project://input/contexts/low", Priority = 0 },
});
prioritized.SetRouteEnabled(InputRoute.Gameplay, true);
var contextEvents = new List<InputActionEvent>();
using var priorityHandler = prioritized.BindAction(InputRoute.Gameplay,
    "project://input/actions/gameplay.primary", 0, value => { contextEvents.Add(value); return true; });
using var activeHigh = prioritized.ActivateContext("project://input/contexts/high");
prioritized.ProcessNative("main", NativeInputEventKind.KeyDown, 0x20, 0, 0, 0, 0, 0, false, true);
Check(contextEvents.All(value => value.ContextId.EndsWith("/high")),
    "Consumption must block lower priority contexts in the same domain");
activeHigh.Dispose();
Check(contextEvents[^1].Phase == InputActionPhase.Canceled,
    "Removing the active context must cancel its action");
prioritized.ProcessNative("main", NativeInputEventKind.KeyUp, 0x20, 0, 0, 0, 0, 0, false, true);
prioritized.ProcessNative("main", NativeInputEventKind.KeyDown, 0x20, 0, 0, 0, 0, 0, false, true);
Check(contextEvents[^1].ContextId.EndsWith("/low"), "The next press must reach the remaining context");

using var snapshot = JsonDocument.Parse(input.DescribeJson());
var snapshotRoot = snapshot.RootElement;
Check(snapshotRoot.GetProperty("schemaVersion").GetInt32() == 1 &&
    snapshotRoot.GetProperty("stateRevision").GetUInt64() > 1 &&
    snapshotRoot.GetProperty("recentEvents").GetArrayLength() > 0,
    "Input snapshot must expose version, live revision, and routed history");
Check(snapshotRoot.GetProperty("contexts").EnumerateArray().Select(value =>
        value.GetProperty("id").GetString()).SequenceEqual(contexts.Select(value => value.Id)
        .OrderBy(value => value, StringComparer.Ordinal)),
    "Input snapshot context ordering must be deterministic");

string Replay()
{
    var replay = InputRuntimeService.CreateForValidation(actions, contexts);
    replay.SetRouteEnabled(InputRoute.Gameplay, true);
    replay.ProcessNative("main", NativeInputEventKind.KeyDown, 'W', 0, 0, 0, 0, 0, false, true);
    replay.AdvanceFrame(1f / 60f);
    replay.ProcessNative("main", NativeInputEventKind.KeyUp, 'W', 0, 0, 0, 0, 0, false, true);
    return replay.DescribeJson();
}
Check(Replay() == Replay(), "Identical input and frame sequences must produce identical snapshots");
var temporary = Directory.CreateTempSubdirectory("SharpEngine-InputAssets-");
try
{
    File.WriteAllText(Path.Combine(temporary.FullName, "test.sharpproject"),
        "{\"assets\":{\"manifest\":\"assets.json\"}}");
    File.WriteAllText(Path.Combine(temporary.FullName, "assets.json"),
        "{\"schemaVersion\":2,\"assets\":[{\"id\":\"input/actions/test\",\"type\":\"InputAction\",\"path\":\"test.json\"}]}");
    var valid = "{\"schemaVersion\":1,\"actionId\":\"project://input/actions/test\",\"valueType\":\"Axis2D\",\"continuous\":true}";
    Directory.CreateDirectory(Path.Combine(temporary.FullName, "Content"));
    var path = Path.Combine(temporary.FullName, "Content", "test.json");
    File.WriteAllText(path, valid);
    var loaded = InputRuntimeService.Load(AssetRegistry.Load(root, temporary.FullName));
    Check(loaded.Actions.Single(action => action.Id == "project://input/actions/test").Continuous,
        "Non-default input asset values must survive loading");
    foreach (var invalid in new[] { valid.Replace("\"schemaVersion\":1", "\"schemaVersion\":99"),
                 valid.Replace("\"continuous\":true", "\"continuous\":\"true\""),
                 valid.Replace("\"continuous\":true", "\"continuous\":true,\"typo\":0"),
                 valid.Replace("\"continuous\":true", "\"continuous\":true,\"continuous\":false") })
    {
        File.WriteAllText(path, invalid);
        var rejected = false;
        try { _ = InputRuntimeService.Load(AssetRegistry.Load(root, temporary.FullName)); }
        catch (InvalidDataException exception) { rejected = exception.Message.StartsWith("SEINPUT_"); }
        Check(rejected, "Invalid versions, wrong types, unknown and duplicate fields must fail explicitly");
    }
}
finally { temporary.Delete(true); }
Console.WriteLine($"PASS: {assertions} unified input assertions");


