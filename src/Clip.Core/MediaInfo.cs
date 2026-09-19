namespace Clip.Core;

public sealed record MediaInfo(
    string Path, double Duration, int Width, int Height, double FrameRate,
    int VideoStreamIndex, int? AudioStreamIndex, string Codec,
    double VideoTimestampOffset = 0, bool IsHdr = false)
{
    public bool HasAudio => AudioStreamIndex.HasValue;
    public string FileName => System.IO.Path.GetFileName(Path);
}

public readonly record struct Segment(Guid Id, double Start, double End)
{
    public double Duration => End - Start;
    public static Segment Create(double start, double end) => new(Guid.NewGuid(), start, end);
}

public readonly record struct TimelinePosition(int Index, Segment Segment, double SourceTime, double TimelineStart);
