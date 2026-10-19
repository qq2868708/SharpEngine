using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SharpEngine.Assets;
using SharpEngine.Logging;
using SharpEngine.Modules;

namespace SharpEngine.Input;

internal enum NativeInputEventKind : uint
{
    KeyDown,
    KeyUp,
    PointerMove,
    PointerDown,
    PointerUp,
    Wheel,
    FocusLost,
    CaptureLost,
}

internal sealed class InputRuntimeService : IInputRuntime
{
    private const int RecentEventLimit = 64;
    private static readonly Regex IdPattern = new(
        "^(engine|project)://input/(actions|contexts)/[a-z0-9][a-z0-9._/-]*$",
        RegexOptions.CultureInvariant);
    private static readonly Regex ControlPattern = new(
        "^(Keyboard|Mouse|Gamepad)/[A-Za-z0-9._-]+$",
        RegexOptions.CultureInvariant);
    private static readonly HashSet<string> SupportedControls = Enumerable.Range(1, 255)
        .Select(key => KeyControl((uint)key)).Concat(new[] { "Mouse/Left", "Mouse/Right",
            "Mouse/Middle", "Mouse/Button3", "Mouse/Button4", "Mouse/Delta", "Mouse/Position", "Mouse/Wheel" })
        .ToHashSet(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new(
        JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IReadOnlyList<InputActionDefinition> _actions;
    private readonly IReadOnlyList<InputMappingContextDefinition> _contexts;
    private readonly Dictionary<string, InputActionDefinition> _actionsById;
    private readonly Dictionary<string, InputMappingContextDefinition> _contextsById;
    private readonly Dictionary<string, int> _activationCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<InputRoute, bool> _routeEnabled = new()
    {
        [InputRoute.Editor] = true,
        [InputRoute.RuntimeUi] = false,
        [InputRoute.Gameplay] = false,
    };
    private readonly Dictionary<string, InputActionValue> _controls =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _uiSuppressedControls = new(StringComparer.Ordinal);
    private readonly HashSet<string> _gameSuppressedControls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _capturedControls =
        new(StringComparer.Ordinal);
    private readonly Dictionary<(string Context, string Action), (InputDeviceKind Device, string Control, string Host)> _sources = new();
    private readonly int _ownerThread = Environment.CurrentManagedThreadId;
    private bool _evaluating;
    private readonly Dictionary<(string Context, string Action), InputActionValue> _values = new();
    private readonly Dictionary<(InputRoute Route, string Action), List<HandlerRegistration>>
        _handlers = new();
    private readonly Queue<InputActionEvent> _recentEvents = new();
    private readonly List<InputDiagnostic> _diagnostics = new();
    private long _nextRegistration;
    private ulong _frameId;
    private ulong _sequence;
    private ulong _stateRevision = 1;
    private int _pointerX;
    private int _pointerY;
    private string _lastHostId = "main";

    private InputRuntimeService(IReadOnlyList<InputActionDefinition> actions,
        IReadOnlyList<InputMappingContextDefinition> contexts)
    {
        _actions = Array.AsReadOnly(actions.OrderBy(value => value.Id, StringComparer.Ordinal).ToArray());
        _contexts = Array.AsReadOnly(contexts.OrderBy(value => value.Id, StringComparer.Ordinal)
            .Select(context => context with { Bindings = Array.AsReadOnly(context.Bindings.Select(binding =>
                binding with { Modifiers = Array.AsReadOnly(binding.Modifiers.ToArray()) }).ToArray()) }).ToArray());
        _actionsById = _actions.ToDictionary(value => value.Id, StringComparer.Ordinal);
        _contextsById = _contexts.ToDictionary(value => value.Id, StringComparer.Ordinal);
        foreach (var context in _contexts.Where(value => value.ActivateByDefault))
            _activationCounts[context.Id] = 1;
    }

    public IReadOnlyList<InputActionDefinition> Actions => _actions;
    public IReadOnlyList<InputMappingContextDefinition> MappingContexts => _contexts;

    internal static InputRuntimeService Load(AssetRegistry assets)
    {
        ArgumentNullException.ThrowIfNull(assets);
        var actions = assets.Assets
            .Where(asset => string.Equals(asset.Type, "InputAction",
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(asset => asset.Id, StringComparer.Ordinal)
            .Select(asset => LoadAction(assets, asset)).ToArray();
        var actionIds = actions.Select(value => value.Id).ToHashSet(StringComparer.Ordinal);
        var contexts = assets.Assets
            .Where(asset => string.Equals(asset.Type, "InputMappingContext",
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(asset => asset.Id, StringComparer.Ordinal)
            .Select(asset => LoadContext(assets, asset, actionIds)).ToArray();
        return new InputRuntimeService(actions, contexts);
    }

    internal static InputRuntimeService CreateForValidation(
        IReadOnlyList<InputActionDefinition> actions,
        IReadOnlyList<InputMappingContextDefinition> contexts) =>
        new(actions, contexts);

    public IDisposable BindAction(InputRoute route, string actionId, int priority,
        Func<InputActionEvent, bool> handler)
    {
        CheckThread();
        ArgumentNullException.ThrowIfNull(handler);
        if (!_actionsById.ContainsKey(actionId))
            throw new ArgumentException($"Unknown InputAction '{actionId}'", nameof(actionId));
        var key = (route, actionId);
        var owner = ModuleRegistrationScope.Current;
        var registration = new HandlerRegistration(priority, ++_nextRegistration,
            owner is null ? handler : input => { using var entered = owner.Enter(); return handler(input); });
        if (!_handlers.TryGetValue(key, out var handlers))
            _handlers.Add(key, handlers = new List<HandlerRegistration>());
        handlers.Add(registration);
        handlers.Sort(HandlerRegistration.Compare);
        _stateRevision++;
        return ModuleRegistrationScope.Track(new Registration(() =>
        {
            CheckThread();
            if (_handlers.TryGetValue(key, out var current) && current.Remove(registration))
            {
                if (current.Count == 0) _handlers.Remove(key);
                _stateRevision++;
            }
        }));
    }

    public IDisposable ActivateContext(string contextId)
    {
        CheckThread();
        if (!_contextsById.ContainsKey(contextId))
            throw new ArgumentException($"Unknown InputMappingContext '{contextId}'",
                nameof(contextId));
        _activationCounts.TryGetValue(contextId, out var count);
        _activationCounts[contextId] = count + 1;
        _stateRevision++;
        return ModuleRegistrationScope.Track(new Registration(() =>
        {
            CheckThread();
            if (!_activationCounts.TryGetValue(contextId, out var current)) return;
            if (current <= 1) _activationCounts.Remove(contextId);
            else _activationCounts[contextId] = current - 1;
            _stateRevision++;
            EvaluateAll("Context/Deactivated", InputDeviceKind.Keyboard);
        }));
    }

    public bool IsContextActive(string contextId) =>
        _activationCounts.TryGetValue(contextId, out var count) && count > 0;

    internal void SetRouteEnabled(InputRoute route, bool enabled)
    {
        CheckThread();
        if (_routeEnabled[route] == enabled) return;
        _routeEnabled[route] = enabled;
        _stateRevision++;
        EvaluateAll("Route/Changed", InputDeviceKind.Keyboard);
    }

    internal bool ProcessNative(string hostId, NativeInputEventKind kind, uint code,
        int x, int y, float valueX, float valueY, uint modifiers, bool uiHandled,
        bool viewportFocused)
    {
        CheckThread();
        if (_evaluating) throw new InvalidOperationException("Recursive input dispatch is not supported");
        _lastHostId = string.IsNullOrWhiteSpace(hostId) ? "main" : hostId;
        if (kind is NativeInputEventKind.PointerMove or NativeInputEventKind.PointerDown or
            NativeInputEventKind.PointerUp or NativeInputEventKind.Wheel)
        { _pointerX = x; _pointerY = y; }
        if (kind == NativeInputEventKind.FocusLost)
        {
            _controls.Clear();
            _uiSuppressedControls.Clear();
            _gameSuppressedControls.Clear();
            _capturedControls.Clear();
            _stateRevision++;
            return EvaluateAll("Window/Focus", InputDeviceKind.Keyboard);
        }
        if (kind == NativeInputEventKind.CaptureLost)
        {
            foreach (var mouseControl in _controls.Keys.Where(value =>
                         value.StartsWith("Mouse/", StringComparison.Ordinal)).ToArray())
                _controls.Remove(mouseControl);
            _uiSuppressedControls.RemoveWhere(value =>
                value.StartsWith("Mouse/", StringComparison.Ordinal));
            _gameSuppressedControls.RemoveWhere(value => value.StartsWith("Mouse/", StringComparison.Ordinal));
            foreach (var mouseControl in _capturedControls.Keys.Where(value =>
                         value.StartsWith("Mouse/", StringComparison.Ordinal)).ToArray())
                _capturedControls.Remove(mouseControl);
            _stateRevision++;
            return EvaluateAll("Mouse/Capture", InputDeviceKind.Mouse);
        }

        var (control, device, value, isDown, isUp, transient) = kind switch
        {
            NativeInputEventKind.KeyDown => (KeyControl(code), InputDeviceKind.Keyboard,
                new InputActionValue(1), true, false, false),
            NativeInputEventKind.KeyUp => (KeyControl(code), InputDeviceKind.Keyboard,
                new InputActionValue(), false, true, false),
            NativeInputEventKind.PointerDown => (MouseButtonControl(code), InputDeviceKind.Mouse,
                new InputActionValue(1), true, false, false),
            NativeInputEventKind.PointerUp => (MouseButtonControl(code), InputDeviceKind.Mouse,
                new InputActionValue(), false, true, false),
            NativeInputEventKind.PointerMove => ("Mouse/Delta", InputDeviceKind.Mouse,
                new InputActionValue(valueX, valueY), false, false, true),
            NativeInputEventKind.Wheel => ("Mouse/Wheel", InputDeviceKind.Mouse,
                new InputActionValue(valueX, valueY), false, false, true),
            _ => (string.Empty, InputDeviceKind.Keyboard, new InputActionValue(),
                false, false, false),
        };
        if (control.Length == 0) return false;

        // RmlUi and deterministic injection use the same modifier bit layout.
        foreach (var (name, mask) in new[] { ("Keyboard/Shift", 1u),
                     ("Keyboard/Control", 2u), ("Keyboard/Alt", 4u) })
            if (name != control)
                _controls[name] = new InputActionValue((modifiers & mask) != 0 ? 1 : 0);

        if (isDown || transient)
        {
            if (uiHandled) _uiSuppressedControls.Add(control);
            if (!viewportFocused) _gameSuppressedControls.Add(control);
        }
        if (kind == NativeInputEventKind.PointerDown && uiHandled)
            foreach (var heldControl in _controls.Where(pair => !pair.Value.IsZero).Select(pair => pair.Key))
                _uiSuppressedControls.Add(heldControl);
        _controls[control] = value;
        if (kind == NativeInputEventKind.PointerMove)
        {
            _controls["Mouse/Position"] = new InputActionValue(x, y);
            if (uiHandled) _uiSuppressedControls.Add("Mouse/Position");
            else _uiSuppressedControls.Remove("Mouse/Position");
            if (!viewportFocused) _gameSuppressedControls.Add("Mouse/Position");
            else _gameSuppressedControls.Remove("Mouse/Position");
        }
        var handled = EvaluateAll(control, device, true);
        if (isUp)
        {
            _uiSuppressedControls.Remove(control);
            _gameSuppressedControls.Remove(control);
            _capturedControls.Remove(control);
            handled |= EvaluateAll(control, device);
        }
        if (transient)
        {
            _uiSuppressedControls.Remove(control);
            _gameSuppressedControls.Remove(control);
            _controls[control] = default;
            handled |= EvaluateAll(control, device);
            _capturedControls.Remove(control);
            if (kind == NativeInputEventKind.PointerMove) _capturedControls.Remove("Mouse/Position");
        }
        _stateRevision++;
        return handled || uiHandled || _capturedControls.ContainsKey(control) ||
            _uiSuppressedControls.Contains(control);
    }

    internal void AdvanceFrame(float deltaSeconds)
    {
        CheckThread();
        if (!float.IsFinite(deltaSeconds) || deltaSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(deltaSeconds));
        _frameId++;
        _stateRevision++;
        foreach (var context in ActiveContexts().ToArray())
        {
            foreach (var actionId in context.Bindings.Select(value => value.ActionId)
                         .Distinct(StringComparer.Ordinal).OrderBy(value => value,
                             StringComparer.Ordinal))
            {
                var key = (context.Id, actionId);
                if (!_values.TryGetValue(key, out var value) || value.IsZero ||
                    !_actionsById[actionId].Continuous) continue;
                var source = _sources[key];
                _ = Dispatch(context, _actionsById[actionId], InputActionPhase.Performed,
                    value, source.Device, source.Control);
            }
        }
    }

    public string DescribeJson()
    {
        CheckThread();
        var snapshot = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["stateRevision"] = _stateRevision,
            ["frameId"] = _frameId,
            ["hostId"] = _lastHostId,
            ["pointer"] = new JsonObject { ["x"] = _pointerX, ["y"] = _pointerY },
            ["actions"] = JsonSerializer.SerializeToNode(_actions, SnapshotJsonOptions),
            ["contexts"] = new JsonArray(_contexts.Select(context => (JsonNode?)new JsonObject
            {
                ["schemaVersion"] = context.SchemaVersion,
                ["id"] = context.Id,
                ["route"] = context.Route.ToString(),
                ["priority"] = context.Priority,
                ["active"] = IsContextActive(context.Id),
                ["routeEnabled"] = _routeEnabled[context.Route],
                ["bindings"] = JsonSerializer.SerializeToNode(context.Bindings,
                    SnapshotJsonOptions),
            }).ToArray()),
            ["controls"] = new JsonArray(_controls.Where(value => !value.Value.IsZero)
                .OrderBy(value => value.Key, StringComparer.Ordinal)
                .Select(value => (JsonNode?)new JsonObject
                {
                    ["id"] = value.Key,
                    ["x"] = value.Value.X,
                    ["y"] = value.Value.Y,
                    ["uiSuppressed"] = _uiSuppressedControls.Contains(value.Key),
                }).ToArray()),
            ["recentEvents"] = JsonSerializer.SerializeToNode(_recentEvents.ToArray(),
                SnapshotJsonOptions),
            ["diagnostics"] = JsonSerializer.SerializeToNode(_diagnostics,
                SnapshotJsonOptions),
        };
        return snapshot.ToJsonString(new JsonSerializerOptions(JsonSerializerOptions.Default)
            { WriteIndented = true });
    }

    private bool EvaluateAll(string sourceControl, InputDeviceKind device,
        bool captureOnHandled = false)
    {
        if (_evaluating) return false;
        _evaluating = true;
        try
        {
        var handled = false;
        foreach (var context in _contexts.OrderBy(context => context.Route)
                     .ThenByDescending(context => context.Priority)
                     .ThenBy(context => context.Id, StringComparer.Ordinal).ToArray())
        {
            foreach (var actionId in context.Bindings.Select(value => value.ActionId)
                         .Distinct(StringComparer.Ordinal).OrderBy(value => value,
                             StringComparer.Ordinal))
            {
                var action = _actionsById[actionId];
                var key = (context.Id, actionId);
                _values.TryGetValue(key, out var before);
                var after = ValueFor(context, action);
                var actionHandled = false;
                var mayStart = context.Bindings.Any(binding =>
                        string.Equals(binding.ActionId, actionId, StringComparison.Ordinal) &&
                        (string.Equals(binding.Control, sourceControl, StringComparison.Ordinal) ||
                         sourceControl == "Mouse/Delta" && binding.Control == "Mouse/Position"));
                if (before.IsZero && !mayStart) after = default;
                _values[key] = after;
                if (before.IsZero && !after.IsZero)
                {
                    _sources[key] = (device, sourceControl, _lastHostId);
                    actionHandled |= Dispatch(context, action, InputActionPhase.Started,
                        after, device, sourceControl);
                    actionHandled |= Dispatch(context, action, InputActionPhase.Performed,
                        after, device, sourceControl);
                }
                else if (!before.IsZero && after.IsZero)
                    actionHandled |= Dispatch(context, action, InputActionPhase.Canceled,
                        after, device, sourceControl);
                else if (!after.IsZero && before != after)
                    actionHandled |= Dispatch(context, action, InputActionPhase.Performed,
                        after, device, sourceControl);
                handled |= actionHandled;
                if (actionHandled && captureOnHandled && !after.IsZero)
                {
                    foreach (var binding in context.Bindings.Where(binding => binding.ActionId == actionId))
                        if (_controls.TryGetValue(binding.Control, out var held) && !held.IsZero)
                            _capturedControls[binding.Control] = context.Id;
                    if (sourceControl == "Mouse/Delta")
                    {
                        _capturedControls["Mouse/Delta"] = context.Id;
                        _capturedControls["Mouse/Position"] = context.Id;
                    }
                }
            }
        }
        return handled;
        }
        finally { _evaluating = false; }
    }

    private InputActionValue ValueFor(InputMappingContextDefinition context,
        InputActionDefinition action)
    {
        if (!IsContextActive(context.Id) || !_routeEnabled[context.Route]) return default;
        var x = 0f;
        var y = 0f;
        foreach (var binding in context.Bindings.Where(value =>
                     string.Equals(value.ActionId, action.Id, StringComparison.Ordinal)))
        {
            if (_uiSuppressedControls.Contains(binding.Control) ||
                context.Route == InputRoute.Gameplay && _gameSuppressedControls.Contains(binding.Control) ||
                (_capturedControls.TryGetValue(binding.Control, out var captured) &&
                 IsBelow(context, _contextsById[captured])) ||
                binding.Modifiers.Any(modifier => !_controls.TryGetValue(modifier,
                    out var modifierValue) || modifierValue.IsZero) ||
                binding.ExactModifiers && !ExactModifiers(binding.Modifiers)) continue;
            if (!_controls.TryGetValue(binding.Control, out var value)) continue;
            if (action.ValueType == InputActionValueType.Axis2D)
            {
                if (binding.Control is "Mouse/Delta" or "Mouse/Wheel" or "Mouse/Position")
                {
                    x += value.X * binding.ScaleX;
                    y += value.Y * binding.ScaleY;
                }
                else
                {
                    x += value.X * binding.ScaleX;
                    y += value.X * binding.ScaleY;
                }
            }
            else x += (binding.Control == "Mouse/Wheel" ? value.Y : value.X) * binding.ScaleX;
        }
        return action.ValueType switch
        {
            InputActionValueType.Button => new InputActionValue(MathF.Abs(x) > 0.0001f ? 1 : 0),
            InputActionValueType.Axis1D => new InputActionValue(x),
            _ => new InputActionValue(x, y),
        };
    }

    private bool Dispatch(InputMappingContextDefinition context,
        InputActionDefinition action, InputActionPhase phase, InputActionValue value,
        InputDeviceKind device, string control)
    {
        if (_sources.TryGetValue((context.Id, action.Id), out var origin))
        { device = origin.Device; control = origin.Control; }
        var input = new InputActionEvent(_frameId, ++_sequence, origin.Host ?? _lastHostId, context.Id,
            action.Id, context.Route, phase, value, device,
            device == InputDeviceKind.Mouse ? "mouse:0" : "keyboard:0", control);
        _recentEvents.Enqueue(input);
        while (_recentEvents.Count > RecentEventLimit) _recentEvents.Dequeue();
        _stateRevision++;
        if (!_handlers.TryGetValue((context.Route, action.Id), out var handlers))
            return false;
        foreach (var registration in handlers.ToArray())
        {
            try
            {
                if (registration.Handler(input)) return true;
            }
            catch (Exception exception)
            {
                var diagnostic = new InputDiagnostic("SEINPUT_HANDLER_FAILED", "error",
                    action.Id, "handler", "a non-throwing input handler",
                    exception.GetType().Name, _stateRevision, true, exception.Message);
                _diagnostics.Add(diagnostic);
                if (_diagnostics.Count > RecentEventLimit) _diagnostics.RemoveAt(0);
                SharpLog.Error("Input", $"{diagnostic.Code} {action.Id}", exception,
                    new Dictionary<string, string>
                    {
                        ["code"] = diagnostic.Code,
                        ["action"] = action.Id,
                        ["stateRevision"] = _stateRevision.ToString(),
                    });
            }
        }
        return false;
    }

    private IEnumerable<InputMappingContextDefinition> ActiveContexts() => _contexts
        .Where(context => IsContextActive(context.Id) && _routeEnabled[context.Route])
        .OrderBy(context => context.Route)
        .ThenByDescending(context => context.Priority)
        .ThenBy(context => context.Id, StringComparer.Ordinal);

    private static bool IsBelow(InputMappingContextDefinition context,
        InputMappingContextDefinition owner) => context.Route > owner.Route ||
        context.Route == owner.Route && (context.Priority < owner.Priority ||
            context.Priority == owner.Priority && string.CompareOrdinal(context.Id, owner.Id) > 0);

    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _ownerThread)
            throw new InvalidOperationException("Input operations must run on the engine thread");
    }

    private bool ExactModifiers(IReadOnlyList<string> required)
    {
        var requiredSet = required.ToHashSet(StringComparer.Ordinal);
        foreach (var modifier in new[]
                 { "Keyboard/Control", "Keyboard/Shift", "Keyboard/Alt" })
        {
            var pressed = _controls.TryGetValue(modifier, out var value) && !value.IsZero;
            if (pressed != requiredSet.Contains(modifier)) return false;
        }
        return true;
    }

    private static InputActionDefinition LoadAction(AssetRegistry assets,
        AssetDescriptor asset)
    {
        using var document = OpenAsset(assets, asset);
        var root = document.RootElement;
        RequireProperties(root, asset.Id, "$schema", "schemaVersion", "actionId",
            "valueType", "continuous");
        var version = RequiredInt(root, "schemaVersion", asset.Id);
        if (version != 1) throw Error("SEINPUT_SCHEMA_UNSUPPORTED", asset.Id,
            $"InputAction schemaVersion must be 1, got {version}");
        var id = RequiredString(root, "actionId", asset.Id);
        ValidateAssetId(asset, id, "actions");
        var type = ParseEnum<InputActionValueType>(RequiredString(root, "valueType",
            asset.Id), asset.Id, "valueType");
        var continuous = RequiredBool(root, "continuous", asset.Id);
        return new InputActionDefinition(version, id, type, continuous);
    }

    private static InputMappingContextDefinition LoadContext(AssetRegistry assets,
        AssetDescriptor asset, HashSet<string> actionIds)
    {
        using var document = OpenAsset(assets, asset);
        var root = document.RootElement;
        RequireProperties(root, asset.Id, "$schema", "schemaVersion", "contextId",
            "route", "priority", "activateByDefault", "bindings");
        var version = RequiredInt(root, "schemaVersion", asset.Id);
        if (version != 1) throw Error("SEINPUT_SCHEMA_UNSUPPORTED", asset.Id,
            $"InputMappingContext schemaVersion must be 1, got {version}");
        var id = RequiredString(root, "contextId", asset.Id);
        ValidateAssetId(asset, id, "contexts");
        var route = ParseEnum<InputRoute>(RequiredString(root, "route", asset.Id),
            asset.Id, "route");
        var priority = RequiredInt(root, "priority", asset.Id);
        var activate = RequiredBool(root, "activateByDefault", asset.Id);
        if (!root.TryGetProperty("bindings", out var bindingsNode) ||
            bindingsNode.ValueKind != JsonValueKind.Array)
            throw Error("SEINPUT_FIELD_INVALID", asset.Id, "bindings must be an array");
        var bindings = new List<InputBindingDefinition>();
        foreach (var binding in bindingsNode.EnumerateArray())
        {
            RequireProperties(binding, asset.Id, "action", "control", "scaleX",
            "scaleY", "modifiers", "exactModifiers");
            var action = RequiredString(binding, "action", asset.Id);
            if (!actionIds.Contains(action)) throw Error("SEINPUT_ACTION_MISSING", asset.Id,
                $"Binding references unknown InputAction '{action}'");
            var control = RequiredString(binding, "control", asset.Id);
            ValidateControl(control, asset.Id);
            var scaleX = RequiredFloat(binding, "scaleX", asset.Id);
            var scaleY = RequiredFloat(binding, "scaleY", asset.Id);
            if (!binding.TryGetProperty("modifiers", out var modifiersNode) ||
                modifiersNode.ValueKind != JsonValueKind.Array)
                throw Error("SEINPUT_FIELD_INVALID", asset.Id,
                    "'modifiers' requires an array");
            var modifiers = modifiersNode.EnumerateArray().Select(value =>
                value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()! :
                throw Error("SEINPUT_FIELD_INVALID", asset.Id,
                    "modifier controls must be non-empty strings")).ToArray();
            foreach (var modifier in modifiers) ValidateControl(modifier, asset.Id);
            var exactModifiers = RequiredBool(binding, "exactModifiers", asset.Id);
            bindings.Add(new InputBindingDefinition(action, control, scaleX, scaleY,
                modifiers, exactModifiers));
        }
        return new InputMappingContextDefinition(version, id, route, priority,
            activate, bindings);
    }

    private static JsonDocument OpenAsset(AssetRegistry assets, AssetDescriptor asset)
    {
        var path = assets.ResolvePath(asset) ?? throw Error("SEINPUT_ASSET_MISSING",
            asset.Id, "Input asset has no resolvable source path");
        try { return JsonDocument.Parse(File.ReadAllText(path)); }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            throw Error("SEINPUT_ASSET_INVALID", asset.Id, exception.Message);
        }
    }

    private static void ValidateAssetId(AssetDescriptor asset, string id, string kind)
    {
        if (!string.Equals(asset.Id, id, StringComparison.Ordinal) ||
            !IdPattern.IsMatch(id) || !id.Contains($"/input/{kind}/",
                StringComparison.Ordinal))
            throw Error("SEINPUT_ID_INVALID", asset.Id,
                $"Serialized id '{id}' must exactly match asset id '{asset.Id}'");
    }

    private static void ValidateControl(string control, string assetId)
    {
        if (!ControlPattern.IsMatch(control) || !SupportedControls.Contains(control)) throw Error("SEINPUT_CONTROL_INVALID",
            assetId, $"Invalid input control '{control}'");
    }

    private static void RequireProperties(JsonElement value, string assetId,
        params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw Error("SEINPUT_FIELD_INVALID", assetId, "Expected a JSON object");
        var set = allowed.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!seen.Add(property.Name)) throw Error("SEINPUT_FIELD_DUPLICATE", assetId, property.Name);
            if (!set.Contains(property.Name))
                throw Error("SEINPUT_FIELD_UNKNOWN", assetId,
                    $"Unknown field '{property.Name}'");
        }
    }

