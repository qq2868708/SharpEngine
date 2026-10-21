using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharpEngine.Animation;
using SharpEngine.Assets;
using SharpEngine.Editor;
using SharpEngine.Editor.Layout;
using SharpEngine.Playables;

namespace SharpEngine.Timeline;

internal sealed class TimelineAssetEditorInstance : IAssetEditorInstance
{
    private float TimelineWidth = 1200;
    private float TimelineHeight = 320;
    private readonly EditorAssetOpenContext Context;
    private readonly TimelineAssetToolDocument Tool;
    private readonly AssetRegistry Assets;
    private readonly List<SkeletalMeshAsset> CompatibleMeshes = [];
    private SkeletalAnimationAsset? PreviewAnimation;
    private SkeletalMeshAsset? PreviewMesh;
    private string? DefaultPreviewMeshId;
    private bool PreviewMeshOverridden;
    private TimelineClip? PreviewClip;
    private readonly AssetPreviewWorld PreviewWorld;
    private readonly WorldViewportLayout Viewport = new("timelinePreviewViewport", "asset-preview");
    private TimelineView? SubscribedView;

    private bool IsPlaying
    {
        get
        {
            return Tool.View?.IsPlaying == true;
        }
    }

    public TimelineAssetEditorInstance(EditorAssetOpenContext Context)
    {
        this.Context = Context;
        PreviewWorld = new AssetPreviewWorld(Context.Runtime.Components ??
            throw new InvalidOperationException("Component registry is unavailable"), "animation-preview:" + Context.Asset.Id);
        Assets = Context.Runtime.Assets ?? throw new InvalidOperationException(
            "Asset registry is unavailable");
        Tool = new TimelineAssetToolDocument(Assets, Context.Asset, Context.Path);
        PreviewAnimation = Tool.Animation;
        PreviewClip = Tool.Authoring?.Document.Tracks.FirstOrDefault(Track =>
            Track.Type.Equals("skeletal-animation", StringComparison.OrdinalIgnoreCase))?
            .Clips.FirstOrDefault();
        ResolveCompatibleMeshes();
        SubscribeTimeline();
        UpdatePreviewPose();
    }

    public AssetDescriptor Asset => Tool.Asset;
    public string EditorId => "timeline-editor";
    public string Title => Tool.Asset.Name;
    public string PrimaryPaneId => "preview";
    public bool IsDirty => Tool.IsDirty;
    public bool IsReadOnly => !Tool.Writable;
    public IReadOnlyList<AssetEditorPaneDefinition> Panes { get; } =
    [
        new("preview", "Preview", false),
        new("timeline", "Timeline"),
        new("details", "Details"),
    ];
    public IReadOnlyList<AssetEditorCommandDefinition> Commands { get; } =
    [
        new("root-motion", "Enable Root Motion", "Edit", 90, null, true),
        new("add-notify", "Add Notify", "Edit", 100, "add", true),
        new("undo", "Undo", "Edit", 200, "reset", true),
        new("redo", "Redo", "Edit", 210, "reset", true),
    ];

    private TimelineAuthoringSession Authoring => Tool.Authoring ??
        throw new InvalidOperationException("This asset does not have a Timeline document");
    private TimelineView View
    {
        get
        {
            return Tool.View ?? throw new InvalidOperationException("This asset does not have a Timeline view");
        }
        set
        {
            Tool.View = value;
            SubscribeTimeline();
        }
    }
    private TimelineDocument Document => Authoring.Document;
    private bool HasTimeline => Tool.Authoring is not null;

    public EditorLayoutDocument CreateDefaultLayout()
    {
        return new EditorLayoutDocument
        {
            MainHost = new DockHostState
            {
                HostId = "main",
                Root = new DockNodeState
                {
                    NodeId = "asset.root",
                    Kind = DockNodeKind.Split,
                    Orientation = DockSplitOrientation.Horizontal,
                    Ratio = 0.74f,
                    First = new DockNodeState
                    {
                        NodeId = "asset.left",
                        Kind = DockNodeKind.Split,
                        Orientation = DockSplitOrientation.Vertical,
                        Ratio = 0.58f,
                        First = Tabs("asset.preview", "asset.preview.tabs", "preview"),
                        Second = Tabs("asset.timeline", "asset.timeline.tabs", "timeline"),
                    },
                    Second = Tabs("asset.details", "asset.details.tabs", "details"),
                },
            },
            HiddenPaneIds = [],
            ActiveContentManagerId = string.Empty,
            ContentManagers = [],
        };
    }

