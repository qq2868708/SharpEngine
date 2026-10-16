namespace __PROJECT_NAMESPACE__;

using System.Runtime.InteropServices;
using SharpEngine.Editor;
using SharpEngine.World;

// Project-owned C# logic. The public engine API is referenced by the generated
// Project/SharpEngine.props, while this file stays in the project repository.
public static class Game
{
    public static void Update(float deltaSeconds)
    {
    }
}

// This class belongs to the project: no RML or native host changes are needed.
// Every placement and command registration is released when the module unloads.
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
            }, "Reset transform"),
            CanEditSelection,
            "Reset position, rotation, and scale"));

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
            command =>
            {
                command.TransformSnapEnabled = !command.TransformSnapEnabled;
                command.SetStatus($"Snap To Grid: {(command.TransformSnapEnabled ? "On" : "Off")}");
            },
            tooltip: "Toggle transform gizmo snapping",
            getChecked: command => command.TransformSnapEnabled));
        RegisterCommand(context, new EditorCommand(
            "project.select-main-camera", "Select Main Camera",
            command => command.Runtime.InvokeUiAction("editor.select-camera"),
            command => command.World?.ActiveCamera is not null));
        RegisterCommand(context, new EditorCommand(
            "project.show-asset-path", "Show Asset Path",
            command => command.SetStatus($"Asset: {command.SelectedAsset?.RelativePath ?? command.Selection.Path}"),
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

    private static void Register(List<IDisposable> registrations, IDisposable registration) =>
        registrations.Add(registration);
}

// The native host resolves this type from Project/project.json. Keep the
// UnmanagedCallersOnly signatures stable so projects can run with the public
// engine distribution without copying engine sources.
public static class GameEntrypoint
{
    private static EditorRuntime? s_editor;

    [UnmanagedCallersOnly]
    public static int Initialize(nint windowHandle, uint width, uint height, nint nativeApi)
    {
        try
        {
            s_editor = new EditorRuntime(windowHandle, width, height, nativeApi);
            s_editor.Initialize();
            s_editor.RegisterExtension(new ProjectEditorExtension());
            Console.Error.WriteLine($"[Project] Started {width}x{height}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[Project] Initialize failed: {exception}");
            s_editor?.Shutdown();
            s_editor = null;
            return -1;
        }
    }

    [UnmanagedCallersOnly]
    public static void Update(float deltaSeconds)
    {
        try
        {
            Game.Update(deltaSeconds);
            s_editor?.Update(deltaSeconds);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[Project] Update failed: {exception}");
        }
    }

    [UnmanagedCallersOnly]
    public static void Shutdown()
    {
        try
        {
            s_editor?.Shutdown();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[Project] Shutdown failed: {exception}");
        }
        finally
        {
            s_editor = null;
        }
    }

    [UnmanagedCallersOnly]
    public static void PointerDown(nint windowHandle, int x, int y, uint button)
    {
        try
        {
            s_editor?.PointerDown(windowHandle, x, y, button);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[Project] PointerDown failed: {exception}");
        }
    }

    [UnmanagedCallersOnly]
    public static void InvokeUiAction(nint actionUtf8)
    {
        try
        {
            var action = Marshal.PtrToStringUTF8(actionUtf8);
            if (!string.IsNullOrWhiteSpace(action)) s_editor?.InvokeUiAction(action);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[Project] InvokeUiAction failed: {exception}");
        }
    }

    [UnmanagedCallersOnly]
    public static void ViewportInput(nint windowHandle, int x, int y,
        uint inputEvent, uint buttons, int wheelDelta)
    {
        try
        {
            s_editor?.ViewportInput(windowHandle, x, y, inputEvent, buttons, wheelDelta);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[Project] ViewportInput failed: {exception}");
        }
    }

    [UnmanagedCallersOnly]
    public static void ViewportKey(uint key, byte pressed, byte viewportFocused)
    {
        try
        {
            s_editor?.ViewportKey(key, pressed != 0, viewportFocused != 0);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[Project] ViewportKey failed: {exception}");
        }
    }

    [UnmanagedCallersOnly]
    public static int RunMcp()
    {
        try
        {
            return s_editor is null ? -1 : McpEndpoint.Run(s_editor);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[Project] MCP failed: {exception}");
            return -1;
        }
    }
}
