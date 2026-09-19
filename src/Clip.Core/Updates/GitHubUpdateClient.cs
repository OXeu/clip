using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Clip.Core.Updates;

/// <summary>Checks only this application's Windows workflow and installable x64 ZIPs.</summary>
public sealed class GitHubUpdateClient(HttpClient http)
{
    public const string Repository = "OXeu/clip";
    public const string PackageName = "Clip-win-x64.zip";
    private const string ApiRoot = "https://api.github.com/repos/" + Repository;
    private const string WebRoot = "https://github.com/" + Repository;
    public string? Token { private get; set; }

    public async Task<UpdateCheck> CheckAsync(UpdateChannel channel, AppBuild current, CancellationToken token = default)
    {
        // A missing Release is normal. An inaccessible/private repository must be reported as an error.
        using var repository = await GetJsonAsync(ApiRoot, token);
        if (channel == UpdateChannel.Release) return await CheckReleaseAsync(current, token);
        var branch = repository!.RootElement.GetProperty("default_branch").GetString()
            ?? throw new InvalidDataException("GitHub 未返回默认分支。");
        return await CheckDevAsync(current, branch, token);
    }

    private async Task<UpdateCheck> CheckReleaseAsync(AppBuild current, CancellationToken token)
    {
        using var release = await GetJsonAsync(ApiRoot + "/releases/latest", token, allowMissing: true);
        if (release is null) return new(UpdateStatus.NoPackage, "尚未发布正式版本。");
        var root = release.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean())
            return new(UpdateStatus.NoPackage, "尚未发布正式版本。");
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!ReleaseVersion.TryParse(tag, out var latest, out var preview) || preview)
            throw new InvalidDataException("Release 标签不是受支持的正式版本号：" + tag);
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != PackageName || asset.GetProperty("state").GetString() != "uploaded") continue;
            var id = asset.GetProperty("id").GetInt64();
            var package = new UpdatePackage(UpdateChannel.Release, tag, new($"{ApiRoot}/releases/assets/{id}"),
                ReadDigest(asset), asset.GetProperty("size").GetInt64(), new($"{WebRoot}/releases/tag/{Uri.EscapeDataString(tag)}"));
            if (!ReleaseVersion.TryParse(current.Version, out var installed, out var installedPreview))
                return new(UpdateStatus.UnknownVersion, "无法比较当前版本；可安装最新正式版。", package);
            return latest > installed || (latest == installed && installedPreview)
                ? new(UpdateStatus.Available, $"发现正式版 {tag}。", package)
                : new(UpdateStatus.Current, "当前已是最新正式版本。", package);
        }
        return new(UpdateStatus.NoPackage, $"{tag} 尚未提供 {PackageName} 安装包。");
    }

    private async Task<UpdateCheck> CheckDevAsync(AppBuild current, string branch, CancellationToken token)
    {
        // GitHub caps filtered workflow queries at 1,000 runs. Paginate rather than mistaking a PR for a build.
        for (var page = 1; page <= 10; page++)
        {
            using var document = await GetJsonAsync($"{ApiRoot}/actions/workflows/windows.yml/runs?branch={Uri.EscapeDataString(branch)}&status=success&per_page=100&page={page}", token);
            var runs = document!.RootElement.GetProperty("workflow_runs");
            foreach (var run in runs.EnumerateArray())
            {
                if (run.GetProperty("status").GetString() != "completed" || run.GetProperty("conclusion").GetString() != "success" ||
                    run.GetProperty("head_branch").GetString() != branch ||
                    run.GetProperty("event").GetString() is not ("push" or "workflow_dispatch") ||
                    !string.Equals(run.GetProperty("head_repository").GetProperty("full_name").GetString(), Repository, StringComparison.OrdinalIgnoreCase)) continue;
                var id = run.GetProperty("id").GetInt64();
                using var artifacts = await GetJsonAsync($"{ApiRoot}/actions/runs/{id}/artifacts?name={PackageName}&per_page=100", token);
                foreach (var artifact in artifacts!.RootElement.GetProperty("artifacts").EnumerateArray())
                {
                    if (artifact.GetProperty("name").GetString() != PackageName || artifact.GetProperty("expired").GetBoolean() ||
                        artifact.GetProperty("expires_at").GetDateTimeOffset() <= DateTimeOffset.UtcNow) continue;
                    var number = run.GetProperty("run_number").GetInt64();
                    var attempt = run.GetProperty("run_attempt").GetInt32();
                    var sha = run.GetProperty("head_sha").GetString() ?? "";
                    var artifactId = artifact.GetProperty("id").GetInt64();
                    var package = new UpdatePackage(UpdateChannel.Dev, $"dev #{number}.{attempt} ({sha[..Math.Min(7, sha.Length)]})",
                        new($"{ApiRoot}/actions/artifacts/{artifactId}/zip"), ReadDigest(artifact),
                        artifact.GetProperty("size_in_bytes").GetInt64(), new($"{WebRoot}/actions/runs/{id}"), sha, number, attempt);
                    if (current.RunNumber > 0)
                        return number > current.RunNumber || (number == current.RunNumber && attempt > current.RunAttempt)
                            ? new(UpdateStatus.Available, "发现新的开发构建。", package)
                            : new(UpdateStatus.Current, "当前构建已是最新，或比默认分支构建更新。", package);
                    if (!string.IsNullOrEmpty(current.Commit) && string.Equals(current.Commit, sha, StringComparison.OrdinalIgnoreCase))
                        return new(UpdateStatus.Current, "当前提交与最新开发构建相同。", package);
                    return new(UpdateStatus.UnknownVersion, "找到最新开发构建；当前版本没有可比较的构建编号。", package);
                }
            }
            if (runs.GetArrayLength() < 100) break;
        }
        return new(UpdateStatus.NoPackage, "默认分支暂无成功构建且未过期的 Windows 安装包。");
    }

    private static string ReadDigest(JsonElement item)
    {
        var digest = item.TryGetProperty("digest", out var value) ? value.GetString() : null;
        if (digest is null || !digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 71 ||
            !digest[7..].All(char.IsAsciiHexDigit))
            throw new InvalidDataException("GitHub 未提供有效的 SHA-256 摘要，无法安全校验更新包。");
        return digest[7..];
    }

    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken token, bool allowMissing = false)
    {
        using var response = await SendAsync(new(url), false, token);
        if (allowMissing && response.StatusCode == HttpStatusCode.NotFound) return null;
        EnsureSuccess(response);
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        return await JsonDocument.ParseAsync(stream, cancellationToken: token);
    }

    internal async Task<HttpResponseMessage> SendAsync(Uri uri, bool binary, CancellationToken token)
    {
        // Production HttpClient has redirects disabled: never send a GitHub token to signed blob URLs.
        for (var redirect = 0; redirect < 6; redirect++)
        {
            if (uri.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException("更新地址必须使用 HTTPS。");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("Clip-Updater/1.0");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(binary ? "application/octet-stream" : "application/vnd.github+json"));
            if (uri.Host == "api.github.com")
            {
                request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
                if (!string.IsNullOrWhiteSpace(Token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token.Trim());
            }
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null) throw new InvalidDataException("GitHub 下载跳转缺少地址。");
            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
        }
        throw new HttpRequestException("GitHub 下载跳转次数过多。");
    }

    internal static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "GitHub 令牌无效或已过期。",
            HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests => "GitHub 访问受限或请求过于频繁，请检查令牌权限并稍后重试。",
            HttpStatusCode.NotFound => "无法访问 GitHub 仓库或产物。私有仓库需要 Contents 和 Actions 只读权限的令牌；产物也可能已被删除。",
            HttpStatusCode.Gone => "更新产物已过期，请重新检查更新。",
            _ => $"GitHub 请求失败（HTTP {(int)response.StatusCode}）。"
        };
        throw new HttpRequestException(message, null, response.StatusCode);
    }
}
