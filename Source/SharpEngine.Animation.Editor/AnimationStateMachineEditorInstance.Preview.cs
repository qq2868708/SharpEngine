using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using SharpEngine.Assets;
using SharpEngine.Editor;
using SharpEngine.UI;
using SharpEngine.World;

namespace SharpEngine.Animation;

internal sealed partial class AnimationStateMachineEditorExtension
{
    private sealed partial class EditorInstance
    {
        private AssetRegistry PreviewAssets = null!;
        private AssetPreviewWorld PreviewWorld = null!;
        private Viewport3D PreviewViewport = null!;
        private AnimationStateMachinePlayer? PreviewPlayer;
        private readonly Dictionary<string, double> PreviewParameters = new(StringComparer.Ordinal);
        private readonly List<SkeletalMeshAsset> PreviewMeshes = [];
        private string? PreviewMeshId;
        private bool PreviewPlaying;
        private ulong PreviewRevision = 1;
        private string PreviewDiagnostic = string.Empty;

        public bool SuppressMarkupRefresh
        {
            get
            {
                return false;
            }
        }

        public bool ConsumeMarkupRefreshRequest() { return false; }
        public IReadOnlyList<AssetEditorLiveProperty> DrainLiveProperties()
        {
            var Weight = PreviewPlayer?.Describe()["blendWeight"]?.GetValue<double>() ?? 0;
            return
            [
                new("stateMachineBlendFill", "width", (Weight * 100).ToString("0.###", CultureInfo.InvariantCulture) + "%"),
            ];
        }

        private void InitializePreview(AssetRegistry Assets)
        {
            PreviewAssets = Assets;
            PreviewWorld = new AssetPreviewWorld(Context.Runtime.Components ??
                throw new InvalidOperationException("Component registry is unavailable"), "state-machine-preview:" + Asset.Id);
            PreviewViewport = new Viewport3D("stateMachinePreviewViewport", "asset-preview");
            PreviewViewport.AddContent(new WorldViewportContent(PreviewWorld.World, Assets));
            RebuildPreview();
        }

