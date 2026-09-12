using System.IO;
using System.Text.Json;
using NeuroCamera.Models;

namespace NeuroCamera.Engine;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON under the current user's roaming
/// AppData folder. Every operation is best-effort: a missing, corrupt, or unwritable
/// settings file falls back to defaults (Load) or is silently skipped (Save) rather than
/// ever throwing into the caller - persisted preferences are a convenience, not something
/// that should be able to crash the app.
/// </summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NeuroCamera", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath);
                AppSettings? settings = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
                if (settings is not null)
                {
                    return settings;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Missing, unreadable, or corrupt settings file - defaults are a safe fallback.
        }

        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            string? directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string json = JsonSerializer.Serialize(settings, SerializerOptions);
            File.WriteAllText(FilePath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort - failing to save preferences should never interrupt the user.
        }
    }
}
