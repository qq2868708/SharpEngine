using SharpEngine.Editor;
using SharpEngine.World;

namespace __PROJECT_NAMESPACE__.Editor;

public sealed class ProjectEditorExtension : IEditorExtension
{
    private readonly List<IDisposable> _registrations = new();

    public void Startup(EditorContext context)
    {
        RegisterCommand(context, new EditorCommand(
            "project.reset-transform", "Reset Transform",
            command => EditSelected(command, entity =>
            {
                entity.Position = WorldVector3.Zero;
                entity.Rotation = WorldVector3.Zero;
                entity.Scale = WorldVector3.One;
            }, "Reset transform"), CanEditSelection));
        RegisterCommand(context, new EditorCommand(
            "project.reset-position", "Reset Position",
            command => EditSelected(command,
                entity => entity.Position = WorldVector3.Zero, "Reset position"),
            CanEditSelection));
        RegisterCommand(context, new EditorCommand(
            "project.reset-rotation", "Reset Rotation",
            command => EditSelected(command,
                entity => entity.Rotation = WorldVector3.Zero, "Reset rotation"),
            CanEditSelection));
        RegisterCommand(context, new EditorCommand(
            "project.reset-scale", "Reset Scale",
            command => EditSelected(command,
                entity => entity.Scale = WorldVector3.One, "Reset scale"),
            CanEditSelection));
        RegisterCommand(context, new EditorCommand(
            "project.snap-to-grid", "Snap To Grid",
            command => command.TransformSnapEnabled = !command.TransformSnapEnabled,
            getChecked: command => command.TransformSnapEnabled));
        RegisterCommand(context, new EditorCommand(
            "project.select-main-camera", "Select Main Camera",
            command => command.Runtime.InvokeUiAction("editor.select-camera"),
            command => command.World?.ActiveCamera is not null));
        RegisterCommand(context, new EditorCommand(
            "project.show-asset-path", "Show Asset Path",
            command => command.SetStatus(
                $"Asset: {command.SelectedAsset?.RelativePath ?? command.Selection.Path}"),
            command => command.Selection.Kind == EditorSelectionKind.Asset));
        RegisterCommand(context, new EditorCommand(
            "project.show-folder-path", "Show Folder Path",
            command => command.SetStatus($"Folder: Content/{command.SelectedFolderPath}"),
            command => command.Selection.Kind == EditorSelectionKind.Folder));

        Register(_registrations, context.Menus.RegisterMenu(
            new EditorMenu("project", "Project", 450)));
        Register(_registrations, context.Menus.RegisterCategory(
            new EditorMenuCategory("project", "transform", 100)));
        Register(_registrations, context.Menus.RegisterCategory(
            new EditorMenuCategory("project", "settings", 200)));
        Register(_registrations, context.Menus.RegisterItem(
            new EditorMenuItem("project", "transform", "project.reset-transform", 100)));
        Register(_registrations, context.Menus.RegisterItem(
            new EditorMenuItem("project", "settings", "project.snap-to-grid", 100)));

        RegisterContext(context, EditorContextMenuKind.Object, "transform",
            "project.reset-position", "project.reset-rotation", "project.reset-scale");
        RegisterContext(context, EditorContextMenuKind.Scene, "selection",
            "project.select-main-camera");
        RegisterContext(context, EditorContextMenuKind.Asset, "inspect",
            "project.show-asset-path");
        RegisterContext(context, EditorContextMenuKind.Folder, "inspect",
            "project.show-folder-path");
    }

    public void Shutdown()
    {
        for (var index = _registrations.Count - 1; index >= 0; index--)
            _registrations[index].Dispose();
        _registrations.Clear();
    }

    private void RegisterCommand(EditorContext context, EditorCommand command) =>
        Register(_registrations, context.Commands.Register(command));

    private void RegisterContext(EditorContext context, EditorContextMenuKind kind,
        string categoryId, params string[] commandIds)
    {
        Register(_registrations, context.ContextMenus.RegisterCategory(
            new EditorContextMenuCategory(kind, categoryId)));
        for (var index = 0; index < commandIds.Length; index++)
            Register(_registrations, context.ContextMenus.RegisterItem(
                new EditorContextMenuItem(kind, categoryId, commandIds[index], index * 100)));
    }

    private static bool CanEditSelection(EditorCommandContext command) =>
        command.PlayState == EditorPlayState.Stopped && command.SelectedEntity is not null;

    private static void EditSelected(EditorCommandContext command,
        Action<WorldEntity> edit, string status)
    {
        var entity = command.SelectedEntity!;
        edit(entity);
        command.MarkDirty();
        command.RefreshUi();
        command.SetStatus($"{status}: {entity.Name}");
    }

    private static void Register(List<IDisposable> registrations,
        IDisposable registration) => registrations.Add(registration);
}
