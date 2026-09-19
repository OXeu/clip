using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Clip.Desktop;

/// <summary>Renders the original Remix SVG path geometry without a browser or an icon-font dependency.</summary>
public sealed class RemixIcon : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(string), typeof(RemixIcon), new FrameworkPropertyMetadata("Film", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(RemixIcon), new FrameworkPropertyMetadata(SystemColors.ControlTextBrush, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));
    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    public RemixIcon()
    {
        Width = Height = 20;
        VerticalAlignment = VerticalAlignment.Center;
        IsHitTestVisible = false;
        Focusable = false;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (TryFindResource("Remix." + Kind) is not Geometry geometry) return;
        var size = Math.Min(ActualWidth, ActualHeight);
        drawingContext.PushTransform(new TranslateTransform((ActualWidth - size) / 2, (ActualHeight - size) / 2));
        drawingContext.PushTransform(new ScaleTransform(size / 24, size / 24));
        drawingContext.DrawGeometry(Foreground, null, geometry);
        drawingContext.Pop();
        drawingContext.Pop();
    }
}
