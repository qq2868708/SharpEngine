using System.Net;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using SharpEngine.Assets;
using SharpEngine.UI;

namespace SharpEngine.Editor;

/// <summary>One browser UI instance owns its menu and layout-scope registrations.</summary>
internal sealed class ContentManagerView : IDisposable
{
    private readonly IDisposable ContextRegistration;
    private readonly IDisposable CompatibilityRegistration;
    private readonly EditorPopupMenu Popup;
    internal string PaneId
    {
        get;
    }
    internal ContentManager Model
    {
        get;
    }
    internal ContentManagerContextMenus Menu
    {
        get;
    }
    internal bool IsDisposed
    {
        get;
        private set;
    }

    internal ContentManagerView(string PaneId, ContentManager Model, IContentManagerMenuHost Host,
        EditorCommandRegistry Commands, EditorContextMenuRegistry Extensions,
        EditorPopupMenu Popup, UiContextMenuRouter Routing)
    {
        this.PaneId = PaneId;
        this.Model = Model;
        this.Popup = Popup;
        Menu = new ContentManagerContextMenus(PaneId, Model, () => !IsDisposed, Host, Commands, Extensions, Popup);
        ContextRegistration = Routing.Register(ScopeId(PaneId), Menu.HandleContext);
        CompatibilityRegistration = Routing.RegisterCompatibility(Menu.TryHandleRequest);
    }

    internal static string ScopeId(string PaneId) { return "content-manager/" + PaneId; }

    internal static string ContextAttribute(string Kind, string Source, string? Target, bool AddOnly = false)
    {
        return WebUtility.HtmlEncode(ContextPayload(Kind, Source, Target, AddOnly));
    }

    private static string ContextPayload(string Kind, string Source, string? Target, bool AddOnly = false)
    {
        var Payload = new JsonObject
        {
            ["kind"] = Kind,
            ["source"] = Source,
            ["target"] = Target,
            ["addOnly"] = AddOnly,
        };
        return Payload.ToJsonString();
    }

    internal string BuildEntries(AssetSource Source)
    {
        var SourceName = Source.ToString().ToLowerInvariant();
        var Tile = Model.ViewMode == ContentViewMode.Tile;
        var Root = new XElement("div",
            new XAttribute("class", "content-context " + (Tile ? "tile-view" : "list-view")),
            new XAttribute("data-ui-context", ContextPayload("content", SourceName, null)));
        foreach (var Entry in Model.VisibleEntries.Where(Entry => Entry.Source == Source))
        {
            var Selected = Model.SelectedSource == Source &&
                string.Equals(Model.SelectedPath, Entry.RelativePath, StringComparison.OrdinalIgnoreCase);
            var Action = Entry.IsDirectory ? $"editor.content.select-folder:{PaneId}:{SourceName}|{Entry.RelativePath}" :
                Entry.AssetId is null ? $"editor.content.open:{PaneId}:{SourceName}|{Entry.RelativePath}" :
                $"editor.content.select:{PaneId}:{Entry.AssetId}";
            var Item = new XElement("button",
                new XAttribute("class", "asset-item " + (Tile ? "tile-item" : "list-item") +
                    (Selected ? " selected" : "") + (Entry.Exists ? "" : " missing")),
                new XAttribute("data-action", Action),
                new XAttribute("data-ui-context", ContextPayload(Entry.IsDirectory ? "folder" : "asset",
                    SourceName, Entry.AssetId ?? Entry.RelativePath)),
                new XAttribute("title", $"{Entry.RelativePath} ({Entry.Type})"),
                XElement.Parse(EditorResourceUris.ItemIcon(Entry.IsDirectory ? "Folder" : Entry.Type)),
                new XElement("span", new XAttribute("class", "asset-name"), Entry.Name),
                new XElement("span", new XAttribute("class", "item-kind"), Entry.IsDirectory ? "Folder" : Entry.Type));
            if (Entry.IsDirectory)
            {
                Item.SetAttributeValue("data-double-click-action", $"editor.content.open:{PaneId}:{SourceName}|{Entry.RelativePath}");
            }
            else if (Entry.AssetId is not null)
            {
                Item.SetAttributeValue("data-double-click-action", $"editor.content.open-asset:{PaneId}:{Entry.AssetId}");
            }
            if (Entry.Type.Equals("Prefab", StringComparison.OrdinalIgnoreCase))
            {
                Item.SetAttributeValue("draggable", "true");
                Item.SetAttributeValue("data-prefab-asset", Entry.AssetId ?? "");
            }
            if (Entry.ReadOnly)
            {
                Item.Add(new XElement("img", new XAttribute("class", "readonly-icon"),
                    new XAttribute("src", EditorResourceUris.IconSource("lock"))));
            }
            Root.Add(Item);
        }
        if (!Root.HasElements)
        {
            Root.Add(new XElement("div", new XAttribute("class", "empty-list"), "Folder is empty"));
        }
        return Root.ToString(SaveOptions.DisableFormatting);
    }

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }
        IsDisposed = true;
        ContextRegistration.Dispose();
        CompatibilityRegistration.Dispose();
        Popup.ClosePane(PaneId);
        Menu.Dispose();
    }
}
