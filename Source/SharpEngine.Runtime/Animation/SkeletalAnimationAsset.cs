using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharpEngine.Assets;
using SharpEngine.Playables;

namespace SharpEngine.Animation;

public readonly record struct BoneTransform(Vector3 Position, Quaternion Rotation, Vector3 Scale)
{
    public static BoneTransform Identity => new(Vector3.Zero, Quaternion.Identity, Vector3.One);
}

public sealed record SkeletonBone(
    string BoneId,
    string Name,
    string? ParentBoneId,
    BoneTransform BindTransform);

public sealed record BoneKeyframe(
    double Time,
    BoneTransform Transform);

public sealed record BoneTrack(
    string TrackId,
    string BoneId,
    IReadOnlyList<BoneKeyframe> Keyframes);

/// <summary>A derived pose sampled from a skeletal animation asset.</summary>
public sealed class SkeletalPose
{
    public SkeletalPose(double time, IReadOnlyList<(string BoneId, BoneTransform Transform)> bones)
    {
        Time = time;
        Bones = bones;
    }

    public double Time { get; }
    public IReadOnlyList<(string BoneId, BoneTransform Transform)> Bones { get; }

    public BoneTransform Get(string boneId) => Bones.First(item =>
        string.Equals(item.BoneId, boneId, StringComparison.Ordinal)).Transform;

    public JsonObject ToJson()
    {
        var bones = new JsonArray();
        foreach (var (boneId, transform) in Bones)
        {
            bones.Add(new JsonObject
            {
                ["boneId"] = boneId,
                ["position"] = PlayableJson.Vector3(transform.Position),
                ["rotation"] = PlayableJson.Quaternion(transform.Rotation),
                ["scale"] = PlayableJson.Vector3(transform.Scale),
            });
        }
        return new JsonObject
        {
            ["time"] = Time,
            ["bones"] = bones,
        };
    }
}

/// <summary>
/// Versioned, readable skeletal animation asset. The bone order and key order
/// are authoritative and are preserved during serialization. Optional FBX
/// source fields record provenance for import/export validation; they do not
/// require an export to preserve the source file's bytes.
/// </summary>
public sealed class SkeletalAnimationAsset
{
    public const int CurrentSchemaVersion = 1;

    public SkeletalAnimationAsset(string assetId, string name, double duration,
        float sampleRate, IReadOnlyList<SkeletonBone> bones,
        IReadOnlyList<BoneTrack> tracks, string? skeletonId = null)
        : this(assetId, name, duration, sampleRate, bones, tracks, skeletonId, null, null)
    {
    }

