using System.Globalization;
using Clip.Core;

var failures = 0;
var passed = 0;
var media = new MediaInfo("source.mp4", 10, 1920, 1080, 30, 0, 1, "h264");

void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

void Near(double actual, double expected, double tolerance = 0.001) =>
    Check(Math.Abs(actual - expected) <= tolerance, $"Expected {expected}, got {actual}");

void Test(string name, Action run)
{
    try { run(); Console.WriteLine($"PASS {name}"); passed++; }
    catch (Exception e) { Console.Error.WriteLine($"FAIL {name}: {e}"); failures++; }
}

async Task TestAsync(string name, Func<Task> run)
{
    try { await run(); Console.WriteLine($"PASS {name}"); passed++; }
    catch (Exception e) { Console.Error.WriteLine($"FAIL {name}: {e}"); failures++; }
}

async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}");
}

Test("split, select by ID, ripple delete and source mapping", () =>
{
    var timeline = new Timeline();
    timeline.Load(media);
    var middle = timeline.Split(3)!.Value;
    timeline.Split(7);
    Check(timeline.Delete(middle), "Delete failed");
    Near(timeline.Duration, 6);
    Near(timeline.Locate(3)!.Value.SourceTime, 7);
    Near(timeline.Locate(4)!.Value.SourceTime, 8);
    Near(timeline.Locate(6)!.Value.SourceTime, 10);
});

Test("boundary splits do not create empty segments", () =>
{
    var timeline = new Timeline();
    timeline.Load(media);
    Check(timeline.Split(0) is null && timeline.Split(10) is null, "Boundary split was accepted");
    Check(timeline.Split(0.001) is null, "Sub-frame split was accepted");
    timeline.Split(5);
    Check(timeline.Split(5) is null && timeline.Segments.Count == 2, "Duplicate split created a segment");
});

Test("playback crosses live cuts without seeking or losing source time", () =>
{
    var timeline = new Timeline();
    timeline.Load(media);
    var originalId = timeline.Segments[0].Id;
    var rightId = timeline.Split(2)!.Value;
    var before = timeline.AdvancePlayback(originalId, 1.99)!.Value;
    Check(before.Position.Segment.Id == originalId && !before.RequiresSeek && !before.ReachedEnd, "Cut interrupted the left side");
    var after = timeline.AdvancePlayback(originalId, 2.12)!.Value;
    Check(after.Position.Segment.Id == rightId && !after.RequiresSeek && !after.ReachedEnd, "Contiguous cut requested a seek");
    Near(after.Position.SourceTime, 2.12);
    Near(after.TimelineTime, 2.12);
    timeline.Split(2.2);
    var lastId = timeline.Split(2.3)!.Value;
    var late = timeline.AdvancePlayback(originalId, 2.65)!.Value;
    Check(late.Position.Segment.Id == lastId && !late.RequiresSeek, "Late timer failed to cross multiple cuts");
    Near(late.TimelineTime, 2.65);
    Near(timeline.Duration, media.Duration);
});

Test("playback still skips deleted footage and stops at the retained end", () =>
{
    var timeline = new Timeline();
    timeline.Load(media);
    var first = timeline.Segments[0].Id;
    var removed = timeline.Split(2)!.Value;
    var last = timeline.Split(5)!.Value;
    timeline.Delete(removed);
    var gap = timeline.AdvancePlayback(first, 2.1)!.Value;
    Check(gap.RequiresSeek && !gap.ReachedEnd && gap.Position.Segment.Id == last, "Deleted footage was not skipped");
    Near(gap.Position.SourceTime, 5);
    Near(gap.TimelineTime, 2);
    var resumed = timeline.AdvancePlayback(last, 5.4)!.Value;
    Check(!resumed.RequiresSeek, "Playback repeatedly sought after crossing a gap");
    Near(resumed.TimelineTime, 2.4);
    var end = timeline.AdvancePlayback(last, 10.2)!.Value;
    Check(end.ReachedEnd && !end.RequiresSeek, "Playback failed to finish");
    Near(end.TimelineTime, 7);
    Check(timeline.AdvancePlayback(removed, 3) is null && timeline.AdvancePlayback(last, double.NaN) is null,
        "Invalid playback cursor was accepted");
});

