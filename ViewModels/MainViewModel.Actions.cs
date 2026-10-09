using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using Duplify.Models;
using Duplify.Services;
using Duplify.Utilities;

namespace Duplify.ViewModels;

public partial class MainViewModel
{
    // --- File Actions ---

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        if (!CanPerformActions) return;

        var checkedFiles = GetCheckedFiles();
        if (checkedFiles.Count == 0)
        {
            _dialogService.ShowInfo("Не выбрано ни одного файла для удаления.\nОтметьте файлы флажками или используйте кнопки «Оставить самый новый / старый».", "Удаление");
            return;
        }

        // Last-copy loss is never an override. Each operation must leave a verified,
        // unselected physical copy; checkbox changes during awaits do not change this plan.
        var lastCopyGroups = SafetyGuards.FindGroupsLosingAllCopies(Groups);
        if (lastCopyGroups.Count > 0)
        {
            _dialogService.ShowInfo("В этих группах отмечены все копии:\n\n" + BuildGroupList(lastCopyGroups)
                + "\n\nСнимите отметку хотя бы с одной копии в каждой группе. Удаление последней копии запрещено.",
                "Нужно сохранить копию");
            return;
        }

        var retainedPaths = SnapshotRetainedPaths(checkedFiles);
        IsResolving = true;
        using var actionCts = new CancellationTokenSource();
        _actionCts = actionCts;
        try
        {

            long totalSize = checkedFiles.Sum(f => f.File.Size);
            string sizeStr = FormatUtils.FormatBytes(totalSize);
            string filesPlural = FormatUtils.PluralizeFiles(checkedFiles.Count);
            string pathList = BuildPathList(checkedFiles.Select(f => f.File.FilePath), 15);

            string message =
                $"Вы действительно хотите удалить {filesPlural} общим объёмом {sizeStr}?\n\n" +
                "Файлы будут отправлены в Корзину. Постоянное удаление возможно только после отдельного подтверждения.\n\n" +
                pathList;

            if (!_dialogService.ConfirmDanger(message, "Подтверждение удаления"))
            {
                return;
            }

            int deletedCount = 0;
            int skippedCount = 0;
            var failMessages = new List<string>();
            var safetyMessages = new List<string>();

            try
            {
                foreach (var (group, file) in checkedFiles)
                {
                    if (actionCts.IsCancellationRequested) break;
                    string path = file.FilePath;

                    // Recycle Bin uses a UI-capable API (UIOption.OnlyErrorDialogs), so it must
                    // stay on the WPF dispatcher. Permanent File.Delete below may run off-UI.
                    var recycleResult = await ExecuteGuardedAsync(group, file, retainedPaths[group],
                        () => TryRecycle(path), actionCts.Token);
                    if (recycleResult.GuardRejected)
                    {
                        skippedCount++;
                        RecordSafetySkip(file, recycleResult.Error!, "delete", safetyMessages);
                        continue;
                    }
                    if (recycleResult.Success)
                    {
                        deletedCount++;
                        RemoveFileFromGroup(group, file);
                        if (deletedCount % 5 == 0)
                        {
                            await Dispatcher.Yield(DispatcherPriority.Background);
                        }
                        continue;
                    }

                    // Recycle Bin failed: separate, harsher confirmation. No silent File.Delete.
                    bool permanent = _dialogService.ConfirmPermanentDelete(
                        "Не удалось отправить файл в Корзину.\n\n" +
                        $"Путь: {path}\n" +
                        $"Причина: {recycleResult.Error}\n\n" +
                        "Удалить файл НАВСЕГДА, минуя Корзину? Это действие необратимо.",
                        "Окончательное удаление");

                    if (!permanent)
                    {
                        skippedCount++;
                        _journal.Record("delete_skipped", path, false, "Пользователь отказался от постоянного удаления", recycleResult.Error);
                        continue;
                    }

                    // The modal confirmation may be open for minutes. Re-hash and re-lock both
                    // files afterwards, rather than trusting the pre-Recycle-Bin check.
                    var permanentResult = await Task.Run(() => ExecuteGuardedAsync(group, file,
                        retainedPaths[group], () => TryPermanent(path), actionCts.Token));
                    if (permanentResult.GuardRejected)
                    {
                        skippedCount++;
                        RecordSafetySkip(file, permanentResult.Error!, "delete", safetyMessages);
                        continue;
                    }
                    if (permanentResult.Success)
                    {
                        deletedCount++;
                        RemoveFileFromGroup(group, file);
                    }
                    else
                    {
                        failMessages.Add($"{file.FileName}: {permanentResult.Error}");
                    }

                    if (deletedCount % 5 == 0)
                    {
                        await Dispatcher.Yield(DispatcherPriority.Background);
                    }
                }

            }
            catch (OperationCanceledException) when (actionCts.IsCancellationRequested)
            {
                // Cancellation stops before the next mutation; an already completed action is kept.
            }

            CleanupGroups();
            RecalculateTotals();

            if (actionCts.IsCancellationRequested)
                skippedCount = checkedFiles.Count - deletedCount - failMessages.Count;

            ShowResultSummary("Удалено", checkedFiles.Count, deletedCount, skippedCount, failMessages, "Результат удаления", safetyMessages);
        }
        finally
        {
            _actionCts = null;
            IsResolving = false;
        }
    }

    [RelayCommand]
    private async Task MoveSelectedAsync()
    {
        if (!CanPerformActions) return;

        var checkedFiles = GetCheckedFiles();
        if (checkedFiles.Count == 0)
        {
            _dialogService.ShowInfo("Не выбрано ни одного файла для перемещения.\nОтметьте файлы флажками.", "Перемещение");
            return;
        }

        var retainedPaths = SnapshotRetainedPaths(checkedFiles);
        IsResolving = true;
        using var actionCts = new CancellationTokenSource();
        _actionCts = actionCts;
        try
        {
            string? destFolder = _dialogService.PickFolder("Выберите папку для перемещения выбранных файлов");
            if (string.IsNullOrWhiteSpace(destFolder))
            {
                return;
            }

            if (IsDestinationInsideScannedSet(destFolder) &&
                !_dialogService.ConfirmDanger(
                    "Папка назначения находится внутри уже просканированной папки:\n\n" +
                    destFolder + "\n\n" +
                    "Перемещённые файлы останутся в текущем наборе и могут снова попасть в группы при следующем сканировании. " +
                    "Продолжить перемещение?",
                    "Папка назначения внутри набора"))
            {
                return;
            }

            int movedCount = 0;
            int skippedCount = 0;
            var failMessages = new List<string>();
            var safetyMessages = new List<string>();

            try
            {
                foreach (var (group, file) in checkedFiles)
                {
                    if (actionCts.IsCancellationRequested) break;
                    string path = file.FilePath;
                    var result = await Task.Run(() => ExecuteGuardedAsync(group, file, retainedPaths[group],
                        () =>
                        {
                            var moved = TryMove(path, destFolder);
                            return (moved.Success, moved.Error);
                        }, actionCts.Token));

                    if (result.GuardRejected)
                    {
                        skippedCount++;
                        RecordSafetySkip(file, result.Error!, "move", safetyMessages);
                        continue;
                    }

                    if (result.Success)
                    {
                        movedCount++;
                        RemoveFileFromGroup(group, file);
                    }
                    else if (!string.IsNullOrEmpty(result.Error))
                    {
                        failMessages.Add($"{file.FileName}: {result.Error}");
                    }
                    else
                    {
                        skippedCount++;
                    }

                    if (movedCount % 5 == 0)
                    {
                        await Dispatcher.Yield(DispatcherPriority.Background);
                    }
                }

            }
            catch (OperationCanceledException) when (actionCts.IsCancellationRequested)
            {
            }

            CleanupGroups();
            RecalculateTotals();

            if (actionCts.IsCancellationRequested)
                skippedCount = checkedFiles.Count - movedCount - failMessages.Count;

            ShowResultSummary("Перемещено", checkedFiles.Count, movedCount, skippedCount, failMessages, "Результат перемещения", safetyMessages);
        }
        finally
        {
            _actionCts = null;
            IsResolving = false;
        }
    }

    private static Dictionary<DuplicateGroup, string[]> SnapshotRetainedPaths(
        IReadOnlyList<(DuplicateGroup Group, DuplicateFileItem File)> selected)
    {
        var selectedPaths = selected.Select(item => Path.GetFullPath(item.File.FilePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return selected.Select(item => item.Group).Distinct().ToDictionary(group => group,
            group => group.Files.Select(file => file.FilePath)
                .Where(path => !selectedPaths.Contains(Path.GetFullPath(path))).ToArray());
    }

    private async Task<(bool Success, string? Error, bool GuardRejected)> ExecuteGuardedAsync(
        DuplicateGroup group, DuplicateFileItem file, string[] retainedPaths,
        Func<(bool Success, string? Error)> action, CancellationToken cancellationToken)
    {
        using var lease = await Task.Run(() => _duplicateActionGuard.AcquireAsync(file.FilePath, group.FileSize,
            group.Hash, retainedPaths, cancellationToken), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!lease.TryConfirmCurrent(out string? error)) return (false, error, true);
        cancellationToken.ThrowIfCancellationRequested();
        var result = action();
        return (result.Success, result.Error, false);
    }

    private void RecordSafetySkip(DuplicateFileItem file, string error, string action, List<string> messages)
    {
        _journal.Record("safety_skip", file.FilePath, false, error, action);
        messages.Add($"{file.FileName}: пропущен — {error}");
    }

    internal bool IsDestinationInsideScannedSet(string destinationFolder)
    {
        return SafetyGuards.IsDestinationInsideScannedSet(destinationFolder, _lastSuccessfulScanRoots, Groups);
    }

    private void ShowResultSummary(string resultLabel, int total, int succeeded, int skipped, List<string> failures, string title,
        IReadOnlyList<string>? skippedReasons = null)
    {
        string summary = ActionSummary.Build(resultLabel, total, succeeded, skipped, failures);
        if (skippedReasons is { Count: > 0 })
            summary += "\n\nПропущены после повторной проверки:\n" + string.Join("\n", skippedReasons.Take(15));
        bool openJournal = _dialogService.ShowActionSummary(summary, title);

        if (openJournal)
        {
            OpenJournalFile();
        }
    }

    private void OpenJournalFile()
    {
        string journalPath = _journal.JournalFilePath;

        try
        {
            string? directory = Path.GetDirectoryName(journalPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (!File.Exists(journalPath))
            {
                File.WriteAllText(journalPath, string.Empty);
            }

            if (!_fileActionService.OpenJournal(journalPath))
            {
                _dialogService.ShowError(
                    $"Не удалось открыть журнал ни связанным приложением, ни Блокнотом:\n{journalPath}",
                    "Журнал действий");
            }
        }
        catch (Exception ex)
        {
            _dialogService.ShowError(
                $"Не удалось открыть журнал:\n{journalPath}\n\n{ex.Message}",
                "Журнал действий");
        }
    }

    [RelayCommand]
    private void OpenJournal()
    {
        OpenJournalFile();
    }

    [RelayCommand]
    private void OpenLogsFolder()
    {
        try
        {
            string logsDirectory = AppInfo.EnsureLogsDirectory();
            if (!_fileActionService.OpenFolder(logsDirectory))
            {
                _dialogService.ShowError(
                    $"Не удалось открыть папку журналов ошибок:\n{logsDirectory}",
                    "Журналы ошибок");
            }
        }
        catch (Exception ex)
        {
            _dialogService.ShowError(
                $"Не удалось открыть папку журналов ошибок:\n{AppInfo.LogsDirectory}\n\n{ex.Message}",
                "Журналы ошибок");
        }
    }

    [RelayCommand]
    private void ShowAbout()
    {
        string message =
            $"{AppInfo.Name} {AppInfo.Version}\n\n" +
            "Локальное приложение для поиска дубликатов файлов.\n" +
            "Работает полностью на этом компьютере: без сети, облака и телеметрии.\n\n" +
            "Удаление файлов выполняется в Корзину.\n" +
            "Безвозвратное удаление возможно только после отдельного подтверждения.";

        _dialogService.ShowInfo(message, "О программе");
    }

    private void OnJournalWriteFailed(string message)
    {
        _dialogService.ShowError(
            message + "\n\nДействие с файлами не отменено, но журнал недоступен.",
            "Журнал действий");
    }

    private (bool Success, string? Error) TryRecycle(string path)
    {
        return _fileActionService.TrySendToRecycleBin(path, out string? error)
            ? (true, null)
            : (false, error);
    }

    private (bool Success, string? Error) TryPermanent(string path)
    {
        return _fileActionService.DeletePermanently(path, out string? error)
            ? (true, null)
            : (false, error);
    }

    private (bool Success, string FinalPath, string? Error) TryMove(string path, string destinationFolder)
    {
        bool moved = _fileActionService.MoveFile(path, destinationFolder, out string finalPath, out string? error);
        return (moved, finalPath, error);
    }

    private void RemoveFileFromGroup(DuplicateGroup group, DuplicateFileItem file)
    {
        group.Files.Remove(file);
        group.UpdateMetrics();
    }

    private static string BuildPathList(IEnumerable<string> paths, int maxShown)
    {
        var list = paths.ToList();
        var builder = new StringBuilder();

        int shown = Math.Min(list.Count, maxShown);
        for (int i = 0; i < shown; i++)
        {
            builder.AppendLine(list[i]);
        }

        if (list.Count > shown)
        {
            builder.AppendLine($"и ещё {list.Count - shown}");
        }

        return builder.ToString().TrimEnd();
    }

    private static string BuildGroupList(IReadOnlyList<DuplicateGroup> groups)
    {
        var builder = new StringBuilder();

        int shown = Math.Min(groups.Count, 15);
        for (int i = 0; i < shown; i++)
        {
            var group = groups[i];
            builder.AppendLine($"• {group.SampleFileName} — {FormatUtils.PluralizeFiles(group.Files.Count)}");
        }

        if (groups.Count > shown)
        {
            builder.AppendLine($"и ещё {groups.Count - shown}");
        }

        return builder.ToString().TrimEnd();
    }

    private List<(DuplicateGroup Group, DuplicateFileItem File)> GetCheckedFiles()
    {
        var list = new List<(DuplicateGroup Group, DuplicateFileItem File)>();
        foreach (var group in Groups)
        {
            foreach (var file in group.Files)
            {
                if (file.IsChecked)
                {
                    list.Add((group, file));
                }
            }
        }
        return list;
    }

    private void CleanupGroups()
    {
        var toRemove = Groups.Where(g => g.Files.Count < 2).ToList();
        foreach (var group in toRemove)
        {
            Groups.Remove(group);
        }

        if (SelectedGroup != null && !Groups.Contains(SelectedGroup))
        {
            SelectedGroup = Groups.FirstOrDefault();
        }

        UpdateAmbiguousState();
    }

    private void RecalculateTotals()
    {
        TotalGroupsCount = Groups.Count;
        TotalWastedSpace = Groups.Sum(g => g.WastedSpace);
        TotalWastedSpaceFormatted = FormatUtils.FormatBytes(TotalWastedSpace);
        OnPropertyChanged(nameof(CanPerformActions));
        OnPropertyChanged(nameof(CanBulkResolve));
        OnPropertyChanged(nameof(HasNoDuplicates));
    }

    [RelayCommand]
    private void OpenInExplorer(DuplicateFileItem? file)
    {
        if (file != null)
        {
            _fileActionService.OpenInExplorer(file.FilePath);
        }
    }

    [RelayCommand]
    private void OpenFile(DuplicateFileItem? file)
    {
        if (file != null)
        {
            _fileActionService.OpenFile(file.FilePath);
        }
    }

    [RelayCommand]
    private void CopyFilePath(DuplicateFileItem? file)
    {
        if (file != null)
        {
            try
            {
                System.Windows.Clipboard.SetText(file.FilePath);
            }
            catch
            {
                // Ignore clipboard locking issue
            }
        }
    }

    [RelayCommand]
    private void ToggleErrorsPanel()
    {
        ShowErrorsPanel = !ShowErrorsPanel;
    }
}
