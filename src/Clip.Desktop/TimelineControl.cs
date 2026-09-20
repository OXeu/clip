using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Clip.Core;

namespace Clip.Desktop;

public sealed class TimelineControl : FrameworkElement
{
    public const string ClipDataFormat = "Clip.VideoClip";
    public const double ContentInset = 16;
    public const double RulerHeight = 28;
    public const double RowHeight = 68;
    private const double ClipHeight = 48;
    private Point _mouseDown;
    private Guid? _pressedClip;
    private Guid? _draggedClip;
    private (Guid Track, int Index)? _drop;
    public EditProject? Project { get; set; }
    public Guid ActiveTrackId { get; set; }
    public Guid? SelectedTrackId { get; set; }
    public Guid? SelectedId { get; set; }
    public double Position { get; set; }
    public double HorizontalOffset { get; set; }
    public double VerticalOffset { get; set; }
    public event Action<Guid, double>? SeekRequested;
    public event Action<Guid, double>? SelectionChanged;
    public event Action<Guid, double>? TrackSelectionChanged;
    public event Action<Guid, Guid, int>? MoveRequested;
    public event Action<Point>? AutoScrollRequested;
    public double ContentHeight => RulerHeight + (Project?.Tracks.Count ?? 1) * RowHeight + 8;
    public double Duration { get; set; }
    private double Scale => Math.Max(1, ActualWidth - ContentInset - 20) / Math.Max(Duration, 1);
    public double TimeAtX(double x) => Math.Clamp((x - ContentInset) / Scale, 0, Duration);
    public double XAtTime(double time) => ContentInset + Math.Clamp(time, 0, Duration) * Scale;

    public TimelineControl()
    {
        Focusable = true;
        ClipToBounds = true;
        AllowDrop = true;
        Cursor = Cursors.Hand;
    }

    public Rect ClipBounds(Guid id)
    {
        if (Project?.FindClip(id) is not { } p) return Rect.Empty;
        var row = Project.Tracks.ToList().FindIndex(t => t.Id == p.TrackId);
        return new Rect(XAtTime(p.TimelineStart), RulerHeight + row * RowHeight + 10,
            Math.Max(2, p.Clip.Duration * Scale - 3), ClipHeight);
    }

