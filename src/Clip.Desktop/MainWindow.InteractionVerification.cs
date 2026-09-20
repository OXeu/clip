using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Clip.Core;

namespace Clip.Desktop;

public partial class MainWindow
{
    private static void Require(bool condition, string failure)
    {
        if (!condition) throw new InvalidOperationException(failure);
    }

    private void VerifyBrandHeader()
    {
        UpdateLayout();
        WindowPresentation.VerifyCaption(this);
        Require(CommandHeader.Children.Count == 2 && CommandHeader.ColumnDefinitions.Count == 2 &&
            BrandLogo.Source is System.Windows.Media.Imaging.BitmapSource { PixelWidth: > 0, PixelHeight: > 0 } &&
            Grid.GetColumn(BrandLogo) == 0 && Math.Abs(BrandLogo.TranslatePoint(new Point(), WindowRoot).X - 24) < 1,
            "The command header did not show the embedded logo at the left edge.");
    }

    private void VerifyWheelNavigation()
    {
        ResetTimelineZoom();
        UpdateLayout();
        var anchor = TimelineScroll.ViewportWidth * 0.6;
        var time = TimelineView.TimeAtX(anchor);
        ApplyTimelineWheel(120, ModifierKeys.Control, anchor);
        UpdateLayout();
        var pixelTime = TimelineView.TimeAtX(TimelineControl.ContentInset + 2);
        Require(_timelineZoom > 1 && Math.Abs(TimelineView.TimeAtX(TimelineScroll.HorizontalOffset + anchor) - time) <= pixelTime,
            "Ctrl-wheel lost its time anchor.");
        var zoom = _timelineZoom;
        var horizontalOffset = TimelineScroll.HorizontalOffset;
        ApplyTimelineWheel(-120, ModifierKeys.Shift, anchor);
        UpdateLayout();
        Require((SystemParameters.WheelScrollLines == 0 || TimelineScroll.HorizontalOffset > horizontalOffset) && _timelineZoom == zoom,
            "Shift-wheel did not pan the timeline horizontally.");
        horizontalOffset = TimelineScroll.HorizontalOffset;
        var verticalOffset = TimelineScroll.VerticalOffset;
        var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
        TimelineView.RaiseEvent(wheel);
        UpdateLayout();
        Require(wheel.Handled && TimelineScroll.HorizontalOffset == horizontalOffset && _timelineZoom == zoom &&
            (TimelineScroll.ScrollableHeight == 0 || SystemParameters.WheelScrollLines == 0 || TimelineScroll.VerticalOffset > verticalOffset),
            "Routed wheel did not scroll the timeline vertically.");
        ApplyTimelineWheel(-60, ModifierKeys.Control, anchor);
        UpdateLayout();
        Require(_timelineZoom < zoom, "Fractional Ctrl-wheel did not zoom.");
        ApplyTimelineWheel(120000, ModifierKeys.Control, anchor);
        UpdateLayout();
        Require(_timelineZoom == MaximumTimelineZoom, "Zoom maximum failed.");
        ApplyTimelineWheel(-120000, ModifierKeys.Control, anchor);
        UpdateLayout();
        Require(_timelineZoom == 1 && TimelineScroll.HorizontalOffset == 0, "Zoom did not return to fit.");

        SetTimelineZoom(4);
        UpdateLayout();
        var main = _project.MainTrack;
        TimelineScroll.ScrollToHorizontalOffset(TimelineView.XAtTime(main.Clips[0].Duration * 0.75) - 1);
        UpdateLayout();
        var leftEdge = new Point(TimelineScroll.HorizontalOffset + 1,
            TimelineView.TrackTop(0) + TimelineControl.RowHeightFor(main) / 2);
        Require(TimelineView.InsertionAt(leftEdge) == (main.Id, 1),
            "A scrolled drop at the left viewport edge still hit an invisible track-name column.");
        ResetTimelineZoom();
        UpdateLayout();
    }

