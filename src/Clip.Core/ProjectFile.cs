using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Clip.Core;

public sealed record EditProjectSnapshot(VideoTrack[] Tracks, MediaInfo[] Sources);

public sealed record SourceFingerprint(
    string Path, string Name, long Size, long LastModified, string Type, string SampleHash);

public sealed record WorkspaceSnapshot(
    Guid ActiveTrackId, Guid? SelectedTrackId, Guid? SelectedClipId, double Position,
    double PreviewZoom, double TimelineZoom, double TimelineScrollLeft);

public sealed record ClipProjectFile(
    string Format, int Version, long SavedAt, EditProjectSnapshot Project,
    SourceFingerprint[] Sources, WorkspaceSnapshot Workspace);

/// <summary>Portable, media-free project documents shared by the desktop and browser editors.</summary>
public static class ProjectFile
{
    public const string FileExtension = ".clip";
    public const string FormatName = "clip-project";
    public const int CurrentVersion = 1;
    public const int FingerprintSampleBytes = 64 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static string Serialize(ClipProjectFile document)
    {
        Validate(document);
        return JsonSerializer.Serialize(document, JsonOptions);
    }

    public static ClipProjectFile Deserialize(string json)
    {
        ClipProjectFile document;
        try
        {
            document = JsonSerializer.Deserialize<ClipProjectFile>(json, JsonOptions)
                ?? throw new InvalidDataException("剪辑记录文件为空。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("剪辑记录文件格式无效或已损坏。", exception);
        }
        Validate(document);
        return document;
    }

    public static void Validate(ClipProjectFile document)
    {
        if (document.Format != FormatName || document.Version != CurrentVersion)
            throw new InvalidDataException("剪辑记录文件版本不受支持。");
        if (document.SavedAt <= 0 || document.Project is null || document.Sources is null || document.Workspace is null)
            throw new InvalidDataException("剪辑记录文件不完整。");

        _ = EditProject.FromSnapshot(document.Project);
        var expectedPaths = document.Project.Sources.Select(source => source.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (document.Sources.Length != expectedPaths.Count ||
            document.Sources.Any(source => source is null || !expectedPaths.Contains(source.Path) || !ValidFingerprint(source)) ||
            document.Sources.Select(source => source.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != document.Sources.Length)
            throw new InvalidDataException("剪辑记录中的素材校验信息无效。");

        if (!double.IsFinite(document.Workspace.Position) || document.Workspace.Position < 0 ||
            !double.IsFinite(document.Workspace.PreviewZoom) || document.Workspace.PreviewZoom <= 0 ||
            !double.IsFinite(document.Workspace.TimelineZoom) || document.Workspace.TimelineZoom <= 0 ||
            !double.IsFinite(document.Workspace.TimelineScrollLeft) || document.Workspace.TimelineScrollLeft < 0)
            throw new InvalidDataException("剪辑记录中的工作区信息无效。");
    }

    public static bool SameVideo(SourceFingerprint first, SourceFingerprint second) =>
        first.Size == second.Size && string.Equals(first.SampleHash, second.SampleHash, StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads at most three 64 KiB regions so very large source videos stay cheap to identify.</summary>
    public static async Task<SourceFingerprint> FingerprintAsync(
        string filePath, string? projectPath = null, CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(filePath);
        if (!info.Exists) throw new FileNotFoundException("找不到项目使用的原视频。", filePath);
        var chunkSize = (int)Math.Min(FingerprintSampleBytes, info.Length);
        var offsets = new List<long>();
        foreach (var offset in new[]
        {
            0L,
            Math.Max(0, (info.Length - chunkSize) / 2),
            Math.Max(0, info.Length - chunkSize)
        })
            if (!offsets.Contains(offset)) offsets.Add(offset);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            FingerprintSampleBytes, FileOptions.Asynchronous | FileOptions.RandomAccess);
        var buffer = new byte[chunkSize];
        foreach (var offset in offsets)
        {
            stream.Position = offset;
            var read = 0;
            while (read < chunkSize)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(read, chunkSize - read), cancellationToken);
                if (count == 0) break;
                read += count;
            }
            hash.AppendData(buffer, 0, read);
        }

        return new(
            projectPath ?? filePath,
            Path.GetFileName(filePath),
            info.Length,
            new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
            "",
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static bool ValidFingerprint(SourceFingerprint source) =>
        !string.IsNullOrWhiteSpace(source.Path) && !string.IsNullOrWhiteSpace(source.Name) && source.Size >= 0 &&
        source.Type is not null && source.SampleHash is { Length: 64 } && source.SampleHash.All(Uri.IsHexDigit);
}
