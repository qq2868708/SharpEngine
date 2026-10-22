using SharpEngine.Input;
using SharpEngine.Settings;

namespace SharpEngine.Editor;

/// <summary>Keys the engine accepts in editor shortcut mappings. Values map to Win32
/// virtual keys and to the runtime input control identifiers.</summary>
public enum EngineShortcutKey
{
    None = 0,
    A = 0x41,
    B = 0x42,
    C = 0x43,
    D = 0x44,
    E = 0x45,
    F = 0x46,
    G = 0x47,
    H = 0x48,
    I = 0x49,
    J = 0x4A,
    K = 0x4B,
    L = 0x4C,
    M = 0x4D,
    N = 0x4E,
    O = 0x4F,
    P = 0x50,
    Q = 0x51,
    R = 0x52,
    S = 0x53,
    T = 0x54,
    U = 0x55,
    V = 0x56,
    W = 0x57,
    X = 0x58,
    Y = 0x59,
    Z = 0x5A,
    Digit0 = 0x30,
    Digit1 = 0x31,
    Digit2 = 0x32,
    Digit3 = 0x33,
    Digit4 = 0x34,
    Digit5 = 0x35,
    Digit6 = 0x36,
    Digit7 = 0x37,
    Digit8 = 0x38,
    Digit9 = 0x39,
    F1 = 0x70,
    F2 = 0x71,
    F3 = 0x72,
    F4 = 0x73,
    F5 = 0x74,
    F6 = 0x75,
    F7 = 0x76,
    F8 = 0x77,
    F9 = 0x78,
    F10 = 0x79,
    F11 = 0x7A,
    F12 = 0x7B,
    Backspace = 0x08,
    Tab = 0x09,
    Enter = 0x0D,
    Shift = 0x10,
    Control = 0x11,
    Alt = 0x12,
    Escape = 0x1B,
    Space = 0x20,
    Left = 0x25,
    Up = 0x26,
    Right = 0x27,
    Down = 0x28,
    Delete = 0x2E,
}

/// <summary>One configurable engine shortcut: a primary key plus exact modifiers.</summary>
public sealed class EngineShortcut
{
    [Setting("key", DisplayName = "Key", Description = "Primary key. None leaves the shortcut unbound.", Order = 100)]
    public EngineShortcutKey Key { get; init; }

    [Setting("control", DisplayName = "Ctrl", Order = 200)]
    public bool Control { get; init; }

    [Setting("shift", DisplayName = "Shift", Order = 300)]
    public bool Shift { get; init; }

    [Setting("alt", DisplayName = "Alt", Order = 400)]
    public bool Alt { get; init; }

    public bool IsBound => Key != EngineShortcutKey.None;

    public static EngineShortcut Of(EngineShortcutKey Key, bool Control = false,
        bool Shift = false, bool Alt = false)
    {
        return new EngineShortcut
        {
            Key = Key,
            Control = Control,
            Shift = Shift,
            Alt = Alt,
        };
    }

    /// <summary>Runtime input control identifier, for example <c>Keyboard/F5</c>.</summary>
    public string ControlId => EngineShortcutKeys.ControlId(Key);

    public IReadOnlyList<string> ModifierControls => EngineShortcutKeys.ModifierControls(Control, Shift, Alt);

    /// <summary>Human-readable gesture in the settings and diagnostics text form.</summary>
    public string Describe()
    {
        if (!IsBound)
        {
            return "Unbound";
        }
        var Parts = new List<string>(4);
        if (Control)
        {
            Parts.Add("Ctrl");
        }
        if (Shift)
        {
            Parts.Add("Shift");
        }
        if (Alt)
        {
            Parts.Add("Alt");
        }
        Parts.Add(EngineShortcutKeys.KeyLabel(Key));
        return string.Join('+', Parts);
    }

    /// <summary>Resolves the runtime binding back to its gesture text.</summary>
    public static string DescribeOf(InputBindingDefinition Binding)
    {
        var Parts = new List<string>(4);
        if (Binding.Modifiers.Contains("Keyboard/Control", StringComparer.Ordinal))
        {
            Parts.Add("Ctrl");
        }
        if (Binding.Modifiers.Contains("Keyboard/Shift", StringComparer.Ordinal))
        {
            Parts.Add("Shift");
        }
        if (Binding.Modifiers.Contains("Keyboard/Alt", StringComparer.Ordinal))
        {
            Parts.Add("Alt");
        }
        Parts.Add(Binding.Control.StartsWith("Keyboard/", StringComparison.Ordinal)
            ? Binding.Control["Keyboard/".Length..] : Binding.Control);
        return string.Join('+', Parts);
    }

