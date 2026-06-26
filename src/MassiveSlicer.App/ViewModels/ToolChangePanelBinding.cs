using System.Windows.Input;
using MassiveSlicer.Commands;
using MassiveSlicer.ViewModels.Base;

namespace MassiveSlicer.ViewModels;

/// <summary>Pick/Deposit UI for one LFAM 3 toolhead (simulate + run-on-robot caret).</summary>
public sealed class ToolChangePanelBinding : ViewModelBase
{
    readonly ViewportViewModel _vm;
    readonly string _pickId;
    readonly string _depositId;

    public ToolChangePanelBinding(
        ViewportViewModel vm,
        string toolHeader,
        string pickId,
        string depositId,
        RelayCommand pickCommand,
        RelayCommand depositCommand)
    {
        _vm            = vm;
        ToolHeader     = toolHeader;
        _pickId        = pickId;
        _depositId     = depositId;
        PickCommand    = pickCommand;
        DepositCommand = depositCommand;
        RunPickCommand    = new RelayCommand(() => { }, () => false);
        RunDepositCommand = new RelayCommand(() => { }, () => false);
    }

    public string ToolHeader { get; }

    public RelayCommand PickCommand { get; }
    public RelayCommand DepositCommand { get; }
    public RelayCommand RunPickCommand { get; }
    public RelayCommand RunDepositCommand { get; }

    public bool IsPickActive    => _vm.ActiveToolChangeSequenceId == _pickId;
    public bool IsDepositActive => _vm.ActiveToolChangeSequenceId == _depositId;

    public bool ShowPlayback =>
        _vm.IsToolChangePlaybackExpanded
        && _vm.ActiveToolChangeSequenceId is not null
        && (_vm.ActiveToolChangeSequenceId == _pickId || _vm.ActiveToolChangeSequenceId == _depositId);

    public string StepText           => _vm.ToolChangeStepText;

    /// <summary>Playback step line without I/O annotations (compact timeline strip).</summary>
    public string StepTextCompact    => _vm.ToolChangeStepTextCompact;
    public int ScrubValue
    {
        get => _vm.ToolChangeScrubValue;
        set => _vm.ToolChangeScrubValue = value;
    }
    public bool IsPlaying              => _vm.ToolChangeIsPlaying;
    public string PlaybackToggleIcon   => _vm.ToolChangePlaybackToggleIcon;
    public RelayCommand TogglePlaybackCommand => _vm.ToggleToolChangePlaybackCommand;
    public RelayCommand CollapsePlaybackCommand => _vm.CollapseToolChangePlaybackCommand;

    public void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(IsPickActive));
        OnPropertyChanged(nameof(IsDepositActive));
        OnPropertyChanged(nameof(ShowPlayback));
        OnPropertyChanged(nameof(StepText));
        OnPropertyChanged(nameof(StepTextCompact));
        OnPropertyChanged(nameof(ScrubValue));
        OnPropertyChanged(nameof(IsPlaying));
        OnPropertyChanged(nameof(PlaybackToggleIcon));
        PickCommand.RaiseCanExecuteChanged();
        DepositCommand.RaiseCanExecuteChanged();
    }
}