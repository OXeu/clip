namespace Clip.Core;

public readonly record struct PlaybackPosition(TimelinePosition Position, bool RequiresSeek, bool ReachedEnd)
{
    public double TimelineTime => Position.TimelineStart + Position.SourceTime - Position.Segment.Start;
}
