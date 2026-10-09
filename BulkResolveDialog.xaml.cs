using System;
using System.Windows;
using Duplify.Services;
using Duplify.ViewModels;

namespace Duplify;

public partial class BulkResolveDialog : Window
{
    private readonly BulkResolveViewModel _viewModel;

    public BulkSelectionPlan? AppliedPlan { get; private set; }

    public BulkResolveDialog(BulkResolveViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;

        _viewModel.ApplyRequested += OnApplyRequested;
        _viewModel.CancelRequested += OnCancelRequested;
    }

    private void OnApplyRequested(object? sender, EventArgs e)
    {
        AppliedPlan = _viewModel.AppliedPlan;
        DialogResult = true;
    }

    private void OnCancelRequested(object? sender, EventArgs e)
    {
        DialogResult = false;
    }
}
