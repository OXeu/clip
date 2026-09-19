using System.Reflection;
using System.Text.RegularExpressions;

namespace Clip.Core.Updates;

public enum UpdateChannel { Release, Dev }
public enum UpdateStatus { Available, Current, NoPackage, UnknownVersion }

public sealed record AppBuild(string Version, string? Commit = null, long RunNumber = 0, int RunAttempt = 0)
{
    public static AppBuild FromAssembly(Assembly assembly)
    {
        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(item => item.Key, item => item.Value);
        long.TryParse(metadata.GetValueOrDefault("GitHubRunNumber"), out var run);
        int.TryParse(metadata.GetValueOrDefault("GitHubRunAttempt"), out var attempt);
        return new(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
            metadata.GetValueOrDefault("GitHubSha"), run, attempt);
    }
}

public sealed record UpdatePackage(UpdateChannel Channel, string Version, Uri DownloadUrl, string Sha256,
    long Size, Uri PageUrl, string? Commit = null, long RunNumber = 0, int RunAttempt = 0);

public sealed record UpdateCheck(UpdateStatus Status, string Message, UpdatePackage? Package = null);
public sealed record UpdateProgress(string Stage, double? Fraction = null, long Bytes = 0, long? TotalBytes = null);

internal static partial class ReleaseVersion
{
    // Compare stable release versions numerically; a stable version also supersedes its own prerelease.
    public static bool TryParse(string text, out Version version, out bool prerelease)
    {
        version = new Version(0, 0, 0);
        prerelease = false;
        var match = Pattern().Match(text);
        if (!match.Success || !Version.TryParse(match.Groups[1].Value, out var parsed)) return false;
        version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build), Math.Max(0, parsed.Revision));
        prerelease = match.Groups[2].Success;
        return true;
    }

    [GeneratedRegex(@"^[vV]?((?:0|[1-9]\d*)\.(?:0|[1-9]\d*)(?:\.(?:0|[1-9]\d*)){0,2})(-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$")]
    private static partial Regex Pattern();
}
