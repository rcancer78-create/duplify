using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Duplify.Models;
using Duplify.Services;
using Xunit;

namespace Duplify.Tests;

public class BulkSelectionPlannerTests : IDisposable
{
    private readonly string _tempDir;

    public BulkSelectionPlannerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "Duplify_BulkTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
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
    public void Plan_PreferredFolder_KeepsCopyInPreferredFolder()
    {
        string keepFolder = Path.Combine(_tempDir, "KeepMe");
        string deleteFolder = Path.Combine(_tempDir, "DeleteMe");
        Directory.CreateDirectory(keepFolder);
        Directory.CreateDirectory(deleteFolder);

        DateTime sameTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var item1 = new DuplicateFileItem(Path.Combine(keepFolder, "photo.jpg"), 1000, sameTime);
        var item2 = new DuplicateFileItem(Path.Combine(deleteFolder, "photo.jpg"), 1000, sameTime);
        var group = new DuplicateGroup("HASH", 1000, new[] { item1, item2 });

        var options = new BulkSelectionOptions
        {
            PreferredFolder = keepFolder,
            PreferCleanName = false,
            PreferShorterPath = false,
            KeepNewest = true
        };

        var plan = BulkSelectionPlanner.Build(new[] { group }, options);

        Assert.Equal(1, plan.ProcessedGroupCount);
        Assert.Empty(plan.AmbiguousGroups);
        BulkSelectionPlanner.Apply(plan);

        Assert.False(item1.IsChecked);
        Assert.True(item2.IsChecked);
    }

    [Fact]
    public void Plan_AmbiguousTie_LeavesGroupUnchecked()
    {
        string folder1 = Path.Combine(_tempDir, "Folder1");
        string folder2 = Path.Combine(_tempDir, "Folder2");
        Directory.CreateDirectory(folder1);
        Directory.CreateDirectory(folder2);

        DateTime sameTime = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var item1 = new DuplicateFileItem(Path.Combine(folder1, "doc.txt"), 1000, sameTime);
        var item2 = new DuplicateFileItem(Path.Combine(folder2, "doc.txt"), 1000, sameTime);
        var group = new DuplicateGroup("HASH", 1000, new[] { item1, item2 });

        var options = new BulkSelectionOptions
        {
            PreferredFolder = string.Empty,
            PreferCleanName = false,
            PreferShorterPath = false,
            KeepNewest = true
        };

        var plan = BulkSelectionPlanner.Build(new[] { group }, options);

        Assert.Equal(1, plan.AmbiguousGroupCount);
        BulkSelectionPlanner.Apply(plan);

        Assert.False(item1.IsChecked);
        Assert.False(item2.IsChecked);
    }

    [Fact]
    public void Plan_CleanName_PrefersOriginalOverCopy()
    {
        string docsFolder = Path.Combine(_tempDir, "Docs");
        Directory.CreateDirectory(docsFolder);

        DateTime sameTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var itemOriginal = new DuplicateFileItem(Path.Combine(docsFolder, "Report.pdf"), 500, sameTime);
        var itemCopy = new DuplicateFileItem(Path.Combine(docsFolder, "Report - Copy (1).pdf"), 500, sameTime);
        var group = new DuplicateGroup("HASH", 500, new[] { itemOriginal, itemCopy });

        var options = new BulkSelectionOptions
        {
            PreferredFolder = string.Empty,
            PreferCleanName = true,
            PreferShorterPath = false,
            KeepNewest = true
        };

        var plan = BulkSelectionPlanner.Build(new[] { group }, options);

        Assert.Equal(1, plan.ProcessedGroupCount);
        BulkSelectionPlanner.Apply(plan);

        Assert.False(itemOriginal.IsChecked);
        Assert.True(itemCopy.IsChecked);
    }
}
