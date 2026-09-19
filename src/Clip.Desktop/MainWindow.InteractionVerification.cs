using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Clip.Desktop;

public partial class MainWindow
{
    private void VerifyWheelNavigation()
    {
        FitClick(this, new RoutedEventArgs());
        UpdateLayout();
        var anchor = TimelineScroll.ViewportWidth * 0.6;
        var time = TimelineView.TimeAtX(anchor);
        ApplyTimelineWheel(120, ModifierKeys.Shift, anchor);
        UpdateLayout();
        var pixelTime = Math.Abs(TimelineView.TimeAtX(42) - TimelineView.TimeAtX(40));
        if (ZoomSlider.Value <= 1 || Math.Abs(TimelineView.TimeAtX(TimelineScroll.HorizontalOffset + anchor) - time) > pixelTime)
            throw new InvalidOperationException("Shift + wheel did not zoom around the mouse position.");

        var zoom = ZoomSlider.Value;
        var offset = TimelineScroll.HorizontalOffset;
        var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
            { RoutedEvent = Mouse.PreviewMouseWheelEvent };
        TimelineView.RaiseEvent(wheel);
        UpdateLayout();
        if (!wheel.Handled || (SystemParameters.WheelScrollLines != 0 && TimelineScroll.HorizontalOffset <= offset) || ZoomSlider.Value != zoom)
            throw new InvalidOperationException("The routed wheel event did not pan the horizontal timeline.");

        ApplyTimelineWheel(-60, ModifierKeys.Control, anchor);
        UpdateLayout();
        if (ZoomSlider.Value >= zoom) throw new InvalidOperationException("High-resolution Ctrl + wheel zoom did not work.");
        ApplyTimelineWheel(120000, ModifierKeys.Shift, anchor);
        UpdateLayout();
        if (ZoomSlider.Value != ZoomSlider.Maximum) throw new InvalidOperationException("Wheel zoom exceeded its maximum.");
        ApplyTimelineWheel(-120000, ModifierKeys.Shift, anchor);
        UpdateLayout();
        if (ZoomSlider.Value != ZoomSlider.Minimum || TimelineScroll.HorizontalOffset != 0)
            throw new InvalidOperationException("Wheel zoom did not return to the fitted timeline.");
    }

