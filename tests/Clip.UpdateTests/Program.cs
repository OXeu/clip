using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Clip.Core.Updates;

if (args is ["--windows-smoke", var published, var archive])
    return await WindowsUpdateSmoke.RunAsync(published, archive);

var failed = 0;
var passed = 0;
const string Api = "https://api.github.com/repos/OXeu/clip";
const string Digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
var temp = Path.Combine(Path.GetTempPath(), "clip-update-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);

void Check(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
async Task Test(string name, Func<Task> run)
{
    try { await run(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception error) { failed++; Console.Error.WriteLine($"FAIL {name}: {error}"); }
}
async Task Throws<T>(Func<Task> run) where T : Exception
{
    try { await run(); }
    catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) };
object Asset(string name = "Clip-win-x64.zip", string? digest = Digest) => new { id = 5, name, digest, size = 100, state = "uploaded" };
object Release(string tag = "v1.2.0", object[]? assets = null, bool prerelease = false, bool draft = false) =>
    new { tag_name = tag, assets = assets ?? [Asset()], prerelease, draft };
object Run(long number = 11, int attempt = 1, string sha = "newcommit", string branch = "master", string trigger = "push", string conclusion = "success", string repo = "OXeu/clip") =>
    new { id = number + 100, run_number = number, run_attempt = attempt, head_sha = sha, head_branch = branch, @event = trigger,
        status = "completed", conclusion, head_repository = new { full_name = repo } };
object Artifact(bool expired = false, string name = "Clip-win-x64.zip", DateTimeOffset? expires = null) =>
    new { id = 7, name, expired, expires_at = expires ?? DateTimeOffset.UtcNow.AddDays(1), digest = Digest, size_in_bytes = 100 };
MockHandler Handler(object? release = null, object[]? runs = null, object[]? artifacts = null) => new(request =>
{
    var uri = request.RequestUri!;
    if (uri.AbsoluteUri == Api) return Json(new { default_branch = "master" });
    if (uri.AbsolutePath.EndsWith("/releases/latest")) return release is null ? new(HttpStatusCode.NotFound) : Json(release);
    if (uri.AbsolutePath.EndsWith("windows.yml/runs")) return Json(new { workflow_runs = runs ?? Array.Empty<object>() });
    if (uri.AbsolutePath.EndsWith("/artifacts")) return Json(new { artifacts = artifacts ?? Array.Empty<object>() });
    throw new Exception("Unexpected request: " + uri);
});
async Task<UpdateCheck> GetCheck(MockHandler handler, AppBuild? build = null, UpdateChannel channel = UpdateChannel.Release)
{
    using var http = new HttpClient(handler);
    return await new GitHubUpdateClient(http).CheckAsync(channel, build ?? new("1.0.0"));
}
byte[] Zip(Dictionary<string, string>? extra = null, string? omit = null)
{
    var files = new Dictionary<string, string>
    {
        ["Clip.exe"] = "new exe", ["Clip.dll"] = "new app", ["Clip.deps.json"] = "{}", ["Clip.runtimeconfig.json"] = "{}",
        ["Clip.Core.dll"] = "new core", ["ffmpeg/ffmpeg.exe"] = "new ffmpeg", ["ffmpeg/ffprobe.exe"] = "new probe"
    };
    if (extra is not null) foreach (var entry in extra) files[entry.Key] = entry.Value;
    if (omit is not null) files.Remove(omit);
    using var bytes = new MemoryStream();
    using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        foreach (var file in files)
        {
            using var output = new StreamWriter(zip.CreateEntry(file.Key).Open());
            output.Write(file.Value);
        }
    return bytes.ToArray();
}
UpdatePackage Package(byte[] bytes, string? digest = null) => new(UpdateChannel.Dev, "dev #11", new(Api + "/actions/artifacts/7/zip"),
    digest ?? Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length, new("https://github.com/OXeu/clip/actions/runs/111"));
string InstallDirectory()
{
    var directory = Path.Combine(temp, Guid.NewGuid().ToString("N"), "应用 Clip's files [test]");
    Directory.CreateDirectory(directory);
    File.WriteAllText(Path.Combine(directory, "Clip.exe"), "old exe");
    File.WriteAllText(Path.Combine(directory, "notes.txt"), "keep user file");
    return directory;
}
async Task<PreparedUpdate> Prepare(byte[] bytes, string directory, string? digest = null, IProgress<UpdateProgress>? progress = null, CancellationToken token = default)
{
    using var http = new HttpClient(new MockHandler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
    return await new UpdateInstaller(new(http)).PrepareAsync(Package(bytes, digest), directory, progress, token);
}

try
{
    await Test("stable release comparison is numeric and ignores build metadata", async () =>
    {
        foreach (var (tag, current, expected) in new[] {
            ("v1.10.0", "1.9.0", UpdateStatus.Available), ("v1.2.0", "1.2.0+abc", UpdateStatus.Current),
            ("v1.2.0", "1.2.0-rc.1+abc", UpdateStatus.Available), ("v1.2.0", "2.0.0", UpdateStatus.Current),
            ("v1.2.0", "1.2.0.0", UpdateStatus.Current), ("v1.2.0", "unknown", UpdateStatus.UnknownVersion) })
            Check((await GetCheck(Handler(Release(tag)), new(current))).Status == expected, $"{tag} vs {current}");
    });
    await Test("release requires an uploaded x64 ZIP and a non-prerelease", async () =>
    {
        Check((await GetCheck(Handler(Release(assets: [Asset("source.zip")])))).Status == UpdateStatus.NoPackage);
        Check((await GetCheck(Handler(Release(prerelease: true)))).Status == UpdateStatus.NoPackage);
        Check((await GetCheck(Handler(Release(draft: true)))).Status == UpdateStatus.NoPackage);
        await Throws<InvalidDataException>(() => GetCheck(Handler(Release("nightly"))));
        await Throws<InvalidDataException>(() => GetCheck(Handler(Release(assets: [Asset(digest: null)]))));
    });
    await Test("empty Releases differ from inaccessible private repository", async () =>
    {
        Check((await GetCheck(Handler())).Status == UpdateStatus.NoPackage);
        await Throws<HttpRequestException>(() => GetCheck(new MockHandler(_ => new(HttpStatusCode.NotFound))));
    });
    await Test("GitHub headers, token, release asset endpoint, and auth errors", async () =>
    {
        using var handler = Handler(Release());
        using var http = new HttpClient(handler);
        var result = await new GitHubUpdateClient(http) { Token = "secret-for-test" }.CheckAsync(UpdateChannel.Release, new("1.0.0"));
        Check(handler.Requests.All(item => item.Token == "secret-for-test" && item.Agent.Contains("Clip-Updater") && item.ApiVersion == "2022-11-28"));
        Check(result.Package!.DownloadUrl.AbsoluteUri == Api + "/releases/assets/5");
        foreach (var code in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests, HttpStatusCode.InternalServerError })
            await Throws<HttpRequestException>(() => GetCheck(new MockHandler(_ => new(code))));
    });
    await Test("dev ignores failures, PRs, forks, other branches, and expired artifacts", async () =>
    {
        var runs = new[] { Run(20, trigger: "pull_request"), Run(19, conclusion: "failure"), Run(18, branch: "topic"), Run(17, repo: "fork/clip"), Run(16), Run(15) };
        using var handler = Handler(runs: runs, artifacts: [Artifact(expired: true), Artifact(name: "checksums"), Artifact(expires: DateTimeOffset.UtcNow.AddDays(-1))]);
        Check((await GetCheck(handler, channel: UpdateChannel.Dev)).Status == UpdateStatus.NoPackage);
        Check(handler.Requests.Count(item => item.Url.Contains("/artifacts?")) == 2);
        Check(handler.Requests.Any(item => item.Url.Contains("branch=master&status=success")));
    });
    await Test("dev compares run number and rerun attempt, never offers older builds", async () =>
    {
        foreach (var (number, attempt, expected) in new[] { (10L, 1, UpdateStatus.Available), (11L, 1, UpdateStatus.Current), (12L, 1, UpdateStatus.Current) })
            Check((await GetCheck(Handler(runs: [Run()], artifacts: [Artifact()]), new("1.0.0", RunNumber: number, RunAttempt: attempt), UpdateChannel.Dev)).Status == expected);
        Check((await GetCheck(Handler(runs: [Run(attempt: 2)], artifacts: [Artifact()]), new("1.0.0", RunNumber: 11, RunAttempt: 1), UpdateChannel.Dev)).Status == UpdateStatus.Available);
        Check((await GetCheck(Handler(runs: [Run()], artifacts: [Artifact()]), new("1.0.0", "newcommit"), UpdateChannel.Dev)).Status == UpdateStatus.Current);
        Check((await GetCheck(Handler(runs: [Run()], artifacts: [Artifact()]), channel: UpdateChannel.Dev)).Status == UpdateStatus.UnknownVersion);
    });
    await Test("dev falls back when the latest successful run has no installable artifact", async () =>
    {
        using var defaults = Handler(runs: [Run(12), Run(11)], artifacts: [Artifact()]);
        using var handler = new MockHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/runs/112/artifacts")
            ? Json(new { artifacts = Array.Empty<object>() }) : defaults.Respond(request));
        Check((await GetCheck(handler, new("1.0.0", RunNumber: 10), UpdateChannel.Dev)).Package?.RunNumber == 11);
    });
    await Test("dev paginates workflow runs", async () =>
    {
        using var defaults = Handler(artifacts: [Artifact()]);
        using var handler = new MockHandler(request => request.RequestUri!.AbsolutePath.EndsWith("windows.yml/runs")
            ? Json(new { workflow_runs = request.RequestUri.Query.EndsWith("&page=1", StringComparison.Ordinal)
                ? Enumerable.Range(1, 100).Select(_ => Run(trigger: "pull_request")).ToArray() : new[] { Run() } }) : defaults.Respond(request));
        Check((await GetCheck(handler, new("1.0.0", RunNumber: 10), UpdateChannel.Dev)).Status == UpdateStatus.Available);
        Check(handler.Requests.Any(item => item.Url.EndsWith("page=2")));
    });
    await Test("check cancellation and malformed responses propagate without claiming current", async () =>
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var http = new HttpClient(Handler(Release()));
        await Throws<OperationCanceledException>(() => new GitHubUpdateClient(http).CheckAsync(UpdateChannel.Release, new("1.0.0"), cancellation.Token));
        await Throws<JsonException>(() => GetCheck(new MockHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent("not json") })));
    });
    await Test("download redirect strips token, reports progress, stages beside app, and applies before restart", async () =>
    {
        var bytes = Zip();
        var directory = InstallDirectory();
        var events = new List<UpdateProgress>();
        var progress = new InlineProgress<UpdateProgress>(events.Add);
        using var handler = new MockHandler(request => request.RequestUri!.Host == "api.github.com"
            ? new(HttpStatusCode.Found) { Headers = { Location = new Uri("https://blob.example.test/download") } }
            : new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        using var http = new HttpClient(handler);
        var prepared = await new UpdateInstaller(new(http) { Token = "private-token" }).PrepareAsync(Package(bytes), directory, progress);
        Check(handler.Requests[0].Token == "private-token" && handler.Requests[1].Token is null, "Token leaked to blob storage");
        Check(prepared.Workspace.StartsWith(Path.Combine(directory, ".clip-updates") + Path.DirectorySeparatorChar));
        Check(File.ReadAllText(Path.Combine(directory, "Clip.exe")) == "old exe", "Installed files changed while preparing");
        Check(events.Any(item => item.Stage.Contains("下载")) && events.Any(item => item.Stage.Contains("校验")) && events.Any(item => item.Stage.Contains("解压")));
        var loaded = UpdateInstaller.LoadPrepared(prepared.ManifestPath);
        var restarted = false;
        await UpdateInstaller.ApplyAsync(loaded, progress, () => { Check(File.ReadAllText(Path.Combine(directory, "Clip.exe")) == "new exe"); restarted = true; });
        Check(restarted && File.ReadAllText(Path.Combine(directory, "notes.txt")) == "keep user file");
        Check(File.ReadAllText(Path.Combine(prepared.Workspace, "backup", "Clip.exe")) == "old exe");
    });
    await Test("SHA mismatch and cancellation leave installed bytes untouched and remove partial staging", async () =>
    {
        var directory = InstallDirectory();
        await Throws<InvalidDataException>(() => Prepare(Zip(), directory, new string('0', 64)));
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<UpdateProgress>(_ => cancellation.Cancel());
        await Throws<OperationCanceledException>(() => Prepare(Zip(), directory, progress: progress, token: cancellation.Token));
        Check(File.ReadAllText(Path.Combine(directory, "Clip.exe")) == "old exe");
        Check(!Directory.EnumerateDirectories(Path.Combine(directory, ".clip-updates")).Any());
    });
    await Test("truncated and oversized responses fail before extraction", async () =>
    {
        var bytes = Zip();
        foreach (var size in new[] { bytes.Length - 1, bytes.Length + 1 })
        {
            using var http = new HttpClient(new MockHandler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
            await Throws<InvalidDataException>(() => new UpdateInstaller(new(http)).PrepareAsync(Package(bytes) with { Size = size }, InstallDirectory()));
        }
    });
    await Test("download rejects insecure redirects and stale artifacts", async () =>
    {
        using var http = new HttpClient(new MockHandler(_ => new(HttpStatusCode.Found) { Headers = { Location = new Uri("http://example.test/unsafe") } }));
        await Throws<InvalidDataException>(() => new UpdateInstaller(new(http)).PrepareAsync(Package(Zip()), InstallDirectory()));
        using var gone = new HttpClient(new MockHandler(_ => new(HttpStatusCode.Gone)));
        await Throws<HttpRequestException>(() => new UpdateInstaller(new(gone)).PrepareAsync(Package(Zip()), InstallDirectory()));
    });
    await Test("ZIP rejects traversal, absolute paths, ADS, device names, and case collisions", async () =>
    {
        foreach (var path in new[] { "../escape.txt", "/absolute.txt", "C:/evil.txt", "dir/../../escape.txt", "file:stream", "NUL.txt", "dir/COM1", "trailing. ", ".clip-updates/payload", "clip.EXE" })
            await Throws<InvalidDataException>(() => Prepare(Zip(new() { [path] = "evil" }), InstallDirectory()));
    });
    await Test("ZIP rejects symlinks, incomplete packages, and non-archives", async () =>
    {
        var bytes = Zip(new() { ["link"] = "../elsewhere" });
        using var stream = new MemoryStream();
        stream.Write(bytes);
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Update, true)) archive.GetEntry("link")!.ExternalAttributes = 0xA1FF << 16;
        await Throws<InvalidDataException>(() => Prepare(stream.ToArray(), InstallDirectory()));
        await Throws<InvalidDataException>(() => Prepare(Zip(omit: "ffmpeg/ffprobe.exe"), InstallDirectory()));
        await Throws<InvalidDataException>(() => Prepare(Encoding.UTF8.GetBytes("not a zip"), InstallDirectory()));
    });
    await Test("tampered staging and manifest path escapes are rejected before mutation", async () =>
    {
        var directory = InstallDirectory();
        var prepared = await Prepare(Zip(), directory);
        File.WriteAllText(Path.Combine(prepared.PayloadDirectory, "Clip.exe"), "tampered");
        await Throws<InvalidDataException>(() => UpdateInstaller.ApplyAsync(prepared));
        Check(File.ReadAllText(Path.Combine(directory, "Clip.exe")) == "old exe");
        var files = prepared.Files.Append(new UpdateFile("../escape.txt", 1, new string('a', 64))).ToArray();
        await Throws<InvalidDataException>(() => UpdateInstaller.ApplyAsync(prepared with { Files = files }));
        await Throws<InvalidDataException>(() => UpdateInstaller.ApplyAsync(prepared with { TargetDirectory = temp }));
    });
    await Test("mid-install failure restores overwritten files and removes newly installed files", async () =>
    {
        var directory = InstallDirectory();
        var prepared = await Prepare(Zip(), directory);
        Directory.CreateDirectory(Path.Combine(directory, "ffmpeg", "ffprobe.exe")); // Last replacement fails.
        await Throws<IOException>(() => UpdateInstaller.ApplyAsync(prepared));
        Check(File.ReadAllText(Path.Combine(directory, "Clip.exe")) == "old exe");
        Check(!File.Exists(Path.Combine(directory, "Clip.dll")) && !File.Exists(Path.Combine(directory, "ffmpeg", "ffmpeg.exe")));
        Check(File.ReadAllText(Path.Combine(directory, "notes.txt")) == "keep user file");
    });
    await Test("restart failure rolls back and the same preparation cannot apply twice", async () =>
    {
        var directory = InstallDirectory();
        var prepared = await Prepare(Zip(), directory);
        await Throws<IOException>(() => UpdateInstaller.ApplyAsync(prepared, restart: () => throw new IOException("Launch failed")));
        Check(File.ReadAllText(Path.Combine(directory, "Clip.exe")) == "old exe");
        await Throws<IOException>(() => UpdateInstaller.ApplyAsync(prepared));
    });
    await Test("another installer lock prevents any replacement", async () =>
    {
        var directory = InstallDirectory();
        var prepared = await Prepare(Zip(), directory);
        using var updateLock = new FileStream(Path.Combine(directory, ".clip-update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        await Throws<IOException>(() => UpdateInstaller.ApplyAsync(prepared));
        Check(File.ReadAllText(Path.Combine(directory, "Clip.exe")) == "old exe");
    });
    await Test("runner uses the installed app, not the downloaded executable", async () =>
    {
        var directory = InstallDirectory();
        var prepared = await Prepare(Zip(), directory);
        UpdateInstaller.PrepareRunner(prepared);
        Check(File.ReadAllText(Path.Combine(prepared.Workspace, "runner", "Clip.exe")) == "old exe");
        Check(!File.Exists(Path.Combine(prepared.Workspace, "runner", "notes.txt")));
    });
    if (!OperatingSystem.IsWindows())
        await Test("destination symlinks cannot redirect writes outside application directory", async () =>
        {
            var directory = InstallDirectory();
            var prepared = await Prepare(Zip(), directory);
            var outside = Path.Combine(temp, "outside");
            Directory.CreateDirectory(outside);
            Directory.CreateSymbolicLink(Path.Combine(directory, "ffmpeg"), outside);
            await Throws<IOException>(() => UpdateInstaller.ApplyAsync(prepared));
            Check(!Directory.EnumerateFiles(outside).Any());
            Check(File.ReadAllText(Path.Combine(directory, "Clip.exe")) == "old exe");
        });
}
finally { Directory.Delete(temp, true); }
Console.WriteLine($"\n{passed} passed, {failed} failed.");
return failed == 0 ? 0 : 1;

sealed class MockHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(string Url, string? Token, string Agent, string? ApiVersion)> Requests { get; } = [];
    public HttpResponseMessage Respond(HttpRequestMessage request) => respond(request);
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add((request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.Parameter, request.Headers.UserAgent.ToString(),
            request.Headers.TryGetValues("X-GitHub-Api-Version", out var values) ? values.Single() : null));
        return Task.FromResult(respond(request));
    }
}

sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
