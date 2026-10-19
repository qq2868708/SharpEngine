using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpEngine.Timeline;

/// <summary>Transient viewport state. It is intentionally separate from the timeline asset.</summary>
public sealed class TimelineViewState
{
    public double PixelsPerSecond { get; set; } = 100;
    public double HorizontalScroll { get; set; }
    public double VerticalScroll { get; set; }
    public double CurrentTime { get; set; }
    public string? SelectedTrackId { get; set; }
    public string? SelectedClipId { get; set; }

    internal void Validate()
    {
        if (!double.IsFinite(PixelsPerSecond) || PixelsPerSecond < 10 || PixelsPerSecond > 2000) throw new ArgumentOutOfRangeException(nameof(PixelsPerSecond));
        if (!double.IsFinite(HorizontalScroll) || HorizontalScroll < 0) throw new ArgumentOutOfRangeException(nameof(HorizontalScroll));
        if (!double.IsFinite(VerticalScroll) || VerticalScroll < 0) throw new ArgumentOutOfRangeException(nameof(VerticalScroll));
        if (!double.IsFinite(CurrentTime) || CurrentTime < 0) throw new ArgumentOutOfRangeException(nameof(CurrentTime));
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

    internal TimelineViewSnapshot(string timelineId, float width, float height, float headerWidth,
        double currentTime, float playheadX, IReadOnlyList<TimelineRulerTick> ruler,
        IReadOnlyList<TimelineViewRow> rows)
    {
        TimelineId = timelineId;
        Width = width;
        Height = height;
        HeaderWidth = headerWidth;
        CurrentTime = currentTime;
        PlayheadX = playheadX;
        Ruler = ruler;
        Rows = rows;
    }

    public string TimelineId { get; }
    public float Width { get; }
    public float Height { get; }
    public float HeaderWidth { get; }
    public double CurrentTime { get; }
    public float PlayheadX { get; }
    public IReadOnlyList<TimelineRulerTick> Ruler { get; }
    public IReadOnlyList<TimelineViewRow> Rows { get; }

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
    public const float RulerHeight = 24;

    public TimelineView(TimelineDocument document, TimelineViewState? state = null)
    {
        Document = document ?? throw new ArgumentNullException(nameof(document));
        State = state ?? new TimelineViewState();
        State.Validate();
    }

    public TimelineDocument Document { get; }
    public TimelineViewState State { get; }

    public TimelineViewSnapshot BuildSnapshot(float width, float height, float headerWidth = DefaultHeaderWidth)
    {
        if (!float.IsFinite(width) || !float.IsFinite(height) || width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (!float.IsFinite(headerWidth) || headerWidth < 0 || headerWidth >= width) throw new ArgumentOutOfRangeException(nameof(headerWidth));
        State.Validate();
        var currentTime = Math.Clamp(State.CurrentTime, 0, Document.Duration);
        var timelineWidth = width - headerWidth;
        var viewStart = State.HorizontalScroll / State.PixelsPerSecond;
        var viewEnd = viewStart + timelineWidth / (float)State.PixelsPerSecond;
        var ticks = BuildTicks(viewStart, viewEnd, headerWidth, State.PixelsPerSecond);
        var rows = new List<TimelineViewRow>();
        var y = RulerHeight - (float)State.VerticalScroll;
        foreach (var track in Document.Tracks)
        {
            var clips = track.Clips.Select(clip => new TimelineViewClip(
                clip.ClipId,
                track.TrackId,
                clip.PlayableId,
                new TimelineViewRect(
                    headerWidth + (float)((clip.Start - viewStart) * State.PixelsPerSecond),
                    y,
                    Math.Max(1, (float)(clip.Duration * State.PixelsPerSecond)),
                    DefaultRowHeight),
                string.Equals(State.SelectedClipId, clip.ClipId, StringComparison.Ordinal),
                track.Muted)).ToArray();
            rows.Add(new TimelineViewRow(track.TrackId, track.Name, y, DefaultRowHeight,
                string.Equals(State.SelectedTrackId, track.TrackId, StringComparison.Ordinal), track.Muted, clips));
            y += DefaultRowHeight;
        }
        return new TimelineViewSnapshot(Document.TimelineId, width, height, headerWidth, currentTime,
            headerWidth + (float)((currentTime - viewStart) * State.PixelsPerSecond), ticks, rows);
    }

    public string BuildMarkup(float width, float height, float headerWidth = DefaultHeaderWidth)
    {
        var snapshot = BuildSnapshot(width, height, headerWidth);
        var markup = new StringBuilder("<div class=\"timeline-view\" data-timeline-id=\"")
            .Append(Escape(snapshot.TimelineId)).Append("\">");
        markup.Append("<div class=\"timeline-ruler\" data-timeline-action=\"seek\">");
        foreach (var tick in snapshot.Ruler)
            markup.Append("<button class=\"timeline-tick").Append(tick.Major ? " major" : string.Empty)
                .Append("\" style=\"left:").Append(Number(tick.X)).Append("px\" data-time=\"")
                .Append(Number(tick.Time)).Append("\" data-action=\"editor.timeline-seek:")
                .Append(Number(tick.Time)).Append("\">").Append(Escape(tick.Label)).Append("</button>");
        markup.Append("</div><div class=\"timeline-rows\">");
        foreach (var row in snapshot.Rows)
        {
            markup.Append("<div class=\"timeline-row").Append(row.Selected ? " selected" : string.Empty)
                .Append(row.Muted ? " muted" : string.Empty).Append("\" style=\"top:")
                .Append(Number(row.Y)).Append("px;height:").Append(Number(row.Height)).Append("px\" data-track-id=\"")
                .Append(Escape(row.TrackId)).Append("\"><span class=\"timeline-track-label\">")
                .Append(Escape(row.Label)).Append("</span>");
            foreach (var clip in row.Clips)
                markup.Append("<button class=\"timeline-clip").Append(clip.Selected ? " selected" : string.Empty)
                    .Append(clip.Muted ? " muted" : string.Empty).Append("\" style=\"left:")
                    .Append(Number(clip.Bounds.X)).Append("px;top:").Append(Number(clip.Bounds.Y - row.Y))
                    .Append("px;width:").Append(Number(clip.Bounds.Width)).Append("px;height:")
                    .Append(Number(clip.Bounds.Height)).Append("px\" data-timeline-action=\"select-clip:")
                    .Append(Escape(clip.ClipId)).Append("\" data-action=\"editor.timeline-select:")
                    .Append(Escape(clip.ClipId)).Append("\" title=\"")
                    .Append(Escape(clip.Label)).Append("\">").Append(Escape(clip.Label)).Append("</button>");
            markup.Append("</div>");
        }
        markup.Append("</div><div class=\"timeline-playhead\" style=\"left:")
            .Append(Number(snapshot.PlayheadX)).Append("px\" data-time=\"").Append(Number(snapshot.CurrentTime)).Append("\"></div></div>");
        return markup.ToString();
    }

    public void Seek(double time)
    {
        if (!double.IsFinite(time)) throw new ArgumentOutOfRangeException(nameof(time));
        State.CurrentTime = Math.Clamp(time, 0, Document.Duration);
    }

    public bool SelectClip(string clipId)
    {
        var track = Document.Tracks.FirstOrDefault(item => item.Clips.Any(clip => string.Equals(clip.ClipId, clipId, StringComparison.Ordinal)));
        if (track is null) return false;
        State.SelectedTrackId = track.TrackId;
        State.SelectedClipId = clipId;
        return true;
    }

    public static double ChooseTickSpacing(double pixelsPerSecond)
    {
        if (!double.IsFinite(pixelsPerSecond) || pixelsPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(pixelsPerSecond));
        var targetSeconds = 90 / pixelsPerSecond;
        var power = Math.Pow(10, Math.Floor(Math.Log10(targetSeconds)));
        var normalized = targetSeconds / power;
        var multiple = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
        return multiple * power;
    }

    private static IReadOnlyList<TimelineRulerTick> BuildTicks(double start, double end, float headerWidth, double pixelsPerSecond)
    {
        var spacing = ChooseTickSpacing(pixelsPerSecond);
        var first = Math.Floor(start / spacing) * spacing;
        var result = new List<TimelineRulerTick>();
        for (var time = first; time <= end + spacing * 0.5; time += spacing)
        {
            if (time < 0) continue;
            var major = Math.Abs(time / spacing % 5) < 1e-6 || spacing >= 1;
            result.Add(new TimelineRulerTick(time, headerWidth + (float)((time - start) * pixelsPerSecond), FormatTime(time), major));
            if (result.Count > 2000) break;
        }
        return result;
    }

    private static string FormatTime(double time)
    {
        if (time >= 60) return TimeSpan.FromSeconds(time).ToString(@"m\:ss\.fff", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');
        return time.ToString(time < 10 ? "0.###" : "0.#", CultureInfo.InvariantCulture) + "s";
    }

    private static string Escape(string value) => WebUtility.HtmlEncode(value);
    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
