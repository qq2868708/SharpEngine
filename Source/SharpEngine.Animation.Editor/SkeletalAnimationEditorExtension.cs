using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharpEngine.Timeline;
using SharpEngine.Assets;
using SharpEngine.Editor;
using SharpEngine.Editor.Layout;
using SharpEngine.Playables;
using SharpEngine.UI;

namespace SharpEngine.Animation;

/// <summary>Dedicated skeletal-animation editor; feature logic and its one RML layout are maintained together.</summary>
internal sealed class SkeletalAnimationEditorExtension : IEditorExtension
{
    private IDisposable? Registration;

    public void Startup(EditorContext Context)
    {
        Registration = Context.AssetOpeners.Register(new EditorAssetOpener(
            "engine.skeletal-animation-editor", "SkeletalAnimation", OpenAsset,
            Priority: -100, EditorId: "skeletal-animation-editor"));
    }

    public void Shutdown()
    {
        Registration?.Dispose();
        Registration = null;
    }

    private static IAssetEditorInstance OpenAsset(EditorAssetOpenContext Context) { return new EditorInstance(Context); }

    private sealed class EditorInstance : IAssetEditorInstance
    {
        private static readonly AssetEditorLayoutDefinition Layout = AssetEditorLayoutDefinition.LoadEmbedded(
            typeof(SkeletalAnimationEditorExtension).Assembly, "SharpEngine.Animation.SkeletalAnimationEditor.rml");
        private readonly EditorAssetOpenContext Context;
        private readonly SkeletalAnimationDocument Tool;
        private readonly AssetRegistry Assets;
        private readonly AssetPreviewWorld PreviewWorld;
        private readonly Viewport3D Viewport;
        private readonly AssetEditorLayoutInstance LayoutInstance;
        private readonly List<SkeletalMeshAsset> CompatibleMeshes = [];
        private TimelineView? SubscribedView;
        private SkeletalMeshAsset? PreviewMesh;
        private string? DefaultPreviewMeshId;
        private bool PreviewMeshOverridden;
        private float TimelineWidth = 1200;
        private float TimelineHeight = 320;

        internal EditorInstance(EditorAssetOpenContext Context)
        {
            this.Context = Context;
            LayoutInstance = Layout.CreateInstance(Context.EditorRootId);
            Assets = Context.Runtime.Assets ?? throw new InvalidOperationException("Asset registry is unavailable");
            Tool = new SkeletalAnimationDocument(Assets, Context.Asset, Context.Path);
            PreviewWorld = new AssetPreviewWorld(Context.Runtime.Components ??
                throw new InvalidOperationException("Component registry is unavailable"), "animation-preview:" + Context.Asset.Id);
            Viewport = new Viewport3D("timelinePreviewViewport", "asset-preview");
            Viewport.AddContent(new WorldViewportContent(PreviewWorld.World, Assets));
            ResolveCompatibleMeshes();
            SubscribeTimeline();
            UpdatePreviewPose();
        }

        public AssetDescriptor Asset
        {
            get
            {
                return Tool.Asset;
            }
        }
        public string EditorId
        {
            get
            {
                return "skeletal-animation-editor";
            }
        }
        public string Title
        {
            get
            {
                return Asset.Name;
            }
        }
        public string PrimaryPaneId
        {
            get
            {
                return Layout.PrimaryPaneId;
            }
        }
        public bool IsDirty
        {
            get
            {
                return Tool.IsDirty;
            }
        }
        public bool IsReadOnly
        {
            get
            {
                return !Tool.Writable;
            }
        }
        public IReadOnlyList<AssetEditorPaneDefinition> Panes
        {
            get
            {
                return Layout.Panes;
            }
        }
        public IReadOnlyList<AssetEditorCommandDefinition> Commands
        {
            get;
        } =
        [
            new("root-motion", "Enable Root Motion", "Edit", 90, null, true),
            new("add-notify", "Add Notify", "Edit", 100, "add", true),
            new("undo", "Undo", "Edit", 200, "reset", true),
            new("redo", "Redo", "Edit", 210, "reset", true),
        ];
        private SkeletalAnimationAsset Animation
        {
            get
            {
                return Tool.Animation;
            }
        }
        private TimelineView View
        {
            get
            {
                return Tool.View;
            }
        }
        private TimelineDocument Document
        {
            get
            {
                return Tool.Document;
            }
        }
        private bool IsPlaying
        {
            get
            {
                return View.IsPlaying;
            }
        }

        public EditorLayoutDocument CreateDefaultLayout() { return Layout.CreateDockLayout(); }

