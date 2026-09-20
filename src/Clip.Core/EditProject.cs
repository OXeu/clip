namespace Clip.Core;

/// <summary>Independently exportable, ripple-edited tracks with project-wide undo.</summary>
public sealed class EditProject
{
    private sealed record State(VideoTrack[] Tracks, MediaInfo[] Sources);
    private readonly List<VideoTrack> _tracks = [];
    private readonly List<MediaInfo> _sources = [];
    private readonly Stack<State> _undo = [];
    private readonly Stack<State> _redo = [];
    public IReadOnlyList<VideoTrack> Tracks => _tracks.AsReadOnly();
    public IReadOnlyList<MediaInfo> Sources => _sources.AsReadOnly();
    public VideoTrack MainTrack => _tracks.First(t => t.IsMain);
    public double Duration => _tracks.Max(t => t.Duration);
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public IReadOnlyList<VideoTrack> ExportableTracks => _tracks.Where(t => t.Clips.Count > 0 && t.Kind != TrackKind.Audio).ToArray();

    public EditProject() => _tracks.Add(new(Guid.NewGuid(), "轨道 1", true, Array.AsReadOnly(Array.Empty<VideoClip>())));

    public VideoTrack Import(MediaInfo media)
    {
        var clip = VideoClip.Create(media);
        clip.Validate();
        if (media.IsHdr) throw new NotSupportedException("暂不支持 HDR 素材，请先转换为 SDR。");
        SaveUndo();
        if (!_sources.Any(s => SameSource(s, media))) _sources.Add(media);
        var track = new VideoTrack(Guid.NewGuid(), $"轨道 {_tracks.Count + 1}", false, Array.AsReadOnly(new[] { clip }));
        _tracks.Add(track);
        return track;
    }

    public SeparatedImport ImportSeparated(MediaInfo media)
    {
        var videoClip = VideoClip.Create(media) with { Kind = ClipKind.Video };
        videoClip.Validate();
        if (media.IsHdr) throw new NotSupportedException("暂不支持 HDR 素材，请先转换为 SDR。");
        SaveUndo();
        if (!_sources.Any(s => SameSource(s, media))) _sources.Add(media);
        var companionGroupId = Guid.NewGuid();
        var videoTrack = new VideoTrack(Guid.NewGuid(), $"视频 {media.FileName}", false,
            Array.AsReadOnly(new[] { videoClip }), TrackKind.Video, CompanionGroupId: companionGroupId);
        _tracks.Add(videoTrack);
        var audioClip = VideoClip.Create(media) with { Kind = ClipKind.Audio };
        var audioTrack = new VideoTrack(Guid.NewGuid(), media.HasAudio ? $"音频 {media.FileName}" : $"音频槽 {media.FileName}", false,
            Array.AsReadOnly(new[] { audioClip }), TrackKind.Audio, CompanionGroupId: companionGroupId);
        _tracks.Add(audioTrack);
        return new(videoTrack, audioTrack);
    }

    public IReadOnlyList<VideoTrack> CompanionTracks(Guid trackId)
    {
        var track = FindTrack(trackId);
        if (track is null) return [];
        if (track.CompanionGroupId is not { } groupId) return [track];
        return _tracks.Where(candidate => candidate.CompanionGroupId == groupId)
            .OrderBy(candidate => candidate.Kind == TrackKind.Video ? 0 : 1).ToArray();
    }

    public IReadOnlyList<VideoTrack> BindingTracks(Guid trackId)
    {
        var track = FindTrack(trackId);
        if (track is null) return [];
        if (track.BindingId is not { } bindingId) return [track];
        return _tracks.Where(t => t.BindingId == bindingId).ToArray();
    }

    /// <summary>分割同步集：显式对齐绑定与视频的伴生音频槽做传递闭包。</summary>
    public IReadOnlyList<VideoTrack> SynchronizedTracks(Guid trackId)
    {
        if (FindTrack(trackId) is not { } first) return [];
        var pending = new Stack<VideoTrack>();
        var included = new HashSet<Guid>();
        pending.Push(first);
        while (pending.TryPop(out var current))
        {
            if (!included.Add(current.Id)) continue;
            foreach (var candidate in _tracks)
            {
                var companion = current.CompanionGroupId.HasValue && candidate.CompanionGroupId == current.CompanionGroupId;
                var aligned = current.BindingId.HasValue && candidate.BindingId == current.BindingId;
                if ((companion || aligned) && !included.Contains(candidate.Id)) pending.Push(candidate);
            }
        }
        return _tracks.Where(track => included.Contains(track.Id)).ToArray();
    }

