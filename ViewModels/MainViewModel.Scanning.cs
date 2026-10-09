using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Duplify.Models;
using Duplify.Services;
using Duplify.Utilities;

namespace Duplify.ViewModels;

public partial class MainViewModel
{
    [RelayCommand]
    private void BrowseFolder()
    {
        string? picked = _dialogService.PickFolder("Выберите первую папку", SelectedFolder);
        if (!string.IsNullOrWhiteSpace(picked))
        {
            SelectedFolder = picked;
            SaveSettings();
        }
    }

    [RelayCommand]
    private void BrowseSecondFolder()
    {
        string? picked = _dialogService.PickFolder("Выберите вторую папку", SelectedSecondFolder);
        if (!string.IsNullOrWhiteSpace(picked))
        {
            SelectedSecondFolder = picked;
            SaveSettings();
        }
    }

    [RelayCommand]
    private void ClearSecondFolder()
    {
        SelectedSecondFolder = string.Empty;
        OnlyCrossRootDuplicates = false;
        SaveSettings();
    }

    private bool ConfirmScanRoot(string folder)
    {
        if (SafetyGuards.IsReparsePoint(folder))
        {
            return _dialogService.ConfirmDanger(
                "Выбранная папка является ярлыком (junction/symlink):\n\n" +
                folder + "\n\n" +
                "Сканирование пойдёт по её целевой папке, которая может находиться вне выбранного расположения. Продолжить?",
                "Папка-ярлык");
        }

        if (SafetyGuards.IsDangerousScanRoot(folder))
        {
            return _dialogService.ConfirmDanger(
                "Выбран корень диска или папка профиля пользователя:\n\n" +
                folder + "\n\n" +
                "Сканирование может занять очень долго и затронуть системные файлы. Продолжить?",
                "Опасный путь сканирования");
        }

        return true;
    }

