using System;
using System.IO;
using Duplify.Services;
using Xunit;

namespace Duplify.Tests;

public class FileActionServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileActionService _service;

    public FileActionServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "Duplify_ActionTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _service = new FileActionService();
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
    public void MoveFile_ResolvesNamingCollisionsWithNumbering()
    {
        string src1 = Path.Combine(_tempDir, "test.txt");
        File.WriteAllText(src1, "Hello 1");

        string destDir = Path.Combine(_tempDir, "Target");
        Directory.CreateDirectory(destDir);

        // Move first copy
        bool ok1 = _service.MoveFile(src1, destDir, out string final1, out string? err1);
        Assert.True(ok1);
        Assert.Null(err1);
        Assert.Equal("test.txt", Path.GetFileName(final1));
        Assert.True(File.Exists(final1));

        // Create second copy and move
        string src2 = Path.Combine(_tempDir, "test.txt");
        File.WriteAllText(src2, "Hello 2");
        bool ok2 = _service.MoveFile(src2, destDir, out string final2, out string? err2);
        Assert.True(ok2);
        Assert.Null(err2);
        Assert.Equal("test (2).txt", Path.GetFileName(final2));
        Assert.True(File.Exists(final2));

        // Create third copy and move
        string src3 = Path.Combine(_tempDir, "test.txt");
        File.WriteAllText(src3, "Hello 3");
        bool ok3 = _service.MoveFile(src3, destDir, out string final3, out string? err3);
        Assert.True(ok3);
        Assert.Null(err3);
        Assert.Equal("test (3).txt", Path.GetFileName(final3));
        Assert.True(File.Exists(final3));
    }

    [Fact]
    public void MoveFile_WhenSourceDoesNotExist_ReturnsFalse()
    {
        string nonExistent = Path.Combine(_tempDir, "does_not_exist.txt");
        string destDir = Path.Combine(_tempDir, "Target");

        bool ok = _service.MoveFile(nonExistent, destDir, out _, out string? err);
        Assert.False(ok);
        Assert.NotNull(err);
    }
}
