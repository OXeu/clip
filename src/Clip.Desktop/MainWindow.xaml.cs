using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Clip.Core;
using Microsoft.Win32;

namespace Clip.Desktop;

public partial class MainWindow : Window
{
    private readonly Timeline _timeline = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(25) };
    private readonly string[] _arguments;
    private readonly string _cacheDirectory = Path.Combine(Path.GetTempPath(), "Clip", Guid.NewGuid().ToString("N"));
    private FfmpegTools _tools = FfmpegTools.Discover();
    private CancellationTokenSource? _operation;
    private bool _hasNvidia;
    private bool _mediaReady;
    private bool _playing;
    private bool _usingProxy;
    private bool _previewRetryPending;
    private bool _initializing;
    private bool _closed;
    private double _position;
    private Guid? _selected;
    private Guid? _playbackSegment;
    private double? _pendingSeek;
    private DateTime _seekStarted;

    public MainWindow(string[] arguments)
    {
        _arguments = arguments;
        InitializeComponent();
        TimelineView.SelectionChanged += id => { _selected = id; Refresh(); };
        TimelineView.SeekRequested += Seek;
        TimelineView.DeleteRequested += DeleteSelected;
        _timer.Tick += Tick;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Environment.GetCommandLineArgs().Contains("--smoke-test")) return;
        _initializing = true;
        SetBusy(true, "正在检测 FFmpeg 和 NVIDIA…");
        try
        {
            var settings = Settings.Load();
            _tools = FfmpegTools.Discover(settings.FfmpegDirectory);
            await VerifyToolsAsync(_operation!.Token);
            StatusText.Text = "就绪 · Ctrl + O 导入视频";
        }
        catch (OperationCanceledException) { StatusText.Text = "已取消检测"; }
        catch (Exception exception) { StatusText.Text = "FFmpeg 未就绪：请通过「设置」选择 FFmpeg 目录。"; HardwareText.ToolTip = exception.Message; }
        finally { SetBusy(false); _initializing = false; }
        if (!_closed && _arguments.Length > 0) await ImportAsync(_arguments[0]);
    }

    private async Task VerifyToolsAsync(CancellationToken token)
    {
        await _tools.VerifyAsync(token);
        _hasNvidia = await _tools.CanEncodeNvidiaAsync(token);
        HardwareText.Text = _hasNvidia ? "● NVIDIA NVENC 可用" : "● FFmpeg 就绪 · CPU 编码";
    }

    private async void ImportClick(object sender, RoutedEventArgs e)
    {
        if (_operation is not null) return;
        var dialog = new OpenFileDialog
        {
            Title = "导入视频", Filter = "视频文件|*.mp4;*.mov;*.mkv;*.avi;*.webm;*.m4v;*.wmv;*.ts;*.mts;*.m2ts|所有文件|*.*",
            Multiselect = false, CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true) await ImportAsync(dialog.FileName);
    }

    private async Task ImportAsync(string path)
    {
        if (_operation is not null) return;
        if (_timeline.Media is not null && MessageBox.Show(this, "导入新视频将替换当前时间轴，未导出的剪辑会丢失。继续导入？", "导入视频",
            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Pause();
        SetBusy(true, "正在读取视频…");
        try
        {
            var media = await _tools.ProbeAsync(path, _operation!.Token);
            if (media.IsHdr) throw new NotSupportedException("该视频为 HDR。第一期支持 SDR 视频，请先转换为 SDR 后导入。");
            Directory.CreateDirectory(_cacheDirectory);
            var thumbnailPath = Path.Combine(_cacheDirectory, Guid.NewGuid().ToString("N") + ".png");
            BitmapImage? thumbnail = null;
            try
            {
                await _tools.MakeThumbnailAsync(media, thumbnailPath, _operation.Token);
                thumbnail = new BitmapImage();
                thumbnail.BeginInit();
                thumbnail.CacheOption = BitmapCacheOption.OnLoad;
                thumbnail.UriSource = new Uri(thumbnailPath);
                thumbnail.EndInit();
                thumbnail.Freeze();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { /* Metadata is sufficient for editing even when thumbnail generation fails. */ }
            _timeline.Load(media);
            _selected = _timeline.Segments[0].Id;
            _position = 0;
            _usingProxy = false;
            _previewRetryPending = false;
            _mediaReady = false;
            Thumbnail.Source = thumbnail;
            FileNameText.Text = media.FileName;
            MediaDetailsText.Text = $"{media.Width} × {media.Height}  ·  {media.FrameRate:0.##} fps\n{TimelineControl.FormatTime(media.Duration)}\n{media.Codec.ToUpperInvariant()} · {(media.HasAudio ? "含音频" : "无音频")}";
            Title = $"{media.FileName} — Clip";
            EmptyPreview.Visibility = Visibility.Collapsed;
            PreviewModeText.Text = "原始视频 · 适应画面";
            Preview.Close();
            Preview.Source = new Uri(media.Path);
            Preview.Play();
            Preview.Pause();
            StatusText.Text = "已导入 · 单击时间轴定位，按 S 分割";
        }
        catch (OperationCanceledException) { StatusText.Text = "已取消导入"; }
        catch (Exception exception) { ShowError(exception); }
        finally { SetBusy(false); Refresh(); }
    }

    private void PreviewOpened(object sender, RoutedEventArgs e)
    {
        _mediaReady = true;
        Preview.Pause();
        Seek(_position);
        Refresh();
    }

    private async void PreviewFailed(object sender, ExceptionRoutedEventArgs e) => await CreateCompatiblePreviewAsync();

    private async Task CreateCompatiblePreviewAsync()
    {
        _mediaReady = false;
        Pause();
        if (_usingProxy || _timeline.Media is not { } media || _closed)
        {
            StatusText.Text = "预览不可用。请安装 Windows 媒体功能包；仍可编辑和导出。";
            Refresh();
            return;
        }
        if (_operation is not null)
        {
            // Defer once until the active operation completes; do not spin the dispatcher.
            _previewRetryPending = true;
            return;
        }
        _usingProxy = true;
        SetBusy(true, "系统暂不支持此格式，正在生成兼容预览…");
        try
        {
            var output = Path.Combine(_cacheDirectory, Guid.NewGuid().ToString("N") + ".mp4");
            var progress = new Progress<ExportProgress>(p => { OperationProgress.IsIndeterminate = false; OperationProgress.Value = p.Fraction; });
            await new ExportService(_tools).MakePreviewAsync(media, output, false, progress, _operation!.Token);
            Preview.Close();
            Preview.Source = new Uri(output);
            Preview.Play();
            Preview.Pause();
            PreviewModeText.Text = "兼容预览 · 导出使用源视频";
            StatusText.Text = "兼容预览已生成";
        }
        catch (OperationCanceledException) { StatusText.Text = "已取消预览生成，仍可剪辑和导出"; }
        catch (Exception exception) { ShowError(exception); }
        finally { SetBusy(false); }
    }

    private void Seek(double time)
    {
        if (_operation is not null) return;
        Pause();
        _position = Math.Clamp(time, 0, _timeline.Duration);
        var located = _timeline.Locate(_position);
        if (located is { } p && _mediaReady)
        {
            _playbackSegment = p.Segment.Id;
            SetPreviewPosition(Math.Min(p.SourceTime, Math.Max(p.Segment.Start, p.Segment.End - _timeline.FrameDuration)));
        }
        RefreshPosition();
    }

    private void SetPreviewPosition(double sourceTime)
    {
        _pendingSeek = sourceTime;
        _seekStarted = DateTime.UtcNow;
        Preview.Position = TimeSpan.FromSeconds(Math.Max(0, sourceTime));
    }

    private void PlayClick(object sender, RoutedEventArgs e) => TogglePlay();
    private void TogglePlay()
    {
        if (_operation is not null || !_mediaReady || _timeline.Segments.Count == 0) return;
        if (_playing) { Pause(); return; }
        if (_position >= _timeline.Duration - _timeline.FrameDuration / 2) Seek(0);
        var p = _timeline.Locate(_position)!.Value;
        _playbackSegment = p.Segment.Id;
        SetPreviewPosition(p.SourceTime);
        _playing = true;
        Preview.Play();
        _timer.Start();
        PlayButton.Content = "Ⅱ";
    }

    private void Pause()
    {
        _playing = false;
        _timer.Stop();
        Preview.Pause();
        PlayButton.Content = "▶";
    }

    private void Tick(object? sender, EventArgs e)
    {
        if (!_playing) return;
        var sourceTime = Preview.Position.TotalSeconds;
        if (_pendingSeek is { } target)
        {
            if (Math.Abs(sourceTime - target) > Math.Max(0.12, _timeline.FrameDuration * 2) &&
                DateTime.UtcNow - _seekStarted < TimeSpan.FromSeconds(2)) return;
            _pendingSeek = null;
        }
        var index = _timeline.Segments.ToList().FindIndex(s => s.Id == _playbackSegment);
        if (index < 0) { Pause(); return; }
        var segment = _timeline.Segments[index];
        var timelineStart = _timeline.Segments.Take(index).Sum(s => s.Duration);
        if (sourceTime >= segment.End - Math.Min(0.012, _timeline.FrameDuration / 3))
        {
            if (index + 1 >= _timeline.Segments.Count)
            {
                Pause();
                _position = _timeline.Duration;
            }
            else
            {
                var next = _timeline.Segments[index + 1];
                _playbackSegment = next.Id;
                _position = timelineStart + segment.Duration;
                SetPreviewPosition(next.Start);
                Preview.Play();
            }
        }
        else _position = timelineStart + Math.Clamp(sourceTime - segment.Start, 0, segment.Duration);
        RefreshPosition();
    }

    private void PreviewEnded(object sender, RoutedEventArgs e)
    {
        if (!_playing) return;
        Pause();
        _position = _timeline.Duration;
        RefreshPosition();
    }

    private void SplitClick(object sender, RoutedEventArgs e) => Split();
    private void Split()
    {
        if (_operation is not null) return;
        Pause();
        var id = _timeline.Split(_position);
        if (id is null) { StatusText.Text = "将播放头移到片段内部再分割，不能在边界生成空片段。"; return; }
        _selected = id;
        StatusText.Text = "已分割 · 单击片段后按 Delete 删除";
        Refresh();
    }

    private void DeleteClick(object sender, RoutedEventArgs e) => DeleteSelected();
    private void DeleteSelected()
    {
        if (_operation is not null || _selected is not { } id) return;
        Pause();
        var index = _timeline.Segments.ToList().FindIndex(s => s.Id == id);
        if (index < 0) return;
        var start = _timeline.Segments.Take(index).Sum(s => s.Duration);
        var length = _timeline.Segments[index].Duration;
        if (!_timeline.Delete(id)) return;
        if (_position >= start) _position = Math.Max(start, _position - length);
        _position = Math.Clamp(_position, 0, _timeline.Duration);
        _selected = _timeline.Locate(_position)?.Segment.Id;
        Seek(_position);
        StatusText.Text = "已删除片段，剩余内容自动收拢 · Ctrl + Z 撤销";
        Refresh();
    }

    private void UndoClick(object sender, RoutedEventArgs e) => Restore(false);
    private void RedoClick(object sender, RoutedEventArgs e) => Restore(true);
    private void Restore(bool redo)
    {
        if (_operation is not null) return;
        Pause();
        if (!(redo ? _timeline.Redo() : _timeline.Undo())) return;
        _position = Math.Clamp(_position, 0, _timeline.Duration);
        _selected = _timeline.Locate(_position)?.Segment.Id;
        Seek(_position);
        Refresh();
    }

    private void StepBackClick(object sender, RoutedEventArgs e) => Seek(_position - _timeline.FrameDuration);
    private void StepForwardClick(object sender, RoutedEventArgs e) => Seek(_position + _timeline.FrameDuration);

    private async void ExportClick(object sender, RoutedEventArgs e)
    {
        if (_operation is not null || _timeline.Media is not { } media || _timeline.Segments.Count == 0) return;
        Pause();
        var options = new ExportWindow(media, _hasNvidia) { Owner = this };
        if (options.ShowDialog() != true) return;
        var save = new SaveFileDialog
        {
            Title = "导出视频（请选择新文件名）", Filter = "MP4 视频|*.mp4", DefaultExt = ".mp4", AddExtension = true,
            FileName = Path.GetFileNameWithoutExtension(media.Path) + "_clip.mp4", OverwritePrompt = false
        };
        if (save.ShowDialog(this) != true) return;
        SetBusy(true, "准备导出…");
        OperationProgress.IsIndeterminate = false;
        try
        {
            var progress = new Progress<ExportProgress>(p =>
            {
                OperationProgress.Value = p.Fraction;
                StatusText.Text = $"{p.Message} {p.Fraction:P0}";
            });
            await new ExportService(_tools).ExportAsync(media, _timeline.Segments, options.Options!, save.FileName, progress, _operation!.Token);
            StatusText.Text = "导出完成 · " + save.FileName;
            if (!_closed && MessageBox.Show(this, $"已导出 {TimelineControl.FormatTime(_timeline.Duration)} 的视频。\n\n{save.FileName}\n\n在资源管理器中查看？",
                "导出完成", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            {
                var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
                start.Arguments = $"/select,\"{save.FileName}\"";
                Process.Start(start);
            }
        }
        catch (OperationCanceledException) { StatusText.Text = "已取消导出，临时文件已清理"; }
        catch (Exception exception) { ShowError(exception); }
        finally { SetBusy(false); }
    }

    private void Refresh()
    {
        var ready = _operation is null;
        var any = _timeline.Segments.Count > 0;
        Preview.Visibility = any ? Visibility.Visible : Visibility.Hidden;
        ExportButton.IsEnabled = SplitButton.IsEnabled = ready && any;
        PlayButton.IsEnabled = ready && any && _mediaReady;
        BackButton.IsEnabled = ForwardButton.IsEnabled = ready && any;
        UndoButton.IsEnabled = ready && _timeline.CanUndo;
        RedoButton.IsEnabled = ready && _timeline.CanRedo;
        DeleteButton.IsEnabled = ready && _selected.HasValue && _timeline.Segments.Any(s => s.Id == _selected);
        TimelineView.Segments = _timeline.Segments;
        TimelineView.Duration = _timeline.Duration;
        TimelineView.SourceName = _timeline.Media?.FileName ?? "";
        TimelineView.SelectedId = _selected;
        TimelineView.IsEnabled = ready;
        var selected = _timeline.Segments.Where(s => s.Id == _selected).ToArray();
        SelectionText.Text = selected.Length > 0 ? $"片段 {_timeline.Segments.ToList().FindIndex(s => s.Id == _selected) + 1:00}" : "尚未选择片段";
        InText.Text = selected.Length > 0 ? TimelineControl.FormatTime(selected[0].Start) : "—";
        OutText.Text = selected.Length > 0 ? TimelineControl.FormatTime(selected[0].End) : "—";
        LengthText.Text = selected.Length > 0 ? TimelineControl.FormatTime(selected[0].Duration) : "—";
        TimelineSummaryText.Text = $"{_timeline.Segments.Count} 个片段  ·  保留 {_timeline.Duration:0.##} s  ·  已移除 {Math.Max(0, (_timeline.Media?.Duration ?? 0) - _timeline.Duration):0.##} s";
        RefreshPosition();
    }

    private void RefreshPosition()
    {
        TimelineView.Position = _position;
        TimelineView.InvalidateVisual();
        PositionText.Text = TimelineControl.FormatTime(_position);
        TotalText.Text = "/ " + TimelineControl.FormatTime(_timeline.Duration);
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
        ImportButton.IsEnabled = SettingsButton.IsEnabled = !busy;
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
        if (!_closed) MessageBox.Show(this, exception.Message, "Clip · 操作未完成", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void CancelClick(object sender, RoutedEventArgs e) => _operation?.Cancel();
    private void SettingsClick(object sender, RoutedEventArgs e)
    {
        SettingsButton.ContextMenu.PlacementTarget = SettingsButton;
        SettingsButton.ContextMenu.IsOpen = true;
    }

    private async void ChooseFfmpegClick(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "选择包含 ffmpeg.exe 和 ffprobe.exe 的目录" };
        if (picker.ShowDialog(this) != true) return;
        SetBusy(true, "验证 FFmpeg…");
        var old = _tools;
        try
        {
            _tools = FfmpegTools.Discover(picker.FolderName);
            await VerifyToolsAsync(_operation!.Token);
            new Settings(picker.FolderName).Save();
            StatusText.Text = "FFmpeg 配置已保存";
        }
        catch (OperationCanceledException) { _tools = old; }
        catch (Exception exception) { _tools = old; ShowError(exception); }
        finally { SetBusy(false); }
    }

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
        if (_operation is not null || Keyboard.FocusedElement is TextBoxBase or ComboBox or ComboBoxItem or MenuItem) return;
        var control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (control && e.Key == Key.O) ImportClick(sender, e);
        else if (control && e.Key == Key.Z) Restore(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        else if (control && e.Key == Key.Y) Restore(true);
        else if (Keyboard.Modifiers == ModifierKeys.None)
        {
            switch (e.Key)
            {
                case Key.S: Split(); break;
                case Key.Delete: DeleteSelected(); break;
                case Key.Space: TogglePlay(); break;
                case Key.Left: Seek(_position - _timeline.FrameDuration); break;
                case Key.Right: Seek(_position + _timeline.FrameDuration); break;
                case Key.Home: Seek(0); break;
                case Key.End: Seek(_timeline.Duration); break;
                default: return;
            }
        }
        else return;
        e.Handled = true;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = _operation is null && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (_operation is not null || _initializing) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            if (files.Length > 1) { StatusText.Text = "第一期每个时间轴支持一个源视频，请一次拖入一个文件。"; return; }
            await ImportAsync(files[0]);
        }
    }

    private void ZoomChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateTimelineWidth();
    private void TimelineSizeChanged(object sender, SizeChangedEventArgs e) => UpdateTimelineWidth();
    private void FitClick(object sender, RoutedEventArgs e) => ZoomSlider.Value = 1;
    private void UpdateTimelineWidth()
    {
        if (TimelineView is null || TimelineScroll is null) return;
        TimelineView.Width = Math.Max(100, TimelineScroll.ActualWidth - 4) * ZoomSlider.Value;
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
        if (_timeline.CanUndo && !_closed && !Environment.GetCommandLineArgs().Contains("--smoke-test") &&
            MessageBox.Show(this, "关闭后时间轴编辑不会保存。确认已导出需要的片段？", "关闭 Clip",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) { e.Cancel = true; return; }
        _closed = true;
        _timer.Stop();
        Preview.Close();
        Thumbnail.Source = null;
        try { if (Directory.Exists(_cacheDirectory)) Directory.Delete(_cacheDirectory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal void VerifyUi()
    {
        _timeline.Load(new MediaInfo("smoke.mp4", 12, 1920, 1080, 30, 0, 1, "h264"));
        _timeline.Split(4);
        _timeline.Split(8);
        _selected = _timeline.Segments[1].Id;
        Refresh();
        DeleteSelected();
        Restore(false);
        UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(this);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(AppContext.BaseDirectory, "smoke-ui.png"));
        encoder.Save(stream);
    }
}