    private static string RequiredString(JsonElement value, string property,
        string assetId) => value.TryGetProperty(property, out var element) &&
        element.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(element.GetString()) ? element.GetString()! :
        throw Error("SEINPUT_FIELD_INVALID", assetId,
            $"'{property}' requires a non-empty string");

    private static int RequiredInt(JsonElement value, string property,
        string assetId) => value.TryGetProperty(property, out var element) &&
        element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var result) ? result :
        throw Error("SEINPUT_FIELD_INVALID", assetId, $"'{property}' requires an integer");

    private static bool RequiredBool(JsonElement value, string property,
        string assetId) => value.TryGetProperty(property, out var element) &&
        element.ValueKind is JsonValueKind.True or JsonValueKind.False ?
        element.GetBoolean() : throw Error("SEINPUT_FIELD_INVALID", assetId,
            $"'{property}' requires a boolean");

    private static float RequiredFloat(JsonElement value, string property,
        string assetId)
    {
        if (!value.TryGetProperty(property, out var element) ||
            element.ValueKind != JsonValueKind.Number || !element.TryGetSingle(out var result) || !float.IsFinite(result))
            throw Error("SEINPUT_FIELD_INVALID", assetId,
                $"'{property}' requires a finite number");
        return result;
    }

    private static T ParseEnum<T>(string value, string assetId, string property)
        where T : struct, Enum => Enum.TryParse<T>(value, true, out var result) &&
        Enum.IsDefined(result) ? result : throw Error("SEINPUT_FIELD_INVALID", assetId,
            $"'{property}' has unsupported value '{value}'");

    private static InvalidDataException Error(string code, string objectId,
        string message) => new($"{code} [{objectId}] {message}");

    private static string KeyControl(uint key)
    {
        if (key is >= 0x41 and <= 0x5A) return $"Keyboard/{(char)key}";
        if (key is >= 0x30 and <= 0x39) return $"Keyboard/{(char)key}";
        if (key is >= 0x70 and <= 0x87) return $"Keyboard/F{key - 0x6F}";
        return key switch
        {
            0x08 => "Keyboard/Backspace",
            0x09 => "Keyboard/Tab",
            0x0D => "Keyboard/Enter",
            0x10 or 0xA0 or 0xA1 => "Keyboard/Shift",
            0x11 or 0xA2 or 0xA3 => "Keyboard/Control",
            0x12 or 0xA4 or 0xA5 => "Keyboard/Alt",
            0x1B => "Keyboard/Escape",
            0x20 => "Keyboard/Space",
            0x25 => "Keyboard/Left",
            0x26 => "Keyboard/Up",
            0x27 => "Keyboard/Right",
            0x28 => "Keyboard/Down",
            0x2E => "Keyboard/Delete",
            _ => $"Keyboard/VK{key:X2}",
        };
    }

    private static string MouseButtonControl(uint button) => button switch
    {
        0 => "Mouse/Left",
        1 => "Mouse/Right",
        2 => "Mouse/Middle",
        _ => $"Mouse/Button{button}",
    };

    private sealed record HandlerRegistration(int Priority, long Order,
        Func<InputActionEvent, bool> Handler)
    {
        internal static int Compare(HandlerRegistration left, HandlerRegistration right)
        {
            var priority = right.Priority.CompareTo(left.Priority);
            return priority != 0 ? priority : left.Order.CompareTo(right.Order);
        }
    }

    private sealed class Registration(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }

    private sealed record InputDiagnostic(string Code, string Severity,
        string ObjectId, string FieldPath, string Expected, string Actual,
        ulong StateRevision, bool Retryable, string Message);
}