    private void VerifyPreviewZoom()
    {
        SetPreviewZoom(1, false);
        Require(ApplyPreviewWheel(120) && _previewZoom > 1 &&
            PreviewScale.ScaleX == _previewZoom && PreviewPosterScale.ScaleX == _previewZoom,
            "Preview wheel did not zoom both the video and fallback poster from content-fit.");
        var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
            { RoutedEvent = Mouse.PreviewMouseWheelEvent };
        PreviewCanvas.RaiseEvent(wheel);
        Require(wheel.Handled && Math.Abs(_previewZoom - 1) < 0.001,
            "Routed preview wheel was not handled or did not restore content-fit.");
        SetPreviewZoom(1, false);
    }

    private void RaiseEditorKey(Key key, UIElement? target = null, bool handled = true)
    {
        target ??= TimelineView;
        Require(target.Focus(), $"Cannot focus shortcut target for {key}.");
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this)!, Environment.TickCount, key)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        target.RaiseEvent(args);
        if (handled) Require(args.Handled, $"Shortcut {key} was not handled.");
        args.RoutedEvent = Keyboard.KeyDownEvent;
        target.RaiseEvent(args);
        var up = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this)!, Environment.TickCount, key)
            { RoutedEvent = Keyboard.PreviewKeyUpEvent };
        target.RaiseEvent(up);
        up.RoutedEvent = Keyboard.KeyUpEvent;
        target.RaiseEvent(up);
    }

    private static async Task WaitForPreviewAsync(Func<bool> ready, string failure)
    {
        var deadline = DateTime.UtcNow.AddSeconds(12);
        while (!ready() && DateTime.UtcNow < deadline) await Task.Delay(25);
        Require(ready(), failure);
    }

    private async Task VerifyNativeDragAsync(Guid clipId, Guid trackId, int index, bool realInput)
    {
        Pause();
        ResetTimelineZoom();
        TimelineScroll.ScrollToVerticalOffset(0);
        UpdateLayout();
        var rect = TimelineView.ClipBounds(clipId);
        var track = _project.FindTrack(trackId)!;
        var row = _project.Tracks.ToList().FindIndex(t => t.Id == trackId);
        var x = TimelineView.XAtTime(track.Clips.Take(index).Sum(c => c.Duration)) + 5;
        var target = new Point(x, TimelineView.TrackTop(row) + TimelineControl.RowHeightFor(track) / 2);
        Require(TimelineView.InsertionAt(target) == (trackId, index), "Drop insertion slot did not match its visual marker.");
        if (!realInput) { MoveClip(clipId, trackId, index); return; }
        Activate();
        var from = TimelineView.PointToScreen(new Point(rect.X + Math.Min(40, rect.Width * 0.4), rect.Y + 24));
        var to = TimelineView.PointToScreen(target);
        StartupDiagnostics.Write($"Native drag: {clipId} from {from} to {to}; target track {trackId}, slot {index}.");
        UiCapture.Save(WindowRoot, "smoke-drag-before.png");
        await Task.Run(() =>
        {
            NativeMouse.SetCursorPos((int)from.X, (int)from.Y);
            Thread.Sleep(100);
            NativeMouse.mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
            try
            {
                Thread.Sleep(120);
                for (var step = 1; step <= 12; step++)
                {
                    NativeMouse.SetCursorPos((int)(from.X + (to.X - from.X) * step / 12), (int)(from.Y + (to.Y - from.Y) * step / 12));
                    Thread.Sleep(35);
                }
            }
            finally { NativeMouse.mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); }
        });
        UiCapture.Save(WindowRoot, "smoke-drag-after.png");
        await WaitForPreviewAsync(() => _project.FindClip(clipId) is { } moved && moved.TrackId == trackId &&
            moved.Index == (track.Clips.Any(c => c.Id == clipId) && _project.FindClip(clipId)!.Value.Index < index ? index - 1 : index),
            "Native mouse drag did not move the clip into the target slot.");
    }

    private async Task VerifySpaceFocusAsync(bool realMedia)
    {
        Pause();
        var before = _project.MainTrack.Clips.ToArray();
        Seek(_project.MainTrack.Id, 0);
        UpdateLayout();
        // Opening on the ruler must preserve the selected clip and delete that clip.
        await OpenTimelineMenuAsync(TimelineView, new Point(80, 12), realMedia);
        InvokeTimelineMenuItem(DeleteMenuItem);
        Require(_project.MainTrack.Clips.Count == before.Length - 1, "Context-menu delete did not remove the selected clip.");
        // IsOpen becomes false before WPF finishes closing the popup and restores keyboard focus.
        // Yield to the dispatcher and observe restoration without forcing focus in the test.
        await WaitForPreviewAsync(() => !TimelineMenu.IsOpen && TimelineView.IsKeyboardFocused,
            "Context-menu delete did not return focus to the timeline.");
        if (realMedia) await WaitForPreviewAsync(() => _mediaReady && _operation is null, "Preview failed after delete.");
        var remaining = _project.MainTrack.Clips.ToArray();
        UIElement[] targets = [TimelineView, PlayButton, BackButton, ForwardButton, MoreButton, ExportButton, ExportTracksButton];
        foreach (var target in targets.Where(t => t.IsEnabled && t.IsVisible))
        {
            RaiseEditorKey(Key.Space, target);
            Require(_playing == _mediaReady && _project.MainTrack.Clips.SequenceEqual(remaining), "Space activated a focused button.");
            RaiseEditorKey(Key.Space, target);
            Require(!_playing && _project.MainTrack.Clips.SequenceEqual(remaining), "Space did not pause without editing.");
        }
        SetBusy(true);
        try
        {
            UpdateLayout();
            RaiseEditorKey(Key.Space, CancelButton);
            Require(!_operation!.IsCancellationRequested, "Space activated Cancel while busy.");
        }
        finally { SetBusy(false); }
        Restore(false);
        Require(_project.MainTrack.Clips.SequenceEqual(before), "Space changed project history.");
    }

    private async Task VerifyMoreMenuAsync(bool hasProject)
    {
        MoreClick(this, new RoutedEventArgs());
        await WaitForPreviewAsync(() => MoreMenu.IsOpen, "The More button did not open its dropdown menu.");
        var items = MoreMenu.Items.OfType<MenuItem>().ToArray();
        Require(items.Select(item => item.Header?.ToString()).SequenceEqual(new[] { "设置", "导入项目", "导出项目" }),
            "The More dropdown did not contain the expected commands in order.");
        Require(RestoreProjectMenuItem.IsEnabled && SaveProjectMenuItem.IsEnabled == hasProject,
            "The project commands in the More dropdown used the wrong enabled state.");
        UiCapture.Save(MoreMenu, hasProject ? "smoke-more-menu.png" : "smoke-more-menu-empty.png");
        MoreMenu.IsOpen = false;
    }

    private async Task OpenTimelineMenuAsync(FrameworkElement target, Point point, bool realInput)
    {
        TimelineMenu.IsOpen = false;
        UpdateLayout();
        if (realInput)
        {
            Activate();
            var screen = target.PointToScreen(point);
            await Task.Run(() =>
            {
                NativeMouse.SetCursorPos((int)screen.X, (int)screen.Y);
                Thread.Sleep(100);
                NativeMouse.mouse_event(0x0008, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(50);
                NativeMouse.mouse_event(0x0010, 0, 0, 0, UIntPtr.Zero);
            });
        }
        else
        {
            TimelineView.Focus();
            TimelineMenu.PlacementTarget = target;
            TimelineMenu.IsOpen = true;
        }
        await WaitForPreviewAsync(() => TimelineMenu.IsOpen, "The timeline context menu did not open.");
    }

    private void InvokeTimelineMenuItem(MenuItem item)
    {
        Require(TimelineMenu.IsOpen && item.IsEnabled, "The requested timeline menu item is unavailable.");
        TimelineMenu.IsOpen = false;
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    }

    private async Task VerifyTimelineMenuAsync(bool realInput)
    {
        ResetTimelineZoom();
        TimelineScroll.ScrollToVerticalOffset(0);
        Seek(_project.MainTrack.Id, 0);
        var before = _project.Tracks.ToArray();
        await OpenTimelineMenuAsync(TimelineView,
            new Point(80, TimelineView.TrackTop(0) + TimelineControl.RowHeightFor(_project.Tracks[0]) / 2), realInput);
        Require(!SplitMenuItem.IsEnabled && !DeleteMenuItem.IsEnabled && !CopyMenuItem.IsEnabled && !RenameMenuItem.IsEnabled && UndoMenuItem.IsEnabled && !RedoMenuItem.IsEnabled,
            "An empty track exposed invalid editing actions or hid undo history.");
        TimelineMenu.IsOpen = false;

        var candidate = before[1];
        var clip = candidate.Clips[0];
        var rect = TimelineView.ClipBounds(clip.Id);
        var point = new Point(rect.X + rect.Width * 0.4, rect.Y + rect.Height / 2);
        if (!realInput) SelectClip(clip.Id, TimelineView.TimeAtX(point.X));
        await OpenTimelineMenuAsync(TimelineView, point, realInput);
        Require(_selected == clip.Id && _activeTrackId == candidate.Id && SplitMenuItem.IsEnabled && DeleteMenuItem.IsEnabled,
            "Right-click did not target the candidate clip.");
        var cut = _position;
        Require(cut > 0 && cut < clip.Duration, "Right-click did not place the playhead inside the target clip.");
        UiCapture.Save(TimelineMenu, "smoke-timeline-menu.png");
        InvokeTimelineMenuItem(SplitMenuItem);
        Require(_project.FindTrack(candidate.Id)!.Clips.Count == 2 && _project.MainTrack.Clips.Count == 0 &&
            Math.Abs(_project.FindTrack(candidate.Id)!.Clips[0].Duration - cut) <= 1 / clip.Media.FrameRate,
            "Context-menu split did not use the target track and playhead.");

        var position = _position;
        await OpenTimelineMenuAsync(TimelineView, new Point(80, 12), realInput);
        Require(_position == position, "Opening the ruler context menu moved the playhead.");
        InvokeTimelineMenuItem(UndoMenuItem);
        Require(_project.Tracks.SequenceEqual(before), "Context-menu undo did not restore the split.");
        await OpenTimelineMenuAsync(TimelineRegion, new Point(40, TimelineRegion.ActualHeight - 16), realInput);
        InvokeTimelineMenuItem(RedoMenuItem);
        Require(_project.FindTrack(candidate.Id)!.Clips.Count == 2, "Context-menu redo did not restore the split.");
        await OpenTimelineMenuAsync(TimelineView, new Point(120, 12), realInput);
        InvokeTimelineMenuItem(UndoMenuItem);
        Require(_project.Tracks.SequenceEqual(before), "Timeline menu actions changed unrelated project state.");

        SetBusy(true);
        try
        {
            await OpenTimelineMenuAsync(TimelineRegion, new Point(120, TimelineRegion.ActualHeight - 16), realInput);
            Require(new[] { SplitMenuItem, DeleteMenuItem, CopyMenuItem, RenameMenuItem, UndoMenuItem, RedoMenuItem }.All(item => !item.IsEnabled) &&
                !ExportButton.IsEnabled && !ExportTracksButton.IsEnabled,
                "Timeline context-menu actions remained enabled while busy.");
        }
        finally { TimelineMenu.IsOpen = false; SetBusy(false); }
    }

    private async Task VerifyCopyAndNamingAsync(bool realInput)
    {
        var before = _project.Tracks.ToArray();
        foreach (var track in before.Skip(1))
        {
            ResetTimelineZoom();
            TimelineScroll.ScrollToVerticalOffset(0);
            UpdateLayout();
            var original = track.Clips[0];
            var rect = TimelineView.ClipBounds(original.Id);
            var point = new Point(rect.X + rect.Width / 2, rect.Y + 24);
            if (!realInput) SelectClip(original.Id, TimelineView.TimeAtX(point.X));
            await OpenTimelineMenuAsync(TimelineView, point, realInput);
            InvokeTimelineMenuItem(CopyMenuItem);
            Require(_selected is { } id && id != original.Id, "Copy did not select a new clip.");
            var copyId = _selected!.Value;
            var copy = _project.FindClip(copyId)!.Value;
            Require(copy.Clip with { Id = original.Id } == original, "Copy changed the media or source range.");
            Require(original.Duration < 10
                ? copy.TrackId == track.Id && copy.Index == 1 && _project.Tracks.Count == before.Length
                : _project.Tracks.ToList().FindIndex(t => t.Id == copy.TrackId) == Array.IndexOf(before, track) + 1 &&
                    !_project.FindTrack(copy.TrackId)!.IsMain,
                "Copy used the wrong duration-dependent destination.");
            Restore(false);
            Require(_project.Tracks.SequenceEqual(before), "Undo copy did not restore all tracks.");
            Restore(true);
            Require(_project.FindClip(copyId) == copy, "Redo copy changed its identity.");
            Restore(false);
        }

        TimelineScroll.ScrollToVerticalOffset(0);
        UpdateLayout();
        var clip = before[1].Clips[0];
        var bounds = TimelineView.ClipBounds(clip.Id);
        var target = new Point(bounds.X + bounds.Width * 0.4, bounds.Y + 24);
        if (!realInput) SelectClip(clip.Id, TimelineView.TimeAtX(target.X));
        await OpenTimelineMenuAsync(TimelineView, target, realInput);
        var inputCheck = Dispatcher.InvokeAsync(() =>
        {
            var dialog = OwnedWindows.OfType<ClipNameWindow>().Single();
            try
            {
                WindowPresentation.VerifyCaption(dialog);
                dialog.NameInput.Text = "   ";
                dialog.SaveNameButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Require(dialog.IsVisible && dialog.ValidationText.Visibility == Visibility.Visible, "An empty clip name closed the dialog.");
                RaiseEditorKey(Key.S, dialog.NameInput, false);
                RaiseEditorKey(Key.X, dialog.NameInput, false);
                RaiseEditorKey(Key.Delete, dialog.NameInput, false);
                Require(_project.Tracks.SequenceEqual(before), "Typing in the name dialog edited the timeline.");
                dialog.NameInput.Text = "开场镜头";
                UiCapture.Save((FrameworkElement)dialog.Content, "smoke-clip-name.png");
                dialog.SaveNameButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            }
            finally { if (dialog.IsVisible) dialog.Close(); }
        }, DispatcherPriority.ApplicationIdle);
        InvokeTimelineMenuItem(RenameMenuItem);
        await inputCheck.Task;
        Require(_project.FindClip(clip.Id)!.Value.Clip.DisplayName == "开场镜头" && _project.FindClip(clip.Id)!.Value.Clip.Media == clip.Media,
            "Naming did not update the clip label or changed its source.");
        Restore(false);
        Require(_project.Tracks.SequenceEqual(before), "Undo naming did not restore the original label.");
    }

    private async Task VerifyTrackExportAsync(bool realInput)
    {
        ResetTimelineZoom();
        TimelineScroll.ScrollToVerticalOffset(0);
        var exportable = _project.ExportableTracks.ToArray();
        var track = exportable[1];
        SelectClip(exportable[0].Clips[0].Id, 1);
        Require(_selectedTrackId is null && _project.ResolveExportTrack(_selectedTrackId) is null && ExportTracksButton.IsVisible,
            "Selecting a clip implicitly selected a track for export.");
        ExportClick(this, new RoutedEventArgs());
        await WaitForPreviewAsync(() => ExportTracksMenu.IsOpen && ExportTracksMenu.Items.Count == 2,
            "The export button did not offer all nonempty tracks.");
        var items = ExportTracksMenu.Items.Cast<MenuItem>().ToArray();
        Require(items.Select(item => (Guid)item.Tag).SequenceEqual(_project.ExportableTracks.Select(t => t.Id)),
            "The export dropdown included an empty track or omitted a candidate.");
        var selectionBeforePreview = (_selected, _selectedTrackId, _activeTrackId, _position);
        items[1].RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
        Require(TimelineView.ExportPreviewTrackId == track.Id &&
            selectionBeforePreview == (_selected, _selectedTrackId, _activeTrackId, _position),
            "Hovering an export choice did not highlight its row or changed the editing selection.");
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Require(items[1].Focus() && items[0].Focus() && TimelineView.ExportPreviewTrackId == (Guid)items[0].Tag,
            "Keyboard focus did not preview the export choice.");
        UiCapture.Save(ExportTracksMenu, "smoke-export-tracks.png");
        ExportTracksMenu.IsOpen = false;
        await WaitForPreviewAsync(() => TimelineView.ExportPreviewTrackId is null,
            "Closing the export menu left a preview highlight in the timeline.");
        Require(selectionBeforePreview == (_selected, _selectedTrackId, _activeTrackId, _position),
            "Previewing export choices changed the editing selection.");
        await VerifyTrackExportDialogAsync(() => items[1].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)), track);

        UpdateLayout();
        if (realInput)
        {
            // The strip above clips is part of the track and remains clickable for a full-length clip.
            var row = _project.Tracks.ToList().FindIndex(candidate => candidate.Id == track.Id);
            var point = TimelineView.PointToScreen(new Point(100, TimelineView.TrackTop(row) + 4));
            await Task.Run(() =>
            {
                NativeMouse.SetCursorPos((int)point.X, (int)point.Y);
                Thread.Sleep(100);
                NativeMouse.mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(50);
                NativeMouse.mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
            });
        }
        else SelectTrack(track.Id, 0);
        Require(_selectedTrackId == track.Id && _selected is null && TimelineView.SelectedTrackId == track.Id &&
            _project.ResolveExportTrack(_selectedTrackId)?.Id == track.Id && !CopyMenuItem.IsEnabled,
            "Selecting the track background did not select the whole export track.");
        await VerifyTrackExportDialogAsync(() => ExportClick(this, new RoutedEventArgs()), track);
        SelectClip(exportable[0].Clips[0].Id, 1);
        Require(_selectedTrackId is null && _project.ResolveExportTrack(_selectedTrackId) is null,
            "Clicking a clip retained an implicit track selection.");
    }

    private async Task VerifyTrackExportDialogAsync(Action openDialog, VideoTrack expected)
    {
        var check = Dispatcher.InvokeAsync(() =>
        {
            var dialog = OwnedWindows.OfType<ExportWindow>().Single();
            try
            {
                Require(dialog.SourceNameText.Text == $"{expected.Clips[0].DisplayName} · {expected.Clips.Count} 个片段 · {expected.Duration:0.##} 秒",
                    "The export dialog described the wrong track.");
                Require(dialog.SourceInfoText.Text.StartsWith($"{expected.Clips[0].Media.Width} × {expected.Clips[0].Media.Height}", StringComparison.Ordinal),
                    "Export dimensions came from a different track.");
            }
            finally { dialog.DialogResult = false; }
        }, DispatcherPriority.ApplicationIdle);
        openDialog();
        await check.Task;
    }

    internal async Task VerifyUiAsync()
    {
        Refresh();
        VerifyBrandHeader();
        Require(TimelineRegion.Visibility == Visibility.Collapsed && ExportButtonGroup.Visibility == Visibility.Collapsed, "Editing controls leaked into empty state.");
        await VerifyMoreMenuAsync(false);
        VerifyBrandButtons(this);
        VerifyWorkspaceLayout();
        UiCapture.Save(WindowRoot, "smoke-empty.png");
        var source = Environment.GetEnvironmentVariable("CLIP_UI_TEST_VIDEO");
        var second = Environment.GetEnvironmentVariable("CLIP_UI_TEST_VIDEO_SECOND");
        var realMedia = !string.IsNullOrWhiteSpace(source);
        if (realMedia)
        {
            Require(!string.IsNullOrWhiteSpace(second), "Multi-track UI verification requires the second video fixture.");
            await ImportFilesAsync([source!, second!]);
            await WaitForPreviewAsync(() => _mediaReady && _operation is null, "Imported candidate did not become playable.");
        }
        else
        {
            foreach (var media in new[] { new MediaInfo("Sample A.mp4", 12, 960, 540, 30, 0, 1, "h264"),
                new MediaInfo("Sample B.mp4", 3.2, 540, 960, 24, 0, null, "h264") })
            {
                var track = _project.ImportSeparated(media).VideoTrack;
                AssetFor(media).StaticOnly = true;
                ActivatePreview(_project.FindClip(track.Clips[0].Id)!.Value, false);
            }
        }
        var videoTracks = _project.Tracks.Where(track => track.Kind == TrackKind.Video && track.Clips.Count > 0).ToArray();
        var audioTracks = _project.Tracks.Where(track => track.Kind == TrackKind.Audio).ToArray();
        Require(videoTracks.Length == 2 && audioTracks.Length == videoTracks.Length &&
            _project.MainTrack.Clips.Count == 0 && ExportButton.IsEnabled && ExportTracksButton.IsVisible &&
            _activeTrackId == videoTracks[1].Id, "Imports must create video tracks with companion audio slots and preview the last video track.");
        await VerifyMoreMenuAsync(true);
        var firstVideoRow = _project.Tracks.ToList().FindIndex(track => track.Id == videoTracks[0].Id);
        var firstAudioRow = _project.Tracks.ToList().FindIndex(track => track.Id == audioTracks[0].Id);
        var videoHeight = TimelineView.TrackTop(firstVideoRow + 1) - TimelineView.TrackTop(firstVideoRow);
        var audioHeight = TimelineView.TrackTop(firstAudioRow + 1) - TimelineView.TrackTop(firstAudioRow);
        Require(Math.Abs(videoHeight - audioHeight * 2) < 0.001 &&
            Math.Abs(TimelineView.ClipBounds(videoTracks[0].Clips[0].Id).Height -
                TimelineView.ClipBounds(audioTracks[0].Clips[0].Id).Height * 2) < 0.001,
            "Audio rows and clips must render at exactly half the video height.");
        if (audioTracks.Length >= 2)
        {
            var videoIndex = _project.Tracks.ToList().FindIndex(track => track.Id == videoTracks[0].Id);
            Require(TimelineView.TrackInsertionAt(new Point(10, TimelineView.TrackTop(videoIndex) + 2)) == videoIndex,
                "Track drop geometry did not target the requested row.");
            MoveTrack(audioTracks[1].Id, videoIndex);
            Require(_project.Tracks[videoIndex].Id == videoTracks[1].Id && _project.Tracks[videoIndex + 1].Id == audioTracks[1].Id &&
                _project.Tracks[videoIndex + 2].Id == videoTracks[0].Id && _project.Tracks[videoIndex + 3].Id == audioTracks[0].Id,
                "Dragging an audio slot did not move its complete companion group.");
            MoveTrack(audioTracks[1].Id, videoIndex + 4);
            Require(_project.Tracks[videoIndex].Id == videoTracks[0].Id && _project.Tracks[videoIndex + 1].Id == audioTracks[0].Id &&
                _project.Tracks[videoIndex + 2].Id == videoTracks[1].Id && _project.Tracks[videoIndex + 3].Id == audioTracks[1].Id,
                "Companion groups could not be restored to their original order.");
        }
        MultiSelectClick(this, new RoutedEventArgs());
        ToggleMultiSelectedTrack(videoTracks[0].Id);
        ToggleMultiSelectedTrack(videoTracks[1].Id);
        Require(_multiSelectMode && BindTracksButton.IsEnabled, "Track multi-select did not enable binding.");
        BindTracksClick(this, new RoutedEventArgs());
        Require(videoTracks.Select(track => _project.FindTrack(track.Id)!.BindingId).Distinct().Count() == 1 &&
            _project.FindTrack(videoTracks[0].Id)!.BindingId.HasValue, "Saving track binding did not enter alignment mode.");
        MultiSelectClick(this, new RoutedEventArgs());
        ToggleMultiSelectedTrack(videoTracks[0].Id);
        ToggleMultiSelectedTrack(videoTracks[1].Id);
        UnbindTracksClick(this, new RoutedEventArgs());
        Require(videoTracks.All(track => _project.FindTrack(track.Id)!.BindingId is null), "Track unbinding failed.");
        VerifyBrandHeader();
        UiCapture.Save(WindowRoot, "smoke-imported.png");
        await VerifyWorkspaceResizeAsync(realMedia);
        await VerifyTimelineMenuAsync(realMedia);
        await VerifyCopyAndNamingAsync(realMedia);
        await VerifyTrackExportAsync(realMedia);
        var candidate = videoTracks[0].Id;
        var secondCandidate = videoTracks[1].Id;
        Seek(candidate, 4); RaiseEditorKey(Key.S);
        Seek(candidate, 8); RaiseEditorKey(Key.S);
        Require(_project.FindTrack(candidate)!.Clips.Count == 3 && _project.MainTrack.Clips.Count == 0, "Candidate splits changed the main track.");
        var firstId = _project.FindTrack(candidate)!.Clips[0].Id;
        var secondId = _project.FindTrack(secondCandidate)!.Clips[0].Id;
        await VerifyNativeDragAsync(firstId, _project.MainTrack.Id, 0, realMedia);
        await VerifyNativeDragAsync(secondId, _project.MainTrack.Id, 1, realMedia);
        Require(ExportButton.IsEnabled && _project.MainTrack.Clips.Select(c => c.Id).SequenceEqual(new[] { firstId, secondId }),
            "Cross-track drops did not populate the export track.");
        await VerifyNativeDragAsync(secondId, _project.MainTrack.Id, 0, realMedia);
        Require(_project.MainTrack.Clips[0].Id == secondId, "Same-track reorder failed.");
        Restore(false);
        var mainDuration = _project.MainTrack.Duration;
        VerifyWheelNavigation();
        VerifyPreviewZoom();

        if (realMedia)
        {
            Seek(candidate, 0.8);
            await WaitForPreviewAsync(() => _mediaReady, "Candidate preview did not open.");
            TogglePlay();
            await WaitForPreviewAsync(() => _playing && _pendingSeek is null && Preview.Position.TotalSeconds > 5, "Candidate preview clock did not advance.");
            for (var i = 0; i < 2; i++)
            {
                var sourceBefore = Preview.Position.TotalSeconds;
                var seekBefore = _seekStarted;
                var clipsBefore = ActiveTrack.Clips.Count;
                RaiseEditorKey(Key.S, i == 0 ? PlayButton : TimelineView);
                Require(_playing && _timer.IsEnabled && ActiveTrack.Clips.Count == clipsBefore + 1 && _seekStarted == seekBefore,
                    "Live split stopped, sought, or changed the wrong track.");
                await WaitForPreviewAsync(() => Preview.Position.TotalSeconds > sourceBefore + 0.2, "Live split interrupted the native clock.");
            }
            Pause();
            var lastCandidate = _project.FindTrack(candidate)!.Clips[^1].Id;
            Seek(candidate, 3.8);
            TogglePlay();
            await WaitForPreviewAsync(() => _selected == lastCandidate && _playing, "Preview did not continue along the candidate track.");
            Require(_activeTrackId == candidate && _project.MainTrack.Duration == mainDuration, "Candidate preview leaked into the main track.");
            Pause();
            Seek(_project.MainTrack.Id, 3.8);
            TogglePlay();
            await WaitForPreviewAsync(() => _selected == secondId && _currentAsset?.Media.Path == second && _mediaReady &&
                _playing && Preview.Position.TotalSeconds > 0.2, "Preview failed to switch source while continuing on the main track.");
            Pause();
        }
        Seek(candidate, 0.1);
        var count = ActiveTrack.Clips.Count;
        RaiseEditorKey(Key.X);
        Require(ActiveTrack.Clips.Count == count - 1, "X did not delete the selected candidate.");
        Restore(false);
        RaiseEditorKey(Key.Delete);
        Require(ActiveTrack.Clips.Count == count - 1, "Delete did not delete the selected candidate.");
        Restore(false);
        await VerifySpaceFocusAsync(realMedia);
        Seek(_project.MainTrack.Id, 0);
        if (realMedia) await WaitForPreviewAsync(() => _mediaReady && _operation is null, "Main preview did not restore.");
        UpdateLayout();
        UiCapture.Save(WindowRoot, "smoke-multitrack.png");
        UiCapture.Save(WindowRoot, "smoke-ui.png");
        var width = Width; var height = Height;
        Width = MinWidth; Height = MinHeight;
        UpdateLayout();
        Require(PreviewCanvas.ActualHeight >= 100, "Compact multitrack layout collapsed the preview.");
        VerifyWorkspaceLayout();
        UiCapture.Save(WindowRoot, "smoke-compact.png");
        Width = width; Height = height;
        StartupDiagnostics.Write("Multi-track verification passed: two candidate imports; timeline context menu and busy guards; clip copy / naming and history; explicit track export selection; native cross-track and same-track drag; live S / X / Delete; candidate continuation and cross-source main playback; Space focus and wheel navigation.");
    }

    private static class NativeMouse
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")]
        internal static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extraInfo);
    }
}
