using System.Globalization;
using Clip.Core;

var failures = 0;
var passed = 0;
var media = new MediaInfo("source.mp4", 10, 1920, 1080, 30, 0, 1, "h264");

void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

void Near(double actual, double expected, double tolerance = 0.001) =>
    Check(Math.Abs(actual - expected) <= tolerance, $"Expected {expected}, got {actual}");

void Test(string name, Action run)
{
    try { run(); Console.WriteLine($"PASS {name}"); passed++; }
    catch (Exception e) { Console.Error.WriteLine($"FAIL {name}: {e}"); failures++; }
}

async Task TestAsync(string name, Func<Task> run)
{
    try { await run(); Console.WriteLine($"PASS {name}"); passed++; }
    catch (Exception e) { Console.Error.WriteLine($"FAIL {name}: {e}"); failures++; }
}

async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}");
}

Test("split, select by ID, ripple delete and source mapping", () =>
{
    var timeline = new Timeline();
    timeline.Load(media);
    var middle = timeline.Split(3)!.Value;
    timeline.Split(7);
    Check(timeline.Delete(middle), "Delete failed");
    Near(timeline.Duration, 6);
    Near(timeline.Locate(3)!.Value.SourceTime, 7);
    Near(timeline.Locate(4)!.Value.SourceTime, 8);
    Near(timeline.Locate(6)!.Value.SourceTime, 10);
});

Test("separated import creates companion audio slots and bound split stays atomic", () =>
{
    var project = new EditProject();
    var first = project.ImportSeparated(media);
    var second = project.ImportSeparated(media with { Path = "angle-b.mp4" });
    Check(first.VideoTrack.Kind == TrackKind.Video && first.VideoTrack.Clips.Single().Kind == ClipKind.Video,
        "Separated video track has the wrong kind");
    Check(first.AudioTrack is { Kind: TrackKind.Audio } audioTrack && audioTrack.Clips.Single().Kind == ClipKind.Audio &&
        audioTrack.CompanionGroupId == first.VideoTrack.CompanionGroupId,
        "Separated audio track was not created");
    Check(project.BindTracks([first.VideoTrack.Id, second.VideoTrack.Id]).HasValue, "Track binding failed");
    var right = project.Split(first.VideoTrack.Id, 4)
        ?? throw new Exception("Bound split failed");
    Check(project.FindTrack(first.VideoTrack.Id)!.Clips.Count == 2 &&
        project.FindTrack(second.VideoTrack.Id)!.Clips.Count == 2 &&
        project.FindTrack(first.AudioTrack.Id)!.Clips.Count == 2 &&
        project.FindTrack(second.AudioTrack.Id)!.Clips.Count == 2, "Bound split did not propagate to every companion track");
    Check(project.Delete(right), "Local delete failed");
    Check(project.FindTrack(first.VideoTrack.Id)!.Clips.Count == 1 &&
        project.FindTrack(second.VideoTrack.Id)!.Clips.Count == 2, "Delete propagated to a bound track");
});

Test("companion audio slots always reorder with their video tracks", () =>
{
    var project = new EditProject();
    var first = project.ImportSeparated(media);
    var second = project.ImportSeparated(media with { Path = "angle-b.mp4" });
    Check(!project.Move(first.AudioTrack.Clips[0].Id, first.VideoTrack.Id, 0), "Audio clip mixed into a video track");
    Check(project.MoveTrack(second.AudioTrack.Id, 1), "Companion track group reorder failed");
    Check(project.Tracks[1].Id == second.VideoTrack.Id && project.Tracks[2].Id == second.AudioTrack.Id &&
        project.Tracks[3].Id == first.VideoTrack.Id && project.Tracks[4].Id == first.AudioTrack.Id,
        "Audio slot became detached from its video track");
    Check(project.Undo() && project.Tracks[1].Id == first.VideoTrack.Id && project.Tracks[2].Id == first.AudioTrack.Id,
        "Undo did not restore track order");
});

Test("silent video still owns a companion audio slot", () =>
{
    var project = new EditProject();
    var imported = project.ImportSeparated(media with { AudioStreamIndex = null });
    Check(imported.AudioTrack.Kind == TrackKind.Audio &&
        imported.AudioTrack.CompanionGroupId == imported.VideoTrack.CompanionGroupId,
        "Silent video did not retain a companion audio slot");
});

Test("empty companion audio tracks do not block video splits", () =>
{
    var project = new EditProject();
    var imported = project.ImportSeparated(media);
    Check(project.Delete(imported.AudioTrack.Clips[0].Id), "Audio slot could not be emptied");
    var right = project.Split(imported.VideoTrack.Id, 4);
    Check(right.HasValue && project.FindTrack(imported.VideoTrack.Id)!.Clips.Count == 2,
        "Empty companion audio track blocked the video split");
    Check(project.FindTrack(imported.AudioTrack.Id)!.Clips.Count == 0,
        "Splitting video unexpectedly populated the empty audio track");
});

Test("nonempty companion tracks still require coverage at the split point", () =>
{
    var project = new EditProject();
    var imported = project.ImportSeparated(media);
    var audioRight = project.Split(imported.AudioTrack.Id, 3)
        ?? throw new Exception("Initial companion split failed");
    Check(project.Delete(audioRight), "Audio tail could not be deleted");
    Check(project.Split(imported.VideoTrack.Id, 5) is null,
        "A nonempty companion track missing the split point was ignored");
});

Test("subtitle tracks stay inside their video group and edit absolute cue ranges", () =>
{
    var project = new EditProject();
    var imported = project.ImportSeparated(media);
    var first = project.AddSubtitleTrack(imported.VideoTrack.Id) ?? throw new Exception("Subtitle track was not created");
    var second = project.AddSubtitleTrack(imported.VideoTrack.Id) ?? throw new Exception("Second subtitle track was not created");
    Check(project.CompanionTracks(imported.VideoTrack.Id).Select(track => track.Kind)
        .SequenceEqual(new[] { TrackKind.Video, TrackKind.Audio, TrackKind.Subtitle, TrackKind.Subtitle }),
        "Subtitle tracks were detached from the video group");
    var opening = project.AddSubtitle(first.Id, 1, 2, "开场字幕") ?? throw new Exception("Subtitle cue was not created");
    Check(project.AddSubtitle(first.Id, 2, 2, "重叠") is null, "Overlapping subtitle cue was accepted");
    Check(project.UpdateSubtitle(opening, 1.5, 4.5, "新的内容"), "Subtitle cue could not be moved or resized");
    var cue = project.FindSubtitle(opening)!.Value.Cue;
    Check(cue.Text == "新的内容" && cue.Start == 1.5 && cue.End == 4.5, "Subtitle cue edit was not preserved");
    var region = new SubtitleRegion(0.2, 0.1, 0.6, 0.25, SubtitleVerticalAlignment.Center);
    Check(project.SetSubtitleRegion(first.Id, region) && project.FindTrack(first.Id)!.SubtitleRegion == region,
        "Subtitle guide rectangle was not updated");
    Check(project.SetSubtitleAlignment(first.Id, SubtitleVerticalAlignment.Top) &&
        project.FindTrack(first.Id)!.SubtitleRegion!.Alignment == SubtitleVerticalAlignment.Top,
        "Subtitle alignment was not updated");
    project.ImportSeparated(media with { Path = "other.mp4" });
    Check(project.MoveTrack(second.Id, project.Tracks.Count) && project.CompanionTracks(imported.VideoTrack.Id).Count == 4,
        "Moving a subtitle track detached the companion group");
    Check(project.Undo(), "Subtitle group move could not be undone");
});

