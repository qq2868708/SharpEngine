using System.Text.Json.Nodes;
using SharpEngine.Input;
using SharpEngine.Logging;
using SharpEngine.Settings;

namespace SharpEngine.Editor;

/// <summary>Engine-owned keyboard shortcuts. The Editor Shortcuts settings page is the
/// single source of truth; each committed change rebinds the live editor input context and
/// the viewport navigation key map without restarting the engine.</summary>
public sealed partial class EditorRuntime
{
    private EditorShortcutSettings _shortcutSettings = new();
    private long _appliedShortcutRevision = long.MinValue;
    private IReadOnlyList<string> _shortcutDiagnostics = Array.Empty<string>();
    private Dictionary<string, InputBindingDefinition> _appliedShortcutBindings = new(StringComparer.Ordinal);
    private IInputRuntime? _shortcutInputRuntime;

    public EditorShortcutSettings Shortcuts
    {
        get
        {
            return _shortcutSettings;
        }
    }

    internal void ApplyEngineShortcuts(bool Force = false)
    {
        var Page = _developmentSettings?.Find(SettingsScope.Engine,
            EditorShortcutSettings.SettingsId);
        // The input runtime arrives after settings initialization, and a project may
        // ship saved overrides, so a new runtime always re-applies the current values.
        var Rebind = !ReferenceEquals(_shortcutInputRuntime, _inputRuntime);
        if (!Force && !Rebind && Page is not null && Page.Revision == _appliedShortcutRevision)
        {
            return;
        }
        _shortcutSettings = ReadEngineShortcutSettings();
        _appliedShortcutRevision = Page?.Revision ?? long.MinValue;
        _shortcutInputRuntime = _inputRuntime;
        ApplyEditorShortcutBindings(_shortcutSettings);
    }