    private void RaiseEditorKey(Key key, UIElement? target = null)
    {
        target ??= TimelineView;
        if (!target.Focus()) throw new InvalidOperationException($"Could not focus the target for shortcut {key}.");
        var keyEvent = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this)!, Environment.TickCount, key)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        target.RaiseEvent(keyEvent);
        if (!keyEvent.Handled) throw new InvalidOperationException($"Editor shortcut {key} was not handled.");
        keyEvent.RoutedEvent = Keyboard.KeyDownEvent;
        target.RaiseEvent(keyEvent);
        var keyUp = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this)!, Environment.TickCount, key)
            { RoutedEvent = Keyboard.PreviewKeyUpEvent };
        target.RaiseEvent(keyUp);
        keyUp.RoutedEvent = Keyboard.KeyUpEvent;
        target.RaiseEvent(keyUp);
    }

    private void VerifySpacePlaybackFocus()
    {
        var original = _timeline.Segments.ToArray();
        Seek(1);
        _selected = original[1].Id;
        Refresh();
        UpdateLayout();
        if (!DeleteButton.Focus()) throw new InvalidOperationException("Could not focus the delete button.");
        DeleteButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var remaining = original.Where(segment => segment.Id != original[1].Id).ToArray();
        if (!_timeline.Segments.SequenceEqual(remaining) || !DeleteButton.IsKeyboardFocused || _playing)
            throw new InvalidOperationException("Delete did not leave the next segment selected with the button focused.");

        UIElement[] targets = [DeleteButton, SplitButton, UndoButton, PlayButton, BackButton, ForwardButton,
            ZoomSlider, TimelineView, SettingsButton, ExportButton];
        foreach (var target in targets.Where(target => target.IsEnabled))
        {
            RaiseEditorKey(Key.Space, target);
            if (_playing != _mediaReady || _timer.IsEnabled != _mediaReady ||
                !_timeline.Segments.SequenceEqual(remaining) || !target.IsKeyboardFocused)
                throw new InvalidOperationException("Space did not exclusively start playback while preserving keyboard focus.");
            RaiseEditorKey(Key.Space, target);
            if (_playing || _timer.IsEnabled || !_timeline.Segments.SequenceEqual(remaining) || !target.IsKeyboardFocused)
                throw new InvalidOperationException("Space did not exclusively pause playback while preserving keyboard focus.");
        }

        var mediaReady = _mediaReady;
        try
        {
            _mediaReady = false;
            RaiseEditorKey(Key.Space, DeleteButton);
            if (_playing || !_timeline.Segments.SequenceEqual(remaining))
                throw new InvalidOperationException("Space activated a button while preview was unavailable.");
        }
        finally { _mediaReady = mediaReady; }

        SetBusy(true);
        try
        {
            UpdateLayout();
            RaiseEditorKey(Key.Space, CancelButton);
            if (_playing || _operation!.IsCancellationRequested || !_timeline.Segments.SequenceEqual(remaining))
                throw new InvalidOperationException("Space activated the cancel button during a busy operation.");
        }
        finally { SetBusy(false); }

        Restore(false);
        if (!_timeline.Segments.SequenceEqual(original))
            throw new InvalidOperationException("Space changed the edit history after deletion.");
        StartupDiagnostics.Write("Space focus checks passed: delete then play/pause; focused buttons and zoom slider; unavailable preview; busy operation.");
    }

    private static async Task WaitForPreviewAsync(Func<bool> ready, string failure)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!ready() && DateTime.UtcNow < deadline) await Task.Delay(25);
        if (!ready()) throw new InvalidOperationException(failure);
    }

    private async Task VerifyTimelineInteractionsAsync(bool hasVideoFixture)
    {
        VerifyWheelNavigation();
        Seek(1);
        var count = _timeline.Segments.Count;
        RaiseEditorKey(Key.S);
        if (_playing || _timeline.Segments.Count != count + 1)
            throw new InvalidOperationException("Paused S shortcut did not split while remaining paused.");
        RaiseEditorKey(Key.X);
        if (_timeline.Segments.Count != count) throw new InvalidOperationException("X did not delete the selected segment.");
        Restore(false);
        RaiseEditorKey(Key.Delete);
        if (_timeline.Segments.Count != count) throw new InvalidOperationException("Delete shortcut regressed.");
        Restore(false);
        Restore(false);
        if (hasVideoFixture)
            await WaitForPreviewAsync(() => _mediaReady && _operation is null, "Native video preview did not become ready.");
        VerifySpacePlaybackFocus();
        if (!hasVideoFixture)
        {
            StartupDiagnostics.Write("Timeline keyboard and wheel checks passed; no video fixture supplied for native playback verification.");
            return;
        }

        Seek(1);
        TogglePlay();
        await WaitForPreviewAsync(() => _playing && _pendingSeek is null && Preview.Position.TotalSeconds > 1.2,
            "Native preview clock did not advance before live splitting.");
        for (var i = 0; i < 2; i++)
        {
            count = _timeline.Segments.Count;
            var sourceBefore = Preview.Position.TotalSeconds;
            var seekBefore = _seekStarted;
            // S must work after starting playback or adjusting the zoom slider.
            RaiseEditorKey(Key.S, i == 0 ? PlayButton : ZoomSlider);
            if (!_playing || !_timer.IsEnabled || PlayGlyph.Kind != "Pause" || _timeline.Segments.Count != count + 1 ||
                _seekStarted != seekBefore || _pendingSeek is not null)
                throw new InvalidOperationException("S interrupted playback or sought the native media clock.");
            VerifyWheelNavigation();
            await WaitForPreviewAsync(() => Preview.Position.TotalSeconds > sourceBefore + 0.25,
                "Native preview did not keep playing after S / wheel navigation.");
            if (!_playing || _seekStarted != seekBefore)
                throw new InvalidOperationException("Playback paused or sought across a contiguous split.");
        }
        Pause();
        StartupDiagnostics.Write("Timeline interaction verification passed: routed S / X / Delete; anchored Shift / Ctrl wheel zoom; horizontal wheel; native media clock advanced across two live splits without pause or seek.");
    }
}
