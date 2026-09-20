using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using Clip.Core;
using Microsoft.Win32;

namespace Clip.Desktop;

public partial class MainWindow
{
    private async void SaveProjectClick(object sender, RoutedEventArgs e)
    {
        if (_operation is not null || _project.Sources.Count == 0) return;
        var dialog = new SaveFileDialog
        {
            Title = "导出项目",
            Filter = "Clip 项目|*.clip",
            DefaultExt = ProjectFile.FileExtension,
            AddExtension = true,
            OverwritePrompt = true,
            FileName = $"Clip-{DateTime.Now:yyyyMMdd-HHmm}.clip"
        };
        if (dialog.ShowDialog(this) != true) return;

        SetBusy(true, "正在核对素材并导出项目…");
        try
        {
            var fingerprints = new List<SourceFingerprint>();
            foreach (var source in _project.Sources)
            {
                _operation!.Token.ThrowIfCancellationRequested();
                fingerprints.Add(await ProjectFile.FingerprintAsync(source.Path, source.Path, _operation.Token));
            }
            var document = new ClipProjectFile(
                ProjectFile.FormatName,
                ProjectFile.CurrentVersion,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                _project.ExportSnapshot(),
                fingerprints.ToArray(),
                new WorkspaceSnapshot(_activeTrackId, _selectedTrackId, _selected, _position,
                    _previewZoom, _timelineZoom, TimelineScroll.HorizontalOffset));
            await File.WriteAllTextAsync(dialog.FileName, ProjectFile.Serialize(document), new UTF8Encoding(false), _operation!.Token);
            StatusText.Text = $"项目已导出 · {Path.GetFileName(dialog.FileName)}（不包含原视频）";
        }
        catch (OperationCanceledException) { StatusText.Text = "已取消导出项目"; }
        catch (Exception exception) { ShowError(exception); }
        finally { SetBusy(false); }
    }

    private async void RestoreProjectClick(object sender, RoutedEventArgs e)
    {
        if (_operation is not null) return;
        var dialog = new OpenFileDialog
        {
            Title = "导入项目",
            Filter = "Clip 项目|*.clip|所有文件|*.*",
            DefaultExt = ProjectFile.FileExtension,
            Multiselect = false,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true) await RestoreProjectAsync(dialog.FileName);
    }

