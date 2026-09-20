using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Clip.Core;
using Microsoft.Win32;

namespace Clip.Desktop;

public partial class MainWindow : Window
{
    private EditProject _project = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(25) };
    private readonly string[] _arguments;
    private readonly TaskCompletionSource _initialization = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _cacheDirectory = Path.Combine(Path.GetTempPath(), "Clip", Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, PreviewAsset> _assets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, float[]> _waveforms = new(StringComparer.OrdinalIgnoreCase);
    private FfmpegTools _tools = FfmpegTools.Discover();
    private CancellationTokenSource? _operation;
    private NvidiaEncoderProbeResult? _nvidiaProbe;
    private bool _initializing;
    private bool _closed;
    private bool _previewRetryPending;
    private Guid _activeTrackId;
    private Guid? _selectedTrackId;
    private Guid? _selected;
    private bool _multiSelectMode;
    private readonly HashSet<Guid> _multiSelectedTracks = [];
    private double _position;
    private double _timelineZoom = 1;
    private const double MaximumTimelineZoom = 20;
    private DateTime _lastAutoScroll;
    internal Task InitializationCompleted => _initialization.Task;
    internal MediaInfo? CurrentMedia => _playbackClip is { } id ? _project.FindClip(id)?.Clip.Media : _project.Sources.FirstOrDefault();
    internal bool ToolsReady { get; private set; }
    private VideoTrack ActiveTrack => _project.FindTrack(_activeTrackId) ?? _project.MainTrack;
    private double FrameDuration => _project.Locate(_activeTrackId, _position) is { } p ? 1 / (p.Clip.Media.FrameRate * p.Clip.Speed) : 1.0 / 30;

    public MainWindow(string[] arguments)
    {
        _arguments = arguments;
        _activeTrackId = _project.MainTrack.Id;
        InitializeComponent();
        WindowPresentation.HideCaptionIcon(this);
        // Center the size that actually fits the desktop, not an oversized requested window.
        WindowPresentation.FitInitialBounds(this);
        TimelineView.Project = _project;
        TimelineView.SelectionChanged += SelectClip;
        TimelineView.ContextSelectionRequested += SelectClipForContextMenu;
        TimelineView.TrackSelectionChanged += SelectTrack;
        TimelineView.TrackToggled += ToggleMultiSelectedTrack;
        TimelineView.SeekRequested += (track, time) => Seek(track, time);
        TimelineView.MoveRequested += MoveClip;
        TimelineView.TrackMoveRequested += MoveTrack;
        TimelineView.AutoScrollRequested += AutoScrollTimeline;
        _timer.Tick += Tick;
        Design.UiTheme.Changed += ThemeChanged;
        Closed += (_, _) => Design.UiTheme.Changed -= ThemeChanged;
        Loaded += OnLoaded;
    }

    private void ThemeChanged() => TimelineView.InvalidateVisual();

    private void ZoomInClick(object sender, RoutedEventArgs e) => SetTimelineZoom(_timelineZoom * 1.25);
    private void ZoomOutClick(object sender, RoutedEventArgs e) => SetTimelineZoom(_timelineZoom / 1.25);
    private void FitTimelineClick(object sender, RoutedEventArgs e) => ResetTimelineZoom();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        StartupDiagnostics.Write("Main window Loaded; initializing services");
        if (Environment.GetCommandLineArgs().Contains("--smoke-test")) { _initialization.TrySetResult(); return; }
        _initializing = true;
        try
        {
            SetBusy(true, "正在检测 FFmpeg 和 NVIDIA…");
            var settings = Settings.Load();
            _tools = FfmpegTools.Discover(settings.FfmpegDirectory);
            await VerifyToolsAsync(_operation!.Token);
            StatusText.Text = "就绪 · Ctrl + O 导入视频";
        }
        catch (OperationCanceledException) { StatusText.Text = "已取消检测"; }
        catch (Exception exception)
        {
            StartupDiagnostics.Write("FFmpeg initialization unavailable", exception);
            StatusText.Text = "FFmpeg 未就绪：请通过「更多」→「设置」→「高级设置」选择 FFmpeg 目录。";
            HardwareText.ToolTip = exception.Message;
        }
        finally
        {
            SetBusy(false);
            _initializing = false;
            StartupDiagnostics.Write("Main window initialization completed");
            _initialization.TrySetResult();
        }
        if (!_closed && _arguments.Length == 1 && string.Equals(Path.GetExtension(_arguments[0]), ProjectFile.FileExtension, StringComparison.OrdinalIgnoreCase))
            await RestoreProjectAsync(_arguments[0]);
        else if (!_closed && _arguments.Length > 0) await ImportFilesAsync(_arguments);
    }

    private async Task VerifyToolsAsync(CancellationToken token)
    {
        await _tools.VerifyAsync(token);
        var probe = await _tools.ProbeNvidiaAsync(token);
        ApplyNvidiaProbe(probe);
        ToolsReady = true;
    }

    private void ApplyNvidiaProbe(NvidiaEncoderProbeResult? probe)
    {
        _nvidiaProbe = probe;
        HardwareText.Text = probe?.IsAvailable == true ? "● NVIDIA NVENC 可用" : "● FFmpeg 就绪 · CPU 编码";
        HardwareText.ToolTip = probe is null ? null : $"{probe.Summary}\n更多 → 设置 → 高级设置 → NVIDIA 检测详情…";
        if (probe is not null) StartupDiagnostics.Write(NvidiaDiagnostics());
    }

    private string NvidiaDiagnostics() => _nvidiaProbe is not { } probe ? "尚未完成 NVIDIA NVENC 检测。" :
        $"{probe.Summary}\n\nFFmpeg：{_tools.Ffmpeg}\n退出码：{probe.ExitCode}\n" +
        $"检测参数：{string.Join(" ", FfmpegTools.BuildNvidiaProbeArguments())}\n\n" +
        $"{(string.IsNullOrWhiteSpace(probe.StandardError) ? "FFmpeg 未输出错误详情。" : probe.StandardError)}";


    private async void ImportClick(object sender, RoutedEventArgs e)
    {
        if (_operation is not null) return;
        var dialog = new OpenFileDialog
        {
            Title = "导入素材", Filter = "视频文件|*.mp4;*.mov;*.mkv;*.avi;*.webm;*.m4v;*.wmv;*.ts;*.mts;*.m2ts|所有文件|*.*",
            Multiselect = true, CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true) await ImportFilesAsync(dialog.FileNames);
    }

    private async Task ImportFilesAsync(IEnumerable<string> paths)
    {
        if (_operation is not null) return;
        Pause();
        SetBusy(true, "正在导入素材…");
        Guid? last = null;
        var imported = 0;
        var cancelled = false;
        List<string> failures = [];
        try
        {
            foreach (var path in paths)
            {
                _operation!.Token.ThrowIfCancellationRequested();
                try
                {
                    var media = await _tools.ProbeAsync(path, _operation.Token);
                    if (media.IsHdr) throw new NotSupportedException("暂不支持 HDR，请先转换为 SDR。");
                    Directory.CreateDirectory(_cacheDirectory);
                    BitmapImage? thumbnail = null;
                    try
                    {
                        var thumbnailPath = Path.Combine(_cacheDirectory, Guid.NewGuid().ToString("N") + ".png");
                        await _tools.MakeThumbnailAsync(media, thumbnailPath, _operation.Token);
                        thumbnail = new BitmapImage();
                        thumbnail.BeginInit();
                        thumbnail.CacheOption = BitmapCacheOption.OnLoad;
                        thumbnail.UriSource = new Uri(thumbnailPath);
                        thumbnail.EndInit();
                        thumbnail.Freeze();
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception) { /* Metadata is sufficient to edit a source without a thumbnail. */ }
                    if (media.HasAudio)
                    {
                        try { _waveforms[media.Path] = await _tools.MakeWaveformAsync(media, token: _operation!.Token); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception) { /* 波形失败不影响素材导入。 */ }
                    }
                    var separated = _project.ImportSeparated(media);
                    if (!_assets.ContainsKey(media.Path)) _assets[media.Path] = new(media, thumbnail);
                    last = separated.VideoTrack.Clips[0].Id;
                    imported++;
                    StatusText.Text = $"已导入 {imported} 个素材…";
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) { failures.Add($"{Path.GetFileName(path)}：{error.Message}"); }
            }
        }
        catch (OperationCanceledException) { cancelled = true; StatusText.Text = $"已停止导入，保留已导入的 {imported} 个素材。"; }
        finally { SetBusy(false); }
        if (last is { } id && _project.FindClip(id) is { } p)
        {
            ActivatePreview(p, false);
            RevealTrack(p.TrackId);
            StatusText.Text = cancelled ? $"已停止导入，保留已导入的 {imported} 个素材。" :
                $"已导入 {imported} 个素材 · 有声音的素材已自动分轨";
        }
        Refresh();
        if (failures.Count > 0) ShowError(new InvalidOperationException("以下素材未导入；其他素材已保留。\n\n" + string.Join("\n", failures)));
    }

    private void SetBusy(bool busy, string? message = null)
    {
        if (busy)
        {
            _operation = new CancellationTokenSource();
            OperationProgress.Value = 0;
            OperationProgress.IsIndeterminate = true;
        }
        else { _operation?.Dispose(); _operation = null; }
        ImportButton.IsEnabled = MoreButton.IsEnabled = EmptyImportButton.IsEnabled = !busy;
        RestoreProjectMenuItem.IsEnabled = !busy;
        SaveProjectMenuItem.IsEnabled = !busy && _project.Sources.Count > 0;
        CancelButton.Visibility = OperationProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (message is not null) StatusText.Text = message;
        Refresh();
        if (!busy && _previewRetryPending && !_closed)
        {
            _previewRetryPending = false;
            _ = Dispatcher.InvokeAsync(async () => await CreateCompatiblePreviewAsync());
        }
    }

    private void ShowError(Exception exception)
    {
        StatusText.Text = exception.Message.Split('\n')[0];
        if (App.IsAutomatedRun) throw new InvalidOperationException("Automated UI operation failed.", exception);
        if (!_closed) NoticeWindow.Show(this, "操作未完成", exception.Message);
    }

    private void CancelClick(object sender, RoutedEventArgs e) => _operation?.Cancel();
    private void MoreClick(object sender, RoutedEventArgs e)
    {
        MoreButton.ContextMenu.PlacementTarget = MoreButton;
        MoreButton.ContextMenu.IsOpen = true;
    }

    private async void ChooseFfmpegClick(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "选择包含 ffmpeg.exe 和 ffprobe.exe 的目录" };
        if (picker.ShowDialog(this) != true) return;
        SetBusy(true, "验证 FFmpeg…");
        var old = _tools;
        var oldProbe = _nvidiaProbe;
        var oldReady = ToolsReady;
        try
        {
            _tools = FfmpegTools.Discover(picker.FolderName);
            await VerifyToolsAsync(_operation!.Token);
            new Settings(picker.FolderName).Save();
            StatusText.Text = "FFmpeg 配置已保存";
        }
        catch (OperationCanceledException) { RestoreTools(); }
        catch (Exception exception) { RestoreTools(); ShowError(exception); }
        finally { SetBusy(false); }

        void RestoreTools()
        {
            _tools = old;
            ToolsReady = oldReady;
            ApplyNvidiaProbe(oldProbe);
            if (!oldReady) HardwareText.Text = "● FFmpeg 未就绪";
        }
    }

    private async void RetryNvidiaClick(object sender, RoutedEventArgs e)
    {
        if (_operation is not null) return;
        SetBusy(true, "正在重新检测 NVIDIA NVENC…");
        try
        {
            await VerifyToolsAsync(_operation!.Token);
            StatusText.Text = _nvidiaProbe!.Summary;
        }
        catch (OperationCanceledException) { StatusText.Text = "已取消检测"; }
        catch (Exception exception) { ShowError(exception); }
        finally { SetBusy(false); }
    }

    private void NvidiaDiagnosticsClick(object sender, RoutedEventArgs e) =>
        NoticeWindow.Show(this, "NVIDIA NVENC 检测详情", NvidiaDiagnostics() + $"\n\n诊断日志：{StartupDiagnostics.LogPath ?? "日志目录不可写"}");

    private void RegisterShellClick(object sender, RoutedEventArgs e)
    {
        try { ShellIntegration.Register(); StatusText.Text = "已添加「使用 Clip 剪辑」右键菜单；Windows 11 中位于「显示更多选项」。"; }
        catch (Exception exception) { ShowError(exception); }
    }

    private void UnregisterShellClick(object sender, RoutedEventArgs e)
    {
        try { ShellIntegration.Unregister(); StatusText.Text = "已移除 Clip 的资源管理器右键菜单"; }
        catch (Exception exception) { ShowError(exception); }
    }


    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        // Text, menus and splitters retain native input; buttons never consume Space as an accidental click.
        if (Keyboard.FocusedElement is TextBoxBase or ComboBox or ComboBoxItem or MenuItem or GridSplitter) return;
        if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            if (!e.IsRepeat) TogglePlay();
            return;
        }
        if (_operation is not null) return;
        var control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (control && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && e.Key == Key.O) RestoreProjectClick(sender, e);
        else if (control && e.Key == Key.S) SaveProjectClick(sender, e);
        else if (control && e.Key == Key.O) ImportClick(sender, e);
        else if (control && e.Key == Key.Z) Restore(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        else if (control && e.Key == Key.Y) Restore(true);
        else if (Keyboard.Modifiers == ModifierKeys.None)
        {
            switch (e.Key)
            {
                case Key.S: Split(); break;
                case Key.Delete:
                case Key.X: DeleteSelected(); break;
                case Key.Left: Seek(_activeTrackId, _position - FrameDuration); break;
                case Key.Right: Seek(_activeTrackId, _position + FrameDuration); break;
                case Key.Home: Seek(_activeTrackId, 0); break;
                case Key.End: Seek(_activeTrackId, ActiveTrack.Duration); break;
                default: return;
            }
        }
        else return;
        e.Handled = true;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = _operation is null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (_operation is not null || _initializing || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        if (files.Length == 1 && string.Equals(Path.GetExtension(files[0]), ProjectFile.FileExtension, StringComparison.OrdinalIgnoreCase))
            await RestoreProjectAsync(files[0]);
        else await ImportFilesAsync(files);
    }

    private void TimelineSizeChanged(object sender, SizeChangedEventArgs e) => UpdateTimelineWidth();
    private void ResetTimelineZoom() { SetTimelineZoom(1); TimelineScroll.ScrollToHorizontalOffset(0); }
    private void SetTimelineZoom(double zoom)
    {
        _timelineZoom = Math.Clamp(zoom, 1, MaximumTimelineZoom);
        UpdateTimelineWidth();
    }

    private void TimelineMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var point = e.GetPosition(TimelineScroll);
        var modifiers = Keyboard.Modifiers;
        e.Handled = ApplyTimelineWheel(e.Delta, modifiers, point.X);
    }

    private bool ApplyTimelineWheel(int delta, ModifierKeys modifiers, double pointerX)
    {
        if (_operation is not null || _project.Sources.Count == 0 || delta == 0) return false;
        var notches = delta / 120.0;
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            var anchorX = Math.Clamp(pointerX, 0, TimelineScroll.ViewportWidth);
            var anchorTime = TimelineView.TimeAtX(TimelineScroll.HorizontalOffset + anchorX);
            var zoom = Math.Clamp(_timelineZoom * Math.Pow(1.2, notches), 1, MaximumTimelineZoom);
            if (zoom == _timelineZoom) return true;
            SetTimelineZoom(zoom);
            TimelineScroll.UpdateLayout();
            TimelineScroll.ScrollToHorizontalOffset(TimelineView.XAtTime(anchorTime) - anchorX);
        }
        else if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            var step = SystemParameters.WheelScrollLines < 0 ? TimelineScroll.ViewportWidth * 0.9 : SystemParameters.WheelScrollLines * 24.0;
            TimelineScroll.ScrollToHorizontalOffset(TimelineScroll.HorizontalOffset - notches * step);
        }
        else
        {
            var step = SystemParameters.WheelScrollLines < 0 ? TimelineScroll.ViewportHeight * 0.9 : SystemParameters.WheelScrollLines * 24.0;
            TimelineScroll.ScrollToVerticalOffset(TimelineScroll.VerticalOffset - notches * step);
        }
        return true;
    }

    private void TimelineScrolled(object sender, ScrollChangedEventArgs e)
    {
        if (e.ViewportWidthChange != 0) UpdateTimelineWidth();
        TimelineView.HorizontalOffset = TimelineScroll.HorizontalOffset;
        TimelineView.VerticalOffset = TimelineScroll.VerticalOffset;
        TimelineView.InvalidateVisual();
    }
    private void UpdateTimelineWidth()
    {
        if (TimelineView is null || TimelineScroll is null) return;
        var viewport = TimelineScroll.ViewportWidth > 0 ? TimelineScroll.ViewportWidth : TimelineScroll.ActualWidth;
        TimelineView.Width = Math.Max(TimelineControl.ContentInset + 100, viewport) * _timelineZoom;
    }
    private void RevealTrack(Guid id)
    {
        UpdateLayout();
        var index = _project.Tracks.ToList().FindIndex(t => t.Id == id);
        TimelineScroll.ScrollToVerticalOffset(Math.Max(0, TimelineView.TrackTop(index + 1) - TimelineScroll.ViewportHeight + 8));
    }
    private void AutoScrollTimeline(Point point)
    {
        if (DateTime.UtcNow - _lastAutoScroll < TimeSpan.FromMilliseconds(40)) return;
        _lastAutoScroll = DateTime.UtcNow;
        var x = point.X - TimelineScroll.HorizontalOffset;
        var y = point.Y - TimelineScroll.VerticalOffset;
        if (x < 24) TimelineScroll.ScrollToHorizontalOffset(TimelineScroll.HorizontalOffset - 24);
        else if (x > TimelineScroll.ViewportWidth - 24) TimelineScroll.ScrollToHorizontalOffset(TimelineScroll.HorizontalOffset + 24);
        if (y < TimelineControl.RulerHeight + 16) TimelineScroll.ScrollToVerticalOffset(TimelineScroll.VerticalOffset - 24);
        else if (y > TimelineScroll.ViewportHeight - 24) TimelineScroll.ScrollToVerticalOffset(TimelineScroll.VerticalOffset + 24);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_operation is not null)
        {
            e.Cancel = true;
            _operation.Cancel();
            StatusText.Text = "正在取消当前操作并清理，请稍后再次关闭。";
            return;
        }
        if (_project.CanUndo && !_closed && !App.IsAutomatedRun &&
            !NoticeWindow.Show(this, "关闭视频剪辑", "尚未导出项目的剪辑内容将丢失。请确认已导出项目或需要的视频。",
                "确认关闭", "继续剪辑")) { e.Cancel = true; return; }
        _closed = true;
        _timer.Stop();
        Preview.Close();
        PreviewPoster.Source = null;
        _assets.Clear();
        try { if (Directory.Exists(_cacheDirectory)) Directory.Delete(_cacheDirectory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
