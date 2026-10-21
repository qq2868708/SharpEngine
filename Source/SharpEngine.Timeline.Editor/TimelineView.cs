using SharpEngine.UI;
using System.Text.Json.Nodes;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpEngine.Timeline;

/// <summary>Transient timeline state. It is intentionally separate from the timeline asset.</summary>
public sealed class TimelineViewState
{
    public double PixelsPerSecond
    {
        get;
        set;
    } = 100;
    public double HorizontalScroll
    {
        get;
        set;
    }
    public double VerticalScroll
    {
        get;
        set;
    }
    public double CurrentTime
    {
        get;
        internal set;
    }
    public string? SelectedTrackId
    {
        get;
        set;
    }
    public string? SelectedClipId
    {
        get;
        set;
    }

    public bool IsPlaying
    {
        get;
        internal set;
    }

    internal void Validate()
    {
        if (!double.IsFinite(PixelsPerSecond) || PixelsPerSecond < 10 || PixelsPerSecond > 2000)
        {
            throw new ArgumentOutOfRangeException(nameof(PixelsPerSecond));
        }
        if (!double.IsFinite(HorizontalScroll) || HorizontalScroll < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(HorizontalScroll));
        }
        if (!double.IsFinite(VerticalScroll) || VerticalScroll < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(VerticalScroll));
        }
        if (!double.IsFinite(CurrentTime) || CurrentTime < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(CurrentTime));
        }
    }
}

public readonly record struct TimelineViewRect(float X, float Y, float Width, float Height);
public sealed record TimelineRulerTick(double Time, float X, string Label, bool Major);
public sealed record TimelineViewClip(string ClipId, string TrackId, string Label, TimelineViewRect Bounds, bool Selected, bool Muted);
public sealed record TimelineViewRow(string TrackId, string Label, float Y, float Height, bool Selected, bool Muted, IReadOnlyList<TimelineViewClip> Clips);

public sealed class TimelineViewSnapshot
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal TimelineViewSnapshot(string TimelineId, float Width, float Height, float HeaderWidth,
        double CurrentTime, float PlayheadX, IReadOnlyList<TimelineRulerTick> Ruler,
        IReadOnlyList<TimelineViewRow> Rows)
    {
        this.TimelineId = TimelineId;
        this.Width = Width;
        this.Height = Height;
        this.HeaderWidth = HeaderWidth;
        this.CurrentTime = CurrentTime;
        this.PlayheadX = PlayheadX;
        this.Ruler = Ruler;
        this.Rows = Rows;
    }

    public string TimelineId
    {
        get;
    }
    public float Width
    {
        get;
    }
    public float Height
    {
        get;
    }
    public float HeaderWidth
    {
        get;
    }
    public double CurrentTime
    {
        get;
    }
    public float PlayheadX
    {
        get;
    }
    public IReadOnlyList<TimelineRulerTick> Ruler
    {
        get;
    }
    public IReadOnlyList<TimelineViewRow> Rows
    {
        get;
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
}

/// <summary>
/// Renderer-independent timeline view. It produces stable rows, clip rectangles,
/// ruler ticks, and markup that can be hosted in RmlUi or another frontend.
/// </summary>
public sealed class TimelineView : IUiLayoutPart, IUiActionHandler
{
    private static readonly UiLayoutTemplate Layout = UiLayoutTemplate.LoadEmbedded(typeof(TimelineView).Assembly, "SharpEngine.Timeline.TimelineView.rml");
    public const float DefaultHeaderWidth = 180;
    public const float DefaultRowHeight = 30;
    public const float RulerHeight = 28;

    public TimelineView(TimelineDocument Document, TimelineViewState? State = null)
    {
        this.Document = Document ?? throw new ArgumentNullException(nameof(Document));
        this.State = State ?? new TimelineViewState();
        this.State.Validate();
    }

    public TimelineDocument Document
    {
        get;
    }
    public TimelineViewState State
    {
        get;
    }
    public ulong StateRevision
    {
        get;
        private set;
    } = 1;

