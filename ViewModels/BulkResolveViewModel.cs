using System;
using System.Collections.Generic;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Duplify.Models;
using Duplify.Services;
using Duplify.Utilities;

namespace Duplify.ViewModels;

/// <summary>
/// View model for the compact bulk-resolve window. It owns the user's rule settings, computes a
/// preview and applies the plan (checkbox changes only) when the user confirms.
/// </summary>
public partial class BulkResolveViewModel : ObservableObject
{
    private readonly IReadOnlyList<DuplicateGroup> _groups;
    private readonly IDialogService _dialogService;
    private BulkSelectionPlan? _plan;

    public event EventHandler? ApplyRequested;
    public event EventHandler? CancelRequested;

    public BulkSelectionPlan? AppliedPlan { get; private set; }

    public BulkResolveViewModel(IReadOnlyList<DuplicateGroup> groups, IDialogService dialogService)
    {
        _groups = groups ?? Array.Empty<DuplicateGroup>();
        _dialogService = dialogService;
        ValidateFolder();
    }

    // --- Rule settings ---

    [ObservableProperty]
    private string _preferredFolder = string.Empty;

    [ObservableProperty]
    private bool _keepNewest = true;

    public bool KeepOldest
    {
        get => !KeepNewest;
        set => KeepNewest = !value;
    }

    [ObservableProperty]
    private bool _preferCleanName = true;

    [ObservableProperty]
    private bool _preferShorterPath = true;

    partial void OnKeepNewestChanged(bool value)
    {
        OnPropertyChanged(nameof(KeepOldest));
        RecalculateIfNeeded();
    }

    partial void OnPreferCleanNameChanged(bool value) => RecalculateIfNeeded();

    partial void OnPreferShorterPathChanged(bool value) => RecalculateIfNeeded();

    partial void OnPreferredFolderChanged(string value)
    {
        ValidateFolder();
        RecalculateIfNeeded();
    }

    // --- Validation ---

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    private bool _hasValidationError;

    [ObservableProperty]
    private string _validationMessage = string.Empty;

    // --- Preview ---

    [ObservableProperty]
    private int _processedGroupCount;

    [ObservableProperty]
    private int _markedFileCount;

    [ObservableProperty]
    private long _markedBytes;

    [ObservableProperty]
    private string _markedBytesFormatted = "0 Б";

    [ObservableProperty]
    private int _ambiguousGroupCount;

    [ObservableProperty]
    private int _changedCount;

    [ObservableProperty]
    private string _priorityDescription = string.Empty;

    [ObservableProperty]
    private string _previewText = "Нажмите «Рассчитать», чтобы увидеть предварительный итог.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    private bool _hasCalculated;

    public bool CanApply => HasCalculated && !HasValidationError;

    private void ValidateFolder()
    {
        string folder = (PreferredFolder ?? string.Empty).Trim();

        if (folder.Length == 0)
        {
            HasValidationError = false;
            ValidationMessage = string.Empty;
            return;
        }

        bool exists;
        try
        {
            exists = Directory.Exists(folder);
        }
        catch
        {
            exists = false;
        }

        HasValidationError = !exists;
        ValidationMessage = exists ? string.Empty : $"Папка не найдена: {folder}";
    }

    private void RecalculateIfNeeded()
    {
        if (HasCalculated)
        {
            Calculate();
        }
    }

    // --- Commands ---

    [RelayCommand]
    private void BrowseFolder()
    {
        string? picked = _dialogService.PickFolder("Выберите предпочтительную папку", PreferredFolder);
        if (!string.IsNullOrWhiteSpace(picked))
        {
            PreferredFolder = picked;
        }
    }

    [RelayCommand]
    private void ClearFolder() => PreferredFolder = string.Empty;

    [RelayCommand]
    private void Calculate()
    {
        var options = new BulkSelectionOptions
        {
            PreferredFolder = PreferredFolder ?? string.Empty,
            KeepNewest = KeepNewest,
            PreferCleanName = PreferCleanName,
            PreferShorterPath = PreferShorterPath
        };

        _plan = BulkSelectionPlanner.Build(_groups, options);

        ProcessedGroupCount = _plan.ProcessedGroupCount;
        MarkedFileCount = _plan.MarkedFileCount;
        MarkedBytes = _plan.MarkedBytes;
        MarkedBytesFormatted = FormatUtils.FormatBytes(_plan.MarkedBytes);
        AmbiguousGroupCount = _plan.AmbiguousGroupCount;
        ChangedCount = _plan.ChangedCount;
        PriorityDescription = _plan.PriorityDescription;

        PreviewText =
            $"Групп будет обработано: {ProcessedGroupCount}\n" +
            $"Файлов будет отмечено: {MarkedFileCount} ({MarkedBytesFormatted})\n" +
            $"Пропущено неоднозначных групп: {AmbiguousGroupCount}\n" +
            $"Изменится отметок: {ChangedCount}";

        HasCalculated = true;
    }

    [RelayCommand]
    private void Apply()
    {
        if (!CanApply)
        {
            return;
        }

        if (_plan == null)
        {
            Calculate();
        }

        if (_plan == null || !BulkSelectionPlanner.TryApply(_plan, _groups))
        {
            _dialogService.ShowError(
                "Список групп изменился с момента расчёта. Выполните расчёт заново.",
                "Массовый разбор");
            HasCalculated = false;
            return;
        }

        AppliedPlan = _plan;
        ApplyRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel() => CancelRequested?.Invoke(this, EventArgs.Empty);
}
