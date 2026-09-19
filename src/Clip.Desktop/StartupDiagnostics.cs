using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Clip.Desktop;

internal static class StartupDiagnostics
{
    private static readonly object Gate = new();
    internal static string? LogPath { get; private set; }

    internal static void Initialize()
    {
        var directories = new[]
        {
            Environment.GetEnvironmentVariable("CLIP_LOG_DIR"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clip", "logs"),
            Path.Combine(Path.GetTempPath(), "Clip", "logs")
        };
        foreach (var directory in directories)
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            try
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, $"startup-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
                File.WriteAllText(path, $"Clip startup {DateTimeOffset.Now:O}\nOS: {RuntimeInformation.OSDescription}\n" +
                    $"Process architecture: {RuntimeInformation.ProcessArchitecture}; OS architecture: {RuntimeInformation.OSArchitecture}\n" +
                    $"Runtime: {RuntimeInformation.FrameworkDescription}\nApplication directory: {AppContext.BaseDirectory}\n", new UTF8Encoding(false));
                LogPath = path;
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }
    }

    internal static void Write(string message, Exception? exception = null)
    {
        if (LogPath is null) return;
        lock (Gate)
        {
            try { File.AppendAllText(LogPath, $"[{DateTimeOffset.Now:O}] {message}\n{(exception is null ? "" : exception + "\n")}"); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static void ReportFailure(string title, Exception exception, bool automated)
    {
        Write(title, exception);
        if (automated) return;
        // Native MessageBox still works when WPF resource loading itself has failed.
        var detail = exception.GetBaseException().Message;
        if (detail.Length > 1600) detail = detail[..1600];
        var message = $"{detail}\n\n诊断日志：\n{LogPath ?? "日志目录不可写"}\n\n也可运行程序旁的 Start-Clip-Diagnostics.cmd 获取启动报告。";
        try { _ = MessageBoxW(IntPtr.Zero, message, title, 0x10); }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int MessageBoxW(IntPtr window, string text, string caption, uint type);
}