Test("frame-rounded live cuts preserve the playback position on either side", () =>
{
    foreach (var time in new[] { 2.01, 2.02 })
    {
        var timeline = new Timeline();
        timeline.Load(media);
        timeline.Split(time);
        var cursor = timeline.Locate(time)!.Value;
        var playback = timeline.AdvancePlayback(cursor.Segment.Id, time)!.Value;
        Check(!playback.RequiresSeek && !playback.ReachedEnd, "Rounded split requested playback interruption");
        Near(playback.TimelineTime, time);
        Near(playback.Position.SourceTime, time);
    }
});

Test("undo/redo restores IDs and all-deleted timeline", () =>
{
    var timeline = new Timeline();
    timeline.Load(media);
    var original = timeline.Segments[0];
    timeline.Delete(original.Id);
    Check(timeline.Locate(0) is null && timeline.Duration == 0, "Empty timeline is not empty");
    timeline.Undo();
    Check(timeline.Segments[0] == original, "Undo lost segment identity");
    timeline.Redo();
    Check(timeline.Segments.Count == 0, "Redo failed");
    timeline.Undo();
    timeline.Split(4);
    Check(!timeline.CanRedo, "New edit did not discard redo history");
    timeline.Load(media);
    Check(!timeline.CanUndo && !timeline.CanRedo, "Import retained old history");
});

Test("random edits preserve timeline invariants", () =>
{
    var timeline = new Timeline();
    timeline.Load(media);
    var random = new Random(47);
    for (var i = 0; i < 1000; i++)
    {
        switch (random.Next(4))
        {
            case 0: timeline.Split(random.NextDouble() * timeline.Duration); break;
            case 1: if (timeline.Segments.Count > 0) timeline.Delete(timeline.Segments[random.Next(timeline.Segments.Count)].Id); break;
            case 2: timeline.Undo(); break;
            case 3: timeline.Redo(); break;
        }
        Check(timeline.Segments.All(s => s.Start >= 0 && s.End <= media.Duration && s.Duration > 0), "Invalid range");
        Check(timeline.Segments.Select(s => s.Id).Distinct().Count() == timeline.Segments.Count, "Duplicate ID");
        Near(timeline.Duration, timeline.Segments.Sum(s => s.Duration));
        double offset = 0;
        foreach (var segment in timeline.Segments)
        {
            Near(timeline.Locate(offset + segment.Duration / 2)!.Value.SourceTime, segment.Start + segment.Duration / 2);
            offset += segment.Duration;
        }
    }
});

Test("probe ignores cover art and handles portrait rotation, rational FPS and silent sources", () =>
{
    const string json = """
        {"streams":[
          {"index":0,"codec_type":"video","disposition":{"attached_pic":1},"width":600,"height":600},
          {"index":2,"codec_type":"video","codec_name":"h264","width":1920,"height":1080,"duration":"5.0","avg_frame_rate":"30000/1001","side_data_list":[{"rotation":-90}]}],
         "format":{"duration":"5.2","start_time":"0"}}
        """;
    var info = FfmpegTools.ParseProbe("phone.mov", json);
    Check(info.Width == 1080 && info.Height == 1920 && !info.HasAudio && info.VideoStreamIndex == 2, "Incorrect stream metadata");
    Near(info.FrameRate, 29.97002997);
    Near(info.Duration, 5);
});

Test("filter uses only retained intervals and correct audio/video stream indexes", () =>
{
    var filter = ExportService.BuildFilter(media with { VideoStreamIndex = 2, AudioStreamIndex = 3 },
        [Segment.Create(0, 2), Segment.Create(5, 10)], new());
    Check(filter.Contains("[0:2]") && filter.Contains("[0:3]") && filter.Contains("trim=start=5:end=10"), "Wrong source ranges");
    Check(filter.Contains("concat=n=2:v=1:a=1") && filter.Contains("apad"), "Audio concat missing");
});