Test("subtitle project snapshots validate text, bounds, ordering and layout", () =>
{
    var project = new EditProject();
    var video = project.ImportSeparated(media).VideoTrack;
    var subtitle = project.AddSubtitleTrack(video.Id)!;
    project.AddSubtitle(subtitle.Id, 0.5, 2.5, "第一行\n第二行");
    project.SetSubtitleRegion(subtitle.Id, new(0.1, 0.7, 0.8, 0.2, SubtitleVerticalAlignment.Bottom));
    var restored = EditProject.FromSnapshot(project.ExportSnapshot());
    Check(restored.SubtitleTracks(video.Id).Single().SubtitleCues.Single().Text == "第一行\n第二行",
        "Subtitle snapshot lost cue text");

    var track = subtitle with
    {
        Cues = Array.AsReadOnly(new[] { new SubtitleCue(Guid.NewGuid(), 9, 11, "越界") }),
        SubtitleRegion = SubtitleRegion.Default
    };
    var broken = project.ExportSnapshot() with
    {
        Tracks = project.Tracks.Select(candidate => candidate.Id == subtitle.Id ? track : candidate).ToArray()
    };
    try { EditProject.FromSnapshot(broken); throw new Exception("Out-of-range subtitle snapshot was accepted"); }
    catch (InvalidDataException) { }
});

Test("boundary splits do not create empty segments", () =>
{
    var timeline = new Timeline();
    timeline.Load(media);
    Check(timeline.Split(0) is null && timeline.Split(10) is null, "Boundary split was accepted");
    Check(timeline.Split(0.001) is null, "Sub-frame split was accepted");
    timeline.Split(5);
    Check(timeline.Split(5) is null && timeline.Segments.Count == 2, "Duplicate split created a segment");
});

Test("playback crosses live cuts without seeking or losing source time", () =>
{
    var timeline = new Timeline();
    timeline.Load(media);
    var originalId = timeline.Segments[0].Id;
    var rightId = timeline.Split(2)!.Value;
    var before = timeline.AdvancePlayback(originalId, 1.99)!.Value;
    Check(before.Position.Segment.Id == originalId && !before.RequiresSeek && !before.ReachedEnd, "Cut interrupted the left side");
    var after = timeline.AdvancePlayback(originalId, 2.12)!.Value;
    Check(after.Position.Segment.Id == rightId && !after.RequiresSeek && !after.ReachedEnd, "Contiguous cut requested a seek");
    Near(after.Position.SourceTime, 2.12);
    Near(after.TimelineTime, 2.12);
    timeline.Split(2.2);
    var lastId = timeline.Split(2.3)!.Value;
    var late = timeline.AdvancePlayback(originalId, 2.65)!.Value;
    Check(late.Position.Segment.Id == lastId && !late.RequiresSeek, "Late timer failed to cross multiple cuts");
    Near(late.TimelineTime, 2.65);
    Near(timeline.Duration, media.Duration);
});

Test("playback still skips deleted footage and stops at the retained end", () =>
{
    var timeline = new Timeline();
    timeline.Load(media);
    var first = timeline.Segments[0].Id;
    var removed = timeline.Split(2)!.Value;
    var last = timeline.Split(5)!.Value;
    timeline.Delete(removed);
    var gap = timeline.AdvancePlayback(first, 2.1)!.Value;
    Check(gap.RequiresSeek && !gap.ReachedEnd && gap.Position.Segment.Id == last, "Deleted footage was not skipped");
    Near(gap.Position.SourceTime, 5);
    Near(gap.TimelineTime, 2);
    var resumed = timeline.AdvancePlayback(last, 5.4)!.Value;
    Check(!resumed.RequiresSeek, "Playback repeatedly sought after crossing a gap");
    Near(resumed.TimelineTime, 2.4);
    var end = timeline.AdvancePlayback(last, 10.2)!.Value;
    Check(end.ReachedEnd && !end.RequiresSeek, "Playback failed to finish");
    Near(end.TimelineTime, 7);
    Check(timeline.AdvancePlayback(removed, 3) is null && timeline.AdvancePlayback(last, double.NaN) is null,
        "Invalid playback cursor was accepted");
});

Test("frame-rounded live cuts preserve the playback position on either side", () =>
{
    foreach (var time in new[] { 2.01, 2.02 })
    {
        var timeline = new Timeline();
        timeline.Load(media);
        timeline.Split(time);
        var cursor = timeline.Locate(time)!.Value;
        var playback = timeline.AdvancePlayback(cursor.Segment.Id, time)!.Value;
        Check(!playback.RequiresSeek && !playback.ReachedEnd, "Rounded split requested playback interruption");
        Near(playback.TimelineTime, time);
        Near(playback.Position.SourceTime, time);
    }
});

Test("undo/redo restores IDs and all-deleted timeline", () =>
{
    var timeline = new Timeline();
    timeline.Load(media);
    var original = timeline.Segments[0];
    timeline.Delete(original.Id);
    Check(timeline.Locate(0) is null && timeline.Duration == 0, "Empty timeline is not empty");
    timeline.Undo();
    Check(timeline.Segments[0] == original, "Undo lost segment identity");
    timeline.Redo();
    Check(timeline.Segments.Count == 0, "Redo failed");
    timeline.Undo();
    timeline.Split(4);
    Check(!timeline.CanRedo, "New edit did not discard redo history");
    timeline.Load(media);
    Check(!timeline.CanUndo && !timeline.CanRedo, "Import retained old history");
});

Test("random edits preserve timeline invariants", () =>
{
    var timeline = new Timeline();
    timeline.Load(media);
    var random = new Random(47);
    for (var i = 0; i < 1000; i++)
    {
        switch (random.Next(4))
        {
            case 0: timeline.Split(random.NextDouble() * timeline.Duration); break;
            case 1: if (timeline.Segments.Count > 0) timeline.Delete(timeline.Segments[random.Next(timeline.Segments.Count)].Id); break;
            case 2: timeline.Undo(); break;
            case 3: timeline.Redo(); break;
        }
        Check(timeline.Segments.All(s => s.Start >= 0 && s.End <= media.Duration && s.Duration > 0), "Invalid range");
        Check(timeline.Segments.Select(s => s.Id).Distinct().Count() == timeline.Segments.Count, "Duplicate ID");
        Near(timeline.Duration, timeline.Segments.Sum(s => s.Duration));
        double offset = 0;
        foreach (var segment in timeline.Segments)
        {
            Near(timeline.Locate(offset + segment.Duration / 2)!.Value.SourceTime, segment.Start + segment.Duration / 2);
            offset += segment.Duration;
        }
    }
});

