using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using SharpEngine.UI;
using SharpEngine.Logging;

namespace SharpEngine.Editor;

/// <summary>One command popup per native host. No asset or browser-specific behavior.</summary>
internal sealed class EditorPopupMenu
{
    private sealed record CommandBinding(EditorCommand Command, EditorCommandRegistry Registry);

    private sealed record Session(string Token, EditorCommandContext Context,
        Dictionary<string, CommandBinding> Commands, Func<bool> IsValid);

    private readonly NativeEngineApi Native;
    private readonly EditorCommandRegistry Commands;
    private readonly Dictionary<string, Session> Sessions = new(StringComparer.Ordinal);
    private ulong NextToken;
    private ulong Revision;

    internal EditorPopupMenu(NativeEngineApi Native, EditorCommandRegistry Commands)
    {
        this.Native = Native;
        this.Commands = Commands;
    }

    internal void Show(string HostId, int X, int Y, EditorCommandContext Context,
        IEnumerable<(string Id, IReadOnlyList<EditorCommand> Commands)> Sections, Func<bool> IsValid,
        EditorCommandRegistry? LocalCommands = null)
    {
        Close(HostId);
        var Token = (++NextToken).ToString(CultureInfo.InvariantCulture);
        var BoundCommands = new Dictionary<string, CommandBinding>(StringComparer.Ordinal);
        var Items = new List<UiMenuSection>();
        foreach (var Section in Sections)
        {
            var Entries = new List<UiMenuItem>();
            foreach (var Command in Section.Commands)
            {
                var Registry = Commands;
                if (LocalCommands is not null)
                {
                    var IsLocal = LocalCommands.TryGet(Command.Id, out var LocalCommand);
                    if (IsLocal && ReferenceEquals(LocalCommand, Command))
                    {
                        Registry = LocalCommands;
                    }
                }
                var Added = BoundCommands.TryAdd(Command.Id, new CommandBinding(Command, Registry));
                if (!Added)
                {
                    continue;
                }
                var Label = Command.Label;
                try
                {
                    Label = Command.GetLabel?.Invoke(Context) ?? Label;
                }
                catch (Exception Error)
                {
                    SharpLog.Error("EditorMenu", $"Menu label failed for {Command.Id}", Error);
                }
                Entries.Add(new UiMenuItem("context-command-" + Command.Id, Label,
                    "editor.menu:" + Token + ":" + Command.Id,
                    Registry.CanExecute(Command, Context),
                    Command.GetChecked is null ? null : Registry.IsChecked(Command, Context),
                    Command.Tooltip));
            }
            Items.Add(new UiMenuSection(Section.Id, Entries));
        }
        if (BoundCommands.Count == 0)
        {
            return;
        }
        var Menu = XElement.Parse(new UiMenu(Items).BuildLayout(new UiLayoutContext(0, 0)));
        Menu.SetAttributeValue("data-menu-session", Token);
        Menu.SetAttributeValue("data-anchor-x", X);
        Menu.SetAttributeValue("data-anchor-y", Y);
        Sessions[HostId] = new Session(Token, Context, BoundCommands, IsValid);
        Revision++;
        var Markup = Menu.ToString(SaveOptions.DisableFormatting);
        Native.SetHostMarkup(HostId, "contextMenu", Markup);
        if (!Native.HasHostMarkup && HostId == "main")
        {
            Native.SetUiMarkup("contextMenu", Markup);
            Native.SetUiProperty("contextMenu", "left", X.ToString(CultureInfo.InvariantCulture) + "px");
            Native.SetUiProperty("contextMenu", "top", Y.ToString(CultureInfo.InvariantCulture) + "px");
            Native.SetUiProperty("contextMenu", "display", "block");
        }
    }

    internal bool TryHandleAction(string HostId, string Action)
    {
        const string ClosedPrefix = "editor.menu-closed:";
        if (Action.StartsWith(ClosedPrefix, StringComparison.Ordinal))
        {
            var HasSession = Sessions.TryGetValue(HostId, out var Closed);
            if (HasSession && Closed!.Token == Action[ClosedPrefix.Length..])
            {
                Close(HostId);
            }
            return true;
        }
        const string Prefix = "editor.menu:";
        if (!Action.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }
        var Parts = Action[Prefix.Length..].Split(':', 2);
        var Found = Sessions.TryGetValue(HostId, out var Session);
        if (!Found || Parts.Length != 2 || Session!.Token != Parts[0])
        {
            return true;
        }
        Close(HostId);
        var HasCommand = Session.Commands.TryGetValue(Parts[1], out var Binding);
        if (!Session.IsValid() || !HasCommand)
        {
            Session.Context.SetStatus("Context menu target is no longer available");
            return true;
        }
        var Registered = Binding!.Registry.TryGet(Parts[1], out var Current);
        if (!Registered || !ReferenceEquals(Binding.Command, Current))
        {
            Session.Context.SetStatus("Context menu target is no longer available");
            return true;
        }
        Binding.Registry.TryExecute("editor.command:" + Binding.Command.Id, Session.Context);
        return true;
    }

    internal JsonObject Describe()
    {
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["stateRevision"] = Revision,
            ["sessions"] = new JsonArray(Sessions.OrderBy(Pair => Pair.Key, StringComparer.Ordinal)
                .Select(Pair => (JsonNode)new JsonObject
                {
                    ["hostId"] = Pair.Key,
                    ["sessionId"] = Pair.Value.Token,
                    ["paneId"] = Pair.Value.Context.ContentPaneId,
                    ["targetId"] = Pair.Value.Context.Selection.Id,
                    ["targetKind"] = Pair.Value.Context.Selection.Kind.ToString(),
                    ["targetPath"] = Pair.Value.Context.Selection.Path,
                    ["commands"] = new JsonArray(Pair.Value.Commands.Keys
                        .Select(Id => (JsonNode?)JsonValue.Create(Id)).ToArray()),
                }).ToArray()),
        };
    }

    internal void Close(string HostId)
    {
        if (Sessions.Remove(HostId))
        {
            Revision++;
            Native.SetHostMarkup(HostId, "contextMenu", string.Empty);
            if (!Native.HasHostMarkup && HostId == "main")
            {
                Native.SetUiMarkup("contextMenu", string.Empty);
                Native.SetUiProperty("contextMenu", "display", "none");
            }
        }
    }

    internal void Clear()
    {
        foreach (var HostId in Sessions.Keys.ToArray())
        {
            Close(HostId);
        }
    }

    internal void ClosePane(string PaneId)
    {
        foreach (var HostId in Sessions.Where(Pair => Pair.Value.Context.ContentPaneId == PaneId)
            .Select(Pair => Pair.Key).ToArray())
        {
            Close(HostId);
        }
    }
}
