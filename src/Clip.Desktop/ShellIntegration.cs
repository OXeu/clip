using System.IO;
using Microsoft.Win32;

namespace Clip.Desktop;

public static class ShellIntegration
{
    private static readonly string[] Associations = ["video", ".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v", ".wmv", ".ts", ".mts", ".m2ts"];
    private static string Verb(string association) => $@"Software\Classes\SystemFileAssociations\{association}\shell\Clip.Edit";

    public static void Register()
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "Clip.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("请先发布或构建 Clip.exe，再注册右键菜单。");
        foreach (var association in Associations)
        {
            using var key = Registry.CurrentUser.CreateSubKey(Verb(association));
            key.SetValue("", "使用 Clip 剪辑");
            key.SetValue("Icon", $"\"{executable}\",0");
            key.SetValue("MultiSelectModel", "Single");
            using var command = key.CreateSubKey("command");
            command.SetValue("", $"\"{executable}\" \"%1\"");
        }
    }

    public static void Unregister()
    {
        foreach (var association in Associations)
            Registry.CurrentUser.DeleteSubKeyTree(Verb(association), throwOnMissingSubKey: false);
    }
}
