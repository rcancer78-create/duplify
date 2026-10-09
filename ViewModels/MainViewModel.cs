using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Duplify.Models;
using Duplify.Services;
using Duplify.Utilities;

namespace Duplify.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IScannerService _scannerService;
    private readonly IFileActionService _fileActionService;
    private readonly ISettingsService _settingsService;
    private readonly IImageThumbnailService _thumbnailService;
    private readonly IDialogService _dialogService;
    private readonly IActionJournal _journal;
    private readonly DispatcherTimer _elapsedTimer;
    private Stopwatch? _scanStopwatch;
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _actionCts;
    private readonly DuplicateActionGuard _duplicateActionGuard = new();

    // Groups/files whose IsChecked we observe so the global selection totals stay correct no matter
    // how the flags changed. Reconciled on every collection change to avoid leaking handlers.
    private readonly HashSet<DuplicateGroup> _trackedGroups = new();
    private readonly HashSet<DuplicateFileItem> _trackedFiles = new();

    // Ambiguous groups left over from the last applied bulk plan, for "show next ambiguous".
    private readonly List<DuplicateGroup> _ambiguousGroups = new();
    private int _ambiguousVisited;

    // Roots of the last scan that completed and replaced the result set. Kept in memory only so the
    // move-into-scanned-tree warning does not follow the editable folder fields.
    private readonly List<string> _lastSuccessfulScanRoots = new();

    internal string LastSuccessfulScanRoot => _lastSuccessfulScanRoots.Count > 0 ? _lastSuccessfulScanRoots[0] : string.Empty;

    // --- Settings & Scan Options ---

    [ObservableProperty]
    private string _selectedFolder = string.Empty;

    [ObservableProperty]
    private string _selectedSecondFolder = string.Empty;

    [ObservableProperty]
    private bool _onlyCrossRootDuplicates;

    [ObservableProperty]
    private bool _includeSubdirectories = true;

    [ObservableProperty]
    private string _fileMask = "*.*";

    [ObservableProperty]
    private long _minSizeKb = 0;

    partial void OnMinSizeKbChanged(long value)
    {
        if (value < 0)
        {
            MinSizeKb = 0;
        }
    }

    [ObservableProperty]
    private bool _excludeSystemFolders = true;

    [ObservableProperty]
    private string _excludeFolderNames = ".git;node_modules;AppData";

    // --- State & Progress ---

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanScan))]
    [NotifyPropertyChangedFor(nameof(CanPerformActions))]
    [NotifyPropertyChangedFor(nameof(CanBulkResolve))]
    [NotifyPropertyChangedFor(nameof(CanShowNextAmbiguous))]
    [NotifyPropertyChangedFor(nameof(HasNoDuplicates))]
    private bool _isScanning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanScan))]
    [NotifyPropertyChangedFor(nameof(CanPerformActions))]
    [NotifyPropertyChangedFor(nameof(CanBulkResolve))]
    [NotifyPropertyChangedFor(nameof(CanShowNextAmbiguous))]
    private bool _isResolving;

    public bool CanScan => !IsScanning && !IsResolving;
    public bool CanPerformActions => !IsScanning && !IsResolving && Groups.Count > 0;
    public bool CanBulkResolve => !IsScanning && !IsResolving && Groups.Count > 0;
    public bool CanShowNextAmbiguous => !IsScanning && !IsResolving && HasAmbiguousGroups;

    public string AppVersion => AppInfo.Version;

    public string WindowTitle => $"{AppInfo.Name} {AppInfo.Version} — Поиск дубликатов файлов";

    [ObservableProperty]
    private int _filesScanned;

    [ObservableProperty]
    private string _currentScanPath = string.Empty;

    [ObservableProperty]
    private string _scanPhase = string.Empty;

    [ObservableProperty]
    private int _hashProgressCurrent;

    [ObservableProperty]
    private int _hashProgressTotal;

    [ObservableProperty]
    private long _hashBytesProcessed;

    [ObservableProperty]
    private long _hashBytesTotal;

    [ObservableProperty]
    private string _hashBytesProgressFormatted = string.Empty;

    [ObservableProperty]
    private double _hashProgressPercent;

    [ObservableProperty]
    private bool _isIndeterminateProgress = true;

    [ObservableProperty]
    private string _elapsedTimeFormatted = "00:00:00";

    // --- Results & Totals ---

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoDuplicates))]
    private bool _hasScanned;

    public bool HasNoDuplicates => HasScanned && !IsScanning && Groups.Count == 0;

    public ObservableCollection<DuplicateGroup> Groups { get; } = new();

    [ObservableProperty]
    private DuplicateGroup? _selectedGroup;

    [ObservableProperty]
    private int _totalFilesScanned;

    [ObservableProperty]
    private int _totalGroupsCount;

    [ObservableProperty]
    private long _totalWastedSpace;

    [ObservableProperty]
    private string _totalWastedSpaceFormatted = "0 Б";

    public ObservableCollection<ScanError> Errors { get; } = new();

    [ObservableProperty]
    private bool _showErrorsPanel;

    // --- Global selection summary ---

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(SelectedSummary))]
    private int _selectedCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedSummary))]
    private long _selectedBytes;

    public bool HasSelection => SelectedCount > 0;

    public string SelectedSummary =>
        $"Отмечено: {FormatUtils.PluralizeFiles(SelectedCount)}, {FormatUtils.FormatBytes(SelectedBytes)}";

    // --- Ambiguous groups navigation ---

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanShowNextAmbiguous))]
    [NotifyPropertyChangedFor(nameof(AmbiguousSummaryFormatted))]
    private bool _hasAmbiguousGroups;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AmbiguousSummaryFormatted))]
    private int _ambiguousRemaining;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AmbiguousSummaryFormatted))]
    private int _ambiguousTotal;

    public string AmbiguousSummaryFormatted => AmbiguousTotal > 0
        ? $"Неоднозначных: {AmbiguousRemaining} из {AmbiguousTotal}"
        : "Неоднозначных групп нет";

    public MainViewModel(
        IScannerService scannerService,
        IFileActionService fileActionService,
        ISettingsService settingsService,
        IImageThumbnailService thumbnailService,
        IDialogService dialogService,
        IActionJournal journal)
    {
        _scannerService = scannerService;
        _fileActionService = fileActionService;
        _settingsService = settingsService;
        _thumbnailService = thumbnailService;
        _dialogService = dialogService;
        _journal = journal;

        journal.WriteFailed += OnJournalWriteFailed;
        Groups.CollectionChanged += OnGroupsCollectionChanged;

        _elapsedTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _elapsedTimer.Tick += (s, e) =>
        {
            if (_scanStopwatch != null && _scanStopwatch.IsRunning)
            {
                ElapsedTimeFormatted = _scanStopwatch.Elapsed.ToString(@"hh\:mm\:ss");
            }
        };

        LoadSettings();
    }

    private void LoadSettings()
    {
        var settings = _settingsService.LoadSettings();
        SelectedFolder = settings.LastFolder;
        SelectedSecondFolder = settings.LastSecondFolder ?? string.Empty;
        OnlyCrossRootDuplicates = settings.OnlyCrossRootDuplicates;
        IncludeSubdirectories = settings.IncludeSubdirectories;
        FileMask = string.IsNullOrWhiteSpace(settings.FileMask) ? "*.*" : settings.FileMask;
        MinSizeKb = Math.Max(0, settings.MinSizeKb);
        ExcludeSystemFolders = settings.ExcludeSystemFolders;
        ExcludeFolderNames = settings.ExcludeFolderNames ?? ".git;node_modules;AppData";
    }

    public void SaveSettings()
    {
        _settingsService.SaveSettings(new AppSettings
        {
            LastFolder = SelectedFolder,
            LastSecondFolder = SelectedSecondFolder,
            OnlyCrossRootDuplicates = OnlyCrossRootDuplicates,
            IncludeSubdirectories = IncludeSubdirectories,
            FileMask = FileMask,
            MinSizeKb = MinSizeKb,
            ExcludeSystemFolders = ExcludeSystemFolders,
            ExcludeFolderNames = ExcludeFolderNames
        });
    }

    private static List<string> ParseExcludeFolderNames(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new List<string>();
        }

        return text
            .Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }
}
