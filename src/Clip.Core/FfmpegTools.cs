using System.Globalization;
using System.Text.Json;

namespace Clip.Core;

public sealed record FfmpegTools(string Ffmpeg, string Ffprobe)
{
    public static FfmpegTools Discover(string? directory = null)
    {
        var extension = OperatingSystem.IsWindows() ? ".exe" : "";
        var candidates = new[] { directory, Environment.GetEnvironmentVariable("CLIP_FFMPEG_DIR"),
            System.IO.Path.Combine(AppContext.BaseDirectory, "ffmpeg"), AppContext.BaseDirectory };
        foreach (var folder in candidates)
        {
            if (string.IsNullOrWhiteSpace(folder)) continue;
            var ffmpeg = System.IO.Path.Combine(folder, "ffmpeg" + extension);
            var ffprobe = System.IO.Path.Combine(folder, "ffprobe" + extension);
            if (File.Exists(ffmpeg) && File.Exists(ffprobe)) return new(ffmpeg, ffprobe);
        }
        if (directory is not null)
            throw new FileNotFoundException("所选目录必须同时包含 ffmpeg.exe 和 ffprobe.exe。");
        return new("ffmpeg" + extension, "ffprobe" + extension);
    }

    public async Task VerifyAsync(CancellationToken token = default)
    {
        foreach (var tool in new[] { Ffmpeg, Ffprobe })
        {
            var result = await ProcessRunner.RunAsync(tool, ["-version"], token);
            if (result.ExitCode != 0) throw new InvalidOperationException($"无法运行 {tool}：{result.StandardError}");
        }
    }

    public async Task<MediaInfo> ProbeAsync(string path, CancellationToken token = default)
    {
        path = System.IO.Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("找不到视频文件。", path);
        var result = await ProcessRunner.RunAsync(Ffprobe,
            ["-v", "error", "-show_streams", "-show_format", "-of", "json", path], token);
        if (result.ExitCode != 0) throw new InvalidOperationException($"无法读取视频：{result.StandardError}");
        return ParseProbe(path, result.StandardOutput);
    }

    public static MediaInfo ParseProbe(string path, string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var streams = root.GetProperty("streams").EnumerateArray().ToArray();
        var video = streams.FirstOrDefault(s => Text(s, "codec_type") == "video" &&
            (!s.TryGetProperty("disposition", out var d) || Number(d, "attached_pic") != 1));
        if (video.ValueKind == JsonValueKind.Undefined) throw new InvalidOperationException("文件中没有可剪辑的视频轨道。");
        var audio = streams.FirstOrDefault(s => Text(s, "codec_type") == "audio");
        root.TryGetProperty("format", out var format);
        var duration = Number(video, "duration", Number(format, "duration"));
        var frameRate = Rational(Text(video, "avg_frame_rate"), Rational(Text(video, "r_frame_rate"), 30));
        var width = (int)Number(video, "width");
        var height = (int)Number(video, "height");
        var sar = Rational(Text(video, "sample_aspect_ratio").Replace(':', '/'), 1);
        width = (int)Math.Round(width * sar);
        var rotation = 0.0;
        if (video.TryGetProperty("side_data_list", out var sideData))
            foreach (var item in sideData.EnumerateArray()) rotation += Number(item, "rotation");
        if (Math.Abs(rotation % 180) > 45 && Math.Abs(rotation % 180) < 135) (width, height) = (height, width);
        if (!double.IsFinite(duration) || duration <= 0 || width < 1 || height < 1)
            throw new InvalidOperationException("视频时长或尺寸无效；暂不支持直播流与静态图片。");
        return new(path, duration, width, height, frameRate, (int)Number(video, "index"),
            audio.ValueKind == JsonValueKind.Undefined ? null : (int)Number(audio, "index"), Text(video, "codec_name"),
            Number(video, "start_time") - Number(format, "start_time"),
            Text(video, "color_transfer") is "smpte2084" or "arib-std-b67");
    }

