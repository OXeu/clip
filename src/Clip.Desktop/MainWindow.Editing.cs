using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Clip.Core;

namespace Clip.Desktop;

public partial class MainWindow
{
    private void SplitClick(object sender, RoutedEventArgs e) => Split();
    private void Split()
    {
        if (_operation is not null) return;
        if (_playing) Tick(this, EventArgs.Empty);
        var id = _project.Split(_activeTrackId, _position);
        if (id is null) { StatusText.Text = "请将播放头移到当前轨道的片段内部再分割。"; return; }
        _selected = id;
        if (_playing) _selected = _playbackClip = _project.Locate(_activeTrackId, _position)?.Clip.Id;
        else ActivatePreview(_project.FindClip(id.Value)!.Value, false);
        StatusText.Text = "已分割当前轨道 · 可跨轨拖拽，Delete / X 删除。";
        Refresh();
    }

    private void MoveClip(Guid id, Guid trackId, int index)
    {
        if (_operation is not null || _project.FindClip(id) is not { } before) return;
        var source = _selected == id ? _project.Locate(_activeTrackId, _position)?.SourceTime ?? before.Clip.Start : before.Clip.Start;
        Pause();
        if (!_project.Move(id, trackId, index)) return;
        var moved = _project.FindClip(id)!.Value;
        ActivatePreview(moved with { SourceTime = Math.Clamp(source, moved.Clip.Start, moved.Clip.End) }, false);
        RevealTrack(trackId);
        StatusText.Text = _project.FindTrack(trackId)!.IsMain ? "已移入主轨，将参与最终导出。" : "已移入候选轨，不参与最终导出。";
    }

    private void DeleteClick(object sender, RoutedEventArgs e) => DeleteSelected();
    private void DeleteSelected()
    {
        if (_operation is not null || _selected is not { } id || _project.FindClip(id) is not { } p) return;
        Pause();
        if (!_project.Delete(id)) return;
        var position = _position >= p.TimelineStart ? Math.Max(p.TimelineStart, _position - p.Clip.Duration) : _position;
        Seek(p.TrackId, position);
        StatusText.Text = "已删除片段并收拢当前轨道 · Ctrl + Z 撤销";
        Refresh();
    }

    private void UndoClick(object sender, RoutedEventArgs e) => Restore(false);
    private void RedoClick(object sender, RoutedEventArgs e) => Restore(true);
    private void Restore(bool redo)
    {
        if (_operation is not null) return;
        Pause();
        if (!(redo ? _project.Redo() : _project.Undo())) return;
        var selected = _selected is { } id ? _project.FindClip(id) : null;
        var target = selected ?? _project.Locate(_activeTrackId, _position) ??
            _project.Tracks.Where(t => t.Clips.Count > 0).Select(t => _project.FindClip(t.Clips[0].Id)).FirstOrDefault();
        if (target is { } p) ActivatePreview(p, false);
        else { _selected = _playbackClip = null; _activeTrackId = _project.MainTrack.Id; _position = 0; }
        StatusText.Text = redo ? "已重做上一步操作" : "已撤销上一步操作";
        Refresh();
    }