    public (Guid Track, int Index)? InsertionAt(Point point)
    {
        if (Project is null || point.Y < VerticalOffset + RulerHeight) return null;
        var row = (int)((point.Y - RulerHeight) / RowHeight);
        if (row < 0 || row >= Project.Tracks.Count) return null;
        var track = Project.Tracks[row];
        var time = TimeAtX(point.X);
        double offset = 0;
        for (var i = 0; i < track.Clips.Count; i++)
        {
            if (time < offset + track.Clips[i].Duration / 2) return (track.Id, i);
            offset += track.Clips[i].Duration;
        }
        return (track.Id, track.Clips.Count);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(Brush("ColorNeutralBackground1"), null, new Rect(RenderSize));
        if (Project is null) return;
        for (var row = 0; row < Project.Tracks.Count; row++)
        {
            var track = Project.Tracks[row];
            var top = RulerHeight + row * RowHeight;
            if (track.Id == SelectedTrackId)
            {
                dc.DrawRectangle(Brush("ColorBrandBackground2"), null, new Rect(0, top, ActualWidth, RowHeight));
                dc.DrawRectangle(Brush("ColorBrandBackground"), null, new Rect(HorizontalOffset, top, 3, RowHeight));
            }
            else if (track.Id == ActiveTrackId)
                dc.DrawRectangle(Brush("ColorNeutralBackground2"), null, new Rect(0, top, ActualWidth, RowHeight));
            dc.DrawLine(new Pen(Brush("ColorNeutralStroke2"), 1), new Point(0, top + RowHeight), new Point(ActualWidth, top + RowHeight));
            double offset = 0;
            foreach (var clip in track.Clips)
            {
                var rect = new Rect(XAtTime(offset), top + 10, Math.Max(2, clip.Duration * Scale - 3), ClipHeight);
                offset += clip.Duration;
                var selected = clip.Id == SelectedId;
                var foreground = selected ? "ColorOnBrand" : "ColorBrandForeground";
                if (_draggedClip == clip.Id) dc.PushOpacity(0.45);
                dc.DrawRoundedRectangle(Brush(selected ? "ColorBrandBackground" : "ColorTimelineClipBackground"),
                    new Pen(Brush(selected ? "ColorBrandBackgroundPressed" : "ColorTimelineClipBorder"), selected ? 2 : 1), rect, 4, 4);
                dc.PushClip(new RectangleGeometry(rect));
                if (rect.Width > 44)
                {
                    if (TryFindResource("Remix.Film") is Geometry icon)
                    {
                        dc.PushTransform(new TranslateTransform(rect.X + 8, rect.Y + 8));
                        dc.PushTransform(new ScaleTransform(14.0 / 24, 14.0 / 24));
                        dc.DrawGeometry(Brush(foreground), null, icon);
                        dc.Pop(); dc.Pop();
                    }
                    Text(dc, clip.DisplayName, rect.X + 28, rect.Y + 6, 12, foreground, rect.Width - 36);
                    Text(dc, $"{clip.Speed:0.##}× · {clip.Duration:0.##} 秒", rect.X + 8, rect.Y + 28, 11, foreground, rect.Width - 16);
                }
                dc.Pop();
                if (_draggedClip == clip.Id) dc.Pop();
            }
            if (track.Id == ActiveTrackId && track.Clips.Count > 0)
            {
                var x = XAtTime(Math.Min(Position, track.Duration));
                dc.DrawLine(new Pen(Brush("ColorNeutralForeground1"), 2), new Point(x, top + 3), new Point(x, top + RowHeight - 2));
                dc.DrawEllipse(Brush("ColorNeutralForeground1"), null, new Point(x, top + 4), 3, 3);
            }
            if (_drop is { } drop && drop.Track == track.Id)
            {
                var x = XAtTime(track.Clips.Take(drop.Index).Sum(c => c.Duration));
                dc.DrawLine(new Pen(Brush("ColorBrandBackground"), 3), new Point(x, top + 2), new Point(x, top + RowHeight - 2));
                dc.DrawEllipse(Brush("ColorBrandBackground"), null, new Point(x, top + 3), 4, 4);
            }
        }
        // Frozen shared ruler.
        dc.DrawRectangle(Brush("ColorNeutralBackground1"), null, new Rect(HorizontalOffset, VerticalOffset, ActualWidth, RulerHeight));
        var intervals = new[] { 0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800, 3600, 7200, 21600, 86400 };
        var interval = intervals.FirstOrDefault(v => v * Scale >= 88, Math.Max(Duration / 8, 1));
        var first = Math.Max(0, Math.Floor(TimeAtX(HorizontalOffset) / interval) * interval);
        for (var time = first; time <= Duration; time += interval)
        {
            var x = XAtTime(time);
            if (x < HorizontalOffset - 1) continue;
            var t = TimeSpan.FromSeconds(time);
            var label = time >= 3600 ? $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes:00}:{t.Seconds:00}" + (interval < 1 ? $".{t.Milliseconds / 100}" : "");
            if (x + label.Length * 7 < ActualWidth - 12) Text(dc, label, x + 3, VerticalOffset + 3, 11, "ColorNeutralForeground3");
            dc.DrawLine(new Pen(Brush("ColorNeutralStroke2"), 1), new Point(x, VerticalOffset + 22), new Point(x, VerticalOffset + 28));
        }
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (!IsEnabled || Project is null || e.ChangedButton is not (MouseButton.Left or MouseButton.Right)) return;
        Focus();
        var point = e.GetPosition(this);
        _pressedClip = null;
        if (point.Y < VerticalOffset + RulerHeight)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                SeekRequested?.Invoke(ActiveTrackId, TimeAtX(point.X));
                CaptureMouse();
            }
        }
        else
        {
            var row = (int)((point.Y - RulerHeight) / RowHeight);
            if (row < 0 || row >= Project.Tracks.Count) return;
            var track = Project.Tracks[row];
            var clip = track.Clips.FirstOrDefault(c => ClipBounds(c.Id).Contains(point));
            if (clip is not null)
            {
                SelectionChanged?.Invoke(clip.Id, TimeAtX(point.X));
                if (e.ChangedButton == MouseButton.Left) { _pressedClip = clip.Id; _mouseDown = point; CaptureMouse(); }
            }
            else if (e.ChangedButton == MouseButton.Left) TrackSelectionChanged?.Invoke(track.Id, TimeAtX(point.X));
        }
        // Let WPF open the shared timeline context menu after right-button selection.
        if (e.ChangedButton == MouseButton.Left) e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(this);
        if (_pressedClip is { } id)
        {
            if (Math.Abs(point.X - _mouseDown.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(point.Y - _mouseDown.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            ReleaseMouseCapture();
            _draggedClip = id;
            InvalidateVisual();
            try { DragDrop.DoDragDrop(this, new DataObject(ClipDataFormat, id.ToString()), DragDropEffects.Move); }
            finally { _pressedClip = _draggedClip = null; _drop = null; InvalidateVisual(); }
        }
        else SeekRequested?.Invoke(ActiveTrackId, TimeAtX(point.X));
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        _pressedClip = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        if (!e.Data.GetDataPresent(ClipDataFormat)) return;
        _drop = InsertionAt(e.GetPosition(this));
        e.Effects = IsEnabled && _drop is not null ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
        AutoScrollRequested?.Invoke(e.GetPosition(this));
        InvalidateVisual();
    }

    protected override void OnDragLeave(DragEventArgs e)
    {
        base.OnDragLeave(e);
        _drop = null;
        InvalidateVisual();
    }

    protected override void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);
        if (!e.Data.GetDataPresent(ClipDataFormat)) return;
        if (IsEnabled && Guid.TryParse(e.Data.GetData(ClipDataFormat) as string, out var id) && InsertionAt(e.GetPosition(this)) is { } drop)
            MoveRequested?.Invoke(id, drop.Track, drop.Index);
        e.Handled = true;
        _drop = null;
        InvalidateVisual();
    }

    private void Text(DrawingContext dc, string text, double x, double y, double size, string token, double maxWidth = double.PositiveInfinity)
    {
        var font = (FontFamily?)TryFindResource("FontFamilyBase") ?? SystemFonts.MessageFontFamily;
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), size, Brush(token), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        if (double.IsFinite(maxWidth)) { formatted.MaxTextWidth = Math.Max(1, maxWidth); formatted.MaxLineCount = 1; formatted.Trimming = TextTrimming.CharacterEllipsis; }
        dc.DrawText(formatted, new Point(x, y));
    }

    private Brush Brush(string key)
    {
        if (SystemParameters.HighContrast)
        {
            if (key == "ColorOnBrand") return SystemColors.HighlightTextBrush;
            if (key is "ColorBrandBackground" or "ColorBrandBackgroundPressed") return SystemColors.HighlightBrush;
            return key.Contains("Background", StringComparison.Ordinal) ? SystemColors.WindowBrush : SystemColors.WindowTextBrush;
        }
        return (Brush?)TryFindResource(key) ?? SystemColors.WindowTextBrush;
    }

    public static string FormatTime(double value)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, double.IsFinite(value) ? value : 0));
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";
    }
}
