using System.Text.Json.Serialization;

namespace Clip.Core;

public enum ClipKind { Combined, Video, Audio }
public enum TrackKind { Video, Audio, Subtitle }
public enum SubtitleVerticalAlignment { Top, Center, Bottom }

public sealed record SubtitleCue(Guid Id, double Start, double End, string Text)
{
    public const double MinimumDuration = 0.1;
    public double Duration => End - Start;

    public void Validate(double trackDuration)
    {
        if (Id == Guid.Empty || !double.IsFinite(Start) || !double.IsFinite(End) || Start < 0 ||
            End - Start < MinimumDuration || End > trackDuration + 0.001 || string.IsNullOrWhiteSpace(Text))
            throw new ArgumentException("字幕片段的时间或文本无效。");
    }
}

/// <summary>Normalized subtitle guide rectangle inside the rendered video frame.</summary>
public sealed record SubtitleRegion(
    double X = 0.1, double Y = 0.72, double Width = 0.8, double Height = 0.16,
    SubtitleVerticalAlignment Alignment = SubtitleVerticalAlignment.Bottom)
{
    public static SubtitleRegion Default { get; } = new();

    public void Validate()
    {
        if (!double.IsFinite(X) || !double.IsFinite(Y) || !double.IsFinite(Width) || !double.IsFinite(Height) ||
            X < 0 || Y < 0 || Width < 0.05 || Height < 0.05 || X + Width > 1.000001 || Y + Height > 1.000001 ||
            !Enum.IsDefined(Alignment))
            throw new ArgumentException("字幕矩形框超出视频画面。");
    }
}

public sealed record VideoClip(Guid Id, MediaInfo Media, double Start, double End, double Speed = 1,
    ClipKind Kind = ClipKind.Combined, double Volume = 1, double PitchSemitones = 0)
{
    public const double MinimumSpeed = 0.1;
    public const double MaximumSpeed = 8;
    public const double MinimumVolume = 0;
    public const double MaximumVolume = 2;
    public const double MinimumPitch = -12;
    public const double MaximumPitch = 12;
    public string? Name { get; init; }
    public string DisplayName => Name ?? Media.FileName;
    public double SourceDuration => End - Start;
    public double Duration => SourceDuration / Speed;
    public static VideoClip Create(MediaInfo media) => new(Guid.NewGuid(), media, 0, media.Duration);

    public void Validate()
    {
        ValidateSpeed(Speed);
        ValidateVolume(Volume);
        ValidatePitch(PitchSemitones);
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

    public static void ValidateVolume(double volume)
    {
        if (!double.IsFinite(volume) || volume < MinimumVolume || volume > MaximumVolume)
            throw new ArgumentException("音量必须在 0%–200% 之间。");
    }

    public static void ValidatePitch(double pitchSemitones)
    {
        if (!double.IsFinite(pitchSemitones) || pitchSemitones < MinimumPitch || pitchSemitones > MaximumPitch)
            throw new ArgumentException("变调必须在 −12 到 +12 半音之间。");
    }
}

public sealed record VideoTrack(Guid Id, string Name, bool IsMain, IReadOnlyList<VideoClip> Clips,
    TrackKind Kind = TrackKind.Video, Guid? BindingId = null, Guid? CompanionGroupId = null,
    IReadOnlyList<SubtitleCue>? Cues = null, SubtitleRegion? SubtitleRegion = null, double Volume = 1)
{
    [JsonIgnore]
    public IReadOnlyList<SubtitleCue> SubtitleCues => Cues ?? Array.Empty<SubtitleCue>();
    [JsonIgnore]
    public double Duration => Kind == TrackKind.Subtitle
        ? (SubtitleCues.Count == 0 ? 0 : SubtitleCues.Max(cue => cue.End))
        : Clips.Sum(c => c.Duration);
}

public readonly record struct SeparatedImport(VideoTrack VideoTrack, VideoTrack AudioTrack);

public readonly record struct ClipPosition(Guid TrackId, int Index, VideoClip Clip, double SourceTime, double TimelineStart)
{
    public double TimelineTime => TimelineStart + (SourceTime - Clip.Start) / Clip.Speed;
}

public readonly record struct ClipPlayback(ClipPosition Position, bool RequiresSeek, bool ReachedEnd);

public readonly record struct SubtitlePosition(Guid TrackId, int Index, SubtitleCue Cue);
