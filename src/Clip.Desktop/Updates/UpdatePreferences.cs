using System.IO;
using System.Text.Json;

namespace Clip.Desktop.Updates;

internal sealed record UpdatePreferences(bool DevMode = false)
{
    // Kept separate so the existing FFmpeg settings writer cannot reset the update channel.
    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clip", "updates.json");

    public static UpdatePreferences Load()
    {
        try { return JsonSerializer.Deserialize<UpdatePreferences>(File.ReadAllText(FilePath)) ?? new(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this));
        File.Move(temporary, FilePath, true);
    }
}
