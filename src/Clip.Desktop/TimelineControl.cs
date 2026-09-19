using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Clip.Core;

namespace Clip.Desktop;

public sealed class TimelineControl : FrameworkElement
{
    private const double Inset = 20;
    private const double TrackTop = 40;
    private const double TrackHeight = 80;
    public IReadOnlyList<Segment> Segments { get; set; } = [];
    public string SourceName { get; set; } = "";
    public double Duration { get; set; }
    public double Position { get; set; }
    public Guid? SelectedId { get; set; }
    public event Action<double>? SeekRequested;
    public event Action<Guid>? SelectionChanged;
    public event Action? DeleteRequested;

    public TimelineControl()
    {
        Focusable = true;
        ClipToBounds = true;
        MinHeight = 144;
        Cursor = Cursors.Hand;
    }

    private double Scale => Math.Max(1, ActualWidth - Inset * 2) / Math.Max(Duration, 1);

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(TokenBrush("ColorNeutralBackground1"), null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (Segments.Count == 0)
        {
            DrawText(dc, "所有片段已删除，按 Ctrl+Z 恢复", Inset, 64, 14, "ColorNeutralForeground3");
            return;
        }
        var steps = new[] { 0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800, 3600, 7200, 21600, 86400 };
        var interval = steps.FirstOrDefault(s => s * Scale >= 88, Math.Max(Duration / 8, 1));
        for (double t = 0; t <= Duration; t += interval)
        {
            var x = Inset + t * Scale;
            dc.DrawLine(new Pen(TokenBrush("ColorSubtleStroke"), 1), new Point(x, 28), new Point(x, ActualHeight - 8));
            var time = TimeSpan.FromSeconds(t);
            var label = t >= 3600 ? $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}" :
                $"{(int)time.TotalMinutes:00}:{time.Seconds:00}" + (interval < 1 ? $".{time.Milliseconds / 100}" : "");
            DrawText(dc, label, x + 4, 6, 12, "ColorNeutralForeground3");
        }
        double offset = 0;
        for (var i = 0; i < Segments.Count; i++)
        {
            var s = Segments[i];
            var x = Inset + offset * Scale;
            var width = Math.Max(0.5, s.Duration * Scale - 3);
            var selected = s.Id == SelectedId;
            var foreground = selected ? "ColorOnBrand" : "ColorBrandForeground";
            var rect = new Rect(x, TrackTop, width, TrackHeight);
            dc.DrawRoundedRectangle(TokenBrush(selected ? "ColorBrandBackground" : "ColorTimelineClipBackground"),
                new Pen(TokenBrush(selected ? "ColorBrandBackgroundPressed" : "ColorTimelineClipBorder"), selected ? 2 : 1), rect, 4, 4);
            dc.PushClip(new RectangleGeometry(rect));
            if (width > 48)
            {
                if (TryFindResource("Remix.Film") is Geometry icon)
                {
                    dc.PushTransform(new TranslateTransform(x + 12, TrackTop + 14));
                    dc.PushTransform(new ScaleTransform(16.0 / 24, 16.0 / 24));
                    dc.DrawGeometry(TokenBrush(foreground), null, icon);
                    dc.Pop();
                    dc.Pop();
                }
                DrawText(dc, $"{i + 1:00}  {SourceName}", x + 36, TrackTop + 13, 13, foreground, width - 48);
                DrawText(dc, $"{s.Duration:0.##} 秒" + (selected && width > 140 ? " · 已选中" : ""), x + 12, TrackTop + 45, 12, foreground, width - 24);
            }
            dc.Pop();
            offset += s.Duration;
        }
        var playhead = Inset + Math.Clamp(Position, 0, Duration) * Scale;
        dc.DrawLine(new Pen(TokenBrush("ColorNeutralForeground1"), 1.5), new Point(playhead, 28), new Point(playhead, ActualHeight - 6));
        var pointer = new StreamGeometry();
        using (var context = pointer.Open())
        {
            context.BeginFigure(new Point(playhead - 5, 22), true, true);
            context.LineTo(new Point(playhead + 5, 22), true, false);
            context.LineTo(new Point(playhead, 30), true, false);
        }
        dc.DrawGeometry(TokenBrush("ColorNeutralForeground1"), null, pointer);
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (!IsEnabled || Segments.Count == 0) return;
        Focus();
        var point = e.GetPosition(this);
        var time = Math.Clamp((point.X - Inset) / Scale, 0, Duration);
        if (point.Y >= TrackTop && point.Y <= TrackTop + TrackHeight)
        {
            double offset = 0;
            for (var i = 0; i < Segments.Count; i++)
            {
                var segment = Segments[i];
                if (time < offset + segment.Duration || i == Segments.Count - 1)
                {
                    SelectionChanged?.Invoke(segment.Id);
                    break;
                }
                offset += segment.Duration;
            }
        }
        if (e.ChangedButton == MouseButton.Left)
        {
            SeekRequested?.Invoke(time);
            CaptureMouse();
        }
        else if (e.ChangedButton == MouseButton.Right && point.Y >= TrackTop && point.Y <= TrackTop + TrackHeight)
        {
            var menu = new ContextMenu();
            var delete = new MenuItem { Header = "删除片段", InputGestureText = "Delete" };
            delete.Click += (_, _) => DeleteRequested?.Invoke();
            menu.Items.Add(delete);
            menu.PlacementTarget = this;
            menu.IsOpen = true;
        }
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed)
            SeekRequested?.Invoke(Math.Clamp((e.GetPosition(this).X - Inset) / Scale, 0, Duration));
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    private void DrawText(DrawingContext dc, string text, double x, double y, double size, string token, double maxWidth = double.PositiveInfinity)
    {
        var font = (FontFamily?)TryFindResource("FontFamilyBase") ?? SystemFonts.MessageFontFamily;
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            size, TokenBrush(token), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        if (double.IsFinite(maxWidth))
        {
            formatted.MaxTextWidth = Math.Max(1, maxWidth);
            formatted.MaxLineCount = 1;
            formatted.Trimming = TextTrimming.CharacterEllipsis;
        }
        dc.DrawText(formatted, new Point(x, y));
    }

    private Brush TokenBrush(string key)
    {
        if (SystemParameters.HighContrast)
        {
            if (key == "ColorOnBrand") return SystemColors.HighlightTextBrush;
            if (key is "ColorBrandBackground" or "ColorBrandBackgroundPressed") return SystemColors.HighlightBrush;
            if (key.Contains("Background", StringComparison.Ordinal)) return SystemColors.WindowBrush;
            return SystemColors.WindowTextBrush;
        }
        return (Brush?)TryFindResource(key) ?? SystemColors.WindowTextBrush;
    }

    public static string FormatTime(double value)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, double.IsFinite(value) ? value : 0));
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";
    }
}
