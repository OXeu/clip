namespace Clip.Core;

/// <summary>Ordered source ranges. Removing a range ripples the remaining edit; the source is never changed.</summary>
public sealed class Timeline
{
    private readonly List<Segment> _segments = [];
    private readonly Stack<Segment[]> _undo = [];
    private readonly Stack<Segment[]> _redo = [];
    public IReadOnlyList<Segment> Segments => _segments.AsReadOnly();
    public MediaInfo? Media { get; private set; }
    public double Duration => _segments.Sum(s => s.Duration);
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public double FrameDuration => 1 / (Media?.FrameRate ?? 30);

    public void Load(MediaInfo media)
    {
        if (!double.IsFinite(media.Duration) || media.Duration <= 0 ||
            !double.IsFinite(media.FrameRate) || media.FrameRate <= 0)
            throw new ArgumentException("视频时长或帧率无效。", nameof(media));
        Media = media;
        _segments.Clear();
        _segments.Add(Segment.Create(0, media.Duration));
        _undo.Clear();
        _redo.Clear();
    }

    public TimelinePosition? Locate(double time)
    {
        if (_segments.Count == 0 || !double.IsFinite(time)) return null;
        time = Math.Clamp(time, 0, Duration);
        double start = 0;
        for (var i = 0; i < _segments.Count; i++)
        {
            var segment = _segments[i];
            if (time < start + segment.Duration - 0.000001 || i == _segments.Count - 1)
                return new(i, segment, Math.Clamp(segment.Start + time - start, segment.Start, segment.End), start);
            start += segment.Duration;
        }
        return null;
    }

    public Guid? Split(double time)
    {
        var position = Locate(time);
        if (position is not { } p) return null;
        var cut = Math.Round(p.SourceTime / FrameDuration) * FrameDuration;
        if (cut - p.Segment.Start < FrameDuration * 0.5 || p.Segment.End - cut < FrameDuration * 0.5)
            return null;
        SaveUndo();
        var right = Segment.Create(cut, p.Segment.End);
        _segments[p.Index] = p.Segment with { End = cut };
        _segments.Insert(p.Index + 1, right);
        return right.Id;
    }

    /// <summary>Follow the source clock across adjacent cuts without seeking; seek only across removed ranges.</summary>
    public PlaybackPosition? AdvancePlayback(Guid segmentId, double sourceTime)
    {
        if (!double.IsFinite(sourceTime)) return null;
        var index = _segments.FindIndex(s => s.Id == segmentId);
        if (index < 0) return null;
        var timelineStart = _segments.Take(index).Sum(s => s.Duration);
        while (true)
        {
            var segment = _segments[index];
            if (sourceTime < segment.End)
                return new(new(index, segment, Math.Max(segment.Start, sourceTime), timelineStart), false, false);
            if (index == _segments.Count - 1)
                return new(new(index, segment, segment.End, timelineStart), false, true);
            timelineStart += segment.Duration;
            var next = _segments[++index];
            if (Math.Abs(next.Start - segment.End) > 0.000001)
                return new(new(index, next, next.Start, timelineStart), true, false);
            // A late UI timer may cross several tiny, contiguous cuts in one tick.
        }
    }

    public bool Delete(Guid id)
    {
        var index = _segments.FindIndex(s => s.Id == id);
        if (index < 0) return false;
        SaveUndo();
        _segments.RemoveAt(index);
        return true;
    }

    public bool Undo() => Restore(_undo, _redo);
    public bool Redo() => Restore(_redo, _undo);

    private void SaveUndo()
    {
        _undo.Push(_segments.ToArray());
        _redo.Clear();
    }

    private bool Restore(Stack<Segment[]> from, Stack<Segment[]> to)
    {
        if (!from.TryPop(out var segments)) return false;
        to.Push(_segments.ToArray());
        _segments.Clear();
        _segments.AddRange(segments);
        return true;
    }
}