    public Guid? BindTracks(IEnumerable<Guid> trackIds)
    {
        var ids = trackIds.Distinct().ToArray();
        var selected = ids.Select(FindTrack).ToArray();
        if (ids.Length < 2 || selected.Any(t => t is null || t.Clips.Count == 0)) return null;
        SaveUndo();
        var touched = selected.Where(t => t!.BindingId.HasValue).Select(t => t!.BindingId!.Value).ToHashSet();
        var bindingId = Guid.NewGuid();
        for (var i = 0; i < _tracks.Count; i++)
            if (ids.Contains(_tracks[i].Id)) _tracks[i] = _tracks[i] with { BindingId = bindingId };
        ClearSingletonBindings(touched);
        return bindingId;
    }

    public bool UnbindTracks(IEnumerable<Guid> trackIds)
    {
        var ids = trackIds.ToHashSet();
        var touched = _tracks.Where(t => ids.Contains(t.Id) && t.BindingId.HasValue).Select(t => t.BindingId!.Value).ToHashSet();
        if (touched.Count == 0) return false;
        SaveUndo();
        for (var i = 0; i < _tracks.Count; i++)
            if (ids.Contains(_tracks[i].Id)) _tracks[i] = _tracks[i] with { BindingId = null };
        ClearSingletonBindings(touched);
        return true;
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
        if (FindTrack(trackId) is not { Clips.Count: > 0 }) return null;
        var cuts = new List<(VideoTrack Track, ClipPosition Position, double Cut)>();
        foreach (var track in SynchronizedTracks(trackId))
        {
            // 空的伴生音轨/对齐轨没有可切内容，不应阻止当前轨道分割。
            if (track.Clips.Count == 0) continue;
            if (Locate(track.Id, time) is not { } position) return null;
            var frame = 1 / position.Clip.Media.FrameRate;
            var cut = Math.Round(position.SourceTime / frame) * frame;
            if (cut - position.Clip.Start < frame * 0.5 || position.Clip.End - cut < frame * 0.5) return null;
            cuts.Add((track, position, cut));
        }
        if (cuts.Count == 0) return null;
        SaveUndo();
        Guid? requested = null;
        foreach (var (track, position, cut) in cuts)
        {
            var clips = track.Clips.ToList();
            var right = position.Clip with { Id = Guid.NewGuid(), Start = cut };
            clips[position.Index] = position.Clip with { End = cut };
            clips.Insert(position.Index + 1, right);
            ReplaceClips(track.Id, clips);
            if (track.Id == trackId) requested = right.Id;
        }
        return requested;
    }