    public bool CanExecuteCommand(string CommandId)
    {
        return CommandId switch
        {
            "play" => HasTimeline && !IsPlaying,
            "pause" => HasTimeline && IsPlaying,
            "stop" => HasTimeline && (IsPlaying || View.State.CurrentTime > 0),
            "seek-start" => HasTimeline && View.State.CurrentTime > 0,
            "root-motion" => Asset.Type == "SkeletalAnimation" && !IsReadOnly && !IsPlaying,
            "add-notify" => HasTimeline && !IsReadOnly && !IsPlaying,
            "undo" => HasTimeline && !IsPlaying && Tool.CanUndo,
            "redo" => HasTimeline && !IsPlaying && Tool.CanRedo,
            _ => false,
        };
    }

    public void ExecuteCommand(string CommandId)
    {
        switch (CommandId)
        {
            case "play":
                StartPlayback();
                break;
            case "pause":
                View.Pause();
                Context.SetStatus("Timeline preview paused");
                break;
            case "stop":
                StopPlayback();
                break;
            case "seek-start":
                Seek(0);
                break;
            case "root-motion":
                Tool.SetRootMotion(!Tool.Animation!.RootMotion.Enabled);
                PreviewAnimation = Tool.Animation;
                SubscribeTimeline();
                UpdatePreviewPose();
                break;
            case "add-notify":
                AddNotify();
                break;
            case "undo":
                Undo();
                break;
            case "redo":
                Redo();
                break;
        }
    }

    public bool TryHandleAction(string Action)
    {
        const string SelectPrefix = "editor.timeline-select:";
        if (Action.StartsWith(SelectPrefix, StringComparison.Ordinal) && HasTimeline)
        {
            View.SelectClip(Action[SelectPrefix.Length..]);
            return true;
        }

        if (Viewport.TryHandleAction(PreviewWorld.World, Action))
        {
            return true;
        }
        if (HasTimeline && View.TryHandleAction(Action))
        {
            return true;
        }

        const string MeshPrefix = "editor.timeline-preview-mesh:";
        if (Action.StartsWith(MeshPrefix, StringComparison.Ordinal))
        {
            SetPreviewMesh(Uri.UnescapeDataString(Action[MeshPrefix.Length..]));
            return true;
        }
        if (Action == "editor.timeline-preview-default")
        {
            PreviewMeshOverridden = false;
            SetPreviewMesh(DefaultPreviewMeshId);
            return true;
        }

        if (Action == "editor.timeline-root-motion" && CanExecuteCommand("root-motion"))
        {
            ExecuteCommand("root-motion");
            return true;
        }
        return false;
    }

    public string BuildPaneMarkup(string PaneId)
    {
        return PaneId switch
        {
            "preview" => BuildPreviewMarkup(),
            "timeline" => BuildTimelineMarkup(),
            "details" => BuildDetailsMarkup(),
            _ => string.Empty,
        };
    }

    public void Save()
    {
        Tool.Save();
        SharpEngine.World.SceneRenderer.InvalidateAssets(Assets);
        SubscribeTimeline();
        UpdatePreviewPose();
    }

    public void Update(float DeltaSeconds)
    {
        SubscribeTimeline();
        if (HasTimeline && float.IsFinite(DeltaSeconds) && DeltaSeconds >= 0)
        {
            View.Update(DeltaSeconds);
        }
        if (Context.TryGetElementRect("timelineViewHost", out var Bounds) && Bounds.Width > 0 && Bounds.Height > 0)
        {
            TimelineWidth = Math.Max(TimelineView.DefaultHeaderWidth + 1, Bounds.Width);
            TimelineHeight = Bounds.Height;
        }
        SubmitPreviewFrame();
    }

