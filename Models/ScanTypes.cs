using System;
using System.Collections.Generic;

namespace Duplify.Models;

public class ScanProgress
{
    public int FilesScanned { get; set; }
    public string CurrentPath { get; set; } = string.Empty;
    public string Phase { get; set; } = string.Empty;
    public int HashProgressCurrent { get; set; }
    public int HashProgressTotal { get; set; }
    public long HashBytesProcessed { get; set; }
    public long HashBytesTotal { get; set; }
    public TimeSpan ElapsedTime { get; set; }
}

public class ScanOptions
{
    public string RootPath { get; set; } = string.Empty;
    public string SecondRootPath { get; set; } = string.Empty;
    public bool OnlyCrossRootDuplicates { get; set; }
    public bool IncludeSubdirectories { get; set; } = true;
    public string FileMask { get; set; } = "*.*";
    public long MinSizeKb { get; set; } = 0;
    public bool ExcludeSystemFolders { get; set; } = true;
    public List<string> ExcludeFolderNames { get; set; } = new();
}

public class ScanResult
{
    public List<DuplicateGroup> Groups { get; set; } = new();
    public List<ScanError> Errors { get; set; } = new();
    public int TotalFilesScanned { get; set; }
    public TimeSpan ElapsedTime { get; set; }
    public bool IsCancelled { get; set; }
}
