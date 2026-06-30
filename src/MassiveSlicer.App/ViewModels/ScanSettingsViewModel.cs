using System.Collections.ObjectModel;
using MassiveSlicer.Commands;
using MassiveSlicer.Core.Models;
using MassiveSlicer.ViewModels.Base;

namespace MassiveSlicer.ViewModels;

/// <summary>
/// Settings for the Zivid 3D scanner workflow (SCAN tab). Holds camera
/// connection and capture-output options plus the test-scan trigger; capture
/// itself runs through <c>ZividScanService</c>, wired by <c>MainWindowViewModel</c>.
/// </summary>
public sealed class ScanSettingsViewModel : ViewModelBase
{
    private string _cameraIp = "192.168.0.150";
    private string _outputDirectory = "scans";
    private int _toolDataIndex = 6;
    private int _baseDataIndex = 1;
    private bool _isScanning;
    private string _scanStatus = "";
    private bool _isCaptureSubTab = true;
    private string _newQuickPositionName = "Quick Position 1";
    private string _quickPositionStatus = "";

    public const string ScannerDownWaypointName = "scanner-down-bed";
    public const string ScannerDownLabel      = "Scanner Down";
    public const string QuickPositionTag      = "scan-quick";

    public ScanSettingsViewModel()
    {
        TestScanCommand        = new RelayCommand(() => OnTestScanRequested?.Invoke(), () => !IsScanning);
        ShowCaptureCommand     = new RelayCommand(() => IsCaptureSubTab = true);
        ShowCalibrationCommand = new RelayCommand(() => IsCaptureSubTab = false);
        SaveQuickPositionCommand = new RelayCommand(
            () => OnSaveQuickPositionRequested?.Invoke(NewQuickPositionName),
            () => !string.IsNullOrWhiteSpace(NewQuickPositionName));
    }

    /// <summary>Triggers a single capture from the Zivid camera.</summary>
    public RelayCommand TestScanCommand        { get; }

    /// <summary>Switches the SCAN tab to the CAPTURE sub-tab.</summary>
    public RelayCommand ShowCaptureCommand     { get; }

    /// <summary>Switches the SCAN tab to the SETTINGS (calibration) sub-tab.</summary>
    public RelayCommand ShowCalibrationCommand { get; }

    /// <summary>Calibration workflow state — pose collection and hand-eye computation.</summary>
    public ScanCalibrationViewModel Calibration { get; } = new();

    /// <summary>Named scan poses (built-in + user-saved) shown as recall buttons.</summary>
    public ObservableCollection<ScanQuickPositionItem> QuickPositions { get; } = [];

    /// <summary>Name for the next user-saved quick position.</summary>
    public string NewQuickPositionName
    {
        get => _newQuickPositionName;
        set
        {
            if (!SetField(ref _newQuickPositionName, value)) return;
            SaveQuickPositionCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Status line under the save control (move/save feedback).</summary>
    public string QuickPositionStatus
    {
        get => _quickPositionStatus;
        set => SetField(ref _quickPositionStatus, value);
    }

    /// <summary>Saves the synced robot pose as a new quick position.</summary>
    public RelayCommand SaveQuickPositionCommand { get; }

    internal Action<string>? OnMoveQuickPositionRequested { get; set; }
    internal Action<string>? OnSaveQuickPositionRequested { get; set; }

    // -- Sub-tab selection ----------------------------------------------------

    public bool IsCaptureSubTab
    {
        get => _isCaptureSubTab;
        set
        {
            if (!SetField(ref _isCaptureSubTab, value)) return;
            OnPropertyChanged(nameof(IsCalibrationSubTab));
        }
    }

    public bool IsCalibrationSubTab => !_isCaptureSubTab;

    /// <summary>
    /// Callback that performs the capture. Wired by <c>MainWindowViewModel</c>,
    /// which owns the viewport the resulting mesh is added to.
    /// </summary>
    internal Action? OnTestScanRequested { get; set; }

    /// <summary>True while a capture is in flight; disables the scan button.</summary>
    public bool IsScanning
    {
        get => _isScanning;
        set
        {
            if (!SetField(ref _isScanning, value)) return;
            TestScanCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Human-readable capture progress / result shown under the scan button.</summary>
    public string ScanStatus
    {
        get => _scanStatus;
        set => SetField(ref _scanStatus, value);
    }

    /// <summary>IP address of the Zivid camera on the cell network.</summary>
    public string CameraIp
    {
        get => _cameraIp;
        set => SetField(ref _cameraIp, value);
    }

    /// <summary>
    /// KUKA TOOL_DATA index for the scanner (1–16). TOOL_DATA[6] is the
    /// calibrated Zivid TCP ("ZvidScannerCalibrated") on the LFAM 3 controller.
    /// </summary>
    public int ToolDataIndex
    {
        get => _toolDataIndex;
        set => SetField(ref _toolDataIndex, value);
    }

    /// <summary>KUKA BASE_DATA index used while scanning (1–32).</summary>
    public int BaseDataIndex
    {
        get => _baseDataIndex;
        set => SetField(ref _baseDataIndex, value);
    }

    /// <summary>Directory where captured scans (.zdf / .ply) are written.</summary>
    public string OutputDirectory
    {
        get => _outputDirectory;
        set => SetField(ref _outputDirectory, value);
    }

    /// <summary>Rebuilds quick-position buttons from the active cell waypoints.</summary>
    public void RefreshQuickPositions(CellConfig? cell)
    {
        QuickPositions.Clear();
        QuickPositions.Add(new ScanQuickPositionItem(
            ScannerDownLabel, ScannerDownWaypointName, MoveQuickPosition));

        if (cell is not null)
        {
            foreach (var wp in cell.Waypoints)
            {
                if (!wp.Tags.Any(t => t.Equals(QuickPositionTag, StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (wp.Name.Equals(ScannerDownWaypointName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var label = string.IsNullOrWhiteSpace(wp.Description)
                    ? HumanizeWaypointName(wp.Name)
                    : wp.Description!;
                QuickPositions.Add(new ScanQuickPositionItem(label, wp.Name, MoveQuickPosition));
            }
        }

        SuggestNextQuickPositionName(cell);
    }

    internal void SuggestNextQuickPositionName(CellConfig? cell = null)
    {
        int userCount = cell?.Waypoints.Count(w =>
            w.Tags.Any(t => t.Equals(QuickPositionTag, StringComparison.OrdinalIgnoreCase))) ?? 0;
        NewQuickPositionName = $"Quick Position {userCount + 1}";
    }

    private void MoveQuickPosition(string waypointName)
    {
        QuickPositionStatus = $"Moving to {waypointName}…";
        OnMoveQuickPositionRequested?.Invoke(waypointName);
    }

    internal static string SlugifyQuickPositionName(string displayName)
    {
        var parts = displayName.Trim().ToLowerInvariant()
            .Split([' ', '_', '-'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? "quick-position" : string.Join("-", parts);
    }

    private static string HumanizeWaypointName(string name)
        => string.Join(' ', name.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static p => char.ToUpperInvariant(p[0]) + p[1..]));
}
