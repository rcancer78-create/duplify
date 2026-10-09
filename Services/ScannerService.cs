using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Duplify.Models;

namespace Duplify.Services;

public interface IScannerService
{
    Task<ScanResult> ScanAsync(ScanOptions options, IProgress<ScanProgress>? progress, CancellationToken cancellationToken);
}

public class ScannerService : IScannerService
{
    private static readonly HashSet<string> SystemFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "$Recycle.Bin",
        "System Volume Information",
        "Windows",
        "Program Files",
        "Program Files (x86)"
    };

    // Report mid-file hash progress at least every 256 KiB so large files visibly move the bar.
    private const long HashReportByteInterval = 256 * 1024;

    private sealed record FileEntry(string Path, long Size, DateTime LastModifiedUtc);

    private readonly struct DirEntryInfo
    {
        public readonly string FullPath;
        public readonly string FileName;
        public readonly bool IsDirectory;
        public readonly FileAttributes Attributes;
        public readonly long Length;
        public readonly DateTime LastWriteTimeUtc;

        public DirEntryInfo(string fullPath, string fileName, bool isDirectory, FileAttributes attributes, long length, DateTime lastWriteTimeUtc)
        {
            FullPath = fullPath;
            FileName = fileName;
            IsDirectory = isDirectory;
            Attributes = attributes;
            Length = length;
            LastWriteTimeUtc = lastWriteTimeUtc;
        }
    }

    public Task<ScanResult> ScanAsync(ScanOptions options, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        // Enumeration, identity checks, and hashing must not run on the UI thread.
        return Task.Run(() => ExecuteScan(options, progress, cancellationToken), cancellationToken);
    }

    private ScanResult ExecuteScan(ScanOptions options, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        var errors = new List<ScanError>();
        var stopwatch = Stopwatch.StartNew();
        int totalFilesScanned = 0;

        if (string.IsNullOrWhiteSpace(options.RootPath) || !Directory.Exists(options.RootPath))
        {
            return new ScanResult
            {
                TotalFilesScanned = 0,
                ElapsedTime = stopwatch.Elapsed,
                IsCancelled = false
            };
        }

        var scanRoots = ResolveScanRoots(options);

        string[] masks = ParseMasks(options.FileMask);
        long minSizeBytes = Math.Max(0, options.MinSizeKb * 1024);
        HashSet<string> excludedFolderNames = BuildExcludedFolderNames(options);

        var sizeMap = new Dictionary<long, List<FileEntry>>();
        // Hard links of one physical file share the same (volume serial, file index) identity.
        // Keep only the first path seen for an identity so extra names are never hashed or grouped.
        var seenFileIdentities = new HashSet<FileIdentity>();
        var dirQueue = new Queue<string>();
        foreach (var root in scanRoots)
        {
            dirQueue.Enqueue(root);
        }

        DateTime lastProgressReportTime = DateTime.UtcNow;

        // Phase 1: Directory traversal & file gathering
        try
        {
            while (dirQueue.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string currentDir = dirQueue.Dequeue();

                // Check directory exclusion
                if (IsExcludedFolder(currentDir, scanRoots, excludedFolderNames))
                {
                    continue;
                }

                try
                {
                    var entries = new FileSystemEnumerable<DirEntryInfo>(
                        currentDir,
                        (ref FileSystemEntry entry) => new DirEntryInfo(
                            entry.ToSpecifiedFullPath(),
                            entry.FileName.ToString(),
                            entry.IsDirectory,
                            entry.Attributes,
                            entry.Length,
                            entry.LastWriteTimeUtc.UtcDateTime),
                        new EnumerationOptions
                        {
                            RecurseSubdirectories = false,
                            IgnoreInaccessible = true,
                            AttributesToSkip = 0
                        });

                    foreach (var entry in entries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        if (entry.IsDirectory)
                        {
                            if (options.IncludeSubdirectories)
                            {
                                if (IsExcludedFolder(entry.FullPath, scanRoots, excludedFolderNames))
                                {
                                    continue;
                                }

                                if (SafetyGuards.IsReparsePoint(entry.Attributes))
                                {
                                    continue;
                                }

                                dirQueue.Enqueue(entry.FullPath);
                            }
                            continue;
                        }

                        // File processing
                        totalFilesScanned++;

                        if (SafetyGuards.IsReparsePoint(entry.Attributes))
                        {
                            continue;
                        }

                        if (!MatchesMask(entry.FileName, masks))
                        {
                            continue;
                        }

                        if (entry.Length == 0 || entry.Length < minSizeBytes)
                        {
                            continue;
                        }

                        // Skip additional hard-link names of a file we already gathered.
                        if (FileIdentityProvider.TryGetIdentity(entry.FullPath, out var identity)
                            && !seenFileIdentities.Add(identity))
                        {
                            continue;
                        }

                        if (!sizeMap.TryGetValue(entry.Length, out var list))
                        {
                            list = new List<FileEntry>();
                            sizeMap[entry.Length] = list;
                        }

                        list.Add(new FileEntry(entry.FullPath, entry.Length, entry.LastWriteTimeUtc));

                        // Report progress periodically
                        if ((DateTime.UtcNow - lastProgressReportTime).TotalMilliseconds >= 70)
                        {
                            lastProgressReportTime = DateTime.UtcNow;
                            progress?.Report(new ScanProgress
                            {
                                FilesScanned = totalFilesScanned,
                                CurrentPath = entry.FullPath,
                                Phase = "Поиск файлов...",
                                ElapsedTime = stopwatch.Elapsed
                            });
                        }
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
                {
                    errors.Add(new ScanError
                    {
                        FilePath = currentDir,
                        Message = $"Не удалось прочитать папку: {ex.Message}",
                        Timestamp = DateTime.Now
                    });
                }
            }
        }
        catch (OperationCanceledException)
        {
            return new ScanResult
            {
                TotalFilesScanned = totalFilesScanned,
                ElapsedTime = stopwatch.Elapsed,
                Errors = errors,
                IsCancelled = true
            };
        }

        // Phase 2: Hashing files with size collisions
        // Do not hash files until size-collisions exist
        var candidatesBySize = new List<KeyValuePair<long, List<FileEntry>>>();
        int totalFilesToHash = 0;
        long totalBytesToHash = 0;

        foreach (var kvp in sizeMap)
        {
            if (kvp.Value.Count > 1)
            {
                candidatesBySize.Add(kvp);
                totalFilesToHash += kvp.Value.Count;
                totalBytesToHash += kvp.Key * kvp.Value.Count;
            }
        }

        var duplicateGroups = new List<DuplicateGroup>();
        int hashedFilesCount = 0;
        long hashedBytes = 0;
        long bytesSinceLastHashReport = 0;

        if (candidatesBySize.Count > 0)
        {
            try
            {
                foreach (var (size, entries) in candidatesBySize)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var hashMap = new Dictionary<string, List<DuplicateFileItem>>(StringComparer.OrdinalIgnoreCase);

                    foreach (var entry in entries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        hashedFilesCount++;

                        if ((DateTime.UtcNow - lastProgressReportTime).TotalMilliseconds >= 70)
                        {
                            lastProgressReportTime = DateTime.UtcNow;
                            progress?.Report(new ScanProgress
                            {
                                FilesScanned = totalFilesScanned,
                                CurrentPath = entry.Path,
                                Phase = "Вычисление контрольных сумм...",
                                HashProgressCurrent = hashedFilesCount,
                                HashProgressTotal = totalFilesToHash,
                                HashBytesProcessed = hashedBytes,
                                HashBytesTotal = totalBytesToHash,
                                ElapsedTime = stopwatch.Elapsed
                            });
                        }

                        string? hash = ComputeFileHash(entry.Path, cancellationToken, bytesRead =>
                        {
                            hashedBytes += bytesRead;
                            bytesSinceLastHashReport += bytesRead;

                            if (bytesSinceLastHashReport >= HashReportByteInterval ||
                                (DateTime.UtcNow - lastProgressReportTime).TotalMilliseconds >= 70)
                            {
                                bytesSinceLastHashReport = 0;
                                lastProgressReportTime = DateTime.UtcNow;
                                progress?.Report(new ScanProgress
                                {
                                    FilesScanned = totalFilesScanned,
                                    CurrentPath = entry.Path,
                                    Phase = "Вычисление контрольных сумм...",
                                    HashProgressCurrent = hashedFilesCount,
                                    HashProgressTotal = totalFilesToHash,
                                    HashBytesProcessed = hashedBytes,
                                    HashBytesTotal = totalBytesToHash,
                                    ElapsedTime = stopwatch.Elapsed
                                });
                            }
                        });

                        if (hash == null)
                        {
                            errors.Add(new ScanError
                            {
                                FilePath = entry.Path,
                                Message = "Не удалось прочитать файл для вычисления контрольной суммы",
                                Timestamp = DateTime.Now
                            });
                            continue;
                        }

                        var item = new DuplicateFileItem(entry.Path, entry.Size, entry.LastModifiedUtc);

                        if (!hashMap.TryGetValue(hash, out var hashList))
                        {
                            hashList = new List<DuplicateFileItem>();
                            hashMap[hash] = hashList;
                        }

                        hashList.Add(item);
                    }

                    foreach (var (hash, items) in hashMap)
                    {
                        if (items.Count > 1)
                        {
                            duplicateGroups.Add(new DuplicateGroup(hash, size, items));
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return new ScanResult
                {
                    TotalFilesScanned = totalFilesScanned,
                    ElapsedTime = stopwatch.Elapsed,
                    Errors = errors,
                    IsCancelled = true
                };
            }
        }

        stopwatch.Stop();

        // Sort groups by wasted space descending
        duplicateGroups.Sort((a, b) => b.WastedSpace.CompareTo(a.WastedSpace));

        if (options.OnlyCrossRootDuplicates && scanRoots.Count >= 2)
        {
            duplicateGroups.RemoveAll(g => CountRootsPresent(g, scanRoots) < 2);
        }

        // Final progress report.
        // Report the bytes actually read, never the pre-computed workload: a file that failed
        // to read (or changed size mid-read) must not make the final counter claim 100%.
        progress?.Report(new ScanProgress
        {
            FilesScanned = totalFilesScanned,
            CurrentPath = "Сканирование завершено",
            Phase = "Готово",
            HashProgressCurrent = hashedFilesCount,
            HashProgressTotal = totalFilesToHash,
            HashBytesProcessed = hashedBytes,
            HashBytesTotal = totalBytesToHash,
            ElapsedTime = stopwatch.Elapsed
        });

        return new ScanResult
        {
            Groups = duplicateGroups,
            Errors = errors,
            TotalFilesScanned = totalFilesScanned,
            ElapsedTime = stopwatch.Elapsed,
            IsCancelled = false
        };
    }

    private static HashSet<string> BuildExcludedFolderNames(ScanOptions options)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (options.ExcludeSystemFolders)
        {
            names.UnionWith(SystemFolderNames);
        }

        if (options.ExcludeFolderNames != null)
        {
            foreach (var rawName in options.ExcludeFolderNames)
            {
                if (!string.IsNullOrWhiteSpace(rawName))
                {
                    names.Add(rawName.Trim());
                }
            }
        }

        return names;
    }

    private static List<string> ResolveScanRoots(ScanOptions options)
    {
        var roots = new List<string>();
        AddScanRoot(roots, options.RootPath);
        AddScanRoot(roots, options.SecondRootPath);
        return roots;
    }

    private static void AddScanRoot(List<string> roots, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        string full = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var existing in roots)
        {
            if (string.Equals(existing, full, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        roots.Add(full);
    }

    private static int CountRootsPresent(DuplicateGroup group, List<string> scanRoots)
    {
        int count = 0;
        foreach (var root in scanRoots)
        {
            if (group.Files.Any(f => SafetyGuards.IsWithinDirectory(f.FilePath, root)))
            {
                count++;
            }
        }

        return count;
    }

    private static bool IsExcludedFolder(string path, List<string> scanRoots, HashSet<string> excludedNames)
    {
        if (excludedNames.Count == 0)
        {
            return false;
        }

        string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Never exclude a root folder the user explicitly selected.
        foreach (var root in scanRoots)
        {
            if (string.Equals(trimmed, root, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        string name = Path.GetFileName(trimmed);
        return !string.IsNullOrEmpty(name) && excludedNames.Contains(name);
    }

    private static string? ComputeFileHash(string filePath, CancellationToken cancellationToken, Action<long>? onBytesRead)
    {
        try
        {
            // FileShare.ReadWrite lets us hash files another process still has open for writing.
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 65536, FileOptions.SequentialScan);
            using var sha256 = SHA256.Create();

            byte[] buffer = new byte[65536];
            int bytesRead;
            while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                sha256.TransformBlock(buffer, 0, bytesRead, null, 0);
                onBytesRead?.Invoke(bytesRead);
            }

            sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            byte[] hashBytes = sha256.Hash ?? Array.Empty<byte>();
            return Convert.ToHexString(hashBytes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static string[] ParseMasks(string maskText)
    {
        if (string.IsNullOrWhiteSpace(maskText))
        {
            return new[] { "*.*" };
        }

        var parts = maskText.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? new[] { "*.*" } : parts;
    }

    private static bool MatchesMask(string fileName, string[] masks)
    {
        foreach (var mask in masks)
        {
            if (mask == "*.*" || mask == "*" || string.IsNullOrWhiteSpace(mask))
            {
                return true;
            }

            try
            {
                if (mask.StartsWith("*.") && mask.IndexOf('*', 2) == -1 && mask.IndexOf('?', 2) == -1)
                {
                    string ext = mask[1..];
                    if (fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                else if (FileSystemName.MatchesSimpleExpression(mask, fileName, ignoreCase: true))
                {
                    return true;
                }
            }
            catch
            {
                try
                {
                    string pattern = "^" + Regex.Escape(mask).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
                    if (Regex.IsMatch(fileName, pattern, RegexOptions.IgnoreCase))
                    {
                        return true;
                    }
                }
                catch
                {
                    // Ignore pattern syntax error
                }
            }
        }

        return false;
    }
}
