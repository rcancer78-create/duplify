using System;

namespace Duplify.Models;

public class AppSettings
{
    public string LastFolder { get; set; } = string.Empty;
    public string LastSecondFolder { get; set; } = string.Empty;
    public bool OnlyCrossRootDuplicates { get; set; }
    public bool IncludeSubdirectories { get; set; } = true;
    public string FileMask { get; set; } = "*.*";
    public long MinSizeKb { get; set; } = 0;
    public bool ExcludeSystemFolders { get; set; } = true;
    public string ExcludeFolderNames { get; set; } = ".git;node_modules;AppData";
}