    /// <summary>Builds the runtime binding for this shortcut, or null when unbound.</summary>
    public InputBindingDefinition? ToBinding(string ActionId)
    {
        if (!IsBound)
        {
            return null;
        }
        return new InputBindingDefinition(ActionId, ControlId, 1, 0, ModifierControls, true);
    }
}

public static class EngineShortcutKeys
{
    public static string ControlId(EngineShortcutKey Key)
    {
        if (Key == EngineShortcutKey.None)
        {
            return string.Empty;
        }
        if (Key is >= EngineShortcutKey.A and <= EngineShortcutKey.Z)
        {
            return "Keyboard/" + (char)Key;
        }
        if (Key is >= EngineShortcutKey.Digit0 and <= EngineShortcutKey.Digit9)
        {
            return "Keyboard/" + (char)Key;
        }
        if (Key is >= EngineShortcutKey.F1 and <= EngineShortcutKey.F12)
        {
            return "Keyboard/F" + (Key - EngineShortcutKey.F1 + 1);
        }
        return Key switch
        {
            EngineShortcutKey.Backspace => "Keyboard/Backspace",
            EngineShortcutKey.Tab => "Keyboard/Tab",
            EngineShortcutKey.Enter => "Keyboard/Enter",
            EngineShortcutKey.Shift => "Keyboard/Shift",
            EngineShortcutKey.Control => "Keyboard/Control",
            EngineShortcutKey.Alt => "Keyboard/Alt",
            EngineShortcutKey.Escape => "Keyboard/Escape",
            EngineShortcutKey.Space => "Keyboard/Space",
            EngineShortcutKey.Left => "Keyboard/Left",
            EngineShortcutKey.Up => "Keyboard/Up",
            EngineShortcutKey.Right => "Keyboard/Right",
            EngineShortcutKey.Down => "Keyboard/Down",
            EngineShortcutKey.Delete => "Keyboard/Delete",
            _ => throw new InvalidDataException($"Unsupported engine shortcut key '{Key}'"),
        };
    }

    public static string KeyLabel(EngineShortcutKey Key)
    {
        return Key switch
        {
            EngineShortcutKey.Digit0 or EngineShortcutKey.Digit1 or EngineShortcutKey.Digit2 or
                EngineShortcutKey.Digit3 or EngineShortcutKey.Digit4 or EngineShortcutKey.Digit5 or
                EngineShortcutKey.Digit6 or EngineShortcutKey.Digit7 or EngineShortcutKey.Digit8 or
                EngineShortcutKey.Digit9 => ((char)Key).ToString(),
            _ => Key.ToString(),
        };
    }

    public static IReadOnlyList<string> ModifierControls(bool Control = false,
        bool Shift = false, bool Alt = false)
    {
        var Controls = new List<string>(3);
        if (Control)
        {
            Controls.Add("Keyboard/Control");
        }
        if (Shift)
        {
            Controls.Add("Keyboard/Shift");
        }
        if (Alt)
        {
            Controls.Add("Keyboard/Alt");
        }
        return Controls;
    }

    /// <summary>Win32 virtual key used by viewport navigation key-state tracking.</summary>
    public static uint VirtualKey(EngineShortcutKey Key) => (uint)Key;
}

/// <summary>Engine-owned editor shortcuts. This page is the single source of truth for
/// every keyboard mapping the editor supports; edits rebind the live input context.</summary>
[DevelopmentSettings(SettingsId, SettingsScope.Engine, 1,
    DisplayName = "Shortcuts", Category = "Editor", Order = 110)]
public sealed class EditorShortcutSettings : DevelopmentSettings
{
    public const string SettingsId = "sharpengine.editor-shortcuts";
    public const string EditorContextId = "engine://input/contexts/editor";

    /// <summary>Handler priorities on the Editor route form the shortcut bubbling order:
    /// a focused document scope consumes first, then the gesture falls through to the
    /// global editor handlers.</summary>
    public const int ScopedPriority = 1000;
    public const int GlobalPriority = 0;
    public const string PlayPauseActionId = "engine://input/actions/editor.play-pause";
    public const string StopActionId = "engine://input/actions/editor.stop";
    public const string SaveActionId = "engine://input/actions/editor.save";
    public const string UndoActionId = "engine://input/actions/editor.undo";
    public const string RedoActionId = "engine://input/actions/editor.redo";
    public const string ReleasePointerActionId = "engine://input/actions/player.release-pointer";

    [Setting("playPause", DisplayName = "Play / Pause",
        Description = "Start or pause simulation.", Order = 100)]
    public EngineShortcut PlayPause { get; init; } = EngineShortcut.Of(EngineShortcutKey.F5);

