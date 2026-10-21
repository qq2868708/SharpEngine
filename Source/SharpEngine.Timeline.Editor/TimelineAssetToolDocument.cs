using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SharpEngine.Animation;
using SharpEngine.Assets;
using SharpEngine.Playables;
using SharpEngine.Editor;

namespace SharpEngine.Timeline;

/// <summary>One asset owns its authoring history; the view is transient and the pose is derived.</summary>
internal sealed class TimelineAssetToolDocument
{
    private string SourceHash;
    private JsonAssetAuthoringDocument? AnimationDocument;

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
            AnimationDocument = new JsonAssetAuthoringDocument(Asset, Path,
                Value => SkeletalAnimationAsset.Parse(Value.ToJsonString(), Path));
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
        private set;
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
            ["documentRevision"] = AnimationDocument?.Revision ?? Authoring?.DocumentRevision ?? 1,
            ["savepointRevision"] = AnimationDocument?.SavepointRevision ?? Authoring?.SavepointRevision ?? 1,
            ["dirty"] = IsDirty,
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
        if (AnimationDocument is not null)
        {
            Snapshot["rootMotion"] = new JsonObject
            {
                ["enabled"] = Animation!.RootMotion.Enabled,
                ["boneId"] = Animation.RootMotion.BoneId,
            };
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
            AnimationDocument!.Save();
            RefreshAnimation();
        }
        SourceHash = HashSource();
    }

    public bool IsDirty => AnimationDocument?.IsDirty ?? Authoring?.IsDirty ?? false;
    public bool CanUndo => AnimationDocument?.CanUndo ?? Authoring?.CanUndo ?? false;
    public bool CanRedo => AnimationDocument?.CanRedo ?? Authoring?.CanRedo ?? false;

    public bool SetRootMotion(bool Enabled)
    {
        if (!Writable || AnimationDocument is null)
        {
            return false;
        }
        var Changed = AnimationDocument.Edit("root-motion", Value =>
        {
            var Candidate = SkeletalAnimationAsset.Parse(Value.ToJsonString());
            var RootId = Candidate.RootMotion.BoneId;
            var Replacement = Candidate.WithRootMotion(new(Enabled, RootId)).ToJson();
            Value.Clear();
            foreach (var Pair in Replacement)
            {
                Value[Pair.Key] = Pair.Value?.DeepClone();
            }
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

    public bool ReplaceTimeline(TimelineDocument Replacement)
    {
        if (!Writable || Authoring is null)
        {
            return false;
        }
        if (AnimationDocument is null)
        {
            return Authoring.Replace(Replacement, "add-notify", Authoring.DocumentRevision).Success;
        }
        var Notifies = Replacement.Tracks.Where(Track => Track.Type == "notify")
            .SelectMany(Track => Track.Clips).Select(Clip => new AnimationNotify(
                Clip.ClipId, Clip.PlayableId, Clip.Start, Clip.Duration)).ToArray();
        var Changed = AnimationDocument.Edit("add-notify", Value =>
        {
            var Candidate = SkeletalAnimationAsset.Parse(Value.ToJsonString());
            var Json = Candidate.WithNotifies(Notifies).ToJson();
            Value.Clear();
            foreach (var Pair in Json)
            {
                Value[Pair.Key] = Pair.Value?.DeepClone();
            }
        }, out var Message);
        if (Changed)
        {
            RefreshAnimation();
        }
        return Changed;
    }

    public bool Undo()
    {
        if (!Writable)
        {
            return false;
        }
        if (AnimationDocument is null)
        {
            return Authoring!.Undo(Authoring.DocumentRevision).Success;
        }
        var Changed = AnimationDocument.Undo();
        if (Changed)
        {
            RefreshAnimation();
        }
        return Changed;
    }

    public bool Redo()
    {
        if (!Writable)
        {
            return false;
        }
        if (AnimationDocument is null)
        {
            return Authoring!.Redo(Authoring.DocumentRevision).Success;
        }
        var Changed = AnimationDocument.Redo();
        if (Changed)
        {
            RefreshAnimation();
        }
        return Changed;
    }

    private void RefreshAnimation()
    {
        Animation = SkeletalAnimationAsset.Parse(AnimationDocument!.Source, Path);
        var Previous = Authoring!.Document;
        var Tracks = Previous.Tracks.Select(Track => Track.Type == "notify"
            ? new TimelineTrack(Track.TrackId, Track.Name, Track.TargetId, Track.Type,
                Animation.Notifies.Select(Notify => new TimelineClip(Notify.NotifyId,
                    Notify.Name, Notify.Time, Notify.Duration)).ToArray(), Track.Muted)
            : Track).ToArray();
        Authoring = new TimelineAuthoringSession(new TimelineDocument(Previous.TimelineId,
            Previous.Name, Previous.Duration, Previous.FrameRate, Tracks));
        View = new TimelineView(Authoring.Document, View!.State);
    }

    private string HashSource() => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path)));
}
