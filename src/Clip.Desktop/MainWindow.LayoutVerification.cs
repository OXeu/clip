using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace Clip.Desktop;

public partial class MainWindow
{
    private void VerifyWorkspaceLayout()
    {
        UpdateLayout();
        var previewOrigin = PreviewCanvas.TranslatePoint(new Point(), WindowRoot);
        Require(Math.Abs(previewOrigin.X) < 1 &&
            Math.Abs(previewOrigin.Y - WindowRoot.RowDefinitions[0].ActualHeight) < 1 &&
            Math.Abs(PreviewCanvas.ActualWidth - WindowRoot.ActualWidth) < 1,
            "The preview has an outer border or inset from the command header or window edges.");
        if (!TimelineRegion.IsVisible)
        {
            Require(!TimelineSplitter.IsVisible && TimelineRow.ActualHeight < 1,
                "The empty workspace retained a timeline divider or reserved timeline space.");
            return;
        }

        var timelineTop = TimelineRegion.TranslatePoint(new Point(), WindowRoot).Y;
        var previewBottom = PreviewRegion.TranslatePoint(new Point(0, PreviewRegion.ActualHeight), WindowRoot).Y;
        var dividerCenter = TimelineSplitter.TranslatePoint(new Point(0, TimelineSplitter.ActualHeight / 2), WindowRoot).Y;
        Require(TimelineSplitter.IsVisible && Math.Abs(previewBottom - timelineTop) < 1 && Math.Abs(dividerCenter - timelineTop) <= 1,
            "The timeline divider is not on the shared preview/timeline edge.");
        Require(PreviewCanvas.ActualHeight >= 99 && TimelineRegion.ActualHeight >= TimelineRow.MinHeight - 1 &&
            Math.Abs(WindowRoot.RowDefinitions.Sum(row => row.ActualHeight) - WindowRoot.ActualHeight) < 1,
            "Resizing collapsed a workspace pane or pushed the status bar outside the window.");
    }

    private async Task VerifyWorkspaceResizeAsync(bool realInput)
    {
        var previewHeight = PreviewRow.Height;
        var timelineHeight = TimelineRow.Height;
        var windowHeight = Height;
        try
        {
            VerifyWorkspaceLayout();
            var before = TimelineRow.ActualHeight;
            if (realInput)
            {
                await DragWorkspaceDividerAsync(-50);
                Require(TimelineRow.ActualHeight > before + 35, "Dragging the divider upward did not enlarge the timeline.");
                before = TimelineRow.ActualHeight;
                await DragWorkspaceDividerAsync(30);
                Require(TimelineRow.ActualHeight < before - 20, "Dragging the divider downward did not enlarge the preview.");
            }

            before = TimelineRow.ActualHeight;
            ResizeWorkspaceWithKey(Key.Up);
            Require(TimelineRow.ActualHeight > before + 5, "The focused divider did not respond to the Up key.");
            ResizeWorkspaceWithKey(Key.Down);
            Require(Math.Abs(TimelineRow.ActualHeight - before) < 1, "Keyboard resizing did not return to the previous height.");
            Refresh();
            VerifyWorkspaceLayout();
            Require(Math.Abs(TimelineRow.ActualHeight - before) < 1, "An editor refresh reset the user's pane sizes.");
            TimelineView.Focus();
            UiCapture.Save(WindowRoot, "smoke-resized.png");

            for (var step = 0; step < 100; step++) ResizeWorkspaceWithKey(Key.Up);
            VerifyWorkspaceLayout();
            Require(Math.Abs(PreviewRow.ActualHeight - PreviewRow.MinHeight) <= 1, "The preview minimum height was not enforced.");
            Height = MinHeight;
            VerifyWorkspaceLayout();
            Height = windowHeight;
            UpdateLayout();
            for (var step = 0; step < 100; step++) ResizeWorkspaceWithKey(Key.Down);
            VerifyWorkspaceLayout();
            Require(Math.Abs(TimelineRow.ActualHeight - TimelineRow.MinHeight) <= 1, "The timeline minimum height was not enforced.");
            StartupDiagnostics.Write("Workspace layout verified: edge-to-edge preview, shared divider, native bidirectional drag, keyboard resize, retained split ratio and minimum pane heights.");
        }
        finally
        {
            Height = windowHeight;
            PreviewRow.Height = previewHeight;
            TimelineRow.Height = timelineHeight;
            UpdateLayout();
            TimelineView.Focus();
        }
    }

    private void ResizeWorkspaceWithKey(Key key)
    {
        Require(TimelineSplitter.Focus(), "The workspace divider could not receive keyboard focus.");
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this)!, Environment.TickCount, key)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        TimelineSplitter.RaiseEvent(args);
        Require(!args.Handled, "The editor intercepted a workspace resize key.");
        args.RoutedEvent = Keyboard.KeyDownEvent;
        TimelineSplitter.RaiseEvent(args);
        Require(args.Handled, "The native divider did not handle a resize key.");
        UpdateLayout();
    }

    private async Task DragWorkspaceDividerAsync(double distance)
    {
        Activate();
        UpdateLayout();
        var from = TimelineSplitter.PointToScreen(new Point(TimelineSplitter.ActualWidth / 2, TimelineSplitter.ActualHeight / 2));
        var to = TimelineSplitter.PointToScreen(new Point(TimelineSplitter.ActualWidth / 2, TimelineSplitter.ActualHeight / 2 + distance));
        await Task.Run(() =>
        {
            NativeMouse.SetCursorPos((int)from.X, (int)from.Y);
            Thread.Sleep(100);
            NativeMouse.mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
            try
            {
                Thread.Sleep(100);
                for (var step = 1; step <= 10; step++)
                {
                    NativeMouse.SetCursorPos((int)from.X, (int)(from.Y + (to.Y - from.Y) * step / 10));
                    Thread.Sleep(25);
                }
            }
            finally { NativeMouse.mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); }
        });
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        UpdateLayout();
    }
}