    public JsonObject Describe()
    {
        var State = Tool.Describe();
        State["schemaVersion"] = 2;
        State["editorRootId"] = Context.EditorRootId;
        State["previewMeshId"] = PreviewMesh?.AssetId;
        State["defaultPreviewMeshId"] = DefaultPreviewMeshId;
        State["previewMeshOverridden"] = PreviewMeshOverridden;
        State["panes"] = new JsonArray(Panes.Select(Pane => (JsonNode)Pane.PaneId).ToArray());
        State["playing"] = IsPlaying;
        State["previewWorld"] = PreviewWorld.Describe();
        State["viewport"] = Viewport.Describe();
        State["notifyPreview"] = "ignored";
        if (HasTimeline)
        {
            State["timelineState"] = new JsonObject
            {
                ["schemaVersion"] = 1,
                ["stateRevision"] = View.StateRevision,
                ["playing"] = View.IsPlaying,
                ["pixelsPerSecond"] = View.State.PixelsPerSecond,
                ["horizontalScroll"] = View.State.HorizontalScroll,
                ["view"] = JsonNode.Parse(View.BuildSnapshot(TimelineWidth, TimelineHeight).ToJson()),
            };
        }
        return State;
    }

    public void Dispose()
    {
        if (SubscribedView is not null)
        {
            SubscribedView.Pause();
            SubscribedView.TimeChanged -= OnTimelineTimeChanged;
            SubscribedView = null;
        }
    }

    private string BuildPreviewMarkup()
    {
        if (PreviewAnimation is null || PreviewClip is null)
        {
            return BuildStateMachinePreviewMarkup();
        }

        return Viewport.BuildMarkup(PreviewAnimation.Name);
    }

    private string BuildTimelineMarkup()
    {
        if (Tool.StateMachine is not null)
        {
            return BuildStateMachineMarkup(Tool.StateMachine);
        }
        if (!HasTimeline)
        {
            return "<div class=\"timeline-empty\">No Timeline document</div>";
        }

        var Selected = Document.Tracks.SelectMany(Track => Track.Clips)
            .FirstOrDefault(Clip => Clip.ClipId == View.State.SelectedClipId);
        return "<div class=\"timeline-pane asset-timeline-pane\"><div id=\"timelineViewHost\" class=\"timeline-view-host\">" +
            View.BuildMarkup(TimelineWidth, TimelineHeight) +
            "</div><div class=\"timeline-notify-inspector\"><span class=\"timeline-inspector-label\">NOTIFY</span><span>" +
            WebUtility.HtmlEncode(Selected is null ? "Select a clip or add a notify" :
                $"{Selected.PlayableId} · {FormatTime(Selected.Start)}") +
            "</span></div></div>";
    }

