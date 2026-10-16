using System.Globalization;
using System.Diagnostics;
using System.Net;
using System.Numerics;
using System.Text;
using System.Text.Json;
using SharpEngine.Assets;
using SharpEngine.UI;
using SharpEngine.World;

namespace SharpEngine.Editor;

public enum EditorPlayState { Stopped, Playing, Paused }
public enum EditorViewportMode { Scene3D, Ui2D }

/// <summary>The public managed editor session used by the built-in host and
/// generated projects. RmlUi owns layout/input; this class owns scene and assets.</summary>
public sealed partial class EditorRuntime
{
    private readonly nint _windowHandle;
    private readonly uint _windowWidth;
    private readonly uint _windowHeight;
    private readonly NativeEngineApi _nativeApi;
    private readonly EditorCommandRegistry _commands;
    private readonly EditorMenuRegistry _menus;
    private readonly EditorToolbarRegistry _toolbar;
    private readonly EditorContextMenuRegistry _contextMenus;
    private readonly EditorCommandContext _commandContext;
    private readonly EditorContext _context;
    private readonly Dictionary<IEditorExtension, IDisposable> _extensions =
        new(ReferenceEqualityComparer.Instance);
    private readonly Queue<string> _messages = new();
    private AssetRegistry? _assets;
    private ContentManager? _content;
    private EngineSettings _settings = new();
    private WorldDocument? _world;
    private string _engineRoot = string.Empty;
    private string? _projectRoot;
    private string? _projectManifest;
    private string _editSnapshot = string.Empty;
    private EditorSelection _selection = EditorSelection.None;
    private string _search = string.Empty;
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
    private EditorViewportMode _viewportMode = EditorViewportMode.Scene3D;
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
    private bool _extensionUiDirty = true;
    private string _menuMarkup = string.Empty;
    private string _toolbarLeftMarkup = string.Empty;
    private string _toolbarCenterMarkup = string.Empty;
    private string _toolbarRightMarkup = string.Empty;
    private bool _rightPointerPending;
    private bool _rightPointerDragging;
    private int _rightPointerX;
    private int _rightPointerY;
    private bool _dirty;
    private bool _editWasDirty;

    public EditorRuntime(nint windowHandle, uint width, uint height, nint nativeApi)
    {
        _windowHandle = windowHandle;
        _windowWidth = width;
        _windowHeight = height;
        _nativeApi = new NativeEngineApi(nativeApi);
        _commands = new EditorCommandRegistry(MarkExtensionUiDirty);
        _menus = new EditorMenuRegistry(_commands, MarkExtensionUiDirty);
        _toolbar = new EditorToolbarRegistry(_commands, MarkExtensionUiDirty);
        _contextMenus = new EditorContextMenuRegistry(_commands, MarkExtensionUiDirty);
        _commandContext = new EditorCommandContext(this);
        _context = new EditorContext(this, _commands, _menus, _toolbar, _contextMenus);
    }

    public WorldDocument? World => _world;
    public AssetRegistry? Assets => _assets;
    public ContentManager? Content => _content;
    public EditorCommandRegistry Commands => _commands;
    public EditorMenuRegistry Menus => _menus;
    public EditorToolbarRegistry Toolbar => _toolbar;
    public EditorContextMenuRegistry ContextMenus => _contextMenus;
    public EditorContext Context => _context;
    public EditorSelection Selection => _selection;
    public string? SelectedEntityId => _selection.Kind == EditorSelectionKind.Entity ? _selection.Id : null;
    public WorldEntity? SelectedEntity => _world?.Entities.FirstOrDefault(entity =>
        string.Equals(entity.Id, SelectedEntityId, StringComparison.OrdinalIgnoreCase));
    public AssetDescriptor? SelectedAsset => _selection.Kind == EditorSelectionKind.Asset && _selection.Id is not null
        ? _assets?.Find(_selection.Id) : null;
    public string? SelectedFolderPath => _selection.Kind == EditorSelectionKind.Folder ? _selection.Path : null;
    public EngineSettings Settings => _settings;
    public EditorPlayState PlayState { get; private set; }
    public EditorViewportMode ViewportMode => _viewportMode;
    public bool TransformSnapEnabled { get; set; }
    public bool IsDirty => _dirty;
    public string DescribeSceneJson() => _world?.ToJson() ?? "{}";
    public string DescribeAssetsJson() => _assets?.ToJson() ?? "{}";
    public string DescribeContentJson() => _content?.ToJson() ?? "{}";
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
        scene = _world?.Describe(),
        isDirty = IsDirty,
        settings = new
        {
            invertYAxis = _settings.InvertYAxis,
            path = EngineSettings.GetPath(_engineRoot, _projectRoot),
        },
        viewport = new
        {
            mode = _viewportMode == EditorViewportMode.Scene3D ? "scene3d" : "ui2d",
            selectedEntity = SelectedEntityId,
            transformTool = _viewportTool,
            transformGizmoVisible = _viewportMode == EditorViewportMode.Scene3D &&
                SelectedEntityId is not null,
            transformGizmo = DescribeTransformGizmo(_viewportTool),
            gizmo = new { position = "top-right", type = _viewportMode == EditorViewportMode.Scene3D ? "orientation-3d" : "orientation-2d" },
            worldAxes = new { x = "right", y = "up", z = "depth" },
            uiAxes = new { x = "right", y = "down" },
            navigation = new[] { "WASD move", "QE vertical move", "RMB orbit", "MMB pan", "wheel zoom" },
        },
        assets = _assets?.Assets.Select(asset => new
        {
            id = asset.Id, type = asset.Type, name = asset.Name,
            source = asset.Source.ToString(), path = asset.RelativePath,
            importer = asset.Importer, exists = asset.Exists,
            tags = asset.Tags, metadata = asset.Metadata,
        }),
        content = _content is null ? null : new
        {
            search = _content.SearchText,
            source = _content.SourceFilter.ToString(),
            type = _content.TypeFilter,
            selected = _content.SelectedId,
            selectedPath = _content.SelectedPath,
            currentFolder = _content.CurrentFolder,
            contentRoot = _content.ContentRoot,
            visibleCount = _content.VisibleAssets.Count,
        },
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

