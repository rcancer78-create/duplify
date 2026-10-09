using System;
using System.IO;
using Duplify.Models;
using Duplify.Services;
using Xunit;

namespace Duplify.Tests;

public class SettingsServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _settingsPath;

    public SettingsServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "Duplify_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _settingsPath = Path.Combine(_tempDir, "settings.json");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch { }
    }

    [Fact]
    public void LoadSettings_WhenFileDoesNotExist_ReturnsDefaults()
    {
        var service = new SettingsService(_settingsPath);
        var settings = service.LoadSettings();

        Assert.NotNull(settings);
        Assert.Equal(string.Empty, settings.LastFolder);
        Assert.True(settings.IncludeSubdirectories);
        Assert.Equal("*.*", settings.FileMask);
    }

    [Fact]
    public void SaveAndLoadSettings_PreservesAllProperties()
    {
        var service = new SettingsService(_settingsPath);
        var original = new AppSettings
        {
            LastFolder = @"C:\Photos",
            LastSecondFolder = @"D:\Backup",
            OnlyCrossRootDuplicates = true,
            IncludeSubdirectories = false,
            FileMask = "*.jpg;*.png",
            MinSizeKb = 1024,
            ExcludeSystemFolders = true,
            ExcludeFolderNames = "temp;trash"
        };

        service.SaveSettings(original);
        Assert.True(File.Exists(_settingsPath));

        var loaded = service.LoadSettings();
        Assert.Equal(original.LastFolder, loaded.LastFolder);
        Assert.Equal(original.LastSecondFolder, loaded.LastSecondFolder);
        Assert.Equal(original.OnlyCrossRootDuplicates, loaded.OnlyCrossRootDuplicates);
        Assert.Equal(original.IncludeSubdirectories, loaded.IncludeSubdirectories);
        Assert.Equal(original.FileMask, loaded.FileMask);
        Assert.Equal(original.MinSizeKb, loaded.MinSizeKb);
        Assert.Equal(original.ExcludeSystemFolders, loaded.ExcludeSystemFolders);
        Assert.Equal(original.ExcludeFolderNames, loaded.ExcludeFolderNames);
    }

    [Fact]
    public void LoadSettings_WhenCorrupted_CreatesBadBackupAndReturnsDefaults()
    {
        File.WriteAllText(_settingsPath, "{ corrupted json !?? ");

        var service = new SettingsService(_settingsPath);
        var settings = service.LoadSettings();

        Assert.NotNull(settings);
        Assert.True(File.Exists(_settingsPath + ".bad"));
        Assert.Equal("{ corrupted json !?? ", File.ReadAllText(_settingsPath + ".bad"));
    }
}
