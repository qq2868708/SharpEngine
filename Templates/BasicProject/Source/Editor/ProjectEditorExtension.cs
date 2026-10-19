using SharpEngine.Editor;
using SharpEngine.World;

namespace __PROJECT_NAMESPACE__.Editor;

public sealed class ProjectEditorExtension : IEditorExtension
{
    private readonly List<IDisposable> Registrations = new();

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

        Register(Registrations, Context.Menus.RegisterMenu(
            new EditorMenu("project", "Project", 450)));
        Register(Registrations, Context.Menus.RegisterCategory(
            new EditorMenuCategory("project", "transform", 100)));
        Register(Registrations, Context.Menus.RegisterCategory(
            new EditorMenuCategory("project", "settings", 200)));
        Register(Registrations, Context.Menus.RegisterItem(
            new EditorMenuItem("project", "transform", "project.reset-transform", 100)));
        Register(Registrations, Context.Menus.RegisterItem(
            new EditorMenuItem("project", "settings", "project.snap-to-grid", 100)));

        RegisterContext(Context, EditorContextMenuKind.Object, "transform",
            "project.reset-position", "project.reset-rotation", "project.reset-scale");
        RegisterContext(Context, EditorContextMenuKind.Scene, "selection",
            "project.select-main-camera");
        RegisterContext(Context, EditorContextMenuKind.Asset, "inspect",
            "project.show-asset-path");
        RegisterContext(Context, EditorContextMenuKind.Folder, "inspect",
            "project.show-folder-path");
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

    private static bool CanEditSelection(EditorCommandContext Command) =>
        Command.PlayState == EditorPlayState.Stopped && Command.SelectedEntity is not null;

    private static void EditSelected(EditorCommandContext Command,
        Action<WorldEntity> Edit, string Status)
    {
        var Entity = Command.SelectedEntity!;
        Edit(Entity);
        Command.MarkDirty();
        Command.RefreshUi();
        Command.SetStatus($"{Status}: {Entity.Name}");
    }

    private static void Register(List<IDisposable> Registrations,
        IDisposable Registration) => Registrations.Add(Registration);
}
