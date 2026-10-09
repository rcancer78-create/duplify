using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Duplify.Models;

namespace Duplify.Services;

public sealed class BulkSelectionOptions
{
    public string PreferredFolder { get; set; } = string.Empty;
    public bool KeepNewest { get; set; } = true;
    public bool PreferCleanName { get; set; } = true;
    public bool PreferShorterPath { get; set; } = true;
}

public sealed class BulkSelectionGroupPlan
{
    public DuplicateGroup Group { get; }
    public DuplicateFileItem KeptFile { get; }
    public IReadOnlyList<DuplicateFileItem> FilesToMark { get; }
    public int SnapshotFileCount { get; }

    internal BulkSelectionGroupPlan(DuplicateGroup group, DuplicateFileItem keptFile, IReadOnlyList<DuplicateFileItem> filesToMark)
    {
        Group = group;
        KeptFile = keptFile;
        FilesToMark = filesToMark;
        SnapshotFileCount = group.Files.Count;
    }
}

public sealed class BulkSelectionAmbiguousGroup
{
    public DuplicateGroup Group { get; }
    public IReadOnlyList<DuplicateFileItem> SnapshotFiles { get; }

    internal BulkSelectionAmbiguousGroup(DuplicateGroup group, IReadOnlyList<DuplicateFileItem> snapshotFiles)
    {
        Group = group;
        SnapshotFiles = snapshotFiles;
    }
}

/// <summary>
/// Immutable snapshot of a bulk-resolve calculation. Applying it only toggles checkboxes; it never
/// deletes or moves files. The snapshot stores the exact groups and files it was built from so a
/// stale plan can be rejected instead of applied partially.
/// </summary>
public sealed class BulkSelectionPlan
{
    public IReadOnlyList<BulkSelectionGroupPlan> ProcessedGroups { get; }
    public IReadOnlyList<BulkSelectionAmbiguousGroup> AmbiguousGroups { get; }
    public IReadOnlyList<DuplicateFileItem> FilesToMark { get; }
    public long MarkedBytes { get; }
    public int ChangedCount { get; }
    public string PriorityDescription { get; }

    public int ProcessedGroupCount => ProcessedGroups.Count;
    public int AmbiguousGroupCount => AmbiguousGroups.Count;
    public int MarkedFileCount => FilesToMark.Count;

    internal BulkSelectionPlan(
        IReadOnlyList<BulkSelectionGroupPlan> processedGroups,
        IReadOnlyList<BulkSelectionAmbiguousGroup> ambiguousGroups,
        IReadOnlyList<DuplicateFileItem> filesToMark,
        long markedBytes,
        int changedCount,
        string priorityDescription)
    {
        ProcessedGroups = processedGroups;
        AmbiguousGroups = ambiguousGroups;
        FilesToMark = filesToMark;
        MarkedBytes = markedBytes;
        ChangedCount = changedCount;
        PriorityDescription = priorityDescription;
    }
}

/// <summary>
/// Pure, UI-free bulk selection logic: for every candidate group it ranks files by the enabled
/// criteria and marks every copy except exactly one kept file. A group whose best rank is shared by
/// two or more files is ambiguous and is left entirely unmarked. No hidden alphabetical or
/// enumeration-order tie-breaker is used.
/// </summary>
public static class BulkSelectionPlanner
{
    private static readonly string[] CopyMarkerWords = { "copy", "копия", "duplicate", "дубликат" };

