using System.Windows;

namespace Clip.Desktop;

/// <summary>应用内提示与确认共用的可滚动、可复制详情窗口。</summary>
public partial class NoticeWindow : Window
{
    internal NoticeWindow(string title, string message, string confirmLabel, string? cancelLabel = null)
    {
        InitializeComponent();
        Title = Heading.Text = title;
        Message.Text = message;
        ConfirmAction.Content = confirmLabel;
        CancelAction.Content = cancelLabel;
        CancelAction.Visibility = cancelLabel is null ? Visibility.Collapsed : Visibility.Visible;
        ConfirmAction.IsDefault = cancelLabel is null;
        ConfirmAction.IsCancel = cancelLabel is null;
        CancelAction.IsDefault = cancelLabel is not null;
        WindowPresentation.FitInitialBounds(this);
        WindowPresentation.HideCaptionIcon(this);
        Loaded += (_, _) =>
        {
            if (cancelLabel is null) ConfirmAction.Focus();
            else CancelAction.Focus();
        };
    }

    private void ConfirmClick(object sender, RoutedEventArgs e) => DialogResult = true;

    internal static bool Show(Window? owner, string title, string message, string confirmLabel = "知道了", string? cancelLabel = null)
    {
        var window = new NoticeWindow(title, message, confirmLabel, cancelLabel);
        if (owner is { IsVisible: true }) window.Owner = owner;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return window.ShowDialog() == true;
    }
}
