using System.Numerics;
using MassiveSlicer.Commands;
using MassiveSlicer.Core.Scanning;
using MassiveSlicer.ViewModels.Base;

namespace MassiveSlicer.ViewModels;

/// <summary>
/// State and commands for the SCAN → SETTINGS sub-tab (hand-eye calibration).
/// </summary>
public sealed class ScanCalibrationViewModel : ViewModelBase
{
    private int    _poseCount;
    private bool   _isCapturing;
    private string _captureStatus = "";
    private bool   _isCalibrating;
    private string _calibrateStatus = "";
    private bool   _hasResult;
    private bool   _applied;
    private double _resultX, _resultY, _resultZ;
    private double _resultA, _resultB, _resultC;
    private double _residualRot, _residualTrans;

    public ScanCalibrationViewModel()
    {
        AddPoseCommand    = new RelayCommand(async () => await CaptureAsync(), () => !_isCapturing && !_isCalibrating);
        ClearPosesCommand = new RelayCommand(ClearPoses, () => _poseCount > 0 && !_isCapturing && !_isCalibrating);
        CalibrateCommand  = new RelayCommand(async () => await CalibrateAsync(), () => _poseCount >= 3 && !_isCapturing && !_isCalibrating);
        ApplyToTcpCommand = new RelayCommand(ApplyToTcp, () => _hasResult && !_applied);
        AutoCalibrateCommand = new RelayCommand(
            async () => { if (OnAutoCalibrateRequested is { } f) await f(); }, () => !_isCapturing && !_isCalibrating);
    }

    // -- Commands -------------------------------------------------------------

    public RelayCommand AddPoseCommand       { get; }
    public RelayCommand ClearPosesCommand    { get; }
    public RelayCommand CalibrateCommand     { get; }
    public RelayCommand ApplyToTcpCommand    { get; }
    public RelayCommand AutoCalibrateCommand { get; }

    // -- Callbacks (wired by MainWindowViewModel) ----------------------------

    /// <summary>Returns the current robot flange pose in ROBROOT (row-vector FK matrix).</summary>
    internal Func<Matrix4x4>? GetFlangeInBase { get; set; }

    /// <summary>Called when the user clicks "Apply to TCP". Args: x, y, z, a, b, c (mm / °).</summary>
    internal Action<double, double, double, double, double, double>? OnApplyCalibration { get; set; }

    /// <summary>Runs the automated pose sweep (deploy SCAN_TOOL_CAL, handshake, scan ×N, calibrate). Wired by MainWindowViewModel.</summary>
    internal Func<Task>? OnAutoCalibrateRequested { get; set; }

    /// <summary>Console logger for success/diagnostic feedback (wired by MainWindowViewModel).</summary>
    internal Action<string>? Log { get; set; }

    // -- Observable state -----------------------------------------------------

    public int PoseCount
    {
        get => _poseCount;
        private set
        {
            if (!SetField(ref _poseCount, value)) return;
            OnPropertyChanged(nameof(PoseCountLabel));
            AddPoseCommand.RaiseCanExecuteChanged();
            ClearPosesCommand.RaiseCanExecuteChanged();
            CalibrateCommand.RaiseCanExecuteChanged();
        }
    }

    public string PoseCountLabel => _poseCount switch
    {
        0 => "No poses collected",
        1 => "1 pose collected",
        _ => $"{_poseCount} poses collected",
    };

    public bool IsCapturing
    {
        get => _isCapturing;
        private set
        {
            if (!SetField(ref _isCapturing, value)) return;
            RaiseAllCanExecuteChanged();
        }
    }

    public string CaptureStatus
    {
        get => _captureStatus;
        private set => SetField(ref _captureStatus, value);
    }

    public bool IsCalibrating
    {
        get => _isCalibrating;
        private set
        {
            if (!SetField(ref _isCalibrating, value)) return;
            RaiseAllCanExecuteChanged();
        }
    }

    public string CalibrateStatus
    {
        get => _calibrateStatus;
        private set => SetField(ref _calibrateStatus, value);
    }

