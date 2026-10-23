namespace SharpEngine.UI;

using System.Text.Json.Nodes;

public sealed record UiViewModelContract(string Id, int Version,
    string RuntimeType, string JsonSchema)
{
    public string Key
    {
        get
        {
            return $"{Id}@{Version}";
        }
    }
}

/// <summary>Result of asking the runtime to present a UI screen. The screen asset is
/// authoritative; the presented document is transient runtime state.</summary>
public sealed record UiScreenResult(bool Success, string Code, string? AssetId,
    string Message);

/// <summary>One request to present a screen. <paramref name="Values"/> and
/// <paramref name="Parts"/> are per-use data: a <c>&lt;template&gt;</c> screen renders
/// them each time, while a complete document ignores them.</summary>
public sealed record UiScreenRequest(string AssetId, JsonObject? Values = null,
    IReadOnlyDictionary<string, IUiLayoutPart>? Parts = null,
    float Width = 1280, float Height = 720);

/// <summary>Native presentation of one runtime screen. Implemented by the host that
/// owns the UI shell; the runtime resolves project assets and hands over RML text
/// plus the authoritative source path so relative stylesheets keep resolving.</summary>
public interface IUiScreenHost
{
    bool SetDocument(string Rml, string SourcePath, out string Message);

    bool ClearDocument();
}

public interface IUiRuntime
{
    IReadOnlyList<UiViewModelContract> ViewModelContracts
    {
        get;
    }

    IDisposable RegisterViewModel<T>(string Id, int Version) where T : class;

    /// <summary>Asset id currently presented by game code, or null when no screen is
    /// shown. This is transient state; it is never persisted into project assets.</summary>
    string? ActiveScreenId
    {
        get;
    }

    /// <summary>Presents a project UI asset. Accepts <c>UiDocument</c> (<c>.sui</c>) and
    /// <c>RmlDocument</c> (<c>.rml</c>) assets and replaces any visible screen.</summary>
    UiScreenResult ShowScreen(string AssetId);

    /// <summary>Presents a screen with per-use values and UI parts. A
    /// <c>&lt;template&gt;</c> definition is rendered with these values every time, so
    /// the same definition can back several independent instances.</summary>
    UiScreenResult ShowScreen(UiScreenRequest Request);

    /// <summary>Routes one native UI action to the parts of the active screen. When a
    /// part handles it, the screen is recomposed so the new state is presented.</summary>
    bool HandleScreenAction(string Action);

    /// <summary>Removes the visible screen without unloading the UI context.</summary>
    void ClearScreen();
}
