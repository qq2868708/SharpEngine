using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SharpEngine.Animation;
using SharpEngine.Assets;
using SharpEngine.Playables;

namespace SharpEngine.Timeline;

/// <summary>One asset owns its authoring history; the view is transient and the pose is derived.</summary>
internal sealed class TimelineAssetToolDocument
{
    private string SourceHash;

    public TimelineAssetToolDocument(AssetRegistry Assets, AssetDescriptor Asset, string Path)
    {
        this.Asset = Asset;
        this.Path = Path;
        SourceHash = HashSource();
        if (Asset.Type.Equals("AnimationStateMachine", StringComparison.OrdinalIgnoreCase))
        {
            StateMachine = AnimationStateMachineContent.Load(Assets, Asset.Id);
            return;
        }

        TimelineDocument Source;
        if (Asset.Type.Equals("SkeletalAnimation", StringComparison.OrdinalIgnoreCase))
        {
            Animation = SkeletalAnimationContent.Load(Assets, Asset.Id);
            Source = new TimelineDocument(Asset.Id, Animation.Name, Animation.Duration, Animation.SampleRate,
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
        else
        {
            Source = TimelineContent.Load(Assets, Asset.Id);
            if (Source.Tracks.All(Track => !Track.Type.Equals("notify", StringComparison.OrdinalIgnoreCase)))
            {
                Source = new TimelineDocument(Source.TimelineId, Source.Name, Source.Duration, Source.FrameRate,
                    Source.Tracks.Append(new TimelineTrack("notify", "Animation Notify", "preview", "notify", [])).ToArray());
            }
            var AnimationClip = Source.Tracks.FirstOrDefault(Track =>
                Track.Type.Equals("skeletal-animation", StringComparison.OrdinalIgnoreCase))?.Clips.FirstOrDefault();
            if (AnimationClip is not null)
            {
                Animation = SkeletalAnimationContent.Load(Assets, AnimationClip.PlayableId);
            }
        }

        Authoring = new TimelineAuthoringSession(Source);
        View = new TimelineView(Source);
    }

    public AssetDescriptor Asset
    {
        get;
    }
    public string Path
    {
        get;
    }
    public SkeletalAnimationAsset? Animation
    {
        get;
        private set;
    }
    public AnimationStateMachineAsset? StateMachine
    {
        get;
    }
    public TimelineAuthoringSession? Authoring
    {
        get;
    }
    public TimelineView? View
    {
        get;
        set;
    }
    public bool Writable
    {
        get
        {
            return StateMachine is null && !Asset.ReadOnly && Asset.Source == AssetSource.Project;
        }
    }

    public JsonObject Describe()
    {
        var Snapshot = new JsonObject
        {
            ["assetId"] = Asset.Id,
            ["type"] = Asset.Type,
            ["source"] = Asset.Source.ToString(),
            ["readOnly"] = !Writable,
            ["documentRevision"] = Authoring?.DocumentRevision ?? 1,
            ["savepointRevision"] = Authoring?.SavepointRevision ?? 1,
            ["dirty"] = Authoring?.IsDirty ?? false,
        };
        if (StateMachine is not null)
        {
            Snapshot["stateCount"] = StateMachine.States.Count;
            Snapshot["parameterCount"] = StateMachine.Parameters.Count;
            Snapshot["transitionCount"] = StateMachine.Transitions.Count;
            Snapshot["clipIds"] = new JsonArray(StateMachine.ClipIds.Select(ClipId => (JsonNode)ClipId).ToArray());
            Snapshot["document"] = StateMachine.ToJson();
        }
        else
        {
            Snapshot["currentTime"] = View!.State.CurrentTime;
            Snapshot["boneCount"] = Animation?.Bones.Count ?? 0;
            Snapshot["document"] = Authoring!.Document.ToJson();
        }
        return Snapshot;
    }

    public void Save()
    {
        if (!Writable || Authoring is null)
        {
            throw new InvalidOperationException("This asset is read-only");
        }
        if (!string.Equals(SourceHash, HashSource(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Asset source changed outside this tool; reopen the Editor before saving");
        }

        if (Asset.Type.Equals("Timeline", StringComparison.OrdinalIgnoreCase))
        {
            Authoring.Save(Path);
        }
        else
        {
            var Notifies = Authoring.Document.Tracks.Where(Track =>
                Track.Type.Equals("notify", StringComparison.OrdinalIgnoreCase))
                .SelectMany(Track => Track.Clips).Select(Clip => new AnimationNotify(
                    Clip.ClipId, Clip.PlayableId, Clip.Start, Clip.Duration)).ToArray();
            var Replacement = Animation!.WithNotifies(Notifies);
            var Serialized = Replacement.Serialize(true) + Environment.NewLine;
            SkeletalAnimationAsset.Parse(Serialized, Path);
            var TemporaryPath = Path + $".tmp-{Guid.NewGuid():N}";
            try
            {
                File.WriteAllText(TemporaryPath, Serialized, new UTF8Encoding(false));
                File.Move(TemporaryPath, Path, true);
            }
            finally
            {
                if (File.Exists(TemporaryPath))
                {
                    File.Delete(TemporaryPath);
                }
            }
            Animation = Replacement;
            Authoring.MarkSavepoint();
        }
        SourceHash = HashSource();
    }

    private string HashSource() => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path)));
}