        public bool CanExecuteCommand(string CommandId)
        {
            return CommandId switch
            {
                "play" => !IsPlaying,
                "pause" => IsPlaying,
                "stop" => IsPlaying || View.State.CurrentTime > 0,
                "seek-start" => View.State.CurrentTime > 0,
                "root-motion" or "add-notify" => !IsReadOnly && !IsPlaying,
                "undo" => !IsPlaying && Tool.CanUndo,
                "redo" => !IsPlaying && Tool.CanRedo,
                _ => false,
            };
        }

        public void ExecuteCommand(string CommandId)
        {
            switch (CommandId)
            {
                case "play":
                    View.Play();
                    Context.SetStatus("Timeline preview playing");
                    break;
                case "pause":
                    View.Pause();
                    Context.SetStatus("Timeline preview paused");
                    break;
                case "stop":
                    View.Stop();
                    Context.SetStatus("Timeline preview stopped");
                    break;
                case "seek-start":
                    View.Seek(0);
                    Context.SetStatus("Timeline cursor · 00:00.000");
                    break;
                case "root-motion":
                    Tool.SetRootMotion(!Animation.RootMotion.Enabled);
                    RefreshTimeline();
                    break;
                case "add-notify":
                    AddNotify();
                    break;
                case "undo":
                    if (Tool.Undo())
                    {
                        RefreshTimeline();
                    }
                    break;
                case "redo":
                    if (Tool.Redo())
                    {
                        RefreshTimeline();
                    }
                    break;
            }
        }

        public bool TryHandleAction(string Action)
        {
            if (LayoutInstance.TryHandleAction(Action))
            {
                return true;
            }
            const string SelectPrefix = "editor.timeline-select:";
            if (Action.StartsWith(SelectPrefix, StringComparison.Ordinal))
            {
                View.SelectClip(Action[SelectPrefix.Length..]);
                return true;
            }
            if (Viewport.TryHandleAction(Action) || View.TryHandleAction(Action))
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
            var Selected = Document.Tracks.SelectMany(Track => Track.Clips)
                .FirstOrDefault(Clip => Clip.ClipId == View.State.SelectedClipId);
            var Values = new JsonObject
            {
                ["AnimationName"] = Animation.Name,
                ["TimelineWidth"] = TimelineWidth,
                ["TimelineHeight"] = TimelineHeight,
                ["SelectedNotify"] = Selected is null ? "Select a clip or add a notify" :
                    Selected.PlayableId + " · " + FormatTime(Selected.Start),
                ["NoMeshes"] = CompatibleMeshes.Count == 0,
                ["HasMeshes"] = CompatibleMeshes.Count != 0,
                ["Meshes"] = new JsonArray(CompatibleMeshes.Select(Mesh => (JsonNode)new JsonObject
                {
                    ["Id"] = Uri.EscapeDataString(Mesh.AssetId),
                    ["Name"] = Mesh.Name,
                }).ToArray()),
                ["RootMotionDisabled"] = IsReadOnly || IsPlaying,
                ["RootMotionState"] = Animation.RootMotion.Enabled ? "Enabled" : "Disabled",
                ["DetailsJson"] = Tool.Describe().ToJsonString(new JsonSerializerOptions
                {
                    WriteIndented = true,
                }),
            };
            var Parts = new Dictionary<string, IUiLayoutPart>(StringComparer.Ordinal)
            {
                ["Viewport"] = Viewport,
                ["Timeline"] = View,
            };
            return LayoutInstance.RenderPane(PaneId, new(TimelineWidth, TimelineHeight, Title), Values, Parts);
        }

        public void Update(float DeltaSeconds)
        {
            SubscribeTimeline();
            if (float.IsFinite(DeltaSeconds) && DeltaSeconds >= 0)
            {
                View.Update(DeltaSeconds);
            }
            var Found = Context.TryGetElementRect("timelineViewHost", out var Bounds);
            if (Found && Bounds.Width > 0 && Bounds.Height > 0)
            {
                TimelineWidth = Math.Max(TimelineView.DefaultHeaderWidth + 1, Bounds.Width);
                TimelineHeight = Bounds.Height;
            }
            Viewport.Render(Context);
        }

        public void Save()
        {
            Tool.Save();
            SharpEngine.World.SceneRenderer.InvalidateAssets(Assets);
            RefreshTimeline();
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
            State["runtimeUi"] = LayoutInstance.Describe();
            State["timelineState"] = new JsonObject
            {
                ["schemaVersion"] = 1,
                ["stateRevision"] = View.StateRevision,
                ["playing"] = View.IsPlaying,
                ["pixelsPerSecond"] = View.State.PixelsPerSecond,
                ["horizontalScroll"] = View.State.HorizontalScroll,
                ["verticalScroll"] = View.State.VerticalScroll,
                ["selectedTrackId"] = View.State.SelectedTrackId,
                ["selectedClipId"] = View.State.SelectedClipId,
                ["view"] = JsonNode.Parse(View.BuildSnapshot(TimelineWidth, TimelineHeight).ToJson()),
            };
            return State;
        }

