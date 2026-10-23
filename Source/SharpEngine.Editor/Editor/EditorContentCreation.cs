using System.Text.Json;
using System.Text.Json.Nodes;
using SharpEngine.Assets;

namespace SharpEngine.Editor;

public sealed partial class EditorRuntime
{
    private const string ContentCreationHost = "content.create";
    private sealed record PendingContentCreation(string PaneId, string Type, string Folder, string Revision);
    private PendingContentCreation? PendingCreation;
    private string CreationName = string.Empty;

    internal IReadOnlyList<ContentAssetType> ContentCreationTypes
    {
        get
        {
            return ContentAssetCreation.Types(DataAssetTypes);
        }
    }

    private ContentAssetCreation ContentCreation()
    {
        return new ContentAssetCreation(_assets ?? throw new InvalidOperationException("Assets are unavailable"),
            ProjectAssetManifestPath() ?? throw new InvalidOperationException("Open a project before creating assets"),
            Components ?? throw new InvalidOperationException("Components are unavailable"), DataAssetTypes);
    }

    internal bool CanCreateContent(string Folder)
    {
        if (_projectManifest is null || PlayState != EditorPlayState.Stopped)
        {
            return false;
        }
        try
        {
            var Creation = ContentCreation();
            Creation.FolderPath(Folder);
            Creation.Revision();
            return true;
        }
        catch (Exception Error) when (Error is InvalidDataException or IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Creates and saves a project source asset through the same disk transaction used by the UI.</summary>
    public CreateContentAssetResult CreateContentAsset(CreateContentAssetRequest Request)
    {
        if (Environment.CurrentManagedThreadId != _sourceOwnerThread)
        {
            return new(1, false, "CONTENT_CREATE_THREAD", "Content creation must run on the editor thread");
        }
        if (PlayState != EditorPlayState.Stopped || _projectManifest is null)
        {
            return new(1, false, "CONTENT_CREATE_UNAVAILABLE", "Open a project and stop Play before creating content");
        }
        CreateContentAssetResult Result;
        try
        {
            Result = ContentCreation().Create(Request);
        }
        catch (Exception Error) when (Error is InvalidDataException or IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            return new(1, false, "CONTENT_CREATE_UNAVAILABLE", Error.Message);
        }
        if (Result.Success)
        {
            ReloadAssetRegistry();
        }
        SetStatus(Result.Message);
        return Result;
    }

    internal JsonObject DescribeContentCreation()
    {
        string? Revision = null;
        if (_projectManifest is not null)
        {
            try
            {
                Revision = ContentCreation().Revision();
            }
            catch (Exception Error) when (Error is InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // An unavailable manifest is observable without making content browsing fail.
            }
        }
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["available"] = Revision is not null && PlayState == EditorPlayState.Stopped,
            ["revision"] = Revision,
            ["effect"] = "external",
            ["types"] = JsonSerializer.SerializeToNode(ContentCreationTypes, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            ["pending"] = PendingCreation is null ? null : new JsonObject
            {
                ["paneId"] = PendingCreation.PaneId,
                ["type"] = PendingCreation.Type,
                ["folder"] = PendingCreation.Folder,
                ["expectedRevision"] = PendingCreation.Revision,
                ["name"] = CreationName,
            },
        };
    }

    internal void OpenContentCreation(string PaneId, string Type, string Folder)
    {
        if (!CanCreateContent(Folder) || !IsPaneVisible(PaneId))
        {
            return;
        }
        var Definition = ContentCreationTypes.Single(Item => Item.Type == Type);
        var Pending = new PendingContentCreation(PaneId, Type, Folder, ContentCreation().Revision());
        if (_nativeApi.CreateDockWindow(ContentCreationHost, "New " + Definition.Label) == 0)
        {
            SetStatus("Asset creation requires the document UI host");
            return;
        }
        PendingCreation = Pending;
        CreationName = string.Empty;
        _nativeApi.SetHostMarkup(ContentCreationHost, "dockWindowTitle", "Add Content");
        var Markup = "<div class=\"content-create-form\"><h2>" + Escape(Definition.Label) + "</h2>" +
            "<p>Project / Content" + Escape(Folder.Length == 0 ? "" : "/" + Folder) + "</p>" +
            "<label>Name</label><input id=\"content-create-name\" data-change-action=\"editor.content-create-name:\" />" +
            "<p>" + Escape(Definition.Extension) + "</p><div class=\"content-create-buttons\">" +
            "<button id=\"content-create-confirm\" data-action=\"editor.content-create-confirm\">Create</button>" +
            "<button id=\"content-create-cancel\" data-action=\"editor.content-create-cancel\">Cancel</button></div>" +
            "<div id=\"content-create-feedback\"></div></div>";
        _nativeApi.SetHostMarkup(ContentCreationHost, "workspace", Markup);
    }

    private void CloseContentCreation()
    {
        PendingCreation = null;
        CreationName = string.Empty;
        _nativeApi.DestroyDockWindow(ContentCreationHost);
    }

    private bool HandleContentCreationAction(string HostId, string Action)
    {
        if (HostId != ContentCreationHost)
        {
            return false;
        }
        if (Action is "window.closed" or "window.close-requested" or "editor.content-create-cancel")
        {
            CloseContentCreation();
            return true;
        }
        var Pending = PendingCreation;
        if (Pending is null)
        {
            return true;
        }
        const string NamePrefix = "editor.content-create-name:";
        if (Action.StartsWith(NamePrefix, StringComparison.Ordinal))
        {
            CreationName = Action[NamePrefix.Length..];
            _nativeApi.SetHostMarkup(ContentCreationHost, "content-create-feedback", string.Empty);
            return true;
        }
        if (Action != "editor.content-create-confirm")
        {
            return false;
        }
        if (!IsPaneVisible(Pending.PaneId))
        {
            CloseContentCreation();
            return true;
        }
        var Name = CreationName;
        var Result = CreateContentAsset(new(1, Pending.Type, Pending.Folder, Name, Pending.Revision));
        if (!Result.Success)
        {
            _nativeApi.SetHostMarkup(ContentCreationHost, "content-create-feedback", "<p>" + Escape(Result.Message) + "</p>");
            return true;
        }
        CloseContentCreation();
        var Manager = _contentManagers!.Get(Pending.PaneId);
        Manager.SetSource(ContentSourceFilter.Project);
        Manager.OpenFolder(Pending.Folder);
        Manager.SetSearch(string.Empty);
        Manager.SetType(null);
        if (Result.AssetId is not null)
        {
            Manager.Select(Result.AssetId);
        }
        else
        {
            Manager.SelectFolder(AssetSource.Project, Pending.Folder.Length == 0 ? Name : Pending.Folder + "/" + Name);
        }
        _contentManagers.Activate(Pending.PaneId);
        ApplyActiveContentSelection();
        CommitContentManagerState(Result.Message, true);
        return true;
    }
}
