using SharpEngine.Editor;
using SharpEngine.World;

namespace __PROJECT_NAMESPACE__.Editor;

public sealed class ProjectEditorExtension : IEditorExtension, IMenuExtension,
    IToolBarExtension, ICustomBarExtension
{
    private readonly List<IDisposable> Registrations = new();

    public string MenuId
    {
        get
        {
            return "file";
        }
    }

    public string ToolbarId
    {
        get
        {
            return EditorCustomBarRegistry.MainToolbarId;
        }
    }

    public string BarId
    {
        get
        {
            return EditorCustomBarRegistry.ViewportSurfaceId;
        }
    }

    public EditorCustomBarMode Mode
    {
        get
        {
            return EditorCustomBarMode.Append;
        }
    }

    public void Startup(EditorContext Context)
    {
        RegisterCommand(Context, new EditorCommand(
            "project.reset-transform", "Reset Transform",
            Command => EditSelected(Command, Entity =>
            {
                Entity.Position = WorldVector3.Zero;
                Entity.Rotation = WorldVector3.Zero;
                Entity.Scale = WorldVector3.One;
            }, "Reset transform"), CanEditSelection));
        RegisterCommand(Context, new EditorCommand(
            "project.reset-position", "Reset Position",
            Command => EditSelected(Command,
                Entity => Entity.Position = WorldVector3.Zero, "Reset position"),
            CanEditSelection));
        RegisterCommand(Context, new EditorCommand(
            "project.reset-rotation", "Reset Rotation",
            Command => EditSelected(Command,
                Entity => Entity.Rotation = WorldVector3.Zero, "Reset rotation"),
            CanEditSelection));
        RegisterCommand(Context, new EditorCommand(
            "project.reset-scale", "Reset Scale",
            Command => EditSelected(Command,
                Entity => Entity.Scale = WorldVector3.One, "Reset scale"),
            CanEditSelection));
        RegisterCommand(Context, new EditorCommand(
            "project.snap-to-grid", "Snap To Grid",
            Command => Command.TransformSnapEnabled = !Command.TransformSnapEnabled,
            getChecked: Command => Command.TransformSnapEnabled));
        RegisterCommand(Context, new EditorCommand(
            "project.select-main-camera", "Select Main Camera",
            Command => Command.Runtime.InvokeUiAction("editor.select-camera"),
            Command => Command.World?.ActiveCamera is not null));
        RegisterCommand(Context, new EditorCommand(
            "project.show-asset-path", "Show Asset Path",
            Command => Command.SetStatus(
                $"Asset: {Command.SelectedAsset?.RelativePath ?? Command.Selection.Path}"),
            Command => Command.Selection.Kind == EditorSelectionKind.Asset));
        RegisterCommand(Context, new EditorCommand(
            "project.show-folder-path", "Show Folder Path",
            Command => Command.SetStatus($"Folder: Content/{Command.SelectedFolderPath}"),
            Command => Command.Selection.Kind == EditorSelectionKind.Folder));
        RegisterCommand(Context, new EditorCommand(
            "project.save-and-export-scene", "Save And Export Scene",
            Command =>
            {
                Command.Save();
                Command.SaveSceneDescription();
                Command.SetStatus("Project scene saved and exported");
            },
            CanSaveScene,
            "Save the current World and export its JSON description"));
        RegisterCommand(Context, new EditorCommand(
            "project.show-world-stats", "Show World Statistics",
            Command =>
            {
                var EntityCount = Command.World?.Entities.Count ?? 0;
                var AssetCount = Command.Assets?.Assets.Count ?? 0;
                Command.SetStatus($"World: {EntityCount} entities · {AssetCount} assets · {Command.PlayState}");
            },
            Command => Command.World is not null,
            "Show the current World and asset counts"));
        RegisterCommand(Context, new EditorCommand(
            "project.describe-selection", "Describe Selection",
            Command => Command.SetStatus(SelectionDescription(Command)),
            HasSelection,
            "Show the selected entity, asset, folder or scene"));
        RegisterCommand(Context, new EditorCommand(
            "project.preview-frame-selection", "Frame Selection",
            Command => Command.SetStatus(FrameSelectionStatus(Command)),
            HasSelection,
            "Frame the selected object in the project preview"));
        RegisterCommand(Context, new EditorCommand(
            "project.preview-mode", "Project Preview",
            Command => Command.SetStatus("Project preview is using the active Viewport custom bar"),
            Command => Command.World is not null,
            "Show the project preview integration status"));

        Register(Registrations, Context.Menus.RegisterExtension(this));
        Register(Registrations, Context.Toolbar.RegisterExtension(this));

        RegisterContext(Context, EditorContextMenuKind.Object, "transform",
            "project.reset-position", "project.reset-rotation", "project.reset-scale");
        RegisterContext(Context, EditorContextMenuKind.Scene, "selection",
            "project.select-main-camera", "project.describe-selection");
        RegisterContext(Context, EditorContextMenuKind.Scene, "workflow",
            "project.show-world-stats", "project.save-and-export-scene");
        RegisterContext(Context, EditorContextMenuKind.Object, "selection",
            "project.describe-selection");
        RegisterContext(Context, EditorContextMenuKind.Asset, "inspect",
            "project.show-asset-path", "project.describe-selection");
        RegisterContext(Context, EditorContextMenuKind.Folder, "inspect",
            "project.show-folder-path", "project.describe-selection");
        Register(Registrations, Context.CustomBars.Register(this));
    }

    public void Shutdown()
    {
        for (var Index = Registrations.Count - 1; Index >= 0; Index--)
        {
            Registrations[Index].Dispose();
        }
        Registrations.Clear();
    }

    private void RegisterCommand(EditorContext Context, EditorCommand Command) =>
        Register(Registrations, Context.Commands.Register(Command));

    private void RegisterContext(EditorContext Context, EditorContextMenuKind Kind,
        string CategoryId, params string[] CommandIds)
    {
        Register(Registrations, Context.ContextMenus.RegisterCategory(
            new EditorContextMenuCategory(Kind, CategoryId)));
        for (var Index = 0; Index < CommandIds.Length; Index++)
        {
            Register(Registrations, Context.ContextMenus.RegisterItem(
                new EditorContextMenuItem(Kind, CategoryId, CommandIds[Index], Index * 100)));
        }
    }

    public void Extend(EditorMenuExtensionContext Context)
    {
        Context.RegisterCategory("project-tools", 350);
        Context.RegisterCategory("project-selection", 360);
        Context.RegisterCommand("project-tools", "project.reset-transform", 100);
        Context.RegisterCommand("project-tools", "project.snap-to-grid", 200);
        Context.RegisterCommand("project-tools", "project.save-and-export-scene", 300);
        Context.RegisterCommand("project-tools", "project.show-world-stats", 400);
        Context.RegisterCommand("project-selection", "project.select-main-camera", 100);
        Context.RegisterCommand("project-selection", "project.describe-selection", 200);
        Context.RegisterCommand("project-selection", "project.preview-mode", 300);
    }

    public void Extend(EditorToolbarExtensionContext Context)
    {
        Context.RegisterCategory("project-tools", 200);
        Context.RegisterTool("project-workflow", "project-tools", "Project",
            ["project.save-and-export-scene", "project.show-world-stats",
                "project.describe-selection"], 100, "Project workflow commands");
        Context.RegisterTool("project-reset-transform", "project-tools", "Reset Transform",
            ["project.reset-transform"], 200, "Reset the selected entity transform");
        Context.RegisterTool("project-snap-to-grid", "project-tools", "Snap To Grid",
            ["project.snap-to-grid"], 300, "Toggle transform snapping", "grid");
        Context.RegisterTool("project-main-camera", "project-tools", "Main Camera",
            ["project.select-main-camera"], 400, "Select the active World camera");
    }

    private static bool CanEditSelection(EditorCommandContext Command) =>
        Command.PlayState == EditorPlayState.Stopped && Command.SelectedEntity is not null;

    private static bool CanSaveScene(EditorCommandContext Command) =>
        Command.PlayState == EditorPlayState.Stopped && Command.World is not null;

    private static bool HasSelection(EditorCommandContext Command) =>
        Command.Selection.Kind != EditorSelectionKind.None;

    private static string SelectionDescription(EditorCommandContext Command)
    {
        if (Command.SelectedEntity is { } Entity)
        {
            return $"Selected entity: {Entity.Name} ({Entity.Id})";
        }
        if (Command.SelectedAsset is { } Asset)
        {
            return $"Selected asset: {Asset.RelativePath}";
        }
        if (Command.SelectedFolderPath is { } FolderPath)
        {
            return $"Selected folder: Content/{FolderPath}";
        }
        if (Command.Selection.Kind == EditorSelectionKind.Scene)
        {
            return $"Selected scene: {Command.World?.Name ?? "World"}";
        }
        return "No project selection";
    }

    private static string FrameSelectionStatus(EditorCommandContext Command)
    {
        if (Command.SelectedEntity is { } Entity)
        {
            return $"Project preview framed: {Entity.Name}";
        }

        return "Project preview framed the current selection";
    }

    public string BuildMarkup(EditorCustomBarContext Context)
    {
        var Content = Context.CommandButton("project.preview-frame-selection", Icon: "all") +
            Context.CommandButton("project.show-world-stats", Icon: "world") +
            Context.CommandButton("project.snap-to-grid", Icon: "grid") +
            Context.CommandButton("project.preview-mode", Icon: "camera");
        return Context.Group("Project Preview", Content,
            "custom-bar-group project-preview-custom-bar");
    }

    private static void EditSelected(EditorCommandContext Command,
        Action<WorldEntity> Edit, string Status)
    {
        var Entity = Command.SelectedEntity!;
        var Result = Command.EditSelectedEntity(Edit, Status);
        Command.SetStatus(Result.Success ? $"{Status}: {Entity.Name}" : Result.Message);
    }

    private static void Register(List<IDisposable> Registrations,
        IDisposable Registration) => Registrations.Add(Registration);
}