Test("NVIDIA decoder and encoder can be configured independently", () =>
{
    var hardware = ExportService.BuildArguments(media, new(), "filter.ffgraph", "output.mp4");
    Check(hardware.Contains("cuda") && hardware.Contains("h264_nvenc"), "Hardware acceleration flags missing");
    var hybrid = ExportService.BuildArguments(media, new(HardwareDecode: false), "filter.ffgraph", "output.mp4");
    Check(!hybrid.Contains("-hwaccel") && hybrid.Contains("h264_nvenc"), "Hardware decode toggle did not work");
    var software = ExportService.BuildArguments(media, new(Encoder: VideoEncoder.Software, HardwareDecode: false), "filter.ffgraph", "output.mp4");
    Check(software.Contains("libx264") && !software.Contains("cuda"), "Software fallback unavailable");
});

Test("NVENC probe uses hardware-safe dimensions and the export encoding profile", () =>
{
    var probe = FfmpegTools.BuildNvidiaProbeArguments().ToList();
    var input = probe[probe.IndexOf("-i") + 1];
    var size = System.Text.RegularExpressions.Regex.Match(input, @"s=(\d+)x(\d+)");
    Check(size.Success, "Probe is missing its test frame dimensions");
    var width = int.Parse(size.Groups[1].Value, CultureInfo.InvariantCulture);
    var height = int.Parse(size.Groups[2].Value, CultureInfo.InvariantCulture);
    // H.264 on Turing has a minimum width of 145 and minimum height of 49.
    Check(width >= 145 && height >= 49 && width % 2 == 0 && height % 2 == 0,
        "Probe would reject a working NVENC encoder because the test frame is too small or odd-sized");
    Check(probe.Contains("yuv420p") && !probe.Contains("-hwaccel"), "Probe must use SDR software frames independently of NVDEC");
    var export = ExportService.BuildArguments(media, new(ExportQuality.Balanced, HardwareDecode: false), "filter.ffgraph", "output.mp4").ToList();
    foreach (var option in new[] { "-c:v", "-preset", "-tune", "-rc", "-cq", "-b:v" })
    {
        Check(probe.Contains(option) && export.Contains(option), $"Missing encoder option: {option}");
        Check(probe[probe.IndexOf(option) + 1] == export[export.IndexOf(option) + 1], $"Probe differs from export: {option}");
    }
    Check(probe.Contains("-nostdin") && probe[probe.IndexOf("-frames:v") + 1] == "1", "Probe must be noninteractive and finite");
});

Test("NVENC failures preserve raw errors and distinguish driver, build and device problems", () =>
{
    (string Error, string Hint)[] failuresToCheck =
    [
        ("Driver does not support the required nvenc API version. Required: 13.0 Found: 12.2\nThe minimum required Nvidia driver for nvenc is 570.0 or newer", "驱动不兼容"),
        ("Unknown encoder 'h264_nvenc'", "未包含 h264_nvenc"),
        ("Unrecognized option 'rc'.\nError splitting the argument list: Option not found", "不支持 NVENC 检测所需参数"),
        ("Cannot load nvcuda.dll", "驱动组件"),
        ("Cannot load nvEncodeAPI64.dll", "驱动组件"),
        ("Cannot load libnvidia-encode.so.1", "驱动组件"),
        ("No capable devices found", "未找到可用"),
        ("InitializeEncoder failed: invalid param (8)", "试编码失败"),
        ("", "试编码失败")
    ];
    foreach (var (error, hint) in failuresToCheck)
    {
        var result = new NvidiaEncoderProbeResult(1, error);
        Check(!result.IsAvailable && result.Summary.Contains(hint), $"Wrong diagnostic for: {error}");
        Check(result.StandardError == error && result.ExitCode == 1, "Raw FFmpeg failure was lost");
    }
    Check(new NvidiaEncoderProbeResult(0, "warning").IsAvailable, "Successful encoding must not be rejected for stderr warnings");
    Check(!new NvidiaEncoderProbeResult(-1, "h264_nvenc is listed").IsAvailable, "Encoder listing is not proof of working hardware");
});

