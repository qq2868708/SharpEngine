using System.Net;
using System.Text;
using SharpEngine.Logging;

namespace SharpEngine.Editor;

/// <summary>Engine-owned custom bar surfaces. Extensions may fill a surface, but may not create or delete one.</summary>
public sealed record EditorCustomBarSurface(string Id, string Label);

public sealed class EditorCustomBarContext
{
    private readonly EditorRuntime Runtime;

    internal EditorCustomBarContext(EditorRuntime Runtime, string BarId)
    {
        this.Runtime = Runtime;
        this.BarId = BarId;
    }

    public string BarId { get; }
    public EditorCommandContext CommandContext
    {
        get
        {
            return Runtime.CommandContext;
        }
    }

    public bool CanExecute(string CommandId)
    {
        if (!Runtime.Commands.TryGet(CommandId, out var Command))
        {
            return false;
        }

        return Runtime.Commands.CanExecute(Command, CommandContext);
    }

    public bool IsChecked(string CommandId)
    {
        if (!Runtime.Commands.TryGet(CommandId, out var Command))
        {
            return false;
        }

        return Runtime.Commands.IsChecked(Command, CommandContext);
    }

    public string CommandLabel(string CommandId, string? Fallback = null)
    {
        if (!Runtime.Commands.TryGet(CommandId, out var Command))
        {
            return Fallback ?? CommandId;
        }

        return Runtime.ResolveCommandLabel(Command, Fallback);
    }

    public void SetStatus(string Text) => Runtime.SetStatusFromExtension(Text);

    public string CommandButton(string CommandId, string? Label = null,
        string? Icon = null, string? Tooltip = null, string CssClass = "custom-bar-button")
    {
        if (!Runtime.Commands.TryGet(CommandId, out var Command))
        {
            return string.Empty;
        }

        var Enabled = Runtime.Commands.CanExecute(Command, CommandContext);
        var Checked = Runtime.Commands.IsChecked(Command, CommandContext);
        var ResolvedLabel = Label ?? Runtime.ResolveCommandLabel(Command, null);
        var Classes = string.IsNullOrWhiteSpace(CssClass) ? "custom-bar-button" : CssClass.Trim();
        var Markup = new StringBuilder("<button class=\"")
            .Append(Escape(Classes))
            .Append(Checked ? " checked" : string.Empty)
            .AppendAvailability(Enabled)
            .AppendAction("editor.command:" + Command.Id, Enabled)
            .Append(" title=\"")
            .Append(Escape(Tooltip ?? Command.Tooltip ?? ResolvedLabel))
            .Append("\">");
        if (Icon is not null)
        {
            Markup.Append("<img class=\"icon\" src=\"")
                .Append(Escape(EditorResourceUris.IconSource(Icon)))
                .Append("\" />");
        }

        if (Icon is null || Label is not null)
        {
            Markup.Append(Escape(ResolvedLabel));
        }

        Markup.Append("</button>");
        return Markup.ToString();
    }

    public string Group(string Label, string Content, string CssClass = "custom-bar-group") =>
        $"<div class=\"{Escape(CssClass)}\" data-custom-bar-group=\"{Escape(Label)}\"><span class=\"custom-bar-group-label\">{Escape(Label)}</span>{Content}</div>";

    internal static string Escape(string Value) => WebUtility.HtmlEncode(Value);
}

public sealed class EditorMenuExtensionContext
{
    private readonly EditorMenuRegistry Registry;
    private readonly List<IDisposable> Registrations;

    internal EditorMenuExtensionContext(EditorMenuRegistry Registry, string MenuId,
        List<IDisposable> Registrations)
    {
        this.Registry = Registry;
        this.MenuId = MenuId;
        this.Registrations = Registrations;
    }

    public string MenuId { get; }

    public void RegisterCategory(string Id, int Order = 0)
    {
        Registrations.Add(Registry.RegisterCategory(new EditorMenuCategory(MenuId, Id, Order)));
    }

    public void RegisterCommand(string CategoryId, string CommandId, int Order = 0)
    {
        Registrations.Add(Registry.RegisterItem(new EditorMenuItem(MenuId, CategoryId, CommandId, Order)));
    }
}

public sealed class EditorToolbarExtensionContext
{
    private readonly EditorToolbarRegistry Registry;
    private readonly List<IDisposable> Registrations;

    internal EditorToolbarExtensionContext(EditorToolbarRegistry Registry, string ToolbarId,
        List<IDisposable> Registrations)
    {
        this.Registry = Registry;
        this.ToolbarId = ToolbarId;
        this.Registrations = Registrations;
    }

    public string ToolbarId { get; }

    public void RegisterCategory(string Id, int Order = 0,
        EditorToolbarAlignment Alignment = EditorToolbarAlignment.Left)
    {
        Registrations.Add(Registry.RegisterCategory(new EditorToolbarCategory(Id, Order, Alignment)));
    }

