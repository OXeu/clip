using System.Windows;
using System.Windows.Controls;
using Clip.Core;

namespace Clip.Desktop;

public partial class ExportWindow : Window
{
    private readonly MediaInfo _media;
    public ExportOptions? Options { get; private set; }

    public ExportWindow(MediaInfo media, bool hasNvidia)
    {
        _media = media;
        InitializeComponent();
        NvidiaItem.IsEnabled = hasNvidia;
        EncoderBox.SelectedIndex = hasNvidia ? 0 : 1;
        HardwareDecodeBox.IsEnabled = hasNvidia;
        HardwareDecodeBox.IsChecked = hasNvidia;
        HardwareHint.Text = hasNvidia
            ? "NVENC 编码检测通过。解码支持取决于显卡和素材格式；失败时可关闭硬件解码重试。"
            : "未检测到可用的 NVIDIA NVENC，当前使用 CPU。";
        UpdateDimensions();
    }

    private void SizeSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateDimensions();

    private void UpdateDimensions()
    {
        if (WidthBox is null || _media is null) return;
        DimensionsPanel.IsEnabled = SizeBox.SelectedIndex == 4;
        var (w, h) = SizeBox.SelectedIndex switch
        {
            1 => (1920, 1080), 2 => (1280, 720), 3 => (3840, 2160), _ => (_media.Width, _media.Height)
        };
        // Resolution presets follow portrait orientation; custom dimensions are exact.
        if (SizeBox.SelectedIndex is >= 1 and <= 3 && _media.Height > _media.Width) (w, h) = (h, w);
        WidthBox.Text = (w + w % 2).ToString();
        HeightBox.Text = (h + h % 2).ToString();
    }

    private void ConfirmClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!int.TryParse(WidthBox.Text, out var width) || !int.TryParse(HeightBox.Text, out var height))
                throw new ArgumentException("请输入有效的整数宽高。");
            if (QualityBox.SelectedIndex == 0 && SizeBox.SelectedIndex != 0)
                throw new ArgumentException("原画模式使用源视频尺寸。调整尺寸时请选择高清、均衡或小体积质量。");
            Options = new((ExportQuality)QualityBox.SelectedIndex, SizeBox.SelectedIndex == 0 ? null : width,
                SizeBox.SelectedIndex == 0 ? null : height, EncoderBox.SelectedIndex == 0 ? VideoEncoder.Nvidia : VideoEncoder.Software,
                HardwareDecodeBox.IsChecked == true);
            Options.GetDimensions(_media);
            DialogResult = true;
        }
        catch (ArgumentException exception)
        {
            MessageBox.Show(this, exception.Message, "检查导出设置", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
