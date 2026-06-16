using System.Numerics;
using MassiveSlicer.Commands;
using MassiveSlicer.Core.Scanning;
using MassiveSlicer.ViewModels.Base;

namespace MassiveSlicer.ViewModels;

/// <summary>
/// Rotary-bed (E1) axis calibration. With the arm held still and the calibration
/// board fixed to the bed, the user rotates E1 and captures the board centroid at
/// each angle. The centroids sweep a circle about the bed axis; fitting it gives the
/// bed centre (X/Y) and surface height (Z). Mirrors <see cref="ScanCalibrationViewModel"/>.
/// </summary>
public sealed class RotaryBedCalibrationViewModel : ViewModelBase
{
    private readonly List<(double Angle, Vector3 World)> _samples = new();

    private bool   _isBusy;
    private string _status = "";
    private bool   _hasResult;
    private double _centerX, _centerY, _centerZ, _radius, _residual, _zSpread;
    private double _rotationSign, _rotationDegPerE1, _rotationResidual;
    private bool   _rotationResolved;

    public RotaryBedCalibrationViewModel()
    {
        AddSampleCommand     = new RelayCommand(async () => await AddSampleAsync(), () => !_isBusy);
        ClearCommand         = new RelayCommand(Clear,   () => _samples.Count > 0 && !_isBusy);
        ComputeCommand       = new RelayCommand(Compute, () => _samples.Count >= 3 && !_isBusy);
        ApplyCommand         = new RelayCommand(Apply,   () => _hasResult && !_isBusy);
        AutoCalibrateCommand = new RelayCommand(
            async () => { if (OnAutoCalibrateRequested is { } f) await f(); }, () => !_isBusy);
    }

    // -- Commands -------------------------------------------------------------

    public RelayCommand AddSampleCommand     { get; }
    public RelayCommand ClearCommand         { get; }
    public RelayCommand ComputeCommand       { get; }
    public RelayCommand ApplyCommand         { get; }
    public RelayCommand AutoCalibrateCommand { get; }

    // -- Callbacks (wired by MainWindowViewModel) ----------------------------

    /// <summary>Scanner camera→world pose (row-vector Matrix4x4), or null when no robot/scanner is available.</summary>
    internal Func<Matrix4x4?>? GetCameraToWorld { get; set; }

    /// <summary>Live E1 angle (KRL degrees).</summary>
    internal Func<double>? GetCurrentE1 { get; set; }

    /// <summary>Apply the measured bed centre (world / ROBROOT mm) and E1 rotation sign.</summary>
    internal Action<float, float, float, float>? OnApplyCenter { get; set; }

    /// <summary>Runs the automated E1-sweep capture (deploy KRL, handshake, scan ×10, compute). Wired by MainWindowViewModel.</summary>
    internal Func<Task>? OnAutoCalibrateRequested { get; set; }

    // -- Observable state -----------------------------------------------------

    public int SampleCount => _samples.Count;

    public string SampleCountLabel => _samples.Count switch
    {
        0 => "No samples collected",
        1 => "1 sample collected",
        _ => $"{_samples.Count} samples collected",
    };

    public bool IsBusy
    {
        get => _isBusy;
        private set { if (SetField(ref _isBusy, value)) RaiseAllCanExecuteChanged(); }
    }

    public string Status { get => _status; private set => SetField(ref _status, value); }

    public bool HasResult
    {
        get => _hasResult;
        private set { if (SetField(ref _hasResult, value)) ApplyCommand.RaiseCanExecuteChanged(); }
    }

    public double CenterX  { get => _centerX;  private set => SetField(ref _centerX,  value); }
    public double CenterY  { get => _centerY;  private set => SetField(ref _centerY,  value); }
    public double CenterZ  { get => _centerZ;  private set => SetField(ref _centerZ,  value); }
    public double Radius   { get => _radius;   private set => SetField(ref _radius,   value); }
    public double Residual { get => _residual; private set => SetField(ref _residual, value); }
    public double ZSpread  { get => _zSpread;  private set => SetField(ref _zSpread,  value); }

    public bool   RotationResolved { get => _rotationResolved; private set { if (SetField(ref _rotationResolved, value)) OnPropertyChanged(nameof(RotationLabel)); } }
    public double RotationSign     { get => _rotationSign;     private set => SetField(ref _rotationSign, value); }
    public double RotationDegPerE1 { get => _rotationDegPerE1; private set { if (SetField(ref _rotationDegPerE1, value)) OnPropertyChanged(nameof(RotationLabel)); } }
    public double RotationResidual { get => _rotationResidual; private set => SetField(ref _rotationResidual, value); }

