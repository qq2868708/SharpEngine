namespace SharpEngine.Timeline;

public enum TimelineTimeChangeSource
{
    Playback,
    Seek,
    Stop,
}

public sealed record TimelineTimeChangedEvent(string TimelineId, double PreviousTime,
    double CurrentTime, TimelineTimeChangeSource Source);

public sealed record TimelinePlaybackChangedEvent(string TimelineId, bool IsPlaying);