    public SkeletalAnimationAsset(string AssetId, string Name, double Duration,
        float SampleRate, IReadOnlyList<SkeletonBone> Bones,
        IReadOnlyList<BoneTrack> Tracks, string? SkeletonId,
        string? SourcePath, string? SourceSha256)
    {
        this.AssetId = RequireId(AssetId, nameof(AssetId));
        this.Name = string.IsNullOrWhiteSpace(Name) ? throw new ArgumentException("Animation name is required", nameof(Name)) : Name.Trim();
        if (!double.IsFinite(Duration) || Duration <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Duration));
        }
        if (!float.IsFinite(SampleRate) || SampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SampleRate));
        }
        this.Duration = Duration;
        this.SampleRate = SampleRate;
        this.SkeletonId = string.IsNullOrWhiteSpace(SkeletonId) ? null : SkeletonId.Trim();
        this.Bones = Bones?.ToArray() ?? throw new ArgumentNullException(nameof(Bones));
        this.Tracks = Tracks?.ToArray() ?? throw new ArgumentNullException(nameof(Tracks));
        this.SourcePath = string.IsNullOrWhiteSpace(SourcePath) ? null : SourcePath.Trim();
        this.SourceSha256 = string.IsNullOrWhiteSpace(SourceSha256) ? null : SourceSha256.Trim();
        Validate();
    }

    public string AssetId { get; }
    public string Name { get; }
    public double Duration { get; }
    public float SampleRate { get; }
    public string? SkeletonId { get; }
    public IReadOnlyList<SkeletonBone> Bones { get; }
    public IReadOnlyList<BoneTrack> Tracks { get; }
    /// <summary>Optional provenance path relative to the owning engine/project root.</summary>
    public string? SourcePath
    {
        get;
    }
    /// <summary>SHA-256 of the imported source bytes, independent of export bytes.</summary>
    public string? SourceSha256
    {
        get;
    }

    /// <summary>Export an FBX that is semantically equivalent to this animation.</summary>
    public string ExportFbx(AssetRegistry Registry, string Destination) =>
        FbxSourceContract.ExportFbx(Registry, AssetId, Destination);

    /// <summary>Compatibility source copy; does not include current authored asset changes.</summary>
    [Obsolete("Source copy only; use ExportFbx for semantic export. Canonical asset reconstruction requires an FBX writer.")]
    public string ExportOriginalFbx(AssetRegistry Registry, string Destination) =>
        FbxSourceContract.ExportOriginalFbx(Registry, AssetId, Destination);

    public SkeletalPose Sample(double time)
    {
        if (!double.IsFinite(time)) throw new ArgumentOutOfRangeException(nameof(time));
        var sampleTime = Math.Clamp(time, 0, Duration);
        var byBone = Tracks.ToDictionary(track => track.BoneId, track => SampleTrack(track, sampleTime), StringComparer.Ordinal);
        var result = new List<(string BoneId, BoneTransform Transform)>(Bones.Count);
        foreach (var bone in Bones)
            result.Add((bone.BoneId, byBone.TryGetValue(bone.BoneId, out var value) ? value : bone.BindTransform));
        return new SkeletalPose(sampleTime, result);
    }

    public SkeletalPose SampleLooped(double time)
    {
        if (!double.IsFinite(time)) throw new ArgumentOutOfRangeException(nameof(time));
        var wrapped = time % Duration;
        if (wrapped < 0) wrapped += Duration;
        return Sample(wrapped);
    }

    public string Serialize(bool indented = true) => ToJson().ToJsonString(new JsonSerializerOptions
    {
        WriteIndented = indented,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict,
    });

    public JsonObject ToJson()
    {
        var bones = new JsonArray();
        foreach (var bone in Bones)
        {
            bones.Add(new JsonObject
            {
                ["boneId"] = bone.BoneId,
                ["name"] = bone.Name,
                ["parentBoneId"] = bone.ParentBoneId,
                ["bindPosition"] = PlayableJson.Vector3(bone.BindTransform.Position),
                ["bindRotation"] = PlayableJson.Quaternion(bone.BindTransform.Rotation),
                ["bindScale"] = PlayableJson.Vector3(bone.BindTransform.Scale),
            });
        }

        var tracks = new JsonArray();
        foreach (var track in Tracks)
        {
            var keys = new JsonArray();
            foreach (var key in track.Keyframes)
            {
                keys.Add(new JsonObject
                {
                    ["time"] = key.Time,
                    ["position"] = PlayableJson.Vector3(key.Transform.Position),
                    ["rotation"] = PlayableJson.Quaternion(key.Transform.Rotation),
                    ["scale"] = PlayableJson.Vector3(key.Transform.Scale),
                });
            }
            tracks.Add(new JsonObject
            {
                ["trackId"] = track.TrackId,
                ["boneId"] = track.BoneId,
                ["keyframes"] = keys,
            });
        }

        var Result = new JsonObject
        {
            ["schemaVersion"] = CurrentSchemaVersion,
            ["kind"] = "skeletal-animation",
            ["assetId"] = AssetId,
            ["name"] = Name,
            ["duration"] = Duration,
            ["sampleRate"] = SampleRate,
            ["skeletonId"] = SkeletonId,
            ["bones"] = bones,
            ["tracks"] = tracks,
        };
        if (SourcePath is not null)
        {
            Result["sourcePath"] = SourcePath;
        }
        if (SourceSha256 is not null)
        {
            Result["sourceSha256"] = SourceSha256;
        }
        return Result;
    }

    public static SkeletalAnimationAsset Parse(string json, string source = "<memory>")
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            EnsureNoDuplicateProperties(document.RootElement);
            var root = JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException("Animation root must be an object");
            ExactWithOptional(root,
                ["schemaVersion", "kind", "assetId", "name", "duration", "sampleRate", "skeletonId", "bones", "tracks"],
                ["sourcePath", "sourceSha256"]);
            if (root["schemaVersion"]!.GetValue<int>() != CurrentSchemaVersion ||
                root["kind"]!.GetValue<string>() != "skeletal-animation")
                throw new InvalidDataException("Unsupported skeletal animation schema");

            var bones = root["bones"]!.AsArray().Select(ParseBone).ToArray();
            var tracks = root["tracks"]!.AsArray().Select(ParseTrack).ToArray();
            return new SkeletalAnimationAsset(root["assetId"]!.GetValue<string>(),
                root["name"]!.GetValue<string>(), root["duration"]!.GetValue<double>(),
                root["sampleRate"]!.GetValue<float>(), bones, tracks,
                root["skeletonId"]?.GetValue<string>(), OptionalString(root, "sourcePath"),
                OptionalString(root, "sourceSha256"));
        }
        catch (InvalidDataException) { throw; }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or ArgumentException or OverflowException)
        {
            throw new InvalidDataException($"Invalid skeletal animation '{source}': {exception.Message}", exception);
        }
    }

    private void Validate()
    {
        SkeletonAsset.ValidateBones(Bones);
        var boneIds = new HashSet<string>(Bones.Select(bone => bone.BoneId), StringComparer.Ordinal);

        var trackIds = new HashSet<string>(StringComparer.Ordinal);
        var trackBones = new HashSet<string>(StringComparer.Ordinal);
        foreach (var track in Tracks)
        {
            if (string.IsNullOrWhiteSpace(track.TrackId) || !trackIds.Add(track.TrackId)) throw new InvalidDataException("Track IDs must be unique and non-empty");
            if (!boneIds.Contains(track.BoneId) || !trackBones.Add(track.BoneId)) throw new InvalidDataException($"Track '{track.TrackId}' references a missing or duplicate bone");
            if (track.Keyframes.Count == 0) throw new InvalidDataException($"Track '{track.TrackId}' requires keyframes");
            var previous = -1d;
            foreach (var key in track.Keyframes)
            {
                if (!double.IsFinite(key.Time) || key.Time < 0 || key.Time > Duration || key.Time <= previous) throw new InvalidDataException($"Track '{track.TrackId}' keyframe times must increase within the clip");
                ValidateTransform(key.Transform, $"track '{track.TrackId}' keyframe");
                previous = key.Time;
            }
        }
        SourceAssetContract.ValidateMetadata(SourcePath, SourceSha256, AssetId);
    }

    private static BoneTransform SampleTrack(BoneTrack track, double time)
    {
        if (track.Keyframes.Count == 1 || time <= track.Keyframes[0].Time) return track.Keyframes[0].Transform;
        if (time >= track.Keyframes[^1].Time) return track.Keyframes[^1].Transform;
        for (var index = 1; index < track.Keyframes.Count; index++)
        {
            var right = track.Keyframes[index];
            if (time > right.Time) continue;
            var left = track.Keyframes[index - 1];
            var amount = (float)((time - left.Time) / (right.Time - left.Time));
            return new BoneTransform(Vector3.Lerp(left.Transform.Position, right.Transform.Position, amount),
                Quaternion.Slerp(left.Transform.Rotation, right.Transform.Rotation, amount),
                Vector3.Lerp(left.Transform.Scale, right.Transform.Scale, amount));
        }
        return track.Keyframes[^1].Transform;
    }

    private static void ValidateTransform(BoneTransform transform, string path)
    {
        if (!IsFinite(transform.Position) || !IsFinite(transform.Rotation) || !IsFinite(transform.Scale) ||
            transform.Scale.X <= 0 || transform.Scale.Y <= 0 || transform.Scale.Z <= 0 ||
            transform.Rotation.LengthSquared() < 0.999f || transform.Rotation.LengthSquared() > 1.001f)
            throw new InvalidDataException($"{path} contains an invalid transform");
    }

    private static bool IsFinite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static bool IsFinite(Quaternion value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);
    private static string RequireId(string value, string parameterName) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A stable ID is required", parameterName) : value.Trim();

    private static SkeletonBone ParseBone(JsonNode? node)
    {
        var value = node?.AsObject() ?? throw new InvalidDataException("Bone must be an object");
        PlayableJson.Exact(value, "boneId", "name", "parentBoneId", "bindPosition", "bindRotation", "bindScale");
        return new SkeletonBone(value["boneId"]!.GetValue<string>(), value["name"]!.GetValue<string>(), value["parentBoneId"]?.GetValue<string>(),
            new BoneTransform(ParseVector3(value["bindPosition"], "bindPosition"), ParseQuaternion(value["bindRotation"], "bindRotation"), ParseVector3(value["bindScale"], "bindScale")));
    }

    private static BoneTrack ParseTrack(JsonNode? node)
    {
        var value = node?.AsObject() ?? throw new InvalidDataException("Track must be an object");
        PlayableJson.Exact(value, "trackId", "boneId", "keyframes");
        return new BoneTrack(value["trackId"]!.GetValue<string>(), value["boneId"]!.GetValue<string>(), value["keyframes"]!.AsArray().Select(ParseKeyframe).ToArray());
    }

    private static BoneKeyframe ParseKeyframe(JsonNode? node)
    {
        var value = node?.AsObject() ?? throw new InvalidDataException("Keyframe must be an object");
        PlayableJson.Exact(value, "time", "position", "rotation", "scale");
        return new BoneKeyframe(value["time"]!.GetValue<double>(), new BoneTransform(ParseVector3(value["position"], "position"), ParseQuaternion(value["rotation"], "rotation"), ParseVector3(value["scale"], "scale")));
    }

    private static string? OptionalString(JsonObject Root, string Name) =>
        Root[Name] is null || Root[Name]!.GetValueKind() == JsonValueKind.Null
            ? null : Root[Name]!.GetValue<string>();

    private static void ExactWithOptional(JsonObject Value, IReadOnlyList<string> Required,
        IReadOnlyList<string> Optional)
    {
        if (Required.Any(Field => !Value.ContainsKey(Field)) ||
            Value.Select(Property => Property.Key).Any(Field =>
                !Required.Contains(Field, StringComparer.Ordinal) &&
                !Optional.Contains(Field, StringComparer.Ordinal)))
        {
            throw new InvalidDataException("Unexpected or missing fields in skeletal animation asset");
        }
    }

    private static Vector3 ParseVector3(JsonNode? node, string path)
    {
        var values = node?.AsArray() ?? throw new InvalidDataException($"{path} must be a vector");
        if (values.Count != 3) throw new InvalidDataException($"{path} must have three values");
        return new(PlayableJson.Number(values[0], path), PlayableJson.Number(values[1], path), PlayableJson.Number(values[2], path));
    }

    private static Quaternion ParseQuaternion(JsonNode? node, string path)
    {
        var values = node?.AsArray() ?? throw new InvalidDataException($"{path} must be a quaternion");
        if (values.Count != 4) throw new InvalidDataException($"{path} must have four values");
        return new(PlayableJson.Number(values[0], path), PlayableJson.Number(values[1], path), PlayableJson.Number(values[2], path), PlayableJson.Number(values[3], path));
    }

    private static void EnsureNoDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException($"Duplicate JSON field '{property.Name}'");
                EnsureNoDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) EnsureNoDuplicateProperties(item);
    }
}