    public static IReadOnlyList<string> BuildNvidiaProbeArguments() =>
        // Turing and newer NVENC implementations reject tiny inputs such as 128x128.
        // Use an ordinary SDR frame and the same encoding settings as a balanced export.
        ["-hide_banner", "-v", "error", "-nostdin", "-f", "lavfi", "-i", "color=c=black:s=640x360:r=30,format=yuv420p",
         "-frames:v", "1", "-an", "-c:v", "h264_nvenc", "-pix_fmt", "yuv420p",
         "-preset", "p5", "-tune", "hq", "-rc", "vbr", "-cq", "23", "-b:v", "0", "-f", "null", "-"];

    public async Task<NvidiaEncoderProbeResult> ProbeNvidiaAsync(CancellationToken token = default)
    {
        // An encoder listing only proves that FFmpeg was built with NVENC, not that a GPU/driver works.
        var result = await ProcessRunner.RunAsync(Ffmpeg, BuildNvidiaProbeArguments(), token);
        return new(result.ExitCode, result.StandardError);
    }

    public async Task<bool> CanEncodeNvidiaAsync(CancellationToken token = default) =>
        (await ProbeNvidiaAsync(token)).IsAvailable;

    public async Task MakeThumbnailAsync(MediaInfo media, string output, CancellationToken token = default)
    {
        var result = await ProcessRunner.RunAsync(Ffmpeg,
            ["-hide_banner", "-v", "error", "-nostdin", "-y", "-ss", "0", "-i", media.Path,
             "-map", $"0:{media.VideoStreamIndex}", "-frames:v", "1", "-vf", "scale=480:-2", output], token);
        if (result.ExitCode != 0) throw new InvalidOperationException(result.StandardError);
    }

    /// <summary>把源音频降采样为时间轴波形峰值；不保留任何临时音频。</summary>
    public async Task<float[]> MakeWaveformAsync(MediaInfo media, int bucketCount = 2400, CancellationToken token = default)
    {
        if (!media.HasAudio) return [];
        var raw = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"clip-waveform-{Guid.NewGuid():N}.pcm");
        try
        {
            var result = await ProcessRunner.RunAsync(Ffmpeg,
                ["-hide_banner", "-v", "error", "-nostdin", "-y", "-i", media.Path,
                 "-map", $"0:{media.AudioStreamIndex}", "-vn", "-ac", "1", "-ar", "200",
                 "-c:a", "pcm_s16le", "-f", "s16le", raw], token);
            if (result.ExitCode != 0) throw new InvalidOperationException(result.StandardError);
            var bytes = await File.ReadAllBytesAsync(raw, token);
            var sampleCount = bytes.Length / 2;
            if (sampleCount == 0) return [];
            var buckets = Math.Clamp(bucketCount, 1, sampleCount);
            var peaks = new float[buckets];
            for (var bucket = 0; bucket < buckets; bucket++)
            {
                var start = (int)((long)bucket * sampleCount / buckets);
                var end = Math.Max(start + 1, (int)((long)(bucket + 1) * sampleCount / buckets));
                var peak = 0;
                for (var sample = start; sample < end; sample++)
                {
                    var index = sample * 2;
                    var value = (short)(bytes[index] | bytes[index + 1] << 8);
                    peak = Math.Max(peak, Math.Abs((int)value));
                }
                peaks[bucket] = Math.Min(1, peak / 32768f);
            }
            return peaks;
        }
        finally
        {
            if (File.Exists(raw)) File.Delete(raw);
        }
    }

    internal static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value.ToString() : "";

    private static double Number(JsonElement element, string name, double fallback = 0) =>
        double.TryParse(Text(element, name), CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : fallback;

    private static double Rational(string value, double fallback)
    {
        var parts = value.Split('/');
        if (parts.Length != 2 || !double.TryParse(parts[0], CultureInfo.InvariantCulture, out var numerator) ||
            !double.TryParse(parts[1], CultureInfo.InvariantCulture, out var denominator) || denominator <= 0 || numerator <= 0)
            return fallback;
        return numerator / denominator;
    }
}
