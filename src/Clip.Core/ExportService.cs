using System.Globalization;
using System.Text;

namespace Clip.Core;

public sealed record ExportProgress(double Fraction, string Message);

public sealed class ExportService(FfmpegTools tools)
{
    private static string N(double value) => value.ToString("0.#########", CultureInfo.InvariantCulture);
    private static VideoClip[] ConvertClips(MediaInfo media, IReadOnlyList<Segment> segments) =>
        segments.Select(s => new VideoClip(s.Id, media, s.Start, s.End)).ToArray();
    private static MediaInfo[] Sources(IReadOnlyList<VideoClip> clips) =>
        clips.Select(c => c.Media).DistinctBy(m => m.Path, StringComparer.OrdinalIgnoreCase).ToArray();

    public static string BuildFilter(MediaInfo media, IReadOnlyList<Segment> segments, ExportOptions options) =>
        BuildFilter(ConvertClips(media, segments), options);

    public static string BuildTempoFilter(double speed)
    {
        VideoClip.ValidateSpeed(speed);
        List<string> filters = [];
        // Keep every stage within 0.5–2 so faster changes do not skip audio samples.
        while (speed < 0.5) { filters.Add("atempo=0.5"); speed /= 0.5; }
        while (speed > 2) { filters.Add("atempo=2"); speed /= 2; }
        filters.Add($"atempo={N(speed)}");
        return string.Join(",", filters);
    }

    public static string BuildFilter(IReadOnlyList<VideoClip> clips, ExportOptions options)
    {
        Validate(clips, options);
        var (width, height) = options.GetDimensions(clips[0].Media);
        var sources = Sources(clips);
        var hasAudio = clips.Any(c => c.Media.HasAudio);
        var fps = clips[0].Media.FrameRate;
        var graph = new StringBuilder();
        for (var input = 0; input < sources.Length; input++)
        {
            var source = sources[input];
            var indexes = Enumerable.Range(0, clips.Count).Where(i => EditProject.SameSource(clips[i].Media, source)).ToArray();
            graph.Append($"[{input}:{source.VideoStreamIndex}]setpts=PTS-STARTPTS,split={indexes.Length}");
            foreach (var i in indexes) graph.Append($"[vs{i}]");
            graph.AppendLine(";");
            if (!source.HasAudio) continue;
            graph.Append($"[{input}:{source.AudioStreamIndex}]asetpts=PTS-({N(source.VideoTimestampOffset)})/TB,aresample=48000:async=1:first_pts=0,aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo,apad,asplit={indexes.Length}");
            foreach (var i in indexes) graph.Append($"[as{i}]");
            graph.AppendLine(";");
        }
        for (var i = 0; i < clips.Count; i++)
        {
            var clip = clips[i];
            graph.AppendLine($"[vs{i}]trim=start={N(clip.Start)}:end={N(clip.End)},settb=AVTB,setpts=(PTS-STARTPTS)/{N(clip.Speed)}," +
                $"scale=w='trunc(ih*dar/2)*2':h=ih,setsar=1,scale={width}:{height}:force_original_aspect_ratio=decrease:force_divisible_by=2," +
                $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,setsar=1,format=yuv420p," +
                $"fps={N(fps)}:eof_action=pass,tpad=stop_mode=clone:stop_duration={N(Math.Max(1 / (clip.Media.FrameRate * clip.Speed), 2 / fps))},trim=duration={N(Math.Max(clip.Duration, 1 / fps))},settb=AVTB[v{i}];");
            if (clip.Media.HasAudio)
                graph.AppendLine($"[as{i}]atrim=start={N(clip.Start)}:end={N(clip.End)},asetpts=PTS-STARTPTS,{BuildTempoFilter(clip.Speed)},apad,atrim=duration={N(clip.Duration)}[a{i}];");
            else if (hasAudio)
                graph.AppendLine($"anullsrc=r=48000:cl=stereo,atrim=duration={N(clip.Duration)},asetpts=PTS-STARTPTS[a{i}];");
        }
        for (var i = 0; i < clips.Count; i++)
        {
            graph.Append($"[v{i}]");
            if (hasAudio) graph.Append($"[a{i}]");
        }
        graph.Append($"concat=n={clips.Count}:v=1:a={(hasAudio ? 1 : 0)}[video]");
        if (hasAudio) graph.Append("[audio]");
        return graph.ToString();
    }

    public static IReadOnlyList<string> BuildArguments(MediaInfo media, ExportOptions options, string filterFile, string output) =>
        BuildArguments([VideoClip.Create(media)], options, filterFile, output);

    public static IReadOnlyList<string> BuildArguments(IReadOnlyList<VideoClip> clips, ExportOptions options, string filterFile, string output)
    {
        List<string> arguments = ["-hide_banner", "-nostdin", "-y", "-loglevel", "warning", "-copyts", "-start_at_zero"];
        foreach (var media in Sources(clips))
        {
            // Each input chooses NVDEC independently; CPU filters receive downloaded frames.
            if (options.HardwareDecode) arguments.AddRange(["-hwaccel", "cuda"]);
            arguments.AddRange(["-i", media.Path]);
        }
        arguments.AddRange(["-filter_complex_script", filterFile, "-map", "[video]"]);
        if (clips.Any(c => c.Media.HasAudio)) arguments.AddRange(["-map", "[audio]", "-c:a", "aac", "-b:a", "192k"]);
        else arguments.Add("-an");
        if (options.Encoder == VideoEncoder.Nvidia)
            arguments.AddRange(["-c:v", "h264_nvenc", "-preset", "p5", "-tune", "hq", "-rc", "vbr", "-cq", options.QualityValue.ToString(CultureInfo.InvariantCulture), "-b:v", "0"]);
        else
            arguments.AddRange(["-c:v", "libx264", "-preset", "medium", "-crf", options.QualityValue.ToString(CultureInfo.InvariantCulture)]);
        arguments.AddRange(["-fps_mode", "vfr", "-map_metadata", "-1", "-metadata:s:v:0", "rotate=0",
            "-movflags", "+faststart", "-progress", "pipe:1", "-nostats", output]);
        return arguments;
    }

