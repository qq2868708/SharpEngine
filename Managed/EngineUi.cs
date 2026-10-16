namespace SharpEngine.UI;

/// <summary>Small state object shared by the editor shell and its XML document.</summary>
public sealed class EngineUiState
{
    public string ProjectName { get; set; } = "No Project";
    public string ProjectPath { get; set; } = "Choose a project to begin";
    public string SelectedAsset { get; set; } = "No asset selected";
    public string Status { get; set; } = "Ready";
    public float FramesPerSecond { get; set; }

    internal UiBindingContext ToBindings()
    {
        var bindings = new UiBindingContext();
        bindings.Set("Editor.ProjectName", ProjectName);
        bindings.Set("Editor.ProjectPath", ProjectPath);
        bindings.Set("Editor.SelectedAsset", SelectedAsset);
        bindings.Set("Editor.Status", Status);
        bindings.Set("Editor.Fps", $"{FramesPerSecond:0.0} FPS");
        return bindings;
    }
}

/// <summary>
/// The engine's own UI shell. It intentionally depends only on the renderer-neutral
/// UI model, so the native Vulkan renderer can consume UiDrawList later.
/// </summary>
public sealed class EngineUiController
{
    private const string BuiltInDocument = """
        <?xml version="1.0" encoding="utf-8"?>
        <ui version="1">
          <panel id="engine" layout="column" padding="16" gap="8">
            <row id="toolbar" height="40" gap="8">
              <text id="brand" width="220" value="SharpEngine" fontSize="20" />
              <button id="newProject" width="120" text="New Project" action="editor.new-project" />
              <button id="openProject" width="120" text="Open Project" action="editor.open-project" />
              <button id="saveProject" width="90" text="Save" action="editor.save" />
            </row>
            <row id="workspace" gap="12">
              <panel id="projectBrowser" width="240" layout="column" padding="12" gap="6">
                <text id="projectName" bind="Editor.ProjectName" />
                <text id="projectPath" bind="Editor.ProjectPath" />
                <text id="selectedAsset" bind="Editor.SelectedAsset" />
              </panel>
              <panel id="viewport" layout="column" padding="12">
                <text id="viewportTitle" value="Viewport" />
                <spacer id="viewportSpace" />
              </panel>
              <panel id="inspector" width="240" layout="column" padding="12" gap="6">
                <text id="inspectorTitle" value="Inspector" />
                <text id="status" bind="Editor.Status" />
              </panel>
            </row>
            <row id="statusBar" height="24" gap="12">
              <text id="statusText" bind="Editor.Status" />
              <text id="fps" bind="Editor.Fps" />
            </row>
          </panel>
        </ui>
        """;

    public EngineUiController(UiDocumentModel document,
        EngineUiState? state = null)
    {
        Document = document ?? throw new ArgumentNullException(nameof(document));
        State = state ?? new EngineUiState();
        Interactions.Register("editor.new-project", _ => NewProjectRequested?.Invoke());
        Interactions.Register("editor.open-project", _ => OpenProjectRequested?.Invoke());
        Interactions.Register("editor.save", _ => SaveRequested?.Invoke());
        foreach (var action in new[] {
            "editor.play", "editor.pause", "editor.stop", "editor.project-menu",
            "editor.inspector-menu", "editor.select-scene", "editor.select-prefab",
            "editor.select-material", "editor.select-camera", "editor.select-light",
            "editor.search", "editor.tool-move", "editor.tool-rotate",
            "editor.tool-scale" })
        {
            var actionName = action;
            Interactions.Register(actionName, _ => ActionRequested?.Invoke(actionName));
        }
        Interactions.Register("editor.play", _ => PlayRequested?.Invoke());
        Interactions.Register("editor.file-menu", _ => MenuRequested?.Invoke("File"));
        Interactions.Register("editor.edit-menu", _ => MenuRequested?.Invoke("Edit"));
        Interactions.Register("editor.assets-menu", _ => MenuRequested?.Invoke("Assets"));
        Interactions.Register("editor.window-menu", _ => MenuRequested?.Invoke("Window"));
        Interactions.Register("editor.help-menu", _ => MenuRequested?.Invoke("Help"));
        Interactions.Register("editor.select-assets", _ =>
            AssetSelectedRequested?.Invoke("Assets"));
        Interactions.Register("editor.select-scenes", _ =>
            AssetSelectedRequested?.Invoke("Scenes"));
        Interactions.Register("editor.select-prefabs", _ =>
            AssetSelectedRequested?.Invoke("Prefabs"));
    }

    public UiDocumentModel Document { get; }
    public EngineUiState State { get; }
    public UiInteraction Interactions { get; } = new();
    public event Action? NewProjectRequested;
    public event Action? OpenProjectRequested;
    public event Action? SaveRequested;
    public event Action<string>? ActionRequested;
    public event Action? PlayRequested;
    public event Action<string>? MenuRequested;
    public event Action<string>? AssetSelectedRequested;

    public UiDrawList BuildFrame(UiSize viewport)
    {
        return UiLayout.BuildDrawList(Document, viewport, State.ToBindings());
    }

    /// <summary>
    /// Returns the stable UI contract used by automation and future RmlUi/Vulkan
    /// frontends. Bounds are refreshed using the same layout pass as rendering.
    /// </summary>
    public UiSemanticSnapshot BuildSemanticSnapshot(UiSize viewport)
    {
        var bindings = State.ToBindings();
        UiLayout.BuildDrawList(Document, viewport, bindings);
        return UiSemanticSnapshot.Create(Document, bindings);
    }

    public string BuildSemanticSnapshotJson(UiSize viewport) =>
        BuildSemanticSnapshot(viewport).ToJson();

    public bool Activate(string nodeId) => Interactions.Activate(Document, nodeId);

    public static EngineUiController CreateDefault(string? contentRoot = null)
    {
        foreach (var candidate in CandidatePaths(contentRoot))
        {
            if (File.Exists(candidate))
            {
                return new EngineUiController(UiDocumentModel.Load(candidate));
            }
        }

        return new EngineUiController(
            UiDocumentModel.Parse(BuiltInDocument, "built-in:EngineShell.ui.xml"));
    }

    private static IEnumerable<string> CandidatePaths(string? contentRoot)
    {
        if (!string.IsNullOrWhiteSpace(contentRoot))
        {
            yield return Path.Combine(contentRoot, "UI", "EngineShell.ui.xml");
            yield return Path.Combine(contentRoot, "Content", "UI", "EngineShell.ui.xml");
        }

        yield return Path.Combine(AppContext.BaseDirectory, "Content", "UI",
            "EngineShell.ui.xml");
    }
}
