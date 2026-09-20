using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Clip.Core.Updates;
using Clip.Desktop.Design;
using Clip.Desktop.Updates;

namespace Clip.Desktop;

public partial class MainWindow
{
    internal async Task VerifyAppearanceAsync()
    {
        var originalTheme = UiTheme.IsDark;
        var export = new ExportWindow(CurrentMedia!, false) { Owner = this };
        try
        {
            export.Show();
            foreach (var dark in new[] { false, true })
            {
                UiTheme.Apply(dark);
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                UpdateLayout();
                export.UpdateLayout();
                var theme = dark ? "dark" : "light";
                Require(SameColor(WindowRoot.Background, (Brush)FindResource("ColorNeutralBackground3")),
                    "An existing editor retained the previous theme.");
                Require(SameColor(export.QualityBox.Background, (Brush)FindResource("ColorNeutralBackground1")),
                    "An existing export field retained the previous theme.");
                Require(Contrast(ExportButton.Foreground, ExportButton.Background) >= 4.5,
                    "The primary action does not have readable text.");
                UiCapture.Save(WindowRoot, $"smoke-theme-{theme}-editor.png");
                UiCapture.Save(export.DialogRoot, $"smoke-theme-{theme}-export.png");

                export.QualityBox.IsDropDownOpen = true;
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var selected = (ComboBoxItem)export.QualityBox.SelectedItem;
                selected.ApplyTemplate();
                Require(selected.Template.FindName("Choice", selected) is Border choice &&
                    SameColor(choice.Background, (Brush)FindResource("ColorSelectionBackground")),
                    "Dropdown selection did not use the neutral selection surface.");
                export.QualityBox.IsDropDownOpen = false;

                var naming = new ClipNameWindow("开场镜头") { Owner = this };
                CaptureWindow(naming, $"smoke-theme-{theme}-name.png");
                var notice = new NoticeWindow("关闭视频剪辑", "关闭后轨道编辑不会保存。请确认已导出需要的片段。", "确认关闭", "继续剪辑") { Owner = this };
                CaptureWindow(notice, $"smoke-theme-{theme}-notice.png");
                using var http = new HttpClient(new AppearanceUpdateHandler());
                var update = new UpdateWindow(new(http), AppBuild.FromAssembly(typeof(App).Assembly), UpdateChannel.Release) { Owner = this };
                try
                {
                    update.Show();
                    await WaitForPreviewAsync(() => update._check.IsEnabled && update._status.Text == "尚未发布正式版本。",
                        "The fixture update check did not complete.");
                    Require(!update._install.IsEnabled && update._status.Text == "尚未发布正式版本。",
                        "The no-update state was not correctly presented.");
                    UiCapture.Save(update.UpdateRoot, $"smoke-theme-{theme}-update.png");
                }
                finally { update.Close(); }
            }

            export.QualityHelpButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Require(export.QualityHelp.IsOpen, "The quality help action did not open its explanation.");
            var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(export)!, Environment.TickCount, Key.Escape)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            export.QualityHelpButton.RaiseEvent(escape);
            Require(escape.Handled && !export.QualityHelp.IsOpen && export.IsVisible,
                "Escape did not dismiss only the quality explanation.");
        }
        finally
        {
            export.Close();
            UiTheme.Apply(originalTheme);
        }

        SetBusy(true);
        try
        {
            Require(!SplitButton.IsEnabled && !DeleteButton.IsEnabled && !UndoButton.IsEnabled && !RedoButton.IsEnabled,
                "The edit toolbar remained enabled during an operation.");
        }
        finally { SetBusy(false); }
        ResetTimelineZoom();
        ZoomInClick(this, new RoutedEventArgs());
        Require(_timelineZoom > 1, "Zoom in did not change the timeline scale.");
        ZoomOutClick(this, new RoutedEventArgs());
        Require(_timelineZoom == 1, "Zoom out did not return to fit.");
        ZoomInClick(this, new RoutedEventArgs());
        FitTimelineClick(this, new RoutedEventArgs());
        Require(_timelineZoom == 1, "Fit did not restore the full timeline.");
        await VerifyNoticeCancellationAsync();
        StartupDiagnostics.Write("Appearance verified: live light/dark resources, primary contrast, neutral dropdown selection, help dismissal, dialog surfaces, toolbar guards and notice cancellation.");
    }

    private static void CaptureWindow(Window window, string fileName)
    {
        try
        {
            window.Show();
            window.UpdateLayout();
            WindowPresentation.VerifyCaption(window);
            UiCapture.Save((FrameworkElement)window.Content, fileName);
        }
        finally { window.Close(); }
    }

    private async Task VerifyNoticeCancellationAsync()
    {
        var check = Dispatcher.InvokeAsync(() =>
        {
            var notice = OwnedWindows.OfType<NoticeWindow>().Single();
            try
            {
                Require(notice.CancelAction.IsDefault && notice.CancelAction.IsCancel &&
                    !notice.ConfirmAction.IsDefault, "A destructive confirmation did not default to cancellation.");
                var peer = new ButtonAutomationPeer(notice.CancelAction);
                ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
            }
            catch { notice.Close(); throw; }
        }, DispatcherPriority.ApplicationIdle);
        var accepted = NoticeWindow.Show(this, "关闭视频剪辑", "关闭后轨道编辑不会保存。", "确认关闭", "继续剪辑");
        await check.Task;
        Require(!accepted && IsVisible, "Cancelling a confirmation closed the editor.");
    }

    private static bool SameColor(Brush first, Brush second) =>
        first is SolidColorBrush a && second is SolidColorBrush b && a.Color == b.Color;

    private static double Contrast(Brush first, Brush second)
    {
        static double Luminance(Brush brush)
        {
            var c = ((SolidColorBrush)brush).Color;
            static double Linear(byte channel)
            {
                var value = channel / 255.0;
                return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);
        }
        var a = Luminance(first); var b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private sealed class AppearanceUpdateHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/releases/latest", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
    }
}