        private void RebuildPreview()
        {
            PreviewRevision++;
            PreviewPlaying = false;
            PreviewPlayer = null;
            PreviewDiagnostic = string.Empty;
            PreviewParameters.Clear();
            PreviewMeshes.Clear();
            try
            {
                var Graph = StateMachine;
                var Clips = Graph.ClipIds.ToDictionary(Id => Id,
                    Id => SkeletalAnimationContent.Load(PreviewAssets, Id), StringComparer.OrdinalIgnoreCase);
                foreach (var Definition in Graph.Parameters)
                {
                    PreviewParameters.Add(Definition.ParameterId, Definition.DefaultValue);
                }
                foreach (var Descriptor in PreviewAssets.Assets.Where(Asset =>
                    Asset.Type.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        var Mesh = SkeletalMeshContent.Load(PreviewAssets, Descriptor.Id);
                        foreach (var Clip in Clips.Values)
                        {
                            SkeletalAnimationCompatibility.Validate(Clip, Mesh);
                        }
                        PreviewMeshes.Add(Mesh);
                    }
                    catch (Exception Error) when (Error is InvalidDataException or IOException or KeyNotFoundException)
                    {
                    }
                }
                PreviewMeshes.Sort((Left, Right) => string.Compare(Left.AssetId, Right.AssetId, StringComparison.Ordinal));
                var Previous = PreviewMeshId;
                var FirstClip = Clips[Graph.State(Graph.InitialStateId).ClipId];
                var Default = FirstClip.SkeletonId is null ? null :
                    SkeletonContent.Load(PreviewAssets, FirstClip.SkeletonId).PreviewMeshId;
                var MeshId = PreviewMeshes.FirstOrDefault(Mesh => Mesh.AssetId == Previous)?.AssetId ??
                    PreviewMeshes.FirstOrDefault(Mesh => Mesh.AssetId == Default)?.AssetId ??
                    PreviewMeshes.FirstOrDefault()?.AssetId;
                SetPreviewMesh(MeshId, Clips);
            }
            catch (Exception Error) when (Error is IOException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
            {
                PreviewDiagnostic = Error.Message;
                PreviewMeshId = null;
                PreviewWorld.SetMesh(null, "engine.skeletal-mesh");
            }
        }

        private void SetPreviewMesh(string? MeshId, IReadOnlyDictionary<string, SkeletalAnimationAsset>? Clips = null)
        {
            if (MeshId is null)
            {
                PreviewMeshId = null;
                PreviewPlayer = null;
                PreviewWorld.SetMesh(null, "engine.skeletal-mesh");
                PreviewDiagnostic = "No mesh is compatible with every state. Assign compatible Clips before previewing.";
                return;
            }
            var Mesh = PreviewMeshes.Single(Mesh => Mesh.AssetId == MeshId);
            var Graph = StateMachine;
            Clips ??= Graph.ClipIds.ToDictionary(Id => Id, Id => SkeletalAnimationContent.Load(PreviewAssets, Id),
                StringComparer.OrdinalIgnoreCase);
            var Candidate = new AnimationStateMachinePlayer(Graph, Clips, Mesh.Bones);
            var Skeleton = SkeletonContent.Load(PreviewAssets, Mesh.SkeletonId);
            PreviewWorld.SetMesh(MeshId, "engine.skeletal-mesh", Mesh.DefaultMaterial, Mesh, Skeleton);
            PreviewPlayer = Candidate;
            PreviewMeshId = MeshId;
            PreviewPlaying = false;
            PreviewDiagnostic = string.Empty;
            PreviewViewport.Frame(Mesh.Positions.Select(Position => new System.Numerics.Vector3(Position.X, Position.Y, Position.Z)));
            PreviewWorld.SetPose(PreviewPlayer.Sample(true));
        }

        private bool ExecutePreviewCommand(string CommandId)
        {
            if (CommandId is "play" or "pause" or "stop" or "step")
            {
                PreviewRevision++;
            }
            switch (CommandId)
            {
                case "play":
                    PreviewPlaying = true;
                    return true;
                case "pause":
                    PreviewPlaying = false;
                    return true;
                case "stop":
                    PreviewPlaying = false;
                    PreviewPlayer?.Seek(StateMachine.InitialStateId, 0);
                    PreviewWorld.SetPose(PreviewPlayer?.Sample(true));
                    return true;
                case "step":
                    AdvancePreview(1d / 60);
                    return true;
                default:
                    return false;
            }
        }

        private bool TryHandlePreviewAction(string Action)
        {
            if (PreviewViewport.TryHandleAction(Action))
            {
                PreviewRevision++;
                return true;
            }
            const string Prefix = "editor.state-machine-preview:";
            if (!Action.StartsWith(Prefix, StringComparison.Ordinal))
            {
                return false;
            }
            var Parts = Action[Prefix.Length..].Split(':', 3);
            try
            {
                if (Parts.Length < 2)
                {
                    throw new InvalidDataException("Incomplete state machine preview action.");
                }
                var Id = Uri.UnescapeDataString(Parts[1]);
                switch (Parts[0])
                {
                    case "parameter":
                        var Value = Parts.Length == 3 ? double.Parse(Parts[2], NumberStyles.Float, CultureInfo.InvariantCulture) : double.NaN;
                        if (!PreviewParameters.ContainsKey(Id) || !double.IsFinite(Value))
                        {
                            throw new InvalidDataException("Preview parameters require a declared ID and a finite numeric value.");
                        }
                        PreviewParameters[Id] = Value;
                        break;
                    case "state":
                        var StatePlayer = PreviewPlayer ??
                            throw new InvalidOperationException("A compatible preview mesh is required.");
                        StatePlayer.RequestState(Id);
                        break;
                    case "transition":
                        var TransitionPlayer = PreviewPlayer ??
                            throw new InvalidOperationException("A compatible preview mesh is required.");
                        TransitionPlayer.TriggerTransition(Id);
                        break;
                    case "mesh":
                        SetPreviewMesh(Id);
                        break;
                    default:
                        throw new InvalidDataException("Unknown state machine preview action.");
                }
                PreviewWorld.SetPose(PreviewPlayer?.Sample(true));
                PreviewDiagnostic = string.Empty;
                PreviewRevision++;
            }
            catch (Exception Error) when (Error is InvalidDataException or InvalidOperationException or ArgumentException or FormatException or OverflowException or IOException)
            {
                PreviewDiagnostic = Error.Message;
                PreviewRevision++;
                Context.SetStatus(PreviewDiagnostic);
            }
            return true;
        }

        private void UpdatePreview(float DeltaSeconds)
        {
            if (PreviewPlaying && float.IsFinite(DeltaSeconds) && DeltaSeconds > 0)
            {
                AdvancePreview(DeltaSeconds);
            }
            PreviewViewport.Render(Context);
        }

        private void AdvancePreview(double DeltaSeconds)
        {
            if (PreviewPlayer is null)
            {
                return;
            }
            try
            {
                PreviewPlayer.Advance(DeltaSeconds, PreviewParameters);
                PreviewWorld.SetPose(PreviewPlayer.Sample(true));
            }
            catch (Exception Error) when (Error is InvalidDataException or InvalidOperationException or ArgumentException)
            {
                PreviewPlaying = false;
                PreviewDiagnostic = Error.Message;
                Context.SetStatus(PreviewDiagnostic);
            }
            PreviewRevision++;
        }

        private string BuildPreviewMeshPicker()
        {
            var Markup = new StringBuilder("<div class=\"asset-preview-mesh-picker\"><strong>Preview Mesh (session only)</strong>");
            foreach (var Mesh in PreviewMeshes)
            {
                Markup.Append("<button data-action=\"editor.state-machine-preview:mesh:")
                    .Append(Uri.EscapeDataString(Mesh.AssetId)).Append("\">")
                    .Append(AssetEditorInstanceUtilities.Escape(Mesh.Name)).Append("</button>");
            }
            return Markup.Append("</div>").ToString();
        }

        private JsonObject DescribePreview()
        {
            return new JsonObject
            {
                ["schemaVersion"] = 1,
                ["stateRevision"] = PreviewRevision,
                ["context"] = "preview",
                ["transient"] = true,
                ["playing"] = PreviewPlaying,
                ["meshId"] = PreviewMeshId,
                ["compatibleMeshIds"] = new JsonArray(PreviewMeshes.Select(Mesh => (JsonNode)JsonValue.Create(Mesh.AssetId)!).ToArray()),
                ["diagnostic"] = PreviewDiagnostic,
                ["diagnostics"] = PreviewDiagnostic.Length == 0 ? new JsonArray() : new JsonArray(new JsonObject
                {
                    ["code"] = PreviewPlayer is null ? "ANIMATION_PREVIEW_UNAVAILABLE" : "ANIMATION_PREVIEW_INVALID",
                    ["severity"] = "Error",
                    ["objectId"] = Asset.Id,
                    ["fieldPath"] = "preview",
                    ["expected"] = "Compatible clips and mesh, finite declared parameters, and a valid transition source.",
                    ["actual"] = PreviewDiagnostic,
                    ["message"] = PreviewDiagnostic,
                    ["stateRevision"] = PreviewRevision,
                    ["retryable"] = true,
                }),
                ["parameters"] = new JsonObject(PreviewParameters.Select(Parameter =>
                    new KeyValuePair<string, JsonNode?>(Parameter.Key, JsonValue.Create(Parameter.Value)))),
                ["playback"] = PreviewPlayer?.Describe(),
                ["pose"] = PreviewPlayer?.Sample(true).ToJson(),
                ["world"] = PreviewWorld.Describe(),
                ["viewport"] = PreviewViewport.Describe(),
            };
        }
    }
}
