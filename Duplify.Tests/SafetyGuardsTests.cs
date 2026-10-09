using System;
using System.IO;
using Duplify.Models;
using Duplify.Services;
using Xunit;

namespace Duplify.Tests;

public class SafetyGuardsTests
{
    [Fact]
    public void IsReparsePoint_FlagCheck()
    {
        Assert.True(SafetyGuards.IsReparsePoint(FileAttributes.ReparsePoint));
        Assert.False(SafetyGuards.IsReparsePoint(FileAttributes.Normal));
        Assert.False(SafetyGuards.IsReparsePoint(string.Empty));
    }

    [Fact]
    public void IsDangerousScanRoot_FailsClosed()
    {
        // Root drive should be flagged dangerous
        string systemDrive = Path.GetPathRoot(Environment.SystemDirectory)!;
        Assert.True(SafetyGuards.IsDangerousScanRoot(systemDrive));

        // User profile root should be flagged dangerous
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(userProfile))
        {
            Assert.True(SafetyGuards.IsDangerousScanRoot(userProfile));
        }

        // Empty string is not dangerous (nothing to scan)
        Assert.False(SafetyGuards.IsDangerousScanRoot(string.Empty));

        // Invalid path chars (embedded null) should fail closed (return true)
        Assert.True(SafetyGuards.IsDangerousScanRoot("C:\\bad\0path"));
    }

    [Fact]
    public void FindGroupsLosingAllCopies_DetectsLastCopyLoss()
    {
        var items = new[]
        {
            new DuplicateFileItem(@"C:\temp\copy1.txt", 100, DateTime.UtcNow),
            new DuplicateFileItem(@"C:\temp\copy2.txt", 100, DateTime.UtcNow)
        };
        var group = new DuplicateGroup("HASH1", 100, items);

        // Neither checked -> safe
        items[0].IsChecked = false;
        items[1].IsChecked = false;
        Assert.Empty(SafetyGuards.FindGroupsLosingAllCopies(new[] { group }));

        // One checked -> safe (one survives)
        items[0].IsChecked = true;
        items[1].IsChecked = false;
        Assert.Empty(SafetyGuards.FindGroupsLosingAllCopies(new[] { group }));

        // Both checked -> danger (all copies lost!)
        items[0].IsChecked = true;
        items[1].IsChecked = true;
        var danger = SafetyGuards.FindGroupsLosingAllCopies(new[] { group });
        Assert.Single(danger);
        Assert.Same(group, danger[0]);
    }

    [Fact]
    public void IsWithinDirectory_ChecksNestingCorrectly()
    {
        Assert.True(SafetyGuards.IsWithinDirectory(@"C:\Folder\Sub\file.txt", @"C:\Folder"));
        Assert.True(SafetyGuards.IsWithinDirectory(@"C:\Folder", @"C:\Folder"));
        Assert.False(SafetyGuards.IsWithinDirectory(@"C:\Other\file.txt", @"C:\Folder"));
        Assert.False(SafetyGuards.IsWithinDirectory(@"C:\Folder2\file.txt", @"C:\Folder"));
    }
}
