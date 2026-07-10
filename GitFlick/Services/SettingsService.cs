using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using GitFlick.Models;

namespace GitFlick.Services;

public sealed class SettingsService : ISettingsService
{
    public SettingsService()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GitFlick");

        FilePath = Path.Combine(directory, "settings.json");
    }

    public AppSettings Current { get; private set; } = new();

    public string FilePath { get; }

    /// <summary>
    /// Never throws. A missing or corrupt file yields defaults, which are then written back.
    /// Settings must not be able to take the app down on startup.
    /// </summary>
    public void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings);

                if (loaded is not null)
                {
                    Current = loaded;
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Trace.TraceWarning($"Failed to read settings from '{FilePath}': {ex.Message}. Using defaults.");
        }

        Current = new AppSettings();
        Save();
    }

    /// <summary>Writes via a temp file so a crash mid-write can't leave a truncated settings file.</summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

            var json = JsonSerializer.Serialize(Current, SettingsJsonContext.Default.AppSettings);
            var temp = FilePath + ".tmp";

            File.WriteAllText(temp, json);
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Failed to write settings to '{FilePath}': {ex.Message}.");
        }
    }
}