await TestAsync("NVENC detection respects cancellation before launching FFmpeg", async () =>
{
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    var unavailableTools = new FfmpegTools("clip-test-missing-ffmpeg", "clip-test-missing-ffprobe");
    await ThrowsAsync<OperationCanceledException>(() => unavailableTools.ProbeNvidiaAsync(cancellation.Token));
});

Test("custom dimensions validate and silent sources omit audio", () =>
{
    var options = new ExportOptions(Width: 720, Height: 1280);
    Check(options.GetDimensions(media) == (720, 1280), "Custom size lost");
    var filter = ExportService.BuildFilter(media with { AudioStreamIndex = null }, [Segment.Create(0, 5)], options);
    Check(!filter.Contains("atrim") && filter.Contains("a=0") && filter.Contains("pad=720:1280"), "Silent video filter incorrect");
    try { new ExportOptions(Width: 721, Height: 1280).GetDimensions(media); throw new Exception("Odd width accepted"); }
    catch (ArgumentException) { }
});

Test("filter numbers use invariant culture", () =>
{
    var previous = CultureInfo.CurrentCulture;
    try
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        var filter = ExportService.BuildFilter(media, [Segment.Create(1.25, 5.5)], new());
        Check(filter.Contains("start=1.25:end=5.5"), "Locale corrupted FFmpeg expression");
    }
    finally { CultureInfo.CurrentCulture = previous; }
});

