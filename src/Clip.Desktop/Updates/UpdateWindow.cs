using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Clip.Core.Updates;

namespace Clip.Desktop.Updates;

internal sealed class UpdateWindow : Window
{
    private readonly GitHubUpdateClient _github;
    private readonly AppBuild _build;
    private readonly UpdateChannel _channel;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 12) };
    private readonly ProgressBar _progress = new() { Height = 8, Minimum = 0, Maximum = 100, Margin = new(0, 0, 0, 16) };
    private readonly PasswordBox _token = new() { Margin = new(0, 4, 0, 16) };
    private readonly Button _check = new() { Content = "检查更新", Margin = new(0, 0, 8, 0), Padding = new(12, 6, 12, 6) };
    private readonly Button _install = new() { Content = "更新并重启", IsEnabled = false, Margin = new(0, 0, 8, 0), Padding = new(12, 6, 12, 6) };
    private readonly Button _cancel = new() { Content = "关闭", Padding = new(12, 6, 12, 6) };
    private CancellationTokenSource? _operation;
    private UpdatePackage? _package;
    internal PreparedUpdate? Prepared { get; private set; }

    internal UpdateWindow(GitHubUpdateClient github, AppBuild build, UpdateChannel channel)
    {
        _github = github;
        _build = build;
        _channel = channel;
        Title = "Clip · 检查更新";
        Width = 540;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new(24) };
        panel.Children.Add(new TextBlock { Text = $"当前版本：{build.Version}\n更新通道：{(channel == UpdateChannel.Dev ? "Dev · GitHub Actions" : "正式版 · GitHub Release")}", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = "GitHub 令牌（可选，仅当前会话；留空沿用已配置令牌）", Margin = new(0, 16, 0, 0), TextWrapping = TextWrapping.Wrap });
        _token.ToolTip = "私有仓库需要 Contents 和 Actions 只读权限。也可设置 CLIP_GITHUB_TOKEN 环境变量。";
        panel.Children.Add(_token);
        panel.Children.Add(new TextBlock { Text = "更新文件保存在程序目录：\n" + AppContext.BaseDirectory + "\n更新完成后自动重启，请先导出需要保留的编辑。", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(_status);
        panel.Children.Add(_progress);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_check);
        buttons.Children.Add(_install);
        buttons.Children.Add(_cancel);
        panel.Children.Add(buttons);
        Content = panel;
        _check.Click += async (_, _) => await CheckAsync();
        _install.Click += async (_, _) => await InstallAsync();
        _cancel.Click += (_, _) => { if (_operation is not null) _operation.Cancel(); else Close(); };
        Closing += OnClosing;
        Loaded += async (_, _) => await CheckAsync();
    }

    private async Task CheckAsync()
    {
        if (_operation is not null) return;
        _package = null;
        BeginOperation(TimeSpan.FromSeconds(30));
        _status.Text = "正在检查 GitHub 更新…";
        try
        {
            if (!string.IsNullOrWhiteSpace(_token.Password)) { _github.Token = _token.Password.Trim(); _token.Clear(); }
            var result = await _github.CheckAsync(_channel, _build, _operation!.Token);
            _status.Text = result.Message;
            if (result.Package is { } package && result.Status is UpdateStatus.Available or UpdateStatus.UnknownVersion)
            {
                _package = package;
                _status.Text += $"\n{package.Version} · {package.Size / 1048576.0:F1} MB";
            }
        }
        catch (OperationCanceledException) { _status.Text = "检查已取消或超时，可以重试。"; }
        catch (Exception error) { _status.Text = error.Message; StartupDiagnostics.Write("Update check failed", error); }
        finally { EndOperation(); }
    }

    private async Task InstallAsync()
    {
        if (_operation is not null || _package is null) return;
        BeginOperation(TimeSpan.FromMinutes(20));
        try
        {
            var progress = new Progress<UpdateProgress>(ShowProgress);
            var token = _operation!.Token;
            Prepared = await Task.Run(() => new UpdateInstaller(_github).PrepareAsync(_package, AppContext.BaseDirectory, progress, token), token);
            ShowProgress(new("正在准备独立更新程序"));
            await Task.Run(() => UpdateInstaller.PrepareRunner(Prepared, token), token);
            EndOperation();
            DialogResult = true;
        }
        catch (OperationCanceledException) { _status.Text = "更新已取消或下载超时，当前版本未修改。"; }
        catch (Exception error) { _status.Text = "更新准备失败，当前版本未修改。\n" + error.Message; StartupDiagnostics.Write("Update preparation failed", error); }
        finally { if (_operation is not null) EndOperation(); }
    }

    private void ShowProgress(UpdateProgress progress)
    {
        _status.Text = progress.Stage;
        _progress.IsIndeterminate = progress.Fraction is null;
        _progress.Value = (progress.Fraction ?? 0) * 100;
        if (progress.TotalBytes is { } total)
            _status.Text += $" · {progress.Bytes / 1048576.0:F1} / {total / 1048576.0:F1} MB ({progress.Fraction:P0})";
        else if (progress.Fraction is not null) _status.Text += $" · {progress.Fraction:P0}";
    }

    private void BeginOperation(TimeSpan timeout)
    {
        _operation = new(timeout);
        _check.IsEnabled = _install.IsEnabled = _token.IsEnabled = false;
        _cancel.Content = "取消";
        _progress.IsIndeterminate = true;
    }

    private void EndOperation()
    {
        _operation?.Dispose();
        _operation = null;
        _check.IsEnabled = _token.IsEnabled = true;
        _install.IsEnabled = _package is not null;
        _cancel.Content = "关闭";
        _progress.IsIndeterminate = false;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_operation is null) return;
        e.Cancel = true;
        _operation.Cancel();
    }
}
