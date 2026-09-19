namespace Clip.Core;

/// <summary>A ripple-edited main track and independent candidate tracks, with project-wide undo.</summary>
public sealed class EditProject
{
    private sealed record State(VideoTrack[] Tracks, MediaInfo[] Sources);
    private readonly List<VideoTrack> _tracks = [];
    private readonly List<MediaInfo> _sources = [];
    private readonly Stack<State> _undo = [];
    private readonly Stack<State> _redo = [];
    public IReadOnlyList<VideoTrack> Tracks => _tracks.AsReadOnly();
    public IReadOnlyList<MediaInfo> Sources => _sources.AsReadOnly();
    public VideoTrack MainTrack => _tracks[0];
    public double Duration => _tracks.Max(t => t.Duration);
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public EditProject() => _tracks.Add(new(Guid.NewGuid(), "主轨", true, Array.AsReadOnly(Array.Empty<VideoClip>())));

    public VideoTrack Import(MediaInfo media)
    {
        var clip = VideoClip.Create(media);
        clip.Validate();
        if (media.IsHdr) throw new NotSupportedException("暂不支持 HDR 素材，请先转换为 SDR。");
        SaveUndo();
        if (!_sources.Any(s => SameSource(s, media))) _sources.Add(media);
        var track = new VideoTrack(Guid.NewGuid(), $"候选 {_tracks.Count:00}", false, Array.AsReadOnly(new[] { clip }));
        _tracks.Add(track);
        return track;
    }

    public VideoTrack? FindTrack(Guid id) => _tracks.FirstOrDefault(t => t.Id == id);

    public ClipPosition? FindClip(Guid id)
    {
        foreach (var track in _tracks)
        {
            double offset = 0;
            for (var i = 0; i < track.Clips.Count; i++)
            {
                var clip = track.Clips[i];
                if (clip.Id == id) return new(track.Id, i, clip, clip.Start, offset);
                offset += clip.Duration;
            }
        }
        return null;
    }

    public ClipPosition? Locate(Guid trackId, double time)
    {
        var track = FindTrack(trackId);
        if (track is null || track.Clips.Count == 0 || !double.IsFinite(time)) return null;
        time = Math.Clamp(time, 0, track.Duration);
        double offset = 0;
        for (var i = 0; i < track.Clips.Count; i++)
        {
            var clip = track.Clips[i];
            if (time < offset + clip.Duration - 0.000001 || i == track.Clips.Count - 1)
                return new(trackId, i, clip, Math.Clamp(clip.Start + (time - offset) * clip.Speed, clip.Start, clip.End), offset);
            offset += clip.Duration;
        }
        return null;
    }

    public Guid? Split(Guid trackId, double time)
    {
        if (Locate(trackId, time) is not { } position) return null;
        var clip = position.Clip;
        var frame = 1 / clip.Media.FrameRate;
        var cut = Math.Round(position.SourceTime / frame) * frame;
        if (cut - clip.Start < frame * 0.5 || clip.End - cut < frame * 0.5) return null;
        SaveUndo();
        var clips = FindTrack(trackId)!.Clips.ToList();
        var right = clip with { Id = Guid.NewGuid(), Start = cut };
        clips[position.Index] = clip with { End = cut };
        clips.Insert(position.Index + 1, right);
        ReplaceClips(trackId, clips);
        return right.Id;
    }

    public bool Move(Guid clipId, Guid targetTrackId, int insertionIndex)
    {
        if (FindClip(clipId) is not { } source || FindTrack(targetTrackId) is not { } target) return false;
        if (insertionIndex < 0 || insertionIndex > target.Clips.Count) throw new ArgumentOutOfRangeException(nameof(insertionIndex));
        if (source.TrackId == targetTrackId && (insertionIndex == source.Index || insertionIndex == source.Index + 1)) return false;
        SaveUndo();
        var sourceClips = FindTrack(source.TrackId)!.Clips.ToList();
        sourceClips.RemoveAt(source.Index);
        if (source.TrackId == targetTrackId)
        {
            if (insertionIndex > source.Index) insertionIndex--;
            sourceClips.Insert(insertionIndex, source.Clip);
            ReplaceClips(source.TrackId, sourceClips);
        }
        else
        {
            var targetClips = target.Clips.ToList();
            targetClips.Insert(insertionIndex, source.Clip);
            ReplaceClips(source.TrackId, sourceClips);
            ReplaceClips(targetTrackId, targetClips);
        }
        return true;
    }

    public bool SetSpeed(Guid clipId, double speed)
    {
        VideoClip.ValidateSpeed(speed);
        if (FindClip(clipId) is not { } position || position.Clip.Speed == speed) return false;
        SaveUndo();
        var clips = FindTrack(position.TrackId)!.Clips.ToList();
        clips[position.Index] = position.Clip with { Speed = speed };
        ReplaceClips(position.TrackId, clips);
        return true;
    }

    public bool Delete(Guid clipId)
    {
        if (FindClip(clipId) is not { } position) return false;
        SaveUndo();
        var clips = FindTrack(position.TrackId)!.Clips.ToList();
        clips.RemoveAt(position.Index);
        ReplaceClips(position.TrackId, clips);
        return true;
    }

    public ClipPlayback? AdvancePlayback(Guid trackId, Guid clipId, double sourceTime)
    {
        if (!double.IsFinite(sourceTime) || FindTrack(trackId) is not { } track || FindClip(clipId) is not { } p || p.TrackId != trackId)
            return null;
        while (true)
        {
            if (sourceTime < p.Clip.End) return new(p with { SourceTime = Math.Max(p.Clip.Start, sourceTime) }, false, false);
            if (p.Index == track.Clips.Count - 1) return new(p with { SourceTime = p.Clip.End }, false, true);
            var next = track.Clips[p.Index + 1];
            var mustSeek = !SameSource(next.Media, p.Clip.Media) || Math.Abs(next.Start - p.Clip.End) > 0.000001;
            p = new(trackId, p.Index + 1, next, next.Start, p.TimelineStart + p.Clip.Duration);
            if (mustSeek) return new(p, true, false);
        }
    }

    public IReadOnlyList<VideoClip> ExportClips() => Array.AsReadOnly(MainTrack.Clips.ToArray());
    public bool Undo() => Restore(_undo, _redo);
    public bool Redo() => Restore(_redo, _undo);
    public static bool SameSource(MediaInfo a, MediaInfo b) => string.Equals(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);

    private void ReplaceClips(Guid trackId, List<VideoClip> clips)
    {
        var index = _tracks.FindIndex(t => t.Id == trackId);
        _tracks[index] = _tracks[index] with { Clips = clips.AsReadOnly() };
    }
    private State Snapshot() => new(_tracks.ToArray(), _sources.ToArray());
    private void SaveUndo() { _undo.Push(Snapshot()); _redo.Clear(); }
    private bool Restore(Stack<State> from, Stack<State> to)
    {
        if (!from.TryPop(out var state)) return false;
        to.Push(Snapshot());
        _tracks.Clear(); _tracks.AddRange(state.Tracks);
        _sources.Clear(); _sources.AddRange(state.Sources);
        return true;
    }
}