    public static BulkSelectionPlan Build(IEnumerable<DuplicateGroup> groups, BulkSelectionOptions options)
    {
        options ??= new BulkSelectionOptions();

        bool preferredActive = !string.IsNullOrWhiteSpace(options.PreferredFolder)
            && SafeDirectoryExists(options.PreferredFolder);
        string? preferredFolder = preferredActive ? options.PreferredFolder : null;

        var processed = new List<BulkSelectionGroupPlan>();
        var ambiguous = new List<BulkSelectionAmbiguousGroup>();
        var filesToMark = new List<DuplicateFileItem>();

        foreach (var group in groups ?? Enumerable.Empty<DuplicateGroup>())
        {
            if (group == null || group.Files.Count < 2)
            {
                continue;
            }

            var snapshot = group.Files.ToList();

            try
            {
                var ranked = snapshot
                    .Select(file => (File: file, Rank: ComputeRank(file, preferredFolder, options.PreferCleanName, options.PreferShorterPath, options.KeepNewest)))
                    .ToList();

                var bestRank = ranked[0].Rank;
                for (int i = 1; i < ranked.Count; i++)
                {
                    if (ranked[i].Rank.CompareTo(bestRank) < 0)
                    {
                        bestRank = ranked[i].Rank;
                    }
                }

                int bestCount = ranked.Count(r => r.Rank.Equals(bestRank));
                if (bestCount >= 2)
                {
                    // Ambiguous: never guess. Clear every mark in the group.
                    ambiguous.Add(new BulkSelectionAmbiguousGroup(group, snapshot));
                    continue;
                }

                var kept = ranked.First(r => r.Rank.Equals(bestRank)).File;
                var marks = snapshot.Where(f => !ReferenceEquals(f, kept)).ToList();

                processed.Add(new BulkSelectionGroupPlan(group, kept, marks));
                filesToMark.AddRange(marks);
            }
            catch
            {
                // A single unreadable/unnormalizable path must not fail the whole run: skip the group.
                ambiguous.Add(new BulkSelectionAmbiguousGroup(group, snapshot));
            }
        }

        long markedBytes = filesToMark.Sum(f => f.Size);
        int changedCount = CountChanges(processed, ambiguous);

        return new BulkSelectionPlan(
            processed,
            ambiguous,
            filesToMark,
            markedBytes,
            changedCount,
            DescribePriority(options, preferredActive));
    }

