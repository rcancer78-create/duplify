using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Duplify.Models;

namespace Duplify.ViewModels;

public partial class MainViewModel
{
    // --- Selection tracking ---

    private void OnGroupsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ReconcileGroupSubscriptions();
        RecalculateSelectionTotals();
    }

    private void ReconcileGroupSubscriptions()
    {
        foreach (var group in _trackedGroups.ToList())
        {
            if (!Groups.Contains(group))
            {
                DetachGroup(group);
            }
        }

        foreach (var group in Groups)
        {
            AttachGroup(group);
        }
    }

    private void AttachGroup(DuplicateGroup group)
    {
        if (!_trackedGroups.Add(group))
        {
            return;
        }

        group.Files.CollectionChanged += OnGroupFilesCollectionChanged;
        foreach (var file in group.Files)
        {
            AttachFile(file);
        }
    }

    private void DetachGroup(DuplicateGroup group)
    {
        if (!_trackedGroups.Remove(group))
        {
            return;
        }

        group.Files.CollectionChanged -= OnGroupFilesCollectionChanged;
        foreach (var file in group.Files)
        {
            DetachFile(file);
        }
    }

    private void OnGroupFilesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (sender is DuplicateGroup group)
        {
            if (e.OldItems != null)
            {
                foreach (DuplicateFileItem file in e.OldItems)
                {
                    DetachFile(file);
                }
            }

            if (e.NewItems != null)
            {
                foreach (DuplicateFileItem file in e.NewItems)
                {
                    AttachFile(file);
                }
            }

            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                foreach (var file in _trackedFiles.ToList())
                {
                    if (!Groups.Any(g => g.Files.Contains(file)))
                    {
                        DetachFile(file);
                    }
                }

                foreach (var file in group.Files)
                {
                    AttachFile(file);
                }
            }
        }

        RecalculateSelectionTotals();
    }

    private void AttachFile(DuplicateFileItem file)
    {
        if (_trackedFiles.Add(file))
        {
            file.PropertyChanged += OnFilePropertyChanged;
        }
    }

    private void DetachFile(DuplicateFileItem file)
    {
        if (_trackedFiles.Remove(file))
        {
            file.PropertyChanged -= OnFilePropertyChanged;
        }
    }

    private void OnFilePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DuplicateFileItem.IsChecked))
        {
            RecalculateSelectionTotals();
        }
    }

    private void RecalculateSelectionTotals()
    {
        int count = 0;
        long bytes = 0;

        foreach (var group in Groups)
        {
            foreach (var file in group.Files)
            {
                if (file.IsChecked)
                {
                    count++;
                    bytes += file.Size;
                }
            }
        }

        SelectedCount = count;
        SelectedBytes = bytes;
    }

    // --- Ambiguous groups navigation ---

    private void PruneAmbiguousGroups()
    {
        _ambiguousGroups.RemoveAll(g => !Groups.Contains(g));
    }

    private void SetAmbiguousGroups(IEnumerable<DuplicateGroup> groups)
    {
        _ambiguousGroups.Clear();
        _ambiguousGroups.AddRange(groups);
        _ambiguousVisited = 0;
        UpdateAmbiguousState();
    }

    private void UpdateAmbiguousState()
    {
        PruneAmbiguousGroups();
        AmbiguousTotal = _ambiguousGroups.Count;
        AmbiguousRemaining = Math.Max(0, AmbiguousTotal - _ambiguousVisited);
        HasAmbiguousGroups = AmbiguousTotal > 0;
    }

    [RelayCommand]
    private void KeepNewestInGroup()
    {
        if (SelectedGroup == null || SelectedGroup.Files.Count == 0) return;
        MarkGroupFiles(SelectedGroup, keepNewest: true);
    }

    [RelayCommand]
    private void KeepOldestInGroup()
    {
        if (SelectedGroup == null || SelectedGroup.Files.Count == 0) return;
        MarkGroupFiles(SelectedGroup, keepNewest: false);
    }

    [RelayCommand]
    private void KeepNewestAllGroups()
    {
        foreach (var group in Groups)
        {
            MarkGroupFiles(group, keepNewest: true);
        }
    }

    [RelayCommand]
    private void KeepOldestAllGroups()
    {
        foreach (var group in Groups)
        {
            MarkGroupFiles(group, keepNewest: false);
        }
    }

    [RelayCommand]
    private void SelectAllInGroup()
    {
        if (SelectedGroup == null) return;
        foreach (var f in SelectedGroup.Files)
        {
            f.IsChecked = true;
        }
    }

    [RelayCommand]
    private void UnselectAllInGroup()
    {
        if (SelectedGroup == null) return;
        foreach (var f in SelectedGroup.Files)
        {
            f.IsChecked = false;
        }
    }

    [RelayCommand]
    private void SelectAllInAllGroups()
    {
        foreach (var g in Groups)
        {
            foreach (var f in g.Files)
            {
                f.IsChecked = true;
            }
        }
    }

    [RelayCommand]
    private void UnselectAllInAllGroups()
    {
        foreach (var g in Groups)
        {
            foreach (var f in g.Files)
            {
                f.IsChecked = false;
            }
        }
    }

    [RelayCommand]
    private void ClearAllMarks()
    {
        foreach (var g in Groups)
        {
            foreach (var f in g.Files)
            {
                f.IsChecked = false;
            }
        }
    }

    [RelayCommand]
    private void OpenBulkResolve()
    {
        if (!CanBulkResolve)
        {
            return;
        }

        var plan = _dialogService.ShowBulkResolve(Groups.ToList());
        if (plan == null)
        {
            return;
        }

        // The planner already toggled the checkboxes; remember which groups were left ambiguous.
        SetAmbiguousGroups(plan.AmbiguousGroups.Select(a => a.Group));
        RecalculateSelectionTotals();
    }

    [RelayCommand]
    private void ShowNextAmbiguous()
    {
        if (IsScanning || IsResolving)
        {
            return;
        }

        PruneAmbiguousGroups();

        if (_ambiguousGroups.Count == 0)
        {
            _ambiguousVisited = 0;
            UpdateAmbiguousState();
            _dialogService.ShowInfo(
                "Неоднозначных групп нет. Они появляются после «Массового разбора», если несколько копий имеют одинаковый приоритет.",
                "Массовый разбор");
            return;
        }

        if (_ambiguousVisited >= _ambiguousGroups.Count)
        {
            _ambiguousVisited = 0;
        }

        SelectedGroup = _ambiguousGroups[_ambiguousVisited];
        _ambiguousVisited++;
        UpdateAmbiguousState();
    }

    private static void MarkGroupFiles(DuplicateGroup group, bool keepNewest)
    {
        if (group.Files.Count == 0) return;

        DuplicateFileItem kept = keepNewest
            ? group.Files.OrderByDescending(f => f.LastModifiedUtc).First()
            : group.Files.OrderBy(f => f.LastModifiedUtc).First();

        foreach (var file in group.Files)
        {
            file.IsChecked = (file != kept);
        }
    }
}
