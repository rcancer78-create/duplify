using System;
using System.IO;
using System.Text.Json;
using Duplify.Models;
using Duplify.Utilities;

namespace Duplify.Services;

public interface ISettingsService
{
    AppSettings LoadSettings();
    void SaveSettings(AppSettings settings);
}

public class SettingsService : ISettingsService
{
    private readonly string _settingsFilePath;

    public SettingsService(string settingsFilePath)
    {
        _settingsFilePath = settingsFilePath;
    }

    public SettingsService()
    {
        _settingsFilePath = Path.Combine(AppInfo.AppDataDirectory, "settings.json");
    }

    public AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                string json = File.ReadAllText(_settingsFilePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings != null)
                {
                    return settings;
                }

                BackupBadSettings();
            }
        }
        catch
        {
            BackupBadSettings();
        }

        return new AppSettings();
    }

    private void BackupBadSettings()
    {
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                File.Copy(_settingsFilePath, _settingsFilePath + ".bad", true);
            }
        }
        catch
        {
            // Backup is best effort only.
        }
    }

    public void SaveSettings(AppSettings settings)
    {
        string? tempPath = null;
        try
        {
            string? dir = Path.GetDirectoryName(_settingsFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(settings, options);
            // Write to a temp file first so a crash mid-write never corrupts the settings.
            tempPath = _settingsFilePath + ".tmp";
            File.WriteAllText(tempPath, json);
            if (File.Exists(_settingsFilePath))
            {
                try
                {
                    File.Replace(tempPath, _settingsFilePath, null, true);
                }
                catch
                {
                    File.Delete(_settingsFilePath);
                    File.Move(tempPath, _settingsFilePath);
                }
            }
            else
            {
                File.Move(tempPath, _settingsFilePath);
            }
        }
        catch
        {
            // Ignore failure to save settings (e.g. disk read-only or permissions)
            if (tempPath != null && File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }
}
