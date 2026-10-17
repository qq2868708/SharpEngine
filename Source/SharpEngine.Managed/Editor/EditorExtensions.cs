using SharpEngine.Assets;
using SharpEngine.World;

namespace SharpEngine.Editor;

public interface IEditorExtension
{
    void Startup(EditorContext context);
    void Shutdown();
}

public enum EditorSelectionKind : byte { None, Scene, Entity, Asset, Folder }

public sealed record EditorSelection(
    EditorSelectionKind Kind,
    string? Id = null,
    string? Path = null,
    AssetSource? Source = null)
{
    public static EditorSelection None { get; } = new(EditorSelectionKind.None);
    public static EditorSelection Scene { get; } = new(EditorSelectionKind.Scene);
}

public sealed class EditorCommand
{
    public EditorCommand(string id, string label,
        Action<EditorCommandContext> execute,
        Func<EditorCommandContext, bool>? canExecute = null,
        string? tooltip = null,
        Func<EditorCommandContext, bool>? getChecked = null)
    {
        Id = EditorExtensionIds.Validate(id, nameof(id));
        Label = EditorExtensionIds.RequireLabel(label, nameof(label));
        Execute = execute ?? throw new ArgumentNullException(nameof(execute));
        CanExecute = canExecute;
        Tooltip = string.IsNullOrWhiteSpace(tooltip) ? null : tooltip.Trim();
        GetChecked = getChecked;
    }

    public string Id { get; }
    public string Label { get; }
    public string? Tooltip { get; }
    public Func<EditorCommandContext, bool>? CanExecute { get; }
    public Func<EditorCommandContext, bool>? GetChecked { get; }
    public Func<EditorCommandContext, string>? GetLabel { get; init; }
    public Action<EditorCommandContext> Execute { get; }
}

public enum EditorToolbarAlignment : byte { Left, Center, Right }

public sealed record EditorToolbarCategory
{
    public EditorToolbarCategory(string id, int order = 0,
        EditorToolbarAlignment alignment = EditorToolbarAlignment.Left)
    {
        Id = EditorExtensionIds.Validate(id, nameof(id));
        Order = order;
        Alignment = alignment;
    }

    public string Id { get; }
    public int Order { get; }
    public EditorToolbarAlignment Alignment { get; }
}

