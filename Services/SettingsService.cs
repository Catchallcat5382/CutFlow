using System.IO;
using System.Text.Json;
using CutFlow.Models;

namespace CutFlow.Services;

public sealed class SettingsService
{
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public string SettingsPath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CutFlow", "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), _json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var temp = SettingsPath + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(settings, _json), cancellationToken).ConfigureAwait(false);
        File.Move(temp, SettingsPath, true);
    }

    public static AppSettings Clone(AppSettings settings) => new()
    {
        ThemeMode = settings.ThemeMode,
        CustomBackgroundR = settings.CustomBackgroundR,
        CustomBackgroundG = settings.CustomBackgroundG,
        CustomBackgroundB = settings.CustomBackgroundB,
        CustomSurfaceR = settings.CustomSurfaceR,
        CustomSurfaceG = settings.CustomSurfaceG,
        CustomSurfaceB = settings.CustomSurfaceB,
        PlaybackVolume = settings.PlaybackVolume,
        SkipSeconds = settings.SkipSeconds,
        AutosaveSeconds = settings.AutosaveSeconds,
        DefaultNoiseProfile = settings.DefaultNoiseProfile,
        PreviewWithoutSilenceByDefault = settings.PreviewWithoutSilenceByDefault,
        FollowPlayhead = settings.FollowPlayhead,
        UiRefreshHz = settings.UiRefreshHz,
        DefaultExportFps = settings.DefaultExportFps,
        DefaultExportResolution = settings.DefaultExportResolution,
        WindowMode = settings.WindowMode,
        SnappingEnabled = settings.SnappingEnabled,
        SnapThresholdSeconds = settings.SnapThresholdSeconds,
        TutorialCompleted = settings.TutorialCompleted,
        ShowFirstRunTutorial = settings.ShowFirstRunTutorial,
        UiSoundsEnabled = settings.UiSoundsEnabled,
        KeyBindings = settings.KeyBindings is null
            ? new AppSettings().KeyBindings
            : new Dictionary<string, string>(settings.KeyBindings, StringComparer.OrdinalIgnoreCase)
    };
}
