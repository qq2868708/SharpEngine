using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpEngine.Playables;

/// <summary>The lifecycle of a playable node. The state is runtime-only.</summary>
public enum PlayableState : byte
{
    Stopped,
    Playing,
    Paused,
    Completed,
}

public enum PlayableLoopMode : byte
{
    Once,
    Loop,
}

/// <summary>Immutable input passed to a node while a graph is evaluated.</summary>
public readonly record struct PlayableEvaluationContext(
    double Time,
    double DeltaTime,
    long FrameIndex,
    bool IsSeeking = false);

/// <summary>A renderer-neutral value emitted by a playable node.</summary>
public sealed record PlayableOutput(
    string PlayableId,
    string TargetId,
    string PropertyPath,
    JsonNode? Value);

public interface IPlayableOutputSink
{
    void Emit(PlayableOutput output);
}

public sealed class ListPlayableOutputSink : IPlayableOutputSink
{
    private readonly List<PlayableOutput> _outputs = [];
    public IReadOnlyList<PlayableOutput> Outputs => _outputs;
    public void Emit(PlayableOutput output) => _outputs.Add(output);
    public void Clear() => _outputs.Clear();
}

/// <summary>
/// The low-level contract used by timelines. Implementations own no native
/// handles; they evaluate their authoritative data into stable output paths.
/// </summary>
public interface IPlayable
{
    string PlayableId { get; }
    double Duration { get; }
    double Time { get; }
    PlayableLoopMode LoopMode { get; }
    PlayableState State { get; }

    void Play();
    void Pause();
    void Stop();
    void Seek(double time);
    void Advance(double deltaTime);
    void Evaluate(in PlayableEvaluationContext context, IPlayableOutputSink output);
}

/// <summary>Common time/state machine for playable nodes.</summary>
public abstract class PlayableBase : IPlayable
{
    protected PlayableBase(string playableId, double duration,
        PlayableLoopMode loopMode = PlayableLoopMode.Once)
    {
        PlayableId = RequireId(playableId, nameof(playableId));
        if (!double.IsFinite(duration) || duration < 0) throw new ArgumentOutOfRangeException(nameof(duration));
        Duration = duration;
        LoopMode = loopMode;
    }

    public string PlayableId { get; }
    public double Duration { get; }
    public double Time { get; private set; }
    public PlayableLoopMode LoopMode { get; }
    public PlayableState State { get; private set; } = PlayableState.Stopped;

    public virtual void Play()
    {
        if (State == PlayableState.Completed && LoopMode == PlayableLoopMode.Once)
            Time = 0;
        State = PlayableState.Playing;
    }

    public virtual void Pause()
    {
        if (State == PlayableState.Playing) State = PlayableState.Paused;
    }

    public virtual void Stop()
    {
        Time = 0;
        State = PlayableState.Stopped;
    }

    public virtual void Seek(double time)
    {
        if (!double.IsFinite(time)) throw new ArgumentOutOfRangeException(nameof(time));
        var wasPlaying = State == PlayableState.Playing;
        Time = ClampTime(time);
        State = Time >= Duration && Duration > 0 && LoopMode == PlayableLoopMode.Once
            ? PlayableState.Completed
            : wasPlaying ? PlayableState.Playing : PlayableState.Paused;
    }

    public virtual void Advance(double deltaTime)
    {
        if (!double.IsFinite(deltaTime) || deltaTime < 0) throw new ArgumentOutOfRangeException(nameof(deltaTime));
        if (State != PlayableState.Playing || deltaTime == 0) return;
        if (Duration <= 0)
        {
            State = LoopMode == PlayableLoopMode.Loop ? PlayableState.Playing : PlayableState.Completed;
            return;
        }

        var next = Time + deltaTime;
        if (LoopMode == PlayableLoopMode.Loop)
        {
            Time = next % Duration;
            return;
        }

        Time = Math.Min(next, Duration);
        if (Time >= Duration) State = PlayableState.Completed;
    }

    public abstract void Evaluate(in PlayableEvaluationContext context, IPlayableOutputSink output);

