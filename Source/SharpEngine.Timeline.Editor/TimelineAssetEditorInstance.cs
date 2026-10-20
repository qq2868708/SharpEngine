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
    private const float TimelineWidth = 1200;
    private const float TimelineHeight = 320;
    private readonly EditorAssetOpenContext Context;
    private readonly TimelineAssetToolDocument Tool;
    private readonly AssetRegistry Assets;
    private readonly List<SkeletalMeshAsset> CompatibleMeshes = [];
    private SkeletalAnimationAsset? PreviewAnimation;
    private SkeletalMeshAsset? PreviewMesh;
    private string? DefaultPreviewMeshId;
    private bool PreviewMeshOverridden;
    private TimelineClip? PreviewClip;
    private bool IsPlaying;

    public TimelineAssetEditorInstance(EditorAssetOpenContext Context)
    {
        this.Context = Context;
        Assets = Context.Runtime.Assets ?? throw new InvalidOperationException(
            "Asset registry is unavailable");
        Tool = new TimelineAssetToolDocument(Assets, Context.Asset, Context.Path);
        PreviewAnimation = Tool.Animation;
        PreviewClip = Tool.Authoring?.Document.Tracks.FirstOrDefault(Track =>
            Track.Type.Equals("skeletal-animation", StringComparison.OrdinalIgnoreCase))?
            .Clips.FirstOrDefault();
        ResolveCompatibleMeshes();
    }

    public AssetDescriptor Asset => Tool.Asset;
    public string EditorId => "timeline-editor";
    public string Title => Tool.Asset.Name;
    public string PrimaryPaneId => "preview";
    public bool IsDirty => Tool.Authoring?.IsDirty == true;
    public bool IsReadOnly => !Tool.Writable;
    public IReadOnlyList<AssetEditorPaneDefinition> Panes { get; } =
    [
        new("preview", "Preview", false),
        new("timeline", "Timeline"),
        new("details", "Details"),
    ];
    public IReadOnlyList<AssetEditorCommandDefinition> Commands { get; } =
    [
        new("play", "Play", "Playback", 100, "play", true),
        new("pause", "Pause", "Playback", 110, "pause", true),
        new("stop", "Stop", "Playback", 120, "stop", true),
        new("seek-start", "Go To Start", "Playback", 130, null, true),
        new("add-notify", "Add Notify", "Edit", 100, "add", true),
        new("undo", "Undo", "Edit", 200, "reset", true),
        new("redo", "Redo", "Edit", 210, "reset", true),
    ];

    private TimelineAuthoringSession Authoring => Tool.Authoring ??
        throw new InvalidOperationException("This asset does not have a Timeline document");
    private TimelineView View
    {
        get => Tool.View ?? throw new InvalidOperationException(
            "This asset does not have a Timeline view");
        set => Tool.View = value;
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
            "add-notify" => HasTimeline && !IsReadOnly && !IsPlaying,
            "undo" => HasTimeline && !IsPlaying && Authoring.CanUndo,
            "redo" => HasTimeline && !IsPlaying && Authoring.CanRedo,
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
                IsPlaying = false;
                Context.SetStatus("Timeline preview paused");
                break;
            case "stop":
                StopPlayback();
                break;
            case "seek-start":
                Seek(0);
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

        const string SeekPrefix = "editor.timeline-seek:";
        if (Action.StartsWith(SeekPrefix, StringComparison.Ordinal) && HasTimeline)
        {
            var Parsed = double.TryParse(Action[SeekPrefix.Length..], NumberStyles.Float,
                CultureInfo.InvariantCulture, out var Time);
            if (Parsed && double.IsFinite(Time))
            {
                Seek(Time);
            }
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
    }

    public void Update(float DeltaSeconds)
    {
        if (HasTimeline && IsPlaying && float.IsFinite(DeltaSeconds) && DeltaSeconds > 0)
        {
            var NextTime = View.State.CurrentTime + Math.Clamp(DeltaSeconds, 0, 0.1f);
            if (NextTime >= Document.Duration)
            {
                NextTime = Document.Duration;
                IsPlaying = false;
                Context.SetStatus("Timeline preview completed");
            }
            View.Seek(NextTime);
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
        return State;
    }

    public void Dispose()
    {
        IsPlaying = false;
    }

    private string BuildPreviewMarkup()
    {
        if (PreviewAnimation is null || PreviewClip is null)
        {
            return BuildStateMachinePreviewMarkup();
        }

        var Time = Math.Clamp(View.State.CurrentTime - PreviewClip.Start, 0,
            PreviewClip.Duration);
        var AnimationTime = PreviewClip.SourceStart + Time * PreviewClip.Speed;
        var Percent = PreviewAnimation.Duration <= 0 ? 0 :
            AnimationTime / PreviewAnimation.Duration * 100;
        var Markup = new StringBuilder(
            "<div class=\"timeline-preview asset-preview\"><div class=\"timeline-preview-stage\"><viewport id=\"timelinePreviewViewport\" class=\"timeline-preview-viewport\" data-viewport-kind=\"asset-preview\" data-input-surface=\"timeline-preview\" /></div><div class=\"timeline-preview-info\"><div class=\"timeline-preview-title\">")
            .Append(WebUtility.HtmlEncode(PreviewAnimation.Name))
            .Append("</div><div class=\"timeline-preview-subtitle\">")
            .Append(FormatTime(AnimationTime)).Append(" / ")
            .Append(FormatTime(PreviewAnimation.Duration))
            .Append(IsReadOnly ? " · Read only" : " · Editable")
            .Append("</div><div class=\"timeline-preview-mesh\">")
            .Append(PreviewMesh is null ? "No explicit preview mesh" :
                WebUtility.HtmlEncode(PreviewMesh.Name))
            .Append("</div><div class=\"timeline-preview-progress\"><div class=\"timeline-preview-progress-value\" style=\"width:")
            .Append(Number(Math.Clamp(Percent, 0, 100)))
            .Append("%\" /></div></div></div>");
        return Markup.ToString();
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
        return "<div class=\"timeline-pane asset-timeline-pane\"><div class=\"timeline-view-host\">" +
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
        PreviewMeshOverridden = !AssetId.Equals(DefaultPreviewMeshId,
            StringComparison.OrdinalIgnoreCase);
        Context.SetStatus($"Preview mesh · {Mesh.Name}");
    }

    private void SubmitPreviewFrame()
    {
        if (PreviewAnimation is null || PreviewClip is null || PreviewMesh is null ||
            !Context.TryGetElementRect("timelinePreviewViewport", out var Bounds))
        {
            return;
        }

        var Time = Math.Clamp(View.State.CurrentTime - PreviewClip.Start, 0,
            PreviewClip.Duration);
        var AnimationTime = PreviewClip.SourceStart + Time * PreviewClip.Speed;
        var Pose = PreviewClip.LoopMode == PlayableLoopMode.Loop ?
            PreviewAnimation.SampleLooped(AnimationTime) : PreviewAnimation.Sample(AnimationTime);
        Context.SubmitViewportFrame("asset-preview",
            TimelinePreviewRenderer.BuildFrame(PreviewMesh, PreviewAnimation, Pose, Bounds));
    }

    private void StartPlayback()
    {
        if (View.State.CurrentTime >= Document.Duration)
        {
            View.Seek(0);
        }
        IsPlaying = true;
        Context.SetStatus("Timeline preview playing");
    }

    private void StopPlayback()
    {
        IsPlaying = false;
        View.Seek(0);
        Context.SetStatus("Timeline preview stopped");
    }

    private void Seek(double Time)
    {
        View.Seek(Time);
        IsPlaying = false;
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
        var Result = Authoring.Replace(new TimelineDocument(Document.TimelineId,
            Document.Name, Document.Duration, Document.FrameRate, Tracks),
            "add-notify", Authoring.DocumentRevision);
        if (!Result.Success)
        {
            Context.SetStatus(Result.Message);
            return;
        }
        View = new TimelineView(Document, View.State);
        View.SelectClip(Clip.ClipId);
    }

    private void Undo()
    {
        var Result = Authoring.Undo(Authoring.DocumentRevision);
        if (Result.Success)
        {
            View = new TimelineView(Document, View.State);
        }
        else
        {
            Context.SetStatus(Result.Message);
        }
    }

    private void Redo()
    {
        var Result = Authoring.Redo(Authoring.DocumentRevision);
        if (Result.Success)
        {
            View = new TimelineView(Document, View.State);
        }
        else
        {
            Context.SetStatus(Result.Message);
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
