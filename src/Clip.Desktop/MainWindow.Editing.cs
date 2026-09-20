using System.Windows;
using System.Windows.Media;
using Clip.Core;

namespace Clip.Desktop;

public partial class MainWindow
{
    private void TimelineMenuOpened(object sender, RoutedEventArgs e) => RefreshTimelineMenu();

    private void RefreshTimelineMenu()
    {
        var ready = _operation is null;
        SplitMenuItem.IsEnabled = ready && ActiveTrack.Clips.Count > 0;
        DeleteMenuItem.IsEnabled = ready && _selected is { } id && _project.FindClip(id) is not null;
        CopyMenuItem.IsEnabled = RenameMenuItem.IsEnabled = DeleteMenuItem.IsEnabled;
        UndoMenuItem.IsEnabled = ready && _project.CanUndo;
        RedoMenuItem.IsEnabled = ready && _project.CanRedo;
        SplitButton.IsEnabled = SplitMenuItem.IsEnabled;
        DeleteButton.IsEnabled = DeleteMenuItem.IsEnabled;
        UndoButton.IsEnabled = UndoMenuItem.IsEnabled;
        RedoButton.IsEnabled = RedoMenuItem.IsEnabled;
    }

    private void SplitClick(object sender, RoutedEventArgs e) => Split();
    private void Split()
    {
        if (_operation is not null) return;
        if (_playing) Tick(this, EventArgs.Empty);
        var id = _project.Split(_activeTrackId, _position);
        if (id is null) { StatusText.Text = "请将播放头移到当前轨道的片段内部再分割。"; return; }
        _selectedTrackId = null;
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
        StatusText.Text = $"已移动片段到 {_project.FindTrack(trackId)!.Name} · 可选中该轨道导出。";
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

    private void CopyClick(object sender, RoutedEventArgs e)
    {
        if (_operation is not null || _selected is not { } id) return;
        Pause();
        if (_project.Duplicate(id) is not { } copyId) return;
        var copy = _project.FindClip(copyId)!.Value;
        ActivatePreview(copy, false);
        RevealTrack(copy.TrackId);
        StatusText.Text = copy.Clip.Duration < 10 ? "已复制片段并插入原片段后方" : "已复制片段到原轨道下方的新候选轨道";
    }

    private void RenameClick(object sender, RoutedEventArgs e)
    {
        if (_operation is not null || _selected is not { } id || _project.FindClip(id) is not { } clip) return;
        Pause();
        var dialog = new ClipNameWindow(clip.Clip.DisplayName) { Owner = this };
        if (dialog.ShowDialog() == true && _project.Rename(id, dialog.ClipName))
        {
            StatusText.Text = $"片段已命名为“{dialog.ClipName}” · Ctrl + Z 撤销";
            Refresh();
        }
    }

    private void Refresh()
    {
        var ready = _operation is null;
        var hasMedia = _project.Sources.Count > 0;
        if (_selectedTrackId is { } trackId && _project.FindTrack(trackId) is null) _selectedTrackId = null;
        var preview = _playbackClip is { } id ? _project.FindClip(id) : null;
        var any = ActiveTrack.Clips.Count > 0;
        TimelineView.Duration = _project.Duration;
        var mediaVisibility = hasMedia ? Visibility.Visible : Visibility.Collapsed;
        TimelineRegion.Visibility = ImportButton.Visibility = ExportButtonGroup.Visibility = mediaVisibility;
        PreviewFooter.Visibility = mediaVisibility;
        TimelineRow.Height = new GridLength(hasMedia ? Math.Clamp(TimelineView.ContentHeight + 88, 260, 332) : 0);
        TimelineView.Height = TimelineView.ContentHeight;
        PreviewFooterRow.Height = new GridLength(hasMedia ? 64 : 0);
        EmptyPreview.Visibility = hasMedia ? Visibility.Collapsed : Visibility.Visible;
        NoSegmentsOverlay.Visibility = hasMedia && !any ? Visibility.Visible : Visibility.Collapsed;
        PreviewCanvas.SetResourceReference(System.Windows.Controls.Panel.BackgroundProperty, hasMedia ? "ColorPreviewBackground" : "ColorNeutralBackground1");
        PreviewPoster.Source = preview is { } chosen ? AssetFor(chosen.Clip.Media).Thumbnail : null;
        PreviewPoster.Visibility = hasMedia && any && !_mediaReady ? Visibility.Visible : Visibility.Collapsed;
        Preview.Visibility = any && _mediaReady ? Visibility.Visible : Visibility.Hidden;
        RefreshExportButton();
        RefreshTimelineMenu();
        PlayButton.IsEnabled = ready && any && _mediaReady;
        BackButton.IsEnabled = ForwardButton.IsEnabled = ready && any;
        TimelineView.ActiveTrackId = _activeTrackId;
        TimelineView.SelectedId = _selected;
        TimelineView.SelectedTrackId = _selectedTrackId;
        TimelineView.IsEnabled = ready;
        DocumentTitle.Text = hasMedia ? $"{_project.Sources.Count} 个素材 · {_project.ExportableTracks.Count} 条可导出轨道" : "新建剪辑";
        Title = hasMedia ? $"{_project.Sources.Count} 个素材 — 视频剪辑" : "视频剪辑";
        TimelineSummaryText.Text = $"{ActiveTrack.Name} · {ActiveTrack.Clips.Count} 片段 · {ActiveTrack.Duration:0.##} 秒";
        TimelineSummaryText.ToolTip = $"共 {_project.Tracks.Count} 条轨道；点击轨道空白处选中整轨，或通过导出按钮的箭头选择轨道。";
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
