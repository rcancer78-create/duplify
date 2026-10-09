using System;
using System.IO;
using System.Reflection;

namespace Duplify.Utilities;

public static class AppInfo
{
    public const string Name = "Duplify";

    public static string Version
    {
        get
        {
            var assembly = typeof(AppInfo).Assembly;

            string? informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;

            if (!string.IsNullOrWhiteSpace(informational))
            {
                // .NET may append "+<commit sha>" to the informational version.
                int metadataIndex = informational.IndexOf('+');
                return metadataIndex >= 0 ? informational[..metadataIndex] : informational;
            }

            return assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        }
    }

    public static string AppDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Name);

    public static string JournalFilePath => Path.Combine(AppDataDirectory, "journal.jsonl");

    public static string LogsDirectory => Path.Combine(AppDataDirectory, "logs");

    public static string EnsureLogsDirectory()
    {
        Directory.CreateDirectory(LogsDirectory);
        return LogsDirectory;
    }
}