    private async Task RestoreProjectAsync(string projectFilePath)
    {
        if (_operation is not null) return;
        Pause();
        SetBusy(true, "正在读取项目…");
        try
        {
            var json = await File.ReadAllTextAsync(projectFilePath, _operation!.Token);
            var document = ProjectFile.Deserialize(json);
            var resolved = await ResolveProjectSourcesAsync(document, projectFilePath, _operation.Token);
            if (resolved is null)
            {
                StatusText.Text = "已取消导入项目，当前工作未改变";
                return;
            }

            var restored = EditProject.FromSnapshot(document.Project, resolved);
            var assets = new Dictionary<string, PreviewAsset>(StringComparer.OrdinalIgnoreCase);
            var waveforms = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in restored.Sources)
            {
                _operation.Token.ThrowIfCancellationRequested();
                Directory.CreateDirectory(_cacheDirectory);
                BitmapImage? thumbnail = null;
                try
                {
                    var thumbnailPath = Path.Combine(_cacheDirectory, Guid.NewGuid().ToString("N") + ".png");
                    await _tools.MakeThumbnailAsync(source, thumbnailPath, _operation.Token);
                    thumbnail = new BitmapImage();
                    thumbnail.BeginInit();
                    thumbnail.CacheOption = BitmapCacheOption.OnLoad;
                    thumbnail.UriSource = new Uri(thumbnailPath);
                    thumbnail.EndInit();
                    thumbnail.Freeze();
                }
                catch (OperationCanceledException) { throw; }
                catch { /* A valid project can still be edited without a thumbnail. */ }
                if (source.HasAudio)
                {
                    try { waveforms[source.Path] = await _tools.MakeWaveformAsync(source, token: _operation.Token); }
                    catch (OperationCanceledException) { throw; }
                    catch { /* A waveform is decorative and must not block recovery. */ }
                }
                assets[source.Path] = new(source, thumbnail);
            }

            ApplyRestoredProject(restored, document.Workspace, assets, waveforms);
            StatusText.Text = $"已从 {Path.GetFileName(projectFilePath)} 导入项目";
        }
        catch (OperationCanceledException) { StatusText.Text = "已取消导入项目，当前工作未改变"; }
        catch (Exception exception) { ShowError(exception); }
        finally { SetBusy(false); }
    }

    private async Task<Dictionary<string, string>?> ResolveProjectSourcesAsync(
        ClipProjectFile document, string projectFilePath, CancellationToken cancellationToken)
    {
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<SourceFingerprint>();
        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectFilePath))!;
        foreach (var expected in document.Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidates = Path.IsPathRooted(expected.Path)
                ? new[] { expected.Path }
                : new[] { Path.Combine(projectDirectory, expected.Path), expected.Path };
            string? match = null;
            foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(candidate)) continue;
                var actual = await ProjectFile.FingerprintAsync(candidate, expected.Path, cancellationToken);
                if (ProjectFile.SameVideo(actual, expected)) { match = Path.GetFullPath(candidate); break; }
            }
            if (match is null) missing.Add(expected); else resolved[expected.Path] = match;
        }

        if (missing.Count == 0) return resolved;
        StatusText.Text = $"需要重新选择 {missing.Count} 个原视频…";
        var picker = new OpenFileDialog
        {
            Title = $"选择项目使用的原视频（还需 {missing.Count} 个）",
            Filter = "视频文件|*.mp4;*.mov;*.mkv;*.avi;*.webm;*.m4v;*.wmv;*.ts;*.mts;*.m2ts|所有文件|*.*",
            Multiselect = true,
            CheckFileExists = true
        };
        if (picker.ShowDialog(this) != true) return null;

        foreach (var chosen in picker.FileNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(chosen);
            var sizeCandidates = missing.Where(expected => expected.Size == info.Length && !resolved.ContainsKey(expected.Path)).ToArray();
            if (sizeCandidates.Length == 0) continue;
            var actual = await ProjectFile.FingerprintAsync(chosen, cancellationToken: cancellationToken);
            var expected = sizeCandidates.FirstOrDefault(item =>
                string.Equals(item.Name, info.Name, StringComparison.OrdinalIgnoreCase) && ProjectFile.SameVideo(actual, item))
                ?? sizeCandidates.FirstOrDefault(item => ProjectFile.SameVideo(actual, item));
            if (expected is not null) resolved[expected.Path] = Path.GetFullPath(chosen);
        }

        var unresolved = missing.Where(expected => !resolved.ContainsKey(expected.Path)).ToArray();
        if (unresolved.Length > 0)
            throw new InvalidDataException("以下原视频尚未找到，或内容与记录不一致：\n\n" +
                string.Join("\n", unresolved.Select(source => source.Name)));
        return resolved;
    }

    private void ApplyRestoredProject(EditProject restored, WorkspaceSnapshot workspace,
        Dictionary<string, PreviewAsset> assets, Dictionary<string, float[]> waveforms)
    {
        Preview.Close();
        _currentAsset = null;
        _mediaReady = false;
        _playing = _resumeOnOpen = false;
        _playbackClip = _selected = null;
        _selectedTrackId = null;
        _multiSelectMode = false;
        _multiSelectedTracks.Clear();
        _assets.Clear();
        _waveforms.Clear();
        foreach (var pair in assets) _assets.Add(pair.Key, pair.Value);
        foreach (var pair in waveforms) _waveforms.Add(pair.Key, pair.Value);

        _project = restored;
        TimelineView.Project = restored;
        _activeTrackId = restored.FindTrack(workspace.ActiveTrackId)?.Id ?? restored.MainTrack.Id;
        _selectedTrackId = workspace.SelectedTrackId is { } trackId && restored.FindTrack(trackId) is not null ? trackId : null;
        _selected = workspace.SelectedClipId is { } clipId && restored.FindClip(clipId) is not null ? clipId : null;
        _position = Math.Clamp(workspace.Position, 0, ActiveTrack.Duration);
        SetPreviewZoom(workspace.PreviewZoom, false);
        SetTimelineZoom(workspace.TimelineZoom);

        if (restored.Locate(_activeTrackId, _position) is { } position) ActivatePreview(position, false, true);
        else Refresh();
        Dispatcher.BeginInvoke(() => TimelineScroll.ScrollToHorizontalOffset(workspace.TimelineScrollLeft),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }
}
