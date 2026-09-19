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
        MinHeight = 160;
        Cursor = Cursors.Cross;
    }

    private double Scale => Math.Max(1, ActualWidth - Inset * 2) / Math.Max(Duration, 1);

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(Brush("#16191C"), null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (Segments.Count == 0)
        {
            DrawText(dc, "时间轴为空 · 导入视频以开始，或按 Ctrl + Z 恢复片段", 20, 72, 13, "#9DA6AE");
            return;
        }
        var steps = new[] { 0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800, 3600, 7200, 21600, 86400 };
        var interval = steps.FirstOrDefault(s => s * Scale >= 90, Math.Max(Duration / 8, 1));
        for (double t = 0; t <= Duration; t += interval)
        {
            var x = Inset + t * Scale;
            dc.DrawLine(new Pen(Brush("#363D43"), 1), new Point(x, 27), new Point(x, ActualHeight - 15));
            DrawText(dc, FormatTime(t), x + 4, 6, 10, "#9DA6AE");
        }
        double offset = 0;
        for (var i = 0; i < Segments.Count; i++)
        {
            var s = Segments[i];
            var x = Inset + offset * Scale;
            var width = Math.Max(0.5, s.Duration * Scale - 2);
            var selected = s.Id == SelectedId;
            var rect = new Rect(x, 45, width, 89);
            dc.DrawRoundedRectangle(Brush(selected ? "#394E38" : "#293C35"),
                new Pen(Brush(selected ? "#BAE8AB" : "#536F61"), selected ? 2 : 1), rect, 4, 4);
            dc.PushClip(new RectangleGeometry(rect));
            dc.DrawRectangle(Brush(selected ? "#BAE8AB" : "#7AAB91"), null, new Rect(x, 45, width, 4));
            if (width > 35)
            {
                DrawText(dc, $"{i + 1:00}   {SourceName}", x + 12, 61, 12, "#EFF2F4");
                DrawText(dc, $"{FormatTime(s.Start)} → {FormatTime(s.End)}", x + 12, 89, 11, "#B3C4B8");
                DrawText(dc, $"{s.Duration:0.##} s", x + 12, 110, 10, "#9AAC9F");
            }
            dc.Pop();
            offset += s.Duration;
        }
        DrawText(dc, "视频 + 原音频 · 删除后自动收拢", Inset, 148, 11, "#7B8790");
        var playhead = Inset + Math.Clamp(Position, 0, Duration) * Scale;
        dc.DrawLine(new Pen(Brush("#D9F3CB"), 1.5), new Point(playhead, 28), new Point(playhead, ActualHeight - 10));
        var pointer = new StreamGeometry();
        using (var context = pointer.Open())
        {
            context.BeginFigure(new Point(playhead - 5, 22), true, true);
            context.LineTo(new Point(playhead + 5, 22), true, false);
            context.LineTo(new Point(playhead, 30), true, false);
        }
        dc.DrawGeometry(Brush("#D9F3CB"), null, pointer);
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (!IsEnabled || Segments.Count == 0) return;
        Focus();
        var point = e.GetPosition(this);
        var time = Math.Clamp((point.X - Inset) / Scale, 0, Duration);
        if (point.Y is >= 45 and <= 134)
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
        else if (e.ChangedButton == MouseButton.Right && point.Y is >= 45 and <= 134)
        {
            var menu = new ContextMenu();
            var delete = new MenuItem { Header = "Delete · 删除片段", InputGestureText = "Delete" };
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

    private void DrawText(DrawingContext dc, string text, double x, double y, double size, string color) =>
        dc.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI, Microsoft YaHei UI"), size, Brush(color), VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(x, y));

    private static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
    public static string FormatTime(double value)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, double.IsFinite(value) ? value : 0));
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";
    }
}
