using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Clip.Core.Updates;

internal static class WindowsUpdateSmoke
{
    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetWindowLongW(IntPtr window, int index);

    // Runs only on Windows CI against the actual self-contained application and verified ZIP.
    internal static async Task<int> RunAsync(string publishedDirectory, string archivePath)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows update smoke requires Windows.");
        var published = Path.GetFullPath(publishedDirectory);
        var archive = Path.GetFullPath(archivePath);
        var root = Path.Combine(Path.GetDirectoryName(published)!, "update-smoke-" + Guid.NewGuid().ToString("N"));
        var installed = Path.Combine(root, "Clip's installation [测试]");
        var logs = Path.Combine(root, "logs");
        Directory.CreateDirectory(logs);
        Process? original = null;
        Process? helper = null;
        Process? restarted = null;
        try
        {
            foreach (var source in Directory.EnumerateFiles(published, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(installed, Path.GetRelativePath(published, source));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination);
            }
            var canary = Path.Combine(installed, "README.md");
            File.WriteAllText(canary, "old release canary");
            var progress = new InlineProgress<UpdateProgress>(value => File.AppendAllText(Path.Combine(logs, "preparation.log"), $"{value.Stage} {value.Fraction:P0}\n"));
            using var http = new HttpClient(new ArchiveHandler(archive));
            string digest;
            await using (var input = File.OpenRead(archive)) digest = Convert.ToHexString(await SHA256.HashDataAsync(input));
            var package = new UpdatePackage(UpdateChannel.Dev, "smoke", new("https://api.github.com/smoke"), digest,
                new FileInfo(archive).Length, new("https://github.com/OXeu/clip"));
            var prepared = await new UpdateInstaller(new(http)).PrepareAsync(package, installed, progress);
            UpdateInstaller.PrepareRunner(prepared);

            ProcessStartInfo StartInfo(string executable)
            {
                var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = installed };
                start.Environment["CLIP_LOG_DIR"] = logs;
                start.Environment.Remove("DOTNET_HOST_TRACE");
                start.Environment.Remove("DOTNET_HOST_TRACEFILE");
                return start;
            }
            original = Process.Start(StartInfo(Path.Combine(installed, "Clip.exe"))) ?? throw new Exception("Original application did not start.");
            await WaitUntilAsync(() => IsInitialized(original), "Original application did not initialize.");
            var start = StartInfo(Path.Combine(prepared.Workspace, "runner", "Clip.exe"));
            start.ArgumentList.Add("--apply-update");
            start.ArgumentList.Add(prepared.ManifestPath);
            start.ArgumentList.Add(original.Id.ToString(CultureInfo.InvariantCulture));
            start.ArgumentList.Add(original.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
            helper = Process.Start(start) ?? throw new Exception("Updater did not start.");
            await WaitUntilAsync(() => { helper.Refresh(); return !helper.HasExited && helper.MainWindowHandle != IntPtr.Zero; }, "Updater progress window did not open.");
            if (helper.MainWindowTitle != "正在更新" || (GetWindowLongW(helper.MainWindowHandle, -20) & 1) == 0)
                throw new Exception("Updater caption retained its application title or icon.");
            if (File.ReadAllText(canary) != "old release canary") throw new Exception("Updater modified files before the old process exited.");
            original.CloseMainWindow();
            await original.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90));
            if (helper.ExitCode != 0) throw new Exception("Updater failed with exit code " + helper.ExitCode);
            if (File.ReadAllText(canary) != File.ReadAllText(Path.Combine(published, "README.md"))) throw new Exception("New package was not installed.");
            if (File.ReadAllText(Path.Combine(prepared.Workspace, "backup", "README.md")) != "old release canary") throw new Exception("Old release backup missing.");

            await WaitUntilAsync(() =>
            {
                foreach (var candidate in Process.GetProcessesByName("Clip"))
                {
                    try
                    {
                        if (!candidate.HasExited && candidate.Id != original.Id &&
                            string.Equals(candidate.MainModule?.FileName, Path.Combine(installed, "Clip.exe"), StringComparison.OrdinalIgnoreCase))
                        {
                            restarted?.Dispose();
                            restarted = candidate;
                            return IsInitialized(candidate);
                        }
                    }
                    catch (InvalidOperationException) { }
                    if (candidate != restarted) candidate.Dispose();
                }
                return false;
            }, "Updated application did not automatically restart and initialize.");
            restarted!.CloseMainWindow();
            await restarted.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            File.WriteAllText(Path.Combine(logs, "success.txt"), "Real updater window, parent exit, replacement, backup, automatic restart and WPF initialization passed.");
            Console.WriteLine("PASS Windows updater, progress window, in-place replacement, and automatic restart");
            return 0;

            bool IsInitialized(Process process)
            {
                process.Refresh();
                if (process.HasExited) throw new Exception("Clip exited before initialization.");
                var log = Directory.EnumerateFiles(logs, $"startup-*-{process.Id}.log").SingleOrDefault();
                return process.MainWindowHandle != IntPtr.Zero && log is not null && HasInitializationCompleted(log);
            }
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(logs, "failure.txt"), error.ToString());
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            foreach (var process in new[] { original, helper, restarted })
            {
                if (process is null) continue;
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                process.Dispose();
            }
            // Keep this isolated directory, including application logs and backups, for diagnosis.
        }
    }

    internal static bool HasInitializationCompleted(string log)
    {
        try
        {
            // StartupDiagnostics may still be appending; the reader must also allow future writes.
            using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Contains("Main window initialization completed", StringComparison.Ordinal);
        }
        catch (FileNotFoundException) { return false; }
        catch (IOException error) when ((error.HResult & 0xFFFF) is 32 or 33)
        {
            // Windows sharing/lock violations are transient: let the existing bounded poll retry.
            return false;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, string failure)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(50))
        {
            if (predicate()) return;
            await Task.Delay(200);
        }
        throw new TimeoutException(failure);
    }

    private sealed class ArchiveHandler(string archive) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(File.OpenRead(archive)) });
    }
}
