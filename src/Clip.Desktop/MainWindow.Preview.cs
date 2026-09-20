using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Clip.Core;

namespace Clip.Desktop;

public partial class MainWindow
{
    private sealed class PreviewAsset(MediaInfo media, BitmapImage? thumbnail)
    {
        public MediaInfo Media { get; } = media;
        public BitmapImage? Thumbnail { get; } = thumbnail;
        public string PlaybackPath { get; set; } = media.Path;
        public bool ProxyAttempted { get; set; }
        public bool StaticOnly { get; set; }
    }

    private PreviewAsset? _currentAsset;
    private bool _mediaReady;
    private bool _playing;
    private bool _resumeOnOpen;
    private Guid? _playbackClip;
    private double? _pendingSeek;
    private DateTime _seekStarted;
    private double _previewZoom = 1;
    private const double MinimumPreviewZoom = 0.25;
    private const double MaximumPreviewZoom = 4;

    private void PreviewCanvasMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e) =>
        e.Handled = ApplyPreviewWheel(e.Delta);

    private void PreviewCanvasMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || _project.Sources.Count == 0) return;
        SetPreviewZoom(1);
        e.Handled = true;
    }

    private bool ApplyPreviewWheel(int delta)
    {
        if (_project.Sources.Count == 0 || delta == 0) return false;
        SetPreviewZoom(_previewZoom * Math.Pow(1.12, delta / 120.0));
        return true;
    }

    /// <summary>1× is always Stretch.Uniform/content-fit; zoom is relative to that fitted frame.</summary>
    private void SetPreviewZoom(double zoom, bool announce = true)
    {
        if (!double.IsFinite(zoom)) return;
        _previewZoom = Math.Clamp(zoom, MinimumPreviewZoom, MaximumPreviewZoom);
        PreviewScale.ScaleX = PreviewScale.ScaleY = _previewZoom;
        PreviewPosterScale.ScaleX = PreviewPosterScale.ScaleY = _previewZoom;
        PreviewCanvas.ToolTip = $"预览 {_previewZoom:P0} · 滚轮缩放 · 双击恢复适应";
        if (announce) StatusText.Text = _previewZoom == 1 ? "预览已恢复适应" : $"预览缩放 {_previewZoom:P0}";
    }

    private PreviewAsset AssetFor(MediaInfo media)
    {
        if (!_assets.TryGetValue(media.Path, out var asset)) _assets[media.Path] = asset = new(media, null);
        return asset;
    }

    private void SelectClip(Guid id, double time)
    {
        if (_operation is not null || _project.FindClip(id) is not { } p) return;
        _selectedTrackId = null;
        var source = Math.Clamp(p.Clip.Start + (time - p.TimelineStart) * p.Clip.Speed, p.Clip.Start,
            Math.Max(p.Clip.Start, p.Clip.End - 1 / p.Clip.Media.FrameRate));
        ActivatePreview(p with { SourceTime = source }, _playing);
    }

    private void SelectClipForContextMenu(Guid id)
    {
        if (_operation is not null || _multiSelectMode || _project.FindClip(id) is null) return;
        _selectedTrackId = null;
        _selected = id;
        Refresh();
    }

    private void SelectTrack(Guid id, double time)
    {
        if (_operation is not null || _project.FindTrack(id) is null) return;
        _selectedTrackId = id;
        Seek(id, time);
        StatusText.Text = "已选中整条轨道";
    }

    private void Seek(Guid trackId, double time)
    {
        if (_operation is not null) return;
        if (_selectedTrackId != trackId) _selectedTrackId = null;
        if (_project.Locate(trackId, time) is { } p) ActivatePreview(p, false);
        else
        {
            Pause();
            _activeTrackId = trackId;
            _position = 0;
            _selected = _playbackClip = null;
            Refresh();
        }
    }

    private void ActivatePreview(ClipPosition position, bool play, bool forceSourceReload = false)
    {
        if (_selectedTrackId != position.TrackId) _selectedTrackId = null;
        _activeTrackId = position.TrackId;
        _playbackClip = position.Clip.Id;
        _selected = _selectedTrackId is null ? position.Clip.Id : null;
        _position = position.TimelineTime;
        // 伴生音频槽承载编辑关系；视频预览仍需播放源文件的内嵌音频。
        Preview.IsMuted = false;
        var asset = AssetFor(position.Clip.Media);
        _playing = _resumeOnOpen = play;
        if (_currentAsset != asset || forceSourceReload)
        {
            _currentAsset = asset;
            _mediaReady = false;
            _pendingSeek = null;
            _timer.Stop();
            Preview.Close();
            if (!asset.StaticOnly)
            {
                Preview.Source = new Uri(Path.GetFullPath(asset.PlaybackPath));
                Preview.Play();
                Preview.Pause();
            }
        }
        else if (_mediaReady)
        {
            Preview.SpeedRatio = position.Clip.Speed;
            SetPreviewPosition(Math.Min(position.SourceTime, Math.Max(position.Clip.Start, position.Clip.End - 1 / position.Clip.Media.FrameRate)));
            if (play) StartPreviewClock(); else Pause();
        }
        if (asset.StaticOnly) Pause();
        PlayGlyph.Kind = _playing ? "Pause" : "Play";
        Refresh();
    }

    private void PreviewOpened(object sender, RoutedEventArgs e)
    {
        if (_closed || _currentAsset is null || _project.Locate(_activeTrackId, _position) is not { } p) return;
        _mediaReady = true;
        _playbackClip = p.Clip.Id;
        Preview.SpeedRatio = p.Clip.Speed;
        SetPreviewPosition(Math.Min(p.SourceTime, Math.Max(p.Clip.Start, p.Clip.End - 1 / p.Clip.Media.FrameRate)));
        if (_playing || _resumeOnOpen) StartPreviewClock(); else Preview.Pause();
        _resumeOnOpen = false;
        Refresh();
    }

    private async void PreviewFailed(object sender, ExceptionRoutedEventArgs e) => await CreateCompatiblePreviewAsync();

    private async Task CreateCompatiblePreviewAsync()
    {
        if (_closed || _currentAsset is not { } asset) return;
        _mediaReady = false;
        if (_operation is not null) { _previewRetryPending = true; return; }
        var resume = _playing || _resumeOnOpen;
        Pause();
        if (asset.ProxyAttempted)
        {
            asset.StaticOnly = true;
            StatusText.Text = "当前素材无法播放，显示静态预览；仍可剪辑和导出。";
            Refresh();
            return;
        }
        asset.ProxyAttempted = true;
        SetBusy(true, $"正在为 {asset.Media.FileName} 生成兼容预览…");
        var completed = false;
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            var output = Path.Combine(_cacheDirectory, Guid.NewGuid().ToString("N") + ".mp4");
            var progress = new Progress<ExportProgress>(p => { OperationProgress.IsIndeterminate = false; OperationProgress.Value = p.Fraction; });
            await new ExportService(_tools).MakePreviewAsync(asset.Media, output, false, progress, _operation!.Token);
            asset.PlaybackPath = output;
            completed = true;
            StatusText.Text = "兼容预览已生成；导出仍使用原素材。";
        }
        catch (OperationCanceledException) { asset.StaticOnly = true; StatusText.Text = "已取消兼容预览生成，仍可剪辑和导出。"; }
        catch (Exception exception) { asset.StaticOnly = true; ShowError(exception); }
        finally { SetBusy(false); }
        if (completed && !_closed && _project.Locate(_activeTrackId, _position) is { } p)
            ActivatePreview(p, resume, true);
    }

    private void SetPreviewPosition(double sourceTime)
    {
        _pendingSeek = sourceTime;
        _seekStarted = DateTime.UtcNow;
        Preview.Position = TimeSpan.FromSeconds(Math.Max(0, sourceTime));
    }
    private void StartPreviewClock()
    {
        _playing = true;
        Preview.Play();
        _timer.Start();
        PlayGlyph.Kind = "Pause";
    }
    private void Pause()
    {
        _playing = _resumeOnOpen = false;
        _timer.Stop();
        Preview.Pause();
        PlayGlyph.Kind = "Play";
    }
    private void PlayClick(object sender, RoutedEventArgs e) => TogglePlay();
    private void TogglePlay()
    {
        if (_operation is not null) return;
        if (_playing) { Pause(); return; }
        if (!_mediaReady || ActiveTrack.Clips.Count == 0) return;
        var time = _position >= ActiveTrack.Duration - FrameDuration / 2 ? 0 : _position;
        if (_project.Locate(_activeTrackId, time) is { } p) ActivatePreview(p, true);
    }

    private void Tick(object? sender, EventArgs e)
    {
        if (!_playing || !_mediaReady || _playbackClip is not { } id) return;
        var sourceTime = Preview.Position.TotalSeconds;
        if (_pendingSeek is { } target)
        {
            var sourceFrame = _project.FindClip(id) is { } clip ? 1 / clip.Clip.Media.FrameRate : 1.0 / 30;
            if (Math.Abs(sourceTime - target) > Math.Max(0.12, sourceFrame * 2) && DateTime.UtcNow - _seekStarted < TimeSpan.FromSeconds(2)) return;
            _pendingSeek = null;
        }
        if (_project.AdvancePlayback(_activeTrackId, id, sourceTime) is not { } playback) { Pause(); return; }
        var p = playback.Position;
        if (playback.RequiresSeek) { ActivatePreview(p, true); return; }
        var changed = _playbackClip != p.Clip.Id;
        _playbackClip = p.Clip.Id;
        _position = p.TimelineTime;
        if (changed)
        {
            _selected = _selectedTrackId is null ? p.Clip.Id : null;
            Preview.SpeedRatio = p.Clip.Speed;
            Refresh();
        }
        if (playback.ReachedEnd) Pause();
        RefreshPosition();
    }

    private void PreviewEnded(object sender, RoutedEventArgs e)
    {
        if (!_playing || !_mediaReady || _playbackClip is not { } id || _project.FindClip(id) is not { } p) return;
        var track = _project.FindTrack(p.TrackId)!;
        if (p.Index + 1 < track.Clips.Count)
            ActivatePreview(_project.FindClip(track.Clips[p.Index + 1].Id)!.Value, true);
        else { Pause(); _position = track.Duration; RefreshPosition(); }
    }

    private void StepBackClick(object sender, RoutedEventArgs e) => Seek(_activeTrackId, _position - FrameDuration);
    private void StepForwardClick(object sender, RoutedEventArgs e) => Seek(_activeTrackId, _position + FrameDuration);
}