    public TimelineViewSnapshot BuildSnapshot(float Width, float Height, float HeaderWidth = DefaultHeaderWidth)
    {
        if (!float.IsFinite(Width) || !float.IsFinite(Height) || Width <= 0 || Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Width));
        }
        if (!float.IsFinite(HeaderWidth) || HeaderWidth < 0 || HeaderWidth >= Width)
        {
            throw new ArgumentOutOfRangeException(nameof(HeaderWidth));
        }
        State.Validate();
        var CurrentTime = Math.Clamp(State.CurrentTime, 0, Document.Duration);
        var TimelineWidth = Width - HeaderWidth;
        var ViewStart = State.HorizontalScroll / State.PixelsPerSecond;
        var ViewEnd = ViewStart + TimelineWidth / (float)State.PixelsPerSecond;
        var Ticks = BuildTicks(ViewStart, ViewEnd, HeaderWidth, State.PixelsPerSecond);
        var Rows = new List<TimelineViewRow>();
        var Y = RulerHeight - (float)State.VerticalScroll;
        foreach (var Track in Document.Tracks)
        {
            var Clips = Track.Clips.Select(Clip => new TimelineViewClip(
                Clip.ClipId,
                Track.TrackId,
                Clip.PlayableId,
                new TimelineViewRect(
                    HeaderWidth + (float)((Clip.Start - ViewStart) * State.PixelsPerSecond),
                    Y,
                    Math.Max(1, (float)(Clip.Duration * State.PixelsPerSecond)),
                    DefaultRowHeight),
                string.Equals(State.SelectedClipId, Clip.ClipId, StringComparison.Ordinal),
                Track.Muted)).ToArray();
            Rows.Add(new TimelineViewRow(Track.TrackId, Track.Name, Y, DefaultRowHeight,
                string.Equals(State.SelectedTrackId, Track.TrackId, StringComparison.Ordinal), Track.Muted, Clips));
            Y += DefaultRowHeight;
        }
        return new TimelineViewSnapshot(Document.TimelineId, Width, Height, HeaderWidth, CurrentTime,
            HeaderWidth + (float)((CurrentTime - ViewStart) * State.PixelsPerSecond), Ticks, Rows);
    }

    public string BuildLayout(UiLayoutContext Context) { return BuildMarkup(Context.Width, Context.Height); }

    public string BuildMarkup(float Width, float Height, float HeaderWidth = DefaultHeaderWidth)
    {
        var Snapshot = BuildSnapshot(Width, Height, HeaderWidth);
        var Values = new JsonObject
        {
            ["TimelineId"] = Snapshot.TimelineId,
            ["HeaderWidth"] = Number(HeaderWidth),
            ["Scale"] = Number(State.PixelsPerSecond),
            ["Scroll"] = Number(State.HorizontalScroll),
            ["Duration"] = Number(Document.Duration),
            ["Playing"] = IsPlaying,
            ["Paused"] = !IsPlaying,
            ["DisplayTime"] = FormatTime(State.CurrentTime),
            ["DisplayDuration"] = FormatTime(Document.Duration),
            ["HasPlayhead"] = Snapshot.PlayheadX >= HeaderWidth && Snapshot.PlayheadX <= Width,
            ["PlayheadX"] = Number(Snapshot.PlayheadX),
            ["CurrentTime"] = Number(Snapshot.CurrentTime),
            ["Ticks"] = new JsonArray(Snapshot.Ruler.Select(Tick =>
                (JsonNode)new JsonObject
                {
                    ["Classes"] = Tick.Major ? " major" : string.Empty,
                    ["Left"] = Number(Tick.X - HeaderWidth),
                    ["Label"] = Tick.Major ? Tick.Label : string.Empty,
                }).ToArray()),
            ["Rows"] = new JsonArray(Snapshot.Rows.Select(Row =>
                (JsonNode)new JsonObject
                {
                    ["Classes"] = (Row.Selected ? " selected" : string.Empty) + (Row.Muted ? " muted" : string.Empty),
                    ["Top"] = Number(Row.Y - RulerHeight),
                    ["Height"] = Number(Row.Height),
                    ["TrackId"] = Row.TrackId,
                    ["Label"] = Row.Label,
                    ["Clips"] = new JsonArray(Row.Clips.Select(Clip =>
                        (JsonNode)new JsonObject
                        {
                            ["Classes"] = (Clip.Selected ? " selected" : string.Empty) + (Clip.Muted ? " muted" : string.Empty),
                            ["Left"] = Number(Clip.Bounds.X - HeaderWidth),
                            ["Width"] = Number(Clip.Bounds.Width),
                            ["Height"] = Number(Clip.Bounds.Height),
                            ["ClipId"] = Clip.ClipId,
                            ["Label"] = Clip.Label,
                        }).ToArray()),
                }).ToArray()),
        };
        return Layout.Render(new(Width, Height), Values);
    }

    public bool IsPlaying
    {
        get
        {
            return State.IsPlaying;
        }
    }

    public event Action<TimelineTimeChangedEvent>? TimeChanged;
    public event Action<TimelinePlaybackChangedEvent>? PlaybackChanged;

    public void Play()
    {
        if (State.CurrentTime >= Document.Duration)
        {
            SetTime(0, TimelineTimeChangeSource.Seek);
        }
        SetPlaying(Document.Duration > 0);
    }

    public void Pause() { SetPlaying(false); }

    public void Stop()
    {
        Pause();
        SetTime(0, TimelineTimeChangeSource.Stop);
    }

    public void Seek(double Time)
    {
        if (!double.IsFinite(Time))
        {
            throw new ArgumentOutOfRangeException(nameof(Time));
        }
        Pause();
        SetTime(Time, TimelineTimeChangeSource.Seek);
    }

    public void Update(double DeltaSeconds)
    {
        if (!double.IsFinite(DeltaSeconds) || DeltaSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(DeltaSeconds));
        }
        if (!IsPlaying || DeltaSeconds == 0)
        {
            return;
        }
        SetTime(Math.Min(Document.Duration, State.CurrentTime + DeltaSeconds), TimelineTimeChangeSource.Playback);
        if (State.CurrentTime >= Document.Duration)
        {
            Pause();
        }
    }

    public bool TryHandleAction(string Action)
    {
        switch (Action)
        {
            case "editor.timeline-play":
                Play();
                return true;
            case "editor.timeline-pause":
            case "editor.timeline-scrub-start":
                Pause();
                return true;
            case "editor.timeline-stop":
                Stop();
                return true;
            case "editor.timeline-start":
                Seek(0);
                return true;
            case "editor.timeline-scrub-end":
                Pause();
                return true;
            case "editor.timeline-zoom-in":
                State.PixelsPerSecond = Math.Min(2000, State.PixelsPerSecond * 1.25);
                StateRevision++;
                return true;
            case "editor.timeline-zoom-out":
                State.PixelsPerSecond = Math.Max(10, State.PixelsPerSecond / 1.25);
                StateRevision++;
                return true;
        }
        const string Prefix = "editor.timeline-seek:";
        if (!Action.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }
        var Parsed = double.TryParse(Action[Prefix.Length..], NumberStyles.Float,
            CultureInfo.InvariantCulture, out var Time);
        if (Parsed && double.IsFinite(Time))
        {
            Seek(Time);
        }
        return true;
    }

    private void SetTime(double Time, TimelineTimeChangeSource Source)
    {
        var Previous = State.CurrentTime;
        State.CurrentTime = Math.Clamp(Time, 0, Document.Duration);
        StateRevision++;
        TimeChanged?.Invoke(new TimelineTimeChangedEvent(Document.TimelineId, Previous, State.CurrentTime, Source));
    }

    private void SetPlaying(bool Playing)
    {
        if (State.IsPlaying == Playing)
        {
            return;
        }
        State.IsPlaying = Playing;
        StateRevision++;
        PlaybackChanged?.Invoke(new TimelinePlaybackChangedEvent(Document.TimelineId, Playing));
    }

    public bool SelectClip(string ClipId)
    {
        var Track = Document.Tracks.FirstOrDefault(Item => Item.Clips.Any(Clip => string.Equals(Clip.ClipId, ClipId, StringComparison.Ordinal)));
        if (Track is null)
        {
            return false;
        }
        State.SelectedTrackId = Track.TrackId;
        State.SelectedClipId = ClipId;
        StateRevision++;
        return true;
    }

    public static double ChooseTickSpacing(double PixelsPerSecond)
    {
        if (!double.IsFinite(PixelsPerSecond) || PixelsPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(PixelsPerSecond));
        }
        var TargetSeconds = 90 / PixelsPerSecond;
        var Power = Math.Pow(10, Math.Floor(Math.Log10(TargetSeconds)));
        var Normalized = TargetSeconds / Power;
        var Multiple = Normalized <= 1 ? 1 : Normalized <= 2 ? 2 : Normalized <= 5 ? 5 : 10;
        return Multiple * Power;
    }

    private static IReadOnlyList<TimelineRulerTick> BuildTicks(double Start, double End, float HeaderWidth, double PixelsPerSecond)
    {
        var Spacing = ChooseTickSpacing(PixelsPerSecond) / 5;
        var First = Math.Floor(Start / Spacing) * Spacing;
        var Result = new List<TimelineRulerTick>();
        for (var Time = First; Time <= End + Spacing * 0.5; Time += Spacing)
        {
            if (Time < 0)
            {
                continue;
            }
            var Major = Math.Abs(Time / Spacing - Math.Round(Time / Spacing / 5) * 5) < 1e-5;
            Result.Add(new TimelineRulerTick(Time, HeaderWidth + (float)((Time - Start) * PixelsPerSecond), FormatTime(Time), Major));
            if (Result.Count > 2000)
            {
                break;
            }
        }
        return Result;
    }

    private static string FormatTime(double Time)
    {
        if (Time >= 60)
        {
            return TimeSpan.FromSeconds(Time).ToString(@"m\:ss\.fff", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');
        }
        return Time.ToString(Time < 10 ? "0.###" : "0.#", CultureInfo.InvariantCulture) + "s";
    }

    private static string Number(double Value) => Value.ToString("0.###", CultureInfo.InvariantCulture);
}