    private string BuildDetailsMarkup()
    {
        var Json = Tool.Describe().ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
        });
        var Markup = new StringBuilder(
            "<div class=\"asset-details-pane\"><div class=\"asset-preview-mesh-picker\"><strong>Preview Mesh</strong>");
        if (CompatibleMeshes.Count == 0)
        {
            Markup.Append("<span>No compatible meshes</span>");
        }
        else
        {
            Markup.Append("<button data-action=\"editor.timeline-preview-default\">Skeleton Default</button>");
            foreach (var Mesh in CompatibleMeshes)
            {
                Markup.Append("<button data-action=\"editor.timeline-preview-mesh:")
                    .Append(Uri.EscapeDataString(Mesh.AssetId)).Append("\">")
                    .Append(WebUtility.HtmlEncode(Mesh.Name)).Append("</button>");
            }
        }
        if (Asset.Type == "SkeletalAnimation")
        {
            Markup.Append("<div><button id=\"animationRootMotion\" data-action=\"editor.timeline-root-motion\"")
                .Append(IsReadOnly || IsPlaying ? " disabled" : string.Empty)
                .Append(">Root Motion: ")
                .Append(Tool.Animation!.RootMotion.Enabled ? "Enabled" : "Disabled")
                .Append("</button><span>Enabled drives the entity; disabled removes root translation and rotation.</span></div>");
        }
        Markup.Append("</div><pre class=\"timeline-details\">")
            .Append(WebUtility.HtmlEncode(Json)).Append("</pre></div>");
        return Markup.ToString();
    }

    private void ResolveCompatibleMeshes()
    {
        CompatibleMeshes.Clear();
        DefaultPreviewMeshId = null;
        if (PreviewAnimation?.SkeletonId is null)
        {
            PreviewMesh = null;
            return;
        }

        var Skeleton = SkeletonContent.Load(Assets, PreviewAnimation.SkeletonId);
        DefaultPreviewMeshId = Skeleton.PreviewMeshId;
        foreach (var Descriptor in Assets.Assets.Where(Asset =>
            Asset.Type.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var Mesh = SkeletalMeshContent.Load(Assets, Descriptor.Id);
                SkeletalAnimationCompatibility.Validate(PreviewAnimation, Mesh);
                CompatibleMeshes.Add(Mesh);
            }
            catch (Exception Exception) when (Exception is InvalidDataException or
                IOException or KeyNotFoundException)
            {
            }
        }
        CompatibleMeshes.Sort((Left, Right) => string.Compare(Left.AssetId,
            Right.AssetId, StringComparison.Ordinal));
        SetPreviewMesh(DefaultPreviewMeshId);
    }

    private void SetPreviewMesh(string? AssetId)
    {
        if (AssetId is null)
        {
            PreviewMesh = null;
            PreviewWorld.SetMesh(null, "engine.skeletal-mesh");
            return;
        }

        var Mesh = CompatibleMeshes.FirstOrDefault(Candidate =>
            Candidate.AssetId.Equals(AssetId, StringComparison.OrdinalIgnoreCase));
        if (Mesh is null)
        {
            Context.SetStatus($"Preview mesh is missing or incompatible: {AssetId}");
            return;
        }

        PreviewMesh = Mesh;
        PreviewWorld.SetMesh(Mesh.AssetId, "engine.skeletal-mesh", Mesh.DefaultMaterial, Mesh);
        Viewport.Frame(PreviewWorld.World, Mesh.Positions.Select(Position =>
            new System.Numerics.Vector3(Position.X, Position.Y, Position.Z)));
        UpdatePreviewPose();
        PreviewMeshOverridden = !AssetId.Equals(DefaultPreviewMeshId,
            StringComparison.OrdinalIgnoreCase);
        Context.SetStatus($"Preview mesh · {Mesh.Name}");
    }

    private void SubmitPreviewFrame()
    {
        Viewport.Render(Context, PreviewWorld.World, Assets);
    }

    private void SubscribeTimeline()
    {
        if (ReferenceEquals(SubscribedView, Tool.View))
        {
            return;
        }
        if (SubscribedView is not null)
        {
            SubscribedView.TimeChanged -= OnTimelineTimeChanged;
        }
        SubscribedView = Tool.View;
        if (SubscribedView is not null)
        {
            SubscribedView.TimeChanged += OnTimelineTimeChanged;
        }
    }

    private void OnTimelineTimeChanged(TimelineTimeChangedEvent Event)
    {
        // Animation preview only samples the pose. Notify execution belongs to subscribing plugin editors.
        UpdatePreviewPose();
    }

    private void UpdatePreviewPose()
    {
        if (PreviewAnimation is null || PreviewClip is null || PreviewMesh is null || !HasTimeline)
        {
            return;
        }
        var Time = Math.Clamp(View.State.CurrentTime - PreviewClip.Start, 0, PreviewClip.Duration);
        var AnimationTime = PreviewClip.SourceStart + Time * PreviewClip.Speed;
        PreviewWorld.SetPose(PreviewAnimation.SamplePreview(AnimationTime,
            PreviewClip.LoopMode == PlayableLoopMode.Loop));
    }

    private void StartPlayback()
    {
        View.Play();
        Context.SetStatus("Timeline preview playing");
    }

    private void StopPlayback()
    {
        View.Stop();
        Context.SetStatus("Timeline preview stopped");
    }

    private void Seek(double Time)
    {
        View.Seek(Time);
        Context.SetStatus($"Timeline cursor · {FormatTime(View.State.CurrentTime)}");
    }

    private void AddNotify()
    {
        var NotifyTrack = Document.Tracks.FirstOrDefault(Track =>
            Track.Type.Equals("notify", StringComparison.OrdinalIgnoreCase));
        if (NotifyTrack is null)
        {
            Context.SetStatus("Notify track is unavailable");
            return;
        }

        var Duration = Math.Min(0.1, Document.Duration);
        var Start = Math.Clamp(0.25 * NotifyTrack.Clips.Count + 0.1,
            0, Document.Duration - Duration);
        foreach (var ExistingClip in NotifyTrack.Clips.OrderBy(Clip => Clip.Start))
        {
            if (ExistingClip.Start >= Start + Duration || ExistingClip.End <= Start)
            {
                continue;
            }
            Start = ExistingClip.End + 0.05;
        }
        if (Start + Duration > Document.Duration + 1e-9)
        {
            Context.SetStatus("No free space remains on the Notify track");
            return;
        }

        var Clip = new TimelineClip($"notify-{Guid.NewGuid():N}",
            $"Notify {NotifyTrack.Clips.Count + 1}", Start, Duration);
        var Tracks = Document.Tracks.Select(Track => ReferenceEquals(Track, NotifyTrack) ?
            new TimelineTrack(Track.TrackId, Track.Name, Track.TargetId, Track.Type,
                Track.Clips.Concat([Clip]).ToArray(), Track.Muted) : Track).ToArray();
        var Changed = Tool.ReplaceTimeline(new TimelineDocument(Document.TimelineId,
            Document.Name, Document.Duration, Document.FrameRate, Tracks));
        if (!Changed)
        {
            Context.SetStatus("Notify edit was rejected");
            return;
        }
        PreviewAnimation = Tool.Animation;
        View = new TimelineView(Document, View.State);
        View.SelectClip(Clip.ClipId);
    }

    private void Undo()
    {
        if (Tool.Undo())
        {
            PreviewAnimation = Tool.Animation;
            View = new TimelineView(Document, View.State);
            UpdatePreviewPose();
        }
    }

    private void Redo()
    {
        if (Tool.Redo())
        {
            PreviewAnimation = Tool.Animation;
            View = new TimelineView(Document, View.State);
            UpdatePreviewPose();
        }
    }

    private string BuildStateMachinePreviewMarkup()
    {
        if (Tool.StateMachine is null)
        {
            return "<div class=\"timeline-empty\">No skeletal animation clip is available for preview</div>";
        }
        var StateMachine = Tool.StateMachine;
        return $"<div class=\"timeline-preview-info\"><div class=\"timeline-preview-title\">{WebUtility.HtmlEncode(Title)}</div><div>Initial state: {WebUtility.HtmlEncode(StateMachine.InitialStateId)}</div><div>States: {StateMachine.States.Count}</div><div>Transitions: {StateMachine.Transitions.Count}</div></div>";
    }

    private static string BuildStateMachineMarkup(AnimationStateMachineAsset StateMachine)
    {
        var Markup = new StringBuilder("<div class=\"animation-state-machine-editor\">");
        Markup.Append("<div class=\"animation-state-machine-header\"><strong>States</strong><span>")
            .Append(StateMachine.States.Count).Append(" states · ")
            .Append(StateMachine.Transitions.Count).Append(" transitions</span></div>")
            .Append("<div class=\"animation-state-machine-states\">");
        foreach (var State in StateMachine.States)
        {
            Markup.Append("<div class=\"animation-state-machine-state")
                .Append(State.StateId == StateMachine.InitialStateId ? " initial" : string.Empty)
                .Append("\"><strong>").Append(WebUtility.HtmlEncode(State.StateId))
                .Append("</strong><span>Clip: ").Append(WebUtility.HtmlEncode(State.ClipId))
                .Append("</span></div>");
        }
        Markup.Append("</div></div>");
        return Markup.ToString();
    }

    private static DockNodeState Tabs(string NodeId, string GroupId, string PaneId) =>
        new()
        {
            NodeId = NodeId,
            Kind = DockNodeKind.Tabs,
            TabGroup = new DockTabGroupState
            {
                GroupId = GroupId,
                ActivePaneId = PaneId,
                Panes = [new DockPaneState { PaneId = PaneId }],
            },
        };

    private static string Number(double Value) => Value.ToString("0.###",
        CultureInfo.InvariantCulture);

    private static string FormatTime(double Seconds)
    {
        var Safe = Math.Max(0, Seconds);
        var Minutes = (int)(Safe / 60);
        var Remaining = Safe - Minutes * 60;
        return $"{Minutes:00}:{Remaining:00.000}";
    }
}
