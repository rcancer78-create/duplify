using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Duplify.Utilities;

namespace Duplify.Services;

public interface IActionJournal
{
    string JournalFilePath { get; }

    void Record(string operation, string path, bool success, string message, string? extra = null);

    event Action<string>? WriteFailed;
}

public class ActionJournal : IActionJournal
{
    private static readonly object FileGate = new();

    private readonly Func<DateTime> _utcNow;
    private readonly long _maxJournalSizeBytes;
    private bool _writeFailureReported;

    public event Action<string>? WriteFailed;

    public ActionJournal()
        : this(AppInfo.JournalFilePath)
    {
    }

    public ActionJournal(string journalFilePath, Func<DateTime>? utcNow = null, long maxJournalSizeBytes = 10 * 1024 * 1024)
    {
        JournalFilePath = journalFilePath;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _maxJournalSizeBytes = Math.Max(256, maxJournalSizeBytes);
    }

    public string JournalFilePath { get; }

    public void Record(string operation, string path, bool success, string message, string? extra = null)
    {
        try
        {
            var entry = new JournalEntry
            {
                TimeUtc = _utcNow().ToUniversalTime().ToString("o"),
                Operation = operation,
                Path = path,
                Extra = extra,
                Success = success,
                Message = message
            };

            string line = JsonSerializer.Serialize(entry);

            lock (FileGate)
            {
                string? directory = Path.GetDirectoryName(JournalFilePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // Size-based rotation: keep one backup .1 if journal exceeds max size
                try
                {
                    var fileInfo = new FileInfo(JournalFilePath);
                    if (fileInfo.Exists && fileInfo.Length >= _maxJournalSizeBytes)
                    {
                        string rotatedPath = JournalFilePath + ".1";
                        if (File.Exists(rotatedPath))
                        {
                            File.Delete(rotatedPath);
                        }
                        File.Move(JournalFilePath, rotatedPath);
                    }
                }
                catch
                {
                    // Rotation is best effort; continue appending to avoid losing records
                }

                using var stream = new FileStream(JournalFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new System.IO.StreamWriter(stream, System.Text.Encoding.UTF8);
                writer.WriteLine(line);
            }
        }
        catch (Exception ex)
        {
            ReportWriteFailure(ex);
        }
    }

    private void ReportWriteFailure(Exception exception)
    {
        if (_writeFailureReported)
        {
            return;
        }

        _writeFailureReported = true;

        try
        {
            WriteFailed?.Invoke($"Не удалось записать журнал действий: {exception.Message}");
        }
        catch
        {
            // Reporting the failure must never break the operation either.
        }
    }

    private sealed class JournalEntry
    {
        [JsonPropertyName("timeUtc")]
        public string TimeUtc { get; set; } = string.Empty;

        [JsonPropertyName("operation")]
        public string Operation { get; set; } = string.Empty;

        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        [JsonPropertyName("extra")]
        public string? Extra { get; set; }

        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;
    }
}
