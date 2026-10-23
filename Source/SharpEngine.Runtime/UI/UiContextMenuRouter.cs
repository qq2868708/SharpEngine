using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpEngine.UI;

public sealed record UiContextMenuEvent(string HostId, int X, int Y, string Payload);

/// <summary>Routes context requests through UI scopes in target-to-root layout order.</summary>
public sealed class UiContextMenuRouter
{
    private readonly int OwnerThread = Environment.CurrentManagedThreadId;
    private readonly Dictionary<string, Func<UiContextMenuEvent, bool>> Handlers = new(StringComparer.Ordinal);
    private readonly List<Func<string, string, bool>> CompatibilityHandlers = [];
    private ulong Revision;
    private string? Diagnostic;

    public IDisposable Register(string ScopeId, Func<UiContextMenuEvent, bool> Handler)
    {
        CheckThread();
        ArgumentException.ThrowIfNullOrWhiteSpace(ScopeId);
        ArgumentNullException.ThrowIfNull(Handler);
        if (!Handlers.TryAdd(ScopeId, Handler))
        {
            throw new InvalidOperationException("UI_CONTEXT_SCOPE_DUPLICATE: " + ScopeId);
        }
        Revision++;
        return new Registration(() =>
        {
            CheckThread();
            var Found = Handlers.TryGetValue(ScopeId, out var Current);
            if (Found && ReferenceEquals(Current, Handler))
            {
                Handlers.Remove(ScopeId);
                Revision++;
            }
        });
    }

    // Compatibility adapters are owned by their feature, not by the editor dispatcher.
    internal IDisposable RegisterCompatibility(Func<string, string, bool> Handler)
    {
        CheckThread();
        CompatibilityHandlers.Add(Handler);
        return new Registration(() =>
        {
            CheckThread();
            CompatibilityHandlers.Remove(Handler);
        });
    }

    public bool Bubble(IEnumerable<string> Route, UiContextMenuEvent Event)
    {
        CheckThread();
        var Scopes = Route.ToArray();
        if (Scopes.Length > 64 || Scopes.Any(string.IsNullOrWhiteSpace) ||
            Scopes.Distinct(StringComparer.Ordinal).Count() != Scopes.Length)
        {
            throw new ArgumentException("UI_CONTEXT_ROUTE_INVALID", nameof(Route));
        }
        foreach (var ScopeId in Scopes)
        {
            var Found = Handlers.TryGetValue(ScopeId, out var Handler);
            if (Found && Handler!(Event))
            {
                return true;
            }
        }
        return false;
    }

    public bool TryHandleAction(string HostId, string Action)
    {
        CheckThread();
        const string Prefix = "ui.context-menu:";
        if (!Action.StartsWith(Prefix, StringComparison.Ordinal))
        {
            foreach (var Handler in CompatibilityHandlers.ToArray())
            {
                if (CompatibilityHandlers.Contains(Handler) && Handler(HostId, Action))
                {
                    return true;
                }
            }
            return false;
        }
        try
        {
            var Request = JsonNode.Parse(Action[Prefix.Length..])!.AsObject();
            var Route = Request["route"]!.AsArray().Select(Node => Node!.GetValue<string>()).ToArray();
            var Event = new UiContextMenuEvent(HostId, Request["x"]!.GetValue<int>(),
                Request["y"]!.GetValue<int>(), Request["payload"]!.GetValue<string>());
            Diagnostic = Bubble(Route, Event) ? null : "UI_CONTEXT_UNHANDLED";
        }
        catch (Exception Error) when (Error is JsonException or InvalidOperationException or ArgumentException or NullReferenceException or FormatException)
        {
            Diagnostic = "UI_CONTEXT_REQUEST_INVALID";
        }
        Revision++;
        return true;
    }

    public JsonObject Describe()
    {
        CheckThread();
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["stateRevision"] = Revision,
            ["scopes"] = new JsonArray(Handlers.Keys.Order(StringComparer.Ordinal)
                .Select(Id => (JsonNode?)JsonValue.Create(Id)).ToArray()),
            ["diagnostic"] = Diagnostic,
        };
    }

    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != OwnerThread)
        {
            throw new InvalidOperationException("UI context routing requires the UI owner thread");
        }
    }

    private sealed class Registration(Action Remove) : IDisposable
    {
        private Action? Release = Remove;
        public void Dispose()
        {
            var Action = Release;
            Action?.Invoke();
            Release = null;
        }
    }
}
