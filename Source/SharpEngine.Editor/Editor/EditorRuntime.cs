using SharpEngine.Settings;
using SharpEngine.Input;
using SharpEngine.Interop;
using System.Collections.Concurrent;
using System.Globalization;
using System.Diagnostics;
using System.Net;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharpEngine.Assets;
using SharpEngine.Components;
using SharpEngine.Editor.Layout;
using SharpEngine.Logging;
using SharpEngine.UI;
using SharpEngine.World;

namespace SharpEngine.Editor;

public enum EditorPlayState { Stopped, Playing, Paused }

/// <summary>The public managed editor session used by the built-in host and
/// generated projects. RmlUi owns layout/input; this class owns scene and assets.</summary>
public sealed partial class EditorRuntime
{
    private readonly nint _windowHandle;
    private readonly uint _windowWidth;
    private readonly uint _windowHeight;
    private readonly NativeEngineApi _nativeApi;
    private readonly NativeGpuSceneBridge GpuSceneBridge;
    private readonly RenderTelemetry RenderTelemetryService;
    private bool GpuFallbackReported;
    private readonly EditorCommandRegistry _commands;
    private readonly EditorMenuRegistry _menus;
    private readonly EditorToolbarRegistry _toolbar;
    private readonly EditorContextMenuRegistry _contextMenus;
    private readonly EditorCustomBarRegistry CustomBarRegistry;
    private readonly EditorCommandContext _commandContext;
    private readonly EditorContext _context;
    private readonly UiRuntimeService _uiRuntime = new();
    private readonly Dictionary<IEditorExtension, IDisposable> _extensions =
        new(ReferenceEqualityComparer.Instance);
    private readonly Queue<string> _messages = new();
    private readonly Queue<string> _consoleMessages = new();
    private readonly Queue<SharpLogEvent> _logHistory = new();
    private readonly ConcurrentQueue<SharpLogEvent> _pendingLogs = new();
    private readonly ConcurrentDictionary<string, byte> _knownLogEntries = new(StringComparer.Ordinal);
    private const int LogHistoryLimit = 200;
    private readonly IDisposable _logSubscription;
    private long _logFilePosition;
    private string _logFileRemainder = string.Empty;
    private AssetRegistry? _assets;
    private DataAssetTypeRegistry DataAssetTypes = new();
    private ContentManagerCoordinator? _contentManagers;
    private readonly EditorPopupMenu PopupMenus;
    private readonly UiContextMenuRouter ContextMenuRouting = new();
    private WorldDocument? _world;
    private string _engineRoot = string.Empty;
    private string? _projectRoot;
    private string? _projectManifest;
    private WorldAuthoringSession? _authoring;
    private bool _worldIsUntitled;
    private string? _worldAssetId;
    private EditorSelection _selection = EditorSelection.None;
    private string _hierarchySearch = string.Empty;
    private readonly HashSet<string> _collapsedHierarchy = new(StringComparer.OrdinalIgnoreCase);
    private float _seconds;
    private float _statsElapsed;
    private int _frameCount;
    private UiRect _lastViewport;
    private readonly Dictionary<string, string> _uiText = new(StringComparer.Ordinal);
    private bool _navigationInitialized;
    private bool _orbiting;
    private bool _panning;
    private float _lastPointerX;
    private float _lastPointerY;
    private Vector3 _orbitTarget;
    private float _orbitYaw;
    private float _orbitPitch;
    private float _orbitDistance;
    private readonly HashSet<uint> _pressedKeys = new();
    private bool _viewportKeyFocus;
    private string _viewportTool = "move";
    private bool _gizmoDragging;
    private TransformGizmoAxis _gizmoAxis;
    private Vector2 _gizmoStartPointer;
    private Vector2 _gizmoScreenDirection;
    private Vector3 _gizmoWorldDirection;
    private Vector3 _gizmoStartWorldPosition;
    private WorldVector3 _gizmoStartRotation;
    private WorldVector3 _gizmoStartScale;
    private float _gizmoUnitsPerPixel;
    private string? _gizmoTransactionGroupId;
    private bool _extensionUiDirty = true;
    private string _menuMarkup = string.Empty;
    private string _toolbarLeftMarkup = string.Empty;
    private string _toolbarCenterMarkup = string.Empty;
    private string _toolbarRightMarkup = string.Empty;
    private string ViewportCustomBarMarkup = string.Empty;
    private string TimelineCustomBarMarkup = string.Empty;
    private bool _rightPointerPending;
    private bool _rightPointerDragging;
    private int _rightPointerX;
    private int _rightPointerY;
    private bool _dirty;
    private readonly EditorLayoutStore _layoutStore = new();
    private EditorLayoutDocument? _defaultLayout;
    private EditorLayoutSession? _layout;
    private readonly Dictionary<string, string> _dockMarkup = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _contentMarkup = new(StringComparer.Ordinal);
    private readonly HashSet<string> _liveDockHosts = new(StringComparer.Ordinal);
    private EditorMcpSessionHost? _mcpSession;
    private IInputRuntime? _inputRuntime;

    public EditorRuntime(nint windowHandle, uint width, uint height, nint nativeApi)
    {
        _windowHandle = windowHandle;
        _windowWidth = width;
        _windowHeight = height;
        _nativeApi = new NativeEngineApi(nativeApi);
        GpuSceneBridge = new NativeGpuSceneBridge(nativeApi);
        RenderTelemetryService = new RenderTelemetry(nativeApi);
        _commands = new EditorCommandRegistry(MarkExtensionUiDirty);
        _menus = new EditorMenuRegistry(_commands, MarkExtensionUiDirty);
        _toolbar = new EditorToolbarRegistry(_commands, MarkExtensionUiDirty);
        _contextMenus = new EditorContextMenuRegistry(_commands, MarkExtensionUiDirty);
        CustomBarRegistry = new EditorCustomBarRegistry(this, MarkExtensionUiDirty);
        AssetOpeners = new EditorAssetOpenerRegistry(this);
        _commandContext = new EditorCommandContext(this);
        PopupMenus = new EditorPopupMenu(_nativeApi, _commands);
        _context = new EditorContext(this, _commands, _menus, _toolbar, _contextMenus,
            CustomBarRegistry);
        EditorSubsystems = SubsystemLifetime.CreateEditorScope(this);
        _logSubscription = SharpLog.Subscribe(entry =>
        {
            if (_knownLogEntries.TryAdd(LogIdentity(entry), 0))
                _pendingLogs.Enqueue(entry);
        });
    }

