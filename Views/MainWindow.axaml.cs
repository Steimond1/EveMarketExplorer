using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using EveMarketExplorer.ViewModels;

namespace EveMarketExplorer.Views;

public partial class MainWindow : Window
{
    private CancellationTokenSource? toastCancellation;
    private bool applyingStoredSort;

    public MainWindow()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                ApplyGridSortGlyph(viewModel.CurrentSortState);
            }
        };
    }

    private async void CopyButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string text } ||
            string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            return;
        }

        await ClipboardExtensions.SetTextAsync(clipboard, text);
        await ShowCopiedToastAsync(text);
    }

    private async Task ShowCopiedToastAsync(string text)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        toastCancellation?.Cancel();
        toastCancellation?.Dispose();
        toastCancellation = new CancellationTokenSource();
        var token = toastCancellation.Token;

        viewModel.Status = $"Скопировано: {text}";
        viewModel.ToastMessage = "Скопировано в буфер";
        viewModel.IsToastVisible = true;

        try
        {
            await Task.Delay(1600, token);
            viewModel.IsToastVisible = false;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OpportunitiesGrid_Sorting(object? sender, DataGridColumnEventArgs e)
    {
        if (applyingStoredSort)
        {
            return;
        }

        if (DataContext is not MainWindowViewModel viewModel ||
            string.IsNullOrWhiteSpace(e.Column.SortMemberPath))
        {
            return;
        }

        viewModel.RememberSortBy(e.Column.SortMemberPath);
    }

    private void ApplyGridSortGlyph(TableSortState sortState)
    {
        var direction = sortState.Descending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;

        applyingStoredSort = true;
        try
        {
            foreach (var column in OpportunitiesGrid.Columns)
            {
                if (column.SortMemberPath == sortState.SortMemberPath)
                {
                    column.Sort(direction);
                }
                else
                {
                    column.ClearSort();
                }
            }
        }
        finally
        {
            applyingStoredSort = false;
        }
    }
}
