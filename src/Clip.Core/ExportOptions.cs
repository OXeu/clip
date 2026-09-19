namespace Clip.Core;

public enum ExportQuality { Original, High, Balanced, Compact }
public enum VideoEncoder { Nvidia, Software }

public sealed record ExportOptions(
    ExportQuality Quality = ExportQuality.Original,
    int? Width = null,
    int? Height = null,
    VideoEncoder Encoder = VideoEncoder.Nvidia,
    bool HardwareDecode = true)
{
    public int QualityValue => Quality switch
    {
        ExportQuality.Original => 16,
        ExportQuality.High => 19,
        ExportQuality.Balanced => 23,
        _ => 28
    };

    public (int Width, int Height) GetDimensions(MediaInfo media)
    {
        if (Width.HasValue != Height.HasValue)
            throw new ArgumentException("宽度和高度必须同时填写。");
        var w = Width ?? media.Width;
        var h = Height ?? media.Height;
        if (w < 2 || h < 2 || w > 7680 || h > 7680)
            throw new ArgumentException("输出宽高必须在 2–7680 像素之间。");
        if (Width.HasValue && (w % 2 != 0 || h % 2 != 0))
            throw new ArgumentException("H.264 输出宽高必须为偶数。");
        return (w + w % 2, h + h % 2);
    }
}
