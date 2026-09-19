using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Clip.Desktop;

internal static class UiCapture
{
    internal static void Save(FrameworkElement surface, string fileName)
    {
        surface.UpdateLayout();
        // Capture client content, not Window/non-client coordinates; this also works at non-100% desktop DPI.
        var dpi = VisualTreeHelper.GetDpi(surface);
        var image = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(surface.ActualWidth * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(surface.ActualHeight * dpi.DpiScaleY)), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        image.Render(surface);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(Path.Combine(AppContext.BaseDirectory, fileName));
        encoder.Save(stream);
    }
}
