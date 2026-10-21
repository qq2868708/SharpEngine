namespace SharpEngine.Timeline;

/// <summary>Builds a Notify-track edit; the caller owns the asset transaction and history.</summary>
public static class TimelineNotifyEditing
{
    public static bool TryAdd(TimelineDocument Source, out TimelineDocument Replacement,
        out string ClipId, out string Message)
    {
        Replacement = Source;
        ClipId = string.Empty;
        Message = string.Empty;
        var Track = Source.Tracks.FirstOrDefault(Track => Track.Type == "notify");
        if (Track is null)
        {
            Message = "Notify track is unavailable";
            return false;
        }
        var Duration = Math.Min(0.1, Source.Duration);
        var Start = Math.Clamp(0.25 * Track.Clips.Count + 0.1, 0, Source.Duration - Duration);
        foreach (var Existing in Track.Clips.OrderBy(Clip => Clip.Start))
        {
            if (Existing.Start >= Start + Duration || Existing.End <= Start)
            {
                continue;
            }
            Start = Existing.End + 0.05;
        }
        if (Start + Duration > Source.Duration + 1e-9)
        {
            Message = "No free space remains on the Notify track";
            return false;
        }
        var Clip = new TimelineClip("notify-" + Guid.NewGuid().ToString("N"),
            "Notify " + (Track.Clips.Count + 1), Start, Duration);
        var Tracks = Source.Tracks.Select(Candidate => ReferenceEquals(Candidate, Track)
            ? new TimelineTrack(Track.TrackId, Track.Name, Track.TargetId, Track.Type,
                Track.Clips.Append(Clip).ToArray(), Track.Muted)
            : Candidate).ToArray();
        Replacement = new TimelineDocument(Source.TimelineId, Source.Name, Source.Duration, Source.FrameRate, Tracks);
        ClipId = Clip.ClipId;
        return true;
    }
}