    /// <summary>Human-readable rotation summary, e.g. "CW (−1.00°/°, residual 0.3°)".</summary>
    public string RotationLabel => !_rotationResolved
        ? "Rotation: not enough E1 spread"
        : $"Rotation: {(_rotationSign < 0 ? "CW" : "CCW")} ({_rotationDegPerE1:+0.00;-0.00}°/°, residual {_rotationResidual:F2}°)";

    // -- Implementation -------------------------------------------------------

    /// <summary>Sets the status line (used by the automated sweep orchestration).</summary>
    internal void SetStatus(string s) => Status = s;

    /// <summary>Clears all samples and the result (used by the automated sweep orchestration).</summary>
    internal void ClearSamples() => Clear();

    /// <summary>Captures one board sample at the current pose (public for the automated sweep).</summary>
    public async Task AddSampleAsync()
    {
        if (GetCameraToWorld?.Invoke() is not { } camToWorld)
        {
            Status = "No camera pose — load the robot cell and select the scanner tool.";
            return;
        }

        IsBusy = true;
        Status = "Capturing...";

        var det = await Task.Run(() => ZividScanService.DetectBoardCentroid(
            msg => Avalonia.Threading.Dispatcher.UIThread.Post(() => Status = msg)));

        if (!det.Detected)
        {
            Status = det.Status;
            IsBusy = false;
            return;
        }

        // Centroid is in the camera frame; lift to world via the (fixed) camera pose.
        var world = Vector3.Transform(new Vector3(det.X, det.Y, det.Z), camToWorld);
        double e1 = GetCurrentE1?.Invoke() ?? 0;
        _samples.Add((e1, world));

        HasResult = false;
        OnPropertyChanged(nameof(SampleCount));
        OnPropertyChanged(nameof(SampleCountLabel));
        RaiseAllCanExecuteChanged();
        Status = $"Sample {_samples.Count} @ E1={e1:F1}° → ({world.X:F0}, {world.Y:F0}, {world.Z:F0}) mm";
        IsBusy = false;
    }

    private void Clear()
    {
        _samples.Clear();
        HasResult = false;
        Status = "";
        OnPropertyChanged(nameof(SampleCount));
        OnPropertyChanged(nameof(SampleCountLabel));
        RaiseAllCanExecuteChanged();
    }

    /// <summary>Fits the collected samples (public for the automated sweep).</summary>
    public void Compute()
    {
        var res = RotaryBedCalibration.Fit(_samples);
        if (!res.Success)
        {
            HasResult = false;
            Status = res.Error ?? "Fit failed.";
            return;
        }

        CenterX  = Math.Round(res.CenterX, 2);
        CenterY  = Math.Round(res.CenterY, 2);
        CenterZ  = Math.Round(res.CenterZ, 2);
        Radius   = Math.Round(res.Radius, 1);
        Residual = Math.Round(res.RmsResidualMm, 2);
        ZSpread  = Math.Round(res.ZSpreadMm, 2);

        RotationResolved = res.RotationResolved;
        RotationSign     = res.RotationSign;
        RotationDegPerE1 = Math.Round(res.MeasuredDegPerE1, 3);
        RotationResidual = Math.Round(res.RotationResidualDeg, 2);

        HasResult = true;
        Status = $"Centre ({CenterX:F1}, {CenterY:F1}, {CenterZ:F1}) — R {Radius:F0} mm, residual {Residual:F2} mm. {RotationLabel}";
    }

    private void Apply()
    {
        if (!_hasResult) return;
        // Fall back to the original CW (−1) direction if E1 spread was too small to resolve.
        float sign = _rotationResolved ? (float)_rotationSign : -1f;
        OnApplyCenter?.Invoke((float)_centerX, (float)_centerY, (float)_centerZ, sign);
        Status = $"Applied centre ({_centerX:F1}, {_centerY:F1}, {_centerZ:F1}) mm, rotation {(sign < 0 ? "CW" : "CCW")}.";
    }

    private void RaiseAllCanExecuteChanged()
    {
        AddSampleCommand.RaiseCanExecuteChanged();
        ClearCommand.RaiseCanExecuteChanged();
        ComputeCommand.RaiseCanExecuteChanged();
        ApplyCommand.RaiseCanExecuteChanged();
        AutoCalibrateCommand.RaiseCanExecuteChanged();
    }
}
