using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SharpEngine.Assets;
using SharpEngine.Playables;

namespace SharpEngine.Timeline;

public sealed class TimelineDocument
{
    public const int CurrentSchemaVersion = 1;

    public TimelineDocument(string timelineId, string name, double duration, double frameRate,
        IReadOnlyList<TimelineTrack> tracks)
    {
        TimelineId = RequireId(timelineId, nameof(timelineId));
        Name = string.IsNullOrWhiteSpace(name) ? throw new ArgumentException("Timeline name is required", nameof(name)) : name.Trim();
        if (!double.IsFinite(duration) || duration <= 0) throw new ArgumentOutOfRangeException(nameof(duration));
        if (!double.IsFinite(frameRate) || frameRate <= 0) throw new ArgumentOutOfRangeException(nameof(frameRate));
        Duration = duration;
        FrameRate = frameRate;
        Tracks = tracks?.ToArray() ?? throw new ArgumentNullException(nameof(tracks));
        Validate();
    }

    public string TimelineId { get; }
    public string Name { get; }
    public double Duration { get; }
    public double FrameRate { get; }
    public IReadOnlyList<TimelineTrack> Tracks { get; }

    public string Serialize(bool indented = true) => ToJson().ToJsonString(new JsonSerializerOptions { WriteIndented = indented });

    public JsonObject ToJson() => new()
    {
        ["schemaVersion"] = CurrentSchemaVersion,
        ["kind"] = "timeline",
        ["timelineId"] = TimelineId,
        ["name"] = Name,
        ["duration"] = Duration,
        ["frameRate"] = FrameRate,
        ["tracks"] = new JsonArray(Tracks.Select(track => (JsonNode)track.ToJson()).ToArray()),
    };

    public static TimelineDocument Parse(string json, string source = "<memory>")
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            EnsureNoDuplicateProperties(document.RootElement);
            var root = JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException("Timeline root must be an object");
            Exact(root, "schemaVersion", "kind", "timelineId", "name", "duration", "frameRate", "tracks");
            if (root["schemaVersion"]!.GetValue<int>() != CurrentSchemaVersion || root["kind"]!.GetValue<string>() != "timeline")
                throw new InvalidDataException("Unsupported timeline schema");
            return new TimelineDocument(root["timelineId"]!.GetValue<string>(), root["name"]!.GetValue<string>(),
                root["duration"]!.GetValue<double>(), root["frameRate"]!.GetValue<double>(),
                root["tracks"]!.AsArray().Select(TimelineTrack.Parse).ToArray());
        }
        catch (InvalidDataException) { throw; }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or ArgumentException or OverflowException)
        {
            throw new InvalidDataException($"Invalid timeline '{source}': {exception.Message}", exception);
        }
    }

    private void Validate()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var track in Tracks)
        {
            if (!ids.Add(track.TrackId)) throw new InvalidDataException($"Duplicate timeline track '{track.TrackId}'");
            track.Validate(Duration);
        }
    }

    private static string RequireId(string value, string parameterName) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A stable ID is required", parameterName) : value.Trim();
    private static void Exact(JsonObject value, params string[] fields)
    {
        if (value.Count != fields.Length || fields.Any(field => !value.ContainsKey(field))) throw new InvalidDataException("Unexpected or missing fields in timeline asset");
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

public sealed class TimelineTrack
{
    public TimelineTrack(string trackId, string name, string targetId, string type,
        IReadOnlyList<TimelineClip> clips, bool muted = false)
    {
        TrackId = RequireId(trackId, nameof(trackId));
        Name = string.IsNullOrWhiteSpace(name) ? throw new ArgumentException("Track name is required", nameof(name)) : name.Trim();
        TargetId = RequireId(targetId, nameof(targetId));
        Type = string.IsNullOrWhiteSpace(type) ? throw new ArgumentException("Track type is required", nameof(type)) : type.Trim();
        Clips = clips?.ToArray() ?? throw new ArgumentNullException(nameof(clips));
        Muted = muted;
    }

    public string TrackId { get; }
    public string Name { get; }
    public string TargetId { get; }
    public string Type { get; }
    public bool Muted { get; }
    public IReadOnlyList<TimelineClip> Clips { get; }

    internal void Validate(double timelineDuration)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var sorted = Clips.OrderBy(clip => clip.Start).ThenBy(clip => clip.ClipId, StringComparer.Ordinal).ToArray();
        foreach (var clip in Clips)
        {
            if (!ids.Add(clip.ClipId)) throw new InvalidDataException($"Duplicate timeline clip '{clip.ClipId}'");
            clip.Validate(timelineDuration);
        }
        for (var index = 1; index < sorted.Length; index++)
            if (sorted[index - 1].End > sorted[index].Start + 1e-9)
                throw new InvalidDataException($"Timeline track '{TrackId}' contains overlapping clips");
    }

    internal JsonObject ToJson() => new()
    {
        ["trackId"] = TrackId,
        ["name"] = Name,
        ["targetId"] = TargetId,
        ["type"] = Type,
        ["muted"] = Muted,
        ["clips"] = new JsonArray(Clips.Select(clip => (JsonNode)clip.ToJson()).ToArray()),
    };

    internal static TimelineTrack Parse(JsonNode? node)
    {
        var value = node?.AsObject() ?? throw new InvalidDataException("Timeline track must be an object");
        Exact(value, "trackId", "name", "targetId", "type", "muted", "clips");
        return new TimelineTrack(value["trackId"]!.GetValue<string>(), value["name"]!.GetValue<string>(), value["targetId"]!.GetValue<string>(), value["type"]!.GetValue<string>(), value["clips"]!.AsArray().Select(TimelineClip.Parse).ToArray(), value["muted"]!.GetValue<bool>());
    }

    private static string RequireId(string value, string parameterName) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A stable ID is required", parameterName) : value.Trim();
    private static void Exact(JsonObject value, params string[] fields)
    {
        if (value.Count != fields.Length || fields.Any(field => !value.ContainsKey(field))) throw new InvalidDataException("Unexpected or missing timeline track fields");
    }
}

