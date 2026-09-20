using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Clip.Core.Updates;

namespace Clip.Desktop.Updates;

/// <summary>The only adapter to the main UI; the editor and its XAML do not own update logic.</summary>
internal sealed class UpdateController
{
    private readonly Window _owner;
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly CancellationTokenSource _lifetime = new();
    private readonly GitHubUpdateClient _github;
    private readonly AppBuild _build = AppBuild.FromAssembly(typeof(App).Assembly);
    private readonly MenuItem _check = new() { Header = "检查更新…" };
    private UpdatePreferences _preferences = UpdatePreferences.Load();
    private int _generation;

    private UpdateController(Window owner, ContextMenu menu)
    {
        _owner = owner;
        _github = new(_http) { Token = Environment.GetEnvironmentVariable("CLIP_GITHUB_TOKEN") };
        var dev = new MenuItem { Header = "Dev 模式（GitHub Actions）", IsCheckable = true, IsChecked = _preferences.DevMode };
        dev.Click += async (_, _) =>
        {
            try
            {
                var next = new UpdatePreferences(dev.IsChecked);
                next.Save();
                _preferences = next;
                _check.Header = "检查更新…";
                await CheckInBackgroundAsync();
            }
            catch (Exception error)
            {
                dev.IsChecked = _preferences.DevMode;
                NoticeWindow.Show(_owner, "无法保存更新设置", error.Message);
            }
        };
        _check.Click += (_, _) => OpenUpdateWindow();
        menu.Items.Add(new Separator());
        menu.Items.Add(_check);
        menu.Items.Add(dev);
        owner.Closed += (_, _) => { _lifetime.Cancel(); _http.Dispose(); };
        owner.Loaded += async (_, _) => await CheckInBackgroundAsync();
    }

    internal static void Attach(Window window)
    {
        if (window.FindName("SettingsButton") is FrameworkElement { ContextMenu: { } menu })
            _ = new UpdateController(window, menu);
        else StartupDiagnostics.Write("Update menu unavailable: SettingsButton has no context menu.");
    }

    private async Task CheckInBackgroundAsync()
    {
        var generation = ++_generation;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var result = await _github.CheckAsync(_preferences.DevMode ? UpdateChannel.Dev : UpdateChannel.Release, _build, timeout.Token);
            if (_lifetime.IsCancellationRequested || generation != _generation) return;
            _check.Header = result.Status is UpdateStatus.Available or UpdateStatus.UnknownVersion ? "发现更新 · 点击查看…" : "检查更新…";
            StartupDiagnostics.Write("Update check: " + result.Message);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { StartupDiagnostics.Write("Background update check unavailable", error); }
    }

    private void OpenUpdateWindow()
    {
        var window = new UpdateWindow(_github, _build, _preferences.DevMode ? UpdateChannel.Dev : UpdateChannel.Release) { Owner = _owner };
        if (window.ShowDialog() != true || window.Prepared is not { } prepared) return;

        // Launch only from Closed: the editor may veto Closing due to an export or unexported edits.
        void Launch(object? sender, EventArgs args)
        {
            try { UpdateProcess.Start(prepared); }
            catch (Exception error)
            {
                StartupDiagnostics.Write("Unable to launch update installer", error);
                NoticeWindow.Show(null, "更新失败", "无法启动更新程序，原版本未修改。\n" + error.Message);
            }
        }
        _owner.Closed += Launch;
        _owner.Close();
        _owner.Closed -= Launch;
    }
}