    public WorldDocument? World => _world;
    public AssetRegistry? Assets => _assets;
    public ContentManager? Content => _contentManagers?.Active;
    public ContentManagerCoordinator? ContentManagers => _contentManagers;
    public EditorCommandRegistry Commands => _commands;
    public EditorMenuRegistry Menus => _menus;
    public EditorToolbarRegistry Toolbar => _toolbar;
    public EditorContextMenuRegistry ContextMenus => _contextMenus;
    public UiContextMenuRouter ContextMenuRouter
    {
        get
        {
            return ContextMenuRouting;
        }
    }
    public EditorCustomBarRegistry CustomBars
    {
        get
        {
            return CustomBarRegistry;
        }
    }
    public EditorContext Context => _context;
    public EditorAssetOpenerRegistry AssetOpeners { get; }
    public IUiRuntime UiRuntime => _uiRuntime;
    public IInputRuntime? InputRuntime => _inputRuntime;
    public EditorSelection Selection => _selection;
    internal string EngineRoot => _engineRoot;
    internal string? ProjectManifest => _projectManifest;
    internal DataAssetTypeRegistry RegisteredDataAssetTypes => DataAssetTypes;
    internal bool HasLiveUi => _windowHandle != 0 && _nativeApi.HasUiObservability;
    internal McpEditorSessionDescriptor? McpSessionDescriptor => _mcpSession?.Descriptor;
    public string? SelectedEntityId => _selection.Kind == EditorSelectionKind.Entity ? _selection.Id : null;
    public WorldEntity? SelectedEntity => _world?.Entities.FirstOrDefault(entity =>
        string.Equals(entity.Id, SelectedEntityId, StringComparison.OrdinalIgnoreCase));
    public AssetDescriptor? SelectedAsset => _selection.Kind == EditorSelectionKind.Asset && _selection.Id is not null
        ? _assets?.Find(_selection.Id) : null;
    public string? SelectedFolderPath => _selection.Kind == EditorSelectionKind.Folder ? _selection.Path : null;
    public EngineSettings Settings => (EngineSettings)(_developmentSettings?.Get(typeof(EngineSettings)) ??
        throw new InvalidOperationException("Settings are not initialized"));
    public EditorPlayState PlayState { get; private set; }
    public bool TransformSnapEnabled { get; set; }
    public bool IsDirty => _worldIsUntitled || _dirty || (_authoring?.IsDirty ?? false);
    internal EditorCommandContext CommandContext
    {
        get
        {
            return _commandContext;
        }
    }
    public ulong DocumentRevision => _authoring?.DocumentRevision ?? 0;
    public WorldEditResult EditWorld(string Kind, ulong ExpectedRevision,
        Action<SceneData> Edit, string[] AffectedEntityIds)
    {
        if (_authoring is null)
        {
            return WorldEditResult.Failure("WORLD_UNAVAILABLE", "No Authoring World is available", 0, IsDirty);
        }
        if (PlayState != EditorPlayState.Stopped)
        {
            return WorldEditResult.Failure("AUTHORING_DISABLED_DURING_PLAY",
                "Authoring editing requires Stop", DocumentRevision, IsDirty);
        }
        var Result = _authoring.Edit(Kind, ExpectedRevision, Edit, AffectedEntityIds);
        if (Result.Success)
        {
            RefreshLists();
            RefreshInspector();
            RefreshWorldTitle();
            MarkExtensionUiDirty();
        }
        return Result;
    }
    public WorldEditResult ReparentEntity(WorldReparentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_authoring is null)
            return WorldEditResult.Failure("WORLD_UNAVAILABLE",
                "No Authoring World is available", 0, IsDirty);
        if (PlayState != EditorPlayState.Stopped)
            return WorldEditResult.Failure("AUTHORING_DISABLED_DURING_PLAY",
                "Hierarchy editing is disabled while Playing or Paused",
                _authoring.DocumentRevision, IsDirty);
        var result = _authoring.Reparent(request);
        if (result.Success)
        {
            _selection = new EditorSelection(EditorSelectionKind.Entity,
                request.SourceEntityId);
            RefreshHierarchy();
            RefreshInspector();
            RefreshWorldTitle();
            MarkExtensionUiDirty();
        }
        return result;
    }
    public string DescribeSceneJson() => _world?.ToJson() ?? "{}";
    public string DescribeAssetsJson()
    {
        var Assets = JsonNode.Parse(_assets?.ToJson() ?? "{}") as JsonObject ?? new JsonObject();
        Assets["dataAssetTypes"] = new JsonArray(DataAssetTypes.Descriptors
            .OrderBy(Descriptor => Descriptor.TypeId, StringComparer.Ordinal)
            .Select(Descriptor => (JsonNode)new JsonObject
            {
                ["typeId"] = Descriptor.TypeId,
                ["schemaVersion"] = Descriptor.SchemaVersion,
                ["runtimeType"] = Descriptor.RuntimeType.FullName,
                ["schema"] = Descriptor.Schema(),
            }).ToArray());
        return Assets.ToJsonString(new JsonSerializerOptions(JsonSerializerOptions.Default)
        {
            WriteIndented = true,
        });
    }
    public string DescribeContentJson()
    {
        var Snapshot = JsonNode.Parse(_contentManagers?.ToJson() ?? "{}")!.AsObject();
        Snapshot["contextMenus"] = PopupMenus.Describe();
        Snapshot["contextRouting"] = ContextMenuRouting.Describe();
        Snapshot["creation"] = DescribeContentCreation();
        return Snapshot.ToJsonString();
    }
    public string DescribeInputJson() => _inputRuntime?.DescribeJson() ??
        "{\"schemaVersion\":1,\"available\":false}";

    internal void AttachInputRuntime(IInputRuntime? inputRuntime)
    {
        CloseInputDemo();
        _inputRuntime = inputRuntime;
        if (inputRuntime is not null) RegisterInputDemoCommand();
    }
    private static object DescribeTransformGizmo(string tool)
    {
        var definition = WorldViewportRenderer.GetTransformGizmoDefinition(tool);
        var mode = definition.Mode switch
        {
            TransformGizmoMode.Rotate => "rotate",
            TransformGizmoMode.Scale => "scale",
            _ => "move",
        };
        var handle = definition.HandleKind switch
        {
            TransformGizmoHandleKind.RotationRing => "rotation-ring",
            TransformGizmoHandleKind.ScaleBox => "scale-box",
            _ => "axis-arrow",
        };
        return new
        {
            tool = mode,
            mode,
            handle,
            pixelLength = definition.PixelLength,
            pickTolerance = definition.PickTolerance,
            ringRadius = definition.RingRadius,
        };
    }

    public string DescribeEditorJson() => JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        mcp = DescribeMcpLinks(),
        input = JsonNode.Parse(DescribeInputJson()),
        projectCode = JsonNode.Parse(DescribeProjectCodeJson()),
        logging = new
        {
            filePath = SharpLog.FilePath,
            minimumLevel = SharpLog.MinimumLevel.ToString(),
            recent = _messages.ToArray(),
            consoleRecent = _consoleMessages.ToArray(),
            recentEvents = _logHistory.Select(entry => new
            {
                timestamp = entry.Timestamp,
                level = entry.LevelName,
                source = entry.Source,
                category = entry.Category,
                message = entry.Message,
                exception = entry.Exception,
                properties = entry.Properties,
            }).ToArray(),
        },
        inputDemo = new { open = _inputDemoOpen, uiConsumes = _inputDemoConsume,
            uiCount = _inputDemoUiCount, gameplayCount = _inputDemoGameCount,
            x = _inputDemoX, y = _inputDemoY },
        playInputCapture = new
        {
            requested = PlayInputRequested,
            active = PlayInputCaptured,
            releaseShortcut = _shortcutSettings.ReleasePointer.Describe(),
        },
        shortcuts = JsonNode.Parse(DescribeShortcutsJson()),
        scene = _world?.Describe(),
        isDirty = IsDirty,
        documentRevision = DocumentRevision,
        savepointRevision = _authoring?.SavepointRevision ?? 0,
        worldAsset = new
        {
            id = _worldAssetId,
            path = _world?.SourcePath,
            untitled = _worldIsUntitled,
        },
        settings = JsonNode.Parse(DescribeSettingsJson()),
        viewport = new
        {
            mode = "scene3d",
            selectedEntity = SelectedEntityId,
            transformTool = _viewportTool,
            transformGizmoVisible = SelectedEntityId is not null,
            transformGizmo = DescribeTransformGizmo(_viewportTool),
            gizmo = new { position = "top-right", type = "orientation-3d" },
            worldAxes = new { x = "right", y = "up", z = "depth" },
            navigation = new[] { "WASD move", "QE vertical move", "RMB orbit", "MMB pan", "wheel zoom" },
            sceneDirectionalLightCount = _world?.Entities.Count(entity =>
                entity.Kind == WorldEntityKind.Light) ?? 0,
        },
        customBars = CustomBarRegistry.Describe(),
        assetTools = AssetOpeners.Describe(),
        uiEditor = DescribeUiEditorState(),
        layout = _layout is null ? null : new
        {
            revision = _layout.Revision,
            main = EditorLayoutJson.ToJsonNode(_layout.Document.MainHost),
            hiddenPaneIds = _layout.Document.HiddenPaneIds,
            floatingHosts = _layout.FloatingHosts.Select(host => new
            {
                host.HostId,
                root = EditorLayoutJson.ToJsonNode(host.Root),
            }),
        },
        assets = _assets?.Assets.Select(asset => new
        {
            id = asset.Id,
            type = asset.Type,
            name = asset.Name,
            source = asset.Source.ToString(),
            plugin = asset.OwnerId,
            path = asset.RelativePath,
            importer = asset.Importer,
            exists = asset.Exists,
            tags = asset.Tags,
            metadata = asset.Metadata,
        }),
        plugins = _assets?.Plugins.Select(plugin => new
        {
            id = plugin.Id,
            version = plugin.Version,
            source = plugin.Source.ToString(),
            manifest = plugin.ManifestPath,
        }),
        content = _contentManagers is null ? null :
            JsonNode.Parse(DescribeContentJson()),
        extensions = new
        {
            selection = new
            {
                kind = _selection.Kind.ToString(),
                id = _selection.Id,
                path = _selection.Path,
                source = _selection.Source?.ToString(),
            },
            commands = _commands.Commands.Select(command => new
            {
                id = command.Id,
                action = $"editor.command:{command.Id}",
                label = command.Label,
                tooltip = command.Tooltip,
                enabled = _commands.CanExecute(command, _commandContext),
                checkable = command.GetChecked is not null,
                @checked = _commands.IsChecked(command, _commandContext),
            }),
            menus = _menus.Menus,
            menuCategories = _menus.Categories,
            menuItems = _menus.Items,
            toolbarCategories = _toolbar.Categories.Select(category => new
            {
                id = category.Id,
                order = category.Order,
                alignment = category.Alignment.ToString(),
            }),
            toolbarTools = _toolbar.Tools.Select(tool => new
            {
                id = tool.Id,
                categoryId = tool.CategoryId,
                label = SafeToolLabel(tool),
                commandIds = tool.CommandIds,
                order = tool.Order,
                tooltip = tool.Tooltip,
                icon = SafeToolIcon(tool),
            }),
            contextCategories = _contextMenus.Categories.Select(category => new
            {
                context = category.Context.ToString(),
                id = category.Id,
                label = category.Label,
                order = category.Order,
            }),
            contextItems = _contextMenus.Items.Select(item => new
            {
                context = item.Context.ToString(),
                categoryId = item.CategoryId,
                commandId = item.CommandId,
                order = item.Order,
            }),
        },
    }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    private object DescribeMcpLinks()
    {
        ulong stateRevision = 0, frameId = 0;
        if (HasLiveUi)
        {
            using var document = JsonDocument.Parse(DescribeUiJson());
            var root = document.RootElement;
            if (root.TryGetProperty("stateRevision", out var state))
                state.TryGetUInt64(out stateRevision);
            if (root.TryGetProperty("frameId", out var frame))
                frame.TryGetUInt64(out frameId);
        }
        return new
        {
            sessionId = McpSessionDescriptor?.SessionId,
            transport = DescribeMcpState(),
            capabilitiesResource = "sharpengine://capabilities",
            uiResource = HasLiveUi ? "sharpengine://ui" : null,
            inputResource = "sharpengine://input",
            stateRevision,
            frameId,
        };
    }

    internal string DescribeUiJson()
    {
        var json = _nativeApi.DescribeUi(
            checked((ulong)(_layout?.Revision ?? 0)), DocumentRevision);
        var root = JsonNode.Parse(json)?.AsObject() ?? new JsonObject();
        root["layoutDirty"] = _layout?.IsDirty ?? false;
        return root.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    internal string SendUiPointer(string hostId, uint inputEvent, int x, int y,
        uint button, uint buttons, uint modifiers, uint pointerId,
        ulong expectedFrameId, float wheelX, float wheelY,
        string? expectedElementId) =>
        _nativeApi.UiPointer(hostId, inputEvent, x, y, button, buttons,
            modifiers, pointerId, expectedFrameId, wheelX, wheelY,
            expectedElementId);

    internal string SendUiKey(string hostId, uint inputEvent, uint key,
        uint modifiers, string text, ulong expectedFrameId) =>
        _nativeApi.UiKey(hostId, inputEvent, key, modifiers, text,
            expectedFrameId);

    internal byte[]? ReadUiFramePng(ulong frameId, string hostId)
    {
        var frame = _nativeApi.ReadUiFrame(frameId, hostId);
        return frame is null ? null : UiPngEncoder.Encode(frame);
    }

    public void Initialize() { Initialize(null); }

    internal void Initialize(Action<int, string, string>? ReportProgress)
    {
        ReportProgress?.Invoke(36, "SharpEngine.Editor", "Initializing editor services and settings");
        _engineRoot = FindEngineRoot();
        _projectManifest = FindProjectManifest();
        _projectRoot = _projectManifest is null ? null :
            Path.GetDirectoryName(_projectManifest)!;
        SharpLog.Initialize("Editor", _projectRoot, _engineRoot);
        ResetLogFileTail();
        InitializeDevelopmentSettings();
        ApplyEngineShortcuts(Force: true);
        ReportProgress?.Invoke(43, "AssetRegistry", "Scanning engine and project assets");
        _assets = AssetRegistry.Load(_engineRoot, _projectRoot, _projectManifest);
        ReportProgress?.Invoke(51, "EditorLayout", "Restoring editor layout and content browsers");
        _defaultLayout = _layoutStore.LoadDefault(_engineRoot);
        try
        {
            _layout = new EditorLayoutSession(
                _layoutStore.LoadSavedOrDefault(_engineRoot, _projectRoot));
        }
        catch (EditorLayoutException exception)
        {
            SharpLog.Error("EditorLayout", exception.Message, exception);
            foreach (var diagnostic in exception.Diagnostics)
                SharpLog.Error("EditorLayout", $"{diagnostic.Code} {diagnostic.Path}: {diagnostic.Message}");
            _layout = new EditorLayoutSession(_defaultLayout);
        }
        var contentLayout = _layout.Document;
        SetContentManagers(new ContentManagerCoordinator(_assets,
            contentLayout.ContentManagers, contentLayout.ActiveContentManagerId));
        // The content browser normalizes unavailable sources/folders on load.
        // Adopt that initial state before exposing revisions so the first Dock
        // edit does not also commit an unrelated browser correction.
        contentLayout.ContentManagers = _contentManagers.Capture().ToList();
        contentLayout.ActiveContentManagerId = _contentManagers.ActivePaneId;
        _layout = new EditorLayoutSession(contentLayout);
        HideLegacyAssetEditorPanes();
        ReportProgress?.Invoke(57, "World", "Loading the initial world");
        var loadedWorld = WorldAssetWorkflow.LoadInitial(_engineRoot, _projectRoot,
            _projectManifest, _assets);
        _world = loadedWorld.Document;
        _worldIsUntitled = loadedWorld.IsUntitled;
        _worldAssetId = loadedWorld.AssetId;
        _authoring = new WorldAuthoringSession(_world);
        _dirty = false;
        var initialEntity = _world.Entities.FirstOrDefault(e => e.Kind == WorldEntityKind.Mesh &&
            !e.Asset.EndsWith("/plane", StringComparison.OrdinalIgnoreCase))?.Id;
        _selection = initialEntity is null ? EditorSelection.Scene :
            new EditorSelection(EditorSelectionKind.Entity, initialEntity);
        EnsureActiveContentManagerVisible();
        if (_contentManagers.Active.SelectedPath is not null ||
            _contentManagers.Active.SelectedId is not null)
            ApplyActiveContentSelection();
        ReportProgress?.Invoke(63, "EditorUI", "Building editor panels and live session");
        RegisterBuiltInEditorUi();
        RefreshDockUi(force: true);
        RefreshWorldTitle();
        SetText("projectTitle", _projectRoot is null ? "Engine workspace" : Path.GetFileName(_projectRoot));
        SetText("projectPath", _projectRoot ?? _engineRoot);
        RefreshLists();
        RefreshInspector();
        RefreshSettingsUi();
        RefreshViewportModeUi();
        RefreshUiEditor();
        RefreshExtensionUi();
        SetStatus($"Loaded {_world.Name} · {_world.Entities.Count} entities · {_assets.Assets.Count} assets");
        foreach (var diagnostic in _assets.Diagnostics) Log(diagnostic.Message);
        SharpLog.Info("World", $"Loaded source={_world.SourcePath}", new Dictionary<string, string>
        {
            ["entities"] = _world.Entities.Count.ToString(),
            ["meshes"] = _world.MeshCount.ToString(),
            ["camera"] = _world.ActiveCameraId ?? string.Empty,
        });
        SharpLog.Info("Assets", "Asset registry loaded", new Dictionary<string, string>
        {
            ["engine"] = _assets.EngineAssets.Count().ToString(),
            ["project"] = _assets.ProjectAssets.Count().ToString(),
            ["diagnostics"] = _assets.Diagnostics.Count.ToString(),
        });
        SharpLog.Info("ManagedUI", "Managed UI bridge initialized", new Dictionary<string, string>
        {
            ["documentUi"] = _nativeApi.HasDocumentUi.ToString(),
            ["assembly"] = typeof(EditorRuntime).Assembly.GetName().Name ?? string.Empty,
        });
        InitializeMcp();
    }

    private void HideLegacyAssetEditorPanes()
    {
        if (_layout is null)
        {
            return;
        }

        var Changed = false;
        foreach (var PaneId in new[] { EditorPaneIds.Timeline, EditorPaneIds.UiEditor })
        {
            if (!IsPaneVisible(PaneId))
            {
                continue;
            }

            var Result = _layout.Apply(new EditorLayoutCommand(
                EditorLayoutCommandKind.HidePane, PaneId));
            if (!Result.Success)
            {
                SharpLog.Warning("EditorLayout",
                    $"Could not hide legacy asset pane '{PaneId}': {Result.Message}");
            }
            else
            {
                Changed = true;
            }
        }
        if (Changed)
        {
            _layout.AdoptCurrentAsInitialState();
        }
    }

    public void Update(float DeltaSeconds)
    {
        SubsystemLifetime.Update(DeltaSeconds);
        AssetOpeners.Update(DeltaSeconds);
        DrainLogs();
        PollDevelopmentSettings();
        ApplyEngineShortcuts();
        PollMcpConfiguration();
        TickProjectPlugins();
        UpdateInputDemo();
        foreach (var Extension in _extensions.Keys.OfType<IEditorExtensionUpdate>().ToArray())
        {
            try
            {
                Extension.Update(DeltaSeconds);
            }
            catch (Exception Exception)
            {
                SharpLog.Error("EditorExtension", "Editor extension update failed", Exception);
            }
        }
        // Conditions can depend on project C# state. Compare the resulting markup
        // before publishing, so an unchanged state preserves RmlUi focus/hover.
        RefreshExtensionUi();
        if (_world is null || _assets is null)
        {
            _mcpSession?.Pump();
            return;
        }
        _frameCount++;
        UpdateNavigationMovement(DeltaSeconds);
        if (PlayState == EditorPlayState.Playing && float.IsFinite(DeltaSeconds))
        {
            _seconds += Math.Clamp(DeltaSeconds, 0, 0.1f);
            UpdateComponentRuntime(Math.Clamp(DeltaSeconds, 0, 0.1f));
        }
        if (_nativeApi.TryGetViewportRect(out var viewport))
        {
            var SceneStart = Stopwatch.GetTimestamp();
            _lastViewport = viewport;
            SceneRenderPreparation.Prepare(_world, _assets);
            WorldViewportFrame frame;
            var GpuSubmitted = false;
            GpuSceneFrame? GpuFrame = null;
            var TriangleCount = 0;
            if (GpuSceneBridge.Available)
            {
                GpuFrame = GpuSceneBuilder.BuildFrame(_world, viewport, _assets,
                    SelectedEntityId);
                GpuSubmitted = GpuSceneBridge.Submit(GpuFrame, _windowHeight);
                if (GpuSubmitted)
                {
                    TriangleCount = GpuFrame.Instances.Sum(Instance =>
                        Instance.Mesh.Vertices.Count / 3);
                }
                else if (!GpuFallbackReported)
                {
                    GpuFallbackReported = true;
                    SharpLog.Warning("Render",
                        "GPU scene submission failed; using visible CPU fallback");
                }
            }
            if (GpuSubmitted)
            {
                frame = WorldViewportRenderer.BuildOverlayFrame(_world, viewport,
                    SelectedEntityId, _viewportTool) with
                {
                    Diagnostics = GpuFrame!.Diagnostics,
                };
            }
            else
            {
                frame = WorldViewportRenderer.BuildFrame(_world, viewport,
                    SelectedEntityId, _assets, _viewportTool);
                TriangleCount = frame.TriangleCount;
            }
            _nativeApi.SubmitViewportFrame(frame);
            var SceneMilliseconds = Stopwatch.GetElapsedTime(SceneStart)
                .TotalMilliseconds;
            RenderTelemetryService.Record(DeltaSeconds, SceneMilliseconds,
                GpuSceneBridge.Available, GpuSubmitted);
            if (_frameCount == 1)
            {
                SharpLog.Info("Viewport", $"First frame bounds={viewport}", new Dictionary<string, string>
                {
                    ["triangles"] = TriangleCount.ToString(),
                    ["lines"] = frame.LineCount.ToString(),
                    ["camera"] = frame.CameraId ?? string.Empty,
                });
                foreach (var diagnostic in frame.Diagnostics) Log(diagnostic);
            }
            _statsElapsed += Math.Max(0, DeltaSeconds);
            if (_statsElapsed > 0.3f || _frameCount == 1)
            {
                SetText("fps", DeltaSeconds > 0 ? $"{1 / DeltaSeconds:0} FPS" : "— FPS");
                SetText("viewportStats",
                    $"{frame.EntityCount} entities · {TriangleCount} triangles · " +
                    $"{_world.Entities.Count(entity => entity.Kind == WorldEntityKind.Light)} scene lights · {PlayState}");
                SetText("assetSource", $"{PlayState} · {_seconds:0.0}s");
                if (PlayState == EditorPlayState.Playing) RefreshInspector();
                _statsElapsed = 0;
            }
        }
        _mcpSession?.Pump();
    }

    internal string DescribeRenderJson()
    {
        return RenderTelemetryService.Describe(_assets).ToJsonString();
    }

    internal bool TryGetUiElementRect(string Id, out UiRect Bounds)
    {
        return _nativeApi.TryGetUiElementRect(Id, out Bounds);
    }

    internal void SubmitTimelinePreviewFrame(WorldViewportFrame Frame)
    {
        _nativeApi.SubmitTimelinePreviewFrame(Frame);
    }

    public void PointerDown(nint windowHandle, int x, int y, uint button)
    {
        if (PlayState != EditorPlayState.Stopped || button != 1 || _world is null || _assets is null)
        {
            return;
        }
        if (_gizmoDragging) return;
        if (x < _lastViewport.X || y < _lastViewport.Y ||
            x > _lastViewport.Right || y > _lastViewport.Bottom) return;
        if (WorldViewportRenderer.TryPick(_world, _lastViewport, x, y, out var entity, _assets))
            SelectEntity(entity!.Id);
    }

    /// <summary>Scene viewport navigation: RMB orbit, MMB pan, wheel zoom.</summary>
    public void ViewportInput(nint windowHandle, int x, int y, uint inputEvent,
        uint buttons, int wheelDelta)
    {
        if (PlayState != EditorPlayState.Stopped || _world is null || _assets is null ||
            (_lastViewport.Width > 0 &&
             (x < _lastViewport.X || y < _lastViewport.Y ||
               x > _lastViewport.Right || y > _lastViewport.Bottom)))
        {
            return;
        }
        const uint RightButton = 0x0002;
        const uint MiddleButton = 0x0010;
        const uint LeftButton = 0x0001;
        if (inputEvent == 1)
        {
            _cameraTransactionGroup = Guid.NewGuid().ToString("N");
            EnsureNavigationState();
            _lastPointerX = x;
            _lastPointerY = y;
            if ((buttons & LeftButton) != 0 && TryBeginGizmoDrag(x, y)) return;
            if ((buttons & RightButton) != 0)
            {
                _rightPointerPending = true;
                _rightPointerDragging = false;
                _rightPointerX = x;
                _rightPointerY = y;
                _orbiting = false;
            }
            _panning = (buttons & MiddleButton) != 0;
            if (_panning) SetStatus("Camera pan — release MMB to finish");
            return;
        }
        if (inputEvent == 2)
        {
            _cameraTransactionGroup = null;
            if ((buttons & LeftButton) != 0 && _gizmoDragging)
            {
                EndGizmoDrag();
                return;
            }
            if ((buttons & RightButton) != 0)
            {
                if (_rightPointerPending && !_rightPointerDragging)
                {
                    if (WorldViewportRenderer.TryPick(_world, _lastViewport, x, y,
                            out var entity, _assets))
                    {
                        SelectEntity(entity!.Id);
                        ShowContextMenu(EditorContextMenuKind.Object, x, y);
                    }
                    else
                    {
                        InvokeUiAction("editor.select-scene");
                        ShowContextMenu(EditorContextMenuKind.Scene, x, y);
                    }
                }
                _rightPointerPending = false;
                _rightPointerDragging = false;
                _orbiting = false;
            }
            if ((buttons & MiddleButton) != 0) _panning = false;
            return;
        }
        if (_gizmoDragging)
        {
            if (inputEvent == 0) UpdateGizmoDrag(x, y, buttons);
            return;
        }
        if (inputEvent == 3 && wheelDelta != 0)
        {
            _cameraTransactionGroup = null;
            EnsureNavigationState();
            _orbitDistance = Math.Clamp(_orbitDistance *
                MathF.Exp(-wheelDelta / 120f * 0.16f), 0.75f, 250f);
            ApplyNavigationCamera();
            SetStatus($"Camera zoom · distance {_orbitDistance:0.0}");
            return;
        }
        if (inputEvent == 0 && _rightPointerPending && !_rightPointerDragging &&
            (buttons & RightButton) != 0)
        {
            var dragX = x - _rightPointerX;
            var dragY = y - _rightPointerY;
            if (dragX * dragX + dragY * dragY > 16)
            {
                _rightPointerDragging = true;
                _orbiting = true;
                _lastPointerX = x;
                _lastPointerY = y;
                SetStatus("Camera orbit — release RMB to finish");
            }
            return;
        }
        if (inputEvent != 0 || (!_orbiting && !_panning)) return;
        var dx = x - _lastPointerX;
        var dy = y - _lastPointerY;
        _lastPointerX = x;
        _lastPointerY = y;
        if (_orbiting)
        {
            // Dragging follows the camera movement direction. The previous
            // signs felt reversed for both the numeric yaw and the viewport.
            _orbitYaw += dx * 0.35f;
            var ySign = Settings.InvertYAxis ? -1f : 1f;
            _orbitPitch = Math.Clamp(_orbitPitch + dy * 0.35f * ySign, -89f, 89f);
            ApplyNavigationCamera();
        }
        else if (_panning)
        {
            var camera = _world.ActiveCamera;
            if (camera is null) return;
            var forward = EntityWorldDirection(camera, -Vector3.UnitZ);
            var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
            if (!float.IsFinite(right.X) || right.LengthSquared() < 1e-6f) right = Vector3.UnitX;
            var up = Vector3.Normalize(Vector3.Cross(right, forward));
            var delta = (-right * dx + up * dy) * (_orbitDistance * 0.0025f);
            _orbitTarget += delta;
            CommitCameraNavigation(camera, LocalPositionForWorld(camera,
                EntityWorldPosition(camera) + delta), camera.Rotation);
            SetText("position", Format(camera.Position));
        }
    }

    /// <summary>Receives Win32 virtual-key state while the viewport owns focus.</summary>
    public void ViewportKey(uint key, bool pressed, bool viewportFocused)
    {
        if (PlayState != EditorPlayState.Stopped && key != 0)
        {
            return;
        }
        if (key == 0)
        {
            _cameraTransactionGroup = null;
            _pressedKeys.Clear();
            _viewportKeyFocus = false;
            return;
        }
        if (pressed)
        {
            if (_pressedKeys.Count == 0) _cameraTransactionGroup = Guid.NewGuid().ToString("N");
            _viewportKeyFocus = viewportFocused;
            if (viewportFocused) _pressedKeys.Add(key);
            else _pressedKeys.Clear();
        }
        else
        {
            _pressedKeys.Remove(key);
            if (_pressedKeys.Count == 0) _cameraTransactionGroup = null;
        }
    }

    public void InvokeUiAction(string action)
    {
        var actionHostId = "main";
        const string hostPrefix = "editor.host-action:";
        if (action.StartsWith(hostPrefix, StringComparison.Ordinal))
        {
            var separator = action.IndexOf(':', hostPrefix.Length);
            if (separator < 0) return;
            actionHostId = action[hostPrefix.Length..separator];
            action = action[(separator + 1)..];
        }
        if (PopupMenus.TryHandleAction(actionHostId, action))
        {
            MarkExtensionUiDirty();
            return;
        }
        if (ContextMenuRouting.TryHandleAction(actionHostId, action))
        {
            MarkExtensionUiDirty();
            return;
        }
        if (HandleSourceAction(actionHostId, action)) return;
        if (HandleContentCreationAction(actionHostId, action))
        {
            return;
        }
        if (AssetOpeners.TryHandleHostAction(actionHostId, action)) return;
        // Pane-owned commands dispatch by their published action before the global registry
        // is consulted, so a content browser never needs a global command entry.
        if (_contentManagers is not null && action.StartsWith("editor.content-command:",
                StringComparison.Ordinal))
        {
            if (_contentManagers.TryHandleCommand(action, Pane => new EditorCommandContext(this, Pane)))
            {
                return;
            }
        }
        if (HandleMcpAction(actionHostId, action))
        {
            return;
        }
        if (HandleDevelopmentSettingsAction(actionHostId, action)) return;
        if (HandleInputDemoAction(actionHostId, action)) return;
        if (HandleProjectPluginsAction(actionHostId, action))
        {
            return;
        }
        if (actionHostId == "prefab-editor" && action == "window.closed")
        {
            ActivateMainWorld(); _prefabWindowOpen = false;
            _nativeApi.DestroyDockWindow("prefab-editor"); return;
        }
        if (action == "window.close-requested")
        {
            HandleMainWindowClose();
            return;
        }
        MarkExtensionUiDirty();
        if (_commands.TryExecute(action, _commandContext)) return;
        foreach (var Handler in _extensions.Keys.OfType<IEditorExtensionActionHandler>().ToArray())
        {
            try
            {
                if (Handler.TryHandleAction(action)) return;
            }
            catch (Exception Exception)
            {
                SharpLog.Error("EditorExtension", $"Editor extension action '{action}' failed", Exception);
            }
        }
        if (HandleUiEditorAction(action)) return;
        if (HandleComponentAction(action) || HandlePrefabAction(action)) return;
        if (_world is null || _assets is null) return;
        if (action.StartsWith("editor.context:", StringComparison.Ordinal))
        {
            HandleContextRequest(actionHostId, action);
            return;
        }
        if (action.StartsWith("editor.select-entity:", StringComparison.Ordinal))
        { SelectEntity(action["editor.select-entity:".Length..]); return; }
        if (action.StartsWith("editor.select-asset:", StringComparison.Ordinal))
        { SelectAsset(action["editor.select-asset:".Length..]); return; }
        if (action.StartsWith("editor.open-asset:", StringComparison.Ordinal))
        {
            OpenAsset(action["editor.open-asset:".Length..]);
            return;
        }
        if (action.StartsWith("editor.search-hierarchy:", StringComparison.Ordinal))
        {
            _hierarchySearch = action["editor.search-hierarchy:".Length..].Trim();
            RefreshHierarchy();
            SetStatus(_hierarchySearch.Length == 0
                ? "Hierarchy filter cleared"
                : $"Hierarchy filtered by '{_hierarchySearch}' - drag disabled");
            return;
        }
        if (action.StartsWith("editor.hierarchy-toggle:", StringComparison.Ordinal))
        {
            var id = action["editor.hierarchy-toggle:".Length..];
            if (!_collapsedHierarchy.Add(id)) _collapsedHierarchy.Remove(id);
            RefreshHierarchy();
            return;
        }
        if (action.StartsWith("editor.hierarchy-drop:", StringComparison.Ordinal))
        {
            HandleHierarchyDrop(action["editor.hierarchy-drop:".Length..]);
            return;
        }
        if (action.StartsWith("editor.dock-", StringComparison.Ordinal))
        {
            HandleDockAction(actionHostId, action);
            return;
        }
        if (action == "window.closed" && actionHostId != "main")
        {
            ApplyLayout(new EditorLayoutCommand(EditorLayoutCommandKind.CloseFloatingHost,
                TargetHostId: actionHostId));
            return;
        }
        if (action is "editor.undo" or "editor.redo")
        {
            if (PlayState != EditorPlayState.Stopped || _authoring is null)
            {
                SetStatus("Authoring Undo/Redo is disabled while Playing or Paused");
                return;
            }
            var result = action == "editor.undo"
                ? _authoring.Undo(_authoring.DocumentRevision)
                : _authoring.Redo(_authoring.DocumentRevision);
            if (!result.Success)
            {
                SetStatus(result.Message);
                return;
            }
            RefreshHierarchy();
            RefreshInspector();
            RefreshWorldTitle();
            MarkExtensionUiDirty();
            SetStatus($"{result.Message} · revision {result.AfterRevision}");
            return;
        }
        if (TryContentAction(action, "activate", out var contentPaneId, out _))
        {
            ActivateContentManager(contentPaneId);
            CommitContentManagerState($"{ContentManagerLabel(contentPaneId)} active");
            return;
        }
        if (TryContentAction(action, "search", out contentPaneId, out var contentValue) ||
            action.StartsWith("editor.search-assets:", StringComparison.Ordinal) &&
            UseLegacyContentAction(action["editor.search-assets:".Length..],
                out contentPaneId, out contentValue))
        {
            var manager = ActivateContentManager(contentPaneId);
            manager?.SetSearch(contentValue);
            CommitContentManagerState(
                $"{ContentManagerLabel(contentPaneId)} · {manager?.VisibleEntries.Count ?? 0} items");
            return;
        }
        if (TryContentAction(action, "source", out contentPaneId, out contentValue) ||
            action.StartsWith("editor.content.source:", StringComparison.Ordinal) &&
            UseLegacyContentAction(action["editor.content.source:".Length..],
                out contentPaneId, out contentValue))
        {
            var manager = ActivateContentManager(contentPaneId);
            manager?.SetSource(contentValue.Equals("engine", StringComparison.OrdinalIgnoreCase)
                ? ContentSourceFilter.Engine
                : contentValue.Equals("project", StringComparison.OrdinalIgnoreCase)
                    ? ContentSourceFilter.Project : ContentSourceFilter.All);
            ApplyActiveContentSelection();
            CommitContentManagerState(
                $"{ContentManagerLabel(contentPaneId)} · source {manager?.SourceFilter}");
            return;
        }
        if (TryContentAction(action, "type", out contentPaneId, out contentValue) ||
            action.StartsWith("editor.content.type:", StringComparison.Ordinal) &&
            UseLegacyContentAction(action["editor.content.type:".Length..],
                out contentPaneId, out contentValue))
        {
            var manager = ActivateContentManager(contentPaneId);
            manager?.SetType(contentValue);
            CommitContentManagerState(
                $"{ContentManagerLabel(contentPaneId)} · type {manager?.TypeFilter ?? "All"}");
            return;
        }
        if (TryContentAction(action, "view", out contentPaneId, out contentValue))
        {
            var manager = ActivateContentManager(contentPaneId);
            manager?.SetViewMode(contentValue.Equals("list", StringComparison.OrdinalIgnoreCase)
                ? ContentViewMode.List : ContentViewMode.Tile);
            CommitContentManagerState(
                $"{ContentManagerLabel(contentPaneId)} · {manager?.ViewMode} view");
            return;
        }
        if (TryContentAction(action, "select", out contentPaneId, out contentValue))
        {
            ActivateContentManager(contentPaneId);
            SelectAsset(contentValue);
            return;
        }
        if (TryContentAction(action, "select-folder", out contentPaneId, out contentValue))
        {
            var Separator = contentValue.IndexOf('|');
            if (Separator <= 0) return;
            var Source = contentValue[..Separator].Equals("engine",
                StringComparison.OrdinalIgnoreCase) ? AssetSource.Engine : AssetSource.Project;
            _contentManagers?.SelectFolder(contentPaneId, Source, contentValue[(Separator + 1)..]);
            return;
        }
        if (TryContentAction(action, "open-asset", out contentPaneId, out contentValue))
        {
            ActivateContentManager(contentPaneId);
            SelectAsset(contentValue);
            OpenAsset(contentValue);
            return;
        }
        if (TryContentAction(action, "open", out contentPaneId, out contentValue))
        {
            OpenContentEntry(contentPaneId, contentValue);
            return;
        }
        if (action.StartsWith("editor.content.open:", StringComparison.Ordinal))
        {
            OpenContentEntry(_contentManagers?.ActivePaneId ?? EditorPaneIds.Content,
                action["editor.content.open:".Length..]);
            return;
        }
        if (TryContentAction(action, "up", out contentPaneId, out _) ||
            action == "editor.content.up" && UseLegacyContentAction(string.Empty,
                out contentPaneId, out _))
        {
            var manager = ActivateContentManager(contentPaneId);
            if (manager?.GoUp() == true)
            {
                ApplyActiveContentSelection();
                CommitContentManagerState(
                    $"{ContentManagerLabel(contentPaneId)} · {manager.CurrentFolderDisplay}");
            }
            return;
        }
        if (TryContentAction(action, "clear", out contentPaneId, out _) ||
            action == "editor.content.clear" && UseLegacyContentAction(string.Empty,
                out contentPaneId, out _))
        {
            var manager = ActivateContentManager(contentPaneId);
            ResetContentManager(manager);
            ApplyActiveContentSelection();
            CommitContentManagerState($"{ContentManagerLabel(contentPaneId)} · filters cleared",
                rebuildDock: true);
            return;
        }
        if (action.StartsWith("editor.create-project:", StringComparison.Ordinal))
        { CreateProject(action["editor.create-project:".Length..]); return; }
        if (action.StartsWith("editor.open-project:", StringComparison.Ordinal))
        { OpenProject(action["editor.open-project:".Length..]); return; }
        switch (action)
        {
            case "editor.play":
                if (!ProjectCodeAvailable) { SetStatus("Project code unavailable; fix code and rebuild before Play"); break; }
                if (PlayState == EditorPlayState.Stopped)
                {
                    BeginComponentPlay();
                }
                PlayState = EditorPlayState.Playing;
                if (actionHostId == "main")
                {
                    RequestPlayInputCapture();
                }
                SetStatus("Playing — independent Runtime World");
                break;
            case "editor.pause":
                ReleasePlayInputCapture();
                if (PlayState == EditorPlayState.Playing)
                { PlayState = EditorPlayState.Paused; SetStatus("Simulation paused"); }
                break;
            case "editor.stop":
                ReleasePlayInputCapture();
                if (PlayState != EditorPlayState.Stopped)
                {
                    EndComponentPlay();
                    PlayState = EditorPlayState.Stopped;
                    _seconds = 0;
                    _gizmoDragging = false;
                    _orbiting = false;
                    _panning = false;
                    _rightPointerPending = false;
                    _rightPointerDragging = false;
                    _navigationInitialized = false;
                    _pressedKeys.Clear();
                    _viewportKeyFocus = false;
                    RefreshInspector();
                    SetStatus("Stopped — Authoring World restored");
                }
                break;
            case "editor.save":
                SaveWorld();
                break;
            case "editor.save-scene-json":
                SaveSceneDescription();
                break;
            case "editor.select-scene":
                _selection = EditorSelection.Scene;
                MarkExtensionUiDirty();
                RefreshHierarchy();
                RefreshAssets();
                SetText("selectedObject", _world.Name);
                SetText("selectedKind", $"World · {_world.Entities.Count} entities");
                SetText("parent", "—");
                SetText("position", "—");
                SetText("rotation", "—");
                SetText("scale", "—");
                RefreshViewportModeUi();
                SetStatus($"Selected {_world.Name}");
                break;
            case "editor.assets-menu":
                var routed = EnsureContentManagerForExternalBrowse();
                ResetContentManager(routed);
                ApplyActiveContentSelection();
                CommitContentManagerState(
                    $"{ContentManagerLabel(_contentManagers!.ActivePaneId)} · {routed?.VisibleEntries.Count ?? 0} items",
                    rebuildDock: true);
                break;
            case "editor.select-camera": SelectEntity(_world.ActiveCameraId); break;
            case "editor.select-light":
                var light = _world.Entities.FirstOrDefault(e => e.Kind == WorldEntityKind.Light);
                if (light is not null) SelectEntity(light.Id);
                break;
            case "editor.tool-move":
            case "editor.tool-rotate":
            case "editor.tool-scale":
                _viewportTool = action.EndsWith("move", StringComparison.Ordinal) ? "move" :
                    action.EndsWith("rotate", StringComparison.Ordinal) ? "rotate" : "scale";
                RefreshViewportModeUi();
                SetStatus($"3D transform tool · {_viewportTool} · select an axis handle and drag");
                break;
            case "editor.new-project":
                SetStatus("Create a project with SharpEngineProject -Command New; project files are generated automatically");
                break;
            case "editor.open-project":
                SetStatus("Open .sharpproject files through SharpEngine Project Selector");
                break;
            default:
                SetStatus($"Action: {action}");
                break;
        }
    }

    /// <summary>Starts a project extension and owns it until the returned handle
    /// is disposed or the editor shuts down. All calls must use the editor thread.</summary>
    public IDisposable RegisterExtension(IEditorExtension extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        if (_extensions.ContainsKey(extension))
            throw new InvalidOperationException("This editor extension is already registered");
        var registration = new EditorRegistration(() =>
        {
            _extensions.Remove(extension);
            try { extension.Shutdown(); }
            catch (Exception exception)
            { SharpLog.Error("EditorExtension", "Shutdown failed", exception); }
            MarkExtensionUiDirty();
        });
        _extensions.Add(extension, registration);
        try { extension.Startup(_context); }
        catch
        {
            registration.Dispose();
            throw;
        }
        return registration;
    }

    public void Shutdown()
    {
        if (PendingCreation is not null)
        {
            CloseContentCreation();
        }
        PopupMenus.Clear();
        StopRuntimeInstance();
        ShutdownMcp();
        foreach (var registration in _extensions.Values.Reverse().ToArray())
            registration.Dispose();
        ShutdownDevelopmentSettings();
        SubsystemLifetime.Dispose();
        _contentManagers?.Dispose();
        _contextMenus.Clear();
        _toolbar.Clear();
        _menus.Clear();
        CustomBarRegistry.Clear();
        AssetOpeners.Clear();
        _commands.Clear();
        RefreshExtensionUi();
        SharpLog.Info("Editor", $"Shutdown after {_frameCount} managed frames",
            new Dictionary<string, string> { ["frames"] = _frameCount.ToString() });
        _logSubscription.Dispose();
        SharpLog.Flush();
    }

    private void SelectEntity(string id)
    {
        var entity = _world!.Entities.FirstOrDefault(e => e.Id == id);
        if (entity is null) return;
        _selection = new EditorSelection(EditorSelectionKind.Entity, entity.Id);
        MarkExtensionUiDirty();
        RefreshHierarchy();
        RefreshAssets();
        RefreshInspector();
        RefreshViewportModeUi();
        SetStatus($"Selected {entity.Name}");
    }

    private void SelectAsset(string id)
    {
        var asset = _assets!.Find(id);
        if (asset is null) return;
        _contentManagers?.Active.Select(asset.Id);
        _selection = new EditorSelection(EditorSelectionKind.Asset, asset.Id,
            asset.RelativePath, asset.Source);
        MarkExtensionUiDirty();
        RefreshHierarchy();
        RefreshViewportModeUi();
        SetText("selectedObject", asset.Name);
        SetText("selectedKind", $"{asset.Type} · {asset.Source} · {(asset.ReadOnly ? "Read only" : "Editable")}");
        SetText("position", asset.Id);
        SetText("rotation", asset.RelativePath);
        SetText("scale", asset.Exists ? "Available" : "File missing");
        SetText("parent", "—");
        CommitContentManagerState($"Selected {asset.Id}");
    }

    private void OpenContentEntry(string managerId, string payload)
    {
        var separator = payload.IndexOf('|');
        var manager = ActivateContentManager(managerId);
        if (separator <= 0 || manager is null) return;
        var source = payload[..separator].Equals("engine", StringComparison.OrdinalIgnoreCase)
            ? AssetSource.Engine : AssetSource.Project;
        var relativePath = payload[(separator + 1)..];
        var root = source == AssetSource.Engine ? _engineRoot : _projectRoot;
        if (root is null) return;
        var browserRoot = relativePath.StartsWith("Plugins/",
            StringComparison.OrdinalIgnoreCase) ? root : Path.Combine(root, "Content");
        var fullPath = Path.Combine(browserRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (Directory.Exists(fullPath))
        {
            if (manager.OpenFolder(relativePath))
            {
                _selection = new EditorSelection(EditorSelectionKind.Folder, Path: relativePath,
                    Source: source);
                MarkExtensionUiDirty();
                RefreshHierarchy();
                RefreshViewportModeUi();
                CommitContentManagerState(
                    $"{ContentManagerLabel(managerId)} · {manager.CurrentFolderDisplay}");
            }
            return;
        }

        if (!manager.SelectFile(source, relativePath)) return;
        var asset = manager.SelectedAsset;
        if (asset is not null)
        {
            SelectAsset(asset.Id);
            OpenAsset(asset.Id);
            return;
        }

        _selection = new EditorSelection(EditorSelectionKind.Asset, Path: relativePath,
            Source: source);
        MarkExtensionUiDirty();
        RefreshHierarchy();
        RefreshViewportModeUi();
        var entry = manager.VisibleEntries.FirstOrDefault(candidate =>
            string.Equals(candidate.RelativePath, relativePath,
                StringComparison.OrdinalIgnoreCase));
        SetText("selectedObject", Path.GetFileName(relativePath));
        SetText("selectedKind", $"{entry?.Type ?? "File"} · {source} · Editable");
        SetText("parent", "Content");
        SetText("position", relativePath);
        SetText("rotation", "—");
        SetText("scale", File.Exists(fullPath) ? "Available" : "Missing");
        CommitContentManagerState($"Selected Content/{relativePath}");
    }

    private bool TryBeginGizmoDrag(int x, int y)
    {
        var selectedEntityId = SelectedEntityId;
        if (PlayState != EditorPlayState.Stopped || _world is null || _authoring is null ||
            string.IsNullOrWhiteSpace(selectedEntityId) ||
            !WorldViewportRenderer.TryPickTransformGizmo(_world, _lastViewport,
                selectedEntityId, x, y, out var hit, _viewportTool)) return false;
        var entity = _world.Entities.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, selectedEntityId, StringComparison.OrdinalIgnoreCase));
        var camera = _world.ActiveCamera;
        if (entity is null || camera is null) return false;
        var matrix = _world.WorldMatrix(entity);
        _gizmoDragging = true;
        _gizmoTransactionGroupId = Guid.NewGuid().ToString("N");
        _gizmoAxis = hit.Axis;
        _gizmoStartPointer = new Vector2(x, y);
        _gizmoScreenDirection = hit.ScreenDirection;
        _gizmoWorldDirection = hit.WorldDirection;
        _gizmoStartWorldPosition = new Vector3(matrix.M41, matrix.M42, matrix.M43);
        _gizmoStartRotation = entity.Rotation;
        _gizmoStartScale = entity.Scale;
        var cameraForward = EntityWorldDirection(camera, -Vector3.UnitZ);
        var depth = MathF.Max(camera.NearPlane,
            Vector3.Dot(_gizmoStartWorldPosition - EntityWorldPosition(camera), cameraForward));
        _gizmoUnitsPerPixel = depth * 2 *
            MathF.Tan(camera.FieldOfView * MathF.PI / 360) /
            MathF.Max(_lastViewport.Height, 1);
        _orbiting = false;
        _panning = false;
        SetStatus($"Gizmo {_viewportTool} {_gizmoAxis} — drag to edit {entity.Name}");
        return true;
    }

    private void UpdateGizmoDrag(int x, int y, uint buttons)
    {
        var selectedEntityId = SelectedEntityId;
        if (!_gizmoDragging || _world is null || selectedEntityId is null) return;
        var entity = _world.Entities.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, selectedEntityId, StringComparison.OrdinalIgnoreCase));
        if (entity is null) return;
        var pointerDelta = new Vector2(x, y) - _gizmoStartPointer;
        var projectedDelta = Vector2.Dot(pointerDelta, _gizmoScreenDirection);
        if (TransformSnapEnabled || (buttons & 0x0004) != 0)
            projectedDelta = MathF.Round(projectedDelta / 10) * 10;
        var transform = new WorldLocalTransform(entity.Position, entity.Rotation, entity.Scale);
        switch (_viewportTool)
        {
            case "move":
                transform = transform with
                {
                    Position = LocalPositionForWorld(entity,
                    _gizmoStartWorldPosition + _gizmoWorldDirection * projectedDelta *
                    _gizmoUnitsPerPixel)
                };
                break;
            case "rotate":
                var degrees = WorldViewportRenderer.GetRotationDragDegrees(
                    _gizmoScreenDirection, pointerDelta);
                if (TransformSnapEnabled || (buttons & 0x0004) != 0)
                    degrees = MathF.Round(degrees / 15) * 15;
                transform = transform with
                {
                    Rotation = AddRotation(_gizmoStartRotation, _gizmoAxis, degrees),
                };
                break;
            case "scale":
                var factor = Math.Clamp(MathF.Exp(projectedDelta * 0.01f), 0.05f, 20f);
                transform = transform with
                {
                    Scale = AddScale(_gizmoStartScale, _gizmoAxis, factor),
                };
                break;
        }
        var result = _authoring!.SetTransform(new WorldTransformRequest(entity.Id, transform,
            _authoring.DocumentRevision, _gizmoTransactionGroupId));
        if (!result.Success)
        {
            SetStatus($"Gizmo rejected: {result.Message}");
            EndGizmoDrag();
            return;
        }
        RefreshWorldTitle();
        RefreshInspector();
        SetText("statusText", $"Gizmo {_viewportTool} {_gizmoAxis} · {entity.Name}");
        SetText("status", $"Gizmo {_viewportTool} {_gizmoAxis}");
    }

    private void EndGizmoDrag()
    {
        if (!_gizmoDragging) return;
        _gizmoDragging = false;
        _gizmoTransactionGroupId = null;
        var entity = _world?.Entities.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, SelectedEntityId, StringComparison.OrdinalIgnoreCase));
        SetStatus(entity is null ? "Gizmo edit finished" :
            $"Gizmo {_viewportTool} applied to {entity.Name}");
    }

    private WorldVector3 LocalPositionForWorld(WorldEntity entity, Vector3 worldPosition)
    {
        if (_world is null || entity.ParentId.Length == 0)
            return ToWorldVector(worldPosition);
        var parent = _world.Entities.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, entity.ParentId, StringComparison.OrdinalIgnoreCase));
        if (parent is null || !Matrix4x4.Invert(_world.WorldMatrix(parent), out var inverse))
            return ToWorldVector(worldPosition);
        return ToWorldVector(Vector3.Transform(worldPosition, inverse));
    }

    private static WorldVector3 AddRotation(WorldVector3 value,
        TransformGizmoAxis axis, float degrees) => axis switch
        {
            TransformGizmoAxis.X => new WorldVector3(value.X + degrees, value.Y, value.Z),
            TransformGizmoAxis.Y => new WorldVector3(value.X, value.Y + degrees, value.Z),
            _ => new WorldVector3(value.X, value.Y, value.Z + degrees),
        };

    private static WorldVector3 AddScale(WorldVector3 value,
        TransformGizmoAxis axis, float factor) => axis switch
        {
            TransformGizmoAxis.X => new WorldVector3(value.X * factor, value.Y, value.Z),
            TransformGizmoAxis.Y => new WorldVector3(value.X, value.Y * factor, value.Z),
            _ => new WorldVector3(value.X, value.Y, value.Z * factor),
        };

    internal void SetStatusFromExtension(string text)
    {
        SetStatus(string.IsNullOrWhiteSpace(text) ? "" : text.Trim());
    }

    internal void MarkDirtyFromExtension()
    {
        if (PlayState == EditorPlayState.Stopped)
        {
            _dirty = true;
            RefreshWorldTitle();
        }
        MarkExtensionUiDirty();
    }

    internal void RefreshUiFromExtension()
    {
        if (_world is not null && _assets is not null)
        {
            RefreshLists();
            RefreshInspector();
            RefreshSettingsUi();
            RefreshViewportModeUi();
        }

        RefreshExtensionUi();
        foreach (var Extension in _extensions.Keys.OfType<IEditorExtensionUiRefresh>().ToArray())
        {
            Extension.RefreshUi();
        }
    }

    internal void SetTextFromExtension(string id, string value)
    {
        SetText(id, value);
    }

    internal void SetMarkupFromExtension(string id, string markup)
    {
        _nativeApi.SetUiMarkup(id, markup);
    }

    internal void SetPropertyFromExtension(string id, string name, string value)
    {
        _nativeApi.SetUiProperty(id, name, value);
    }

    private void MarkExtensionUiDirty()
    {
        _extensionUiDirty = true;
    }

    private static bool TryContentAction(string action, string operation,
        out string paneId, out string value)
    {
        var prefix = $"editor.content.{operation}:";
        paneId = string.Empty;
        value = string.Empty;
        if (!action.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var payload = action[prefix.Length..];
        var separator = payload.IndexOf(':');
        paneId = separator < 0 ? payload : payload[..separator];
        value = separator < 0 ? string.Empty : payload[(separator + 1)..];
        return EditorPaneIds.IsContentManager(paneId);
    }

    private bool UseLegacyContentAction(string value, out string paneId, out string routedValue)
    {
        paneId = _contentManagers?.ActivePaneId ?? EditorPaneIds.Content;
        routedValue = value;
        return true;
    }

    private ContentManager? ActivateContentManager(string paneId)
    {
        if (_contentManagers?.Activate(paneId) != true) return null;
        ApplyActiveContentSelection();
        return _contentManagers.Active;
    }

    private ContentManager? EnsureContentManagerForExternalBrowse()
    {
        if (_contentManagers is null) return null;
        if (!EditorPaneIds.ContentManagers.Any(IsPaneVisible))
        {
            ApplyLayout(new EditorLayoutCommand(EditorLayoutCommandKind.ReopenAsFloating,
                EditorPaneIds.Content));
            _contentManagers.Activate(EditorPaneIds.Content);
        }
        else if (!IsPaneVisible(_contentManagers.ActivePaneId))
        {
            var fallback = _contentManagers.MostRecentlyUsed.First(IsPaneVisible);
            _contentManagers.Activate(fallback);
        }
        var group = FindPaneGroup(_contentManagers.ActivePaneId);
        if (group.GroupId is not null)
            ApplyLayout(new EditorLayoutCommand(EditorLayoutCommandKind.ActivateTab,
                _contentManagers.ActivePaneId, TargetGroupId: group.GroupId,
                TargetHostId: group.HostId));
        ApplyActiveContentSelection();
        return _contentManagers.Active;
    }

    private void ResetContentManager(ContentManager? manager)
    {
        if (manager is null) return;
        manager.SetSearch(string.Empty);
        manager.SetSource(_projectRoot is null
            ? ContentSourceFilter.Engine : ContentSourceFilter.Project);
        manager.SetType(null);
        manager.ResetFolder();
        manager.ClearSelection();
    }

    private void ApplyActiveContentSelection()
    {
        if (_contentManagers is null) return;
        var manager = _contentManagers.Active;
        _selection = ContentManagerContextMenus.SelectionOf(manager);
        MarkExtensionUiDirty();
        RefreshHierarchy();
        RefreshInspector();
        RefreshViewportModeUi();
    }

    private void CommitContentManagerState(string status, bool rebuildDock = false)
    {
        if (_contentManagers is null) return;
        _layout?.UpdateContentManagers(_contentManagers.Capture(),
            _contentManagers.ActivePaneId);
        MarkExtensionUiDirty();
        if (rebuildDock)
        {
            // A user-edited input can differ from the last generated markup even when the model returns to it.
            _dockMarkup.Clear();
            RefreshDockUiAndContents();
        }
        else
        {
            RefreshAssets();
        }
        SetStatus(status);
    }

    private static string ContentManagerLabel(string paneId) =>
        $"Content Manager {EditorPaneIds.ContentManagerNumber(paneId)}";

    private bool IsPaneVisible(string paneId)
    {
        if (_layout is null) return false;
        if (EditorLayoutValidator.EnumeratePanes(_layout.Document.MainHost.Root)
            .Contains(paneId, StringComparer.Ordinal)) return true;
        return _layout.FloatingHosts.Any(host =>
            EditorLayoutValidator.EnumeratePanes(host.Root)
                .Contains(paneId, StringComparer.Ordinal));
    }

    private void HandleDockAction(string actionHostId, string action)
    {
        if (_layout is null) return;
        if (action == "editor.dock-save-layout")
        {
            try
            {
                if (_contentManagers is not null)
                    _layout.UpdateContentManagers(_contentManagers.Capture(),
                        _contentManagers.ActivePaneId);
                _layout.Save(_layoutStore, _engineRoot, _projectRoot);
                var omitted = _layout.FloatingHosts.SelectMany(host =>
                    EditorLayoutValidator.EnumeratePanes(host.Root)).ToArray();
                SetStatus(omitted.Length == 0 ? "Main window layout saved" :
                    $"Main window layout saved · floating panes omitted: {string.Join(", ", omitted)}");
            }
            catch (Exception exception)
            {
                SetStatus($"Layout could not be saved: {exception.Message}");
            }
            return;
        }
        if (action == "editor.dock-reset-layout")
        {
            _layout.ResetToEngineDefault(_defaultLayout!);
            var contentLayout = _layout.Document;
            SetContentManagers(new ContentManagerCoordinator(_assets!,
                contentLayout.ContentManagers, contentLayout.ActiveContentManagerId));
            ApplyActiveContentSelection();
            RefreshDockUiAndContents();
            SetStatus("Engine default layout restored for this session");
            return;
        }
        if (action.StartsWith("editor.dock-toggle:", StringComparison.Ordinal))
        {
            var togglePaneId = action["editor.dock-toggle:".Length..];
            ApplyLayout(new EditorLayoutCommand(IsPaneVisible(togglePaneId)
                ? EditorLayoutCommandKind.HidePane
                : EditorLayoutCommandKind.ReopenAsFloating, togglePaneId));
            return;
        }
        if (action.StartsWith("editor.dock-close:", StringComparison.Ordinal))
        {
            ApplyLayout(new EditorLayoutCommand(EditorLayoutCommandKind.HidePane,
                action["editor.dock-close:".Length..]));
            return;
        }
        if (action.StartsWith("editor.dock-activate:", StringComparison.Ordinal))
        {
            var parts = action["editor.dock-activate:".Length..].Split(':', 3);
            if (parts.Length == 3)
            {
                if (EditorPaneIds.IsContentManager(parts[2]))
                    ActivateContentManager(parts[2]);
                ApplyLayout(new EditorLayoutCommand(EditorLayoutCommandKind.ActivateTab,
                    parts[2], TargetGroupId: parts[1], TargetHostId: parts[0]));
            }
            return;
        }
        if (action.StartsWith("editor.dock-resize:", StringComparison.Ordinal))
        {
            var parts = action["editor.dock-resize:".Length..].Split(':', 2);
            if (parts.Length != 2) return;
            var ratios = new List<EditorSplitRatio>();
            foreach (var entry in parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = entry.Split('=', 2);
                if (pair.Length != 2 || !float.TryParse(pair[1], NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var ratio)) return;
                ratios.Add(new EditorSplitRatio(pair[0], ratio));
            }
            if (ratios.Count != 0)
                ApplyLayout(new EditorLayoutCommand(EditorLayoutCommandKind.ResizeSplit,
                    TargetHostId: parts[0], SplitRatios: ratios));
            return;
        }
        if (!action.StartsWith("editor.dock-drop:", StringComparison.Ordinal)) return;
        var drop = action["editor.dock-drop:".Length..].Split(':', 6);
        if (drop.Length != 6 || !int.TryParse(drop[5], NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var index)) return;
        var paneId = drop[0];
        var sourceHost = drop[1];
        var targetHost = drop[2];
        var target = drop[3];
        var zone = drop[4];
        if (zone == "detach" || targetHost == "none")
        {
            ApplyLayout(new EditorLayoutCommand(EditorLayoutCommandKind.DetachToFloating,
                paneId));
            return;
        }
        if (zone == "center")
        {
            var current = FindPaneGroup(paneId);
            var kind = current.HostId == targetHost && current.GroupId == target
                ? EditorLayoutCommandKind.ReorderTab
                : EditorLayoutCommandKind.MergeIntoCenter;
            ApplyLayout(new EditorLayoutCommand(kind, paneId, TargetGroupId: target,
                Index: index, TargetHostId: targetHost));
            return;
        }
        var edge = zone switch
        {
            "left" => DockEdge.Left,
            "right" => DockEdge.Right,
            "top" => DockEdge.Top,
            "bottom" => DockEdge.Bottom,
            _ => (DockEdge?)null,
        };
        if (edge is not null)
            ApplyLayout(new EditorLayoutCommand(EditorLayoutCommandKind.SplitAtEdge,
                paneId, TargetNodeId: target, Edge: edge.Value, Ratio: 0.25f,
                TargetHostId: targetHost));
    }

    private (string? HostId, string? GroupId) FindPaneGroup(string paneId)
    {
        if (_layout is null) return default;
        static string? Find(DockNodeState node, string id)
        {
            if (node.Kind == DockNodeKind.Tabs && node.TabGroup?.Panes.Any(
                    pane => pane.PaneId == id) == true) return node.TabGroup.GroupId;
            return node.First is null ? null : Find(node.First, id) ??
                (node.Second is null ? null : Find(node.Second, id));
        }
        var document = _layout.Document;
        var group = Find(document.MainHost.Root, paneId);
        if (group is not null) return ("main", group);
        foreach (var host in _layout.FloatingHosts)
        {
            group = Find(host.Root, paneId);
            if (group is not null) return (host.HostId, group);
        }
        return default;
    }

    private void ApplyLayout(EditorLayoutCommand command)
    {
        if (_layout is null) return;
        var result = _layout.Apply(command);
        if (!result.Success)
        {
            SetStatus($"Layout change rejected [{result.Code}]: {result.Message}");
            return;
        }
        if (EditorPaneIds.IsContentManager(command.PaneId) &&
            command.Kind is EditorLayoutCommandKind.ReopenAsFloating or
                EditorLayoutCommandKind.ActivateTab)
            ActivateContentManager(command.PaneId);
        EnsureActiveContentManagerVisible();
        if (_contentManagers is not null)
            _layout.UpdateContentManagers(_contentManagers.Capture(),
                _contentManagers.ActivePaneId);
        RefreshDockUiAndContents();
        SetStatus($"Layout updated · revision {result.AfterRevision}");
    }

    private void EnsureActiveContentManagerVisible()
    {
        if (_contentManagers is null || IsPaneVisible(_contentManagers.ActivePaneId)) return;
        var fallback = _contentManagers.MostRecentlyUsed.FirstOrDefault(IsPaneVisible);
        if (fallback is null) return;
        _contentManagers.Activate(fallback);
        ApplyActiveContentSelection();
    }

    private void RefreshDockUi(bool force = false)
    {
        if (_layout is null || _world is null) return;
        var title = WorldTabTitle();
        var document = _layout.Document;
        PublishDockHost(document.MainHost, title, force);
        var desired = new HashSet<string>(StringComparer.Ordinal);
        foreach (var floating in _layout.FloatingHosts)
        {
            desired.Add(floating.HostId);
            if (!_liveDockHosts.Contains(floating.HostId))
                _nativeApi.CreateDockWindow(floating.HostId,
                    EditorDockMarkup.PaneLabel(
                        EditorLayoutValidator.EnumeratePanes(floating.Root).First(), title));
            PublishDockHost(new DockHostState
            {
                HostId = floating.HostId,
                Root = floating.Root,
            }, title, force);
        }
        foreach (var hostId in _liveDockHosts.Where(id => !desired.Contains(id)).ToArray())
        {
            _nativeApi.DestroyDockWindow(hostId);
            _dockMarkup.Remove(hostId);
        }
        _liveDockHosts.Clear();
        foreach (var hostId in desired) _liveDockHosts.Add(hostId);
    }

    private void PublishDockHost(DockHostState host, string viewportTitle, bool force)
    {
        var markup = EditorDockMarkup.Build(host, viewportTitle,
            _contentManagers?.ActivePaneId ?? EditorPaneIds.Content,
            _contentManagers?.Capture());
        if (!force && _dockMarkup.TryGetValue(host.HostId, out var previous) &&
            previous == markup) return;
        if (_nativeApi.SetHostMarkup(host.HostId, "workspace", markup))
            _dockMarkup[host.HostId] = markup;
    }

    private void RefreshDockUiAndContents()
    {
        PopupMenus.Clear();
        RefreshDockUi();
        // Invalidate generated content for rebuilt or moved panes. The native
        // host retains the Inspector subtree during same-host dock rebuilding
        // so a focused input or color picker keeps its uncommitted draft.
        _componentMarkup = "";
        _contentMarkup.Clear();
        _uiText.Clear();
        _extensionUiDirty = true;
        RefreshLists();
        RefreshInspector();
        RefreshSettingsUi();
        RefreshViewportModeUi();
        RefreshUiEditor();
        RefreshLogUi();
        RefreshExtensionUi();
        foreach (var Extension in _extensions.Keys.OfType<IEditorExtensionUiRefresh>().ToArray())
        {
            Extension.RefreshUi();
        }
        RefreshWorldTitle();
    }

    private string WorldTabTitle() => _worldIsUntitled
        ? "Untitled*"
        : $"{_world?.Name ?? "World"}{(IsDirty ? "*" : string.Empty)}";

    private void RefreshWorldTitle()
    {
        if (_world is null) return;
        var tabTitle = WorldTabTitle();
        SetText("dock-tab-label-viewport", tabTitle);
        _nativeApi.SetWindowTitle(_windowHandle, $"SharpEngine - {tabTitle}");
    }

    private bool SaveWorld()
    {
        if (PlayState != EditorPlayState.Stopped)
        {
            SetStatus("Stop simulation before saving Authoring changes");
            return false;
        }
        if (_world is null || _authoring is null) return false;
        if (_activePrefabPath is not null) return SaveActivePrefab();
        try
        {
            string target;
            if (_worldIsUntitled)
            {
                if (_projectRoot is null || _projectManifest is null)
                    throw new InvalidOperationException(
                        "An Untitled World requires an open project before it can be saved");
                var contentRoot = Path.Combine(_projectRoot, "Content");
                Directory.CreateDirectory(Path.Combine(contentRoot, "Worlds"));
                if (!_nativeApi.TryChooseWorldSavePath(_windowHandle,
                        Path.Combine(contentRoot, "Worlds"), out target))
                {
                    SetStatus("World save canceled");
                    return false;
                }
                var saved = WorldAssetWorkflow.SaveUntitled(_world, _projectRoot,
                    _projectManifest, target);
                target = saved.Path;
                _worldAssetId = saved.AssetId;
                _worldIsUntitled = false;
                var contentStates = _contentManagers?.Capture() ??
                    EditorLayoutDocument.CreateDefaultContentManagers();
                var activeContent = _contentManagers?.ActivePaneId ?? EditorPaneIds.Content;
                _assets = AssetRegistry.Load(_engineRoot, _projectRoot, _projectManifest);
                SetContentManagers(new ContentManagerCoordinator(_assets, contentStates,
                    activeContent));
                _authoring.MarkSavepoint();
            }
            else
            {
                target = _world.SourcePath;
                if (!target.EndsWith(".sworld", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Only .sworld World assets can be saved");
                _authoring.Save(target);
            }
            _dirty = false;
            RefreshAssets();
            RefreshWorldTitle();
            MarkExtensionUiDirty();
            SetStatus($"Saved {target}");
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidDataException or InvalidOperationException or JsonException)
        {
            SetStatus($"World could not be saved: {exception.Message}");
            return false;
        }
    }

    private void HandleMainWindowClose()
    {
        if (!ConfirmDevelopmentSettingsClose())
        {
            return;
        }
        if (IsDirty)
        {
            var Choice = NativeEngineApi.PromptUnsavedWorld(_windowHandle, WorldTabTitle());
            if (Choice == UnsavedWorldChoice.Cancel)
            {
                return;
            }
            if (Choice == UnsavedWorldChoice.Save && (!SaveWorld() || IsDirty))
            {
                return;
            }
        }
        if (!AssetOpeners.ConfirmCloseAll())
        {
            return;
        }
        NativeEngineApi.ExitMessageLoop();
    }

    private void SaveSceneDescription()
    {
        var root = _projectRoot ?? _engineRoot;
        var target = Path.Combine(root, "Saved", "Scenes", "Main.scene.json");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, DescribeEditorJson());
        SetStatus($"Exported scene description {target}");
    }

    private void RefreshLists() { RefreshHierarchy(); RefreshAssets(); }

    private void RefreshHierarchy()
    {
        _nativeApi.SetUiMarkup("hierarchyItems", BuildHierarchyMarkup());
    }

    internal string BuildHierarchyMarkup()
    {
        var markup = new StringBuilder();
        var byParent = _world!.Entities.GroupBy(entity => entity.ParentId,
            StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key,
            group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(string parentId)
        {
            if (!byParent.TryGetValue(parentId, out var children)) return;
            foreach (var entity in children)
                if (reachable.Add(entity.Id)) Visit(entity.Id);
        }
        Visit(string.Empty);

        HashSet<string>? visible = null;
        if (_hierarchySearch.Length != 0)
        {
            visible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entity in _world.Entities.Where(entity =>
                         entity.Id.Contains(_hierarchySearch, StringComparison.OrdinalIgnoreCase) ||
                         entity.Name.Contains(_hierarchySearch, StringComparison.OrdinalIgnoreCase) ||
                         entity.Kind.ToString().Contains(_hierarchySearch,
                             StringComparison.OrdinalIgnoreCase)))
            {
                for (var current = entity; visible.Add(current.Id) &&
                    current.ParentId.Length != 0;)
                    current = _world.Entities.First(parent => string.Equals(parent.Id,
                        current.ParentId, StringComparison.OrdinalIgnoreCase));
            }
        }
        var rendered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dragEnabled = _hierarchySearch.Length == 0 &&
            PlayState == EditorPlayState.Stopped;
        void Append(string parentId, int depth)
        {
            if (!byParent.TryGetValue(parentId, out var children)) return;
            foreach (var entity in children)
            {
                if (visible is not null && !visible.Contains(entity.Id)) continue;
                if (!rendered.Add(entity.Id)) continue;
                var selected = entity.Id == SelectedEntityId ? " selected" : string.Empty;
                var hasChildren = byParent.ContainsKey(entity.Id);
                var collapsed = hasChildren && _collapsedHierarchy.Contains(entity.Id) &&
                    visible is null;
                markup.Append("<div class=\"hierarchy-row\" style=\"padding-left:")
                    .Append(Math.Min(depth, 32) * 18).Append("px;\">");
                if (hasChildren)
                    markup.Append("<button class=\"tree-toggle\" data-action=\"editor.hierarchy-toggle:")
                        .Append(Escape(entity.Id)).Append("\">")
                        .Append(collapsed ? ">" : "v").Append("</button>");
                else markup.Append("<span class=\"tree-toggle-spacer\" />");
                markup.Append("<button class=\"tree-item hierarchy-entity")
                    .Append(dragEnabled ? " hierarchy-draggable" : string.Empty)
                    .Append(selected).Append("\" id=\"entity-").Append(Escape(entity.Id))
                    .Append("\" data-action=\"editor.select-entity:").Append(Escape(entity.Id))
                    .Append("\" data-context-kind=\"object\" data-context-id=\"")
                    .Append(Escape(entity.Id)).Append("\" data-hierarchy-entity=\"")
                    .Append(Escape(entity.Id)).Append("\">")
                    .Append(EditorResourceUris.ItemIcon(entity.Kind.ToString())).Append("<span class=\"tree-label\">")
                    .Append(Escape(entity.Name)).Append("</span><span class=\"item-kind\">")
                    .Append(entity.Kind).Append("</span></button></div>");
                if (!collapsed) Append(entity.Id, depth + 1);
            }
        }
        Append(string.Empty, 0);
        if (reachable.Count != _world.Entities.Count)
            markup.Append("<div class=\"hierarchy-diagnostic\">Hierarchy contains unreachable entities</div>");
        if (markup.Length == 0)
            markup.Append("<div class=\"empty-list\">No matching entities</div>");
        return markup.ToString();
    }

    private void HandleHierarchyDrop(string payload)
    {
        if (_authoring is null) return;
        if (_hierarchySearch.Length != 0)
        {
            SetStatus("Clear the Hierarchy search before dragging entities");
            return;
        }
        if (PlayState != EditorPlayState.Stopped)
        {
            SetStatus("Hierarchy editing is disabled while Playing or Paused (current phase)");
            return;
        }
        var parts = payload.Split(':', 3);
        if (parts.Length != 3) return;
        var placement = parts[2] switch
        {
            "inside" => WorldHierarchyPlacement.Inside,
            "before" => WorldHierarchyPlacement.Before,
            "after" => WorldHierarchyPlacement.After,
            "root" => WorldHierarchyPlacement.Root,
            _ => (WorldHierarchyPlacement?)null,
        };
        if (placement is null) return;
        var target = parts[1];
        var request = placement.Value switch
        {
            WorldHierarchyPlacement.Inside => new WorldReparentRequest(parts[0], target,
                placement.Value, null, _authoring.DocumentRevision),
            WorldHierarchyPlacement.Before or WorldHierarchyPlacement.After =>
                new WorldReparentRequest(parts[0], null, placement.Value, target,
                    _authoring.DocumentRevision),
            _ => new WorldReparentRequest(parts[0], null, placement.Value, null,
                _authoring.DocumentRevision),
        };
        var result = ReparentEntity(request);
        if (!result.Success)
        {
            SetStatus($"Hierarchy drop rejected [{result.Code}]: {result.Message}");
            return;
        }
        SetStatus($"Hierarchy updated · revision {result.AfterRevision}");
    }

    /// <summary>Rebuilds the asset registry and the content browsers after the manifest
    /// changed, preserving each browser's folders, filters and selection.</summary>
    internal void ReloadAssetRegistry()
    {
        _assets = AssetRegistry.Load(_engineRoot, _projectRoot, _projectManifest);
        var ContentStates = _contentManagers?.Capture() ??
            EditorLayoutDocument.CreateDefaultContentManagers();
        var ActiveContent = _contentManagers?.ActivePaneId ?? EditorPaneIds.Content;
        SetContentManagers(new ContentManagerCoordinator(_assets, ContentStates, ActiveContent));
        RefreshAssets();
    }

    /// <summary>Connects a browser aggregate's delete intent to the asset workflow that owns
    /// the project manifest. The browser states what the user asked for; this side decides and
    /// performs it.</summary>
    private void AttachContentCommands(ContentManagerCoordinator ContentManagers)
    {
        ContentManagers.DeleteRequested += Asset => DeleteAsset(Asset.Id);
    }

    private void RefreshAssets()
    {
        if (_contentManagers is null)
        {
            return;
        }
        foreach (var paneId in EditorPaneIds.ContentManagers)
            RefreshContentManager(paneId, _contentManagers.Get(paneId));
    }

    private void RefreshContentManager(string paneId, ContentManager manager)
    {
        string Id(string local) => $"{paneId}-{local}";
        foreach (var source in new[] { AssetSource.Engine, AssetSource.Project })
        {
            var sourceName = source.ToString().ToLowerInvariant();
            _nativeApi.SetUiProperty(Id(sourceName + "-label"), "display",
                manager.SourceFilter == ContentSourceFilter.All ? "block" : "none");
            var targetId = Id(source == AssetSource.Engine ? "engine-items" : "project-items");
            var value = _contentManagers!.BuildEntries(paneId, source);
            if (!_contentMarkup.TryGetValue(targetId, out var previous) || previous != value)
            {
                _nativeApi.SetUiMarkup(targetId, value);
                _contentMarkup[targetId] = value;
            }
        }
        var sourceLabel = manager.SourceFilter == ContentSourceFilter.All
            ? "All" : manager.SourceFilter.ToString();
        SetText(Id("summary"),
            $"{manager.VisibleEntries.Count} items · {sourceLabel} · {manager.TypeFilter ?? "All types"}");
        SetText(Id("breadcrumb"), manager.CurrentFolderDisplay);
        SetText(Id("path"),
            $"Content: {manager.ContentRoot}{(manager.CurrentFolder.Length == 0 ? string.Empty : Path.DirectorySeparatorChar + manager.CurrentFolder.Replace('/', Path.DirectorySeparatorChar))}");
        _nativeApi.SetUiProperty(Id("up"), "display",
            manager.CurrentFolder.Length == 0 ? "none" : "inline-block");
        _nativeApi.SetUiProperty(Id("project-section"), "display",
            manager.SourceFilter == ContentSourceFilter.Engine ? "none" : "block");
        _nativeApi.SetUiProperty(Id("engine-section"), "display",
            manager.SourceFilter == ContentSourceFilter.Project ? "none" : "block");
        foreach (var source in new[] { "all", "project", "engine" })
        {
            var active = source switch
            {
                "project" => manager.SourceFilter == ContentSourceFilter.Project,
                "engine" => manager.SourceFilter == ContentSourceFilter.Engine,
                _ => manager.SourceFilter == ContentSourceFilter.All,
            };
            _nativeApi.SetUiProperty(Id($"source-{source}"), "background-color",
                active ? "#315f8b" : "#30353e");
        }
        foreach (var type in new[] { "all", "mesh", "material", "world" })
        {
            var active = manager.TypeFilter is null ? type == "all" :
                string.Equals(manager.TypeFilter, type, StringComparison.OrdinalIgnoreCase);
            _nativeApi.SetUiProperty(Id($"type-{type}"), "background-color",
                active ? "#314d6a" : "#30353e");
            _nativeApi.SetUiProperty(Id($"type-{type}"), "border-color",
                active ? "#619ce0" : "#424954");
        }
        foreach (var mode in new[] { "tile", "list" })
        {
            var active = manager.ViewMode == (mode == "tile"
                ? ContentViewMode.Tile : ContentViewMode.List);
            _nativeApi.SetUiProperty(Id($"view-{mode}"), "background-color",
                active ? "#315f8b" : "#30353e");
            _nativeApi.SetUiProperty(Id($"view-{mode}"), "border-color",
                active ? "#68a9f2" : "#424954");
        }
        var isActive = paneId == _contentManagers?.ActivePaneId;
        _nativeApi.SetUiProperty(Id("pane"), "border-top-color",
            isActive ? "#68a9f2" : "transparent");
        _nativeApi.SetUiProperty($"dock-tab-{paneId}", "border-top-color",
            isActive ? "#68a9f2" : "transparent");
        _nativeApi.SetUiProperty($"dock-tab-{paneId}", "color",
            isActive ? "#f2f7ff" : "#a7b3c4");
    }

    private void RefreshInspector()
    {
        RefreshComponentInspector();
        var entity = _world?.Entities.FirstOrDefault(e => e.Id == SelectedEntityId);
        if (entity is not null)
        {
            SetText("selectedObject", entity.Name);
            SetText("selectedKind", $"{entity.Kind} · {entity.Id}");
            SetText("position", Format(entity.Position));
            SetText("rotation", Format(entity.Rotation));
            SetText("scale", Format(entity.Scale));
            SetText("parent", entity.ParentId.Length == 0 ? "None" : entity.ParentId);
            return;
        }
        if (_selection.Kind == EditorSelectionKind.Scene)
        {
            SetText("selectedObject", _world?.Name ?? "Scene");
            SetText("selectedKind", $"World · {_world?.Entities.Count ?? 0} entities");
            SetInspectorDetails("-", "-", "-", "-");
            return;
        }
        var asset = _selection.Id is null ? null : _assets?.Find(_selection.Id);
        if (asset is not null)
        {
            SetText("selectedObject", asset.Name);
            SetText("selectedKind",
                $"{asset.Type} · {asset.Source} · {(asset.ReadOnly ? "Read only" : "Editable")}");
            SetInspectorDetails("-", asset.Id, asset.RelativePath,
                asset.Exists ? "Available" : "File missing");
            return;
        }
        if (_selection.Path is not null)
        {
            SetText("selectedObject", Path.GetFileName(_selection.Path));
            SetText("selectedKind", $"{_selection.Kind} · {_selection.Source}");
            SetInspectorDetails("Content", _selection.Path, "-", "Available");
            return;
        }
        SetText("selectedObject", _contentManagers is null
            ? "No selection" : ContentManagerLabel(_contentManagers.ActivePaneId));
        SetText("selectedKind", "No selection");
        SetInspectorDetails("-", "-", "-", "-");
    }

    private void SetInspectorDetails(string parent, string position, string rotation,
        string scale)
    {
        SetText("parent", parent);
        SetText("position", position);
        SetText("rotation", rotation);
        SetText("scale", scale);
    }

    private void RefreshSettingsUi()
    {
        RefreshSettingsWindow(SettingsScope.Engine);
        RefreshSettingsWindow(SettingsScope.Project);
    }

    private void RefreshViewportModeUi()
    {
        var selectedSceneEntity = SelectedEntityId is not null &&
            _world?.Entities.Any(entity => entity.Id == SelectedEntityId) == true;
        foreach (var tool in new[] { "move", "rotate", "scale" })
        {
            var id = $"{tool}Tool";
            _nativeApi.SetUiProperty(id, "display", selectedSceneEntity ? "block" : "none");
            _nativeApi.SetUiProperty(id, "background-color", _viewportTool == tool ? "#315f8b" : "#2a323e");
            _nativeApi.SetUiProperty(id, "border-color", _viewportTool == tool ? "#6a9ed1" : "#48576a");
        }
        _nativeApi.SetUiProperty("viewportProjection", "display", "block");
        SetText("viewportProjection", "Perspective");
        SetText("viewportHelp", BuildNavigationHelp());
    }

    private void UpdateNavigationMovement(float deltaSeconds)
    {
        if (PlayState != EditorPlayState.Stopped || !_viewportKeyFocus ||
            !float.IsFinite(deltaSeconds) || deltaSeconds <= 0 ||
            _world?.ActiveCamera is not { } camera || _pressedKeys.Count == 0)
        {
            return;
        }
        EnsureNavigationState();
        var forward = EntityWorldDirection(camera, -Vector3.UnitZ);
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        if (!float.IsFinite(right.X) || right.LengthSquared() < 1e-6f) right = Vector3.UnitX;
        var direction = Vector3.Zero;
        if (IsPressedShortcut(Shortcuts.NavigateForward) ||
            IsPressedShortcut(Shortcuts.NavigateForwardAlternate)) direction += forward;
        if (IsPressedShortcut(Shortcuts.NavigateBack) ||
            IsPressedShortcut(Shortcuts.NavigateBackAlternate)) direction -= forward;
        if (IsPressedShortcut(Shortcuts.NavigateRight) ||
            IsPressedShortcut(Shortcuts.NavigateRightAlternate)) direction += right;
        if (IsPressedShortcut(Shortcuts.NavigateLeft) ||
            IsPressedShortcut(Shortcuts.NavigateLeftAlternate)) direction -= right;
        if (IsPressedShortcut(Shortcuts.NavigateUp)) direction += Vector3.UnitY;
        if (IsPressedShortcut(Shortcuts.NavigateDown)) direction -= Vector3.UnitY;
        if (direction.LengthSquared() < 1e-6f) return;
        direction = Vector3.Normalize(direction);
        var speed = MathF.Max(_orbitDistance * 0.75f, 1f);
        if (IsPressedShortcut(Shortcuts.NavigateSpeedModifier)) speed *= 4f;
        var delta = direction * speed * Math.Clamp(deltaSeconds, 0, 0.1f);
        CommitCameraNavigation(camera, LocalPositionForWorld(camera, EntityWorldPosition(camera) + delta), camera.Rotation);
        _orbitTarget += delta;
        SetText("position", Format(camera.Position));
    }

    private bool IsPressed(uint key) => _pressedKeys.Contains(key);

    private void EnsureNavigationState()
    {
        if (_navigationInitialized) return;
        var camera = _world?.ActiveCamera;
        if (camera is null) return;
        _orbitTarget = Vector3.Zero;
        var offset = EntityWorldPosition(camera) - _orbitTarget;
        _orbitDistance = Math.Clamp(offset.Length(), 0.75f, 250f);
        _orbitYaw = MathF.Atan2(offset.X, offset.Z) * 180 / MathF.PI;
        _orbitPitch = MathF.Asin(Math.Clamp(offset.Y / _orbitDistance, -1, 1)) * 180 / MathF.PI;
        _navigationInitialized = true;
    }

    private void ApplyNavigationCamera()
    {
        var camera = _world?.ActiveCamera;
        if (camera is null) return;
        var yaw = _orbitYaw * MathF.PI / 180;
        var pitch = _orbitPitch * MathF.PI / 180;
        var cosine = MathF.Cos(pitch);
        var position = _orbitTarget + new Vector3(
            MathF.Sin(yaw) * cosine * _orbitDistance,
            MathF.Sin(pitch) * _orbitDistance,
            MathF.Cos(yaw) * cosine * _orbitDistance);
        var localPosition = LocalPositionForWorld(camera, position);
        var forward = Vector3.Normalize(_orbitTarget - position);
        var worldRotation = new WorldVector3(
            MathF.Asin(Math.Clamp(forward.Y, -1, 1)) * 180 / MathF.PI,
            MathF.Atan2(-forward.X, -forward.Z) * 180 / MathF.PI,
            0);
        var worldQuaternion = Rotation(worldRotation);
        var localQuaternion = Quaternion.Normalize(worldQuaternion *
            Quaternion.Inverse(ParentWorldRotation(camera)));
        CommitCameraNavigation(camera, localPosition, QuaternionToEuler(localQuaternion));
        RefreshInspector();
    }

    private Vector3 EntityWorldPosition(WorldEntity entity)
    {
        var matrix = _world!.WorldMatrix(entity);
        return new Vector3(matrix.M41, matrix.M42, matrix.M43);
    }
    private Vector3 EntityWorldDirection(WorldEntity entity, Vector3 localDirection)
    {
        var direction = localDirection;
        for (var current = entity; current is not null;)
        {
            direction = Vector3.Transform(direction, Rotation(current.Rotation));
            current = current.ParentId.Length == 0 ? null : _world!.Entities.First(parent =>
                string.Equals(parent.Id, current.ParentId, StringComparison.OrdinalIgnoreCase));
        }
        return Vector3.Normalize(direction);
    }
    private Quaternion ParentWorldRotation(WorldEntity entity)
    {
        var result = Quaternion.Identity;
        var parentId = entity.ParentId;
        while (parentId.Length != 0)
        {
            var parent = _world!.Entities.First(candidate => string.Equals(candidate.Id,
                parentId, StringComparison.OrdinalIgnoreCase));
            result = Quaternion.Normalize(result * Rotation(parent.Rotation));
            parentId = parent.ParentId;
        }
        return result;
    }
    private static Quaternion Rotation(WorldVector3 value) =>
        Quaternion.CreateFromYawPitchRoll(value.Y * MathF.PI / 180,
            value.X * MathF.PI / 180, value.Z * MathF.PI / 180);
    private static WorldVector3 QuaternionToEuler(Quaternion value)
    {
        value = Quaternion.Normalize(value);
        var pitch = MathF.Asin(Math.Clamp(2 * (value.W * value.X - value.Y * value.Z), -1, 1));
        var yaw = MathF.Atan2(2 * (value.W * value.Y + value.X * value.Z),
            1 - 2 * (value.X * value.X + value.Y * value.Y));
        var roll = MathF.Atan2(2 * (value.W * value.Z + value.X * value.Y),
            1 - 2 * (value.X * value.X + value.Z * value.Z));
        const float radiansToDegrees = 180 / MathF.PI;
        return new WorldVector3(pitch * radiansToDegrees, yaw * radiansToDegrees,
            roll * radiansToDegrees);
    }
    private static Vector3 ToVector(WorldVector3 value) => new(value.X, value.Y, value.Z);
    private static WorldVector3 ToWorldVector(Vector3 value) => new(value.X, value.Y, value.Z);

    private void CreateProject(string directory)
    {
        try
        {
            var fullPath = Path.GetFullPath(directory);
            var name = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar));
            var parent = Path.GetDirectoryName(fullPath)!;
            RunTool("powershell.exe", "-NoProfile", "-File",
                Path.Combine(_engineRoot, "Tools", "SharpEngineProject.ps1"),
                "-Command", "New", "-Name", name, "-Directory", parent,
                "-EngineRoot", _engineRoot);
            var manifest = Path.Combine(fullPath, name + ".sharpproject");
            RunTool("powershell.exe", "-NoProfile", "-File",
                Path.Combine(_engineRoot, "Tools", "SharpEngineProject.ps1"),
                "-Command", "Build", "-ProjectFile", manifest,
                "-Target", "Editor", "-Configuration", "Debug", "-Platform", "Win64");
            OpenProject(manifest);
        }
        catch (Exception exception)
        {
            SetStatus($"Create project failed: {exception.Message}");
        }
    }

    private void OpenProject(string manifest)
    {
        try
        {
            var fullPath = Path.GetFullPath(manifest);
            if (!File.Exists(fullPath)) throw new FileNotFoundException("Project manifest was not found", fullPath);
            RunTool("powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass",
                "-File", Path.Combine(_engineRoot, "Tools", "SharpEngineProject.ps1"),
                "-Command", "OpenEditor", "-ProjectFile", fullPath,
                "-Configuration", "Debug", "-Platform", "Win64");
            SetStatus($"Opened {Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(fullPath)))} in a new editor window");
        }
        catch (Exception exception)
        {
            SetStatus($"Open project failed: {exception.Message}");
        }
    }

    private void RunTool(string fileName, params string[] arguments)
    {
        var start = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = _engineRoot,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException($"Could not start {fileName}");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(output, errors);
        if (process.ExitCode != 0) throw new IOException($"{fileName} exited {process.ExitCode}: {errors.Result} {output.Result}");
        if (!string.IsNullOrWhiteSpace(output.Result)) SharpLog.Info("EditorTool", output.Result.Trim());
    }

    private void SetStatus(string value)
    {
        SetText("statusText", value);
        SetText("status", value);
        Log(value);
    }

    private void Log(string value)
    {
        SharpLog.Info("Editor", value);
    }

    private void DrainLogs()
    {
        DrainLogFile();
        var consoleChanged = false;
        var logsChanged = false;
        var pending = new List<SharpLogEvent>();
        while (_pendingLogs.TryDequeue(out var entry)) pending.Add(entry);
        foreach (var entry in pending.OrderBy(value => value.Timestamp))
        {
            _logHistory.Enqueue(entry);
            while (_logHistory.Count > LogHistoryLimit) _logHistory.Dequeue();
            logsChanged = true;
            var value = $"[{entry.LevelName}][{entry.Category}] {entry.Message}";
            _messages.Enqueue(value);
            while (_messages.Count > 6) _messages.Dequeue();
            if (!IsConsoleEntry(entry)) continue;
            _consoleMessages.Enqueue(value);
            while (_consoleMessages.Count > 6) _consoleMessages.Dequeue();
            consoleChanged = true;
        }
        if (consoleChanged && _nativeApi.HasDocumentUi)
            SetText("detailsText", string.Join('\n', _consoleMessages));
        if (logsChanged) RefreshLogUi();
    }

    private void RefreshLogUi()
    {
        if (_layout is null) return;
        var markup = EditorDockMarkup.BuildLogEntriesMarkup(_logHistory);
        PublishLogEntries(_layout.Document.MainHost, markup);
        foreach (var floating in _layout.FloatingHosts)
            PublishLogEntries(new DockHostState
            {
                HostId = floating.HostId,
                Root = floating.Root,
            }, markup);
    }

    private void PublishLogEntries(DockHostState host, string markup)
    {
        if (!EditorLayoutValidator.EnumeratePanes(host.Root)
                .Contains(EditorPaneIds.Logs, StringComparer.Ordinal)) return;
        _nativeApi.SetHostMarkup(host.HostId, EditorDockMarkup.LogEntriesId(host.HostId), markup);
    }

    private static bool IsConsoleEntry(SharpLogEvent entry) =>
        entry.Category.Equals("Editor", StringComparison.Ordinal) ||
        entry.Category.StartsWith("Editor", StringComparison.Ordinal);

    private void ResetLogFileTail()
    {
        _logFileRemainder = string.Empty;
        _logFilePosition = 0;
        try
        {
            if (SharpLog.FilePath is { } path && File.Exists(path))
                _logFilePosition = new FileInfo(path).Length;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void DrainLogFile()
    {
        if (SharpLog.FilePath is not { } path || !File.Exists(path)) return;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < _logFilePosition)
            {
                _logFilePosition = 0;
                _logFileRemainder = string.Empty;
            }
            if (stream.Length == _logFilePosition) return;
            stream.Position = _logFilePosition;
            using var reader = new StreamReader(stream, Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            var text = _logFileRemainder + reader.ReadToEnd();
            _logFilePosition = stream.Position;
            var lines = text.Split('\n');
            _logFileRemainder = lines[^1];
            foreach (var line in lines[..^1])
            {
                if (TryParseLogEvent(line.TrimEnd('\r'), out var entry) &&
                    _knownLogEntries.TryAdd(LogIdentity(entry), 0))
                    _pendingLogs.Enqueue(entry);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool TryParseLogEvent(string line, out SharpLogEvent entry)
    {
        entry = default;
        if (string.IsNullOrWhiteSpace(line)) return false;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("timestamp", out var timestamp) ||
                !root.TryGetProperty("level", out var level) ||
                !root.TryGetProperty("category", out var category) ||
                !root.TryGetProperty("message", out var message) ||
                !DateTimeOffset.TryParse(timestamp.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var parsedTimestamp) ||
                !TryParseLogLevel(level.GetString(), out var parsedLevel)) return false;
            var properties = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty("properties", out var propertyObject) &&
                propertyObject.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in propertyObject.EnumerateObject())
                    properties[property.Name] = property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString() ?? string.Empty
                        : property.Value.GetRawText();
            }
            var exception = root.TryGetProperty("exception", out var exceptionValue) &&
                exceptionValue.ValueKind != JsonValueKind.Null
                ? exceptionValue.GetString() : null;
            entry = new SharpLogEvent(parsedTimestamp, parsedLevel,
                root.TryGetProperty("source", out var source) ? source.GetString() ?? "Unknown" : "Unknown",
                category.GetString() ?? "General", message.GetString() ?? string.Empty,
                root.TryGetProperty("processId", out var processId) && processId.TryGetInt32(out var pid)
                    ? pid : 0,
                root.TryGetProperty("threadId", out var threadId) && threadId.TryGetInt32(out var tid)
                    ? tid : 0,
                exception, properties);
            return true;
        }
        catch (JsonException) { return false; }
        catch (FormatException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static bool TryParseLogLevel(string? value, out SharpLogLevel level)
    {
        level = value switch
        {
            "trace" => SharpLogLevel.Trace,
            "debug" => SharpLogLevel.Debug,
            "info" => SharpLogLevel.Information,
            "warning" => SharpLogLevel.Warning,
            "error" => SharpLogLevel.Error,
            "critical" => SharpLogLevel.Critical,
            _ => SharpLogLevel.Trace,
        };
        return value is "trace" or "debug" or "info" or "warning" or "error" or "critical";
    }

    private static string LogIdentity(SharpLogEvent entry) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{entry.Timestamp:O}|{entry.LevelName}|{entry.Source}|{entry.Category}|{entry.Message}|{entry.ProcessId}|{entry.ThreadId}|{entry.Exception}");

    private void SetText(string id, string value)
    {
        if (_uiText.TryGetValue(id, out var previous) && previous == value) return;
        _uiText[id] = value;
        _nativeApi.SetUiText(id, value);
    }

    private static string Format(WorldVector3 vector) => string.Create(CultureInfo.InvariantCulture,
        $"X {vector.X:0.##}   Y {vector.Y:0.##}   Z {vector.Z:0.##}");
    private static string Escape(string value) => WebUtility.HtmlEncode(value);

    private static string FindEngineRoot()
    {
        var configured = Environment.GetEnvironmentVariable("SHARPENGINE_ROOT");
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "engine.json"))) return current.FullName;
        throw new DirectoryNotFoundException("Could not locate engine.json. Set SHARPENGINE_ROOT to the public engine installation.");
    }

    private static string? FindProjectManifest()
    {
        var configured = Environment.GetEnvironmentVariable("SHARPENGINE_PROJECT_MANIFEST");
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        var args = Environment.GetCommandLineArgs();
        for (var index = 0; index + 1 < args.Length; index++)
            if (args[index] == "--project") return Path.GetFullPath(args[index + 1]);
        return null;
    }
}