public sealed class TimelineClip
{
    public TimelineClip(string clipId, string playableId, double start, double duration,
        double sourceStart = 0, double speed = 1, PlayableLoopMode loopMode = PlayableLoopMode.Once)
    {
        ClipId = RequireId(clipId, nameof(clipId));
        PlayableId = RequireId(playableId, nameof(playableId));
        Start = start;
        Duration = duration;
        SourceStart = sourceStart;
        Speed = speed;
        LoopMode = loopMode;
    }

    public string ClipId { get; }
    public string PlayableId { get; }
    public double Start { get; }
    public double Duration { get; }
    public double SourceStart { get; }
    public double Speed { get; }
    public PlayableLoopMode LoopMode { get; }
    public double End => Start + Duration;

    internal void Validate(double timelineDuration)
    {
        if (!double.IsFinite(Start) || !double.IsFinite(Duration) || !double.IsFinite(SourceStart) || !double.IsFinite(Speed) || Start < 0 || Duration <= 0 || SourceStart < 0 || Speed <= 0 || End > timelineDuration + 1e-9)
            throw new InvalidDataException($"Timeline clip '{ClipId}' has invalid timing");
    }

    internal JsonObject ToJson() => new()
    {
        ["clipId"] = ClipId,
        ["playableId"] = PlayableId,
        ["start"] = Start,
        ["duration"] = Duration,
        ["sourceStart"] = SourceStart,
        ["speed"] = Speed,
        ["loopMode"] = LoopMode.ToString(),
    };

    internal static TimelineClip Parse(JsonNode? node)
    {
        var value = node?.AsObject() ?? throw new InvalidDataException("Timeline clip must be an object");
        Exact(value, "clipId", "playableId", "start", "duration", "sourceStart", "speed", "loopMode");
        if (!Enum.TryParse<PlayableLoopMode>(value["loopMode"]!.GetValue<string>(), out var loopMode)) throw new InvalidDataException("Unknown timeline loop mode");
        return new TimelineClip(value["clipId"]!.GetValue<string>(), value["playableId"]!.GetValue<string>(), value["start"]!.GetValue<double>(), value["duration"]!.GetValue<double>(), value["sourceStart"]!.GetValue<double>(), value["speed"]!.GetValue<double>(), loopMode);
    }

    private static string RequireId(string value, string parameterName) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A stable ID is required", parameterName) : value.Trim();
    private static void Exact(JsonObject value, params string[] fields)
    {
        if (value.Count != fields.Length || fields.Any(field => !value.ContainsKey(field))) throw new InvalidDataException("Unexpected or missing timeline clip fields");
    }
}

public interface IPlayableResolver
{
    IPlayable Resolve(string playableId);
}

/// <summary>A composite playable created from a generic TimelineDocument.</summary>
public sealed class TimelinePlayable : PlayableBase
{
    private readonly TimelineDocument _document;
    private readonly IReadOnlyDictionary<string, IPlayable> _playables;

    public TimelinePlayable(TimelineDocument document, IPlayableResolver resolver,
        string playableId = "timeline") : base(playableId, document?.Duration ?? throw new ArgumentNullException(nameof(document)))
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _document = document;
        var ids = _document.Tracks.SelectMany(track => track.Clips).Select(clip => clip.PlayableId).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal);
        _playables = ids.ToDictionary(id => id, resolver.Resolve, StringComparer.Ordinal);
    }

    public TimelineDocument Document => _document;

    public override void Evaluate(in PlayableEvaluationContext context, IPlayableOutputSink output)
    {
        foreach (var track in _document.Tracks)
        {
            if (track.Muted) continue;
            foreach (var clip in track.Clips)
            {
                if (context.Time < clip.Start || context.Time > clip.End) continue;
                var local = clip.SourceStart + (context.Time - clip.Start) * clip.Speed;
                var playable = _playables[clip.PlayableId];
                if (clip.LoopMode == PlayableLoopMode.Loop && playable.Duration > 0) local %= playable.Duration;
                playable.Seek(local);
                playable.Evaluate(new PlayableEvaluationContext(local, context.DeltaTime * clip.Speed, context.FrameIndex, context.IsSeeking), new TargetSink(output, track.TargetId));
            }
        }
    }

    private sealed class TargetSink(IPlayableOutputSink inner, string targetId) : IPlayableOutputSink
    {
        public void Emit(PlayableOutput output) => inner.Emit(output with { TargetId = targetId });
    }
}

public static class TimelineContent
{
    public static TimelineDocument Load(AssetRegistry registry, string id)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var descriptor = registry.Find(id) ?? throw new KeyNotFoundException($"Asset was not registered: '{id}'");
        if (!string.Equals(descriptor.Type, "Timeline", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Asset '{id}' is {descriptor.Type}, expected Timeline");
        var path = registry.ResolvePath(descriptor) ?? throw new InvalidDataException($"Asset '{id}' has no resolved path");
        return TimelineDocument.Parse(File.ReadAllText(path), path);
    }
}