Test("multiple imports create independent candidate tracks and never implicitly enter the main track", () =>
{
    var project = new EditProject();
    var first = project.Import(media);
    var second = project.Import(media with { Path = "second.mp4", Width = 1080, Height = 1920 });
    Check(project.Tracks.Count == 3 && project.MainTrack.IsMain && !first.IsMain && !second.IsMain, "Incorrect track roles");
    Check(project.ExportClips().Count == 0 && project.Sources.Count == 2, "A candidate was included in export");
    project.Move(first.Clips[0].Id, project.MainTrack.Id, 0);
    Check(project.ExportClips().Single().Media.Path == media.Path && project.FindTrack(first.Id)!.Clips.Count == 0, "Cross-track move copied or lost a clip");
    Check(project.FindTrack(second.Id)!.Clips.Count == 1, "Moving another clip mutated the second candidate track");
    project.Undo();
    Check(project.MainTrack.Clips.Count == 0 && project.FindTrack(first.Id)!.Clips.Count == 1, "Cross-track undo was not atomic");
    project.Undo();
    Check(project.Tracks.Count == 2 && project.Sources.Count == 1, "Undo import left orphaned project state");
    project.Redo();
    Check(project.Tracks[2].Id == second.Id, "Redo import changed track identity");
});

Test("cross-track moves, reordering, and speed have project-wide undo and stable clip identities", () =>
{
    var project = new EditProject();
    var a = project.Import(media);
    var b = project.Import(media with { Path = "b.mp4" });
    var right = project.Split(a.Id, 4)!.Value;
    project.Move(a.Clips[0].Id, project.MainTrack.Id, 0);
    project.Move(b.Clips[0].Id, project.MainTrack.Id, 1);
    project.Move(right, project.MainTrack.Id, 1);
    Check(project.MainTrack.Clips.Select(c => c.Id).SequenceEqual(new[] { a.Clips[0].Id, right, b.Clips[0].Id }), "Insertion order is wrong");
    project.Move(a.Clips[0].Id, project.MainTrack.Id, 3);
    Check(project.MainTrack.Clips[^1].Id == a.Clips[0].Id, "Same-track forward move has an off-by-one error");
    Check(!project.Move(right, project.MainTrack.Id, 0), "No-op move modified history");
    project.SetSpeed(right, 2);
    Near(project.FindClip(right)!.Value.Clip.Duration, 3);
    project.Undo();
    Near(project.FindClip(right)!.Value.Clip.Speed, 1);
    project.Undo();
    Check(project.MainTrack.Clips[0].Id == a.Clips[0].Id, "Undo lost the pre-reorder position");
    project.Redo();
    Check(project.MainTrack.Clips[^1].Id == a.Clips[0].Id, "Redo failed to restore reordering");
    var snapshot = project.ExportClips();
    project.Delete(right);
    Check(snapshot.Count == 3 && project.ExportClips().Count == 2, "Export snapshot changed after another edit");
});

Test("copy placement uses the strict ten-second boundary and undo preserves track and clip identities", () =>
{
    foreach (var duration in new[] { 9.999, 10.0, 10.001 })
    {
        var project = new EditProject();
        var source = project.Import(media with { Duration = duration });
        var below = project.Import(media with { Path = "below.mp4" });
        var original = source.Clips[0];
        project.Rename(original.Id, "镜头 A");
        original = project.FindClip(original.Id)!.Value.Clip;
        var before = project.Tracks.ToArray();
        var copyId = project.Duplicate(original.Id)!.Value;
        var copy = project.FindClip(copyId)!.Value;
        Check(copy.Clip.Id != original.Id && copy.Clip with { Id = original.Id } == original,
            "Copy changed the source range, speed, label or media");
        Check(project.Sources.Count == 2 && project.FindTrack(below.Id)!.Clips.Count == 1, "Copy mutated source media or the track below");
        if (duration < 10)
            Check(copy.TrackId == source.Id && copy.Index == 1 && project.Tracks.Count == before.Length, "Short copy did not insert immediately after its source");
        else
            Check(project.Tracks[2].Id == copy.TrackId && !project.Tracks[2].IsMain && project.Tracks[3].Id == below.Id && copy.Index == 0,
                "Long copy did not create a candidate directly below its source track");
        project.Undo();
        Check(project.Tracks.SequenceEqual(before) && project.FindClip(copyId) is null, "Undo copy left an extra track or clip");
        project.Redo();
        Check(project.FindClip(copyId) == copy, "Redo copy changed its identity or placement");
        project.Rename(copyId, "副本标记");
        Check(project.FindClip(original.Id)!.Value.Clip.DisplayName == "镜头 A", "Naming a copy changed the source clip");
    }
});

Test("copies use the edited duration and insert before the following clip", () =>
{
    var project = new EditProject();
    var track = project.Import(media);
    var right = project.Split(track.Id, 4)!.Value;
    var original = track.Clips[0].Id;
    var copy = project.Duplicate(original)!.Value;
    Check(project.FindTrack(track.Id)!.Clips.Select(c => c.Id).SequenceEqual(new[] { original, copy, right }), "Copy appended at the end instead of after the source");
    Near(project.FindClip(copy)!.Value.TimelineStart, 4);
    project.SetSpeed(right, 0.5);
    var longCopy = project.Duplicate(right)!.Value;
    Check(project.FindClip(longCopy)!.Value.TrackId != track.Id, "A slowed twelve-second clip stayed in the source track");
    project.SetSpeed(right, 2);
    var shortCopy = project.Duplicate(right)!.Value;
    Check(project.FindClip(shortCopy)!.Value.TrackId == track.Id, "A sped-up three-second clip created a new track");
    Check(project.Duplicate(Guid.NewGuid()) is null, "Copy accepted a missing clip");
});

Test("clip names are independent, undoable metadata and survive splitting", () =>
{
    var project = new EditProject();
    var first = project.Import(media);
    var second = project.Import(media);
    var id = first.Clips[0].Id;
    var original = first.Clips[0];
    Check(project.Rename(id, "  开场 / S-X 镜头  "), "Rename failed");
    Check(project.FindClip(id)!.Value.Clip.DisplayName == "开场 / S-X 镜头" && project.FindClip(second.Clips[0].Id)!.Value.Clip.DisplayName == media.FileName,
        "Rename affected another instance of the same source");
    Check(project.FindClip(id)!.Value.Clip.Media == media && project.Sources.Single() == media, "Rename changed the underlying file");
    Check(!project.Rename(id, "开场 / S-X 镜头"), "No-op rename created history");
    foreach (var invalid in new[] { "", "   ", "\t\r\n" })
    {
        try { project.Rename(id, invalid); throw new Exception("An empty name was accepted"); }
        catch (ArgumentException) { }
    }
    project.Undo();
    Check(project.FindClip(id)!.Value.Clip == original, "Invalid or no-op naming changed undo history");
    project.Redo();
    var right = project.Split(first.Id, 4)!.Value;
    Check(project.FindClip(right)!.Value.Clip.DisplayName == "开场 / S-X 镜头", "Split discarded the clip label");
});

