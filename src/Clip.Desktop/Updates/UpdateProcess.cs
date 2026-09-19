using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Clip.Core.Updates;

namespace Clip.Desktop.Updates;

internal static class UpdateProcess
{
    internal static void Start(PreparedUpdate prepared)
    {
        using var current = Process.GetCurrentProcess();
        var start = new ProcessStartInfo(Path.Combine(prepared.Workspace, "runner", "Clip.exe"))
        {
            UseShellExecute = false,
            WorkingDirectory = prepared.TargetDirectory
        };
        start.ArgumentList.Add("--apply-update");
        start.ArgumentList.Add(prepared.ManifestPath);
        start.ArgumentList.Add(current.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add(current.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var helper = Process.Start(start) ?? throw new IOException("无法启动独立更新程序。");
    }

    internal static Window CreateInstallerWindow(string[] arguments)
    {
        if (arguments.Length != 4 || !int.TryParse(arguments[2], out var parentId) || !long.TryParse(arguments[3], out var parentTicks))
            throw new ArgumentException("更新启动参数无效。");
        var prepared = UpdateInstaller.LoadPrepared(arguments[1]);
        var runner = Path.GetFullPath(Path.Combine(prepared.Workspace, "runner"));
        if (!string.Equals(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), runner, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("必须从独立暂存目录运行更新程序。");
        var status = new TextBlock { Text = "正在等待 Clip 退出…", TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 16) };
        var bar = new ProgressBar { Height = 10, Maximum = 100, IsIndeterminate = true };
        var panel = new StackPanel { Margin = new(24) };
        panel.Children.Add(status);
        panel.Children.Add(bar);
        var window = new Window { Title = "正在更新", Width = 480, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen, Content = panel };
        WindowPresentation.HideCaptionIcon(window);
        var applying = true;
        var started = false;
        window.Closing += (_, e) => e.Cancel = applying;
        window.ContentRendered += async (_, _) =>
        {
            if (started) return;
            started = true;
            try
            {
                Process? parent = null;
                try { parent = Process.GetProcessById(parentId); }
                catch (ArgumentException) { }
                using (parent)
                {
                    try
                    {
                        if (parent is not null && !parent.HasExited && parent.StartTime.ToUniversalTime().Ticks == parentTicks)
                            await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
                    }
                    catch (InvalidOperationException) when (parent?.HasExited != false) { }
                }
                var progress = new Progress<UpdateProgress>(value =>
                {
                    status.Text = value.Stage + (value.Fraction is null ? "" : $" · {value.Fraction:P0}");
                    bar.IsIndeterminate = value.Fraction is null;
                    bar.Value = (value.Fraction ?? 0) * 100;
                });
                await Task.Run(() => UpdateInstaller.ApplyAsync(prepared, progress, () =>
                {
                    var start = new ProcessStartInfo(Path.Combine(prepared.TargetDirectory, "Clip.exe"))
                        { UseShellExecute = false, WorkingDirectory = prepared.TargetDirectory };
                    using var restarted = Process.Start(start) ?? throw new IOException("无法重启 Clip。");
                }));
                applying = false;
                window.Close();
            }
            catch (Exception error)
            {
                applying = false;
                bar.IsIndeterminate = false;
                status.Text = "更新未完成。\n" + error.Message + "\n更新目录：" + prepared.Workspace;
                StartupDiagnostics.Write("Update installation failed", error);
                MessageBox.Show(window, status.Text, "更新失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };
        return window;
    }
}
