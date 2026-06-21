using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using MassiveSlicer.ViewModels;

namespace MassiveSlicer.App.Views;

/// <summary>
/// Fixed 56px track cell: rivet aligned to connector + floats that overflow upward (Pick/Deposit, playback).
/// </summary>
public partial class Lfam3WorkflowPhaseColumn : UserControl
{
    public static readonly StyledProperty<ToolChangePanelBinding?> ToolPanelProperty =
        AvaloniaProperty.Register<Lfam3WorkflowPhaseColumn, ToolChangePanelBinding?>(nameof(ToolPanel));

    public static readonly StyledProperty<string> PhaseTitleProperty =
        AvaloniaProperty.Register<Lfam3WorkflowPhaseColumn, string>(nameof(PhaseTitle), "");

    public static readonly StyledProperty<string> PhaseToolTipProperty =
        AvaloniaProperty.Register<Lfam3WorkflowPhaseColumn, string>(nameof(PhaseToolTip), "");

    public static readonly StyledProperty<string> PhaseIconProperty =
        AvaloniaProperty.Register<Lfam3WorkflowPhaseColumn, string>(nameof(PhaseIcon), "mdi-printer-3d-nozzle");

    public static readonly StyledProperty<ICommand?> SelectPhaseCommandProperty =
        AvaloniaProperty.Register<Lfam3WorkflowPhaseColumn, ICommand?>(nameof(SelectPhaseCommand));

    public static readonly StyledProperty<bool> IsStepActiveProperty =
        AvaloniaProperty.Register<Lfam3WorkflowPhaseColumn, bool>(nameof(IsStepActive));

    public static readonly StyledProperty<bool> IsStepCompletedProperty =
        AvaloniaProperty.Register<Lfam3WorkflowPhaseColumn, bool>(nameof(IsStepCompleted));

    public static readonly StyledProperty<bool> IsStepPendingProperty =
        AvaloniaProperty.Register<Lfam3WorkflowPhaseColumn, bool>(nameof(IsStepPending));

    public static readonly StyledProperty<bool> IsDetailExpandedProperty =
        AvaloniaProperty.Register<Lfam3WorkflowPhaseColumn, bool>(nameof(IsDetailExpanded));

    public static readonly StyledProperty<bool> ShowPlaybackProperty =
        AvaloniaProperty.Register<Lfam3WorkflowPhaseColumn, bool>(nameof(ShowPlayback));

    public static readonly StyledProperty<object?> DetailContentProperty =
        AvaloniaProperty.Register<Lfam3WorkflowPhaseColumn, object?>(nameof(DetailContent));

    public static readonly StyledProperty<IDataTemplate?> DetailTemplateProperty =
        AvaloniaProperty.Register<Lfam3WorkflowPhaseColumn, IDataTemplate?>(nameof(DetailTemplate));

    public ToolChangePanelBinding? ToolPanel
    {
        get => GetValue(ToolPanelProperty);
        set => SetValue(ToolPanelProperty, value);
    }

    public string PhaseTitle
    {
        get => GetValue(PhaseTitleProperty);
        set => SetValue(PhaseTitleProperty, value);
    }

    public string PhaseToolTip
    {
        get => GetValue(PhaseToolTipProperty);
        set => SetValue(PhaseToolTipProperty, value);
    }

    public string PhaseIcon
    {
        get => GetValue(PhaseIconProperty);
        set => SetValue(PhaseIconProperty, value);
    }

    public ICommand? SelectPhaseCommand
    {
        get => GetValue(SelectPhaseCommandProperty);
        set => SetValue(SelectPhaseCommandProperty, value);
    }

    public bool IsStepActive
    {
        get => GetValue(IsStepActiveProperty);
        set => SetValue(IsStepActiveProperty, value);
    }

    public bool IsStepCompleted
    {
        get => GetValue(IsStepCompletedProperty);
        set => SetValue(IsStepCompletedProperty, value);
    }

    public bool IsStepPending
    {
        get => GetValue(IsStepPendingProperty);
        set => SetValue(IsStepPendingProperty, value);
    }

    public bool IsDetailExpanded
    {
        get => GetValue(IsDetailExpandedProperty);
        set => SetValue(IsDetailExpandedProperty, value);
    }

    public bool ShowPlayback
    {
        get => GetValue(ShowPlaybackProperty);
        set => SetValue(ShowPlaybackProperty, value);
    }

    public object? DetailContent
    {
        get => GetValue(DetailContentProperty);
        set => SetValue(DetailContentProperty, value);
    }

    public IDataTemplate? DetailTemplate
    {
        get => GetValue(DetailTemplateProperty);
        set => SetValue(DetailTemplateProperty, value);
    }

    public Lfam3WorkflowPhaseColumn() => InitializeComponent();
}