/// <summary>Playable adapter that emits a complete skeletal pose per sample.</summary>
public sealed class SkeletalAnimationPlayable : PlayableBase
{
    public SkeletalAnimationPlayable(string playableId, SkeletalAnimationAsset asset,
        string targetId, PlayableLoopMode loopMode = PlayableLoopMode.Loop)
        : base(playableId, asset?.Duration ?? throw new ArgumentNullException(nameof(asset)), loopMode)
    {
        Asset = asset;
        TargetId = string.IsNullOrWhiteSpace(targetId) ? throw new ArgumentException("Target ID is required", nameof(targetId)) : targetId.Trim();
    }

    public SkeletalAnimationAsset Asset { get; }
    public string TargetId { get; }
    public event Action<SkeletalPose>? PoseEvaluated;

    public SkeletalPose SamplePose(double Time)
    {
        return LoopMode == PlayableLoopMode.Loop ? Asset.SampleLooped(Time) : Asset.Sample(Time);
    }

    public override void Evaluate(in PlayableEvaluationContext Context, IPlayableOutputSink Output)
    {
        var Pose = SamplePose(Context.Time);
        PoseEvaluated?.Invoke(Pose);
        Output.Emit(new PlayableOutput(PlayableId, TargetId, "skeleton.pose", Pose.ToJson()));
    }
}

public static class SkeletalAnimationContent
{
    public static SkeletalAnimationAsset Load(AssetRegistry Registry, string Id)
    {
        ArgumentNullException.ThrowIfNull(Registry);
        var Descriptor = Registry.Find(Id) ?? throw new KeyNotFoundException($"Asset was not registered: '{Id}'");
        if (!string.Equals(Descriptor.Type, "SkeletalAnimation", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Asset '{Id}' is {Descriptor.Type}, expected SkeletalAnimation");
        }
        var Path = Registry.ResolvePath(Descriptor) ?? throw new InvalidDataException($"Asset '{Id}' has no resolved path");
        var Animation = SkeletalAnimationAsset.Parse(File.ReadAllText(Path), Path);
        if (!string.Equals(Animation.AssetId, Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Animation asset ID '{Animation.AssetId}' does not match '{Id}'.");
        }
        if (Animation.SourcePath is not null)
        {
            SourceAssetContract.ResolveAndValidate(Registry, Descriptor, Animation.SourcePath,
                Animation.SourceSha256!);
        }
        return Animation;
    }
}
