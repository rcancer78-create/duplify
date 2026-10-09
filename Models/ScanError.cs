using System;

namespace Duplify.Models;

public class ScanError
{
    public string FilePath { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.Now;

    public string FormattedTime => Timestamp.ToString("HH:mm:ss");
}
