using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Clip.Core;
using Microsoft.Win32;

namespace Clip.Desktop;

public partial class MainWindow
{
    private void RefreshExportButton()
    {
        var tracks = _project.ExportableTracks;
        var ready = _operation is null;
        ExportButton.IsEnabled = ExportTracksButton.IsEnabled = ready && tracks.Count > 0;
        ExportTracksButton.Visibility = tracks.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        ExportButton.Padding = new Thickness(16, 8, tracks.Count > 1 ? 48 : 16, 8);
        ExportButton.ToolTip = _project.ResolveExportTrack(_selectedTrackId) is { } track
            ? $"导出 {track.Name} · {track.Clips.Count} 个片段 · {track.Duration:0.##} 秒"
            : tracks.Count > 0 ? "选择要导出的轨道，也可先点击轨道空白处选中整轨" : "请先导入素材";
        if (!ready || tracks.Count == 0) ExportTracksMenu.IsOpen = false;
    }

    private async void ExportClick(object sender, RoutedEventArgs e)
    {
        if (_operation is not null) return;
        if (_project.ResolveExportTrack(_selectedTrackId) is { } track) await ExportTrackAsync(track.Id);
        else OpenExportTracksMenu();
    }

    private void ExportTracksClick(object sender, RoutedEventArgs e) => OpenExportTracksMenu();

    private void OpenExportTracksMenu()
    {
        if (_operation is not null || _project.ExportableTracks.Count == 0) return;
        ExportTracksMenu.PlacementTarget = ExportButtonGroup;
        ExportTracksMenu.IsOpen = true;
    }

    private void ExportTracksMenuOpened(object sender, RoutedEventArgs e)
    {
        ExportTracksMenu.Items.Clear();
        foreach (var track in _project.ExportableTracks)
        {
            var item = new MenuItem
            {
                Header = $"{track.Name} · {track.Clips.Count} 个片段 · {track.Duration:0.##} 秒",
                Tag = track.Id,
                IsChecked = track.Id == _selectedTrackId,
                IsEnabled = _operation is null
            };
            item.Click += ExportTrackMenuClick;
            ExportTracksMenu.Items.Add(item);
        }
    }

    private async void ExportTrackMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: Guid id }) await ExportTrackAsync(id);
    }

    private async Task ExportTrackAsync(Guid trackId)
    {
        if (_operation is not null || _project.FindTrack(trackId) is not { Clips.Count: > 0 } track) return;
        Pause();
        var clips = _project.ExportClips(trackId);
        var media = clips[0].Media;
        var options = new ExportWindow(media, _nvidiaProbe?.IsAvailable == true, _nvidiaProbe?.Summary,
            $"{track.Name} · {clips.Count} 个片段 · {track.Duration:0.##} 秒") { Owner = this };
        if (options.ShowDialog() != true) return;
        var save = new SaveFileDialog
        {
            Title = $"导出 {track.Name}（请选择新文件名）", Filter = "MP4 视频|*.mp4", DefaultExt = ".mp4", AddExtension = true,
            FileName = Path.GetFileNameWithoutExtension(media.Path) + $"_{track.Name}_clip.mp4", OverwritePrompt = false
        };
        if (save.ShowDialog() != true) return;
        SetBusy(true, $"准备导出 {track.Name}…");
        OperationProgress.IsIndeterminate = false;
        try
        {
            var progress = new Progress<ExportProgress>(p => { OperationProgress.Value = p.Fraction; StatusText.Text = $"{track.Name} · {p.Message} {p.Fraction:P0}"; });
            await new ExportService(_tools).ExportAsync(clips, options.Options!, save.FileName, progress, _operation!.Token);
            StatusText.Text = $"{track.Name} 导出完成 · " + save.FileName;
            if (!_closed && MessageBox.Show(this, $"已导出 {track.Name}，时长 {TimelineControl.FormatTime(track.Duration)}。\n\n在资源管理器中查看？",
                "导出完成", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, Arguments = $"/select,{(char)34}{save.FileName}{(char)34}" });
        }
        catch (OperationCanceledException) { StatusText.Text = "已取消导出，临时文件已清理"; }
        catch (Exception exception) { ShowError(exception); }
        finally { SetBusy(false); }
    }
}
