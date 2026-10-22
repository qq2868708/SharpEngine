using System.Text.Json.Nodes;
using SharpEngine.Input;
using SharpEngine.Settings;
using SharpEngine.Editor;

internal static class ShortcutValidation
{
    internal static void Run(EditorRuntime Runtime, Action<bool, string> Require)
    {
        var Input = InputRuntimeService.CreateForValidation(
            new[]
            {
                new InputActionDefinition(1, EditorShortcutSettings.PlayPauseActionId,
                    InputActionValueType.Button, false),
                new InputActionDefinition(1, EditorShortcutSettings.StopActionId,
                    InputActionValueType.Button, false),
                new InputActionDefinition(1, EditorShortcutSettings.SaveActionId,
                    InputActionValueType.Button, false),
                new InputActionDefinition(1, EditorShortcutSettings.UndoActionId,
                    InputActionValueType.Button, false),
                new InputActionDefinition(1, EditorShortcutSettings.RedoActionId,
                    InputActionValueType.Button, false),
                new InputActionDefinition(1, EditorShortcutSettings.ReleasePointerActionId,
                    InputActionValueType.Button, false),
            },
            new[]
            {
                new InputMappingContextDefinition(1, EditorShortcutSettings.EditorContextId,
                    InputRoute.Editor, 1000, true, Array.Empty<InputBindingDefinition>()),
            });
        Runtime.AttachInputRuntime(Input);
        var InputContext = Input;
        Runtime.ApplyEngineShortcuts(Force: true);
        Runtime.InvokeUiAction("editor.settings-page:engine:" +
            EditorShortcutSettings.SettingsId);
        var Markup = Runtime.BuildSettingsMarkup(SettingsScope.Engine);
        foreach (var Field in new[]
                 {
                     "playPause", "stop", "save", "undo", "redo", "redoAlternate", "releasePointer",
                     "navigateForward", "navigateBack", "navigateLeft", "navigateRight",
                     "navigateUp", "navigateDown", "navigateForwardAlternate",
                     "navigateBackAlternate", "navigateLeftAlternate",
                     "navigateRightAlternate", "navigateSpeedModifier",
                 })
        {
            var NestedObject = Markup.Contains("id=\"setting-" + Field + "/key\"",
                StringComparison.Ordinal);
            var Scalar = Markup.Contains("id=\"setting-" + Field + "\"",
                StringComparison.Ordinal);
            Require(NestedObject || Scalar,
                $"Engine Settings must expose the '{Field}' engine shortcut");
        }

        string GestureValue(string Group, string Name) =>
            JsonNode.Parse(Runtime.DescribeShortcutsJson())![Group]![Name]!.GetValue<string>();
        string Gesture(string Name)
        {
            var Bindings = JsonNode.Parse(Runtime.DescribeShortcutsJson())!["bindings"]!.AsArray();
            return Bindings.First(Binding => Binding!["name"]!.GetValue<string>() == Name)!
                ["gesture"]!.GetValue<string>();
        }
        var Described = JsonNode.Parse(Runtime.DescribeShortcutsJson())!;
        Require(Described["bindings"]!.AsArray().Select(Binding =>
                Binding!["gesture"]!.GetValue<string>()).SequenceEqual(new[]
                {
                    "F5", "F8", "Ctrl+S", "Ctrl+Z", "Ctrl+Y", "Ctrl+Shift+Z", "Escape",
                }),
            "Engine shortcut defaults must serialize in deterministic declaration order");
        Require(Described["navigation"]!["forward"]!.GetValue<string>() == "W" &&
            Described["navigation"]!["down"]!.GetValue<string>() == "Q" &&
            Described["navigation"]!["speedModifier"]!.GetValue<string>() == "Shift" &&
            Described["navigation"]!["forwardAlternate"]!.GetValue<string>() == "Up",
            "Viewport navigation keys belong to the engine shortcut settings page");


        var ContextId = EditorShortcutSettings.EditorContextId;
        Require(InputContext.BindingsFor(ContextId).Count == 7,
            "The editor InputMappingContext must start with the seven engine shortcut bindings");
        Require(InputContext.BindingsFor(ContextId).Any(Binding =>
                Binding.ActionId == EditorShortcutSettings.SaveActionId &&
                Binding.Control == "Keyboard/S" && Binding.ExactModifiers &&
                Binding.Modifiers.SequenceEqual(["Keyboard/Control"])),
            "Ctrl+S must be bound to the editor Save action");

        // Shortcuts execute the same commands as the toolbar, not raw UI actions.
        var Global = JsonNode.Parse(Runtime.DescribeShortcutsJson())!["routing"]!["global"]!.AsArray();
        Require(Global.Any(Route => Route!["action"]!.GetValue<string>() ==
                EditorShortcutSettings.SaveActionId &&
            Route["command"]!.GetValue<string>() == "core.save-current-level" &&
            Route["dispatches"]!.GetValue<string>() == "editor.command:core.save-current-level"),
            "The Save shortcut must dispatch the shared editor command");
        foreach (var (ActionId, CommandId) in Runtime.Shortcuts.CommandRoutes())
        {
            Require(Runtime.Commands.Commands.Any(Command => Command.Id == CommandId),
                $"Shortcut action '{ActionId}' must name a registered editor command '{CommandId}'");
        }
        Require(Runtime.Commands.Commands.Any(Command => Command.Id == "core.play-pause") &&
            Runtime.Commands.Commands.Any(Command => Command.Id == "core.stop") &&
            Runtime.Commands.Commands.Any(Command => Command.Id == "core.undo") &&
            Runtime.Commands.Commands.Any(Command => Command.Id == "core.redo"),
            "Every command-backed shortcut must have a toolbar/menu command to share");

        // Shortcut scopes are part of the input route, not a parallel hotkey table.
        var Opened = Runtime.OpenAsset("project://materials/validation");
        Require(Opened.Success, "The shortcut scope check requires an openable asset editor");
        var DescribedRouting = JsonNode.Parse(Runtime.DescribeShortcutsJson())!["routing"]!;
        Require(DescribedRouting["order"]!.AsArray().Select(Value => Value!.GetValue<string>())
            .SequenceEqual(new[] { "focusedHostScope", "globalEditor" }),
            "Shortcut routing must declare the focused scope before the global layer");
        var Scopes = DescribedRouting["scopes"]!.AsArray();
        Require(Scopes.Any(Scope => Scope!["routes"]!.AsArray().Any(Route =>
                Route!["action"]!.GetValue<string>() == EditorShortcutSettings.SaveActionId &&
                Route["command"]!.GetValue<string>() == "editor.asset-save") &&
            Scope["priority"]!.GetValue<int>() > EditorShortcutSettings.GlobalPriority),
            "A focused asset editor must publish a Save scope above the global layer");
        var ScopeHost = Scopes.First(Scope => Scope!["routes"]!.AsArray().Any(Route =>
            Route!["action"]!.GetValue<string>() == EditorShortcutSettings.SaveActionId))!
            ["hostId"]!.GetValue<string>();
        Runtime.InvokeUiAction($"editor.host-action:{ScopeHost}:editor.asset-close");
        Require(JsonNode.Parse(Runtime.DescribeShortcutsJson())!["routing"]!["scopes"]!
                .AsArray().Count == 0,
            "Closing an asset editor must withdraw its shortcut scope");

        Runtime.InvokeUiAction($"editor.settings-change:engine:{EditorShortcutSettings.SettingsId}:playPause/key:F6");
        Require(Runtime.Shortcuts.PlayPause.Key == EngineShortcutKey.F6,
            "A nested shortcut key edit must publish a new live settings snapshot");
        Require(InputContext.BindingsFor(ContextId).Any(Binding =>
                Binding.ActionId == EditorShortcutSettings.PlayPauseActionId &&
                Binding.Control == "Keyboard/F6" && Binding.ExactModifiers &&
                Binding.Modifiers.Count == 0),
            "A shortcut edit must rebind the live editor input context");

        Runtime.InvokeUiAction($"editor.settings-change:engine:{EditorShortcutSettings.SettingsId}:playPause/control:true");
        Require(Runtime.Shortcuts.PlayPause.Control &&
            InputContext.BindingsFor(ContextId).Any(Binding =>
                Binding.ActionId == EditorShortcutSettings.PlayPauseActionId &&
                Binding.Control == "Keyboard/F6" &&
                Binding.Modifiers.SequenceEqual(["Keyboard/Control"])),
            "Modifier toggles must be exact and rebind live");
        Require(Gesture("playPause") == "Ctrl+F6",
            "The described gesture must include the configured modifiers");

        var DuplicateBefore = InputContext.BindingsFor(ContextId).Count(Binding =>
            Binding.ActionId == EditorShortcutSettings.ReleasePointerActionId);
        Runtime.InvokeUiAction($"editor.settings-change:engine:{EditorShortcutSettings.SettingsId}:releasePointer/key:F6");
        Runtime.InvokeUiAction($"editor.settings-change:engine:{EditorShortcutSettings.SettingsId}:releasePointer/control:true");
        Runtime.InvokeUiAction($"editor.settings-change:engine:{EditorShortcutSettings.SettingsId}:releasePointer/shift:false");
        var Diagnostics = JsonNode.Parse(Runtime.DescribeShortcutsJson())!["diagnostics"]!.AsArray();
        Require(Diagnostics.Any(Diagnostic => Diagnostic!.GetValue<string>()
                .StartsWith("SESHORTCUT_DUPLICATE", StringComparison.Ordinal)),
            "A duplicate gesture must produce a structured shortcut diagnostic: " +
            Runtime.DescribeShortcutsJson());
        Require(InputContext.BindingsFor(ContextId).Count(Binding =>
                Binding.ActionId == EditorShortcutSettings.ReleasePointerActionId) == DuplicateBefore,
            "A duplicate gesture must not remove or duplicate the original binding: before=" +
            DuplicateBefore + " after=" + InputContext.BindingsFor(ContextId).Count(Binding =>
                Binding.ActionId == EditorShortcutSettings.ReleasePointerActionId));

        Runtime.InvokeUiAction($"editor.settings-change:engine:{EditorShortcutSettings.SettingsId}:undo/key:None");
        Require(!InputContext.BindingsFor(ContextId).Any(Binding =>
                Binding.ActionId == EditorShortcutSettings.UndoActionId),
            "Setting a shortcut to None must remove its live binding");
        Require(Gesture("undo") == "Unbound",
            "Unbound shortcuts must remain visible instead of disappearing");

        Runtime.InvokeUiAction($"editor.settings-change:engine:{EditorShortcutSettings.SettingsId}:navigateForward:Up");
        Require(Runtime.Shortcuts.NavigateForward == EngineShortcutKey.Up &&
            GestureValue("navigation", "forward") == "Up", "Observed " + Runtime.DescribeShortcutsJson() +
            "Viewport navigation keys must be rebindable from the same settings page");

        Runtime.InvokeUiAction($"editor.settings-change:engine:{EditorShortcutSettings.SettingsId}:playPause/key:F5");
        Runtime.InvokeUiAction($"editor.settings-change:engine:{EditorShortcutSettings.SettingsId}:playPause/control:false");
        Runtime.InvokeUiAction($"editor.settings-change:engine:{EditorShortcutSettings.SettingsId}:releasePointer/key:Escape");
        Runtime.InvokeUiAction($"editor.settings-change:engine:{EditorShortcutSettings.SettingsId}:releasePointer/control:false");
        Runtime.InvokeUiAction($"editor.settings-change:engine:{EditorShortcutSettings.SettingsId}:releasePointer/shift:false");
        Runtime.InvokeUiAction($"editor.settings-change:engine:{EditorShortcutSettings.SettingsId}:undo/key:Z");
        Runtime.InvokeUiAction($"editor.settings-change:engine:{EditorShortcutSettings.SettingsId}:navigateForward:W");
        Require(Runtime.Shortcuts.PlayPause.Key == EngineShortcutKey.F5 &&
            !Runtime.Shortcuts.PlayPause.Control &&
            Runtime.Shortcuts.Undo.Key == EngineShortcutKey.Z &&
            Runtime.Shortcuts.ReleasePointer.Key == EngineShortcutKey.Escape &&
            !Runtime.Shortcuts.ReleasePointer.Shift &&
            Runtime.Shortcuts.NavigateForward == EngineShortcutKey.W &&
            !Runtime.DescribeShortcutsJson().Contains("SESHORTCUT_", StringComparison.Ordinal),
            "Reverting every edit must restore the documented engine shortcut defaults");
        Require(InputContext.BindingsFor(ContextId).Count == 7,
            "Restored defaults must leave the live context with seven shortcut bindings");
        Runtime.InvokeUiAction("editor.settings-page:engine:sharpengine.editor-navigation");
    }
}