Test("export selection includes every nonempty track and requires an explicit choice for multiple tracks", () =>
{
    var project = new EditProject();
    Check(project.ExportableTracks.Count == 0 && project.ResolveExportTrack(null) is null, "Empty project has an export target");
    var a = project.Import(media);
    Check(project.ResolveExportTrack(null)?.Id == a.Id, "The only nonempty candidate was not the default export target");
    var b = project.Import(media with { Path = "candidate.mp4", Width = 1080, Height = 1920 });
    Check(project.ExportableTracks.Select(t => t.Id).SequenceEqual(new[] { a.Id, b.Id }) && project.ResolveExportTrack(null) is null,
        "Multiple tracks silently defaulted to one track or included the empty main track");
    Check(project.ResolveExportTrack(b.Id)?.Id == b.Id && project.ResolveExportTrack(b.Clips[0].Id) is null,
        "A clip selection was mistaken for an explicit track selection");
    Check(project.ResolveExportTrack(project.MainTrack.Id) is null, "An empty selected track became exportable");
    var snapshot = project.ExportClips(b.Id);
    Check(snapshot.Single().Media.Path == "candidate.mp4", "Candidate export selected footage from a different track");
    project.Delete(b.Clips[0].Id);
    Check(snapshot.Count == 1 && project.ExportClips(b.Id).Count == 0 && project.ExportableTracks.Count == 1,
        "Export snapshot or dropdown retained mutable/deleted clips");
    project.Undo();
    Check(project.ExportableTracks.Count == 2, "Undo failed to restore exportable tracks");
    try { project.ExportClips(Guid.NewGuid()); throw new Exception("Missing export track silently fell back to the main track"); }
    catch (ArgumentException) { }
});

Test("clip speed maps timeline time to source frames for split and playback", () =>
{
    var project = new EditProject();
    var track = project.Import(media);
    var original = track.Clips[0].Id;
    project.SetSpeed(original, 2);
    Near(project.Locate(track.Id, 1.5)!.Value.SourceTime, 3);
    var right = project.Split(track.Id, 2)!.Value;
    var clips = project.FindTrack(track.Id)!.Clips;
    Near(clips[0].End, 4);
    Near(clips[0].Duration, 2);
    Near(clips[1].Speed, 2);
    var continued = project.AdvancePlayback(track.Id, original, 4.4)!.Value;
    Check(continued.Position.Clip.Id == right && !continued.RequiresSeek, "Live cut introduced a seek");
    Near(continued.Position.TimelineTime, 2.2);
    project.SetSpeed(right, 0.5);
    Near(project.Locate(track.Id, 4)!.Value.SourceTime, 5);
    Near(project.MainTrack.Duration, 0);
    var next = project.Import(media with { Path = "next.mp4" });
    project.Move(next.Clips[0].Id, track.Id, 2);
    var switched = project.AdvancePlayback(track.Id, right, 10)!.Value;
    Check(switched.RequiresSeek && switched.Position.Clip.Media.Path == "next.mp4", "Cross-source continuation did not request a new source");
    Check(project.AdvancePlayback(project.MainTrack.Id, right, 8) is null, "Playback crossed into a different track");
});

Test("invalid speed cannot mutate a project and tempo stages preserve the requested factor", () =>
{
    var project = new EditProject();
    var track = project.Import(media);
    foreach (var speed in new[] { 0, -1, double.NaN, double.PositiveInfinity, 0.01, 8.1 })
    {
        try { project.SetSpeed(track.Clips[0].Id, speed); throw new Exception("Invalid speed was accepted"); }
        catch (ArgumentException) { }
        Near(project.FindTrack(track.Id)!.Duration, 10);
    }
    foreach (var speed in new[] { 0.1, 0.25, 0.5, 1, 1.37, 2, 4, 8 })
    {
        var stages = ExportService.BuildTempoFilter(speed).Split(',').Select(s => double.Parse(s.Split('=')[1], CultureInfo.InvariantCulture)).ToArray();
        Check(stages.All(s => s >= 0.5 && s <= 2), "Tempo stage can skip audio samples");
        Near(stages.Aggregate(1.0, (a, b) => a * b), speed);
    }
});

Test("multi-source filter normalizes each video and pads silent sections before concatenating", () =>
{
    VideoClip[] clips = [VideoClip.Create(media with { AudioStreamIndex = null }) with { Speed = 2 },
        VideoClip.Create(media with { Path = "portrait.mp4", Width = 1080, Height = 1920, FrameRate = 24 }) with { Speed = 0.5 }];
    var filter = ExportService.BuildFilter(clips, new());
    Check(filter.Contains("[0:0]") && filter.Contains("[1:0]") && filter.Contains("anullsrc") && filter.Contains("atempo=0.5"), "Missing input or audio normalization");
    Check(filter.Split("pad=1920:1080").Length == 3 && filter.Contains("concat=n=2:v=1:a=1"), "Videos were not scaled before concat");
    var arguments = ExportService.BuildArguments(clips, new(), "graph", "output");
    Check(arguments.Count(s => s == "-hwaccel") == 2 && arguments.Contains("[audio]"), "NVDEC or audio mapping omitted an input");
});

Test("random multi-track edits preserve nonoverlap, speed mappings, and unique IDs", () =>
{
    var project = new EditProject();
    project.Import(media);
    project.Import(media with { Path = "other.mp4" });
    var random = new Random(28);
    for (var iteration = 0; iteration < 500; iteration++)
    {
        var all = project.Tracks.SelectMany(t => t.Clips).ToArray();
        var track = project.Tracks[random.Next(project.Tracks.Count)];
        var clip = all.Length > 0 ? all[random.Next(all.Length)] : null;
        switch (random.Next(8))
        {
            case 0: project.Split(track.Id, random.NextDouble() * track.Duration); break;
            case 1: if (clip is not null) project.Move(clip.Id, track.Id, random.Next(track.Clips.Count + 1)); break;
            case 2: if (clip is not null) project.SetSpeed(clip.Id, new[] { 0.25, 0.5, 1, 1.25, 2, 4 }[random.Next(6)]); break;
            case 3: if (clip is not null) project.Delete(clip.Id); break;
            case 4: project.Undo(); break;
            case 5: project.Redo(); break;
            case 6: if (clip is not null) project.Duplicate(clip.Id); break;
            case 7: if (clip is not null) project.Rename(clip.Id, $"镜头 {iteration}"); break;
        }
        all = project.Tracks.SelectMany(t => t.Clips).ToArray();
        Check(all.Select(c => c.Id).Distinct().Count() == all.Length, "A drag duplicated a clip ID");
        foreach (var lane in project.Tracks)
        {
            double offset = 0;
            foreach (var segment in lane.Clips)
            {
                segment.Validate();
                Near(project.Locate(lane.Id, offset + segment.Duration / 2)!.Value.SourceTime, (segment.Start + segment.End) / 2);
                offset += segment.Duration;
            }
            Near(lane.Duration, offset);
        }
    }
});