    public Task ExportAsync(MediaInfo media, IReadOnlyList<Segment> segments, ExportOptions options,
        string output, IProgress<ExportProgress>? progress = null, CancellationToken token = default) =>
        ExportAsync(ConvertClips(media, segments), options, output, progress, token);

    public Task ExportAsync(EditProject project, ExportOptions options, string output,
        IProgress<ExportProgress>? progress = null, CancellationToken token = default) =>
        ExportAsync(project.ExportClips(), options, output, progress, token);

    public Task ExportAsync(EditProject project, Guid trackId, ExportOptions options, string output,
        IProgress<ExportProgress>? progress = null, CancellationToken token = default) =>
        ExportAsync(project.ExportClips(trackId), options, output, progress, token);

    public async Task ExportAsync(IReadOnlyList<VideoClip> clips, ExportOptions options,
        string output, IProgress<ExportProgress>? progress = null, CancellationToken token = default)
    {
        var snapshot = clips.ToArray();
        var filter = BuildFilter(snapshot, options);
        output = Path.GetFullPath(output);
        if (snapshot.Any(c => string.Equals(output, Path.GetFullPath(c.Media.Path), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("不能覆盖源视频，请选择不同的导出路径。");
        if (File.Exists(output)) throw new IOException("目标文件已存在，请选择新文件名。");
        if (!string.Equals(Path.GetExtension(output), ".mp4", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("导出格式为 MP4，请使用 .mp4 扩展名。");
        var directory = Path.GetDirectoryName(output)!;
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("导出目录不存在。");
        var id = Guid.NewGuid().ToString("N");
        var staging = Path.Combine(directory, $".clip-{id}.mp4");
        var script = Path.Combine(Path.GetTempPath(), $"clip-{id}.ffgraph");
        try
        {
            await File.WriteAllTextAsync(script, filter, new UTF8Encoding(false), token);
            var duration = snapshot.Sum(s => s.Duration);
            double lastFraction = 0;
            progress?.Report(new(0, "正在编码所选轨道…"));
            var result = await ProcessRunner.RunAsync(tools.Ffmpeg, BuildArguments(snapshot, options, script, staging), token, line =>
            {
                var fraction = lastFraction;
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                    double.TryParse(line.AsSpan(12), CultureInfo.InvariantCulture, out var microseconds))
                    fraction = microseconds / 1_000_000 / duration;
                else if (line.StartsWith("frame=", StringComparison.Ordinal) &&
                    double.TryParse(line.AsSpan(6), CultureInfo.InvariantCulture, out var frames))
                    fraction = frames / (duration * snapshot[0].Media.FrameRate);
                lastFraction = Math.Clamp(Math.Max(lastFraction, fraction), 0, 0.99);
                progress?.Report(new(lastFraction, "正在变速并拼合轨道片段…"));
            });
            if (result.ExitCode != 0)
            {
                var hint = options.HardwareDecode ? "可关闭 NVIDIA 硬件解码后重试；仍失败时选择 CPU 编码。" :
                    options.Encoder == VideoEncoder.Nvidia ? "请检查 NVIDIA 驱动和 NVENC 支持，或选择 CPU 编码。" : "请检查源视频、输出空间和 FFmpeg。";
                throw new InvalidOperationException($"导出失败。{hint}\n\n{result.StandardError}");
            }
            token.ThrowIfCancellationRequested();
            if (!File.Exists(staging) || new FileInfo(staging).Length == 0) throw new IOException("FFmpeg 未生成有效输出文件。");
            var verified = await tools.ProbeAsync(staging, token);
            var dimensions = options.GetDimensions(snapshot[0].Media);
            if (verified.Width != dimensions.Width || verified.Height != dimensions.Height)
                throw new IOException("导出视频的尺寸与请求不符，未发布输出文件。");
            File.Move(staging, output, overwrite: false);
            progress?.Report(new(1, "导出完成"));
        }
        finally { TryDelete(script); TryDelete(staging); }
    }

    public async Task MakePreviewAsync(MediaInfo media, string output, bool hardwareDecode,
        IProgress<ExportProgress>? progress, CancellationToken token)
    {
        var scale = Math.Min(1, Math.Min(1280.0 / media.Width, 720.0 / media.Height));
        var options = new ExportOptions(ExportQuality.Compact,
            Math.Max(2, (int)(media.Width * scale) / 2 * 2), Math.Max(2, (int)(media.Height * scale) / 2 * 2),
            VideoEncoder.Software, hardwareDecode);
        await ExportAsync(media, [Segment.Create(0, media.Duration)], options, output, progress, token);
    }

    private static void Validate(IReadOnlyList<VideoClip> clips, ExportOptions options)
    {
        if (clips.Count == 0) throw new InvalidOperationException("所选轨道为空，请选择包含片段的轨道。");
        foreach (var clip in clips)
        {
            clip.Validate();
            if (clip.Media.IsHdr) throw new NotSupportedException("HDR 转 SDR 需要色调映射，请先将素材转换为 SDR。");
        }
        options.GetDimensions(clips[0].Media);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
