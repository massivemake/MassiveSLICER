using Avalonia.Input.Platform;
using Avalonia.Media;
using MassiveSlicer.App;
using MassiveSlicer.ViewModels.Base;

namespace MassiveSlicer.ViewModels;

/// <summary>
/// Provides the text displayed in the 24 px bottom status bar.
/// Other ViewModels write to this via the root <see cref="MainWindowViewModel"/>
/// to show file status and transient operation feedback.
/// </summary>
public sealed class StatusBarViewModel : ViewModelBase
{
    private static readonly IBrush DirtyBrush  = new SolidColorBrush(Color.Parse("#F5C542")); // yellow
    private static readonly IBrush SavedBrush  = new SolidColorBrush(Color.Parse("#4ADE80")); // green

    private string _fileStatus = "No file loaded";

    /// <summary>Current file status shown on the left of the status bar.</summary>
    public string FileStatus
    {
        get => _fileStatus;
        set => SetField(ref _fileStatus, value);
    }

    private bool _isWorkspaceDirty;

    /// <summary>
    /// True when the scene has changed since the last save/open.
    /// Drives the yellow (dirty) vs green (saved) status dot.
    /// </summary>
    public bool IsWorkspaceDirty
    {
        get => _isWorkspaceDirty;
        set
        {
            if (!SetField(ref _isWorkspaceDirty, value)) return;
            OnPropertyChanged(nameof(SaveStatusDotBrush));
            OnPropertyChanged(nameof(SaveStatusTooltip));
        }
    }

    /// <summary>Yellow when unsaved, green when saved.</summary>
    public IBrush SaveStatusDotBrush => IsWorkspaceDirty ? DirtyBrush : SavedBrush;

    public string SaveStatusTooltip =>
        IsWorkspaceDirty ? "Unsaved changes" : "All changes saved";

    private string _operationFeedback = string.Empty;

    /// <summary>Transient message shown on the right (e.g., "Slice complete -- 42 passes").</summary>
    public string OperationFeedback
    {
        get => _operationFeedback;
        set => SetField(ref _operationFeedback, value);
    }

    public string BuildLabel { get; } = BuildInfo.Label;

    private bool _isBuildStale;
    private int _latestBaseline;

    /// <summary>
    /// True once the background launch check (see <c>BuildFreshnessChecker</c>) finds
    /// origin/main has moved past this build's baked-in <see cref="BuildInfo.Baseline"/>.
    /// Starts false and only ever flips on later — the check runs off to the side and
    /// never blocks or delays startup.
    /// </summary>
    public bool IsBuildStale
    {
        get => _isBuildStale;
        set
        {
            if (!SetField(ref _isBuildStale, value)) return;
            OnPropertyChanged(nameof(BuildLabelTooltip));
        }
    }

    /// <summary>origin/main's commit count as of the last freshness check, once it's found something newer than this build's own baseline.</summary>
    public int LatestBaseline
    {
        get => _latestBaseline;
        set
        {
            if (!SetField(ref _latestBaseline, value)) return;
            OnPropertyChanged(nameof(BuildLabelTooltip));
        }
    }

    public string BuildLabelTooltip => IsBuildStale
        ? $"main has moved on to build {LatestBaseline} (this build is based on {BuildInfo.Baseline}) — pull main in to catch up. Click to copy."
        : $"Branch: {BuildInfo.Branch} — click to copy";

    /// <summary>
    /// Copies the build label to the clipboard so it can be pasted into a message to the team,
    /// and confirms in the status bar. Callers pass their own <c>TopLevel.GetTopLevel(this)?.Clipboard</c>
    /// (a ViewModel has no TopLevel of its own); a null clipboard is a no-op.
    /// </summary>
    public async Task CopyBuildLabelAsync(IClipboard? clipboard)
    {
        if (clipboard is null) return;
        await clipboard.SetTextAsync(BuildLabel);
        OperationFeedback = $"Copied “{BuildLabel}” to clipboard";
    }

    private bool _isProgressActive;

    /// <summary>True while a long-running operation is in progress (shows the footer progress line).</summary>
    public bool IsProgressActive
    {
        get => _isProgressActive;
        set => SetField(ref _isProgressActive, value);
    }
}