Test("portable project files round-trip edits and safely relink source paths", () =>
{
    var project = new EditProject();
    var imported = project.ImportSeparated(media);
    var right = project.Split(imported.VideoTrack.Id, 4)!.Value;
    project.SetSpeed(right, 2);
    project.Rename(right, "结尾镜头");
    var workspace = new WorkspaceSnapshot(imported.VideoTrack.Id, null, right, 4.5, 1.25, 2, 120);
    var document = new ClipProjectFile(ProjectFile.FormatName, ProjectFile.CurrentVersion, 123,
        project.ExportSnapshot(), [new(media.Path, media.FileName, 10, 1, "video/mp4", new string('0', 64))], workspace);

    var json = ProjectFile.Serialize(document);
    Check(json.Contains("\"format\": \"clip-project\"") && json.Contains("\"kind\": \"video\"") &&
        json.Contains("\"type\": \"video/mp4\""),
        "Project JSON is not portable camel-case data");
    var parsed = ProjectFile.Deserialize(json);
    var restored = EditProject.FromSnapshot(parsed.Project,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [media.Path] = "moved/source.mp4" });
    var clip = restored.FindClip(right)!.Value.Clip;
    Check(clip.DisplayName == "结尾镜头" && clip.Speed == 2 && clip.Media.Path == "moved/source.mp4",
        "Project edit data or source relinking was lost");
    Check(!restored.CanUndo && !restored.CanRedo, "Restored project unexpectedly persisted undo history");
    try
    {
        ProjectFile.Deserialize(json.Replace("\"version\": 1", "\"version\": 2"));
        throw new Exception("Unsupported project version was accepted");
    }
    catch (InvalidDataException) { }
});

await TestAsync("source fingerprints sample content and ignore file timestamps", async () =>
{
    var path = Path.Combine(Path.GetTempPath(), $"clip-fingerprint-{Guid.NewGuid():N}.bin");
    try
    {
        var bytes = new byte[ProjectFile.FingerprintSampleBytes * 5];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i % 251);
        await File.WriteAllBytesAsync(path, bytes);
        var first = await ProjectFile.FingerprintAsync(path, "source.mp4");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-3));
        var timestampChanged = await ProjectFile.FingerprintAsync(path, "source.mp4");
        Check(ProjectFile.SameVideo(first, timestampChanged), "Timestamp change altered source identity");
        bytes[bytes.Length / 2] ^= 0xff;
        await File.WriteAllBytesAsync(path, bytes);
        var changed = await ProjectFile.FingerprintAsync(path, "source.mp4");
        Check(!ProjectFile.SameVideo(first, changed), "Sampled content change was not detected");
    }
    finally { if (File.Exists(path)) File.Delete(path); }
});

Test("probe ignores cover art and handles portrait rotation, rational FPS and silent sources", () =>
{
    const string json = """
        {"streams":[
          {"index":0,"codec_type":"video","disposition":{"attached_pic":1},"width":600,"height":600},
          {"index":2,"codec_type":"video","codec_name":"h264","width":1920,"height":1080,"duration":"5.0","avg_frame_rate":"30000/1001","side_data_list":[{"rotation":-90}]}],
         "format":{"duration":"5.2","start_time":"0"}}
        """;
    var info = FfmpegTools.ParseProbe("phone.mov", json);
    Check(info.Width == 1080 && info.Height == 1920 && !info.HasAudio && info.VideoStreamIndex == 2, "Incorrect stream metadata");
    Near(info.FrameRate, 29.97002997);
    Near(info.Duration, 5);
});

Test("filter uses only retained intervals and correct audio/video stream indexes", () =>
{
    var filter = ExportService.BuildFilter(media with { VideoStreamIndex = 2, AudioStreamIndex = 3 },
        [Segment.Create(0, 2), Segment.Create(5, 10)], new());
    Check(filter.Contains("[0:2]") && filter.Contains("[0:3]") && filter.Contains("trim=start=5:end=10"), "Wrong source ranges");
    Check(filter.Contains("concat=n=2:v=1:a=1") && filter.Contains("apad"), "Audio concat missing");
});

Test("NVIDIA decoder and encoder can be configured independently", () =>
{
    var hardware = ExportService.BuildArguments(media, new(), "filter.ffgraph", "output.mp4");
    Check(hardware.Contains("cuda") && hardware.Contains("h264_nvenc"), "Hardware acceleration flags missing");
    var hybrid = ExportService.BuildArguments(media, new(HardwareDecode: false), "filter.ffgraph", "output.mp4");
    Check(!hybrid.Contains("-hwaccel") && hybrid.Contains("h264_nvenc"), "Hardware decode toggle did not work");
    var software = ExportService.BuildArguments(media, new(Encoder: VideoEncoder.Software, HardwareDecode: false), "filter.ffgraph", "output.mp4");
    Check(software.Contains("libx264") && !software.Contains("cuda"), "Software fallback unavailable");
});

Test("NVENC probe uses hardware-safe dimensions and the export encoding profile", () =>
{
    var probe = FfmpegTools.BuildNvidiaProbeArguments().ToList();
    var input = probe[probe.IndexOf("-i") + 1];
    var size = System.Text.RegularExpressions.Regex.Match(input, @"s=(\d+)x(\d+)");
    Check(size.Success, "Probe is missing its test frame dimensions");
    var width = int.Parse(size.Groups[1].Value, CultureInfo.InvariantCulture);
    var height = int.Parse(size.Groups[2].Value, CultureInfo.InvariantCulture);
    // H.264 on Turing has a minimum width of 145 and minimum height of 49.
    Check(width >= 145 && height >= 49 && width % 2 == 0 && height % 2 == 0,
        "Probe would reject a working NVENC encoder because the test frame is too small or odd-sized");
    Check(probe.Contains("yuv420p") && !probe.Contains("-hwaccel"), "Probe must use SDR software frames independently of NVDEC");
    var export = ExportService.BuildArguments(media, new(ExportQuality.Balanced, HardwareDecode: false), "filter.ffgraph", "output.mp4").ToList();
    foreach (var option in new[] { "-c:v", "-preset", "-tune", "-rc", "-cq", "-b:v" })
    {
        Check(probe.Contains(option) && export.Contains(option), $"Missing encoder option: {option}");
        Check(probe[probe.IndexOf(option) + 1] == export[export.IndexOf(option) + 1], $"Probe differs from export: {option}");
    }
    Check(probe.Contains("-nostdin") && probe[probe.IndexOf("-frames:v") + 1] == "1", "Probe must be noninteractive and finite");
});

