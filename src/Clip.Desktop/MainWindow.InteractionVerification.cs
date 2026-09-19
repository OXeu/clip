using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Clip.Core;

namespace Clip.Desktop;

public partial class MainWindow
{
    private static void Require(bool condition, string failure)
    {
        if (!condition) throw new InvalidOperationException(failure);
    }

    private void VerifyWheelNavigation()
    {
        FitClick(this, new RoutedEventArgs());
        UpdateLayout();
        var anchor = TimelineScroll.ViewportWidth * 0.6;
        var time = TimelineView.TimeAtX(anchor);
        ApplyTimelineWheel(120, ModifierKeys.Shift, anchor);
        UpdateLayout();
        var pixelTime = TimelineView.TimeAtX(TimelineControl.ContentInset + 2);
        Require(ZoomSlider.Value > 1 && Math.Abs(TimelineView.TimeAtX(TimelineScroll.HorizontalOffset + anchor) - time) <= pixelTime,
            "Shift-wheel lost its time anchor.");
        var zoom = ZoomSlider.Value;
        var offset = TimelineScroll.HorizontalOffset;
        var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
        TimelineView.RaiseEvent(wheel);
        UpdateLayout();
        Require(wheel.Handled && (SystemParameters.WheelScrollLines == 0 || TimelineScroll.HorizontalOffset > offset) && ZoomSlider.Value == zoom,
            "Routed wheel did not pan the timeline.");
        ApplyTimelineWheel(-60, ModifierKeys.Control, anchor);
        UpdateLayout();
        Require(ZoomSlider.Value < zoom, "Fractional Ctrl-wheel did not zoom.");
        ApplyTimelineWheel(120000, ModifierKeys.Shift, anchor);
        UpdateLayout();
        Require(ZoomSlider.Value == ZoomSlider.Maximum, "Zoom maximum failed.");
        ApplyTimelineWheel(-120000, ModifierKeys.Shift, anchor);
        UpdateLayout();
        Require(ZoomSlider.Value == 1 && TimelineScroll.HorizontalOffset == 0, "Zoom did not return to fit.");
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
        FitClick(this, new RoutedEventArgs());
        TimelineScroll.ScrollToVerticalOffset(0);
        UpdateLayout();
        var rect = TimelineView.ClipBounds(clipId);
        var track = _project.FindTrack(trackId)!;
        var row = _project.Tracks.ToList().FindIndex(t => t.Id == trackId);
        var x = TimelineView.XAtTime(track.Clips.Take(index).Sum(c => c.Duration)) + 5;
        var target = new Point(x, TimelineControl.RulerHeight + row * TimelineControl.RowHeight + 34);
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
        Require(DeleteButton.Focus(), "Delete button cannot focus.");
        DeleteButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Require(_project.MainTrack.Clips.Count == before.Length - 1 && DeleteButton.IsKeyboardFocused, "Delete did not preserve button focus.");
        if (realMedia) await WaitForPreviewAsync(() => _mediaReady && _operation is null, "Preview failed after delete.");
        var remaining = _project.MainTrack.Clips.ToArray();
        UIElement[] targets = [DeleteButton, SplitButton, UndoButton, PlayButton, ZoomSlider, TimelineView, SettingsButton, ExportButton];
        foreach (var target in targets.Where(t => t.IsEnabled))
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

    internal async Task VerifyUiAsync()
    {
        Refresh();
        Require(TimelineRegion.Visibility == Visibility.Collapsed && ExportButton.Visibility == Visibility.Collapsed, "Editing controls leaked into empty state.");
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
                var track = _project.Import(media);
                AssetFor(media).StaticOnly = true;
                ActivatePreview(_project.FindClip(track.Clips[0].Id)!.Value, false);
            }
        }
        Require(_project.Tracks.Count == 3 && _project.MainTrack.Clips.Count == 0 && !ExportButton.IsEnabled &&
            _activeTrackId == _project.Tracks[2].Id, "Imports must enter separate candidate tracks and preview the last candidate.");
        UiCapture.Save(WindowRoot, "smoke-imported.png");
        var candidate = _project.Tracks[1].Id;
        var secondCandidate = _project.Tracks[2].Id;
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
        Seek(_project.MainTrack.Id, 1);
        SpeedBox.Text = "1.25";
        ApplySpeedClick(this, new RoutedEventArgs());
        Require(_project.FindClip(firstId)!.Value.Clip.Speed == 1.25 && Math.Abs(_project.MainTrack.Duration - 6.4) < 0.02,
            "Custom clip speed did not resize the main track.");
        SpeedBox.Text = "0";
        ApplySpeedClick(this, new RoutedEventArgs());
        Require(SpeedErrorText.Visibility == Visibility.Visible && _project.FindClip(firstId)!.Value.Clip.Speed == 1.25,
            "Invalid speed changed a clip or missed inline feedback.");
        var count = _project.MainTrack.Clips.Count;
        RaiseEditorKey(Key.X, SpeedBox, false);
        RaiseEditorKey(Key.Delete, SpeedBox, false);
        Require(_project.MainTrack.Clips.Count == count, "Speed input triggered deletion.");
        Restore(false);
        var mainDuration = _project.MainTrack.Duration;
        Seek(candidate, 0);
        var candidateId = _selected!.Value;
        SpeedBox.Text = "0.5";
        ApplySpeedClick(this, new RoutedEventArgs());
        Require(_project.FindClip(candidateId)!.Value.Clip.Speed == 0.5 && _project.MainTrack.Duration == mainDuration,
            "Candidate speed changed the main export duration.");
        Restore(false);
        VerifyWheelNavigation();

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
                RaiseEditorKey(Key.S, i == 0 ? PlayButton : ZoomSlider);
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
        count = ActiveTrack.Clips.Count;
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
        var speedBounds = SpeedBox.TransformToAncestor(SourcePane).TransformBounds(new Rect(SpeedBox.RenderSize));
        Require(speedBounds.Bottom <= SourcePane.ActualHeight, "Compact layout hid the speed editor.");
        UiCapture.Save(WindowRoot, "smoke-compact.png");
        Width = width; Height = height;
        StartupDiagnostics.Write("Multi-track verification passed: two candidate imports; native cross-track and same-track drag; main-only export selection; per-clip speed and input guards; live S / X / Delete; candidate continuation and cross-source main playback; Space focus and wheel navigation.");
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
