using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Clip.Core;

namespace Clip.Desktop;

public partial class MainWindow
{
    private void TimelineMenuOpened(object sender, RoutedEventArgs e) => RefreshTimelineMenu();

    private void RefreshTimelineMenu()
    {
        var ready = _operation is null;
        SplitMenuItem.IsEnabled = ready && !_multiSelectMode && ActiveTrack.Clips.Count > 0;
        DeleteMenuItem.IsEnabled = ready && !_multiSelectMode && _selected is { } id && _project.FindClip(id) is not null;
        CopyMenuItem.IsEnabled = RenameMenuItem.IsEnabled = SpeedMenuItem.IsEnabled = DeleteMenuItem.IsEnabled;
        var selectedSpeed = _selected is { } selectedId ? _project.FindClip(selectedId)?.Clip.Speed : null;
        SpeedMenuItem.Header = selectedSpeed is { } speed ? $"片段倍速 · {speed:0.###}×" : "片段倍速";
        foreach (var item in SpeedMenuItem.Items.OfType<MenuItem>().Where(item => item.Tag is string))
            item.IsChecked = selectedSpeed is { } current &&
                double.TryParse((string)item.Tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var preset) &&
                Math.Abs(current - preset) < 0.000001;
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
        if (_operation is not null || _multiSelectMode) return;
        if (_playing) Tick(this, EventArgs.Empty);
        var synchronized = _project.SynchronizedTracks(_activeTrackId).Count(track => track.Clips.Count > 0);
        var id = _project.Split(_activeTrackId, _position);
        if (id is null)
        {
            StatusText.Text = synchronized > 1
                ? "关联轨道无法在此时间点同时分割，请检查有内容的伴生音轨和对齐轨是否覆盖该位置。"
                : "请将播放头移到当前轨道的片段内部再分割。";
            return;
        }
        _selectedTrackId = null;
        _selected = id;
        if (_playing) _selected = _playbackClip = _project.Locate(_activeTrackId, _position)?.Clip.Id;
        else ActivatePreview(_project.FindClip(id.Value)!.Value, false);
        StatusText.Text = synchronized > 1 ? $"已同步分割 {synchronized} 条关联轨道" : "已分割片段";
        Refresh();
    }

    private void MoveClip(Guid id, Guid trackId, int index)
    {
        if (_operation is not null || _multiSelectMode || _project.FindClip(id) is not { } before) return;
        var source = _selected == id ? _project.Locate(_activeTrackId, _position)?.SourceTime ?? before.Clip.Start : before.Clip.Start;
        Pause();
        if (!_project.Move(id, trackId, index)) return;
        var moved = _project.FindClip(id)!.Value;
        ActivatePreview(moved with { SourceTime = Math.Clamp(source, moved.Clip.Start, moved.Clip.End) }, false);
        RevealTrack(trackId);
        StatusText.Text = "已移动片段";
    }

    private void MoveTrack(Guid trackId, int index)
    {
        if (_operation is not null || _multiSelectMode || !_project.MoveTrack(trackId, index)) return;
        StatusText.Text = "已调整音视频轨道组顺序";
        Refresh();
        RevealTrack(trackId);
    }

    private void DeleteClick(object sender, RoutedEventArgs e) => DeleteSelected();
    private void DeleteSelected()
    {
        if (_operation is not null || _multiSelectMode || _selected is not { } id || _project.FindClip(id) is not { } p) return;
        Pause();
        if (!_project.Delete(id)) return;
        var position = _position >= p.TimelineStart ? Math.Max(p.TimelineStart, _position - p.Clip.Duration) : _position;
        Seek(p.TrackId, position);
        StatusText.Text = "已删除片段并收拢间隙";
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
        StatusText.Text = copy.Clip.Duration < 10 ? "已复制片段并插入原片段后方" : "已复制片段到下方";
    }

    private void RenameClick(object sender, RoutedEventArgs e)
    {
        if (_operation is not null || _selected is not { } id || _project.FindClip(id) is not { } clip) return;
        Pause();
        var dialog = new ClipNameWindow(clip.Clip.DisplayName) { Owner = this };
        if (dialog.ShowDialog() == true && _project.Rename(id, dialog.ClipName))
        {
            StatusText.Text = $"片段已命名为“{dialog.ClipName}”";
            Refresh();
        }
    }

