using System.Text.Json;
namespace AirFlash.App.Services;

/// <summary>Small choices made in the panel that are not part of the session settings.</summary>
internal sealed class PanelPreferences
{
    private static string Path => System.IO.Path.Combine(AppPaths.DataDirectory, "panel.json");
    public bool VolumeKeys { get; set; } = true;
    public bool Blur { get; set; } = true;
    public static PanelPreferences Load()
    {
        try { return File.Exists(Path) ? JsonSerializer.Deserialize<PanelPreferences>(File.ReadAllText(Path)) ?? new() : new(); }
        // A damaged file only costs the remembered choices.
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    public void Save()
    {
        try { Directory.CreateDirectory(AppPaths.DataDirectory); File.WriteAllText(Path, JsonSerializer.Serialize(this)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { AppPaths.Log($"Panel preferences were not saved: {error.Message}"); }
    }
}