        public void Dispose()
        {
            LayoutInstance.Dispose();
            if (SubscribedView is not null)
            {
                SubscribedView.Pause();
                SubscribedView.TimeChanged -= OnTimelineTimeChanged;
                SubscribedView = null;
            }
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
            SubscribedView = View;
            SubscribedView.TimeChanged += OnTimelineTimeChanged;
        }

        private void RefreshTimeline()
        {
            SubscribeTimeline();
            UpdatePreviewPose();
        }

        private void OnTimelineTimeChanged(TimelineTimeChangedEvent Event) { UpdatePreviewPose(); }

        private void UpdatePreviewPose()
        {
            if (PreviewMesh is null)
            {
                return;
            }
            var Clip = Document.Tracks.First(Track => Track.Type == "skeletal-animation").Clips[0];
            var Time = Math.Clamp(View.State.CurrentTime - Clip.Start, 0, Clip.Duration);
            PreviewWorld.SetPose(Animation.SamplePreview(Clip.SourceStart + Time * Clip.Speed,
                Clip.LoopMode == PlayableLoopMode.Loop));
        }

        private void ResolveCompatibleMeshes()
        {
            if (Animation.SkeletonId is null)
            {
                return;
            }
            DefaultPreviewMeshId = SkeletonContent.Load(Assets, Animation.SkeletonId).PreviewMeshId;
            foreach (var Descriptor in Assets.Assets.Where(Asset => Asset.Type.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    var Mesh = SkeletalMeshContent.Load(Assets, Descriptor.Id);
                    SkeletalAnimationCompatibility.Validate(Animation, Mesh);
                    CompatibleMeshes.Add(Mesh);
                }
                catch (Exception Error) when (Error is InvalidDataException or IOException or KeyNotFoundException)
                {
                }
            }
            CompatibleMeshes.Sort((Left, Right) => string.Compare(Left.AssetId, Right.AssetId, StringComparison.Ordinal));
            SetPreviewMesh(DefaultPreviewMeshId);
        }

        private void SetPreviewMesh(string? AssetId)
        {
            if (AssetId is null)
            {
                PreviewMesh = null;
                PreviewMeshOverridden = false;
                PreviewWorld.SetMesh(null, "engine.skeletal-mesh");
                return;
            }
            var Mesh = CompatibleMeshes.FirstOrDefault(Candidate => Candidate.AssetId.Equals(AssetId, StringComparison.OrdinalIgnoreCase));
            if (Mesh is null)
            {
                Context.SetStatus("Preview mesh is missing or incompatible: " + AssetId);
                return;
            }
            PreviewMesh = Mesh;
            PreviewWorld.SetMesh(Mesh.AssetId, "engine.skeletal-mesh", Mesh.DefaultMaterial, Mesh);
            Viewport.Frame(Mesh.Positions.Select(Position => new System.Numerics.Vector3(Position.X, Position.Y, Position.Z)));
            UpdatePreviewPose();
            PreviewMeshOverridden = !AssetId.Equals(DefaultPreviewMeshId, StringComparison.OrdinalIgnoreCase);
            Context.SetStatus("Preview mesh · " + Mesh.Name);
        }

        private void AddNotify()
        {
            var Changed = Tool.AddNotify(out var Message);
            if (!Changed)
            {
                Context.SetStatus(Message);
                return;
            }
            RefreshTimeline();
        }