    public bool Move(Guid clipId, Guid targetTrackId, int insertionIndex)
    {
        if (FindClip(clipId) is not { } source || FindTrack(targetTrackId) is not { } target) return false;
        var sourceTrack = FindTrack(source.TrackId)!;
        if (target.Kind != sourceTrack.Kind) return false;
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
            var targetIndex = _tracks.FindIndex(t => t.Id == targetTrackId);
            _tracks[targetIndex] = _tracks[targetIndex] with
            {
                BindingId = null,
                Clips = targetClips.AsReadOnly()
            };
        }
        return true;
    }

    public bool MoveTrack(Guid trackId, int insertionIndex)
    {
        var sourceIndex = _tracks.FindIndex(t => t.Id == trackId);
        if (sourceIndex < 0 || (_tracks[sourceIndex].Clips.Count == 0 && !_tracks[sourceIndex].CompanionGroupId.HasValue)) return false;
        var boundaries = TrackInsertionBoundaries();
        if (insertionIndex < boundaries[0] || insertionIndex > boundaries[^1])
            throw new ArgumentOutOfRangeException(nameof(insertionIndex));
        var targetBoundary = boundaries.OrderBy(boundary => Math.Abs(boundary - insertionIndex)).ThenByDescending(boundary => boundary).First();
        var unit = CompanionTracks(trackId);
        var unitIds = unit.Select(track => track.Id).ToHashSet();
        var unitIndexes = _tracks.Select((track, index) => unitIds.Contains(track.Id) ? index : -1).Where(index => index >= 0).ToArray();
        var unitStart = unitIndexes.Min();
        var unitEnd = unitIndexes.Max() + 1;
        if (targetBoundary >= unitStart && targetBoundary <= unitEnd) return false;
        SaveUndo();
        var orderedUnit = unit.OrderBy(track => track.Kind == TrackKind.Video ? 0 : 1).ToArray();
        _tracks.RemoveAll(track => unitIds.Contains(track.Id));
        var removedBefore = unitIndexes.Count(index => index < targetBoundary);
        _tracks.InsertRange(targetBoundary - removedBefore, orderedUnit);
        return true;
    }

    public IReadOnlyList<int> TrackInsertionBoundaries()
    {
        var minimum = _tracks[0].Clips.Count == 0 && !_tracks[0].CompanionGroupId.HasValue ? 1 : 0;
        List<int> boundaries = [minimum];
        var seen = new HashSet<Guid>();
        var index = minimum;
        while (index < _tracks.Count)
        {
            var track = _tracks[index];
            if (track.CompanionGroupId is { } groupId && seen.Add(groupId))
                index += _tracks.Count(candidate => candidate.CompanionGroupId == groupId);
            else index++;
            boundaries.Add(index);
        }
        return boundaries;
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

    public bool Rename(Guid clipId, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("请输入片段名称。", nameof(name));
        if (FindClip(clipId) is not { } position) return false;
        name = name.Trim();
        if (position.Clip.DisplayName == name) return false;
        SaveUndo();
        var clips = FindTrack(position.TrackId)!.Clips.ToList();
        clips[position.Index] = position.Clip with { Name = name };
        ReplaceClips(position.TrackId, clips);
        return true;
    }

    public Guid? Duplicate(Guid clipId)
    {
        if (FindClip(clipId) is not { } position) return null;
        SaveUndo();
        var copy = position.Clip with { Id = Guid.NewGuid() };
        if (copy.Duration < 10)
        {
            var clips = FindTrack(position.TrackId)!.Clips.ToList();
            clips.Insert(position.Index + 1, copy);
            ReplaceClips(position.TrackId, clips);
        }
        else
        {
            var row = _tracks.FindIndex(t => t.Id == position.TrackId);
            _tracks.Insert(row + 1, new(Guid.NewGuid(), $"轨道 {_tracks.Count + 1}", false,
                Array.AsReadOnly(new[] { copy }), FindTrack(position.TrackId)?.Kind ?? TrackKind.Video));
        }
        return copy.Id;
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

    public VideoTrack? ResolveExportTrack(Guid? selectedTrackId)
    {
        if (selectedTrackId is { } id)
        {
            var selected = FindTrack(id);
            return selected is { Clips.Count: > 0 } && selected.Kind != TrackKind.Audio ? selected : null;
        }
        var tracks = ExportableTracks;
        return tracks.Count == 1 ? tracks[0] : null;
    }

    public IReadOnlyList<VideoClip> ExportClips() => ExportClips(MainTrack.Id);
    public IReadOnlyList<VideoClip> ExportClips(Guid trackId) =>
        Array.AsReadOnly((FindTrack(trackId) ?? throw new ArgumentException("导出轨道不存在。", nameof(trackId))).Clips.ToArray());
    public bool Undo() => Restore(_undo, _redo);
    public bool Redo() => Restore(_redo, _undo);
    public static bool SameSource(MediaInfo a, MediaInfo b) => string.Equals(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);

    private void ReplaceClips(Guid trackId, List<VideoClip> clips)
    {
        var index = _tracks.FindIndex(t => t.Id == trackId);
        _tracks[index] = _tracks[index] with { Clips = clips.AsReadOnly() };
    }
    private void ClearSingletonBindings(IEnumerable<Guid> bindingIds)
    {
        foreach (var bindingId in bindingIds)
            if (_tracks.Count(t => t.BindingId == bindingId) < 2)
                for (var i = 0; i < _tracks.Count; i++)
                    if (_tracks[i].BindingId == bindingId) _tracks[i] = _tracks[i] with { BindingId = null };
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