    private void SpeedPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string value } &&
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed))
            SetSelectedSpeed(speed);
    }

    private void CustomSpeedClick(object sender, RoutedEventArgs e)
    {
        if (_operation is not null || _selected is not { } id || _project.FindClip(id) is not { } clip) return;
        var dialog = new ClipSpeedWindow(clip.Clip.Speed) { Owner = this };
        if (dialog.ShowDialog() == true) SetSelectedSpeed(dialog.Speed);
    }

    private void SetSelectedSpeed(double speed)
    {
        if (_operation is not null || _multiSelectMode || _selected is not { } id) return;
        VideoClip.ValidateSpeed(speed);
        if (_playing) Tick(this, EventArgs.Empty);
        var cursor = _project.Locate(_activeTrackId, _position);
        if (!_project.SetSpeed(id, speed)) return;
        if (cursor is { } previous && _project.FindClip(previous.Clip.Id) is { } current)
        {
            _position = (current with
            {
                SourceTime = Math.Clamp(previous.SourceTime, current.Clip.Start, current.Clip.End)
            }).TimelineTime;
            if (_mediaReady && _playbackClip == current.Clip.Id) Preview.SpeedRatio = current.Clip.Speed;
        }
        StatusText.Text = $"片段倍速已设为 {speed:0.###}× · 可撤销";
        Refresh();
    }

    private void MultiSelectClick(object sender, RoutedEventArgs e)
    {
        if (_operation is not null) return;
        _multiSelectMode = !_multiSelectMode;
        _multiSelectedTracks.Clear();
        if (_multiSelectMode)
        {
            Pause();
            _selected = null;
            _selectedTrackId = null;
            StatusText.Text = "多选模式：点击需要同步分割的轨道，然后保存并对齐";
        }
        else StatusText.Text = "已退出多选模式";
        Refresh();
    }

    private void ToggleMultiSelectedTrack(Guid trackId)
    {
        if (!_multiSelectMode || _operation is not null || _project.FindTrack(trackId) is not { Clips.Count: > 0 }) return;
        if (!_multiSelectedTracks.Add(trackId)) _multiSelectedTracks.Remove(trackId);
        StatusText.Text = $"已选择 {_multiSelectedTracks.Count} 条轨道" +
            (_multiSelectedTracks.Count < 2 ? "，至少选择两条" : "，可以保存并对齐");
        Refresh();
    }

    private void BindTracksClick(object sender, RoutedEventArgs e)
    {
        if (!_multiSelectMode || _operation is not null) return;
        var count = _multiSelectedTracks.Count;
        if (_project.BindTracks(_multiSelectedTracks) is null)
        {
            StatusText.Text = "请至少选择两条包含片段的轨道。";
            return;
        }
        _multiSelectMode = false;
        _multiSelectedTracks.Clear();
        StatusText.Text = $"已进入对齐模式：{count} 条轨道的分割会同步，删除仍只影响当前片段";
        Refresh();
    }

    private void UnbindTracksClick(object sender, RoutedEventArgs e)
    {
        if (!_multiSelectMode || _operation is not null || !_project.UnbindTracks(_multiSelectedTracks)) return;
        _multiSelectMode = false;
        _multiSelectedTracks.Clear();
        StatusText.Text = "已解除所选轨道的绑定";
        Refresh();
    }

    private void Refresh()
    {
        var ready = _operation is null;
        var hasMedia = _project.Sources.Count > 0;
        if (_selectedTrackId is { } trackId && _project.FindTrack(trackId) is null) _selectedTrackId = null;
        _multiSelectedTracks.RemoveWhere(id => _project.FindTrack(id) is null);
        var preview = _playbackClip is { } id ? _project.FindClip(id) : null;
        var any = ActiveTrack.Clips.Count > 0;
        TimelineView.Duration = _project.Duration;
        var mediaVisibility = hasMedia ? Visibility.Visible : Visibility.Collapsed;
        TimelineRegion.Visibility = TimelineSplitter.Visibility = ImportButton.Visibility = ExportButtonGroup.Visibility = mediaVisibility;
        SaveProjectMenuItem.IsEnabled = ready && hasMedia;
        RestoreProjectMenuItem.IsEnabled = ready;
        PreviewFooter.Visibility = mediaVisibility;
        TimelineRow.MinHeight = hasMedia ? 180 : 0;
        // 两个星号行由原生 GridSplitter 调整比例；编辑、播放和窗口缩放不重置用户的布局。
        if (hasMedia && !TimelineRow.Height.IsStar)
        {
            var timelineHeight = Math.Clamp(TimelineView.ContentHeight + 88, 260, 332);
            var availableHeight = WindowRoot.ActualHeight - WindowRoot.RowDefinitions[0].ActualHeight - WindowRoot.RowDefinitions[3].ActualHeight;
            PreviewRow.Height = new GridLength(Math.Max(PreviewRow.MinHeight, availableHeight - timelineHeight), GridUnitType.Star);
            TimelineRow.Height = new GridLength(timelineHeight, GridUnitType.Star);
        }
        else if (!hasMedia)
        {
            PreviewRow.Height = new GridLength(1, GridUnitType.Star);
            TimelineRow.Height = new GridLength(0);
        }
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
        TimelineView.MultiSelectMode = _multiSelectMode;
        TimelineView.MultiSelectedTrackIds = _multiSelectedTracks;
        TimelineView.Waveforms = _waveforms;
        TimelineView.IsEnabled = ready;
        Title = hasMedia ? $"{_project.Sources.Count} 个素材 — 视频剪辑" : "视频剪辑";
        var bound = _project.BindingTracks(ActiveTrack.Id).Count;
        var trackType = ActiveTrack.Kind == TrackKind.Audio
            ? (ActiveTrack.CompanionGroupId.HasValue ? "伴生音频槽" : "音频轨")
            : (ActiveTrack.CompanionGroupId.HasValue ? "视频轨 · 含伴生音频" : "视频轨");
        TimelineSummaryText.Text = $"{trackType} · {ActiveTrack.Clips.Count} 片段 · {ActiveTrack.Duration:0.##} 秒" +
            (bound > 1 ? $" · 对齐组 {bound} 轨" : "");
        MultiSelectButton.Content = _multiSelectMode ? "退出多选" : "多选轨道";
        MultiSelectButton.IsEnabled = ready && _project.Tracks.Any(track => track.Clips.Count > 0);
        BindTracksButton.Visibility = UnbindTracksButton.Visibility = _multiSelectMode ? Visibility.Visible : Visibility.Collapsed;
        BindTracksButton.IsEnabled = ready && _multiSelectedTracks.Count >= 2;
        UnbindTracksButton.IsEnabled = ready && _multiSelectedTracks.Any(id => _project.FindTrack(id)?.BindingId is not null);
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
