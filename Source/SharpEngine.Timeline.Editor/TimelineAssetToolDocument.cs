using System.Security.Cryptography;
using System.Text.Json.Nodes;
using SharpEngine.Animation;
using SharpEngine.Assets;

namespace SharpEngine.Timeline;

/// <summary>One Timeline asset owns its authoring history and transient view.</summary>
internal sealed class TimelineAssetToolDocument
{
    private string SourceHash;

    public TimelineAssetToolDocument(AssetRegistry Assets, AssetDescriptor Asset, string Path)
    {
        this.Asset = Asset;
        this.Path = Path;
        SourceHash = HashSource();
        var Source = TimelineContent.Load(Assets, Asset.Id);
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
    }
    public TimelineAuthoringSession Authoring
    {
        get;
    }
    public TimelineView View
    {
        get;
        set;
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
            ["documentRevision"] = Authoring.DocumentRevision,
            ["savepointRevision"] = Authoring.SavepointRevision,
            ["dirty"] = IsDirty,
            ["currentTime"] = View.State.CurrentTime,
            ["boneCount"] = Animation?.Bones.Count ?? 0,
            ["document"] = Authoring.Document.ToJson(),
        };
    }

    public void Save()
    {
        if (!Writable)
        {
            throw new InvalidOperationException("This asset is read-only");
        }
        if (!string.Equals(SourceHash, HashSource(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Asset source changed outside this tool; reopen the Editor before saving");
        }
        Authoring.Save(Path);
        SourceHash = HashSource();
    }

    public bool AddNotify(out string Message)
    {
        Message = string.Empty;
        if (!Writable)
        {
            return false;
        }
        var Prepared = TimelineNotifyEditing.TryAdd(Authoring.Document, out var Replacement, out var ClipId, out Message);
        if (!Prepared)
        {
            return false;
        }
        var Changed = Authoring.Replace(Replacement, "add-notify", Authoring.DocumentRevision).Success;
        if (!Changed)
        {
            Message = "Notify edit was rejected";
            return false;
        }
        View = new TimelineView(Authoring.Document, View.State);
        View.SelectClip(ClipId);
        return true;
    }

    public bool Undo()
    {
        return Writable && Authoring.Undo(Authoring.DocumentRevision).Success;
    }

    public bool Redo()
    {
        return Writable && Authoring.Redo(Authoring.DocumentRevision).Success;
    }

    private string HashSource() { return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path))); }
}
