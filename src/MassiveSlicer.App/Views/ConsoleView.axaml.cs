using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using MassiveSlicer.ViewModels;

namespace MassiveSlicer.App.Views;

public partial class ConsoleView : UserControl
{
    public ConsoleView()
    {
        InitializeComponent();
        ConsoleInput.AddHandler(InputElement.KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>Copies that history line's full text (without the ▶ command prefix).</summary>
    private async void OnCopyLineClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ConsoleHistoryEntry entry } btn) return;
        var top = TopLevel.GetTopLevel(this);
        if (top?.Clipboard is null) return;

        // Prefer raw Text so shader logs paste cleanly; fall back to DisplayLine.
        string text = string.IsNullOrEmpty(entry.Text) ? entry.DisplayLine : entry.Text;
        await top.Clipboard.SetTextAsync(text);

        // Highlight the whole row so it's obvious which line was copied.
        var row = FindAncestorBorder(btn);
        if (row is not null)
        {
            row.Classes.Add("copied");
            _ = ClearCopiedHighlightAsync(row);
        }

        ToolTip.SetTip(btn, "Copied!");
        _ = ResetCopyTipAsync(btn);
    }

    private static Border? FindAncestorBorder(Control from)
    {
        for (var p = from.Parent; p is not null; p = p.Parent)
            if (p is Border b && b.Classes.Contains("ConsoleHistoryRow"))
                return b;
        return null;
    }

    private static async System.Threading.Tasks.Task ClearCopiedHighlightAsync(Border row)
    {
        await System.Threading.Tasks.Task.Delay(700);
        row.Classes.Remove("copied");
    }

    private static async System.Threading.Tasks.Task ResetCopyTipAsync(Control btn)
    {
        await System.Threading.Tasks.Task.Delay(900);
        ToolTip.SetTip(btn, "Copy line");
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is ConsoleViewModel oldVm)
            oldVm.History.CollectionChanged -= OnHistoryChanged;

        if (DataContext is ConsoleViewModel vm)
            vm.History.CollectionChanged += OnHistoryChanged;
    }

    private void OnHistoryChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // Defer until layout completes — immediate ScrollToEnd leaves the newest line under the input.
        Avalonia.Threading.Dispatcher.UIThread.Post(ScrollHistoryToEnd, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    void ScrollHistoryToEnd()
    {
        if (HistoryScroll is null) return;
        HistoryScroll.ScrollToEnd();
        // Second pass after the scroll extent updates (wrap + new lines).
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => HistoryScroll?.ScrollToEnd(), Avalonia.Threading.DispatcherPriority.Background);
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ConsoleViewModel vm)
            return;

        switch (e.Key)
        {
            case Key.Enter:
                if (vm.SubmitCommand.CanExecute(null))
                {
                    vm.SubmitCommand.Execute(null);
                    e.Handled = true;
                }
                break;

            case Key.Tab:
                if (vm.TryCompleteSuggestion())
                    e.Handled = true;
                break;

            case Key.Down:
                if (vm.TryMoveSuggestion(+1))
                    e.Handled = true;
                else if (vm.TryBrowseHistory(-1))
                    e.Handled = true;
                break;

            case Key.Up:
                if (vm.HasSuggestions && vm.TryMoveSuggestion(-1))
                    e.Handled = true;
                else if (vm.TryBrowseHistory(+1))
                    e.Handled = true;
                break;

            case Key.Escape:
                vm.InputText = string.Empty;
                e.Handled = true;
                break;
        }
    }
}