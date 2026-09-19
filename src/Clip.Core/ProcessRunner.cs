using System.Diagnostics;
using System.Text;

namespace Clip.Core;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments,
        CancellationToken cancellationToken = default, Action<string>? onOutput = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        process.Start();
        using var registration = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });
        var stdout = ReadAsync(process.StandardOutput, onOutput);
        var stderr = ReadAsync(process.StandardError, null);
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        var output = await stdout.ConfigureAwait(false);
        var error = await stderr.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new(process.ExitCode, output, error);
    }

    private static async Task<string> ReadAsync(StreamReader reader, Action<string>? callback)
    {
        var result = new StringBuilder();
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            callback?.Invoke(line);
            result.AppendLine(line);
            // Keep useful diagnostics without unbounded memory use on long exports.
            if (result.Length > 512_000) result.Remove(0, 256_000);
        }
        return result.ToString();
    }
}