    protected static string RequireId(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A stable ID is required", parameterName) : value.Trim();

    protected double ClampTime(double Value)
    {
        if (LoopMode == PlayableLoopMode.Loop && Duration > 0)
        {
            var Wrapped = Value % Duration;
            return Wrapped < 0 ? Wrapped + Duration : Wrapped;
        }
        return Math.Clamp(Value, 0, Duration);
    }
}

/// <summary>
/// Runtime coordinator for independent playables. It owns only transient
/// playback state; the graph can always be reconstructed from serialized data.
/// </summary>
public sealed class PlayableGraph
{
    private readonly List<IPlayable> _playables = [];
    private long _frameIndex;

    public IReadOnlyList<IPlayable> Playables => _playables;
    public double Time { get; private set; }
    public PlayableState State { get; private set; } = PlayableState.Stopped;
    public long FrameIndex => _frameIndex;
    public double Duration => _playables.Count == 0 ? 0 : _playables.Max(playable => playable.Duration);

    public void Add(IPlayable playable)
    {
        ArgumentNullException.ThrowIfNull(playable);
        if (_playables.Any(item => string.Equals(item.PlayableId, playable.PlayableId, StringComparison.Ordinal)))
            throw new InvalidOperationException($"Playable '{playable.PlayableId}' is already in the graph");
        _playables.Add(playable);
        _playables.Sort((left, right) => StringComparer.Ordinal.Compare(left.PlayableId, right.PlayableId));
    }

    public bool Remove(string playableId) => _playables.RemoveAll(item =>
        string.Equals(item.PlayableId, playableId, StringComparison.Ordinal)) > 0;

    public void Play()
    {
        foreach (var playable in _playables) playable.Play();
        State = PlayableState.Playing;
    }

    public void Pause()
    {
        foreach (var playable in _playables) playable.Pause();
        if (State == PlayableState.Playing) State = PlayableState.Paused;
    }

    public void Stop()
    {
        foreach (var playable in _playables) playable.Stop();
        Time = 0;
        State = PlayableState.Stopped;
        _frameIndex = 0;
    }

    public void Seek(double time)
    {
        if (!double.IsFinite(time) || time < 0) throw new ArgumentOutOfRangeException(nameof(time));
        Time = Math.Clamp(time, 0, Duration);
        foreach (var playable in _playables) playable.Seek(Time);
        State = Time >= Duration && Duration > 0 ? PlayableState.Completed : PlayableState.Paused;
    }

    public IReadOnlyList<PlayableOutput> Evaluate(double deltaTime)
    {
        if (!double.IsFinite(deltaTime) || deltaTime < 0) throw new ArgumentOutOfRangeException(nameof(deltaTime));
        if (State == PlayableState.Playing)
        {
            Time = Duration <= 0 ? 0 : Math.Min(Time + deltaTime, Duration);
            foreach (var playable in _playables) playable.Advance(deltaTime);
            if (Duration > 0 && Time >= Duration && _playables.All(item => item.State == PlayableState.Completed))
                State = PlayableState.Completed;
        }

        var sink = new ListPlayableOutputSink();
        var context = new PlayableEvaluationContext(Time, deltaTime, ++_frameIndex);
        foreach (var playable in _playables) playable.Evaluate(context, sink);
        return sink.Outputs;
    }

    public IReadOnlyList<PlayableOutput> EvaluateAt(double time)
    {
        if (!double.IsFinite(time) || time < 0) throw new ArgumentOutOfRangeException(nameof(time));
        Seek(time);
        var sink = new ListPlayableOutputSink();
        var context = new PlayableEvaluationContext(Time, 0, ++_frameIndex, true);
        foreach (var playable in _playables) playable.Evaluate(context, sink);
        return sink.Outputs;
    }
}

internal static class PlayableJson
{
    public static JsonArray Vector3(System.Numerics.Vector3 value) =>
        new(value.X, value.Y, value.Z);

    public static JsonArray Quaternion(System.Numerics.Quaternion value) =>
        new(value.X, value.Y, value.Z, value.W);

    public static float Number(JsonNode? value, string path)
    {
        var number = value?.GetValue<float>() ?? throw new InvalidDataException($"{path} is required");
        if (!float.IsFinite(number)) throw new InvalidDataException($"{path} must be finite");
        return number;
    }

    public static void Exact(JsonObject value, params string[] fields)
    {
        if (value.Count != fields.Length || fields.Any(field => !value.ContainsKey(field)))
            throw new InvalidDataException("Unexpected or missing fields in playable asset");
    }
}
