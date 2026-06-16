using MassiveSlicer.Commands;
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

    public ScanSettingsViewModel()
    {
        TestScanCommand        = new RelayCommand(() => OnTestScanRequested?.Invoke(), () => !IsScanning);
        ShowCaptureCommand     = new RelayCommand(() => IsCaptureSubTab = true);
        ShowCalibrationCommand = new RelayCommand(() => IsCaptureSubTab = false);
    }

    /// <summary>Triggers a single capture from the Zivid camera.</summary>
    public RelayCommand TestScanCommand        { get; }

    /// <summary>Switches the SCAN tab to the CAPTURE sub-tab.</summary>
    public RelayCommand ShowCaptureCommand     { get; }

    /// <summary>Switches the SCAN tab to the SETTINGS (calibration) sub-tab.</summary>
    public RelayCommand ShowCalibrationCommand { get; }

    /// <summary>Calibration workflow state — pose collection and hand-eye computation.</summary>
    public ScanCalibrationViewModel Calibration { get; } = new();

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
}