    [Setting("stop", DisplayName = "Stop",
        Description = "Stop simulation and restore the Authoring world.", Order = 200)]
    public EngineShortcut Stop { get; init; } = EngineShortcut.Of(EngineShortcutKey.F8);

    [Setting("save", DisplayName = "Save",
        Description = "Save the focused document: the active asset editor first, otherwise the World.",
        Order = 250)]
    public EngineShortcut Save { get; init; } =
        EngineShortcut.Of(EngineShortcutKey.S, Control: true);

    [Setting("undo", DisplayName = "Undo",
        Description = "Undo the last Authoring transaction.", Order = 300)]
    public EngineShortcut Undo { get; init; } =
        EngineShortcut.Of(EngineShortcutKey.Z, Control: true);

    [Setting("redo", DisplayName = "Redo",
        Description = "Redo the next Authoring transaction.", Order = 400)]
    public EngineShortcut Redo { get; init; } =
        EngineShortcut.Of(EngineShortcutKey.Y, Control: true);

    [Setting("redoAlternate", DisplayName = "Redo (Shift)",
        Description = "Alternative Redo gesture.", Order = 500)]
    public EngineShortcut RedoAlternate { get; init; } =
        EngineShortcut.Of(EngineShortcutKey.Z, Control: true, Shift: true);

    [Setting("releasePointer", DisplayName = "Release Pointer",
        Description = "Release gameplay pointer capture while Playing.",
        Order = 600)]
    public EngineShortcut ReleasePointer { get; init; } =
        EngineShortcut.Of(EngineShortcutKey.Escape);

    [Setting("navigateForward", DisplayName = "Viewport Forward",
        Description = "Viewport navigation key while the viewport owns focus.", Order = 700)]
    public EngineShortcutKey NavigateForward { get; init; } = EngineShortcutKey.W;

    [Setting("navigateBack", DisplayName = "Viewport Back", Order = 710)]
    public EngineShortcutKey NavigateBack { get; init; } = EngineShortcutKey.S;

    [Setting("navigateLeft", DisplayName = "Viewport Left", Order = 720)]
    public EngineShortcutKey NavigateLeft { get; init; } = EngineShortcutKey.A;

    [Setting("navigateRight", DisplayName = "Viewport Right", Order = 730)]
    public EngineShortcutKey NavigateRight { get; init; } = EngineShortcutKey.D;

    [Setting("navigateUp", DisplayName = "Viewport Up", Order = 740)]
    public EngineShortcutKey NavigateUp { get; init; } = EngineShortcutKey.E;

    [Setting("navigateDown", DisplayName = "Viewport Down", Order = 750)]
    public EngineShortcutKey NavigateDown { get; init; } = EngineShortcutKey.Q;

    [Setting("navigateForwardAlternate", DisplayName = "Viewport Forward (Alt)",
        Description = "Second navigation key. None disables it.", Order = 800)]
    public EngineShortcutKey NavigateForwardAlternate { get; init; } = EngineShortcutKey.Up;

    [Setting("navigateBackAlternate", DisplayName = "Viewport Back (Alt)", Order = 810)]
    public EngineShortcutKey NavigateBackAlternate { get; init; } = EngineShortcutKey.Down;

    [Setting("navigateLeftAlternate", DisplayName = "Viewport Left (Alt)", Order = 820)]
    public EngineShortcutKey NavigateLeftAlternate { get; init; } = EngineShortcutKey.Left;

    [Setting("navigateRightAlternate", DisplayName = "Viewport Right (Alt)", Order = 830)]
    public EngineShortcutKey NavigateRightAlternate { get; init; } = EngineShortcutKey.Right;

    [Setting("navigateSpeedModifier", DisplayName = "Viewport Speed Modifier",
        Description = "Held key that boosts viewport navigation speed.", Order = 840)]
    public EngineShortcutKey NavigateSpeedModifier { get; init; } = EngineShortcutKey.Shift;

    /// <summary>Editor-route action bindings in deterministic declaration order.</summary>
    public IEnumerable<(string ActionId, string Name, EngineShortcut Shortcut)> Bindings()
    {
        yield return (PlayPauseActionId, "playPause", PlayPause);
        yield return (StopActionId, "stop", Stop);
        yield return (SaveActionId, "save", Save);
        yield return (UndoActionId, "undo", Undo);
        yield return (RedoActionId, "redo", Redo);
        yield return (RedoActionId, "redoAlternate", RedoAlternate);
        yield return (ReleasePointerActionId, "releasePointer", ReleasePointer);
    }
}
