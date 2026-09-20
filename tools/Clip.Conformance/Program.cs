// 导出桌面端 BuildFilter / BuildTempoFilter 的真实输出，作为网页版的对照基准。
//
// 网页版把同一套编辑语义移植成 TypeScript（web/src/filtergraph.ts）。这个工具用
// 被测仓库里真正的 C# 实现生成期望值，再由 web/test/conformance.test.ts 逐条比对。
// 基准来自实现本身而不是手抄的常量，所以任一侧语义漂移都会被立刻发现。
//
// 用法：dotnet run --project tools/Clip.Conformance -- <输出文件>

using System.Text.Json;
using Clip.Core;

var output = args.Length > 0 ? args[0] : "conformance.json";
var media = new MediaInfo("source.mp4", 10, 1920, 1080, 30, 0, 1, "h264");

// 与 web/test/conformance.test.ts 中定义的用例必须逐字一致。
var portrait = media with { Path = "portrait.mp4", Width = 1080, Height = 1920, FrameRate = 24 };
var silent = media with { AudioStreamIndex = null };
var indexed = media with { VideoStreamIndex = 2, AudioStreamIndex = 3 };
var offset = media with { VideoTimestampOffset = 1.5 };

VideoClip Clip(MediaInfo m, double start, double end, double speed = 1, string? name = null) =>
    new(Guid.Parse("00000000-0000-0000-0000-000000000001"), m, start, end, speed) { Name = name };

var cases = new List<object>();

void Case(string name, MediaInfo source, Segment[] segments, ExportOptions options)
{
    var clips = segments.Select(s => Clip(source, s.Start, s.End)).ToArray();
    var (width, height) = options.GetDimensions(source);
    cases.Add(new
    {
        name,
        filter = ExportService.BuildFilter(clips, options),
        dimensions = new { width, height }
    });
}

void CaseClips(string name, VideoClip[] clips, ExportOptions options)
{
    var (width, height) = options.GetDimensions(clips[0].Media);
    cases.Add(new
    {
        name,
        filter = ExportService.BuildFilter(clips, options),
        dimensions = new { width, height }
    });
}

Case("single full clip", media, [Segment.Create(0, 10)], new ExportOptions());
Case("trimmed interval", media, [Segment.Create(1.25, 5.5)], new ExportOptions());
Case("two retained intervals", indexed, [Segment.Create(0, 2), Segment.Create(5, 10)], new ExportOptions());
Case("silent source", silent, [Segment.Create(0, 5)], new ExportOptions());
Case("custom portrait dimensions", media, [Segment.Create(0, 5)], new ExportOptions(Width: 720, Height: 1280));
Case("nonstandard stream indexes", indexed, [Segment.Create(1, 4)], new ExportOptions());
Case("timestamp offset", offset, [Segment.Create(0, 3)], new ExportOptions());

CaseClips("speed 2x", [Clip(media, 0, 10, 2)], new ExportOptions());
CaseClips("speed 0.5x", [Clip(media, 0, 10, 0.5)], new ExportOptions());
CaseClips("speed 8x", [Clip(media, 0, 10, 8)], new ExportOptions());
CaseClips("speed 0.1x", [Clip(media, 0, 10, 0.1)], new ExportOptions());
CaseClips("speed 1.37x", [Clip(media, 0, 10, 1.37)], new ExportOptions());
CaseClips("mixed sources and speeds",
    [Clip(silent, 0, 10, 2), Clip(portrait, 0, 10, 0.5)],
    new ExportOptions());
CaseClips("mixed sources with audio",
    [Clip(media, 0, 4, 2), Clip(portrait, 2, 9.5, 0.5)],
    new ExportOptions(Width: 1280, Height: 720));
CaseClips("crossfade-free three cuts",
    [Clip(media, 0, 3), Clip(media, 3, 6), Clip(media, 6, 10)],
    new ExportOptions());
CaseClips("custom size on portrait source",
    [Clip(portrait, 1, 8)],
    new ExportOptions(Width: 640, Height: 640));

var payload = new
{
    // 记录来源，便于排查基准是否过期。
    generatedFrom = "Clip.Core ExportService",
    media = new { width = media.Width, height = media.Height, frameRate = media.FrameRate, duration = media.Duration },
    tempo = new[] { 0.1, 0.25, 0.5, 0.75, 1.0, 1.37, 2.0, 3.0, 4.0, 8.0 }
        .ToDictionary(s => s.ToString(System.Globalization.CultureInfo.InvariantCulture), ExportService.BuildTempoFilter),
    cases
};

var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
File.WriteAllText(output, json);
Console.WriteLine($"wrote {cases.Count} cases to {output}");
