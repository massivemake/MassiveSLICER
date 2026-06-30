using Avalonia.Controls;
using Avalonia.Input;
using MassiveSlicer.ViewModels;

namespace MassiveSlicer.App.Views;

public partial class LeftPanelView : UserControl
{
    public LeftPanelView() => InitializeComponent();

    private void JointAngle_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            e.Handled = true;
    }
}