    public bool HasResult
    {
        get => _hasResult;
        private set
        {
            if (!SetField(ref _hasResult, value)) return;
            ApplyToTcpCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>True once the result is written to the TCP — grays out Apply as success feedback.</summary>
    public bool IsApplied
    {
        get => _applied;
        private set
        {
            if (!SetField(ref _applied, value)) return;
            ApplyToTcpCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(ApplyButtonLabel));
        }
    }

    /// <summary>Apply button caption — flips to a success tick once written to the TCP.</summary>
    public string ApplyButtonLabel => _applied ? "APPLIED ✓" : "APPLY TO TCP";

    public double ResultX { get => _resultX; private set => SetField(ref _resultX, value); }
    public double ResultY { get => _resultY; private set => SetField(ref _resultY, value); }
    public double ResultZ { get => _resultZ; private set => SetField(ref _resultZ, value); }
    public double ResultA { get => _resultA; private set => SetField(ref _resultA, value); }
    public double ResultB { get => _resultB; private set => SetField(ref _resultB, value); }
    public double ResultC { get => _resultC; private set => SetField(ref _resultC, value); }

    public double ResidualRot   { get => _residualRot;   private set => SetField(ref _residualRot,   value); }
    public double ResidualTrans { get => _residualTrans; private set => SetField(ref _residualTrans, value); }

    // -- Implementation -------------------------------------------------------

    private async Task CaptureAsync()
    {
        IsCapturing = true;
        CaptureStatus = "Connecting...";

        var flangeInBase = GetFlangeInBase?.Invoke() ?? Matrix4x4.Identity;

        BoardDetectionResult result = await Task.Run(() =>
            ZividScanService.AddCalibrationPose(
                flangeInBase,
                msg => Avalonia.Threading.Dispatcher.UIThread.Post(() => CaptureStatus = msg)));

        PoseCount = ZividScanService.CalibrationPoseCount;
        CaptureStatus = result.Status;
        IsApplied = false;
        IsCapturing = false;
    }

    private void ClearPoses()
    {
        ZividScanService.ClearCalibrationPoses();
        PoseCount = 0;
        HasResult = false;
        IsApplied = false;
        CaptureStatus = "";
        CalibrateStatus = "";
    }

    private async Task CalibrateAsync()
    {
        IsCalibrating = true;
        CalibrateStatus = $"Running calibration on {_poseCount} poses...";

        HandEyeCalibResult result = await Task.Run(() => ZividScanService.RunHandEyeCalibration());

        if (result.Success)
        {
            ResultX = Math.Round(result.TcpX, 2);
            ResultY = Math.Round(result.TcpY, 2);
            ResultZ = Math.Round(result.TcpZ, 2);
            ResultA = Math.Round(result.TcpA, 3);
            ResultB = Math.Round(result.TcpB, 3);
            ResultC = Math.Round(result.TcpC, 3);
            ResidualRot   = Math.Round(result.AvgRotResidualDeg,   3);
            ResidualTrans = Math.Round(result.AvgTransResidualMm,  3);
            IsApplied = false;   // fresh result — enable Apply (auto-applied by the sweep)
            HasResult = true;
            CalibrateStatus = $"Done — rot residual {ResidualRot:F3}°, trans {ResidualTrans:F3} mm";
        }
        else
        {
            CalibrateStatus = result.Error ?? "Unknown error";
        }

        IsCalibrating = false;
    }

    private void ApplyToTcp()
    {
        if (!_hasResult || _applied) return;
        OnApplyCalibration?.Invoke(_resultX, _resultY, _resultZ, _resultA, _resultB, _resultC);
        IsApplied = true;
        CalibrateStatus = $"Applied to TCP ✓  ({_resultX:F1}, {_resultY:F1}, {_resultZ:F1}) mm / " +
                          $"A{_resultA:F2} B{_resultB:F2} C{_resultC:F2}";
        Log?.Invoke($"[scancal] Applied to TCP: ({_resultX:F1}, {_resultY:F1}, {_resultZ:F1}) mm, " +
                    $"A{_resultA:F2} B{_resultB:F2} C{_resultC:F2}; residual rot {_residualRot:F3}°, " +
                    $"trans {_residualTrans:F3} mm — TCP updated.");
    }

    // -- Hooks for the automated sweep (MainWindowViewModel.RunAutoScanToolCalibration) --------

    /// <summary>Sets the calibrate status line (used by the automated sweep orchestration).</summary>
    internal void SetStatus(string s) => CalibrateStatus = s;

    /// <summary>Clears poses/result (used by the automated sweep orchestration).</summary>
    internal void ClearForAuto() => ClearPoses();

    /// <summary>Captures one hand-eye pose at the current flange; returns true if a pose was added.</summary>
    internal async Task<bool> CapturePoseAutoAsync()
    {
        int before = ZividScanService.CalibrationPoseCount;
        await CaptureAsync();
        return ZividScanService.CalibrationPoseCount > before;
    }

    /// <summary>Runs the hand-eye fit over the captured poses (used by the automated sweep).</summary>
    internal Task ComputeCalibrationAsync() => CalibrateAsync();

    /// <summary>Applies the computed result to the TCP (used by the automated sweep).</summary>
    internal void ApplyResult() => ApplyToTcp();

    private void RaiseAllCanExecuteChanged()
    {
        AddPoseCommand.RaiseCanExecuteChanged();
        ClearPosesCommand.RaiseCanExecuteChanged();
        CalibrateCommand.RaiseCanExecuteChanged();
        ApplyToTcpCommand.RaiseCanExecuteChanged();
        AutoCalibrateCommand.RaiseCanExecuteChanged();
    }
}
