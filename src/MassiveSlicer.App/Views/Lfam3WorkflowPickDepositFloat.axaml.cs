using Avalonia;
using Avalonia.Controls;
using MassiveSlicer.ViewModels;

namespace MassiveSlicer.App.Views;

public partial class Lfam3WorkflowPickDepositFloat : UserControl
{
    public static readonly StyledProperty<ToolChangePanelBinding?> ToolPanelProperty =
        AvaloniaProperty.Register<Lfam3WorkflowPickDepositFloat, ToolChangePanelBinding?>(nameof(ToolPanel));

    public ToolChangePanelBinding? ToolPanel
    {
        get => GetValue(ToolPanelProperty);
        set => SetValue(ToolPanelProperty, value);
    }

    public Lfam3WorkflowPickDepositFloat() => InitializeComponent();
}