public sealed class EditorToolbarTool
{
    public EditorToolbarTool(string id, string categoryId, string label,
        IEnumerable<string> commandIds, int order = 0, string? tooltip = null,
        string? icon = null,
        Func<EditorCommandContext, string>? getLabel = null,
        Func<EditorCommandContext, string?>? getIcon = null)
    {
        Id = EditorExtensionIds.Validate(id, nameof(id));
        CategoryId = EditorExtensionIds.Validate(categoryId, nameof(categoryId));
        Label = EditorExtensionIds.RequireLabel(label, nameof(label));
        CommandIds = (commandIds ?? throw new ArgumentNullException(nameof(commandIds)))
            .Select(commandId => EditorExtensionIds.Validate(commandId, nameof(commandIds)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (CommandIds.Count == 0)
            throw new ArgumentException("A toolbar tool requires at least one command", nameof(commandIds));
        Order = order;
        Tooltip = string.IsNullOrWhiteSpace(tooltip) ? null : tooltip.Trim();
        Icon = EditorExtensionIds.ValidateIcon(icon, nameof(icon));
        GetLabel = getLabel;
        GetIcon = getIcon;
    }

    public string Id { get; }
    public string CategoryId { get; }
    public string Label { get; }
    public IReadOnlyList<string> CommandIds { get; }
    public int Order { get; }
    public string? Tooltip { get; }
    public string? Icon { get; }
    public Func<EditorCommandContext, string>? GetLabel { get; }
    public Func<EditorCommandContext, string?>? GetIcon { get; }
}

public sealed record EditorMenu
{
    public EditorMenu(string id, string label, int order = 0)
    {
        Id = EditorExtensionIds.Validate(id, nameof(id));
        Label = EditorExtensionIds.RequireLabel(label, nameof(label));
        Order = order;
    }

    public string Id { get; }
    public string Label { get; }
    public int Order { get; }
}

public sealed record EditorMenuCategory
{
    public EditorMenuCategory(string menuId, string id, int order = 0)
    {
        MenuId = EditorExtensionIds.Validate(menuId, nameof(menuId));
        Id = EditorExtensionIds.Validate(id, nameof(id));
        Order = order;
    }

    public string MenuId { get; }
    public string Id { get; }
    public int Order { get; }
}

public sealed record EditorMenuItem
{
    public EditorMenuItem(string menuId, string categoryId, string commandId, int order = 0)
    {
        MenuId = EditorExtensionIds.Validate(menuId, nameof(menuId));
        CategoryId = EditorExtensionIds.Validate(categoryId, nameof(categoryId));
        CommandId = EditorExtensionIds.Validate(commandId, nameof(commandId));
        Order = order;
    }

    public string MenuId { get; }
    public string CategoryId { get; }
    public string CommandId { get; }
    public int Order { get; }
}

public enum EditorContextMenuKind : byte { Scene, Object, Asset, Folder }

public sealed record EditorContextMenuCategory
{
    public EditorContextMenuCategory(EditorContextMenuKind context, string id, int order = 0)
    {
        Context = context;
        Id = EditorExtensionIds.Validate(id, nameof(id));
        Order = order;
    }

    public EditorContextMenuKind Context { get; }
    public string Id { get; }
    public int Order { get; }
}

public sealed record EditorContextMenuItem
{
    public EditorContextMenuItem(EditorContextMenuKind context, string categoryId,
        string commandId, int order = 0)
    {
        Context = context;
        CategoryId = EditorExtensionIds.Validate(categoryId, nameof(categoryId));
        CommandId = EditorExtensionIds.Validate(commandId, nameof(commandId));
        Order = order;
    }

    public EditorContextMenuKind Context { get; }
    public string CategoryId { get; }
    public string CommandId { get; }
    public int Order { get; }
}

public sealed class EditorCommandContext
{
    private readonly EditorRuntime _runtime;

    internal EditorCommandContext(EditorRuntime runtime) => _runtime = runtime;

    public EditorRuntime Runtime => _runtime;
    public WorldDocument? World => _runtime.World;
    public AssetRegistry? Assets => _runtime.Assets;
    public EditorSelection Selection => _runtime.Selection;
    public WorldEntity? SelectedEntity => _runtime.SelectedEntity;
    public string? SelectedEntityId => _runtime.SelectedEntityId;
    public AssetDescriptor? SelectedAsset => _runtime.SelectedAsset;
    public string? SelectedFolderPath => _runtime.SelectedFolderPath;
    public EditorPlayState PlayState => _runtime.PlayState;
    public bool TransformSnapEnabled
    {
        get => _runtime.TransformSnapEnabled;
        set => _runtime.TransformSnapEnabled = value;
    }
    public bool IsDirty => _runtime.IsDirty;

    public void SetStatus(string text) => _runtime.SetStatusFromExtension(text);
    public void MarkDirty() => _runtime.MarkDirtyFromExtension();
    public void Save() => _runtime.InvokeUiAction("editor.save");
    public void SaveSceneDescription() => _runtime.InvokeUiAction("editor.save-scene-json");
    public void RefreshUi() => _runtime.RefreshUiFromExtension();
    public void SetText(string id, string value) => _runtime.SetTextFromExtension(id, value);
    public void SetMarkup(string id, string markup) => _runtime.SetMarkupFromExtension(id, markup);
    public void SetProperty(string id, string name, string value) =>
        _runtime.SetPropertyFromExtension(id, name, value);
}

public sealed class EditorContext
{
    internal EditorContext(EditorRuntime runtime, EditorCommandRegistry commands,
        EditorMenuRegistry menus, EditorToolbarRegistry toolbar,
        EditorContextMenuRegistry contextMenus)
    {
        Runtime = runtime;
        Commands = commands;
        Menus = menus;
        Toolbar = toolbar;
        ContextMenus = contextMenus;
    }

    public EditorRuntime Runtime { get; }
    public EditorCommandRegistry Commands { get; }
    public EditorMenuRegistry Menus { get; }
    public EditorToolbarRegistry Toolbar { get; }
    public EditorContextMenuRegistry ContextMenus { get; }
    public EditorSelection Selection => Runtime.Selection;
    public WorldDocument? World => Runtime.World;
    public AssetRegistry? Assets => Runtime.Assets;
    public WorldEntity? SelectedEntity => Runtime.SelectedEntity;
    public string? SelectedEntityId => Runtime.SelectedEntityId;
    public AssetDescriptor? SelectedAsset => Runtime.SelectedAsset;
    public string? SelectedFolderPath => Runtime.SelectedFolderPath;
    public EditorPlayState PlayState => Runtime.PlayState;
    public bool TransformSnapEnabled
    {
        get => Runtime.TransformSnapEnabled;
        set => Runtime.TransformSnapEnabled = value;
    }
    public bool IsDirty => Runtime.IsDirty;

    public void SetStatus(string text) => Runtime.SetStatusFromExtension(text);
    public void MarkDirty() => Runtime.MarkDirtyFromExtension();
    public void Save() => Runtime.InvokeUiAction("editor.save");
    public void SaveSceneDescription() => Runtime.InvokeUiAction("editor.save-scene-json");
    public void RefreshUi() => Runtime.RefreshUiFromExtension();
    public void SetText(string id, string value) => Runtime.SetTextFromExtension(id, value);
    public void SetMarkup(string id, string markup) => Runtime.SetMarkupFromExtension(id, markup);
    public void SetProperty(string id, string name, string value) =>
        Runtime.SetPropertyFromExtension(id, name, value);
}

public sealed class EditorCommandRegistry
{
    private readonly Dictionary<string, EditorCommand> _commands = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action _changed;
    private readonly HashSet<string> _failedConditions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _failedChecked = new(StringComparer.OrdinalIgnoreCase);

    internal EditorCommandRegistry(Action changed) => _changed = changed;

    public IReadOnlyList<EditorCommand> Commands => _commands.Values.ToArray();

    public IDisposable Register(EditorCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!_commands.TryAdd(command.Id, command))
            throw new InvalidOperationException($"Editor command '{command.Id}' is already registered");
        _changed();
        return new EditorRegistration(() => Unregister(command));
    }

    public bool Remove(string id)
    {
        if (!_commands.Remove(id)) return false;
        _failedConditions.Remove(id);
        _failedChecked.Remove(id);
        _changed();
        return true;
    }

    internal bool TryGet(string id, out EditorCommand command) => _commands.TryGetValue(id, out command!);

    internal bool TryExecute(string action, EditorCommandContext context)
    {
        const string prefix = "editor.command:";
        if (!action.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var id = action[prefix.Length..];
        if (!_commands.TryGetValue(id, out var command))
        {
            context.SetStatus($"Unknown editor command: {id}");
            return true;
        }
        try
        {
            if (!CanExecute(command, context))
            {
                context.SetStatus($"Command unavailable: {command.Label}");
                return true;
            }
            command.Execute(context);
        }
        catch (Exception exception)
        {
            context.SetStatus($"Command failed: {command.Label}");
            Console.Error.WriteLine($"[EditorExtension] command={command.Id} failed: {exception}");
        }
        _changed();
        return true;
    }

    internal bool CanExecute(EditorCommand command, EditorCommandContext context)
    {
        if (command.CanExecute is null) return true;
        try
        {
            var enabled = command.CanExecute(context);
            _failedConditions.Remove(command.Id);
            return enabled;
        }
        catch (Exception exception)
        {
            if (_failedConditions.Add(command.Id))
                Console.Error.WriteLine($"[EditorExtension] command={command.Id} CanExecute failed: {exception}");
            return false;
        }
    }

    internal bool IsChecked(EditorCommand command, EditorCommandContext context)
    {
        if (command.GetChecked is null) return false;
        try
        {
            var value = command.GetChecked(context);
            _failedChecked.Remove(command.Id);
            return value;
        }
        catch (Exception exception)
        {
            if (_failedChecked.Add(command.Id))
                Console.Error.WriteLine($"[EditorExtension] command={command.Id} GetChecked failed: {exception}");
            return false;
        }
    }

    internal void Clear()
    {
        _commands.Clear();
        _failedConditions.Clear();
        _failedChecked.Clear();
        _changed();
    }

    private void Unregister(EditorCommand command)
    {
        if (_commands.TryGetValue(command.Id, out var current) && ReferenceEquals(current, command))
        {
            _commands.Remove(command.Id);
            _failedConditions.Remove(command.Id);
            _failedChecked.Remove(command.Id);
            _changed();
        }
    }
}

public sealed class EditorToolbarRegistry
{
    private readonly Dictionary<string, EditorToolbarCategory> _categories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EditorToolbarTool> _tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly EditorCommandRegistry _commands;
    private readonly Action _changed;

    internal EditorToolbarRegistry(EditorCommandRegistry commands, Action changed)
    { _commands = commands; _changed = changed; }

    public IReadOnlyList<EditorToolbarCategory> Categories => _categories.Values.ToArray();
    public IReadOnlyList<EditorToolbarTool> Tools => _tools.Values.ToArray();

    public IDisposable RegisterCategory(EditorToolbarCategory category)
    {
        ArgumentNullException.ThrowIfNull(category);
        if (!_categories.TryAdd(category.Id, category))
            throw new InvalidOperationException($"Toolbar category '{category.Id}' is already registered");
        _changed();
        return new EditorRegistration(() => RemoveCategory(category));
    }

    public IDisposable RegisterTool(EditorToolbarTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (!_categories.ContainsKey(tool.CategoryId))
            throw new InvalidOperationException($"Toolbar category '{tool.CategoryId}' is not registered");
        foreach (var commandId in tool.CommandIds)
            if (!_commands.TryGet(commandId, out _))
                throw new InvalidOperationException($"Editor command '{commandId}' is not registered");
        if (!_tools.TryAdd(tool.Id, tool))
            throw new InvalidOperationException($"Toolbar tool '{tool.Id}' is already registered");
        _changed();
        return new EditorRegistration(() => RemoveTool(tool));
    }

    internal IReadOnlyList<EditorToolbarCategory> OrderedCategories(EditorToolbarAlignment alignment) =>
        _categories.Values.Where(category => category.Alignment == alignment)
            .OrderBy(category => category.Order).ThenBy(category => category.Id,
                StringComparer.OrdinalIgnoreCase).ToArray();

    internal IReadOnlyList<EditorToolbarTool> OrderedTools(string categoryId) =>
        _tools.Values.Where(tool => string.Equals(tool.CategoryId, categoryId,
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(tool => tool.Order).ThenBy(tool => tool.Id,
                StringComparer.OrdinalIgnoreCase).ToArray();

    internal void Clear() { _tools.Clear(); _categories.Clear(); _changed(); }

    private void RemoveTool(EditorToolbarTool tool)
    {
        if (_tools.TryGetValue(tool.Id, out var current) && ReferenceEquals(current, tool))
        { _tools.Remove(tool.Id); _changed(); }
    }

    private void RemoveCategory(EditorToolbarCategory category)
    {
        if (_categories.TryGetValue(category.Id, out var current) && ReferenceEquals(current, category))
        { _categories.Remove(category.Id); _changed(); }
    }
}

public sealed class EditorMenuRegistry
{
    private readonly Dictionary<string, EditorMenu> _menus = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<EditorMenuCategory> _categories = new();
    private readonly List<EditorMenuItem> _items = new();
    private readonly EditorCommandRegistry _commands;
    private readonly Action _changed;

    internal EditorMenuRegistry(EditorCommandRegistry commands, Action changed)
    { _commands = commands; _changed = changed; }

    public IReadOnlyList<EditorMenu> Menus => _menus.Values.ToArray();
    public IReadOnlyList<EditorMenuCategory> Categories => _categories.ToArray();
    public IReadOnlyList<EditorMenuItem> Items => _items.ToArray();

    public IDisposable RegisterMenu(EditorMenu menu)
    {
        ArgumentNullException.ThrowIfNull(menu);
        if (!_menus.TryAdd(menu.Id, menu))
            throw new InvalidOperationException($"Editor menu '{menu.Id}' is already registered");
        _changed();
        return new EditorRegistration(() => { if (_menus.Remove(menu.Id)) _changed(); });
    }

    public IDisposable RegisterCategory(EditorMenuCategory category)
    {
        ArgumentNullException.ThrowIfNull(category);
        if (!_menus.ContainsKey(category.MenuId))
            throw new InvalidOperationException($"Editor menu '{category.MenuId}' is not registered");
        if (_categories.Any(value => value.MenuId.Equals(category.MenuId,
                StringComparison.OrdinalIgnoreCase) && value.Id.Equals(category.Id,
                StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Menu category '{category.MenuId}/{category.Id}' is already registered");
        _categories.Add(category); _changed();
        return new EditorRegistration(() => { if (_categories.Remove(category)) _changed(); });
    }

    public IDisposable RegisterItem(EditorMenuItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!_categories.Any(value => value.MenuId.Equals(item.MenuId,
                StringComparison.OrdinalIgnoreCase) && value.Id.Equals(item.CategoryId,
                StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Menu category '{item.MenuId}/{item.CategoryId}' is not registered");
        if (!_commands.TryGet(item.CommandId, out _))
            throw new InvalidOperationException($"Editor command '{item.CommandId}' is not registered");
        if (_items.Any(value => value.MenuId.Equals(item.MenuId,
                StringComparison.OrdinalIgnoreCase) && value.CategoryId.Equals(item.CategoryId,
                StringComparison.OrdinalIgnoreCase) && value.CommandId.Equals(item.CommandId,
                StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Menu item '{item.CommandId}' is already registered in '{item.MenuId}/{item.CategoryId}'");
        _items.Add(item); _changed();
        return new EditorRegistration(() => { if (_items.Remove(item)) _changed(); });
    }

    internal IReadOnlyList<EditorMenu> OrderedMenus() => _menus.Values
        .OrderBy(menu => menu.Order).ThenBy(menu => menu.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    internal IReadOnlyList<EditorMenuCategory> OrderedCategories(string menuId) => _categories
        .Where(value => value.MenuId.Equals(menuId, StringComparison.OrdinalIgnoreCase))
        .OrderBy(value => value.Order).ThenBy(value => value.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    internal IReadOnlyList<EditorMenuItem> OrderedItems(string menuId, string categoryId) => _items
        .Where(value => value.MenuId.Equals(menuId, StringComparison.OrdinalIgnoreCase) &&
            value.CategoryId.Equals(categoryId, StringComparison.OrdinalIgnoreCase))
        .OrderBy(value => value.Order).ThenBy(value => value.CommandId, StringComparer.OrdinalIgnoreCase).ToArray();

    internal void Clear() { _items.Clear(); _categories.Clear(); _menus.Clear(); _changed(); }
}

public sealed class EditorContextMenuRegistry
{
    private readonly List<EditorContextMenuCategory> _categories = new();
    private readonly List<EditorContextMenuItem> _items = new();
    private readonly EditorCommandRegistry _commands;
    private readonly Action _changed;

    internal EditorContextMenuRegistry(EditorCommandRegistry commands, Action changed)
    { _commands = commands; _changed = changed; }

    public IReadOnlyList<EditorContextMenuCategory> Categories => _categories.ToArray();
    public IReadOnlyList<EditorContextMenuItem> Items => _items.ToArray();

    public IDisposable RegisterCategory(EditorContextMenuCategory category)
    {
        ArgumentNullException.ThrowIfNull(category);
        if (_categories.Any(value => value.Context == category.Context &&
                value.Id.Equals(category.Id, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Context menu category '{category.Context}/{category.Id}' is already registered");
        _categories.Add(category); _changed();
        return new EditorRegistration(() => { if (_categories.Remove(category)) _changed(); });
    }

    public IDisposable RegisterItem(EditorContextMenuItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!_categories.Any(value => value.Context == item.Context &&
                value.Id.Equals(item.CategoryId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Context menu category '{item.Context}/{item.CategoryId}' is not registered");
        if (!_commands.TryGet(item.CommandId, out _))
            throw new InvalidOperationException($"Editor command '{item.CommandId}' is not registered");
        if (_items.Any(value => value.Context == item.Context &&
                value.CategoryId.Equals(item.CategoryId, StringComparison.OrdinalIgnoreCase) &&
                value.CommandId.Equals(item.CommandId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Context menu item '{item.CommandId}' is already registered in '{item.Context}/{item.CategoryId}'");
        _items.Add(item); _changed();
        return new EditorRegistration(() => { if (_items.Remove(item)) _changed(); });
    }

    internal IReadOnlyList<EditorContextMenuCategory> OrderedCategories(EditorContextMenuKind context) =>
        _categories.Where(value => value.Context == context).OrderBy(value => value.Order)
            .ThenBy(value => value.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    internal IReadOnlyList<EditorContextMenuItem> OrderedItems(EditorContextMenuKind context, string categoryId) =>
        _items.Where(value => value.Context == context &&
                value.CategoryId.Equals(categoryId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(value => value.Order).ThenBy(value => value.CommandId,
                StringComparer.OrdinalIgnoreCase).ToArray();

    internal void Clear() { _items.Clear(); _categories.Clear(); _changed(); }
}

internal sealed class EditorRegistration : IDisposable
{
    private Action? _dispose;
    public EditorRegistration(Action dispose) => _dispose = dispose;
    public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
}

internal static class EditorExtensionIds
{
    public static string Validate(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("An extension id is required", parameterName);
        var normalized = value.Trim();
        if (normalized.Any(character => char.IsWhiteSpace(character) || char.IsControl(character) ||
                character is '<' or '>' or '"' or '\''))
            throw new ArgumentException($"Extension id '{value}' contains unsupported characters", parameterName);
        return normalized;
    }

    public static string RequireLabel(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A display label is required", parameterName)
            : value.Trim();

    public static string? ValidateIcon(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var icon = value.Trim();
        if (icon.Any(character => !(char.IsLetterOrDigit(character) || character is '-' or '_')))
            throw new ArgumentException("An icon must be an engine icon name without a path", parameterName);
        return icon;
    }
}