        private static string FormatTime(double Time)
        {
            var Minutes = (int)(Time / 60);
            return Minutes.ToString("00", CultureInfo.InvariantCulture) + ":" +
                (Time - Minutes * 60).ToString("00.000", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Animation authoring owns history; the Timeline is a derived presentation.</summary>
    private sealed class SkeletalAnimationDocument
    {
        private readonly JsonAssetAuthoringDocument Authoring;
        private readonly string Path;

        public SkeletalAnimationDocument(AssetRegistry Assets, AssetDescriptor Asset, string Path)
        {
            this.Path = Path;
            Authoring = new JsonAssetAuthoringDocument(Asset, Path,
                Value => SkeletalAnimationAsset.Parse(Value.ToJsonString(), Path));
            Animation = SkeletalAnimationContent.Load(Assets, Asset.Id);
            Document = BuildTimeline();
            View = new TimelineView(Document);
        }

        public AssetDescriptor Asset
        {
            get
            {
                return Authoring.Asset;
            }
        }
        public SkeletalAnimationAsset Animation
        {
            get;
            private set;
        }
        public TimelineDocument Document
        {
            get;
            private set;
        }
        public TimelineView View
        {
            get;
            private set;
        }
        public bool Writable
        {
            get
            {
                return !Asset.ReadOnly && Asset.Source == AssetSource.Project;
            }
        }
        public bool IsDirty
        {
            get
            {
                return Authoring.IsDirty;
            }
        }
        public bool CanUndo
        {
            get
            {
                return Authoring.CanUndo;
            }
        }
        public bool CanRedo
        {
            get
            {
                return Authoring.CanRedo;
            }
        }

        public JsonObject Describe()
        {
            return new JsonObject
            {
                ["assetId"] = Asset.Id,
                ["type"] = Asset.Type,
                ["source"] = Asset.Source.ToString(),
                ["readOnly"] = !Writable,
                ["documentRevision"] = Authoring.Revision,
                ["savepointRevision"] = Authoring.SavepointRevision,
                ["dirty"] = IsDirty,
                ["currentTime"] = View.State.CurrentTime,
                ["boneCount"] = Animation.Bones.Count,
                ["document"] = Document.ToJson(),
                ["rootMotion"] = new JsonObject
                {
                    ["enabled"] = Animation.RootMotion.Enabled,
                    ["boneId"] = Animation.RootMotion.BoneId,
                },
            };
        }

        public void Save()
        {
            if (!Writable)
            {
                throw new InvalidOperationException("This asset is read-only");
            }
            Authoring.Save();
            RefreshAnimation();
        }

        public bool SetRootMotion(bool Enabled)
        {
            if (!Writable)
            {
                return false;
            }
            var Changed = Authoring.Edit("root-motion", Value =>
            {
                var Candidate = SkeletalAnimationAsset.Parse(Value.ToJsonString());
                ReplaceJson(Value, Candidate.WithRootMotion(new(Enabled, Candidate.RootMotion.BoneId)).ToJson());
            }, out var Message);
            if (!Changed && Message.Length != 0)
            {
                throw new InvalidOperationException(Message);
            }
            if (Changed)
            {
                RefreshAnimation();
            }
            return Changed;
        }

        public bool AddNotify(out string Message)
        {
            Message = string.Empty;
            if (!Writable)
            {
                return false;
            }
            var Prepared = TimelineNotifyEditing.TryAdd(Document, out var Replacement, out var ClipId, out Message);
            if (!Prepared)
            {
                return false;
            }
            var Notifies = Replacement.Tracks.Where(Track => Track.Type == "notify")
                .SelectMany(Track => Track.Clips).Select(Clip => new AnimationNotify(
                    Clip.ClipId, Clip.PlayableId, Clip.Start, Clip.Duration)).ToArray();
            var Changed = Authoring.Edit("add-notify", Value =>
            {
                var Candidate = SkeletalAnimationAsset.Parse(Value.ToJsonString());
                ReplaceJson(Value, Candidate.WithNotifies(Notifies).ToJson());
            }, out Message);
            if (Changed)
            {
                RefreshAnimation();
                View.SelectClip(ClipId);
            }
            return Changed;
        }

        public bool Undo()
        {
            var Changed = Writable && Authoring.Undo();
            if (Changed)
            {
                RefreshAnimation();
            }
            return Changed;
        }

        public bool Redo()
        {
            var Changed = Writable && Authoring.Redo();
            if (Changed)
            {
                RefreshAnimation();
            }
            return Changed;
        }

        private void RefreshAnimation()
        {
            Animation = SkeletalAnimationAsset.Parse(Authoring.Source, Path);
            Document = BuildTimeline();
            View = new TimelineView(Document, View.State);
        }

        private TimelineDocument BuildTimeline()
        {
            return new TimelineDocument(Asset.Id, Animation.Name, Animation.Duration, Animation.SampleRate,
            [
                new TimelineTrack("animation", "Skeletal Animation", "preview", "skeletal-animation",
                [
                    new TimelineClip("animation-clip", Asset.Id, 0, Animation.Duration,
                        loopMode: PlayableLoopMode.Once),
                ]),
                new TimelineTrack("notify", "Animation Notify", "preview", "notify",
                    Animation.Notifies.Select(Notify => new TimelineClip(Notify.NotifyId,
                        Notify.Name, Notify.Time, Notify.Duration)).ToArray()),
            ]);
        }

        private static void ReplaceJson(JsonObject Target, JsonObject Source)
        {
            Target.Clear();
            foreach (var Pair in Source)
            {
                Target[Pair.Key] = Pair.Value?.DeepClone();
            }
        }
    }
}
