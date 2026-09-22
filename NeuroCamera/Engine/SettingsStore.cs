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
///
/// Saving writes to a temporary file and then atomically moves it over the real one, so a
/// crash or power loss in the middle of a save can no longer leave a truncated settings.json
/// (which would silently reset every preference, including the saved calibration).
/// </summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private static readonly object SaveLock = new();

    private static string DefaultFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NeuroCamera", "settings.json");

    public static AppSettings Load() => Load(DefaultFilePath);

    public static void Save(AppSettings settings) => Save(settings, DefaultFilePath);

    internal static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
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

    internal static void Save(AppSettings settings, string path)
    {
        lock (SaveLock)
        {
            string tempPath = path + ".tmp";

            try
            {
                string? directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                string json = JsonSerializer.Serialize(settings, SerializerOptions);
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort - failing to save preferences should never interrupt the user.
                TryDelete(tempPath);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Nothing more to do.
        }
    }
}