    private void ApplySpeedClick(object sender, RoutedEventArgs e) => ApplySpeed();
    private void SpeedKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { ApplySpeed(); e.Handled = true; }
        else if (e.Key == Key.Escape) { Refresh(); TimelineView.Focus(); e.Handled = true; }
    }
    private void ApplySpeed()
    {
        if (_operation is not null || _selected is not { } id) return;
        var text = SpeedBox.Text.Trim().TrimEnd('×', 'x', 'X');
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var speed) &&
            !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out speed))
        {
            SpeedErrorText.Text = "请输入 0.1–8 之间的速度。";
            SpeedErrorText.Visibility = Visibility.Visible;
            return;
        }
        try
        {
            VideoClip.ValidateSpeed(speed);
            if (_playing) Tick(this, EventArgs.Empty);
            var cursor = _project.Locate(_activeTrackId, _position);
            if (_project.SetSpeed(id, speed) && _project.FindClip(cursor?.Clip.Id ?? id) is { } p)
            {
                _position = (p with { SourceTime = Math.Clamp(cursor?.SourceTime ?? p.Clip.Start, p.Clip.Start, p.Clip.End) }).TimelineTime;
                if (_mediaReady) Preview.SpeedRatio = p.Clip.Speed;
                StatusText.Text = $"当前片段速度：{speed:0.###}× · 仅影响此片段，可撤销。";
            }
            Refresh();
        }
        catch (ArgumentException error) { SpeedErrorText.Text = error.Message; SpeedErrorText.Visibility = Visibility.Visible; }
    }

    private void Refresh()
    {
        var ready = _operation is null;
        var hasMedia = _project.Sources.Count > 0;
        var selected = _selected is { } id ? _project.FindClip(id) : null;
        var any = ActiveTrack.Clips.Count > 0;
        TimelineView.Duration = _project.Duration;
        var mediaVisibility = hasMedia ? Visibility.Visible : Visibility.Collapsed;
        SourcePane.Visibility = TimelineRegion.Visibility = ImportButton.Visibility = ExportButton.Visibility = mediaVisibility;
        PreviewHeader.Visibility = PreviewFooter.Visibility = mediaVisibility;
        SourceColumn.Width = new GridLength(hasMedia ? 224 : 0);
        SourceGapColumn.Width = new GridLength(hasMedia ? 20 : 0);
        TimelineRow.Height = new GridLength(hasMedia ? Math.Clamp(TimelineView.ContentHeight + 96, 268, 340) : 0);
        TimelineView.Height = TimelineView.ContentHeight;
        PreviewHeaderRow.Height = new GridLength(hasMedia ? 48 : 0);
        PreviewFooterRow.Height = new GridLength(hasMedia ? 64 : 0);
        EmptyPreview.Visibility = hasMedia ? Visibility.Collapsed : Visibility.Visible;
        NoSegmentsOverlay.Visibility = hasMedia && !any ? Visibility.Visible : Visibility.Collapsed;
        PreviewCanvas.Background = (Brush)FindResource(hasMedia ? "ColorPreviewBackground" : "ColorNeutralBackground1");
        PreviewPoster.Source = selected is { } chosen ? AssetFor(chosen.Clip.Media).Thumbnail : null;
        PreviewPoster.Visibility = hasMedia && any && !_mediaReady ? Visibility.Visible : Visibility.Collapsed;
        Preview.Visibility = any && _mediaReady ? Visibility.Visible : Visibility.Hidden;
        ExportButton.IsEnabled = ready && _project.MainTrack.Clips.Count > 0;
        ExportButton.ToolTip = _project.MainTrack.Clips.Count > 0 ? "只导出主轨，候选轨不参与" : "请先把候选片段拖入主轨";
        SplitButton.IsEnabled = ready && any;
        PlayButton.IsEnabled = ready && any && _mediaReady;
        BackButton.IsEnabled = ForwardButton.IsEnabled = ready && any;
        UndoButton.IsEnabled = ready && _project.CanUndo;
        RedoButton.IsEnabled = ready && _project.CanRedo;
        DeleteButton.IsEnabled = ready && selected is not null;
        TimelineView.ActiveTrackId = _activeTrackId;
        TimelineView.SelectedId = _selected;
        TimelineView.IsEnabled = ready;
        SelectionPanel.Visibility = DeleteButton.Visibility = selected is not null ? Visibility.Visible : Visibility.Collapsed;
        SpeedBox.IsEnabled = ApplySpeedButton.IsEnabled = ready && selected is not null;
        SpeedErrorText.Visibility = Visibility.Collapsed;
        FileNameText.Text = selected?.Clip.Media.FileName ?? "请选择一个片段";
        SelectionText.Text = selected is { } selection ? $"{ActiveTrack.Name} · 片段 {selection.Index + 1:00}" : "尚未选择片段";
        InText.Text = selected is { } start ? TimelineControl.FormatTime(start.Clip.Start) : "—";
        OutText.Text = selected is { } end ? TimelineControl.FormatTime(end.Clip.End) : "—";
        LengthText.Text = selected is { } length ? TimelineControl.FormatTime(length.Clip.Duration) : "—";
        SpeedBox.Text = selected is { } speed ? speed.Clip.Speed.ToString("0.###", CultureInfo.CurrentCulture) + "×" : "1×";
        if (selected is { } info)
        {
            var media = info.Clip.Media;
            Thumbnail.Source = AssetFor(media).Thumbnail;
            SourceSummaryText.Text = $"{media.Width} × {media.Height} · {media.Duration:0.##} 秒";
            MediaDetailsText.Text = $"{media.FrameRate:0.##} fps · {media.Codec.ToUpperInvariant()}\n{(media.HasAudio ? "含音频" : "无音频")}";
        }
        PreviewTrackText.Text = $"预览 · {ActiveTrack.Name}";
        var previewMode = _currentAsset?.StaticOnly == true ? "静态预览" : _currentAsset?.ProxyAttempted == true ? "兼容预览" : "原始素材";
        PreviewModeText.Text = selected is { } current ? $"{previewMode} · {current.Clip.Speed:0.##}×" : "";
        DocumentTitle.Text = hasMedia ? $"{_project.Sources.Count} 个素材 · 主轨 {_project.MainTrack.Duration:0.##} 秒" : "新建剪辑";
        Title = hasMedia ? $"{_project.Sources.Count} 个素材 — 视频剪辑" : "视频剪辑";
        TimelineSummaryText.Text = $"主轨 {_project.MainTrack.Clips.Count} 片段 · {_project.MainTrack.Duration:0.##} 秒";
        TimelineSummaryText.ToolTip = $"共 {_project.Tracks.Count} 条轨道；候选轨不会导出。";
        RefreshPosition();
    }

    private void RefreshPosition()
    {
        TimelineView.Position = _position;
        TimelineView.InvalidateVisual();
        PositionText.Text = TimelineControl.FormatTime(_position);
        TotalText.Text = "/ " + TimelineControl.FormatTime(ActiveTrack.Duration);
    }
}
