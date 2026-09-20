using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Clip.Core;

namespace Clip.Desktop;

public partial class ExportWindow : Window
{
    private readonly MediaInfo _media;
    private bool _updating;
    public ExportOptions? Options { get; private set; }

    public ExportWindow(MediaInfo media, bool hasNvidia, string? nvidiaDiagnostic = null, string? trackSummary = null)
    {
        _media = media;
        InitializeComponent();
        WindowPresentation.FitInitialBounds(this);
        WindowPresentation.HideCaptionIcon(this);
        NvidiaItem.IsEnabled = hasNvidia;
        EncoderBox.SelectedIndex = hasNvidia ? 0 : 1;
        NvidiaItem.ToolTip = hasNvidia ? null : nvidiaDiagnostic ?? "NVENC 检测未通过，可在设置中查看检测详情或重新检测。";
        SourceNameText.Text = trackSummary ?? media.FileName;
        SourceInfoText.Text = $"{media.Width} × {media.Height} · {media.FrameRate:0.##} fps";
        UpdateQuality();
        UpdateDimensions();
    }

    private void QualitySelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateQuality();

    private void QualityHelpClick(object sender, RoutedEventArgs e) => QualityHelp.IsOpen = !QualityHelp.IsOpen;

    private void ExportPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !QualityHelp.IsOpen) return;
        QualityHelp.IsOpen = false;
        QualityHelpButton.Focus();
        e.Handled = true;
    }
    private void SizeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateDimensions();
        ClearValidation();
    }

    private void UpdateQuality()
    {
        if (SizeBox is null || QualityDescriptionText is null || _updating) return;
        _updating = true;
        SizeBox.IsEnabled = QualityBox.SelectedIndex != 0;
        if (QualityBox.SelectedIndex == 0) SizeBox.SelectedIndex = 0;
        QualityDescriptionText.Text = QualityBox.SelectedIndex switch
        {
            0 => "以所选轨道首个素材的分辨率为基准，高质量重编码；不同尺寸素材保持比例并补边。",
            1 => "保留更多画面细节，适合高质量分享。",
            2 => "兼顾画面质量与文件大小，适合日常使用。",
            _ => "优先减小文件体积，画面细节会有所减少。"
        };
        _updating = false;
        UpdateDimensions();
        ClearValidation();
    }

    private void UpdateDimensions()
    {
        if (WidthBox is null || _media is null) return;
        DimensionsPanel.Visibility = SizeBox.SelectedIndex == 4 ? Visibility.Visible : Visibility.Collapsed;
        if (SizeBox.SelectedIndex == 4)
        {
            SizeHintText.Text = "请输入偶数宽高。比例不一致时补黑边，画面不会拉伸。";
            return;
        }
        var (w, h) = SizeBox.SelectedIndex switch
        {
            1 => (1920, 1080), 2 => (1280, 720), 3 => (3840, 2160), _ => (_media.Width, _media.Height)
        };
        // Resolution presets follow portrait orientation; custom dimensions are exact.
        if (SizeBox.SelectedIndex is >= 1 and <= 3 && _media.Height > _media.Width) (w, h) = (h, w);
        WidthBox.Text = (w + w % 2).ToString();
        HeightBox.Text = (h + h % 2).ToString();
        SizeHintText.Text = QualityBox.SelectedIndex == 0
            ? $"{w} × {h} px · 选择其它画面质量后可调整尺寸。"
            : $"{w} × {h} px · 保持比例，必要时补黑边。";
    }

    private void DimensionsEdited(object sender, TextChangedEventArgs e) => ClearValidation();

    private void AdvancedExpanded(object sender, RoutedEventArgs e)
    {
        // Wait for the newly disclosed controls to be measured before revealing them.
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (AdvancedExpander.IsExpanded) ExportScroll.ScrollToEnd();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void ClearValidation()
    {
        if (ValidationPanel is not null) ValidationPanel.Visibility = Visibility.Collapsed;
    }

    private void ConfirmClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!int.TryParse(WidthBox.Text, out var width) || !int.TryParse(HeightBox.Text, out var height))
                throw new ArgumentException("请输入有效的整数宽高。");
            Options = new((ExportQuality)QualityBox.SelectedIndex, SizeBox.SelectedIndex == 0 ? null : width,
                SizeBox.SelectedIndex == 0 ? null : height, EncoderBox.SelectedIndex == 0 ? VideoEncoder.Nvidia : VideoEncoder.Software,
                HardwareDecode: false);
            Options.GetDimensions(_media);
            DialogResult = true;
        }
        catch (ArgumentException exception)
        {
            ValidationText.Text = exception.Message;
            ValidationPanel.Visibility = Visibility.Visible;
            ValidationPanel.BringIntoView();
        }
    }

    internal void VerifyDisclosure()
    {
        WindowPresentation.VerifyCaption(this);
        if (AdvancedExpander.IsExpanded || DimensionsPanel.Visibility != Visibility.Collapsed || SizeBox.IsEnabled)
            throw new InvalidOperationException("Export defaults should disclose only basic options.");
        UiCapture.Save(DialogRoot, "smoke-export-basic.png");
        QualityBox.SelectedIndex = 1;
        SizeBox.SelectedIndex = 4;
        AdvancedExpander.IsExpanded = true;
        UpdateLayout();
        if (!SizeBox.IsEnabled || DimensionsPanel.Visibility != Visibility.Visible)
            throw new InvalidOperationException("Custom dimensions were not disclosed.");
        WidthBox.Text = "721";
        ConfirmClick(this, new RoutedEventArgs());
        if (ValidationPanel.Visibility != Visibility.Visible) throw new InvalidOperationException("Invalid dimensions did not show inline feedback.");
        WidthBox.Text = "1280";
        HeightBox.Text = "720";
        UpdateLayout();
        ExportScroll.ScrollToEnd();
        UpdateLayout();
        var encoderBounds = EncoderBox.TransformToAncestor(ExportScroll).TransformBounds(new Rect(EncoderBox.RenderSize));
        if (encoderBounds.Top < 0 || encoderBounds.Bottom > ExportScroll.ActualHeight)
            throw new InvalidOperationException("Expanded export controls are outside the viewport.");
        UiCapture.Save(DialogRoot, "smoke-export-advanced.png");
    }
}
