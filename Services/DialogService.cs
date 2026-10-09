using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using Duplify.Models;
using Duplify.ViewModels;
using Microsoft.Win32;

namespace Duplify.Services;

public interface IDialogService
{
    string? PickFolder(string title, string? initialFolder = null);
    bool Confirm(string message, string title);
    bool ConfirmDanger(string message, string title);
    bool ConfirmLastCopy(string message, string title);
    bool ConfirmPermanentDelete(string message, string title);
    void ShowInfo(string message, string title);
    void ShowError(string message, string title);

    /// <summary>
    /// Shows the result summary and returns true when the user asked to open the action journal.
    /// </summary>
    bool ShowActionSummary(string message, string title);

    /// <summary>
    /// Shows the modal bulk-resolve window. Returns the applied plan, or null when the user cancels.
    /// </summary>
    BulkSelectionPlan? ShowBulkResolve(IReadOnlyList<DuplicateGroup> groups);
}

public class DialogService : IDialogService
{
    public string? PickFolder(string title, string? initialFolder = null)
    {
        return InvokeOnDispatcher(() =>
        {
            try
            {
                var dialog = new OpenFolderDialog
                {
                    Title = title,
                    Multiselect = false
                };

                if (!string.IsNullOrWhiteSpace(initialFolder) && Directory.Exists(initialFolder))
                {
                    dialog.InitialDirectory = initialFolder;
                }

                if (dialog.ShowDialog() == true)
                {
                    return dialog.FolderName;
                }
            }
            catch (Exception ex)
            {
                ShowError($"Не удалось открыть окно выбора папки: {ex.Message}", "Ошибка");
            }

            return null;
        });
    }

    public bool Confirm(string message, string title)
    {
        return InvokeOnDispatcher(() =>
            MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);
    }

    public bool ConfirmDanger(string message, string title)
    {
        return InvokeOnDispatcher(() =>
            MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes);
    }

    public bool ConfirmLastCopy(string message, string title)
    {
        return InvokeOnDispatcher(() =>
            MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Stop) == MessageBoxResult.Yes);
    }

    public bool ConfirmPermanentDelete(string message, string title)
    {
        return InvokeOnDispatcher(() =>
            MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Stop, MessageBoxResult.No) == MessageBoxResult.Yes);
    }

    public void ShowInfo(string message, string title)
    {
        InvokeOnDispatcher(() =>
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
            return true;
        });
    }

    public void ShowError(string message, string title)
    {
        InvokeOnDispatcher(() =>
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
            return true;
        });
    }

    public bool ShowActionSummary(string message, string title)
    {
        return InvokeOnDispatcher(() =>
        {
            var dialog = new ActionSummaryDialog(title, message)
            {
                Owner = Application.Current?.MainWindow
            };

            dialog.ShowDialog();
            return dialog.OpenJournalRequested;
        });
    }

    public BulkSelectionPlan? ShowBulkResolve(IReadOnlyList<DuplicateGroup> groups)
    {
        return InvokeOnDispatcher(() =>
        {
            var viewModel = new BulkResolveViewModel(groups, this);
            var dialog = new BulkResolveDialog(viewModel)
            {
                Owner = Application.Current?.MainWindow
            };

            dialog.ShowDialog();
            return dialog.AppliedPlan;
        });
    }

    private static T InvokeOnDispatcher<T>(Func<T> action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            return action();
        }

        return dispatcher.Invoke(action);
    }
}