    internal string DescribeShortcutsJson()
    {
        var Bindings = new JsonArray();
        foreach (var (ActionId, Name, Shortcut) in _shortcutSettings.Bindings())
        {
            var Applied = _appliedShortcutBindings.TryGetValue(Name, out var Live);
            Bindings.Add(new JsonObject
            {
                ["name"] = Name,
                ["action"] = ActionId,
                ["gesture"] = Shortcut.Describe(),
                ["appliedGesture"] = Applied ? EngineShortcut.DescribeOf(Live!) : null,
                ["control"] = Applied ? Live!.Control : null,
                ["modifiers"] = new JsonArray(Applied ? Live!.Modifiers
                    .Select(Control => (JsonNode?)JsonValue.Create(Control)).ToArray() :
                    Array.Empty<JsonNode?>()),
                ["bound"] = Applied,
            });
        }
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["settingsId"] = EditorShortcutSettings.SettingsId,
            ["revision"] = _appliedShortcutRevision,
            ["contextId"] = EditorShortcutSettings.EditorContextId,
            ["bindings"] = Bindings,
            ["navigation"] = new JsonObject
            {
                ["forward"] = _shortcutSettings.NavigateForward.ToString(),
                ["back"] = _shortcutSettings.NavigateBack.ToString(),
                ["left"] = _shortcutSettings.NavigateLeft.ToString(),
                ["right"] = _shortcutSettings.NavigateRight.ToString(),
                ["up"] = _shortcutSettings.NavigateUp.ToString(),
                ["down"] = _shortcutSettings.NavigateDown.ToString(),
                ["forwardAlternate"] = _shortcutSettings.NavigateForwardAlternate.ToString(),
                ["backAlternate"] = _shortcutSettings.NavigateBackAlternate.ToString(),
                ["leftAlternate"] = _shortcutSettings.NavigateLeftAlternate.ToString(),
                ["rightAlternate"] = _shortcutSettings.NavigateRightAlternate.ToString(),
                ["speedModifier"] = _shortcutSettings.NavigateSpeedModifier.ToString(),
            },
            ["diagnostics"] = new JsonArray(_shortcutDiagnostics
                .Select(Value => (JsonNode?)JsonValue.Create(Value)).ToArray()),
            // Shortcuts are part of the input route: the focused host's scope consumes
            // first, then the same action bubbles to the global editor handlers.
            ["routing"] = new JsonObject
            {
                ["route"] = InputRoute.Editor.ToString(),
                ["order"] = new JsonArray("focusedHostScope", "globalEditor"),
                ["scopes"] = new JsonArray(AssetOpeners.DescribeShortcutScopes()
                    .Select(Scope => (JsonNode?)Scope).ToArray()),
            },
        }.ToJsonString();
    }

    private EditorShortcutSettings ReadEngineShortcutSettings()
    {
        if (_developmentSettings is null)
        {
            return new EditorShortcutSettings();
        }
        try
        {
            return (EditorShortcutSettings)_developmentSettings.Get(
                typeof(EditorShortcutSettings));
        }
        catch (KeyNotFoundException)
        {
            return new EditorShortcutSettings();
        }
    }

    private void ApplyEditorShortcutBindings(EditorShortcutSettings Settings)
    {
        var Diagnostics = new List<string>();
        var Bindings = new List<InputBindingDefinition>();
        var Applied = new Dictionary<string, InputBindingDefinition>(StringComparer.Ordinal);
        var Gestures = new Dictionary<string, string>(StringComparer.Ordinal);
        var Defaults = new EditorShortcutSettings();
        foreach (var (ActionId, Name, Shortcut) in Settings.Bindings())
        {
            var Binding = Shortcut.ToBinding(ActionId);
            if (Binding is null)
            {
                continue;
            }
            var Gesture = Shortcut.Describe();
            if (Gestures.TryGetValue(Gesture, out var Owner) &&
                !string.Equals(Owner, Name, StringComparison.Ordinal))
            {
                Diagnostics.Add($"SESHORTCUT_DUPLICATE [{Name}] '{Gesture}' is already used by '{Owner}'");
                // A rejected rebind keeps the gesture that was already live instead of
                // silently deleting the shortcut. A cold start falls back to the shipped
                // default so a saved duplicate cannot leave the action unbound.
                var Fallback = _appliedShortcutBindings.TryGetValue(Name, out var Previous)
                    ? Previous
                    : Defaults.Bindings().First(Value => Value.Name == Name)
                        .Shortcut.ToBinding(ActionId);
                if (Fallback is not null && Gestures.TryAdd(
                        EngineShortcut.DescribeOf(Fallback), Name))
                {
                    Applied[Name] = Fallback;
                    Bindings.Add(Fallback);
                }
                continue;
            }
            Gestures[Gesture] = Name;
            Applied[Name] = Binding;
            Bindings.Add(Binding);
        }
        if (_inputRuntime is InputRuntimeService Service)
        {
            if (!Service.SetContextBindings(EditorShortcutSettings.EditorContextId, Bindings,
                    out var Error))
            {
                Diagnostics.Add($"SESHORTCUT_REJECTED [{EditorShortcutSettings.EditorContextId}] {Error}");
            }
            else
            {
                _appliedShortcutBindings = Applied;
                SharpLog.Info("Shortcuts", "Applied engine shortcuts",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["contextId"] = EditorShortcutSettings.EditorContextId,
                        ["bindings"] = string.Join(", ", Settings.Bindings()
                            .Where(Value => Value.Shortcut.IsBound)
                            .Select(Value => Value.Name + "=" + Value.Shortcut.Describe())),
                        ["diagnostics"] = Diagnostics.Count.ToString(),
                    });
            }
        }
        _shortcutDiagnostics = Diagnostics;
        foreach (var Diagnostic in Diagnostics)
        {
            SharpLog.Warning("Shortcuts", Diagnostic);
        }
        MarkExtensionUiDirty();
    }

    private bool IsPressedShortcut(EngineShortcutKey Key)
    {
        if (Key == EngineShortcutKey.None)
        {
            return false;
        }
        return _pressedKeys.Contains(EngineShortcutKeys.VirtualKey(Key));
    }

    private string BuildNavigationHelp()
    {
        var Settings = _shortcutSettings;
        string Label(EngineShortcutKey Key) => EngineShortcutKeys.KeyLabel(Key);
        return $"{Label(Settings.NavigateForward)}{Label(Settings.NavigateLeft)}" +
            $"{Label(Settings.NavigateBack)}{Label(Settings.NavigateRight)}/" +
            $"{Label(Settings.NavigateUp)}{Label(Settings.NavigateDown)} Move · " +
            "RMB Orbit · MMB Pan · Wheel Zoom";
    }
}