    [RelayCommand]
    public async Task StartScanAsync()
    {
        // F5 invokes the command directly, independently of the button's CanScan binding.
        if (IsResolving) return;
        if (IsScanning)
        {
            // F5 restart: cancel current scan first
            CancelScan();
            await Task.Delay(100);
            // Do not replace the CTS/results while the cancelled worker still owns them.
            if (IsScanning || IsResolving) return;
        }

        if (string.IsNullOrWhiteSpace(SelectedFolder) || !Directory.Exists(SelectedFolder))
        {
            _dialogService.ShowError("Указанная папка не существует или путь пуст.", "Ошибка");
            return;
        }

        bool hasSecond = !string.IsNullOrWhiteSpace(SelectedSecondFolder);
        if (hasSecond && !Directory.Exists(SelectedSecondFolder))
        {
            _dialogService.ShowError("Вторая папка не существует.", "Ошибка");
            return;
        }

        if (hasSecond &&
            string.Equals(
                Path.GetFullPath(SelectedFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(SelectedSecondFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            _dialogService.ShowError("Первая и вторая папки совпадают. Укажите разные пути или очистите вторую папку.", "Ошибка");
            return;
        }

        if (!ConfirmScanRoot(SelectedFolder))
        {
            return;
        }

        if (hasSecond && !ConfirmScanRoot(SelectedSecondFolder))
        {
            return;
        }

        SaveSettings();

        IsScanning = true;
        Groups.Clear();
        Errors.Clear();
        SelectedGroup = null;
        TotalFilesScanned = 0;
        TotalGroupsCount = 0;
        TotalWastedSpace = 0;
        TotalWastedSpaceFormatted = "0 Б";
        ShowErrorsPanel = false;

        _ambiguousGroups.Clear();
        _ambiguousVisited = 0;
        UpdateAmbiguousState();
        RecalculateSelectionTotals();

        FilesScanned = 0;
        CurrentScanPath = SelectedFolder;
        ScanPhase = "Подготовка...";
        HashProgressCurrent = 0;
        HashProgressTotal = 0;
        HashBytesProcessed = 0;
        HashBytesTotal = 0;
        HashBytesProgressFormatted = string.Empty;
        HashProgressPercent = 0;
        IsIndeterminateProgress = true;

        _scanCts = new CancellationTokenSource();
        _scanStopwatch = Stopwatch.StartNew();
        _elapsedTimer.Start();

        var options = new ScanOptions
        {
            RootPath = SelectedFolder,
            SecondRootPath = SelectedSecondFolder,
            OnlyCrossRootDuplicates = OnlyCrossRootDuplicates && hasSecond,
            IncludeSubdirectories = IncludeSubdirectories,
            FileMask = FileMask,
            MinSizeKb = MinSizeKb,
            ExcludeSystemFolders = ExcludeSystemFolders,
            ExcludeFolderNames = ParseExcludeFolderNames(ExcludeFolderNames)
        };

        var progress = new Progress<ScanProgress>(p =>
        {
            FilesScanned = p.FilesScanned;
            CurrentScanPath = p.CurrentPath;
            ScanPhase = p.Phase;
            HashProgressCurrent = p.HashProgressCurrent;
            HashProgressTotal = p.HashProgressTotal;
            HashBytesProcessed = p.HashBytesProcessed;
            HashBytesTotal = p.HashBytesTotal;

            if (p.HashBytesTotal > 0)
            {
                IsIndeterminateProgress = false;
                HashProgressPercent = Math.Round((double)p.HashBytesProcessed / p.HashBytesTotal * 100.0, 1);
                HashBytesProgressFormatted = $"{FormatUtils.FormatBytes(p.HashBytesProcessed)} / {FormatUtils.FormatBytes(p.HashBytesTotal)}";
            }
            else if (p.HashProgressTotal > 0)
            {
                IsIndeterminateProgress = false;
                HashProgressPercent = Math.Round((double)p.HashProgressCurrent / p.HashProgressTotal * 100.0, 1);
                HashBytesProgressFormatted = $"{p.HashProgressCurrent} / {p.HashProgressTotal}";
            }
            else
            {
                IsIndeterminateProgress = true;
                HashProgressPercent = 0;
                HashBytesProgressFormatted = string.Empty;
            }
        });

        try
        {
            // Let the UI paint "scanning" before disk work starts.
            await Task.Yield();
            var result = await _scannerService.ScanAsync(options, progress, _scanCts.Token);

            foreach (var group in result.Groups)
            {
                Groups.Add(group);
            }

            foreach (var err in result.Errors)
            {
                Errors.Add(err);
            }

            TotalFilesScanned = result.TotalFilesScanned;
            RecalculateTotals();

            if (result.IsCancelled)
            {
                ScanPhase = "Сканирование отменено";
            }
            else
            {
                // Only a completed scan that produced this result set becomes the reference tree.
                _lastSuccessfulScanRoots.Clear();
                _lastSuccessfulScanRoots.Add(Path.GetFullPath(options.RootPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (!string.IsNullOrWhiteSpace(options.SecondRootPath) && Directory.Exists(options.SecondRootPath))
                {
                    _lastSuccessfulScanRoots.Add(Path.GetFullPath(options.SecondRootPath)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                }
            }

            if (Groups.Count > 0)
            {
                SelectedGroup = Groups[0];
            }
        }
        catch (OperationCanceledException)
        {
            ScanPhase = "Сканирование отменено";
        }
        catch (Exception ex)
        {
            _dialogService.ShowError($"Ошибка при сканировании: {ex.Message}", "Ошибка");
        }
        finally
        {
            _elapsedTimer.Stop();
            if (_scanStopwatch != null)
            {
                _scanStopwatch.Stop();
                ElapsedTimeFormatted = _scanStopwatch.Elapsed.ToString(@"hh\:mm\:ss");
            }

            IsScanning = false;
            HasScanned = true;
            _scanCts?.Dispose();
            _scanCts = null;

            OnPropertyChanged(nameof(CanPerformActions));
            OnPropertyChanged(nameof(HasNoDuplicates));
        }
    }

    [RelayCommand]
    public void CancelScan()
    {
        if (IsResolving)
        {
            _actionCts?.Cancel();
            return;
        }
        if (IsScanning && _scanCts != null && !_scanCts.IsCancellationRequested)
        {
            ScanPhase = "Отмена...";
            _scanCts.Cancel();
        }
    }
}
