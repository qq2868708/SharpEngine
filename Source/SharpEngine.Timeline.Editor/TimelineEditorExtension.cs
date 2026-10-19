using System.Globalization;
using System.Net;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using SharpEngine.Editor.Layout;
using SharpEngine.Animation;
using SharpEngine.Editor;
using SharpEngine.Assets;
using SharpEngine.Mathematics;
using SharpEngine.Playables;

namespace SharpEngine.Timeline;

internal sealed class TimelineEditorExtension : IEditorExtension,
    IEditorExtensionUpdate, IEditorExtensionUiRefresh, IEditorExtensionActionHandler,
    ICustomBarExtension
{
    private const string TimelinePaneMarkupId = "timelineView";
    private const float ViewWidth = 1200;
    private const float ViewHeight = 320;
    private const float PreviewStageWidth = 220;
    private const float PreviewStageHeight = 150;
    private const float PreviewStagePadding = 14;
    private readonly List<IDisposable> Registrations = new();
    private EditorContext? Context;
    private readonly Dictionary<string, TimelineAssetToolDocument> Documents = new(StringComparer.OrdinalIgnoreCase);
    private TimelineAssetToolDocument? Active;
    private TimelineAuthoringSession Authoring
    {
        get
        {
            return Active!.Authoring!;
        }
    }
    private bool HasTimeline
    {
        get
        {
            return Active?.Authoring is not null;
        }
    }
    private TimelineView View
    {
        get
        {
            return Active!.View!;
        }
        set
        {
            Active!.View = value;
        }
    }
    private SkeletalAnimationAsset? PreviewAnimation;
    private TimelineClip? PreviewClip;
    private bool TimelineWritable
    {
        get
        {
            return Active?.Writable == true;
        }
    }
    private bool IsPlaying;

    private TimelineDocument Document
    {
        get
        {
            return Authoring.Document;
        }
    }

    public string BarId
    {
        get
        {
            return EditorCustomBarRegistry.TimelineSurfaceId;
        }
    }

    public EditorCustomBarMode Mode
    {
        get
        {
            return EditorCustomBarMode.Replace;
        }
    }

    public void Startup(EditorContext Context)
    {
        this.Context = Context;
        Registrations.Add(Context.AssetOpeners.Register(new EditorAssetOpener(
            "engine.skeletal-animation-editor", "SkeletalAnimation", OpenAsset,
            Priority: -100, GetState: DescribeState)));
        Registrations.Add(Context.AssetOpeners.Register(new EditorAssetOpener(
            "engine.timeline-editor", "Timeline", OpenAsset, Priority: -100)));
        Registrations.Add(Context.AssetOpeners.Register(new EditorAssetOpener(
            "engine.animation-state-machine-editor", "AnimationStateMachine",
            OpenAsset, Priority: -100, GetState: DescribeState)));
        RegisterCommand(Context, new EditorCommand("timeline.play", "Play Timeline",
            _ => StartPlayback(), _ => HasTimeline && !IsPlaying));
        RegisterCommand(Context, new EditorCommand("timeline.pause", "Pause Timeline",
            _ => PausePlayback(), _ => IsPlaying));
        RegisterCommand(Context, new EditorCommand("timeline.stop", "Stop Timeline",
            _ => StopPlayback(), _ => HasTimeline && (IsPlaying || View.State.CurrentTime > 0)));
        RegisterCommand(Context, new EditorCommand("timeline.add-notify", "Add Notify",
            _ => AddNotify(), _ => TimelineWritable && !IsPlaying));
        RegisterCommand(Context, new EditorCommand("timeline.undo", "Undo Timeline Edit",
            _ => Undo(), _ => HasTimeline && !IsPlaying && Authoring.CanUndo));
        RegisterCommand(Context, new EditorCommand("timeline.redo", "Redo Timeline Edit",
            _ => Redo(), _ => HasTimeline && !IsPlaying && Authoring.CanRedo));
        RegisterCommand(Context, new EditorCommand("timeline.save", "Save Timeline",
            _ => Save(), _ => !IsPlaying && TimelineWritable && Authoring.IsDirty));
        RegisterCommand(Context, new EditorCommand("timeline.seek-start", "Go To Start",
            _ => Seek(0), _ => HasTimeline && View.State.CurrentTime > 0));
        PublishUi();
        Registrations.Add(Context.CustomBars.Register(this));
    }

    public void Shutdown()
    {
        for (var Index = Registrations.Count - 1; Index >= 0; Index--)
        {
            Registrations[Index].Dispose();
        }

        Registrations.Clear();
        Documents.Clear();
        Active = null;
        Context = null;
    }

    public void Update(float DeltaSeconds)
    {
        if (!HasTimeline || !IsPlaying || !float.IsFinite(DeltaSeconds) || DeltaSeconds <= 0)
        {
            return;
        }

        var NextTime = View.State.CurrentTime + Math.Clamp(DeltaSeconds, 0, 0.1f);
        if (NextTime >= Document.Duration)
        {
            NextTime = Document.Duration;
            IsPlaying = false;
            Context?.SetStatus("Timeline preview completed");
        }

        View.Seek(NextTime);
        PublishUi();
    }

    public void RefreshUi()
    {
        PublishUi();
    }

    public bool TryHandleAction(string Action)
    {
        const string ActivatePrefix = "editor.timeline-activate:";
        if (Action.StartsWith(ActivatePrefix, StringComparison.Ordinal))
        {
            var AssetId = Action[ActivatePrefix.Length..];
            if (Documents.TryGetValue(AssetId, out var Existing))
            {
                Activate(Existing);
            }
            return true;
        }
        if (!HasTimeline)
        {
            return false;
        }
        const string SelectPrefix = "editor.timeline-select:";
        const string SeekPrefix = "editor.timeline-seek:";
        if (Action.StartsWith(SelectPrefix, StringComparison.Ordinal))
        {
            var ClipId = Action[SelectPrefix.Length..];
            if (!View.SelectClip(ClipId))
            {
                return true;
            }

            var Clip = Document.Tracks.SelectMany(Track => Track.Clips)
                .FirstOrDefault(Value => string.Equals(Value.ClipId, ClipId,
                    StringComparison.Ordinal));
            Context?.SetStatus(Clip is null
                ? $"Selected timeline clip: {ClipId}"
                : $"Selected notify: {Clip.PlayableId} · {FormatTime(Clip.Start)}");
            PublishUi();
            return true;
        }

        var HasSeekTime = double.TryParse(Action.StartsWith(SeekPrefix, StringComparison.Ordinal)
            ? Action[SeekPrefix.Length..] : string.Empty, NumberStyles.Float,
            CultureInfo.InvariantCulture, out var Time);
        if (Action.StartsWith(SeekPrefix, StringComparison.Ordinal) && HasSeekTime && double.IsFinite(Time))
        {
            Seek(Time);
            return true;
        }

        return false;
    }

    public string BuildMarkup(EditorCustomBarContext Context)
    {
        if (Active?.StateMachine is not null)
        {
            var Label = WebUtility.HtmlEncode($"State Machine · {Active.StateMachine.States.Count} states");
            return Context.Group("Animation State Machine", string.Empty,
                "custom-bar-group timeline-controls") +
                $"<span class=\"timeline-time\">{Label} · Read-only view</span>";
        }

        var Content = Context.CommandButton("timeline.play", Icon: "play") +
            Context.CommandButton("timeline.pause", Icon: "pause") +
            Context.CommandButton("timeline.stop", Icon: "stop") +
            Context.CommandButton("timeline.add-notify", Icon: "add") +
            Context.CommandButton("timeline.undo", Icon: "reset") +
            Context.CommandButton("timeline.redo", Icon: "reset") +
            Context.CommandButton("timeline.save", Icon: "save") +
            Context.CommandButton("timeline.seek-start", Label: "Start");
        var TimeLabel = Active is null ? "No animation selected" :
            $"{FormatTime(View.State.CurrentTime)} / {FormatTime(Document.Duration)}";
        return Context.Group("Timeline", Content, "custom-bar-group timeline-controls") +
            $"<span class=\"timeline-time\">{WebUtility.HtmlEncode(TimeLabel)}</span>";
    }

    private void StartPlayback()
    {
        if (View.State.CurrentTime >= Document.Duration)
        {
            View.Seek(0);
        }

        IsPlaying = true;
        Context?.SetStatus("Timeline preview playing");
        PublishUi();
    }

    private void PausePlayback()
    {
        IsPlaying = false;
        Context?.SetStatus("Timeline preview paused");
        PublishUi();
    }

    private void StopPlayback()
    {
        IsPlaying = false;
        View.Seek(0);
        Context?.SetStatus("Timeline preview stopped");
        PublishUi();
    }

    private void Seek(double Time)
    {
        View.Seek(Time);
        IsPlaying = false;
        Context?.SetStatus($"Timeline cursor · {FormatTime(View.State.CurrentTime)}");
        PublishUi();
    }

    private void AddNotify()
    {
        var NotifyTrack = Document.Tracks.FirstOrDefault(Track =>
            string.Equals(Track.Type, "notify", StringComparison.OrdinalIgnoreCase));
        if (NotifyTrack is null)
        {
            Context?.SetStatus("Notify track is unavailable");
            return;
        }

        var Duration = Math.Min(0.1, Document.Duration);
        var Start = Math.Clamp(0.25 * NotifyTrack.Clips.Count + 0.1,
            0, Document.Duration - Duration);
        var Occupied = NotifyTrack.Clips.OrderBy(ClipValue => ClipValue.Start).ToArray();
        foreach (var ExistingClip in Occupied)
        {
            if (ExistingClip.Start >= Start + Duration || ExistingClip.End <= Start)
            {
                continue;
            }

            Start = ExistingClip.End + 0.05;
        }

        if (Start + Duration > Document.Duration + 1e-9)
        {
            Context?.SetStatus("No free space remains on the Notify track");
            return;
        }

        var ClipId = $"notify-{Guid.NewGuid():N}";
        var Clip = new TimelineClip(ClipId, $"Notify {NotifyTrack.Clips.Count + 1}",
            Start, Duration);
        var Tracks = Document.Tracks.Select(Track => ReferenceEquals(Track, NotifyTrack)
            ? new TimelineTrack(Track.TrackId, Track.Name, Track.TargetId, Track.Type,
                Track.Clips.Concat([Clip]).ToArray(), Track.Muted)
            : Track).ToArray();
        var Result = Authoring.Replace(new TimelineDocument(Document.TimelineId, Document.Name,
            Document.Duration, Document.FrameRate, Tracks), "add-notify", Authoring.DocumentRevision);
        if (!Result.Success)
        {
            Context?.SetStatus(Result.Message);
            return;
        }
        View = new TimelineView(Document, View.State);
        View.SelectClip(ClipId);
        Context?.SetStatus($"Added notify: {Clip.PlayableId}");
        PublishUi();
    }

    private void Undo()
    {
        var Result = Authoring.Undo(Authoring.DocumentRevision);
        if (!Result.Success)
        {
            Context?.SetStatus(Result.Message);
            return;
        }

        View = new TimelineView(Document, View.State);
        Context?.SetStatus("Timeline edit undone");
        PublishUi();
    }

    private void Redo()
    {
        var Result = Authoring.Redo(Authoring.DocumentRevision);
        if (!Result.Success)
        {
            Context?.SetStatus(Result.Message);
            return;
        }

        View = new TimelineView(Document, View.State);
        Context?.SetStatus("Timeline edit redone");
        PublishUi();
    }

    private void Save()
    {
        if (Active is null || !TimelineWritable)
        {
            Context?.SetStatus("Engine Timeline assets are read-only; copy one into the project to save");
            return;
        }

        try
        {
            Active.Save();
            Context?.SetStatus($"Saved Timeline · {Document.Name}");
            PublishUi();
        }
        catch (Exception Exception)
        {
            SharpEngine.Logging.SharpLog.Error("Timeline", "Timeline save failed", Exception);
            Context?.SetStatus($"Asset save failed: {Exception.Message}");
        }
    }

    private void PublishUi()
    {
        if (Context is null)
        {
            return;
        }

        var Tabs = new StringBuilder();
        foreach (var Tool in Documents.Values)
        {
            Tabs.Append("<button class=\"timeline-document-tab")
                .Append(ReferenceEquals(Tool, Active) ? " active" : string.Empty)
                .Append("\" data-action=\"editor.timeline-activate:")
                .Append(WebUtility.HtmlEncode(Tool.Asset.Id)).Append("\">")
                .Append(WebUtility.HtmlEncode(Tool.Asset.Name))
                .Append(Tool.Authoring?.IsDirty == true ? " *" : string.Empty).Append("</button>");
        }
        Context.SetMarkup("timelineDocuments", Tabs.ToString());
        if (Active?.StateMachine is not null)
        {
            Context.SetMarkup(TimelinePaneMarkupId, BuildStateMachineMarkup(Active.StateMachine));
            Context.SetMarkup("timelinePreview", BuildStateMachinePreviewMarkup(Active));
            Context.SetText("timelineCurrentTime", "State graph");
            Context.SetText("timelineInspectorLabel", "STATE MACHINE");
            Context.SetText("timelineSelectedNotify",
                $"Initial state · {Active.StateMachine.InitialStateId} · {Active.StateMachine.Transitions.Count} transitions");
            return;
        }
        if (Active is null)
        {
            Context.SetMarkup(TimelinePaneMarkupId, "<div class=\"timeline-empty\">Double-click a SkeletalAnimation, Timeline, or AnimationStateMachine asset in Content Manager</div>");
            Context.SetMarkup("timelinePreview", "<div class=\"timeline-empty\">Open an animation, Timeline, or AnimationStateMachine asset to preview it</div>");
            return;
        }
        Context.SetMarkup(TimelinePaneMarkupId, View.BuildMarkup(ViewWidth, ViewHeight));
        Context.SetMarkup("timelinePreview", BuildPreviewMarkup());
        Context.SetText("timelineInspectorLabel", "NOTIFY");
        Context.SetText("timelineCurrentTime",
            $"{FormatTime(View.State.CurrentTime)} / {FormatTime(Document.Duration)}");
        var SelectedClip = Document.Tracks.SelectMany(Track => Track.Clips)
            .FirstOrDefault(Clip => string.Equals(Clip.ClipId, View.State.SelectedClipId,
                StringComparison.Ordinal));
        Context.SetText("timelineSelectedNotify", SelectedClip is null
            ? "Select a clip or add a notify"
            : $"{SelectedClip.PlayableId} · {FormatTime(SelectedClip.Start)}");
    }

    private void OpenAsset(EditorAssetOpenContext OpenContext)
    {
        if (!Documents.TryGetValue(OpenContext.Asset.Id, out var Tool))
        {
            Tool = new TimelineAssetToolDocument(Context!.Assets!, OpenContext.Asset, OpenContext.Path);
            Documents.Add(OpenContext.Asset.Id, Tool);
        }
        Activate(Tool);
        OpenContext.ShowPane(EditorPaneIds.Timeline);
        PublishUi();
    }

    private void Activate(TimelineAssetToolDocument Tool)
    {
        IsPlaying = false;
        Active = Tool;
        PreviewAnimation = Tool.Animation;
        PreviewClip = Tool.Authoring?.Document.Tracks.FirstOrDefault(Track =>
            Track.Type.Equals("skeletal-animation", StringComparison.OrdinalIgnoreCase))?.Clips.FirstOrDefault();
        PublishUi();
        Context?.SetStatus($"Animation tool · {Tool.Asset.Name}");
    }

    private JsonObject DescribeState()
    {
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["activeAssetId"] = Active?.Asset.Id,
            ["activeAssetType"] = Active?.Asset.Type,
            ["playing"] = IsPlaying,
            ["documents"] = new JsonArray(Documents.Values.Select(Tool => (JsonNode)Tool.Describe()).ToArray()),
        };
    }

    private static string BuildStateMachineMarkup(AnimationStateMachineAsset StateMachine)
    {
        var Markup = new StringBuilder("<div class=\"animation-state-machine-editor\">");
        Markup.Append("<div class=\"animation-state-machine-header\"><strong>States</strong><span>")
            .Append(StateMachine.States.Count).Append(" states · ")
            .Append(StateMachine.Transitions.Count).Append(" transitions</span></div>");
        Markup.Append("<div class=\"animation-state-machine-states\">");
        foreach (var State in StateMachine.States)
        {
            Markup.Append("<div class=\"animation-state-machine-state")
                .Append(State.StateId == StateMachine.InitialStateId ? " initial" : string.Empty)
                .Append("\" data-state-id=\"").Append(WebUtility.HtmlEncode(State.StateId))
                .Append("\"><strong>").Append(WebUtility.HtmlEncode(State.StateId))
                .Append(State.StateId == StateMachine.InitialStateId ? " (Initial)" : string.Empty)
                .Append("</strong><span>Clip: ").Append(WebUtility.HtmlEncode(State.ClipId))
                .Append("</span><span>").Append(State.Loop ? "Loop" : "Once")
                .Append(" · Speed ").Append(State.Speed.ToString("0.###", CultureInfo.InvariantCulture))
                .Append("</span></div>");
        }
        Markup.Append("</div><div class=\"animation-state-machine-transitions\"><strong>Transitions</strong>");
        for (var Index = 0; Index < StateMachine.Transitions.Count; Index++)
        {
            var Transition = StateMachine.Transitions[Index];
            var Conditions = string.Join(" and ", Transition.Conditions.Select(Condition =>
                $"{Condition.ParameterId} {Condition.Comparison} {Condition.Value.ToString("0.###", CultureInfo.InvariantCulture)}"));
            var ExitTime = Transition.ExitTime is null ? "Immediate" :
                $"Exit time ≥ {Transition.ExitTime.Value.ToString("0.###", CultureInfo.InvariantCulture)}";
            Markup.Append("<div data-transition-id=\"")
                .Append(WebUtility.HtmlEncode(Transition.TransitionId)).Append("\"><span>")
                .Append(Index + 1).Append(". ").Append(WebUtility.HtmlEncode(Transition.FromStateId))
                .Append(" → ").Append(WebUtility.HtmlEncode(Transition.ToStateId))
                .Append("</span><span>").Append(WebUtility.HtmlEncode(Conditions.Length == 0 ? "Always" : Conditions))
                .Append(" · ").Append(WebUtility.HtmlEncode(ExitTime))
                .Append("</span></div>");
        }
        Markup.Append("</div></div>");
        return Markup.ToString();
    }

    private static string BuildStateMachinePreviewMarkup(TimelineAssetToolDocument Tool)
    {
        var StateMachine = Tool.StateMachine!;
        var Document = StateMachine.ToJson();
        var Name = Document["name"]?.GetValue<string>() ?? StateMachine.AssetId;
        var SourceLabel = Tool.Asset.Source == AssetSource.Engine ? "Engine asset" : "Project asset";
        if (Tool.Asset.Tags.Contains("preset", StringComparer.OrdinalIgnoreCase))
        {
            SourceLabel += " preset";
        }
        var Parameters = string.Join(", ", StateMachine.Parameters.Select(Parameter =>
            $"{Parameter.ParameterId}={Parameter.DefaultValue.ToString("0.###", CultureInfo.InvariantCulture)}"));
        return $"<div class=\"timeline-preview-info\"><div class=\"timeline-preview-title\">{WebUtility.HtmlEncode(Name)}</div><div class=\"timeline-preview-subtitle\">{WebUtility.HtmlEncode(StateMachine.AssetId)} · {WebUtility.HtmlEncode(SourceLabel)} · Read-only view</div><div>Initial state: {WebUtility.HtmlEncode(StateMachine.InitialStateId)}</div><div>Parameters: {WebUtility.HtmlEncode(Parameters)}</div><div>Clip dependencies: {StateMachine.ClipIds.Count}</div></div>";
    }

    private string BuildPreviewMarkup()
    {
        if (PreviewAnimation is null || PreviewClip is null)
        {
            return "<div class=\"timeline-preview-empty\">No skeletal animation clip is available for preview</div>";
        }

        var Time = Math.Clamp(View.State.CurrentTime - PreviewClip.Start, 0, PreviewClip.Duration);
        var AnimationTime = PreviewClip.SourceStart + Time * PreviewClip.Speed;
        var Pose = PreviewClip.LoopMode == PlayableLoopMode.Loop
            ? PreviewAnimation.SampleLooped(AnimationTime) : PreviewAnimation.Sample(AnimationTime);
        var BoneMatrices = BuildPreviewBoneMatrices(PreviewAnimation, Pose);
        var Points = BuildPreviewPoints(PreviewAnimation, BoneMatrices);
        var Markup = new System.Text.StringBuilder("<div class=\"timeline-preview\"><div class=\"timeline-preview-stage\"><div class=\"timeline-preview-grid\" />");
        foreach (var Bone in PreviewAnimation.Bones)
        {
            if (!Points.TryGetValue(Bone.BoneId, out var Point))
            {
                continue;
            }
            if (Bone.ParentBoneId is not null && Points.TryGetValue(Bone.ParentBoneId, out var Parent))
            {
                var Delta = Point - Parent;
                var Length = Delta.Length();
                if (Length > 0.5f)
                {
                    var Angle = MathF.Atan2(Delta.Y, Delta.X) * 180 / MathF.PI;
                    Markup.Append("<div class=\"timeline-preview-link\" style=\"left:")
                        .Append(Number(Parent.X)).Append("px;top:").Append(Number(Parent.Y))
                        .Append("px;width:").Append(Number(Length)).Append("px;transform:rotate(")
                        .Append(Number(Angle)).Append("deg)\" />");
                }
            }
            Markup.Append("<div class=\"timeline-preview-bone\" style=\"left:")
                .Append(Number(Point.X)).Append("px;top:").Append(Number(Point.Y)).Append("px\" title=\"")
                .Append(WebUtility.HtmlEncode(Bone.Name)).Append("\" />");
        }
        var Percent = PreviewAnimation.Duration <= 0 ? 0 : AnimationTime / PreviewAnimation.Duration * 100;
        Markup.Append("</div><div class=\"timeline-preview-info\"><div class=\"timeline-preview-title\">")
            .Append(WebUtility.HtmlEncode(PreviewAnimation.Name)).Append("</div><div class=\"timeline-preview-subtitle\">")
            .Append(WebUtility.HtmlEncode(Authoring.Document.Name)).Append(" · ")
            .Append(FormatTime(AnimationTime)).Append(" / ").Append(FormatTime(PreviewAnimation.Duration))
            .Append(TimelineWritable ? " · Project asset" : " · Engine asset (read-only)")
            .Append("</div><div class=\"timeline-preview-progress\"><div class=\"timeline-preview-progress-value\" style=\"width:")
            .Append(Number(Math.Clamp(Percent, 0, 100))).Append("%\" /></div><div class=\"timeline-preview-pose\">");
        foreach (var Bone in Pose.Bones)
        {
            Markup.Append(WebUtility.HtmlEncode(Bone.BoneId)).Append("  ")
                .Append(Bone.Transform.Position.X.ToString("0.000", CultureInfo.InvariantCulture))
                .Append(", ").Append(Bone.Transform.Position.Y.ToString("0.000", CultureInfo.InvariantCulture)).Append('\n');
        }
        Markup.Append("</div></div></div>");
        return Markup.ToString();
    }

    private static IReadOnlyDictionary<string, Matrix4x4> BuildPreviewBoneMatrices(
        SkeletalAnimationAsset Animation, SkeletalPose Pose)
    {
        var Bones = Animation.Bones.ToDictionary(Bone => Bone.BoneId,
            StringComparer.Ordinal);
        var LocalTransforms = Pose.Bones.ToDictionary(Bone => Bone.BoneId,
            Bone => Bone.Transform, StringComparer.Ordinal);
        var Matrices = new Dictionary<string, Matrix4x4>(StringComparer.Ordinal);
        var Visiting = new HashSet<string>(StringComparer.Ordinal);

        Matrix4x4 Build(SkeletonBone Bone)
        {
            if (Matrices.TryGetValue(Bone.BoneId, out var Existing))
            {
                return Existing;
            }
            if (!Visiting.Add(Bone.BoneId))
            {
                throw new InvalidDataException($"Animation preview found a parent cycle at '{Bone.BoneId}'");
            }

            var Transform = LocalTransforms[Bone.BoneId];
            var Local = TransformMath.Trs(Transform.Position, Transform.Rotation,
                Transform.Scale);
            var Parent = Bone.ParentBoneId is null
                ? Matrix4x4.Identity
                : Build(Bones[Bone.ParentBoneId]);
            var World = Local * Parent;
            Visiting.Remove(Bone.BoneId);
            Matrices.Add(Bone.BoneId, World);
            return World;
        }

        foreach (var Bone in Animation.Bones)
        {
            Build(Bone);
        }

        return Matrices;
    }

    private static IReadOnlyDictionary<string, Vector2> BuildPreviewPoints(
        SkeletalAnimationAsset Animation,
        IReadOnlyDictionary<string, Matrix4x4> BoneMatrices)
    {
        var Positions = Animation.Bones.ToDictionary(Bone => Bone.BoneId,
            Bone => BoneMatrices[Bone.BoneId].Translation, StringComparer.Ordinal);
        var Minimum = new Vector2(float.MaxValue, float.MaxValue);
        var Maximum = new Vector2(float.MinValue, float.MinValue);
        foreach (var Position in Positions.Values)
        {
            Minimum = Vector2.Min(Minimum, new Vector2(Position.X, Position.Y));
            Maximum = Vector2.Max(Maximum, new Vector2(Position.X, Position.Y));
        }

        var Size = Vector2.Max(Maximum - Minimum, new Vector2(0.001f, 0.001f));
        var AvailableWidth = PreviewStageWidth - PreviewStagePadding * 2;
        var AvailableHeight = PreviewStageHeight - PreviewStagePadding * 2;
        var Scale = MathF.Min(AvailableWidth / Size.X, AvailableHeight / Size.Y);
        var Center = (Minimum + Maximum) * 0.5f;
        var Result = new Dictionary<string, Vector2>(StringComparer.Ordinal);
        foreach (var Pair in Positions)
        {
            var Position = new Vector2(Pair.Value.X, Pair.Value.Y);
            Result[Pair.Key] = new Vector2(
                PreviewStageWidth * 0.5f + (Position.X - Center.X) * Scale,
                PreviewStageHeight * 0.5f - (Position.Y - Center.Y) * Scale);
        }
        return Result;
    }

    private static string Number(float Value) => Value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Number(double Value) => Value.ToString("0.###", CultureInfo.InvariantCulture);

    private void RegisterCommand(EditorContext Context, EditorCommand Command)
    {
        Registrations.Add(Context.Commands.Register(Command));
    }

    private static string FormatTime(double Time) =>
        TimeSpan.FromSeconds(Time).ToString(@"m\:ss\.fff", CultureInfo.InvariantCulture);

}
