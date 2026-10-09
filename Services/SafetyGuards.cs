using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Duplify.Models;

namespace Duplify.Services;

public static class SafetyGuards
{
    public static List<DuplicateGroup> FindGroupsLosingAllCopies(IEnumerable<DuplicateGroup> groups)
    {
        var result = new List<DuplicateGroup>();
        if (groups == null)
        {
            return result;
        }

        foreach (var group in groups)
        {
            if (group.Files.Count > 0 && group.Files.All(f => f.IsChecked))
            {
                result.Add(group);
            }
        }

        return result;
    }

    public static bool IsReparsePoint(FileAttributes attributes)
    {
        return (attributes & FileAttributes.ReparsePoint) != 0;
    }

    public static bool IsReparsePoint(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            return IsReparsePoint(File.GetAttributes(path));
        }
        catch
        {
            // Fail closed: if attributes cannot be read, treat the path as a reparse point
            // so the walk never silently follows it outside the chosen tree.
            return true;
        }
    }

    /// <summary>
    /// True when the destination lies inside the last successfully scanned root or inside a
    /// directory that currently holds a listed duplicate file. The current folder textbox is
    /// deliberately not consulted so editing it cannot change the warning's comparison.
    /// </summary>
    public static bool IsDestinationInsideScannedSet(string destinationFolder, string? lastScannedRoot, IEnumerable<DuplicateGroup>? listedGroups)
    {
        var roots = string.IsNullOrWhiteSpace(lastScannedRoot)
            ? Array.Empty<string>()
            : new[] { lastScannedRoot };
        return IsDestinationInsideScannedSet(destinationFolder, roots, listedGroups);
    }

    public static bool IsDestinationInsideScannedSet(string destinationFolder, IEnumerable<string>? lastScannedRoots, IEnumerable<DuplicateGroup>? listedGroups)
    {
        if (lastScannedRoots != null)
        {
            foreach (var root in lastScannedRoots)
            {
                if (!string.IsNullOrWhiteSpace(root) && IsWithinDirectory(destinationFolder, root))
                {
                    return true;
                }
            }
        }

        if (listedGroups != null)
        {
            foreach (var group in listedGroups)
            {
                foreach (var file in group.Files)
                {
                    if (!string.IsNullOrEmpty(file.DirectoryName) &&
                        IsWithinDirectory(destinationFolder, file.DirectoryName))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    public static bool IsWithinDirectory(string candidatePath, string directoryPath)
    {
        try
        {
            return IsWithinDirectoryStrict(candidatePath, directoryPath);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Same containment rule as <see cref="IsWithinDirectory"/>, but a path that cannot be
    /// normalized throws instead of being reported as "not inside". Callers that must not silently
    /// process an unnormalizable path (for example the bulk planner) use this to turn the whole
    /// affected group into an ambiguous/skipped one.
    /// </summary>
    public static bool IsWithinDirectoryStrict(string candidatePath, string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(directoryPath))
        {
            return false;
        }

        string candidate = NormalizePath(candidatePath);
        string directory = NormalizePath(directoryPath);

        if (candidate.Length == 0 || directory.Length == 0)
        {
            return false;
        }

        if (string.Equals(candidate, directory, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return candidate.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static bool IsDangerousScanRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            string full = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (full.Length == 0)
            {
                return true;
            }

            string? root = Path.GetPathRoot(full);
            if (!string.IsNullOrEmpty(root))
            {
                string normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(full, normalizedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(profile))
            {
                string normalizedProfile = Path.GetFullPath(profile)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                if (string.Equals(full, normalizedProfile, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        catch
        {
            // Fail closed: if path cannot be resolved or is invalid, treat as dangerous.
            return true;
        }
    }
}
