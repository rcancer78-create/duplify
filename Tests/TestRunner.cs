using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using Duplify.Models;
using Duplify.Services;
using Duplify.Utilities;
using Duplify.ViewModels;

namespace Duplify.Tests;

public static class TestRunner
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newLinkPath, string existingFilePath, IntPtr securityAttributes);

    public static async Task<int> RunTestsAsync()
    {
        Console.WriteLine("=== Duplify Self-Test Suite ===");
        string tempDir = Path.Combine(Path.GetTempPath(), "Duplify_Test_" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(tempDir);
            Console.WriteLine($"Created temp test directory: {tempDir}");

            // 1. Test Settings
            Console.Write("Testing SettingsService... ");
            var settingsService = new SettingsService(Path.Combine(tempDir, "settings.json"));
            var originalSettings = new AppSettings
            {
                LastFolder = @"C:\TestFolder",
                IncludeSubdirectories = false,
                FileMask = "*.jpg;*.png",
                MinSizeKb = 42,
                ExcludeSystemFolders = true,
                ExcludeFolderNames = "custom_a;custom_b"
            };
            settingsService.SaveSettings(originalSettings);
            var loadedSettings = settingsService.LoadSettings();

            if (loadedSettings.LastFolder != originalSettings.LastFolder ||
                loadedSettings.IncludeSubdirectories != originalSettings.IncludeSubdirectories ||
                loadedSettings.FileMask != originalSettings.FileMask ||
                loadedSettings.MinSizeKb != originalSettings.MinSizeKb ||
                loadedSettings.ExcludeSystemFolders != originalSettings.ExcludeSystemFolders ||
                loadedSettings.ExcludeFolderNames != originalSettings.ExcludeFolderNames)
            {
                throw new Exception("Settings load did not match saved settings!");
            }
            Console.WriteLine("PASSED");

            // 2. Setup Files for Scanner Test
            Console.Write("Setting up test files... ");
            byte[] contentX = new byte[100];
            Array.Fill(contentX, (byte)'X');

            byte[] contentY = new byte[100]; // Same size 100, different content
            Array.Fill(contentY, (byte)'Y');

            byte[] contentZ = new byte[250]; // Unique size
            Array.Fill(contentZ, (byte)'Z');

            string fileA = Path.Combine(tempDir, "fileA.txt");
            string fileB = Path.Combine(tempDir, "fileB.txt");
            string subDir = Path.Combine(tempDir, "subfolder");
            Directory.CreateDirectory(subDir);
            string fileC = Path.Combine(subDir, "fileC.txt"); // duplicate of A, B

            string fileD = Path.Combine(tempDir, "fileD.txt"); // same size as A, different hash
            string fileE = Path.Combine(tempDir, "fileE.txt"); // unique file
            string fileF = Path.Combine(tempDir, "fileF.txt"); // empty file (0 bytes)

            // System folder that should be ignored
            string sysDir = Path.Combine(tempDir, "$Recycle.Bin");
            Directory.CreateDirectory(sysDir);
            string sysFile = Path.Combine(sysDir, "deleted_file.txt");

            File.WriteAllBytes(fileA, contentX);
            File.WriteAllBytes(fileB, contentX);
            File.WriteAllBytes(fileC, contentX);
            File.WriteAllBytes(fileD, contentY);
            File.WriteAllBytes(fileE, contentZ);
            File.WriteAllBytes(fileF, Array.Empty<byte>());
            File.WriteAllBytes(sysFile, contentX);
            Console.WriteLine("DONE");

            // 3. Test Scanner
            Console.Write("Testing ScannerService (size grouping, SHA-256, exclusion, empty file skip)... ");
            var scanner = new ScannerService();
            var options = new ScanOptions
            {
                RootPath = tempDir,
                IncludeSubdirectories = true,
                FileMask = "*.*",
                MinSizeKb = 0,
                ExcludeSystemFolders = true
            };

            var scanResult = await scanner.ScanAsync(options, null, CancellationToken.None);

            if (scanResult.IsCancelled)
            {
                throw new Exception("Scan should not be cancelled!");
            }

            if (scanResult.Groups.Count != 1)
            {
                throw new Exception($"Expected 1 duplicate group, but got {scanResult.Groups.Count}!");
            }

            var group = scanResult.Groups[0];
            if (group.Files.Count != 3)
            {
                throw new Exception($"Expected 3 files in duplicate group, but got {group.Files.Count}!");
            }

            var filePaths = group.Files.Select(f => f.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!filePaths.Contains(fileA) || !filePaths.Contains(fileB) || !filePaths.Contains(fileC))
            {
                throw new Exception("Duplicate group did not contain fileA, fileB, and fileC!");
            }

            if (filePaths.Contains(fileD))
            {
                throw new Exception("fileD (different hash) was incorrectly included in duplicate group!");
            }

            if (filePaths.Contains(fileE))
            {
                throw new Exception("fileE (unique) was incorrectly included in duplicate group!");
            }

            if (filePaths.Contains(fileF))
            {
                throw new Exception("fileF (empty file) was incorrectly included in duplicate group!");
            }

            if (filePaths.Contains(sysFile))
            {
                throw new Exception("sysFile inside $Recycle.Bin was not excluded!");
            }

            long expectedWasted = (3 - 1) * 100;
            if (group.WastedSpace != expectedWasted)
            {
                throw new Exception($"Expected wasted space {expectedWasted}, got {group.WastedSpace}!");
            }
            Console.WriteLine("PASSED");

            // 4. Test FileActionService (Move with conflict handling)
            Console.Write("Testing FileActionService Move with conflict resolution (2), (3)... ");
            string journalPath = Path.Combine(tempDir, "test-journal.jsonl");
            var journal = new ActionJournal(journalPath);
            var actionService = new FileActionService(journal);
            string destDir = Path.Combine(tempDir, "MovedFiles");

            // Move fileA to destDir -> should be destDir/fileA.txt
            bool moved1 = actionService.MoveFile(fileA, destDir, out string finalPath1, out string? err1);
            if (!moved1 || !File.Exists(finalPath1) || Path.GetFileName(finalPath1) != "fileA.txt")
            {
                throw new Exception($"Move 1 failed: {err1}");
            }

            // Create another file named fileA.txt in subDir and move it to destDir -> should be destDir/fileA (2).txt
            string anotherFileA = Path.Combine(subDir, "fileA.txt");
            File.WriteAllBytes(anotherFileA, contentX);
            bool moved2 = actionService.MoveFile(anotherFileA, destDir, out string finalPath2, out string? err2);
            if (!moved2 || !File.Exists(finalPath2) || Path.GetFileName(finalPath2) != "fileA (2).txt")
            {
                throw new Exception($"Move 2 conflict resolution failed: got {Path.GetFileName(finalPath2)}, error: {err2}");
            }

            // Create a third file named fileA.txt in subDir and move it to destDir -> should be destDir/fileA (3).txt
            File.WriteAllBytes(anotherFileA, contentX);
            bool moved3 = actionService.MoveFile(anotherFileA, destDir, out string finalPath3, out string? err3);
            if (!moved3 || !File.Exists(finalPath3) || Path.GetFileName(finalPath3) != "fileA (3).txt")
            {
                throw new Exception($"Move 3 conflict resolution failed: got {Path.GetFileName(finalPath3)}, error: {err3}");
            }

            Console.WriteLine("PASSED");

            // 5. Test FileActionService (Delete to Recycle Bin)
            Console.Write("Testing FileActionService TrySendToRecycleBin (Recycle Bin)... ");
            string fileToDelete = Path.Combine(tempDir, "file_to_delete.txt");
            File.WriteAllBytes(fileToDelete, contentX);

            bool deleted = actionService.TrySendToRecycleBin(fileToDelete, out string? delErr);
            if (!deleted || File.Exists(fileToDelete))
            {
                throw new Exception($"TrySendToRecycleBin failed: {delErr}");
            }
            Console.WriteLine("PASSED");

            // 6. Test last-copy guard
            Console.Write("Testing last-copy guard... ");
            var guardItems = new[]
            {
                new DuplicateFileItem(Path.Combine(tempDir, "guard_a.txt"), 10, DateTime.UtcNow),
                new DuplicateFileItem(Path.Combine(tempDir, "guard_b.txt"), 10, DateTime.UtcNow)
            };
            var guardGroup = new DuplicateGroup("GUARD-HASH", 10, guardItems);
            foreach (var item in guardItems)
            {
                item.IsChecked = true;
            }

            var allCopiesRemoved = SafetyGuards.FindGroupsLosingAllCopies(new[] { guardGroup });
            if (allCopiesRemoved.Count != 1 || !ReferenceEquals(allCopiesRemoved[0], guardGroup))
            {
                throw new Exception("Last-copy guard did not detect deletion of every copy in the group!");
            }

            guardItems[1].IsChecked = false;
            var withSurvivingCopy = SafetyGuards.FindGroupsLosingAllCopies(new[] { guardGroup });
            if (withSurvivingCopy.Count != 0)
            {
                throw new Exception("Last-copy guard incorrectly flagged a group that still has an unchecked copy!");
            }
            Console.WriteLine("PASSED");

            // 7. Test action journal
            Console.Write("Testing action journal (append-only JSONL)... ");
            string journalRecycleFile = Path.Combine(tempDir, "journal_recycle.txt");
            File.WriteAllBytes(journalRecycleFile, contentX);
            actionService.TrySendToRecycleBin(journalRecycleFile, out _);

            if (!File.Exists(journalPath))
            {
                throw new Exception("Journal file was not created!");
            }

            string[] journalLines = File.ReadAllLines(journalPath);
            bool hasRecycleEntry = journalLines.Any(line => line.Contains("\"operation\":\"delete_recycle\""));
            bool hasMoveEntry = journalLines.Any(line => line.Contains("\"operation\":\"move\""));
            if (!hasRecycleEntry)
            {
                throw new Exception("No delete_recycle entry found in the journal!");
            }
            if (!hasMoveEntry)
            {
                throw new Exception("No move entry found in the journal!");
            }

            Console.WriteLine("PASSED");

            // 8. Test reparse-point detection used for scan roots and files (no real junction needed)
            Console.Write("Testing reparse-point detection... ");
            if (!SafetyGuards.IsReparsePoint(FileAttributes.ReparsePoint) ||
                SafetyGuards.IsReparsePoint(FileAttributes.Normal) ||
                SafetyGuards.IsReparsePoint(tempDir) ||
                SafetyGuards.IsReparsePoint(string.Empty))
            {
                throw new Exception("Reparse-point detection did not behave as expected!");
            }
            Console.WriteLine("PASSED");

            // 9. Test journal write-failure signal (exactly once per run, operation not failed)
            Console.Write("Testing journal write-failure signal... ");
            string blockedParent = Path.Combine(tempDir, "journal_blocker.txt");
            File.WriteAllBytes(blockedParent, contentX);
            var failingJournal = new ActionJournal(Path.Combine(blockedParent, "journal.jsonl"));
            int failureSignals = 0;
            failingJournal.WriteFailed += _ => failureSignals++;
            failingJournal.Record("delete_permanent", Path.Combine(tempDir, "x.txt"), true, "test");
            failingJournal.Record("move", Path.Combine(tempDir, "y.txt"), true, "test");
            if (failureSignals != 1)
            {
                throw new Exception($"Expected exactly one journal failure signal, got {failureSignals}!");
            }
            Console.WriteLine("PASSED");

            // 10. Test custom folder excludes (.git / node_modules)
            Console.Write("Testing custom folder excludes... ");
            string excludeRoot = Path.Combine(tempDir, "exclude_test");
            string excludeGitDir = Path.Combine(excludeRoot, ".git");
            Directory.CreateDirectory(excludeGitDir);
            string includedFile = Path.Combine(excludeRoot, "included.bin");
            string gitFile = Path.Combine(excludeGitDir, "included_copy.bin");
            byte[] excludeContent = new byte[256];
            Array.Fill(excludeContent, (byte)'E');
            File.WriteAllBytes(includedFile, excludeContent);
            File.WriteAllBytes(gitFile, excludeContent);

            var excludeResult = await scanner.ScanAsync(new ScanOptions
            {
                RootPath = excludeRoot,
                IncludeSubdirectories = true,
                FileMask = "*.*",
                MinSizeKb = 0,
                ExcludeSystemFolders = false,
                ExcludeFolderNames = new List<string> { ".git" }
            }, null, CancellationToken.None);

            if (excludeResult.Groups.Count != 0)
            {
                throw new Exception($"Expected 0 groups when .git is excluded, got {excludeResult.Groups.Count}!");
            }

            if (excludeResult.Groups.SelectMany(g => g.Files)
                .Any(f => f.FilePath.StartsWith(excludeGitDir, StringComparison.OrdinalIgnoreCase)))
            {
                throw new Exception(".git folder file appeared in duplicate groups despite exclusion!");
            }

            // Control: without the exclude list, the same pair forms a group.
            var controlResult = await scanner.ScanAsync(new ScanOptions
            {
                RootPath = excludeRoot,
                IncludeSubdirectories = true,
                FileMask = "*.*",
                MinSizeKb = 0,
                ExcludeSystemFolders = false,
                ExcludeFolderNames = new List<string>()
            }, null, CancellationToken.None);

            if (controlResult.Groups.Count != 1)
            {
                throw new Exception($"Control scan without .git exclude should find 1 group, got {controlResult.Groups.Count}!");
            }
            Console.WriteLine("PASSED");

            // 11. Test locked file: goes to errors, unlocked partner forms no group
            Console.Write("Testing locked file handling (FileShare.ReadWrite)... ");
            string lockRoot = Path.Combine(tempDir, "lock_test");
            Directory.CreateDirectory(lockRoot);
            byte[] lockContent = new byte[512];
            Array.Fill(lockContent, (byte)'L');
            string lockedFile = Path.Combine(lockRoot, "locked.bin");
            string unlockedPartner = Path.Combine(lockRoot, "partner.bin");
            File.WriteAllBytes(lockedFile, lockContent);
            File.WriteAllBytes(unlockedPartner, lockContent);

            using (var exclusiveLock = new FileStream(lockedFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var lockProgressReports = new List<ScanProgress>();
                var lockRecorder = new RecordingProgress(lockProgressReports);

                var lockResult = await scanner.ScanAsync(new ScanOptions
                {
                    RootPath = lockRoot,
                    IncludeSubdirectories = true,
                    FileMask = "*.*",
                    MinSizeKb = 0,
                    ExcludeSystemFolders = false,
                    ExcludeFolderNames = new List<string>()
                }, lockRecorder, CancellationToken.None);

                bool lockedInErrors = lockResult.Errors
                    .Any(e => string.Equals(e.FilePath, lockedFile, StringComparison.OrdinalIgnoreCase));
                if (!lockedInErrors)
                {
                    throw new Exception("Locked file was not reported in the scan errors list!");
                }

                if (lockResult.Groups.Count != 0)
                {
                    throw new Exception($"Locked file test produced {lockResult.Groups.Count} group(s); unlocked partner must not form a 1-file group!");
                }

                if (lockResult.Groups.SelectMany(g => g.Files)
                    .Any(f => string.Equals(f.FilePath, unlockedPartner, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new Exception("Unlocked partner appeared in a group although its size-collision partner could not be hashed!");
                }

                // The final byte counter must reflect bytes actually read (only the readable partner),
                // not the pre-computed workload that included the unreadable file.
                var lockFinal = lockProgressReports.Last();
                if (lockFinal.HashBytesProcessed != lockContent.Length)
                {
                    throw new Exception($"Final progress reported {lockFinal.HashBytesProcessed} hashed bytes, but only {lockContent.Length} were actually read!");
                }
                if (lockFinal.HashBytesProcessed >= lockFinal.HashBytesTotal)
                {
                    throw new Exception("Final progress claimed the full hash workload even though a file could not be read!");
                }
            }
            Console.WriteLine("PASSED");

            // 11b. Writer with a share mode that allows readers: the file is hashed and may join a group.
            Console.Write("Testing reader-allowing writer share (FileShare.Read)... ");
            string sharedRoot = Path.Combine(tempDir, "shared_read_test");
            Directory.CreateDirectory(sharedRoot);
            byte[] sharedContent = new byte[512];
            Array.Fill(sharedContent, (byte)'S');
            string sharedLockedFile = Path.Combine(sharedRoot, "shared.bin");
            string sharedPartner = Path.Combine(sharedRoot, "shared_partner.bin");
            File.WriteAllBytes(sharedLockedFile, sharedContent);
            File.WriteAllBytes(sharedPartner, sharedContent);

            using (var readableLock = new FileStream(sharedLockedFile, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                var sharedResult = await scanner.ScanAsync(new ScanOptions
                {
                    RootPath = sharedRoot,
                    IncludeSubdirectories = true,
                    FileMask = "*.*",
                    MinSizeKb = 0,
                    ExcludeSystemFolders = false,
                    ExcludeFolderNames = new List<string>()
                }, null, CancellationToken.None);

                if (sharedResult.Errors.Any(e => string.Equals(e.FilePath, sharedLockedFile, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new Exception("A writer sharing for read must not cause a hash error!");
                }

                if (sharedResult.Groups.Count != 1)
                {
                    throw new Exception($"Expected 1 group when the writer allows readers, got {sharedResult.Groups.Count}!");
                }

                var sharedPaths = sharedResult.Groups[0].Files
                    .Select(f => f.FilePath)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (!sharedPaths.Contains(sharedLockedFile) || !sharedPaths.Contains(sharedPartner))
                {
                    throw new Exception("Reader-allowing shared file and its unlocked partner were not grouped together!");
                }
            }
            Console.WriteLine("PASSED");

            // 11c. Cancel in the middle of a large file, before it finishes hashing.
            Console.Write("Testing mid-file cancellation... ");
            string cancelRoot = Path.Combine(tempDir, "cancel_mid_file_test");
            Directory.CreateDirectory(cancelRoot);
            int cancelFileSize = 4 * 1024 * 1024;
            byte[] cancelContent = new byte[cancelFileSize];
            for (int i = 0; i < cancelFileSize; i++)
            {
                cancelContent[i] = (byte)(i % 239);
            }
            File.WriteAllBytes(Path.Combine(cancelRoot, "cancel1.bin"), cancelContent);
            File.WriteAllBytes(Path.Combine(cancelRoot, "cancel2.bin"), cancelContent);

            using (var cancelCts = new CancellationTokenSource())
            {
                bool cancelledFromProgress = false;
                long maxBytesSeen = 0;
                var cancelProgress = new InlineProgress(p =>
                {
                    if (p.HashBytesProcessed > maxBytesSeen)
                    {
                        maxBytesSeen = p.HashBytesProcessed;
                    }
                    if (!cancelledFromProgress && p.HashBytesProcessed > 0 && p.HashBytesProcessed < cancelFileSize)
                    {
                        cancelledFromProgress = true;
                        cancelCts.Cancel();
                    }
                });

                var cancelResult = await scanner.ScanAsync(new ScanOptions
                {
                    RootPath = cancelRoot,
                    IncludeSubdirectories = true,
                    FileMask = "*.*",
                    MinSizeKb = 0,
                    ExcludeSystemFolders = false,
                    ExcludeFolderNames = new List<string>()
                }, cancelProgress, cancelCts.Token);

                if (!cancelResult.IsCancelled)
                {
                    throw new Exception("Scan was not reported as cancelled after mid-file cancellation!");
                }
                if (!cancelledFromProgress)
                {
                    throw new Exception("Cancellation was never triggered from intermediate byte progress!");
                }
                if (maxBytesSeen >= cancelFileSize)
                {
                    throw new Exception("The first large file finished hashing before cancellation could take effect!");
                }
            }
            Console.WriteLine("PASSED");

            // 11d. Exclude-list invariants: case-insensitive names, root exemption, system checkbox independence.
            Console.Write("Testing exclude-list invariants... ");
            byte[] excludeInvariantContent = new byte[256];
            Array.Fill(excludeInvariantContent, (byte)'Q');

            // Case-insensitive name match.
            string ciRoot = Path.Combine(tempDir, "exclude_ci_test");
            string ciSubDir = Path.Combine(ciRoot, "MixedCaseDir");
            Directory.CreateDirectory(ciSubDir);
            File.WriteAllBytes(Path.Combine(ciRoot, "ci_root.bin"), excludeInvariantContent);
            File.WriteAllBytes(Path.Combine(ciSubDir, "ci_sub.bin"), excludeInvariantContent);

            var ciResult = await scanner.ScanAsync(new ScanOptions
            {
                RootPath = ciRoot,
                IncludeSubdirectories = true,
                FileMask = "*.*",
                MinSizeKb = 0,
                ExcludeSystemFolders = false,
                ExcludeFolderNames = new List<string> { "mixedcasedir" }
            }, null, CancellationToken.None);
            if (ciResult.Groups.Count != 0)
            {
                throw new Exception($"Case-insensitive exclude did not skip MixedCaseDir (groups={ciResult.Groups.Count})!");
            }

            // A chosen scan root whose own name is on the exclude list must still be scanned.
            string excludedNameRoot = Path.Combine(tempDir, "node_modules");
            Directory.CreateDirectory(excludedNameRoot);
            File.WriteAllBytes(Path.Combine(excludedNameRoot, "root_a.bin"), excludeInvariantContent);
            File.WriteAllBytes(Path.Combine(excludedNameRoot, "root_b.bin"), excludeInvariantContent);

            var rootExemptResult = await scanner.ScanAsync(new ScanOptions
            {
                RootPath = excludedNameRoot,
                IncludeSubdirectories = true,
                FileMask = "*.*",
                MinSizeKb = 0,
                ExcludeSystemFolders = false,
                ExcludeFolderNames = new List<string> { "node_modules" }
            }, null, CancellationToken.None);
            if (rootExemptResult.Groups.Count != 1)
            {
                throw new Exception($"Explicitly chosen root named node_modules must still be scanned (groups={rootExemptResult.Groups.Count})!");
            }

            // ExcludeSystemFolders=false must not skip Windows unless the name is on the user list.
            string windowsScanRoot = Path.Combine(tempDir, "windows_check_root");
            string windowsDir = Path.Combine(windowsScanRoot, "Windows");
            Directory.CreateDirectory(windowsDir);
            File.WriteAllBytes(Path.Combine(windowsDir, "win_a.bin"), excludeInvariantContent);
            File.WriteAllBytes(Path.Combine(windowsDir, "win_b.bin"), excludeInvariantContent);

            var windowsKeptResult = await scanner.ScanAsync(new ScanOptions
            {
                RootPath = windowsScanRoot,
                IncludeSubdirectories = true,
                FileMask = "*.*",
                MinSizeKb = 0,
                ExcludeSystemFolders = false,
                ExcludeFolderNames = new List<string>()
            }, null, CancellationToken.None);
            if (windowsKeptResult.Groups.Count != 1)
            {
                throw new Exception($"Windows folder was skipped although ExcludeSystemFolders=false (groups={windowsKeptResult.Groups.Count})!");
            }

            var windowsExcludedResult = await scanner.ScanAsync(new ScanOptions
            {
                RootPath = windowsScanRoot,
                IncludeSubdirectories = true,
                FileMask = "*.*",
                MinSizeKb = 0,
                ExcludeSystemFolders = false,
                ExcludeFolderNames = new List<string> { "Windows" }
            }, null, CancellationToken.None);
            if (windowsExcludedResult.Groups.Count != 0)
            {
                throw new Exception($"Windows folder on the user list was not excluded (groups={windowsExcludedResult.Groups.Count})!");
            }
            Console.WriteLine("PASSED");

            // 12. Test honest large-file hash byte progress
            Console.Write("Testing large-file hash byte progress... ");
            string hashRoot = Path.Combine(tempDir, "hash_progress_test");
            Directory.CreateDirectory(hashRoot);
            int bigSize = 2 * 1024 * 1024;
            byte[] bigContent = new byte[bigSize];
            for (int i = 0; i < bigSize; i++)
            {
                bigContent[i] = (byte)(i % 251);
            }
            File.WriteAllBytes(Path.Combine(hashRoot, "big1.bin"), bigContent);
            File.WriteAllBytes(Path.Combine(hashRoot, "big2.bin"), bigContent);

            var progressReports = new List<ScanProgress>();
            var recordingProgress = new RecordingProgress(progressReports);
            var hashResult = await scanner.ScanAsync(new ScanOptions
            {
                RootPath = hashRoot,
                IncludeSubdirectories = true,
                FileMask = "*.*",
                MinSizeKb = 0,
                ExcludeSystemFolders = false,
                ExcludeFolderNames = new List<string>()
            }, recordingProgress, CancellationToken.None);

            if (hashResult.IsCancelled)
            {
                throw new Exception("Hash progress scan should not be cancelled!");
            }

            var byteReports = progressReports.Where(r => r.HashBytesTotal > 0).ToList();
            if (byteReports.Count == 0)
            {
                throw new Exception("No hash byte progress reports were emitted!");
            }

            long expectedHashTotal = (long)bigSize * 2;
            if (byteReports.Max(r => r.HashBytesTotal) != expectedHashTotal)
            {
                throw new Exception($"Expected HashBytesTotal {expectedHashTotal}, got {byteReports.Max(r => r.HashBytesTotal)}!");
            }

            if (!byteReports.Any(r => r.HashBytesProcessed > 0 && r.HashBytesProcessed < bigSize))
            {
                throw new Exception("Hash byte progress never advanced before the first file finished hashing!");
            }

            if (byteReports.Select(r => r.HashBytesProcessed).Distinct().Count() < 2)
            {
                throw new Exception("Hash byte progress did not report increasing hashed bytes!");
            }
            Console.WriteLine("PASSED");

            // 13. Hard links of one file share a (volume serial, file index) identity and are one file.
            Console.Write("Testing hard-link identity (NTFS file index)... ");
            string hardLinkRoot = Path.Combine(tempDir, "hardlink_test");
            Directory.CreateDirectory(hardLinkRoot);
            byte[] hardLinkContent = new byte[300];
            Array.Fill(hardLinkContent, (byte)'H');

            string originalFile = Path.Combine(hardLinkRoot, "original.bin");
            string hardLinkFile = Path.Combine(hardLinkRoot, "hardlink.bin");
            File.WriteAllBytes(originalFile, hardLinkContent);

            if (!CreateHardLink(hardLinkFile, originalFile, IntPtr.Zero))
            {
                throw new Exception($"CreateHardLink failed with Win32 error {Marshal.GetLastWin32Error()}!");
            }

            // A file and its hard link must not form a duplicate group on their own.
            var linkOnlyResult = await scanner.ScanAsync(new ScanOptions
            {
                RootPath = hardLinkRoot,
                IncludeSubdirectories = true,
                FileMask = "*.*",
                MinSizeKb = 0,
                ExcludeSystemFolders = false,
                ExcludeFolderNames = new List<string>()
            }, null, CancellationToken.None);

            if (linkOnlyResult.Groups.Count != 0)
            {
                throw new Exception($"A file and its hard link must not form a duplicate group, got {linkOnlyResult.Groups.Count}!");
            }

            // A real copy (different file id, same bytes) must group with exactly one linked name.
            string realCopyFile = Path.Combine(hardLinkRoot, "real_copy.bin");
            File.WriteAllBytes(realCopyFile, hardLinkContent);

            var copyResult = await scanner.ScanAsync(new ScanOptions
            {
                RootPath = hardLinkRoot,
                IncludeSubdirectories = true,
                FileMask = "*.*",
                MinSizeKb = 0,
                ExcludeSystemFolders = false,
                ExcludeFolderNames = new List<string>()
            }, null, CancellationToken.None);

            if (copyResult.Groups.Count != 1)
            {
                throw new Exception($"Expected 1 group with the real copy and one hard-link name, got {copyResult.Groups.Count}!");
            }

            var copyGroupFiles = copyResult.Groups[0].Files;
            if (copyGroupFiles.Count != 2)
            {
                throw new Exception($"Expected 2 files in the group (copy + one link), got {copyGroupFiles.Count}!");
            }

            if (!copyGroupFiles.Any(f => string.Equals(f.FilePath, realCopyFile, StringComparison.OrdinalIgnoreCase)))
            {
                throw new Exception("The real copy was not included in the duplicate group!");
            }

            int linkedNamesInGroup = copyGroupFiles.Count(f =>
                string.Equals(f.FilePath, originalFile, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(f.FilePath, hardLinkFile, StringComparison.OrdinalIgnoreCase));
            if (linkedNamesInGroup != 1)
            {
                throw new Exception($"Expected exactly one hard-link name in the group, got {linkedNamesInGroup}!");
            }
            Console.WriteLine("PASSED");

            // 14. Result summary always reports successes, skips and failures.
            Console.Write("Testing action summary builder... ");
            string summaryWithFailures = ActionSummary.Build(
                "Удалено", 5, 3, 1, new[] { "a.txt: нет доступа", "b.txt: занят" });
            if (!summaryWithFailures.Contains("Удалено: 3 из 5.") ||
                !summaryWithFailures.Contains("Пропущено: 1.") ||
                !summaryWithFailures.Contains("Ошибок: 2.") ||
                !summaryWithFailures.Contains("a.txt: нет доступа") ||
                !summaryWithFailures.Contains("b.txt: занят"))
            {
                throw new Exception($"Unexpected summary with failures:\n{summaryWithFailures}");
            }

            string summaryClean = ActionSummary.Build("Перемещено", 2, 2, 0, Array.Empty<string>());
            if (!summaryClean.Contains("Перемещено: 2 из 2.") ||
                !summaryClean.Contains("Пропущено: 0.") ||
                !summaryClean.Contains("Ошибок: 0."))
            {
                throw new Exception($"Unexpected clean summary:\n{summaryClean}");
            }

            string summaryTruncated = ActionSummary.Build(
                "Удалено", 10, 0, 0,
                new[] { "1", "2", "3", "4", "5", "6", "7" }, maxFailuresShown: 3);
            if (!summaryTruncated.Contains("Ошибок: 7.") ||
                !summaryTruncated.Contains("и ещё 4") ||
                summaryTruncated.Contains("• 7"))
            {
                throw new Exception($"Failure list was not truncated as expected:\n{summaryTruncated}");
            }
            Console.WriteLine("PASSED");

            // 15. Destination-inside-scanned-set detection used before move.
            Console.Write("Testing destination containment guard... ");
            string guardParent = Path.Combine(tempDir, "containment");
            string guardChild = Path.Combine(guardParent, "child", "grandchild");
            Directory.CreateDirectory(guardChild);
            string guardSibling = tempDir.TrimEnd(Path.DirectorySeparatorChar) + "2";

            if (!SafetyGuards.IsWithinDirectory(guardChild, guardParent) ||
                !SafetyGuards.IsWithinDirectory(guardParent, guardParent) ||
                SafetyGuards.IsWithinDirectory(guardParent, guardChild) ||
                SafetyGuards.IsWithinDirectory(tempDir, guardSibling) ||
                SafetyGuards.IsWithinDirectory(string.Empty, guardParent) ||
                SafetyGuards.IsWithinDirectory(guardParent, string.Empty))
            {
                throw new Exception("IsWithinDirectory behaved unexpectedly!");
            }
            Console.WriteLine("PASSED");

            // 16. Move warning must use the last successfully scanned root, not the editable
            // SelectedFolder textbox that the user can change while old groups stay on screen.
            Console.Write("Testing move warning uses last successful scan root... ");
            string scanRootA = Path.Combine(tempDir, "scan_root_A");
            string groupDirA = Path.Combine(scanRootA, "group_dir");
            string otherInnerA = Path.Combine(scanRootA, "other", "nested");
            string scanRootB = Path.Combine(tempDir, "scan_root_B");
            Directory.CreateDirectory(groupDirA);
            Directory.CreateDirectory(otherInnerA);
            Directory.CreateDirectory(scanRootB);

            byte[] warningContent = new byte[192];
            Array.Fill(warningContent, (byte)'W');
            File.WriteAllBytes(Path.Combine(groupDirA, "w1.bin"), warningContent);
            File.WriteAllBytes(Path.Combine(groupDirA, "w2.bin"), warningContent);

            var warningVm = new MainViewModel(
                scanner,
                actionService,
                settingsService,
                new ImageThumbnailService(),
                new NoOpDialogService(),
                journal)
            {
                SelectedFolder = scanRootA,
                IncludeSubdirectories = true,
                FileMask = "*.*",
                MinSizeKb = 0,
                ExcludeSystemFolders = false,
                ExcludeFolderNames = string.Empty
            };

            await warningVm.StartScanAsync();

            if (warningVm.Groups.Count != 1)
            {
                throw new Exception($"Warning-root scan expected 1 group, got {warningVm.Groups.Count}!");
            }

            // Edit the folder textbox after the scan. This alone must not relocate the reference tree.
            warningVm.SelectedFolder = scanRootB;

            if (!warningVm.IsDestinationInsideScannedSet(otherInnerA))
            {
                throw new Exception("Destination under the last scanned root but outside listed group directories was not flagged!");
            }

            if (!warningVm.IsDestinationInsideScannedSet(scanRootA) ||
                !warningVm.IsDestinationInsideScannedSet(groupDirA))
            {
                throw new Exception("Destination inside the last scanned root was not flagged!");
            }

            if (warningVm.IsDestinationInsideScannedSet(scanRootB))
            {
                throw new Exception("Destination in a different, unscanned folder was incorrectly treated as inside the scanned set!");
            }

            // A scan that never completes must not replace the stored reference root.
            string missingRoot = Path.Combine(tempDir, "does_not_exist_root");
            warningVm.SelectedFolder = missingRoot;
            await warningVm.StartScanAsync();
            if (!string.Equals(warningVm.LastSuccessfulScanRoot, scanRootA, StringComparison.OrdinalIgnoreCase))
            {
                throw new Exception("Failed/absent scan replaced the last successful scan root!");
            }
            Console.WriteLine("PASSED");

            // 17. Ship-shape metadata: assembly version and the standard AppData locations.
            Console.Write("Testing ship-shape version and data paths... ");
            if (!string.Equals(AppInfo.Version, "1.0.0", StringComparison.Ordinal))
            {
                throw new Exception($"Expected app version 1.0.0, got '{AppInfo.Version}'!");
            }

            string expectedAppData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Duplify");
            if (!string.Equals(AppInfo.AppDataDirectory, expectedAppData, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(AppInfo.JournalFilePath, Path.Combine(expectedAppData, "journal.jsonl"), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(AppInfo.LogsDirectory, Path.Combine(expectedAppData, "logs"), StringComparison.OrdinalIgnoreCase))
            {
                throw new Exception("AppData paths are not the expected %AppData%\\Duplify locations!");
            }
            Console.WriteLine("PASSED");

            // Testing bounded thumbnail loading & group switch cancellation
            Console.Write("Testing bounded thumbnail loading & cancellation... ");
            var fakeThumbnailService = new CountingThumbnailService();
            var thumbVm = new MainViewModel(
                scanner,
                actionService,
                settingsService,
                fakeThumbnailService,
                new NoOpDialogService(),
                journal);

            var fileList1 = new List<DuplicateFileItem>();
            for (int i = 0; i < 50; i++)
            {
                fileList1.Add(new DuplicateFileItem(Path.Combine(tempDir, $"photo_{i}.jpg"), 100, DateTime.UtcNow));
            }
            var group1 = new DuplicateGroup("hash_group_1", 100, fileList1);

            var fileList2 = new List<DuplicateFileItem>();
            for (int i = 0; i < 10; i++)
            {
                fileList2.Add(new DuplicateFileItem(Path.Combine(tempDir, $"other_{i}.png"), 100, DateTime.UtcNow));
            }
            var group2 = new DuplicateGroup("hash_group_2", 100, fileList2);

            thumbVm.Groups.Add(group1);
            thumbVm.Groups.Add(group2);

            // Selecting group1 triggers bounded load (up to 25 items)
            thumbVm.SelectedGroup = group1;
            await Task.Delay(100);

            if (fakeThumbnailService.RequestedCount > 25)
            {
                throw new Exception($"Bounded thumbnail load requested {fakeThumbnailService.RequestedCount} items, expected <= 25!");
            }

            // Switching to group2 cancels leftover loads for group1
            thumbVm.SelectedGroup = group2;
            await Task.Delay(100);

            // On-demand request for an item in group2
            thumbVm.RequestThumbnailLoad(fileList2[5]);
            await Task.Delay(50);

            Console.WriteLine("PASSED");

            Console.Write("Testing two-folder scan (cross-root duplicates)... ");
            string folderLeft = Path.Combine(tempDir, "compare_left");
            string folderRight = Path.Combine(tempDir, "compare_right");
            Directory.CreateDirectory(folderLeft);
            Directory.CreateDirectory(folderRight);
            byte[] compareShared = new byte[180];
            Array.Fill(compareShared, (byte)'Q');
            byte[] compareLocal = new byte[180];
            Array.Fill(compareLocal, (byte)'Z');
            string leftFile = Path.Combine(folderLeft, "q_left.bin");
            string rightFile = Path.Combine(folderRight, "q_right.bin");
            File.WriteAllBytes(leftFile, compareShared);
            File.WriteAllBytes(rightFile, compareShared);
            File.WriteAllBytes(Path.Combine(folderLeft, "z1.bin"), compareLocal);
            File.WriteAllBytes(Path.Combine(folderLeft, "z2.bin"), compareLocal);

            var twoFolderAll = await scanner.ScanAsync(new ScanOptions
            {
                RootPath = folderLeft,
                SecondRootPath = folderRight,
                IncludeSubdirectories = true,
                FileMask = "*.*",
                MinSizeKb = 0,
                ExcludeSystemFolders = false,
                OnlyCrossRootDuplicates = false
            }, null, CancellationToken.None);

            if (twoFolderAll.Groups.Count != 2)
            {
                throw new Exception($"Two-folder union scan expected 2 groups, got {twoFolderAll.Groups.Count}!");
            }

            var twoFolderCross = await scanner.ScanAsync(new ScanOptions
            {
                RootPath = folderLeft,
                SecondRootPath = folderRight,
                IncludeSubdirectories = true,
                FileMask = "*.*",
                MinSizeKb = 0,
                ExcludeSystemFolders = false,
                OnlyCrossRootDuplicates = true
            }, null, CancellationToken.None);

            if (twoFolderCross.Groups.Count != 1)
            {
                throw new Exception($"Cross-only scan expected 1 group, got {twoFolderCross.Groups.Count}!");
            }

            var crossPaths = twoFolderCross.Groups[0].Files.Select(f => f.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!crossPaths.Contains(leftFile) || !crossPaths.Contains(rightFile) || crossPaths.Count != 2)
            {
                throw new Exception("Cross-only group should be exactly the pair from both folders!");
            }
            Console.WriteLine("PASSED");

            string? sampleFolder = FindSampleFolder();
            if (sampleFolder is not null)
            {
                var sfResult = await scanner.ScanAsync(new ScanOptions
                {
                    RootPath = sampleFolder,
                    IncludeSubdirectories = true,
                    FileMask = "*.*",
                    MinSizeKb = 0,
                    ExcludeSystemFolders = true
                }, null, CancellationToken.None);
                Console.WriteLine($"SampleFolder scan: files scanned={sfResult.TotalFilesScanned}, groups={sfResult.Groups.Count}, errors={sfResult.Errors.Count}");
                foreach (var g in sfResult.Groups)
                {
                    Console.WriteLine($"  Group: {g.SampleFileName}, count={g.Count}, size={g.SizeFormatted}, wasted={g.WastedSpaceFormatted}");
                }
            }

            // 18. Bulk planner: preferred folder wins over date and name.
            Console.Write("Testing bulk planner: preferred folder priority... ");
            string bulkRoot = Path.Combine(tempDir, "bulk_planner");
            string bulkPreferredDir = Path.Combine(bulkRoot, "preferred");
            string bulkOtherDir = Path.Combine(bulkRoot, "other");
            string bulkNamesDir = Path.Combine(bulkRoot, "names");
            Directory.CreateDirectory(bulkPreferredDir);
            Directory.CreateDirectory(bulkOtherDir);
            Directory.CreateDirectory(bulkNamesDir);

            DateTime bulkBase = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            var prefItems = new List<DuplicateFileItem>
            {
                new DuplicateFileItem(Path.Combine(bulkOtherDir, "clean.bin"), 100, bulkBase.AddDays(30)),
                new DuplicateFileItem(Path.Combine(bulkPreferredDir, "copy (2).bin"), 100, bulkBase)
            };
            var prefGroup = new DuplicateGroup("bulk-pref", 100, prefItems);
            var prefPlan = BulkSelectionPlanner.Build(new[] { prefGroup }, new BulkSelectionOptions
            {
                PreferredFolder = bulkPreferredDir,
                KeepNewest = true,
                PreferCleanName = true,
                PreferShorterPath = true
            });
            if (prefPlan.ProcessedGroupCount != 1 || prefPlan.AmbiguousGroupCount != 0)
            {
                throw new Exception($"Preferred-folder plan expected 1 processed / 0 ambiguous, got {prefPlan.ProcessedGroupCount}/{prefPlan.AmbiguousGroupCount}!");
            }
            if (!ReferenceEquals(prefPlan.ProcessedGroups[0].KeptFile, prefItems[1]))
            {
                throw new Exception("Preferred folder did not win over a newer date and a cleaner name!");
            }
            if (prefPlan.MarkedFileCount != 1 || !ReferenceEquals(prefPlan.FilesToMark[0], prefItems[0]))
            {
                throw new Exception("Preferred-folder group did not mark exactly the file outside the preferred folder!");
            }
            Console.WriteLine("PASSED");

            // 19. Directory boundary: Photo must not contain Photography.
            Console.Write("Testing bulk planner: preferred-folder boundary... ");
            string photoDir = Path.Combine(bulkRoot, "Photo");
            string photographyDir = Path.Combine(bulkRoot, "Photography");
            Directory.CreateDirectory(photoDir);
            Directory.CreateDirectory(photographyDir);
            var boundaryItems = new List<DuplicateFileItem>
            {
                new DuplicateFileItem(Path.Combine(photographyDir, "a.bin"), 50, bulkBase),
                new DuplicateFileItem(Path.Combine(photoDir, "b.bin"), 50, bulkBase)
            };
            var boundaryGroup = new DuplicateGroup("bulk-boundary", 50, boundaryItems);
            var boundaryPlan = BulkSelectionPlanner.Build(new[] { boundaryGroup }, new BulkSelectionOptions
            {
                PreferredFolder = photoDir,
                PreferCleanName = false,
                PreferShorterPath = false
            });
            if (boundaryPlan.ProcessedGroupCount != 1 || !ReferenceEquals(boundaryPlan.ProcessedGroups[0].KeptFile, boundaryItems[1]))
            {
                throw new Exception("Directory boundary check failed: Photography must not count as inside Photo!");
            }
            Console.WriteLine("PASSED");

            // 20. Clean name beats copy markers and (2), but a photo-like number is not a marker.
            Console.Write("Testing bulk planner: clean-name priority... ");
            var numericItems = new List<DuplicateFileItem>
            {
                new DuplicateFileItem(Path.Combine(bulkNamesDir, "photo (2).bin"), 70, bulkBase.AddDays(5)),
                new DuplicateFileItem(Path.Combine(bulkNamesDir, "photo.bin"), 70, bulkBase)
            };
            var numericPlan = BulkSelectionPlanner.Build(new[] { new DuplicateGroup("bulk-num", 70, numericItems) }, new BulkSelectionOptions
            {
                PreferCleanName = true,
                PreferShorterPath = false
            });
            if (!ReferenceEquals(numericPlan.ProcessedGroups[0].KeptFile, numericItems[1]))
            {
                throw new Exception("Clean name did not win over the numeric (2) copy suffix!");
            }

            var kopiaItems = new List<DuplicateFileItem>
            {
                new DuplicateFileItem(Path.Combine(bulkNamesDir, "photo копия.bin"), 70, bulkBase.AddDays(5)),
                new DuplicateFileItem(Path.Combine(bulkNamesDir, "photo.bin"), 70, bulkBase)
            };
            var kopiaPlan = BulkSelectionPlanner.Build(new[] { new DuplicateGroup("bulk-kopia", 70, kopiaItems) }, new BulkSelectionOptions
            {
                PreferCleanName = true,
                PreferShorterPath = false
            });
            if (!ReferenceEquals(kopiaPlan.ProcessedGroups[0].KeptFile, kopiaItems[1]))
            {
                throw new Exception("Clean name did not win over the «копия» marker!");
            }

            var imgItems = new List<DuplicateFileItem>
            {
                new DuplicateFileItem(Path.Combine(bulkNamesDir, "photo_copy.bin"), 70, bulkBase.AddDays(5)),
                new DuplicateFileItem(Path.Combine(bulkNamesDir, "IMG_20260919.bin"), 70, bulkBase)
            };
            var imgPlan = BulkSelectionPlanner.Build(new[] { new DuplicateGroup("bulk-img", 70, imgItems) }, new BulkSelectionOptions
            {
                PreferCleanName = true,
                PreferShorterPath = false
            });
            if (!ReferenceEquals(imgPlan.ProcessedGroups[0].KeptFile, imgItems[1]))
            {
                throw new Exception("A photo-like number (IMG_20260919) must not be penalized as a copy!");
            }
            Console.WriteLine("PASSED");

            // 21. Shorter path: fewer directory segments, then shorter full-path length.
            Console.Write("Testing bulk planner: shorter-path priority... ");
            string shortRoot = Path.Combine(bulkRoot, "shortpath");
            Directory.CreateDirectory(Path.Combine(shortRoot, "aa", "bb", "cc"));
            Directory.CreateDirectory(Path.Combine(shortRoot, "alittlelonger"));
            var segmentItems = new List<DuplicateFileItem>
            {
                new DuplicateFileItem(Path.Combine(shortRoot, "aa", "bb", "cc", "f.bin"), 42, bulkBase.AddDays(9)),
                new DuplicateFileItem(Path.Combine(shortRoot, "f.bin"), 42, bulkBase)
            };
            var segmentPlan = BulkSelectionPlanner.Build(new[] { new DuplicateGroup("bulk-seg", 42, segmentItems) }, new BulkSelectionOptions
            {
                PreferCleanName = false,
                PreferShorterPath = true
            });
            if (!ReferenceEquals(segmentPlan.ProcessedGroups[0].KeptFile, segmentItems[1]))
            {
                throw new Exception("Fewer directory segments did not win the shorter-path criterion!");
            }

            var lengthItems = new List<DuplicateFileItem>
            {
                new DuplicateFileItem(Path.Combine(shortRoot, "alittlelonger", "f.bin"), 42, bulkBase),
                new DuplicateFileItem(Path.Combine(shortRoot, "aa", "f.bin"), 42, bulkBase)
            };
            var lengthPlan = BulkSelectionPlanner.Build(new[] { new DuplicateGroup("bulk-len", 42, lengthItems) }, new BulkSelectionOptions
            {
                PreferCleanName = false,
                PreferShorterPath = true
            });
            if (!ReferenceEquals(lengthPlan.ProcessedGroups[0].KeptFile, lengthItems[1]))
            {
                throw new Exception("Full-path length was not used as the shorter-path tie-breaker!");
            }
            Console.WriteLine("PASSED");

            // 22. Fallback rule: newest vs oldest by LastModifiedUtc.
            Console.Write("Testing bulk planner: newest/oldest fallback... ");
            var dateItems = new List<DuplicateFileItem>
            {
                new DuplicateFileItem(Path.Combine(bulkNamesDir, "aaa.bin"), 30, bulkBase),
                new DuplicateFileItem(Path.Combine(bulkNamesDir, "bbb.bin"), 30, bulkBase.AddDays(7))
            };
            var newestPlan = BulkSelectionPlanner.Build(new[] { new DuplicateGroup("bulk-date-n", 30, dateItems) }, new BulkSelectionOptions
            {
                KeepNewest = true,
                PreferCleanName = false,
                PreferShorterPath = false
            });
            if (!ReferenceEquals(newestPlan.ProcessedGroups[0].KeptFile, dateItems[1]))
            {
                throw new Exception("«Сохранить самый новый» did not keep the newest file!");
            }

            var oldestPlan = BulkSelectionPlanner.Build(new[] { new DuplicateGroup("bulk-date-o", 30, dateItems) }, new BulkSelectionOptions
            {
                KeepNewest = false,
                PreferCleanName = false,
                PreferShorterPath = false
            });
            if (!ReferenceEquals(oldestPlan.ProcessedGroups[0].KeptFile, dateItems[0]))
            {
                throw new Exception("«Сохранить самый старый» did not keep the oldest file!");
            }
            Console.WriteLine("PASSED");

            // 23. Full tie → ambiguous group, nothing marked.
            Console.Write("Testing bulk planner: ambiguous tie... ");
            var tieItems = new List<DuplicateFileItem>
            {
                new DuplicateFileItem(Path.Combine(bulkNamesDir, "t1.bin"), 20, bulkBase),
                new DuplicateFileItem(Path.Combine(bulkNamesDir, "t2.bin"), 20, bulkBase)
            };
            tieItems[0].IsChecked = true;
            tieItems[1].IsChecked = true;
            var tieGroup = new DuplicateGroup("bulk-tie", 20, tieItems);
            var tiePlan = BulkSelectionPlanner.Build(new[] { tieGroup }, new BulkSelectionOptions
            {
                PreferCleanName = false,
                PreferShorterPath = false
            });
            if (tiePlan.AmbiguousGroupCount != 1 || tiePlan.ProcessedGroupCount != 0 || tiePlan.MarkedFileCount != 0)
            {
                throw new Exception($"Full tie must be ambiguous and mark nothing, got processed={tiePlan.ProcessedGroupCount}, ambiguous={tiePlan.AmbiguousGroupCount}, marked={tiePlan.MarkedFileCount}!");
            }
            if (tiePlan.ChangedCount != 2)
            {
                throw new Exception($"Ambiguous group should clear 2 previous marks, changed={tiePlan.ChangedCount}!");
            }
            BulkSelectionPlanner.Apply(tiePlan);
            if (tieItems[0].IsChecked || tieItems[1].IsChecked)
            {
                throw new Exception("Applying an ambiguous plan must clear every mark in the group!");
            }
            Console.WriteLine("PASSED");

            // 24. Apply keeps exactly one copy unmarked per processed group, even if all were checked.
            Console.Write("Testing bulk planner: exactly one kept copy per group... ");
            var applyItemsA = new List<DuplicateFileItem>
            {
                new DuplicateFileItem(Path.Combine(bulkNamesDir, "k1.bin"), 80, bulkBase),
                new DuplicateFileItem(Path.Combine(bulkNamesDir, "k2.bin"), 80, bulkBase.AddDays(1)),
                new DuplicateFileItem(Path.Combine(bulkNamesDir, "k3.bin"), 80, bulkBase.AddDays(2))
            };
            var applyGroupA = new DuplicateGroup("bulk-apply-a", 80, applyItemsA);
            var applyGroupB = new DuplicateGroup("bulk-apply-b", 90, new List<DuplicateFileItem>
            {
                new DuplicateFileItem(Path.Combine(bulkNamesDir, "m1.bin"), 90, bulkBase),
                new DuplicateFileItem(Path.Combine(bulkNamesDir, "m2.bin"), 90, bulkBase.AddDays(1))
            });
            foreach (var f in applyGroupA.Files) f.IsChecked = true;
            foreach (var f in applyGroupB.Files) f.IsChecked = true;

            var applyPlan = BulkSelectionPlanner.Build(new[] { applyGroupA, applyGroupB }, new BulkSelectionOptions
            {
                KeepNewest = true,
                PreferCleanName = false,
                PreferShorterPath = false
            });
            if (!BulkSelectionPlanner.TryApply(applyPlan, new[] { applyGroupA, applyGroupB }))
            {
                throw new Exception("A freshly built plan should apply successfully!");
            }

            foreach (var g in new[] { applyGroupA, applyGroupB })
            {
                int uncheckedCount = g.Files.Count(f => !f.IsChecked);
                if (uncheckedCount != 1)
                {
                    throw new Exception($"Processed group must keep exactly one unmarked copy, got {uncheckedCount}!");
                }
            }
            if (!string.Equals(applyGroupA.Files.Single(f => !f.IsChecked).FilePath, Path.Combine(bulkNamesDir, "k3.bin"), StringComparison.OrdinalIgnoreCase))
            {
                throw new Exception("Apply did not keep the newest copy in group A!");
            }
            Console.WriteLine("PASSED");

            // 25. Preview counts, bytes and changed-count are exact.
            Console.Write("Testing bulk planner: preview counts/bytes/changed... ");
            var countItems = new List<DuplicateFileItem>
            {
                new DuplicateFileItem(Path.Combine(bulkNamesDir, "p1.bin"), 100, bulkBase),
                new DuplicateFileItem(Path.Combine(bulkNamesDir, "p2.bin"), 100, bulkBase.AddDays(1)),
                new DuplicateFileItem(Path.Combine(bulkNamesDir, "p3.bin"), 100, bulkBase.AddDays(2))
            };
            var countGroup = new DuplicateGroup("bulk-count", 100, countItems);
            var countPlanUnchecked = BulkSelectionPlanner.Build(new[] { countGroup }, new BulkSelectionOptions
            {
                KeepNewest = true,
                PreferCleanName = false,
                PreferShorterPath = false
            });
            if (countPlanUnchecked.MarkedFileCount != 2 || countPlanUnchecked.MarkedBytes != 200 || countPlanUnchecked.ChangedCount != 2)
            {
                throw new Exception($"Unexpected preview (unchecked): marked={countPlanUnchecked.MarkedFileCount}, bytes={countPlanUnchecked.MarkedBytes}, changed={countPlanUnchecked.ChangedCount}!");
            }

            countItems[2].IsChecked = true; // user already marked the copy that should be kept
            var countPlanChecked = BulkSelectionPlanner.Build(new[] { countGroup }, new BulkSelectionOptions
            {
                KeepNewest = true,
                PreferCleanName = false,
                PreferShorterPath = false
            });
            if (countPlanChecked.ChangedCount != 3)
            {
                throw new Exception($"Changed-count should also count the to-be-cleared kept flag, got {countPlanChecked.ChangedCount}!");
            }
            Console.WriteLine("PASSED");

            // 26. A stale plan must not be applied partially.
            Console.Write("Testing bulk planner: stale-plan rejection... ");
            var staleItems = new List<DuplicateFileItem>
            {
                new DuplicateFileItem(Path.Combine(bulkNamesDir, "s1.bin"), 60, bulkBase),
                new DuplicateFileItem(Path.Combine(bulkNamesDir, "s2.bin"), 60, bulkBase.AddDays(1)),
                new DuplicateFileItem(Path.Combine(bulkNamesDir, "s3.bin"), 60, bulkBase.AddDays(2))
            };
            var staleGroup = new DuplicateGroup("bulk-stale", 60, staleItems);
            var stalePlan = BulkSelectionPlanner.Build(new[] { staleGroup }, new BulkSelectionOptions
            {
                KeepNewest = true,
                PreferCleanName = false,
                PreferShorterPath = false
            });
            if (!BulkSelectionPlanner.IsStillValid(stalePlan, new[] { staleGroup }))
            {
                throw new Exception("A freshly built plan should be valid!");
            }

            staleItems[0].IsChecked = true; // would be changed by a partial apply
            staleGroup.Files.Remove(staleItems[1]); // make the snapshot stale
            bool[] flagsBefore = staleItems.Select(f => f.IsChecked).ToArray();
            if (BulkSelectionPlanner.TryApply(stalePlan, new[] { staleGroup }))
            {
                throw new Exception("A stale plan must be rejected!");
            }
            if (!staleItems.Select(f => f.IsChecked).SequenceEqual(flagsBefore))
            {
                throw new Exception("A rejected stale plan must not change any flag!");
            }
            Console.WriteLine("PASSED");

            // 27. An unnormalizable file path makes its whole group ambiguous (fail-safe).
            Console.Write("Testing bulk planner: unnormalizable path is fail-safe... ");
            string badPath = Path.Combine(bulkNamesDir, "bad\0name.bin");
            bool badPathIsUnnormalizable;
            try
            {
                Path.GetFullPath(badPath);
                badPathIsUnnormalizable = false;
            }
            catch
            {
                badPathIsUnnormalizable = true;
            }
            if (!badPathIsUnnormalizable)
            {
                throw new Exception("Test setup error: the chosen path is actually normalizable on this OS!");
            }

            var badPathItems = new List<DuplicateFileItem>
            {
                new DuplicateFileItem(Path.Combine(bulkPreferredDir, "good.bin"), 55, bulkBase.AddDays(5)),
                new DuplicateFileItem(badPath, 55, bulkBase)
            };
            var badPathGroup = new DuplicateGroup("bulk-bad-path", 55, badPathItems);
            var badPathPlan = BulkSelectionPlanner.Build(new[] { badPathGroup }, new BulkSelectionOptions
            {
                PreferredFolder = bulkPreferredDir,
                KeepNewest = true,
                PreferCleanName = false,
                PreferShorterPath = false
            });
            if (badPathPlan.AmbiguousGroupCount != 1 || badPathPlan.ProcessedGroupCount != 0 || badPathPlan.MarkedFileCount != 0)
            {
                throw new Exception($"An unnormalizable path must make the whole group ambiguous, got processed={badPathPlan.ProcessedGroupCount}, ambiguous={badPathPlan.AmbiguousGroupCount}, marked={badPathPlan.MarkedFileCount}!");
            }
            Console.WriteLine("PASSED");

            // 28. Global selected count/bytes track manual changes and clearing.
            Console.Write("Testing global selection totals... ");
            var totalsVm = new MainViewModel(
                scanner,
                actionService,
                settingsService,
                new ImageThumbnailService(),
                new NoOpDialogService(),
                journal);

            var totalItems = new List<DuplicateFileItem>
            {
                new DuplicateFileItem(Path.Combine(bulkNamesDir, "tot1.bin"), 150, bulkBase),
                new DuplicateFileItem(Path.Combine(bulkNamesDir, "tot2.bin"), 150, bulkBase)
            };
            var totalsGroup = new DuplicateGroup("bulk-totals", 150, totalItems);
            totalsVm.Groups.Add(totalsGroup);

            if (totalsVm.SelectedCount != 0 || totalsVm.SelectedBytes != 0)
            {
                throw new Exception($"A freshly added group should report no selection, got {totalsVm.SelectedCount}/{totalsVm.SelectedBytes}!");
            }

            totalItems[0].IsChecked = true;
            if (totalsVm.SelectedCount != 1 || totalsVm.SelectedBytes != 150)
            {
                throw new Exception($"Manual check should update totals to 1/150, got {totalsVm.SelectedCount}/{totalsVm.SelectedBytes}!");
            }

            totalItems[1].IsChecked = true;
            if (totalsVm.SelectedCount != 2 || totalsVm.SelectedBytes != 300)
            {
                throw new Exception($"Second manual check should update totals to 2/300, got {totalsVm.SelectedCount}/{totalsVm.SelectedBytes}!");
            }

            totalsVm.ClearAllMarksCommand.Execute(null);
            if (totalsVm.SelectedCount != 0 || totalsVm.SelectedBytes != 0)
            {
                throw new Exception($"«Снять все отметки» should zero the totals, got {totalsVm.SelectedCount}/{totalsVm.SelectedBytes}!");
            }

            totalItems[0].IsChecked = true;
            totalsVm.Groups.Remove(totalsGroup);
            if (totalsVm.SelectedCount != 0 || totalsVm.SelectedBytes != 0)
            {
                throw new Exception("Removing a group should drop its contribution to the totals!");
            }
            Console.WriteLine("PASSED");

            // Restore clean default settings
            settingsService.SaveSettings(new AppSettings
            {
                LastFolder = sampleFolder ?? string.Empty,
                IncludeSubdirectories = true,
                FileMask = "*.*",
                MinSizeKb = 0,
                ExcludeSystemFolders = true,
                ExcludeFolderNames = ".git;node_modules;AppData"
            });

            Console.WriteLine("ALL SELF-TESTS PASSED SUCCESSFULLY!");
            return 0;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"TEST FAILED: {ex.Message}\n{ex.StackTrace}");
            Console.ResetColor();
            return 1;
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
            catch
            {
                // Ignore cleanup
            }
        }
    }

    private static string? FindSampleFolder()
    {
        // Support both `dotnet run` from a checkout and launching its built executable.
        // A published app without the optional repository fixtures simply skips this scan.
        foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (DirectoryInfo? directory = new(start); directory is not null; directory = directory.Parent)
            {
                if (!File.Exists(Path.Combine(directory.FullName, "Duplify.csproj"))) continue;
                string sample = Path.Combine(directory.FullName, "SampleFolder");
                if (Directory.Exists(sample)) return sample;
                break;
            }
        }
        return null;
    }

    private sealed class NoOpDialogService : IDialogService
    {
        public string? PickFolder(string title, string? initialFolder = null) => null;
        public bool Confirm(string message, string title) => false;
        public bool ConfirmDanger(string message, string title) => false;
        public bool ConfirmLastCopy(string message, string title) => false;
        public bool ConfirmPermanentDelete(string message, string title) => false;
        public void ShowInfo(string message, string title) { }
        public void ShowError(string message, string title) { }
        public bool ShowActionSummary(string message, string title) => false;
        public BulkSelectionPlan? ShowBulkResolve(IReadOnlyList<DuplicateGroup> groups) => null;
    }

    private sealed class RecordingProgress : IProgress<ScanProgress>
    {
        private readonly List<ScanProgress> _reports;

        public RecordingProgress(List<ScanProgress> reports)
        {
            _reports = reports;
        }

        public void Report(ScanProgress value)
        {
            lock (_reports)
            {
                _reports.Add(value);
            }
        }
    }

    private sealed class InlineProgress : IProgress<ScanProgress>
    {
        private readonly Action<ScanProgress> _onReport;

        public InlineProgress(Action<ScanProgress> onReport)
        {
            _onReport = onReport;
        }

        public void Report(ScanProgress value)
        {
            _onReport(value);
        }
    }

    private sealed class CountingThumbnailService : IImageThumbnailService
    {
        private int _requestedCount;
        public int RequestedCount => _requestedCount;

        public async Task<ImageSource?> LoadThumbnailAsync(string filePath, int targetPixelWidth = 160, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _requestedCount);
            try
            {
                await Task.Delay(10, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            return null;
        }
    }
}