    /// <summary>
    /// Verifies that every group and file captured by the plan is still present and unchanged. The
    /// plan is only safe to apply when this returns true.
    /// </summary>
    public static bool IsStillValid(BulkSelectionPlan? plan, IEnumerable<DuplicateGroup>? currentGroups)
    {
        if (plan == null || currentGroups == null)
        {
            return false;
        }

        var current = new HashSet<DuplicateGroup>(currentGroups);

        foreach (var groupPlan in plan.ProcessedGroups)
        {
            if (!current.Contains(groupPlan.Group) ||
                groupPlan.Group.Files.Count != groupPlan.SnapshotFileCount ||
                !groupPlan.Group.Files.Contains(groupPlan.KeptFile))
            {
                return false;
            }

            foreach (var file in groupPlan.FilesToMark)
            {
                if (!groupPlan.Group.Files.Contains(file))
                {
                    return false;
                }
            }
        }

        foreach (var groupPlan in plan.AmbiguousGroups)
        {
            if (!current.Contains(groupPlan.Group) ||
                groupPlan.Group.Files.Count != groupPlan.SnapshotFiles.Count)
            {
                return false;
            }

            foreach (var file in groupPlan.SnapshotFiles)
            {
                if (!groupPlan.Group.Files.Contains(file))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Validates the snapshot against the current groups and, only if nothing changed, applies the
    /// checkbox changes. A stale plan changes nothing and returns false.
    /// </summary>
    public static bool TryApply(BulkSelectionPlan? plan, IEnumerable<DuplicateGroup>? currentGroups)
    {
        if (!IsStillValid(plan, currentGroups))
        {
            return false;
        }

        Apply(plan!);
        return true;
    }

    public static void Apply(BulkSelectionPlan plan)
    {
        if (plan == null)
        {
            return;
        }

        foreach (var groupPlan in plan.ProcessedGroups)
        {
            groupPlan.KeptFile.IsChecked = false;
            foreach (var file in groupPlan.FilesToMark)
            {
                file.IsChecked = true;
            }
        }

        foreach (var groupPlan in plan.AmbiguousGroups)
        {
            foreach (var file in groupPlan.SnapshotFiles)
            {
                file.IsChecked = false;
            }
        }
    }

    private static int CountChanges(
        IReadOnlyList<BulkSelectionGroupPlan> processed,
        IReadOnlyList<BulkSelectionAmbiguousGroup> ambiguous)
    {
        int changed = 0;

        foreach (var groupPlan in processed)
        {
            if (groupPlan.KeptFile.IsChecked)
            {
                changed++;
            }

            changed += groupPlan.FilesToMark.Count(f => !f.IsChecked);
        }

        foreach (var groupPlan in ambiguous)
        {
            changed += groupPlan.SnapshotFiles.Count(f => f.IsChecked);
        }

        return changed;
    }

    private static (int InPreferred, int NamePenalty, int Segments, int Length, long DateRank) ComputeRank(
        DuplicateFileItem file,
        string? preferredFolder,
        bool preferCleanName,
        bool preferShorterPath,
        bool keepNewest)
    {
        // The full path must be normalized for containment and length rules. If it cannot be
        // normalized we deliberately let the exception escape so the caller marks the whole group
        // ambiguous instead of silently ranking the file as "outside the preferred folder".
        string fullPath = Path.GetFullPath(file.FilePath);

        int inPreferred = 0;
        if (preferredFolder != null && !SafetyGuards.IsWithinDirectoryStrict(fullPath, preferredFolder))
        {
            inPreferred = 1;
        }

        int namePenalty = preferCleanName ? ComputeNamePenalty(file.FileName) : 0;

        int segments = 0;
        int length = 0;
        if (preferShorterPath)
        {
            segments = CountSegments(fullPath);
            length = fullPath.Length;
        }

        long dateRank = keepNewest ? -file.LastModifiedUtc.Ticks : file.LastModifiedUtc.Ticks;

        return (inPreferred, namePenalty, segments, length, dateRank);
    }

    private static int ComputeNamePenalty(string fileName)
    {
        string baseName = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrEmpty(baseName))
        {
            return 0;
        }

        var tokens = baseName.Split(
            new[] { ' ', '_', '-', '.', '(', ')', '[', ']', ',', '+' },
            StringSplitOptions.RemoveEmptyEntries);

        foreach (var token in tokens)
        {
            foreach (var marker in CopyMarkerWords)
            {
                if (string.Equals(token, marker, StringComparison.OrdinalIgnoreCase))
                {
                    return 1;
                }
            }
        }

        return EndsWithNumericCopySuffix(baseName) ? 1 : 0;
    }

    private static bool EndsWithNumericCopySuffix(string baseName)
    {
        string trimmed = baseName.TrimEnd();
        if (!trimmed.EndsWith(")", StringComparison.Ordinal))
        {
            return false;
        }

        int open = trimmed.LastIndexOf('(');
        if (open < 0)
        {
            return false;
        }

        string inner = trimmed.Substring(open + 1, trimmed.Length - open - 2).Trim();
        return inner.Length > 0 && inner.All(char.IsDigit);
    }

    private static int CountSegments(string fullPath)
    {
        int count = 0;
        for (int i = 0; i < fullPath.Length; i++)
        {
            if (fullPath[i] == Path.DirectorySeparatorChar || fullPath[i] == Path.AltDirectorySeparatorChar)
            {
                count++;
            }
        }

        return count + 1;
    }

    private static bool SafeDirectoryExists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    private static string DescribePriority(BulkSelectionOptions options, bool preferredActive)
    {
        var parts = new List<string>();

        if (preferredActive)
        {
            parts.Add("предпочтительная папка");
        }

        if (options.PreferCleanName)
        {
            parts.Add("понятное имя без «копия / copy / (2)»");
        }

        if (options.PreferShorterPath)
        {
            parts.Add("более короткий путь");
        }

        parts.Add(options.KeepNewest ? "самый новый файл" : "самый старый файл");

        return "Приоритет: " + string.Join(" → ", parts) + ". Неоднозначные группы пропускаются.";
    }
}
