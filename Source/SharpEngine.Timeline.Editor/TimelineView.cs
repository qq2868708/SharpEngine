using System.Globalization;
using System.Net;
using System.Text;
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
public sealed class TimelineView
{
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

    public string BuildMarkup(float Width, float Height, float HeaderWidth = DefaultHeaderWidth)
    {
        var Snapshot = BuildSnapshot(Width, Height, HeaderWidth);
        var Markup = new StringBuilder("<div class=\"timeline-view\" data-timeline-id=\"")
            .Append(Escape(Snapshot.TimelineId)).Append("\" data-timeline-header=\"")
            .Append(Number(HeaderWidth)).Append("\" data-timeline-scale=\"").Append(Number(State.PixelsPerSecond))
            .Append("\" data-timeline-scroll=\"").Append(Number(State.HorizontalScroll))
            .Append("\" data-timeline-duration=\"").Append(Number(Document.Duration)).Append("\">");
        Markup.Append("<div class=\"timeline-transport\">")
            .Append("<button id=\"timelineStart\" data-action=\"editor.timeline-start\" title=\"Go to start\">|&lt;</button>")
            .Append("<button id=\"timelinePlay\" data-action=\"editor.timeline-play\"")
            .Append(IsPlaying ? " disabled" : string.Empty).Append(">Play</button>")
            .Append("<button id=\"timelinePause\" data-action=\"editor.timeline-pause\"")
            .Append(IsPlaying ? string.Empty : " disabled").Append(">Pause</button>")
            .Append("<button id=\"timelineStop\" data-action=\"editor.timeline-stop\">Stop</button>")
            .Append("<span class=\"timeline-current-time\">").Append(Escape(FormatTime(State.CurrentTime)))
            .Append(" / ").Append(Escape(FormatTime(Document.Duration))).Append("</span>")
            .Append("<button data-action=\"editor.timeline-zoom-out\" title=\"Zoom out\">-</button>")
            .Append("<button data-action=\"editor.timeline-zoom-in\" title=\"Zoom in\">+</button></div>");
        Markup.Append("<div id=\"timelineRuler\" class=\"timeline-ruler\" data-timeline-ruler=\"true\" data-timeline-header=\"")
            .Append(Number(HeaderWidth)).Append("\" data-timeline-scale=\"").Append(Number(State.PixelsPerSecond))
            .Append("\" data-timeline-scroll=\"").Append(Number(State.HorizontalScroll))
            .Append("\" data-timeline-duration=\"").Append(Number(Document.Duration)).Append("\">")
            .Append("<div class=\"timeline-ruler-header\">Time</div><div class=\"timeline-ruler-scale\">");
        foreach (var Tick in Snapshot.Ruler)
        {
            Markup.Append("<div class=\"timeline-tick").Append(Tick.Major ? " major" : string.Empty)
                .Append("\" style=\"left:").Append(Number(Tick.X - HeaderWidth)).Append("px\">")
                .Append(Tick.Major ? Escape(Tick.Label) : string.Empty).Append("</div>");
        }
        Markup.Append("</div></div><div class=\"timeline-rows\">");
        foreach (var Row in Snapshot.Rows)
        {
            Markup.Append("<div class=\"timeline-row").Append(Row.Selected ? " selected" : string.Empty)
                .Append(Row.Muted ? " muted" : string.Empty).Append("\" style=\"top:")
                .Append(Number(Row.Y - RulerHeight)).Append("px;height:").Append(Number(Row.Height))
                .Append("px\" data-track-id=\"").Append(Escape(Row.TrackId)).Append("\">")
                .Append("<div class=\"timeline-row-clips\">");
            foreach (var Clip in Row.Clips)
            {
                Markup.Append("<button class=\"timeline-clip").Append(Clip.Selected ? " selected" : string.Empty)
                    .Append(Clip.Muted ? " muted" : string.Empty).Append("\" style=\"left:")
                    .Append(Number(Clip.Bounds.X - HeaderWidth)).Append("px;width:")
                    .Append(Number(Clip.Bounds.Width)).Append("px;height:").Append(Number(Clip.Bounds.Height))
                    .Append("px\" data-timeline-action=\"select-clip:").Append(Escape(Clip.ClipId))
                    .Append("\" data-action=\"editor.timeline-select:").Append(Escape(Clip.ClipId))
                    .Append("\" title=\"").Append(Escape(Clip.Label)).Append("\">")
                    .Append(Escape(Clip.Label)).Append("</button>");
            }
            Markup.Append("</div><span class=\"timeline-track-label\">").Append(Escape(Row.Label))
                .Append("</span></div>");
        }
        Markup.Append("</div>");
        if (Snapshot.PlayheadX >= HeaderWidth && Snapshot.PlayheadX <= Width)
        {
            Markup.Append("<div id=\"timelinePlayhead\" class=\"timeline-playhead\" style=\"left:")
                .Append(Number(Snapshot.PlayheadX)).Append("px\" data-time=\"").Append(Number(Snapshot.CurrentTime))
                .Append("\" data-timeline-ruler=\"true\"><div class=\"timeline-playhead-arrow\"><div class=\"timeline-playhead-tip\" /></div></div>");
        }
        Markup.Append("</div>");
        return Markup.ToString();
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

    private static string Escape(string Value) => WebUtility.HtmlEncode(Value);
    private static string Number(double Value) => Value.ToString("0.###", CultureInfo.InvariantCulture);
}