Test("NVENC failures preserve raw errors and distinguish driver, build and device problems", () =>
{
    (string Error, string Hint)[] failuresToCheck =
    [
        ("Driver does not support the required nvenc API version. Required: 13.0 Found: 12.2\nThe minimum required Nvidia driver for nvenc is 570.0 or newer", "驱动不兼容"),
        ("Unknown encoder 'h264_nvenc'", "未包含 h264_nvenc"),
        ("Unrecognized option 'rc'.\nError splitting the argument list: Option not found", "不支持 NVENC 检测所需参数"),
        ("Cannot load nvcuda.dll", "驱动组件"),
        ("Cannot load nvEncodeAPI64.dll", "驱动组件"),
        ("Cannot load libnvidia-encode.so.1", "驱动组件"),
        ("No capable devices found", "未找到可用"),
        ("InitializeEncoder failed: invalid param (8)", "试编码失败"),
        ("", "试编码失败")
    ];
    foreach (var (error, hint) in failuresToCheck)
    {
        var result = new NvidiaEncoderProbeResult(1, error);
        Check(!result.IsAvailable && result.Summary.Contains(hint), $"Wrong diagnostic for: {error}");
        Check(result.StandardError == error && result.ExitCode == 1, "Raw FFmpeg failure was lost");
    }
    Check(new NvidiaEncoderProbeResult(0, "warning").IsAvailable, "Successful encoding must not be rejected for stderr warnings");
    Check(!new NvidiaEncoderProbeResult(-1, "h264_nvenc is listed").IsAvailable, "Encoder listing is not proof of working hardware");
});

await TestAsync("NVENC detection respects cancellation before launching FFmpeg", async () =>
{
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    var unavailableTools = new FfmpegTools("clip-test-missing-ffmpeg", "clip-test-missing-ffprobe");
    await ThrowsAsync<OperationCanceledException>(() => unavailableTools.ProbeNvidiaAsync(cancellation.Token));
});

Test("custom dimensions validate and silent sources omit audio", () =>
{
    var options = new ExportOptions(Width: 720, Height: 1280);
    Check(options.GetDimensions(media) == (720, 1280), "Custom size lost");
    var filter = ExportService.BuildFilter(media with { AudioStreamIndex = null }, [Segment.Create(0, 5)], options);
    Check(!filter.Contains("atrim") && filter.Contains("a=0") && filter.Contains("pad=720:1280"), "Silent video filter incorrect");
    try { new ExportOptions(Width: 721, Height: 1280).GetDimensions(media); throw new Exception("Odd width accepted"); }
    catch (ArgumentException) { }
});

Test("filter numbers use invariant culture", () =>
{
    var previous = CultureInfo.CurrentCulture;
    try
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        var filter = ExportService.BuildFilter(media, [Segment.Create(1.25, 5.5)], new());
        Check(filter.Contains("start=1.25:end=5.5"), "Locale corrupted FFmpeg expression");
    }
    finally { CultureInfo.CurrentCulture = previous; }
});

