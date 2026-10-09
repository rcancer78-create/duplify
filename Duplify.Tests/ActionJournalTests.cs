using System;
using System.IO;
using Duplify.Services;
using Xunit;

namespace Duplify.Tests;

public class ActionJournalTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _journalPath;

    public ActionJournalTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "Duplify_JournalTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _journalPath = Path.Combine(_tempDir, "journal.jsonl");
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
    public void Record_AppendsJsonlEntries()
    {
        var journal = new ActionJournal(_journalPath);
        journal.Record("delete_recycle", @"C:\file1.txt", true, "Success");
        journal.Record("move", @"C:\file2.txt", true, "Moved", @"D:\file2.txt");

        Assert.True(File.Exists(_journalPath));
        string[] lines = File.ReadAllLines(_journalPath);
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"operation\":\"delete_recycle\"", lines[0]);
        Assert.Contains("\"operation\":\"move\"", lines[1]);
    }

    [Fact]
    public void Record_WhenSizeExceeded_RotatesFile()
    {
        // Journal with small 1 KB limit to test rotation
        var journal = new ActionJournal(_journalPath, maxJournalSizeBytes: 1024);

        // Write enough records to trigger rotation
        for (int i = 0; i < 30; i++)
        {
            journal.Record("move", $@"C:\data\file_{i}.txt", true, "Moved successfully to destination");
        }

        // Rotated file .1 should exist
        Assert.True(File.Exists(_journalPath + ".1"));
        Assert.True(File.Exists(_journalPath));
    }

    [Fact]
    public void Record_WhenWriteBlocked_FiresWriteFailedEvent()
    {
        string blockedFile = Path.Combine(_tempDir, "blocked_dir");
        File.WriteAllText(blockedFile, "not a directory");
        string invalidJournalPath = Path.Combine(blockedFile, "journal.jsonl");

        var journal = new ActionJournal(invalidJournalPath);
        int failureSignals = 0;
        journal.WriteFailed += _ => failureSignals++;

        journal.Record("delete", @"C:\f.txt", true, "ok");
        journal.Record("delete", @"C:\g.txt", true, "ok");

        Assert.Equal(1, failureSignals); // Fires once per session
    }
}
