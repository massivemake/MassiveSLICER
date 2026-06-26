using Avalonia.Controls;
using Avalonia.Input;
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