if (args.Contains("--integration"))
{
    var tools = FfmpegTools.Discover();
    var exporter = new ExportService(tools);
    var root = Path.Combine(Path.GetTempPath(), "clip-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var source = Path.Combine(root, "source '测试' video.mp4");
    async Task Run(params string[] arguments)
    {
        var result = await ProcessRunner.RunAsync(tools.Ffmpeg, arguments);
        if (result.ExitCode != 0) throw new Exception(result.StandardError);
    }
    try
    {
        await tools.VerifyAsync();
        await TestAsync("NVENC probe source produces a valid SDR frame without NVIDIA decoding", async () =>
        {
            var probe = FfmpegTools.BuildNvidiaProbeArguments().ToList();
            var probeSource = probe[probe.IndexOf("-i") + 1];
            var output = Path.Combine(root, "probe-frame.mp4");
            await Run("-v", "error", "-nostdin", "-f", "lavfi", "-i", probeSource,
                "-frames:v", "1", "-an", "-c:v", "libx264", "-pix_fmt", "yuv420p", output);
            var frame = await tools.ProbeAsync(output);
            Check(frame.Width >= 145 && frame.Height >= 49 && frame.Codec == "h264", "Invalid NVENC probe source");
        });

        await TestAsync("real NVENC probe retains failure diagnostics or completes actual encoding", async () =>
        {
            var result = await tools.ProbeNvidiaAsync();
            Check(result.IsAvailable || !string.IsNullOrWhiteSpace(result.StandardError), "Failed probe discarded FFmpeg diagnostics");
            Console.WriteLine($"  NVENC available: {result.IsAvailable}; {result.Summary}");
        });

        await Run("-v", "error", "-y", "-f", "lavfi", "-i", "color=red:s=320x180:r=30:d=2",
            "-f", "lavfi", "-i", "color=blue:s=320x180:r=30:d=2", "-f", "lavfi", "-i", "color=green:s=320x180:r=30:d=2",
            "-f", "lavfi", "-i", "aevalsrc=sin(2*PI*if(lt(t\\,2)\\,440\\,if(lt(t\\,4)\\,880\\,660))*t)*0.12:s=48000:d=6",
            "-filter_complex", "[0:v][1:v][2:v]concat=n=3:v=1:a=0[v]", "-map", "[v]", "-map", "3:a",
            "-c:v", "libx264", "-g", "300", "-pix_fmt", "yuv420p", "-c:a", "aac", source);
        var input = await tools.ProbeAsync(source);
        var software = new ExportOptions(Encoder: VideoEncoder.Software, HardwareDecode: false);
        await TestAsync("FFmpeg exports only the mixed-source main track with speed, silence, resolution and frame-rate normalization", async () =>
        {
            var portraitPath = Path.Combine(root, "portrait candidate 静音.mp4");
            await Run("-v", "error", "-f", "lavfi", "-i", "color=yellow:s=180x320:r=24:d=4",
                "-c:v", "libx264", "-pix_fmt", "yuv420p", portraitPath);
            var portrait = await tools.ProbeAsync(portraitPath);
            var project = new EditProject();
            var a = project.Import(input);
            var b = project.Import(portrait);
            // If candidate inputs are accidentally included, FFmpeg will fail to open this file.
            project.Import(input with { Path = Path.Combine(root, "must-not-be-read.mp4") });
            project.Split(a.Id, 2);
            var greenId = project.Split(a.Id, 4)!.Value;
            project.Split(b.Id, 1);
            project.Move(a.Clips[0].Id, project.MainTrack.Id, 0);
            project.SetSpeed(a.Clips[0].Id, 2);
            project.Move(b.Clips[0].Id, project.MainTrack.Id, 1);
            project.SetSpeed(b.Clips[0].Id, 0.5);
            project.Move(greenId, project.MainTrack.Id, 2);
            Near(project.MainTrack.Duration, 5);
            var output = Path.Combine(root, "multi-source-main.mp4");
            await exporter.ExportAsync(project, software, output);
            var result = await tools.ProbeAsync(output);
            Near(result.Duration, 5, 0.07);
            Check(result.Width == 320 && result.Height == 180 && result.HasAudio, "Mixed-source output metadata was not normalized");
            var rgb = Path.Combine(root, "multitrack.rgb");
            await Run("-v", "error", "-i", output, "-vf", "scale=1:1", "-f", "rawvideo", "-pix_fmt", "rgb24", rgb);
            var bytes = await File.ReadAllBytesAsync(rgb);
            var red = 0; var yellow = 0; var green = 0;
            for (var i = 0; i < bytes.Length; i += 3)
            {
                var r = bytes[i]; var g = bytes[i + 1]; var blue = bytes[i + 2];
                Check(blue < Math.Max(r, g), "Candidate-only blue footage leaked into the export");
                if (r > g * 1.5) red++;
                else if (g > r * 1.5) green++;
                else { Check(r > 20 && g > 20, "Portrait section was not visible"); yellow++; }
            }
            Check(Math.Abs(red - 30) <= 1 && Math.Abs(yellow - 60) <= 1 && Math.Abs(green - 60) <= 1,
                $"Speed changed the wrong output ranges: {red}, {yellow}, {green}");
            var audioPath = Path.Combine(root, "multitrack.pcm");
            await Run("-v", "error", "-i", output, "-map", "0:a:0", "-f", "s16le", "-ac", "1", "-ar", "48000", audioPath);
            var audio = await File.ReadAllBytesAsync(audioPath);
            Near(audio.Length / 96000.0, 5, 0.08);
            for (var sample = 72000; sample < 120000; sample++)
                Check(Math.Abs((int)BitConverter.ToInt16(audio, sample * 2)) < 30, "Silent candidate did not produce silence on the main track");
            double Energy(int frequency)
            {
                double real = 0, imaginary = 0;
                for (var sample = 9600; sample < 28800; sample++)
                {
                    var value = BitConverter.ToInt16(audio, sample * 2);
                    real += value * Math.Cos(2 * Math.PI * frequency * sample / 48000);
                    imaginary += value * Math.Sin(2 * Math.PI * frequency * sample / 48000);
                }
                return real * real + imaginary * imaginary;
            }
            Check(Energy(440) > Energy(880) * 30, "Speed adjustment changed pitch or retained deleted audio");
            project.Move(a.Clips[0].Id, a.Id, 0);
            project.Move(greenId, a.Id, 1);
            var silentOutput = Path.Combine(root, "candidate-audio-excluded.mp4");
            await exporter.ExportAsync(project, software, silentOutput);
            Check(!(await tools.ProbeAsync(silentOutput)).HasAudio, "Audio on a candidate track leaked into a silent main track");
        });

        await TestAsync("FFmpeg exports named candidate copies without reading the main track or other candidates", async () =>
        {
            var project = new EditProject();
            var missing = project.Import(input with { Path = Path.Combine(root, "missing-main.mp4") });
            project.Move(missing.Clips[0].Id, project.MainTrack.Id, 0);
            var candidate = project.Import(input);
            project.Import(input with { Path = Path.Combine(root, "missing-other-candidate.mp4") });
            var remainder = project.Split(candidate.Id, 1)!.Value;
            project.Delete(remainder);
            var original = candidate.Clips[0].Id;
            project.Rename(original, "开场 / 命名不改文件路径");
            var copy = project.Duplicate(original)!.Value;
            project.Rename(copy, "复制的开场");
            var output = Path.Combine(root, "chosen-candidate.mp4");
            await exporter.ExportAsync(project, candidate.Id, software, output);
            var result = await tools.ProbeAsync(output);
            Near(result.Duration, 2, 0.07);
            Check(result.Width == input.Width && result.Height == input.Height && result.HasAudio, "Candidate export lost video dimensions or audio");
            var rgb = Path.Combine(root, "candidate-copy.rgb");
            await Run("-v", "error", "-ss", "1.5", "-i", output, "-frames:v", "1", "-vf", "scale=1:1",
                "-f", "rawvideo", "-pix_fmt", "rgb24", rgb);
            var pixel = await File.ReadAllBytesAsync(rgb);
            Check(pixel.Length == 3 && pixel[0] > pixel[1] * 1.5 && pixel[0] > pixel[2] * 1.5, "The copied clip did not retain the source's red first second");
            project.Delete(original);
            project.Delete(copy);
            var emptyOutput = Path.Combine(root, "empty-candidate.mp4");
            await ThrowsAsync<InvalidOperationException>(() => exporter.ExportAsync(project, candidate.Id, software, emptyOutput));
            Check(!File.Exists(emptyOutput), "An empty candidate published an export");
        });

        await TestAsync("FFmpeg handles slow motion, acceleration, and one-frame clips without losing the final frame", async () =>
        {
            foreach (var speed in new[] { 0.1, 0.5, 1.37, 2, 8 })
            {
                var output = Path.Combine(root, $"speed-{speed.ToString(CultureInfo.InvariantCulture)}.mp4");
                var clip = VideoClip.Create(input) with { End = 1, Speed = speed };
                await exporter.ExportAsync(new[] { clip }, software, output);
                Near((await tools.ProbeAsync(output)).Duration, 1 / speed, 0.045);
            }
            var shortest = Path.Combine(root, "fast-one-frame.mp4");
            await exporter.ExportAsync(new[] { VideoClip.Create(input) with { End = 1.0 / 30, Speed = 8 } }, software, shortest);
            Check((await tools.ProbeAsync(shortest)).Duration > 0, "Sub-frame output dropped the entire clip");
            var onlySilent = Path.Combine(root, "speed-silent.mp4");
            var silentPath = Path.Combine(root, "silent-input.mp4");
            await Run("-v", "error", "-i", source, "-map", "0:v:0", "-c", "copy", silentPath);
            var silent = await tools.ProbeAsync(silentPath);
            await exporter.ExportAsync(new[] { VideoClip.Create(silent) with { End = 1, Speed = 0.1 } }, software, onlySilent);
            Near((await tools.ProbeAsync(onlySilent)).Duration, 10, 0.04);
        });

        await TestAsync("FFmpeg precise non-keyframe cuts remove deleted video and audio ranges", async () =>
        {
            var output = Path.Combine(root, "trimmed 测试.mp4");
            await exporter.ExportAsync(input, [Segment.Create(0, 1.3), Segment.Create(4.1, 6)], software, output);
            var result = await tools.ProbeAsync(output);
            Near(result.Duration, 3.2, 0.07);
            Check(result.HasAudio && result.Width == 320 && result.Height == 180, "Output metadata incorrect");
            var pixels = Path.Combine(root, "pixels.rgb");
            await Run("-v", "error", "-i", output, "-vf", "scale=1:1", "-f", "rawvideo", "-pix_fmt", "rgb24", pixels);
            var bytes = await File.ReadAllBytesAsync(pixels);
            var red = 0;
            var green = 0;
            for (var i = 0; i < bytes.Length; i += 3)
            {
                Check(bytes[i + 2] < Math.Max(bytes[i], bytes[i + 1]), "A deleted blue frame leaked into export");
                if (bytes[i] > bytes[i + 1]) red++; else green++;
            }
            Check(Math.Abs(red - 39) <= 1 && Math.Abs(green - 57) <= 1, $"Incorrect cut frames: red={red}, green={green}");
            var audio = Path.Combine(root, "cut-audio.pcm");
            await Run("-v", "error", "-ss", "1.7", "-i", output, "-t", "0.2", "-f", "s16le", "-ac", "1", "-ar", "48000", audio);
            var audioBytes = await File.ReadAllBytesAsync(audio);
            double Energy(int frequency)
            {
                double real = 0, imaginary = 0;
                for (var i = 0; i < audioBytes.Length / 2; i++)
                {
                    var sample = BitConverter.ToInt16(audioBytes, i * 2);
                    real += sample * Math.Cos(2 * Math.PI * frequency * i / 48000);
                    imaginary += sample * Math.Sin(2 * Math.PI * frequency * i / 48000);
                }
                return real * real + imaginary * imaginary;
            }
            Check(Energy(660) > Energy(880) * 100, "Deleted middle audio was retained or audio was not cut with video");
        });

        await TestAsync("FFmpeg silent source exports at exact custom dimensions", async () =>
        {
            var silent = Path.Combine(root, "silent.mp4");
            await Run("-v", "error", "-i", source, "-map", "0:v:0", "-c", "copy", silent);
            var info = await tools.ProbeAsync(silent);
            var output = Path.Combine(root, "portrait.mp4");
            await exporter.ExportAsync(info, [Segment.Create(0.5, 1.5)], software with { Width = 180, Height = 320 }, output);
            var result = await tools.ProbeAsync(output);
            Check(!result.HasAudio && result.Width == 180 && result.Height == 320, "Incorrect silent custom-size output");
            Near(result.Duration, 1, 0.04);
        });

        await TestAsync("short audio is padded across later retained segments", async () =>
        {
            var shortAudio = Path.Combine(root, "short-audio.mp4");
            await Run("-v", "error", "-i", source, "-f", "lavfi", "-i", "sine=frequency=500:duration=1",
                "-map", "0:v:0", "-map", "1:a:0", "-c:v", "copy", "-c:a", "aac", shortAudio);
            var info = await tools.ProbeAsync(shortAudio);
            var output = Path.Combine(root, "padded.mp4");
            await exporter.ExportAsync(info, [Segment.Create(0, 1), Segment.Create(4, 6)], software, output);
            var result = await tools.ProbeAsync(output);
            Near(result.Duration, 3, 0.07);
            var audio = Path.Combine(root, "padded.pcm");
            await Run("-v", "error", "-i", output, "-map", "0:a:0", "-f", "s16le", "-ac", "1", "-ar", "48000", audio);
            var bytes = await File.ReadAllBytesAsync(audio);
            Near(bytes.Length / 96000.0, 3, 0.08);
            Check(bytes.Skip(96000 * 2).All(b => b == 0), "Expected silence after source audio ends");
        });

        await TestAsync("delayed audio keeps its initial silence and AV alignment", async () =>
        {
            var delayed = Path.Combine(root, "delayed.mp4");
            await Run("-v", "error", "-i", source, "-itsoffset", "1", "-i", source,
                "-map", "0:v:0", "-map", "1:a:0", "-c", "copy", delayed);
            var info = await tools.ProbeAsync(delayed);
            var output = Path.Combine(root, "aligned.mp4");
            await exporter.ExportAsync(info, [Segment.Create(0, 3)], software, output);
            var audio = Path.Combine(root, "aligned.pcm");
            await Run("-v", "error", "-i", output, "-map", "0:a:0", "-f", "s16le", "-ac", "1", "-ar", "48000", audio);
            var bytes = await File.ReadAllBytesAsync(audio);
            Check(bytes.Take(48000).All(b => b == 0), "Delayed audio was incorrectly moved to time zero");
            Check(bytes.Skip(120000).Take(20000).Any(b => b != 0), "Expected audible samples after delay");
        });

        await TestAsync("cancellation removes partial output and temporary files", async () =>
        {
            using var cancellation = new CancellationTokenSource();
            var output = Path.Combine(root, "cancelled.mp4");
            var progress = new InlineProgress<ExportProgress>(p => { if (p.Fraction > 0) cancellation.Cancel(); });
            await ThrowsAsync<OperationCanceledException>(() => exporter.ExportAsync(input,
                [Segment.Create(0, 6)], software, output, progress, cancellation.Token));
            Check(!File.Exists(output) && !Directory.EnumerateFiles(root, ".clip-*").Any(), "Partial output was left behind");
        });

        await TestAsync("cancelling a live FFmpeg process stops it promptly", async () =>
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await ThrowsAsync<OperationCanceledException>(() => ProcessRunner.RunAsync(tools.Ffmpeg,
                ["-v", "error", "-re", "-f", "lavfi", "-i", "color=red:s=320x180:r=30:d=30", "-f", "null", "-"], cancellation.Token));
            Check(watch.Elapsed < TimeSpan.FromSeconds(5), "Cancellation did not terminate the running process");
        });

        await TestAsync("source and existing destinations cannot be overwritten", async () =>
        {
            var sourceBytes = await File.ReadAllBytesAsync(source);
            await ThrowsAsync<InvalidOperationException>(() => exporter.ExportAsync(input, [Segment.Create(0, 1)], software, source));
            var target = Path.Combine(root, "existing.mp4");
            await File.WriteAllTextAsync(target, "keep me");
            await ThrowsAsync<IOException>(() => exporter.ExportAsync(input, [Segment.Create(0, 1)], software, target));
            Check(await File.ReadAllTextAsync(target) == "keep me", "Existing destination was modified");
            var sourceAfter = await File.ReadAllBytesAsync(source);
            Check(sourceBytes.SequenceEqual(sourceAfter), "Source was modified");
        });

        await TestAsync("empty timeline and failed FFmpeg runs leave no output", async () =>
        {
            var output = Path.Combine(root, "invalid.mp4");
            await ThrowsAsync<InvalidOperationException>(() => exporter.ExportAsync(input, [], software, output));
            await ThrowsAsync<InvalidOperationException>(() => exporter.ExportAsync(input with { Path = Path.Combine(root, "missing.mp4") },
                [Segment.Create(0, 1)], software, output));
            Check(!File.Exists(output) && !Directory.EnumerateFiles(root, ".clip-*").Any(), "Failed output leaked");
        });
    }
    catch (Exception exception) { Console.Error.WriteLine($"FAIL integration setup: {exception}"); failures++; }
    finally { Directory.Delete(root, recursive: true); }
}

Console.WriteLine($"\n{passed} passed, {failures} failed.");
return failures == 0 ? 0 : 1;

sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