    public void RegisterTool(string Id, string CategoryId, string Label,
        IEnumerable<string> CommandIds, int Order = 0, string? Tooltip = null,
        string? Icon = null)
    {
        Registrations.Add(Registry.RegisterTool(new EditorToolbarTool(Id, CategoryId, Label,
            CommandIds, Order, Tooltip, Icon)));
    }
}

public sealed class EditorCustomBarRegistry
{
    public const string ViewportSurfaceId = "viewport";
    public const string TimelineSurfaceId = "timeline";
    public const string MainToolbarId = "main";

    private readonly Dictionary<string, EditorCustomBarSurface> Surfaces =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<ICustomBarExtension>> Extensions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Action Changed;
    private readonly EditorRuntime Runtime;

    internal EditorCustomBarRegistry(EditorRuntime Runtime, Action Changed)
    {
        this.Runtime = Runtime;
        this.Changed = Changed;
        RegisterSurface(new EditorCustomBarSurface(ViewportSurfaceId, "Viewport"));
        RegisterSurface(new EditorCustomBarSurface(TimelineSurfaceId, "Animation Timeline"));
    }

    public IReadOnlyList<EditorCustomBarSurface> RegisteredSurfaces
    {
        get
        {
            return Surfaces.Values.ToArray();
        }
    }

    public IDisposable Register(ICustomBarExtension Extension)
    {
        ArgumentNullException.ThrowIfNull(Extension);
        var BarId = EditorExtensionIds.Validate(Extension.BarId, nameof(Extension.BarId));
        if (!Surfaces.ContainsKey(BarId))
        {
            throw new InvalidOperationException(
                $"Custom bar surface '{BarId}' is not created by the engine");
        }

        var Items = Extensions[BarId];
        if (Items.Any(Value => ReferenceEquals(Value, Extension)))
        {
            throw new InvalidOperationException($"Custom bar extension '{BarId}' is already registered");
        }

        Items.Add(Extension);
        Changed();
        return new EditorRegistration(() => Unregister(BarId, Extension));
    }

    public string BuildMarkup(string BarId)
    {
        if (!Surfaces.ContainsKey(BarId))
        {
            throw new KeyNotFoundException($"Custom bar surface '{BarId}' is not registered");
        }

        var Markup = new StringBuilder();
        foreach (var Extension in Extensions[BarId])
        {
            string Value;
            try
            {
                Value = Extension.BuildMarkup(new EditorCustomBarContext(Runtime, BarId));
            }
            catch (Exception Exception)
            {
                SharpLog.Error("EditorExtension", $"Custom bar '{BarId}' failed to build", Exception);
                continue;
            }

            if (Extension.Mode == EditorCustomBarMode.Replace)
            {
                Markup.Clear();
            }

            Markup.Append(Value);
        }

        return $"<div class=\"custom-bar-surface custom-bar-{EditorCustomBarContext.Escape(BarId)}\">{Markup}</div>";
    }

    internal object Describe()
    {
        return Surfaces.Values.OrderBy(Surface => Surface.Id, StringComparer.OrdinalIgnoreCase)
            .Select(Surface => new
            {
                id = Surface.Id,
                label = Surface.Label,
                extensionCount = Extensions[Surface.Id].Count,
            }).ToArray();
    }

    internal void Clear()
    {
        foreach (var Items in Extensions.Values)
        {
            Items.Clear();
        }

        Changed();
    }

    private void RegisterSurface(EditorCustomBarSurface Surface)
    {
        Surfaces.Add(Surface.Id, Surface);
        Extensions.Add(Surface.Id, new List<ICustomBarExtension>());
    }

    private void Unregister(string BarId, ICustomBarExtension Extension)
    {
        var Items = Extensions[BarId];
        if (Items.Remove(Extension))
        {
            Changed();
        }
    }
}

internal static class EditorExtensionContextRegistration
{
    public static IDisposable Register(EditorMenuRegistry Registry, IMenuExtension Extension)
    {
        var Registrations = new List<IDisposable>();
        try
        {
            Extension.Extend(new EditorMenuExtensionContext(Registry, Extension.MenuId, Registrations));
            return new EditorRegistration(() => Dispose(Registrations));
        }
        catch
        {
            Dispose(Registrations);
            throw;
        }
    }

    public static IDisposable Register(EditorToolbarRegistry Registry, IToolBarExtension Extension)
    {
        var Registrations = new List<IDisposable>();
        try
        {
            Extension.Extend(new EditorToolbarExtensionContext(Registry, Extension.ToolbarId, Registrations));
            return new EditorRegistration(() => Dispose(Registrations));
        }
        catch
        {
            Dispose(Registrations);
            throw;
        }
    }

    private static void Dispose(List<IDisposable> Registrations)
    {
        for (var Index = Registrations.Count - 1; Index >= 0; Index--)
        {
            Registrations[Index].Dispose();
        }

        Registrations.Clear();
    }
}
