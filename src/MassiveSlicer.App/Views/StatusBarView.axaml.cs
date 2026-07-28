using Avalonia.Controls;
using Avalonia.Input;
using MassiveSlicer.ViewModels;

namespace MassiveSlicer.App.Views;

public partial class StatusBarView : UserControl
{
    public StatusBarView() => InitializeComponent();

    /// <summary>Click the build label to copy it (for pasting into a message to the team).</summary>
    private async void OnBuildLabelTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not StatusBarViewModel vm) return;
        await vm.CopyBuildLabelAsync(TopLevel.GetTopLevel(this)?.Clipboard);
    }
}
