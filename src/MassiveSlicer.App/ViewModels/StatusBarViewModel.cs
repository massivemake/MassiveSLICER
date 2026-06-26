using MassiveSlicer.ViewModels.Base;

namespace MassiveSlicer.ViewModels;

/// <summary>
/// Provides the text displayed in the 24 px bottom status bar.
/// Other ViewModels write to this via the root <see cref="MainWindowViewModel"/>
/// to show file status and transient operation feedback.
/// </summary>
public sealed class StatusBarViewModel : ViewModelBase
{
    private string _fileStatus = "No file loaded";

    /// <summary>Current file status shown on the left of the status bar.</summary>
    public string FileStatus
    {
        get => _fileStatus;
        set => SetField(ref _fileStatus, value);
    }

    private string _operationFeedback = string.Empty;

    /// <summary>Transient message shown on the right (e.g., "Slice complete -- 42 passes").</summary>
    public string OperationFeedback
    {
        get => _operationFeedback;
        set => SetField(ref _operationFeedback, value);
    }

    private bool _isProgressActive;

    /// <summary>True while a long-running operation is in progress (shows the footer progress line).</summary>
    public bool IsProgressActive
    {
        get => _isProgressActive;
        set => SetField(ref _isProgressActive, value);
    }
}
