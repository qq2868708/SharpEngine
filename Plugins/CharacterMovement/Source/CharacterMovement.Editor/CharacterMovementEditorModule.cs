using SharpEngine.Components;
using SharpEngine.Editor;
using SharpEngine.Modules;
using SharpEngine.World;

[assembly: SharpEngineModule("SharpEngine.CharacterMovement.Editor", "Editor")]

namespace SharpEngine.CharacterMovement;

/// <summary>
/// Editor surface for Character Movement: one command inspects the selected character and reports
/// whether its mode and modifier composition is complete, so authors do not have to guess at runtime.
/// </summary>
public sealed class CharacterMovementEditorModule : ISharpEngineModule
{
    internal const string CharacterMovementTypeId = "6f0c3d1a-8b2e-4f7c-9a51-2c4e8d3b7a10";
    private const string CommandId = "character-movement.describe";

    private readonly List<IDisposable> Registrations = [];

    public void Startup(ModuleContext Context)
    {
        var Editor = (Context as EditorModuleContext)?.Editor ??
            throw new InvalidOperationException("Character Movement editor requires the Editor module context.");
        Registrations.Add(Editor.Commands.Register(new EditorCommand(CommandId,
            "Describe Character Movement", Execute, CanExecute,
            "Reports the selected character's modes, modifiers, and whether engine.movement is overridden.")));
        Registrations.Add(Editor.Menus.RegisterCategory(
            new EditorMenuCategory("window", "character-movement", 950)));
        Registrations.Add(Editor.Menus.RegisterItem(
            new EditorMenuItem("window", "character-movement", CommandId, 100)));
    }

    public void Shutdown()
    {
        foreach (var Registration in Registrations.AsEnumerable().Reverse())
        {
            Registration.Dispose();
        }
        Registrations.Clear();
    }

    private static bool CanExecute(EditorCommandContext Context)
    {
        return Context.PlayState == EditorPlayState.Stopped && Report(Context) is not null;
    }

    private static void Execute(EditorCommandContext Context)
    {
        var Message = Report(Context);
        Context.SetStatus(Message ?? "Select an entity with Character Movement first.");
    }

    /// <summary>Builds one composition report, or null when the selection has no Character Movement.</summary>
    internal static string? Report(EditorCommandContext Context)
    {
        var Entity = SelectedEntity(Context);
        if (Entity is null)
        {
            return null;
        }
        var Character = Entity.Components.FirstOrDefault(Component =>
            Component.TypeId == CharacterMovementTypeId);
        if (Character is null)
        {
            return null;
        }
        var Registry = Context.Runtime.Components;
        var GroundModeId = Character.State["GroundModeId"]?.GetValue<string>() ?? "";
        var AirModeId = Character.State["AirModeId"]?.GetValue<string>() ?? "";
        var Modes = Entity.Components
            .Select(Component => ModeIdOf(Registry, Component.TypeId))
            .OfType<string>()
            .OrderBy(Id => Id, StringComparer.Ordinal)
            .ToArray();
        var Modifiers = Entity.Components
            .Where(Component => IsAssignable<ICharacterMovementModifier>(Registry, Component.TypeId))
            .Select(Component => TypeName(Registry, Component.TypeId))
            .OrderBy(Name => Name, StringComparer.Ordinal)
            .ToArray();
        var Missing = new[] { GroundModeId, AirModeId }
            .Where(Id => Id.Length != 0 && !Modes.Contains(Id, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var Legacy = Entity.Components.Any(Component => Component.TypeId == "engine.movement");
        return $"Character Movement on '{Entity.Name}': ground={GroundModeId}; air={AirModeId}; modes=" +
            (Modes.Length == 0 ? "none" : string.Join(",", Modes)) +
            "; modifiers=" + (Modifiers.Length == 0 ? "none" : string.Join(",", Modifiers)) +
            (Legacy ? "; engine.movement present and will be overridden" : "") +
            (Missing.Length == 0 ? "" : "; missing modes: " + string.Join(",", Missing));
    }

    private static WorldEntity? SelectedEntity(EditorCommandContext Context)
    {
        var EntityId = Context.SelectedEntityId;
        if (EntityId is null || Context.World is null)
        {
            return null;
        }
        return Context.World.Entities.FirstOrDefault(Entity => Entity.Id == EntityId);
    }

    private static string? ModeIdOf(ComponentRegistry? Registry, string TypeId)
    {
        var RuntimeType = Registry?.Find(TypeId)?.RuntimeType;
        if (RuntimeType is null || !typeof(ICharacterMovementMode).IsAssignableFrom(RuntimeType))
        {
            return null;
        }
        return CharacterMovementModes.ModeIdOf(RuntimeType);
    }

    private static bool IsAssignable<T>(ComponentRegistry? Registry, string TypeId)
    {
        var RuntimeType = Registry?.Find(TypeId)?.RuntimeType;
        return RuntimeType is not null && typeof(T).IsAssignableFrom(RuntimeType);
    }

    private static string TypeName(ComponentRegistry? Registry, string TypeId)
    {
        return Registry?.Find(TypeId)?.RuntimeType.Name ?? TypeId;
    }
}
