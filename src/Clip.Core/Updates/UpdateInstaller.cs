using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Clip.Core.Updates;

public sealed record UpdateFile(string Path, long Length, string Sha256);
public sealed record PreparedUpdate(string TargetDirectory, string Workspace, IReadOnlyList<UpdateFile> Files)
{
    public string ManifestPath => System.IO.Path.Combine(Workspace, "update.json");
    public string PayloadDirectory => System.IO.Path.Combine(Workspace, "payload");
}

/// <summary>Stages beside the installed executable. Only an independent process may apply the update.</summary>
public sealed class UpdateInstaller(GitHubUpdateClient github)
{
    private const long MaxPackageBytes = 2L * 1024 * 1024 * 1024;
    private const long MaxExpandedBytes = 4L * 1024 * 1024 * 1024;
    private static readonly string[] RequiredFiles =
        ["Clip.exe", "Clip.dll", "Clip.deps.json", "Clip.runtimeconfig.json", "Clip.Core.dll", "ffmpeg/ffmpeg.exe", "ffmpeg/ffprobe.exe"];

    public async Task<PreparedUpdate> PrepareAsync(UpdatePackage package, string applicationDirectory,
        IProgress<UpdateProgress>? progress = null, CancellationToken token = default)
    {
        var target = Path.GetFullPath(applicationDirectory);
        RejectLinks(target);
        var parent = Path.Combine(target, ".clip-updates");
        RejectLinks(parent);
        var workspace = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace); // Fails before downloading when the installation is not writable.
        try
        {
            var archive = Path.Combine(workspace, GitHubUpdateClient.PackageName);
            await DownloadAsync(package, archive, progress, token);
            var files = await ExtractAsync(archive, Path.Combine(workspace, "payload"), progress, token);
            var prepared = new PreparedUpdate(target, workspace, files);
            await File.WriteAllTextAsync(prepared.ManifestPath, JsonSerializer.Serialize(prepared), token);
            return prepared;
        }
        catch
        {
            // This GUID directory was created by this attempt and has not been used for installation.
            try { Directory.Delete(workspace, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    private async Task DownloadAsync(UpdatePackage package, string path, IProgress<UpdateProgress>? progress, CancellationToken token)
    {
        if (package.Size <= 0 || package.Size > MaxPackageBytes || package.Sha256.Length != 64 || !package.Sha256.All(char.IsAsciiHexDigit))
            throw new InvalidDataException("更新包大小或 SHA-256 摘要无效。");
        progress?.Report(new("正在下载更新", 0, 0, package.Size));
        using var response = await github.SendAsync(package.DownloadUrl, true, token);
        GitHubUpdateClient.EnsureSuccess(response);
        if (response.Content.Headers.ContentLength is { } length && length != package.Size)
            throw new InvalidDataException("更新包下载大小与 GitHub 元数据不一致。");
        await using var input = await response.Content.ReadAsStreamAsync(token);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long received = 0;
        var lastReport = Environment.TickCount64;
        while (true)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
            idle.CancelAfter(TimeSpan.FromSeconds(30));
            var count = await input.ReadAsync(buffer, idle.Token);
            if (count == 0) break;
            received += count;
            if (received > package.Size) throw new InvalidDataException("更新包超出预期大小。");
            digest.AppendData(buffer, 0, count);
            await output.WriteAsync(buffer.AsMemory(0, count), token);
            if (Environment.TickCount64 - lastReport >= 100)
            {
                progress?.Report(new("正在下载更新", (double)received / package.Size, received, package.Size));
                lastReport = Environment.TickCount64;
            }
        }
        progress?.Report(new("正在校验 SHA-256", 1, received, package.Size));
        if (received != package.Size || !string.Equals(Convert.ToHexString(digest.GetHashAndReset()), package.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新包不完整或 SHA-256 校验失败，请重新下载。");
    }

    private static async Task<IReadOnlyList<UpdateFile>> ExtractAsync(string archivePath, string destination,
        IProgress<UpdateProgress>? progress, CancellationToken token)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > 10000) throw new InvalidDataException("更新包文件数量超出限制。");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/').TrimEnd('/');
            ValidateRelativePath(name);
            if (!names.Add(name) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 ||
                (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("更新包包含重复路径或链接。");
            expanded = checked(expanded + entry.Length);
            if (expanded > MaxExpandedBytes) throw new InvalidDataException("更新包解压大小超出限制。");
        }
        foreach (var required in RequiredFiles)
            if (!archive.Entries.Any(entry => entry.FullName.Replace('\\', '/') == required && entry.Length > 0))
                throw new InvalidDataException("更新包缺少完整 Windows 分发文件：" + required);
        Directory.CreateDirectory(destination);
        var files = new List<UpdateFile>();
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
            var name = entry.FullName.Replace('\\', '/');
            var path = Path.Combine(destination, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (var input = entry.Open())
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long written = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, token)) > 0)
                {
                    written += count;
                    if (written > entry.Length) throw new InvalidDataException("ZIP 文件超出声明的解压大小。");
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                }
                if (written != entry.Length) throw new InvalidDataException("ZIP 文件解压不完整。");
            }
            files.Add(new(name, entry.Length, await HashFileAsync(path, token)));
            progress?.Report(new("正在解压更新", (double)files.Count / archive.Entries.Count));
        }
        return files;
    }

    public static PreparedUpdate LoadPrepared(string manifestPath)
    {
        var prepared = JsonSerializer.Deserialize<PreparedUpdate>(File.ReadAllText(manifestPath))
            ?? throw new InvalidDataException("更新清单为空。");
        ValidatePrepared(prepared);
        if (!Path.GetFullPath(manifestPath).Equals(prepared.ManifestPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新清单位置无效。");
        return prepared;
    }

    public static async Task ApplyAsync(PreparedUpdate prepared, IProgress<UpdateProgress>? progress = null, Action? restart = null)
    {
        ValidatePrepared(prepared);
        // Serialize separate Clip instances updating the same installation.
        var lockPath = Path.Combine(prepared.TargetDirectory, ".clip-update.lock");
        RejectLinks(lockPath);
        using var updateLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var backup = Path.Combine(prepared.Workspace, "backup");
        if (Directory.Exists(backup)) throw new IOException("此更新已有备份，不能重复安装。请重新检查更新。");
        // Verify every staged byte and every destination before touching the installed application.
        foreach (var file in prepared.Files)
        {
            var source = Path.Combine(prepared.PayloadDirectory, file.Path);
            RejectLinks(source);
            RejectLinks(Path.Combine(prepared.TargetDirectory, file.Path));
            if (new FileInfo(source).Length != file.Length ||
                !string.Equals(await HashFileAsync(source, CancellationToken.None), file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("暂存文件校验失败：" + file.Path);
        }
        Directory.CreateDirectory(backup);
        var changed = new List<(string Target, string Backup, bool Existed)>();
        try
        {
            foreach (var file in prepared.Files)
            {
                var target = Path.Combine(prepared.TargetDirectory, file.Path);
                var saved = Path.Combine(backup, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
                var existed = File.Exists(target);
                if (existed) File.Move(target, saved);
                changed.Add((target, saved, existed));
                File.Copy(Path.Combine(prepared.PayloadDirectory, file.Path), target);
                progress?.Report(new("正在替换文件", (double)changed.Count / prepared.Files.Count));
            }
            progress?.Report(new("更新完成，正在重启", 1));
            restart?.Invoke();
        }
        catch (Exception installError)
        {
            progress?.Report(new("更新失败，正在恢复原版本"));
            var errors = new List<Exception> { installError };
            foreach (var item in changed.AsEnumerable().Reverse())
            {
                try
                {
                    if (File.Exists(item.Target)) File.Delete(item.Target);
                    if (item.Existed) File.Move(item.Backup, item.Target);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { errors.Add(error); }
            }
            if (errors.Count > 1) throw new AggregateException("更新与恢复失败。请保留备份目录：" + backup, errors);
            throw new IOException("更新失败，已恢复原版本。" + installError.Message, installError);
        }
    }

    public static void PrepareRunner(PreparedUpdate prepared, CancellationToken token = default)
    {
        ValidatePrepared(prepared);
        var runner = Path.Combine(prepared.Workspace, "runner");
        RejectLinks(runner);
        Directory.CreateDirectory(runner);
        // Copy the installed updater and its self-contained runtime, never execute a downloaded helper.
        foreach (var file in Directory.EnumerateFiles(prepared.TargetDirectory))
        {
            token.ThrowIfCancellationRequested();
            if (Path.GetExtension(file).ToLowerInvariant() is not (".exe" or ".dll" or ".json" or ".config")) continue;
            RejectLinks(file);
            File.Copy(file, Path.Combine(runner, Path.GetFileName(file)), false);
        }
        if (!File.Exists(Path.Combine(runner, "Clip.exe"))) throw new IOException("更新只支持完整 Windows 发布包。");
    }

    private static void ValidatePrepared(PreparedUpdate prepared)
    {
        var target = Path.GetFullPath(prepared.TargetDirectory);
        var workspace = Path.GetFullPath(prepared.Workspace);
        var parent = Path.Combine(target, ".clip-updates");
        if (target != prepared.TargetDirectory || workspace != prepared.Workspace ||
            !string.Equals(Path.GetDirectoryName(workspace), parent, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(workspace), "N", out _) || prepared.Files.Count == 0)
            throw new InvalidDataException("更新目标或暂存目录无效。");
        RejectLinks(workspace);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in prepared.Files)
        {
            ValidateRelativePath(file.Path);
            if (!names.Add(file.Path) || file.Length < 0 || file.Sha256.Length != 64 || !file.Sha256.All(char.IsAsciiHexDigit))
                throw new InvalidDataException("更新文件清单无效。");
        }
        if (RequiredFiles.Any(required => !names.Contains(required))) throw new InvalidDataException("更新文件清单不完整。");
    }

    private static void ValidateRelativePath(string name)
    {
        var parts = name.Split('/');
        if (Path.IsPathRooted(name) || parts[0].Equals(".clip-updates", StringComparison.OrdinalIgnoreCase) ||
            parts[0].Equals(".clip-update.lock", StringComparison.OrdinalIgnoreCase) ||
            parts.Any(part => part is "" or "." or ".." || part.EndsWith(' ') || part.EndsWith('.') ||
                part.Any(character => character < 32 || "<>:\"\\|?*".Contains(character)) || IsDeviceName(part)))
            throw new InvalidDataException("更新包包含不安全的路径：" + name);
    }

    private static bool IsDeviceName(string part)
    {
        var stem = part.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '1' and <= '9');
    }

    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("更新路径不能包含符号链接或目录联接：" + current);
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
    }
}