    public void Initialize()
    {
        _engineRoot = FindEngineRoot();
        _projectManifest = FindProjectManifest();
        _projectRoot = _projectManifest is null ? null :
            Path.GetDirectoryName(_projectManifest)!;
        _assets = AssetRegistry.Load(_engineRoot, _projectRoot, _projectManifest);
        _content = new ContentManager(_assets);
        _settings = EngineSettings.Load(_engineRoot, _projectRoot);
        var candidates = new List<string>();
        if (_projectRoot is not null)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(_projectManifest!));
            if (document.RootElement.TryGetProperty("world", out var world) &&
                world.TryGetProperty("startup", out var startup))
                candidates.Add(Path.GetFullPath(Path.Combine(_projectRoot, startup.GetString()!)));
            candidates.Add(Path.Combine(_projectRoot, "Content", "Worlds", "Main.world.xml"));
        }
        candidates.Add(Path.Combine(_engineRoot, "Content", "Worlds", "Main.world.xml"));
        _world = WorldDocument.LoadOrDefault(candidates);
        _editSnapshot = _world.ToXml().ToString();
        _dirty = false;
        var initialEntity = _world.Entities.FirstOrDefault(e => e.Kind == WorldEntityKind.Mesh &&
            !e.Asset.EndsWith("/plane", StringComparison.OrdinalIgnoreCase))?.Id;
        _selection = initialEntity is null ? EditorSelection.Scene :
            new EditorSelection(EditorSelectionKind.Entity, initialEntity);
        RegisterBuiltInEditorUi();
        _nativeApi.SetWindowTitle(_windowHandle, $"SharpEngine — {_world.Name}");
        SetText("projectTitle", _projectRoot is null ? "Engine workspace" : Path.GetFileName(_projectRoot));
        SetText("projectPath", _projectRoot ?? _engineRoot);
        RefreshLists();
        RefreshInspector();
        RefreshSettingsUi();
        RefreshViewportModeUi();
        RefreshExtensionUi();
        SetStatus($"Loaded {_world.Name} · {_world.Entities.Count} entities · {_assets.Assets.Count} assets");
        foreach (var diagnostic in _assets.Diagnostics) Log(diagnostic.Message);
        Console.Error.WriteLine($"[World] source={_world.SourcePath}, entities={_world.Entities.Count}, meshes={_world.MeshCount}, camera={_world.ActiveCameraId}");
        Console.Error.WriteLine($"[Assets] engine={_assets.EngineAssets.Count()}, project={_assets.ProjectAssets.Count()}, diagnostics={_assets.Diagnostics.Count}");
        Console.Error.WriteLine($"[ManagedUI] RmlUi bridge={_nativeApi.HasDocumentUi}, public-api={typeof(EditorRuntime).Assembly.GetName().Name}");
    }

    public void Update(float deltaSeconds)
    {
        // Conditions can depend on project C# state. Compare the resulting markup
        // before publishing, so an unchanged state preserves RmlUi focus/hover.
        RefreshExtensionUi();
        if (_world is null || _assets is null) return;
        _frameCount++;
        UpdateNavigationMovement(deltaSeconds);
        if (PlayState == EditorPlayState.Playing && float.IsFinite(deltaSeconds))
        {
            _seconds += Math.Clamp(deltaSeconds, 0, 0.1f);
            var animated = _world.Entities.FirstOrDefault(e => e.Kind == WorldEntityKind.Mesh &&
                !e.Asset.EndsWith("/plane", StringComparison.OrdinalIgnoreCase));
            if (animated is not null)
                animated.Rotation = new WorldVector3(animated.Rotation.X,
                    (animated.Rotation.Y + Math.Clamp(deltaSeconds, 0, 0.1f) * 35) % 360,
                    animated.Rotation.Z);
        }
        if (_nativeApi.TryGetViewportRect(out var viewport))
        {
            _lastViewport = viewport;
            var frame = _viewportMode == EditorViewportMode.Scene3D
                ? WorldViewportRenderer.BuildFrame(_world, viewport, SelectedEntityId, _assets,
                    _viewportTool)
                : WorldViewportRenderer.BuildUiFrame(viewport);
            _nativeApi.SubmitViewportFrame(frame);
            if (_frameCount == 1)
            {
                Console.Error.WriteLine($"[Viewport] bounds={viewport}, triangles={frame.TriangleCount}, lines={frame.LineCount}, camera={frame.CameraId}");
                foreach (var diagnostic in frame.Diagnostics) Log(diagnostic);
            }
            _statsElapsed += Math.Max(0, deltaSeconds);
            if (_statsElapsed > 0.3f || _frameCount == 1)
            {
                SetText("fps", deltaSeconds > 0 ? $"{1 / deltaSeconds:0} FPS" : "— FPS");
                SetText("viewportStats", _viewportMode == EditorViewportMode.Scene3D
                    ? $"{frame.EntityCount} entities · {frame.TriangleCount} triangles · {PlayState}"
                    : $"UI Canvas · {frame.LineCount} guide lines · {PlayState}");
                SetText("assetSource", $"{PlayState} · {_seconds:0.0}s");
                if (PlayState == EditorPlayState.Playing) RefreshInspector();
                _statsElapsed = 0;
            }
        }
    }

    public void PointerDown(nint windowHandle, int x, int y, uint button)
    {
        if (_viewportMode != EditorViewportMode.Scene3D || button != 1 ||
            _world is null || _assets is null) return;
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
        if (_viewportMode != EditorViewportMode.Scene3D || _world is null || _assets is null ||
            (_lastViewport.Width > 0 &&
             (x < _lastViewport.X || y < _lastViewport.Y ||
              x > _lastViewport.Right || y > _lastViewport.Bottom)))
            return;
        const uint RightButton = 0x0002;
        const uint MiddleButton = 0x0010;
        const uint LeftButton = 0x0001;
        if (inputEvent == 1)
        {
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
            var ySign = _settings.InvertYAxis ? -1f : 1f;
            _orbitPitch = Math.Clamp(_orbitPitch + dy * 0.35f * ySign, -89f, 89f);
            ApplyNavigationCamera();
        }
        else if (_panning)
        {
            var camera = _world.ActiveCamera;
            if (camera is null) return;
            var forward = CameraForward(camera.Rotation);
            var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
            if (!float.IsFinite(right.X) || right.LengthSquared() < 1e-6f) right = Vector3.UnitX;
            var up = Vector3.Normalize(Vector3.Cross(right, forward));
            var delta = (-right * dx + up * dy) * (_orbitDistance * 0.0025f);
            _orbitTarget += delta;
            camera.Position = ToWorldVector(ToVector(camera.Position) + delta);
            if (PlayState == EditorPlayState.Stopped) _dirty = true;
            SetText("position", Format(camera.Position));
        }
    }

    /// <summary>Receives Win32 virtual-key state while the viewport owns focus.</summary>
    public void ViewportKey(uint key, bool pressed, bool viewportFocused)
    {
        if (_viewportMode != EditorViewportMode.Scene3D)
        {
            _pressedKeys.Clear();
            _viewportKeyFocus = false;
            return;
        }
        if (key == 0)
        {
            _pressedKeys.Clear();
            _viewportKeyFocus = false;
            return;
        }
        if (pressed)
        {
            _viewportKeyFocus = viewportFocused;
            if (viewportFocused) _pressedKeys.Add(key);
            else _pressedKeys.Clear();
        }
        else
        {
            _pressedKeys.Remove(key);
        }
    }

    public void InvokeUiAction(string action)
    {
        MarkExtensionUiDirty();
        if (_commands.TryExecute(action, _commandContext)) return;
        if (_world is null || _assets is null) return;
        if (action.StartsWith("editor.context:", StringComparison.Ordinal))
        { HandleContextRequest(action); return; }
        if (action.StartsWith("editor.select-entity:", StringComparison.Ordinal))
        { SelectEntity(action["editor.select-entity:".Length..]); return; }
        if (action.StartsWith("editor.select-asset:", StringComparison.Ordinal))
        { SelectAsset(action["editor.select-asset:".Length..]); return; }
        if (action.StartsWith("editor.search-assets:", StringComparison.Ordinal))
        {
            _search = action["editor.search-assets:".Length..];
            _content?.SetSearch(_search);
            RefreshAssets();
            SetStatus($"Content Manager · {_content?.VisibleEntries.Count ?? 0} items");
            return;
        }
        if (action.StartsWith("editor.content.source:", StringComparison.Ordinal))
        {
            var value = action["editor.content.source:".Length..];
            _content?.SetSource(value.Equals("engine", StringComparison.OrdinalIgnoreCase)
                ? ContentSourceFilter.Engine
                : value.Equals("project", StringComparison.OrdinalIgnoreCase)
                    ? ContentSourceFilter.Project : ContentSourceFilter.All);
            RefreshAssets();
            SetStatus($"Content Manager · source {_content?.SourceFilter}");
            return;
        }
        if (action.StartsWith("editor.content.type:", StringComparison.Ordinal))
        {
            _content?.SetType(action["editor.content.type:".Length..]);
            RefreshAssets();
            SetStatus($"Content Manager · type {_content?.TypeFilter ?? "All"}");
            return;
        }
        if (action.StartsWith("editor.content.open:", StringComparison.Ordinal))
        {
            OpenContentEntry(action["editor.content.open:".Length..]);
            return;
        }
        if (action == "editor.content.up")
        {
            if (_content?.GoUp() == true)
            {
                RefreshAssets();
                SetStatus($"Content Manager · {_content.CurrentFolderDisplay}");
            }
            return;
        }
        if (action.StartsWith("editor.create-project:", StringComparison.Ordinal))
        { CreateProject(action["editor.create-project:".Length..]); return; }
        if (action.StartsWith("editor.open-project:", StringComparison.Ordinal))
        { OpenProject(action["editor.open-project:".Length..]); return; }
        switch (action)
        {
            case "editor.play":
                if (PlayState == EditorPlayState.Stopped)
                {
                    _editSnapshot = _world.ToXml().ToString();
                    _editWasDirty = _dirty;
                }
                PlayState = EditorPlayState.Playing;
                SetStatus("Playing — C# rotates the demo mesh");
                break;
            case "editor.pause":
                if (PlayState == EditorPlayState.Playing)
                { PlayState = EditorPlayState.Paused; SetStatus("Simulation paused"); }
                break;
            case "editor.stop":
                if (PlayState != EditorPlayState.Stopped)
                {
                    _world = WorldDocument.Parse(_editSnapshot, _world.SourcePath);
                    PlayState = EditorPlayState.Stopped;
                    _dirty = _editWasDirty;
                    _seconds = 0;
                    _gizmoDragging = false;
                    _navigationInitialized = false;
                    _pressedKeys.Clear();
                    _viewportKeyFocus = false;
                    RefreshInspector();
                    SetStatus("Stopped — edit transforms restored");
                }
                break;
            case "editor.toggle-invert-y":
                _settings.InvertYAxis = !_settings.InvertYAxis;
                try
                {
                    _settings.Save(_engineRoot, _projectRoot);
                    RefreshSettingsUi();
                    SetStatus($"Engine Settings · Invert Y Axis {(_settings.InvertYAxis ? "On" : "Off")}");
                }
                catch (Exception exception)
                {
                    Log($"Engine Settings could not be saved: {exception.Message}");
                }
                break;
            case "editor.engine-settings":
                SetStatus($"Engine Settings · Invert Y Axis {(_settings.InvertYAxis ? "On" : "Off")} · settings are project-local");
                break;
            case "editor.mode-scene":
                _viewportMode = EditorViewportMode.Scene3D;
                RefreshViewportModeUi();
                SetStatus("Viewport mode · 3D Scene · Y up");
                break;
            case "editor.mode-ui":
                _viewportMode = EditorViewportMode.Ui2D;
                _gizmoDragging = false;
                _pressedKeys.Clear();
                _viewportKeyFocus = false;
                RefreshViewportModeUi();
                SetStatus("Viewport mode · UI Canvas · Y down");
                break;
            case "editor.save":
                SaveWorld();
                break;
            case "editor.save-scene-json":
                SaveSceneDescription();
                break;
            case "editor.select-scene":
                _selection = EditorSelection.Scene;
                _content?.ClearSelection();
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
                _content?.SetSearch(string.Empty);
                _content?.SetSource(_projectRoot is null
                    ? ContentSourceFilter.Engine : ContentSourceFilter.Project);
                _content?.SetType(null);
                _search = string.Empty;
                RefreshAssets();
                SetStatus($"Content Manager · {_content?.VisibleEntries.Count ?? 0} items");
                break;
            case "editor.content.clear":
                _content?.SetSearch(string.Empty);
                _content?.SetSource(_projectRoot is null
                    ? ContentSourceFilter.Engine : ContentSourceFilter.Project);
                _content?.SetType(null);
                _search = string.Empty;
                RefreshAssets();
                SetStatus("Content Manager · filters cleared");
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
                SetStatus(_viewportMode == EditorViewportMode.Scene3D
                    ? $"3D transform tool · {_viewportTool} · select an axis handle and drag"
                    : "Switch to 3D Scene to use transform tools");
                break;
            case "editor.new-project":
                SetStatus("Create a project with Tools/SharpEngineProject.ps1 -Command New -Name MyGame");
                break;
            case "editor.open-project":
                SetStatus("Open a project with Engine.exe --project <project.sharpproject>");
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
            { Console.Error.WriteLine($"[EditorExtension] Shutdown failed: {exception}"); }
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
        foreach (var registration in _extensions.Values.Reverse().ToArray())
            registration.Dispose();
        _contextMenus.Clear();
        _toolbar.Clear();
        _menus.Clear();
        _commands.Clear();
        RefreshExtensionUi();
        Console.Error.WriteLine($"[Game] Shutdown after {_frameCount} managed frames");
    }

    private void SelectEntity(string id)
    {
        var entity = _world!.Entities.FirstOrDefault(e => e.Id == id);
        if (entity is null) return;
        _selection = new EditorSelection(EditorSelectionKind.Entity, entity.Id);
        _content?.ClearSelection();
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
        _content?.Select(asset.Id);
        _selection = new EditorSelection(EditorSelectionKind.Asset, asset.Id,
            asset.RelativePath, asset.Source);
        MarkExtensionUiDirty();
        RefreshHierarchy();
        RefreshAssets();
        RefreshViewportModeUi();
        SetText("selectedObject", asset.Name);
        SetText("selectedKind", $"{asset.Type} · {asset.Source} · {(asset.ReadOnly ? "Read only" : "Editable")}");
        SetText("position", asset.Id);
        SetText("rotation", asset.RelativePath);
        SetText("scale", asset.Exists ? "Available" : "File missing");
        SetText("parent", "—");
        SetStatus($"Selected {asset.Id}");
    }

    private void OpenContentEntry(string payload)
    {
        var separator = payload.IndexOf('|');
        if (separator <= 0 || _content is null) return;
        var source = payload[..separator].Equals("engine", StringComparison.OrdinalIgnoreCase)
            ? AssetSource.Engine : AssetSource.Project;
        var relativePath = payload[(separator + 1)..];
        var root = source == AssetSource.Engine ? _engineRoot : _projectRoot;
        if (root is null) return;
        var fullPath = Path.Combine(root, "Content",
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (Directory.Exists(fullPath))
        {
            if (_content.OpenFolder(relativePath))
            {
                _selection = new EditorSelection(EditorSelectionKind.Folder, Path: relativePath,
                    Source: source);
                MarkExtensionUiDirty();
                RefreshHierarchy();
                RefreshAssets();
                RefreshViewportModeUi();
                SetStatus($"Content Manager · {_content.CurrentFolderDisplay}");
            }
            return;
        }

        if (!_content.SelectFile(source, relativePath)) return;
        var asset = _content.SelectedAsset;
        if (asset is not null)
        {
            SelectAsset(asset.Id);
            return;
        }

        _selection = new EditorSelection(EditorSelectionKind.Asset, Path: relativePath,
            Source: source);
        MarkExtensionUiDirty();
        RefreshHierarchy();
        RefreshAssets();
        RefreshViewportModeUi();
        var entry = _content.VisibleEntries.FirstOrDefault(candidate =>
            string.Equals(candidate.RelativePath, relativePath,
                StringComparison.OrdinalIgnoreCase));
        SetText("selectedObject", Path.GetFileName(relativePath));
        SetText("selectedKind", $"{entry?.Type ?? "File"} · {source} · Editable");
        SetText("parent", "Content");
        SetText("position", relativePath);
        SetText("rotation", "—");
        SetText("scale", File.Exists(fullPath) ? "Available" : "Missing");
        SetStatus($"Selected Content/{relativePath}");
    }

    private bool TryBeginGizmoDrag(int x, int y)
    {
        var selectedEntityId = SelectedEntityId;
        if (_world is null || string.IsNullOrWhiteSpace(selectedEntityId) ||
            !WorldViewportRenderer.TryPickTransformGizmo(_world, _lastViewport,
                selectedEntityId, x, y, out var hit, _viewportTool)) return false;
        var entity = _world.Entities.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, selectedEntityId, StringComparison.OrdinalIgnoreCase));
        var camera = _world.ActiveCamera;
        if (entity is null || camera is null) return false;
        var matrix = _world.WorldMatrix(entity);
        _gizmoDragging = true;
        _gizmoAxis = hit.Axis;
        _gizmoStartPointer = new Vector2(x, y);
        _gizmoScreenDirection = hit.ScreenDirection;
        _gizmoWorldDirection = hit.WorldDirection;
        _gizmoStartWorldPosition = new Vector3(matrix.M41, matrix.M42, matrix.M43);
        _gizmoStartRotation = entity.Rotation;
        _gizmoStartScale = entity.Scale;
        var cameraForward = CameraForward(camera.Rotation);
        var depth = MathF.Max(camera.NearPlane,
            Vector3.Dot(_gizmoStartWorldPosition - ToVector(camera.Position), cameraForward));
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
        switch (_viewportTool)
        {
            case "move":
                SetWorldPosition(entity, _gizmoStartWorldPosition +
                    _gizmoWorldDirection * projectedDelta * _gizmoUnitsPerPixel);
                break;
            case "rotate":
                var degrees = WorldViewportRenderer.GetRotationDragDegrees(
                    _gizmoScreenDirection, pointerDelta);
                if (TransformSnapEnabled || (buttons & 0x0004) != 0)
                    degrees = MathF.Round(degrees / 15) * 15;
                entity.Rotation = AddRotation(_gizmoStartRotation, _gizmoAxis, degrees);
                break;
            case "scale":
                var factor = Math.Clamp(MathF.Exp(projectedDelta * 0.01f), 0.05f, 20f);
                entity.Scale = AddScale(_gizmoStartScale, _gizmoAxis, factor);
                break;
        }
        if (PlayState == EditorPlayState.Stopped) _dirty = true;
        RefreshInspector();
        SetText("statusText", $"Gizmo {_viewportTool} {_gizmoAxis} · {entity.Name}");
        SetText("status", $"Gizmo {_viewportTool} {_gizmoAxis}");
    }

    private void EndGizmoDrag()
    {
        if (!_gizmoDragging) return;
        _gizmoDragging = false;
        var entity = _world?.Entities.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, SelectedEntityId, StringComparison.OrdinalIgnoreCase));
        SetStatus(entity is null ? "Gizmo edit finished" :
            $"Gizmo {_viewportTool} applied to {entity.Name}");
    }

    private void SetWorldPosition(WorldEntity entity, Vector3 worldPosition)
    {
        if (_world is null || entity.ParentId.Length == 0)
        {
            entity.Position = ToWorldVector(worldPosition);
            return;
        }
        var parent = _world.Entities.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, entity.ParentId, StringComparison.OrdinalIgnoreCase));
        if (parent is null || !Matrix4x4.Invert(_world.WorldMatrix(parent), out var inverse))
        {
            entity.Position = ToWorldVector(worldPosition);
            return;
        }
        entity.Position = ToWorldVector(Vector3.Transform(worldPosition, inverse));
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
        if (PlayState == EditorPlayState.Stopped) _dirty = true;
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

    private void SaveWorld()
    {
        if (PlayState != EditorPlayState.Stopped)
        { SetStatus("Stop simulation before saving edit transforms"); return; }
        var target = _projectRoot is null
            ? Path.Combine(_engineRoot, "Saved", "Worlds", "Main.world.xml")
            : Path.Combine(_projectRoot, "Content", "Worlds", "Main.world.xml");
        _world!.Save(target);
        _editSnapshot = _world.ToXml().ToString();
        _dirty = false;
        SetStatus($"Saved {target}");
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
        var markup = new StringBuilder();
        var byParent = _world!.Entities.GroupBy(entity => entity.ParentId,
            StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key,
            group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        void Append(string parentId, int depth)
        {
            if (!byParent.TryGetValue(parentId, out var children)) return;
            foreach (var entity in children)
            {
                var selected = entity.Id == SelectedEntityId ? " selected" : string.Empty;
                var child = depth > 0 ? " child" : string.Empty;
                markup.Append($"<button class=\"tree-item{child}{selected}\" id=\"entity-{Escape(entity.Id)}\" data-action=\"editor.select-entity:{Escape(entity.Id)}\" data-context-kind=\"object\" data-context-id=\"{Escape(entity.Id)}\">{EditorIcon(entity.Kind.ToString())}{Escape(entity.Name)}<span class=\"item-kind\">{entity.Kind}</span></button>");
                Append(entity.Id, depth + 1);
            }
        }
        Append(string.Empty, 0);
        _nativeApi.SetUiMarkup("hierarchyItems", markup.ToString());
    }

    private void RefreshAssets()
    {
        if (_content is null) return;
        foreach (var source in new[] { AssetSource.Engine, AssetSource.Project })
        {
            var markup = new StringBuilder();
            markup.Append("<div class=\"content-context\" data-context-kind=\"folder\" data-context-source=\"")
                .Append(source.ToString().ToLowerInvariant()).Append("\" data-context-id=\"")
                .Append(Escape(_content.CurrentFolder)).Append("\">");
            var itemCount = 0;
            foreach (var entry in _content.VisibleEntries.Where(entry => entry.Source == source))
            {
                itemCount++;
                var selected = string.Equals(_content.SelectedPath, entry.RelativePath,
                    StringComparison.OrdinalIgnoreCase) ? " selected" : string.Empty;
                var icon = EditorIcon(entry.IsDirectory ? "Folder" : entry.Type);
                var action = entry.IsDirectory
                    ? $"editor.content.open:{source.ToString().ToLowerInvariant()}|{Escape(entry.RelativePath)}"
                    : entry.AssetId is not null
                        ? $"editor.select-asset:{Escape(entry.AssetId)}"
                        : $"editor.content.open:{source.ToString().ToLowerInvariant()}|{Escape(entry.RelativePath)}";
                var missing = entry.Exists ? string.Empty : " missing";
                var kind = entry.IsDirectory ? string.Empty : $"<span class=\"item-kind\">{Escape(entry.Type)}</span>";
                var contextKind = entry.IsDirectory ? "folder" : "asset";
                var contextId = entry.AssetId ?? entry.RelativePath;
                markup.Append($"<button class=\"tree-item asset-item{selected}{missing}\" data-action=\"{action}\" data-context-kind=\"{contextKind}\" data-context-id=\"{Escape(contextId)}\" data-context-source=\"{source.ToString().ToLowerInvariant()}\" title=\"{Escape(entry.RelativePath)} ({Escape(entry.Type)})\">{icon}<span class=\"asset-name\">{Escape(entry.Name)}</span>{kind}</button>");
            }
            if (itemCount == 0)
                markup.Append("<div class=\"empty-list\">Folder is empty</div>");
            markup.Append("</div>");
            _nativeApi.SetUiMarkup(source == AssetSource.Engine ? "engineItems" : "projectItems", markup.ToString());
        }
        var sourceLabel = _content.SourceFilter == ContentSourceFilter.All ? "All" : _content.SourceFilter.ToString();
        SetText("contentSummary", $"{_content.VisibleEntries.Count} items · {sourceLabel} · {_content.TypeFilter ?? "All types"}");
        SetText("contentBreadcrumb", _content.CurrentFolderDisplay);
        SetText("projectPath", $"Content: {_content.ContentRoot}{(_content.CurrentFolder.Length == 0 ? string.Empty : Path.DirectorySeparatorChar + _content.CurrentFolder.Replace('/', Path.DirectorySeparatorChar))}");
        _nativeApi.SetUiProperty("content-up", "display", _content.CurrentFolder.Length == 0 ? "none" : "inline-block");
        _nativeApi.SetUiProperty("projectSection", "display",
            _content.SourceFilter == ContentSourceFilter.Engine ? "none" : "block");
        _nativeApi.SetUiProperty("engineSection", "display",
            _content.SourceFilter == ContentSourceFilter.Project ? "none" : "block");
        foreach (var source in new[] { "all", "project", "engine" })
        {
            var active = source switch
            {
                "project" => _content.SourceFilter == ContentSourceFilter.Project,
                "engine" => _content.SourceFilter == ContentSourceFilter.Engine,
                _ => _content.SourceFilter == ContentSourceFilter.All,
            };
            _nativeApi.SetUiProperty($"content-source-{source}", "background-color", active ? "#315f8b" : "#30353e");
        }
        foreach (var type in new[] { "all", "mesh", "material", "world" })
        {
            var active = _content.TypeFilter is null ? type == "all" :
                string.Equals(_content.TypeFilter, type, StringComparison.OrdinalIgnoreCase);
            _nativeApi.SetUiProperty($"content-type-{type}", "background-color", active ? "#314d6a" : "#30353e");
            _nativeApi.SetUiProperty($"content-type-{type}", "border-color", active ? "#619ce0" : "#424954");
        }
    }

    private static string EditorIcon(string type)
    {
        var name = type.ToLowerInvariant() switch
        {
            "folder" => "folder",
            "mesh" => "mesh",
            "material" => "material",
            "world" => "world",
            "rmldocument" or "uidocument" => "ui",
            "stylesheet" => "style",
            "texture" => "texture",
            "audio" => "audio",
            "json" or "xml" => "data",
            "camera" => "camera",
            "light" => "light",
            "empty" => "all",
            _ => "file",
        };
        return $"<img class=\"icon item-icon\" src=\"Icons/{name}.tga\" />";
    }

    private void RefreshInspector()
    {
        var entity = _world!.Entities.FirstOrDefault(e => e.Id == SelectedEntityId);
        if (entity is null) return;
        SetText("selectedObject", entity.Name);
        SetText("selectedKind", $"{entity.Kind} · {entity.Id}");
        SetText("position", Format(entity.Position));
        SetText("rotation", Format(entity.Rotation));
        SetText("scale", Format(entity.Scale));
        SetText("parent", entity.ParentId.Length == 0 ? "None" : entity.ParentId);
    }

    private void RefreshSettingsUi()
    {
        SetText("invertYAxisMenu", $"Invert Y Axis: {(_settings.InvertYAxis ? "On" : "Off")}");
        SetText("settingsPath", EngineSettings.GetPath(_engineRoot, _projectRoot));
    }

    private void RefreshViewportModeUi()
    {
        var scene = _viewportMode == EditorViewportMode.Scene3D;
        var selectedSceneEntity = SelectedEntityId is not null &&
            _world?.Entities.Any(entity => entity.Id == SelectedEntityId) == true;
        foreach (var id in new[] { "sceneModeTab", "uiModeTab" })
        {
            var active = id == "sceneModeTab" ? scene : !scene;
            _nativeApi.SetUiProperty(id, "background-color", active ? "#2b4056" : "transparent");
            _nativeApi.SetUiProperty(id, "border-color", active ? "#5e91c1" : "transparent");
            _nativeApi.SetUiProperty(id, "color", active ? "#f1f6ff" : "#8f9bad");
        }
        foreach (var tool in new[] { "move", "rotate", "scale" })
        {
            var id = $"{tool}Tool";
            _nativeApi.SetUiProperty(id, "display", scene && selectedSceneEntity ? "block" : "none");
            _nativeApi.SetUiProperty(id, "background-color", _viewportTool == tool ? "#315f8b" : "#2a323e");
            _nativeApi.SetUiProperty(id, "border-color", _viewportTool == tool ? "#6a9ed1" : "#48576a");
        }
        _nativeApi.SetUiProperty("viewportProjection", "display", "block");
        SetText("viewportProjection", scene ? "Perspective" : "2D Canvas");
        SetText("viewportHelp", scene
            ? "WASD/QE Move · RMB Orbit · MMB Pan · Wheel Zoom"
            : "UI Canvas · X right · Y down · 3D navigation paused");
    }

    private void UpdateNavigationMovement(float deltaSeconds)
    {
        if (_viewportMode != EditorViewportMode.Scene3D || !_viewportKeyFocus ||
            !float.IsFinite(deltaSeconds) || deltaSeconds <= 0 ||
            _world?.ActiveCamera is not { } camera || _pressedKeys.Count == 0)
            return;
        EnsureNavigationState();
        var forward = CameraForward(camera.Rotation);
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        if (!float.IsFinite(right.X) || right.LengthSquared() < 1e-6f) right = Vector3.UnitX;
        var direction = Vector3.Zero;
        if (IsPressed(0x57) || IsPressed(0x26)) direction += forward; // W / Up
        if (IsPressed(0x53) || IsPressed(0x28)) direction -= forward; // S / Down
        if (IsPressed(0x44) || IsPressed(0x27)) direction += right;   // D / Right
        if (IsPressed(0x41) || IsPressed(0x25)) direction -= right;   // A / Left
        if (IsPressed(0x45)) direction += Vector3.UnitY;              // E
        if (IsPressed(0x51)) direction -= Vector3.UnitY;              // Q
        if (direction.LengthSquared() < 1e-6f) return;
        direction = Vector3.Normalize(direction);
        var speed = MathF.Max(_orbitDistance * 0.75f, 1f);
        if (IsPressed(0x10)) speed *= 4f; // Shift
        var delta = direction * speed * Math.Clamp(deltaSeconds, 0, 0.1f);
        camera.Position = ToWorldVector(ToVector(camera.Position) + delta);
        if (PlayState == EditorPlayState.Stopped) _dirty = true;
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
        var offset = ToVector(camera.Position) - _orbitTarget;
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
        camera.Position = ToWorldVector(position);
        var forward = Vector3.Normalize(_orbitTarget - position);
        camera.Rotation = new WorldVector3(
            MathF.Asin(Math.Clamp(forward.Y, -1, 1)) * 180 / MathF.PI,
            MathF.Atan2(-forward.X, -forward.Z) * 180 / MathF.PI,
            0);
        if (PlayState == EditorPlayState.Stopped) _dirty = true;
        RefreshInspector();
    }

    private static Vector3 CameraForward(WorldVector3 rotation) =>
        Vector3.Transform(-Vector3.UnitZ, Quaternion.CreateFromYawPitchRoll(
            rotation.Y * MathF.PI / 180, rotation.X * MathF.PI / 180,
            rotation.Z * MathF.PI / 180));
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
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            start.ArgumentList.Add("--project");
            start.ArgumentList.Add(fullPath);
            start.Environment["SHARPENGINE_ROOT"] = _engineRoot;
            _ = Process.Start(start) ?? throw new IOException("Could not start the project editor");
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
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = _engineRoot,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException($"Could not start {fileName}");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(output, errors);
        if (process.ExitCode != 0) throw new IOException($"{fileName} exited {process.ExitCode}: {errors.Result} {output.Result}");
        Console.Error.WriteLine(output.Result);
    }

    private void SetStatus(string value)
    {
        SetText("statusText", value);
        SetText("status", value);
        Log(value);
    }

    private void Log(string value)
    {
        Console.Error.WriteLine($"[Editor] {value}");
        _messages.Enqueue(value);
        while (_messages.Count > 6) _messages.Dequeue();
        SetText("detailsText", string.Join('\n', _messages));
    }

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
            if (File.Exists(Path.Combine(current.FullName, "Engine", "engine.json"))) return current.FullName;
        throw new DirectoryNotFoundException("Could not locate Engine/engine.json. Set SHARPENGINE_ROOT to the public engine installation.");
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
