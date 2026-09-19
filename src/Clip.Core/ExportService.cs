using System.Globalization;
using System.Text;

namespace Clip.Core;

public sealed record ExportProgress(double Fraction, string Message);

public sealed class ExportService(FfmpegTools tools)
{
    private static string N(double value) => value.ToString("0.#########", CultureInfo.InvariantCulture);

    public static string BuildFilter(MediaInfo media, IReadOnlyList<Segment> segments, ExportOptions options)
    {
        Validate(media, segments, options);
        var (width, height) = options.GetDimensions(media);
        var graph = new StringBuilder();
        graph.Append($"[0:{media.VideoStreamIndex}]setpts=PTS-STARTPTS,split={segments.Count}");
        for (var i = 0; i < segments.Count; i++) graph.Append($"[vs{i}]");
        graph.AppendLine(";");
        if (media.HasAudio)
        {
            graph.Append($"[0:{media.AudioStreamIndex}]asetpts=PTS-({N(media.VideoTimestampOffset)})/TB,aresample=48000:async=1:first_pts=0,apad,asplit={segments.Count}");
            for (var i = 0; i < segments.Count; i++) graph.Append($"[as{i}]");
            graph.AppendLine(";");
        }
        for (var i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            graph.AppendLine($"[vs{i}]trim=start={N(s.Start)}:end={N(s.End)},setpts=PTS-STARTPTS[v{i}];");
            if (media.HasAudio)
                graph.AppendLine($"[as{i}]atrim=start={N(s.Start)}:end={N(s.End)},asetpts=PTS-STARTPTS[a{i}];");
        }
        for (var i = 0; i < segments.Count; i++)
        {
            graph.Append($"[v{i}]");
            if (media.HasAudio) graph.Append($"[a{i}]");
        }
        graph.Append($"concat=n={segments.Count}:v=1:a={(media.HasAudio ? 1 : 0)}[joined]");
        if (media.HasAudio) graph.Append("[audio]");
        // Force square pixels, preserve display aspect ratio, and letterbox to the exact requested dimensions.
        graph.AppendLine(";");
        graph.Append($"[joined]scale=w='trunc(ih*dar/2)*2':h=ih,setsar=1,scale={width}:{height}:force_original_aspect_ratio=decrease:force_divisible_by=2,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,setsar=1,format=yuv420p[video]");
        return graph.ToString();
    }

    public static IReadOnlyList<string> BuildArguments(MediaInfo media, ExportOptions options, string filterFile, string output)
    {
        List<string> arguments = ["-hide_banner", "-nostdin", "-y", "-loglevel", "warning", "-copyts", "-start_at_zero"];
        // Frames are downloaded for CPU trim/concat/scale filters. NVDEC decoding and NVENC encoding remain accelerated.
        if (options.HardwareDecode) arguments.AddRange(["-hwaccel", "cuda"]);
        arguments.AddRange(["-i", media.Path, "-filter_complex_script", filterFile, "-map", "[video]"]);
        if (media.HasAudio) arguments.AddRange(["-map", "[audio]", "-c:a", "aac", "-b:a", "192k"]);
        else arguments.Add("-an");
        if (options.Encoder == VideoEncoder.Nvidia)
            arguments.AddRange(["-c:v", "h264_nvenc", "-preset", "p5", "-tune", "hq", "-rc", "vbr", "-cq", options.QualityValue.ToString(CultureInfo.InvariantCulture), "-b:v", "0"]);
        else
            arguments.AddRange(["-c:v", "libx264", "-preset", "medium", "-crf", options.QualityValue.ToString(CultureInfo.InvariantCulture)]);
        arguments.AddRange(["-fps_mode", "vfr", "-map_metadata", "-1", "-metadata:s:v:0", "rotate=0",
            "-movflags", "+faststart", "-progress", "pipe:1", "-nostats", output]);
        return arguments;
    }

    public async Task ExportAsync(MediaInfo media, IReadOnlyList<Segment> segments, ExportOptions options,
        string output, IProgress<ExportProgress>? progress = null, CancellationToken token = default)
    {
        var snapshot = segments.ToArray();
        var filter = BuildFilter(media, snapshot, options);
        output = Path.GetFullPath(output);
        if (string.Equals(output, Path.GetFullPath(media.Path), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("不能覆盖源视频，请选择不同的导出路径。");
        if (File.Exists(output)) throw new IOException("目标文件已存在，请选择新文件名。");
        if (!string.Equals(Path.GetExtension(output), ".mp4", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("第一期导出格式为 MP4，请使用 .mp4 扩展名。");
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
            progress?.Report(new(0, "正在编码…"));
            var result = await ProcessRunner.RunAsync(tools.Ffmpeg, BuildArguments(media, options, script, staging), token, line =>
            {
                var fraction = lastFraction;
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                    double.TryParse(line.AsSpan(12), CultureInfo.InvariantCulture, out var microseconds))
                    fraction = microseconds / 1_000_000 / duration;
                // With -copyts some FFmpeg versions report out_time_us=0. Frame progress also covers those builds.
                else if (line.StartsWith("frame=", StringComparison.Ordinal) &&
                    double.TryParse(line.AsSpan(6), CultureInfo.InvariantCulture, out var frames))
                    fraction = frames / (duration * media.FrameRate);
                lastFraction = Math.Clamp(Math.Max(lastFraction, fraction), 0, 0.99);
                progress?.Report(new(lastFraction, "正在编码并拼合保留片段…"));
            });
            if (result.ExitCode != 0)
            {
                var hint = options.HardwareDecode ? "可关闭 NVIDIA 硬件解码后重试；仍失败时选择 CPU 编码。" :
                    options.Encoder == VideoEncoder.Nvidia ? "请检查 NVIDIA 驱动和 NVENC 支持，或选择 CPU 编码。" : "请检查源视频、输出空间和 FFmpeg。";
                throw new InvalidOperationException($"导出失败。{hint}\n\n{result.StandardError}");
            }
            token.ThrowIfCancellationRequested();
            if (!File.Exists(staging) || new FileInfo(staging).Length == 0) throw new IOException("FFmpeg 未生成有效输出文件。");
            File.Move(staging, output, overwrite: false);
            progress?.Report(new(1, "导出完成"));
        }
        finally
        {
            TryDelete(script);
            TryDelete(staging);
        }
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

    private static void Validate(MediaInfo media, IReadOnlyList<Segment> segments, ExportOptions options)
    {
        if (segments.Count == 0) throw new InvalidOperationException("时间轴为空，没有可导出的片段。");
        if (media.IsHdr) throw new NotSupportedException("第一期支持 SDR 视频。HDR 转 SDR 需要色调映射，请先将素材转换为 SDR。");
        foreach (var s in segments)
            if (!double.IsFinite(s.Start) || !double.IsFinite(s.End) || s.Start < 0 || s.End > media.Duration + 0.001 || s.Duration <= 0)
                throw new ArgumentException("时间轴包含无效片段。");
        options.GetDimensions(media);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
