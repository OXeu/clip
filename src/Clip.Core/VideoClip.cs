namespace Clip.Core;

public enum ClipKind { Combined, Video, Audio }
public enum TrackKind { Video, Audio }

public sealed record VideoClip(Guid Id, MediaInfo Media, double Start, double End, double Speed = 1, ClipKind Kind = ClipKind.Combined)
{
    public const double MinimumSpeed = 0.1;
    public const double MaximumSpeed = 8;
    public string? Name { get; init; }
    public string DisplayName => Name ?? Media.FileName;
    public double SourceDuration => End - Start;
    public double Duration => SourceDuration / Speed;
    public static VideoClip Create(MediaInfo media) => new(Guid.NewGuid(), media, 0, media.Duration);

    public void Validate()
    {
        ValidateSpeed(Speed);
        if (!double.IsFinite(Start) || !double.IsFinite(End) || Start < 0 || End > Media.Duration + 0.001 || End <= Start ||
            !double.IsFinite(Media.Duration) || Media.Duration <= 0 || !double.IsFinite(Media.FrameRate) || Media.FrameRate <= 0 ||
            Media.Width < 1 || Media.Height < 1 || Media.VideoStreamIndex < 0 || Media.AudioStreamIndex < 0 || string.IsNullOrWhiteSpace(Media.Path))
            throw new ArgumentException("片段的素材信息或源视频范围无效。");
    }

    public static void ValidateSpeed(double speed)
    {
        if (!double.IsFinite(speed) || speed < MinimumSpeed || speed > MaximumSpeed)
            throw new ArgumentException("片段速度必须在 0.1–8 倍之间。");
    }
}

public sealed record VideoTrack(Guid Id, string Name, bool IsMain, IReadOnlyList<VideoClip> Clips,
    TrackKind Kind = TrackKind.Video, Guid? BindingId = null)
{
    public double Duration => Clips.Sum(c => c.Duration);
}

public readonly record struct SeparatedImport(VideoTrack VideoTrack, VideoTrack? AudioTrack);

public readonly record struct ClipPosition(Guid TrackId, int Index, VideoClip Clip, double SourceTime, double TimelineStart)
{
    public double TimelineTime => TimelineStart + (SourceTime - Clip.Start) / Clip.Speed;
}

public readonly record struct ClipPlayback(ClipPosition Position, bool RequiresSeek, bool ReachedEnd);
