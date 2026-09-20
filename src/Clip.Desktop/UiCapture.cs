using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Clip.Desktop;

internal static class UiCapture
{
    internal static void Save(FrameworkElement surface, string fileName, double minimumPixelsPerDip = 1)
    {
        surface.UpdateLayout();
        // 同步视觉树 DPI，令 TextBlock 重建字形缓存，再直接渲染到目标位图。
        // 仅提高 RenderTargetBitmap 的 DPI 会放大已有的低 DPI 文字缓存。
        Visual root = surface;
        while (VisualTreeHelper.GetParent(root) is Visual parent) root = parent;
        var originalDpi = VisualTreeHelper.GetDpi(root);
        var originalFormatting = surface.ReadLocalValue(TextOptions.TextFormattingModeProperty);
        var scaleX = Math.Max(originalDpi.DpiScaleX, minimumPixelsPerDip);
        var scaleY = Math.Max(originalDpi.DpiScaleY, minimumPixelsPerDip);
        var changeDpi = scaleX != originalDpi.DpiScaleX || scaleY != originalDpi.DpiScaleY;
        try
        {
            if (changeDpi)
            {
                VisualTreeHelper.SetRootDpi(root, new DpiScale(scaleX, scaleY));
                // Display 模式会复用面向屏幕像素的字形；高清导出使用可缩放排版。
                TextOptions.SetTextFormattingMode(surface, TextFormattingMode.Ideal);
                surface.UpdateLayout();
            }
            var image = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(surface.ActualWidth * scaleX)),
                Math.Max(1, (int)Math.Ceiling(surface.ActualHeight * scaleY)), 96 * scaleX, 96 * scaleY, PixelFormats.Pbgra32);
            image.Render(surface);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = File.Create(Path.Combine(AppContext.BaseDirectory, fileName));
            encoder.Save(stream);
        }
        finally
        {
            if (changeDpi)
            {
                VisualTreeHelper.SetRootDpi(root, originalDpi);
                if (originalFormatting == DependencyProperty.UnsetValue)
                    surface.ClearValue(TextOptions.TextFormattingModeProperty);
                else surface.SetValue(TextOptions.TextFormattingModeProperty, originalFormatting);
                surface.UpdateLayout();
            }
        }
    }
}