if (args.Contains("--integration"))
{
    var tools = FfmpegTools.Discover();
    var exporter = new ExportService(tools);
    var root = Path.Combine(Path.GetTempPath(), "clip-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var source = Path.Combine(root, "source '测试' video.mp4");
    async Task Run(params string[] arguments)
    {
        var result = await ProcessRunner.RunAsync(tools.Ffmpeg, arguments);
        if (result.ExitCode != 0) throw new Exception(result.StandardError);
    }
    try
    {
        await tools.VerifyAsync();
        await TestAsync("NVENC probe source produces a valid SDR frame without NVIDIA decoding", async () =>
        {
            var probe = FfmpegTools.BuildNvidiaProbeArguments().ToList();
            var probeSource = probe[probe.IndexOf("-i") + 1];
            var output = Path.Combine(root, "probe-frame.mp4");
            await Run("-v", "error", "-nostdin", "-f", "lavfi", "-i", probeSource,
                "-frames:v", "1", "-an", "-c:v", "libx264", "-pix_fmt", "yuv420p", output);
            var frame = await tools.ProbeAsync(output);
            Check(frame.Width >= 145 && frame.Height >= 49 && frame.Codec == "h264", "Invalid NVENC probe source");
        });

        await TestAsync("real NVENC probe retains failure diagnostics or completes actual encoding", async () =>
        {
            var result = await tools.ProbeNvidiaAsync();
            Check(result.IsAvailable || !string.IsNullOrWhiteSpace(result.StandardError), "Failed probe discarded FFmpeg diagnostics");
            Console.WriteLine($"  NVENC available: {result.IsAvailable}; {result.Summary}");
        });

        await Run("-v", "error", "-y", "-f", "lavfi", "-i", "color=red:s=320x180:r=30:d=2",
            "-f", "lavfi", "-i", "color=blue:s=320x180:r=30:d=2", "-f", "lavfi", "-i", "color=green:s=320x180:r=30:d=2",
            "-f", "lavfi", "-i", "aevalsrc=sin(2*PI*if(lt(t\\,2)\\,440\\,if(lt(t\\,4)\\,880\\,660))*t)*0.12:s=48000:d=6",
            "-filter_complex", "[0:v][1:v][2:v]concat=n=3:v=1:a=0[v]", "-map", "[v]", "-map", "3:a",
            "-c:v", "libx264", "-g", "300", "-pix_fmt", "yuv420p", "-c:a", "aac", source);
        var input = await tools.ProbeAsync(source);
        var software = new ExportOptions(Encoder: VideoEncoder.Software, HardwareDecode: false);
        await TestAsync("FFmpeg precise non-keyframe cuts remove deleted video and audio ranges", async () =>
        {
            var output = Path.Combine(root, "trimmed 测试.mp4");
            await exporter.ExportAsync(input, [Segment.Create(0, 1.3), Segment.Create(4.1, 6)], software, output);
            var result = await tools.ProbeAsync(output);
            Near(result.Duration, 3.2, 0.07);
            Check(result.HasAudio && result.Width == 320 && result.Height == 180, "Output metadata incorrect");
            var pixels = Path.Combine(root, "pixels.rgb");
            await Run("-v", "error", "-i", output, "-vf", "scale=1:1", "-f", "rawvideo", "-pix_fmt", "rgb24", pixels);
            var bytes = await File.ReadAllBytesAsync(pixels);
            var red = 0;
            var green = 0;
            for (var i = 0; i < bytes.Length; i += 3)
            {
                Check(bytes[i + 2] < Math.Max(bytes[i], bytes[i + 1]), "A deleted blue frame leaked into export");
                if (bytes[i] > bytes[i + 1]) red++; else green++;
            }
            Check(Math.Abs(red - 39) <= 1 && Math.Abs(green - 57) <= 1, $"Incorrect cut frames: red={red}, green={green}");
            var audio = Path.Combine(root, "cut-audio.pcm");
            await Run("-v", "error", "-ss", "1.7", "-i", output, "-t", "0.2", "-f", "s16le", "-ac", "1", "-ar", "48000", audio);
            var audioBytes = await File.ReadAllBytesAsync(audio);
            double Energy(int frequency)
            {
                double real = 0, imaginary = 0;
                for (var i = 0; i < audioBytes.Length / 2; i++)
                {
                    var sample = BitConverter.ToInt16(audioBytes, i * 2);
                    real += sample * Math.Cos(2 * Math.PI * frequency * i / 48000);
                    imaginary += sample * Math.Sin(2 * Math.PI * frequency * i / 48000);
                }
                return real * real + imaginary * imaginary;
            }
            Check(Energy(660) > Energy(880) * 100, "Deleted middle audio was retained or audio was not cut with video");
        });

        await TestAsync("FFmpeg silent source exports at exact custom dimensions", async () =>
        {
            var silent = Path.Combine(root, "silent.mp4");
            await Run("-v", "error", "-i", source, "-map", "0:v:0", "-c", "copy", silent);
            var info = await tools.ProbeAsync(silent);
            var output = Path.Combine(root, "portrait.mp4");
            await exporter.ExportAsync(info, [Segment.Create(0.5, 1.5)], software with { Width = 180, Height = 320 }, output);
            var result = await tools.ProbeAsync(output);
            Check(!result.HasAudio && result.Width == 180 && result.Height == 320, "Incorrect silent custom-size output");
            Near(result.Duration, 1, 0.04);
        });

        await TestAsync("short audio is padded across later retained segments", async () =>
        {
            var shortAudio = Path.Combine(root, "short-audio.mp4");
            await Run("-v", "error", "-i", source, "-f", "lavfi", "-i", "sine=frequency=500:duration=1",
                "-map", "0:v:0", "-map", "1:a:0", "-c:v", "copy", "-c:a", "aac", shortAudio);
            var info = await tools.ProbeAsync(shortAudio);
            var output = Path.Combine(root, "padded.mp4");
            await exporter.ExportAsync(info, [Segment.Create(0, 1), Segment.Create(4, 6)], software, output);
            var result = await tools.ProbeAsync(output);
            Near(result.Duration, 3, 0.07);
            var audio = Path.Combine(root, "padded.pcm");
            await Run("-v", "error", "-i", output, "-map", "0:a:0", "-f", "s16le", "-ac", "1", "-ar", "48000", audio);
            var bytes = await File.ReadAllBytesAsync(audio);
            Near(bytes.Length / 96000.0, 3, 0.08);
            Check(bytes.Skip(96000 * 2).All(b => b == 0), "Expected silence after source audio ends");
        });

        await TestAsync("delayed audio keeps its initial silence and AV alignment", async () =>
        {
            var delayed = Path.Combine(root, "delayed.mp4");
            await Run("-v", "error", "-i", source, "-itsoffset", "1", "-i", source,
                "-map", "0:v:0", "-map", "1:a:0", "-c", "copy", delayed);
            var info = await tools.ProbeAsync(delayed);
            var output = Path.Combine(root, "aligned.mp4");
            await exporter.ExportAsync(info, [Segment.Create(0, 3)], software, output);
            var audio = Path.Combine(root, "aligned.pcm");
            await Run("-v", "error", "-i", output, "-map", "0:a:0", "-f", "s16le", "-ac", "1", "-ar", "48000", audio);
            var bytes = await File.ReadAllBytesAsync(audio);
            Check(bytes.Take(48000).All(b => b == 0), "Delayed audio was incorrectly moved to time zero");
            Check(bytes.Skip(120000).Take(20000).Any(b => b != 0), "Expected audible samples after delay");
        });

        await TestAsync("cancellation removes partial output and temporary files", async () =>
        {
            using var cancellation = new CancellationTokenSource();
            var output = Path.Combine(root, "cancelled.mp4");
            var progress = new InlineProgress<ExportProgress>(p => { if (p.Fraction > 0) cancellation.Cancel(); });
            await ThrowsAsync<OperationCanceledException>(() => exporter.ExportAsync(input,
                [Segment.Create(0, 6)], software, output, progress, cancellation.Token));
            Check(!File.Exists(output) && !Directory.EnumerateFiles(root, ".clip-*").Any(), "Partial output was left behind");
        });

        await TestAsync("cancelling a live FFmpeg process stops it promptly", async () =>
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await ThrowsAsync<OperationCanceledException>(() => ProcessRunner.RunAsync(tools.Ffmpeg,
                ["-v", "error", "-re", "-f", "lavfi", "-i", "color=red:s=320x180:r=30:d=30", "-f", "null", "-"], cancellation.Token));
            Check(watch.Elapsed < TimeSpan.FromSeconds(5), "Cancellation did not terminate the running process");
        });

        await TestAsync("source and existing destinations cannot be overwritten", async () =>
        {
            var sourceBytes = await File.ReadAllBytesAsync(source);
            await ThrowsAsync<InvalidOperationException>(() => exporter.ExportAsync(input, [Segment.Create(0, 1)], software, source));
            var target = Path.Combine(root, "existing.mp4");
            await File.WriteAllTextAsync(target, "keep me");
            await ThrowsAsync<IOException>(() => exporter.ExportAsync(input, [Segment.Create(0, 1)], software, target));
            Check(await File.ReadAllTextAsync(target) == "keep me", "Existing destination was modified");
            var sourceAfter = await File.ReadAllBytesAsync(source);
            Check(sourceBytes.SequenceEqual(sourceAfter), "Source was modified");
        });

        await TestAsync("empty timeline and failed FFmpeg runs leave no output", async () =>
        {
            var output = Path.Combine(root, "invalid.mp4");
            await ThrowsAsync<InvalidOperationException>(() => exporter.ExportAsync(input, [], software, output));
            await ThrowsAsync<InvalidOperationException>(() => exporter.ExportAsync(input with { Path = Path.Combine(root, "missing.mp4") },
                [Segment.Create(0, 1)], software, output));
            Check(!File.Exists(output) && !Directory.EnumerateFiles(root, ".clip-*").Any(), "Failed output leaked");
        });
    }
    catch (Exception exception) { Console.Error.WriteLine($"FAIL integration setup: {exception}"); failures++; }
    finally { Directory.Delete(root, recursive: true); }
}

Console.WriteLine($"\n{passed} passed, {failures} failed.");
return failures == 0 ? 0 : 1;

sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
