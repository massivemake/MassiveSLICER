using Avalonia.Controls;
using Avalonia.Input;

namespace MassiveSlicer.App.Views;

public partial class RightPanelView : UserControl
{
    public RightPanelView() => InitializeComponent();

    private void JointAngle_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            e.Handled = true;
    }
}
