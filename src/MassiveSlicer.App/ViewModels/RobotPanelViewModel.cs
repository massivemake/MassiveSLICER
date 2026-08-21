using System.Collections.ObjectModel;
using System.Linq;
using System.Numerics;
using System.Windows.Input;
using Avalonia.Threading;
using MassiveSlicer.Commands;
using MassiveSlicer.Core.C3Bridge;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Kinematics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Viewport.FK;
using MassiveSlicer.ViewModels.Base;

namespace MassiveSlicer.ViewModels;

/// <summary>
/// Manages the ROBOT settings sub-tab: six joint sliders (A1-A6) for the
/// KR 210 R3100 Ultra (LFAM 2), plus TCP readout. Angles are in KRL degrees.
/// Limits are LFAM 2 software limits; defaults are the LFAM 2 home position.
/// </summary>
public sealed class RobotPanelViewModel : ViewModelBase
{
    // -- Joint limits (driven by cell config) --------------------------------

    private double _minA1 = -360, _maxA1 = 360;
    private double _minA2 = -360, _maxA2 = 360;
    private double _minA3 = -360, _maxA3 = 360;
    private double _minA4 = -360, _maxA4 = 360;
    private double _minA5 = -360, _maxA5 = 360;
    private double _minA6 = -360, _maxA6 = 360;
    private double _minE1 = -360, _maxE1 = 360;

    private IReadOnlyList<JointConfig> _joints = [];
    private bool _loadingLimits;
    private bool _limitsRequireColdReboot;
    private DispatcherTimer? _limitPushTimer;
    private string _limitsStatus = "";

    /// <summary>True after the user edits a software limit (needs a KUKA cold reboot).</summary>
    public bool LimitsRequireColdReboot
    {
        get => _limitsRequireColdReboot;
        private set => SetField(ref _limitsRequireColdReboot, value);
    }

    public string LimitsStatus
    {
        get => _limitsStatus;
        private set => SetField(ref _limitsStatus, value);
    }

    public double MinA1 { get => _minA1; set => SetSoftLimit(ref _minA1, value); }
    public double MaxA1 { get => _maxA1; set => SetSoftLimit(ref _maxA1, value); }
    public double MinA2 { get => _minA2; set => SetSoftLimit(ref _minA2, value); }
    public double MaxA2 { get => _maxA2; set => SetSoftLimit(ref _maxA2, value); }
    public double MinA3 { get => _minA3; set => SetSoftLimit(ref _minA3, value); }
    public double MaxA3 { get => _maxA3; set => SetSoftLimit(ref _maxA3, value); }
    public double MinA4 { get => _minA4; set => SetSoftLimit(ref _minA4, value); }
    public double MaxA4 { get => _maxA4; set => SetSoftLimit(ref _maxA4, value); }
    public double MinA5 { get => _minA5; set => SetSoftLimit(ref _minA5, value); }
    public double MaxA5 { get => _maxA5; set => SetSoftLimit(ref _maxA5, value); }
    public double MinA6 { get => _minA6; set => SetSoftLimit(ref _minA6, value); }
    public double MaxA6 { get => _maxA6; set => SetSoftLimit(ref _maxA6, value); }
    public double MinE1 { get => _minE1; set => SetSoftLimit(ref _minE1, value); }
    public double MaxE1 { get => _maxE1; set => SetSoftLimit(ref _maxE1, value); }

    /// <summary>Slider / sim range — 5% inside the software stops.</summary>
    public double SimMinA1 => JointLimitEnvelope.UsableMin(_minA1, _maxA1);
    public double SimMaxA1 => JointLimitEnvelope.UsableMax(_minA1, _maxA1);
    public double SimMinA2 => JointLimitEnvelope.UsableMin(_minA2, _maxA2);
    public double SimMaxA2 => JointLimitEnvelope.UsableMax(_minA2, _maxA2);
    public double SimMinA3 => JointLimitEnvelope.UsableMin(_minA3, _maxA3);
    public double SimMaxA3 => JointLimitEnvelope.UsableMax(_minA3, _maxA3);
    public double SimMinA4 => JointLimitEnvelope.UsableMin(_minA4, _maxA4);
    public double SimMaxA4 => JointLimitEnvelope.UsableMax(_minA4, _maxA4);
    public double SimMinA5 => JointLimitEnvelope.UsableMin(_minA5, _maxA5);
    public double SimMaxA5 => JointLimitEnvelope.UsableMax(_minA5, _maxA5);
    public double SimMinA6 => JointLimitEnvelope.UsableMin(_minA6, _maxA6);
    public double SimMaxA6 => JointLimitEnvelope.UsableMax(_minA6, _maxA6);
    public double SimMinE1 => JointLimitEnvelope.UsableMin(_minE1, _maxE1);
    public double SimMaxE1 => JointLimitEnvelope.UsableMax(_minE1, _maxE1);

    // -- Joint angles (KRL degrees) -------------------------------------------

    private double _a1 =   0;
    private double _a2 = -90;
    private double _a3 =  90;
    private double _a4 =   0;
    private double _a5 =  15;
    private double _a6 =   0;
    private double _e1 =   0;

    public double A1 { get => _a1; set => SetField(ref _a1, JointLimitEnvelope.Clamp(value, _minA1, _maxA1)); }
    public double A2 { get => _a2; set => SetField(ref _a2, JointLimitEnvelope.Clamp(value, _minA2, _maxA2)); }
    public double A3 { get => _a3; set => SetField(ref _a3, JointLimitEnvelope.Clamp(value, _minA3, _maxA3)); }
    public double A4 { get => _a4; set => SetField(ref _a4, JointLimitEnvelope.Clamp(value, _minA4, _maxA4)); }
    public double A5 { get => _a5; set => SetField(ref _a5, JointLimitEnvelope.Clamp(value, _minA5, _maxA5)); }
    public double A6 { get => _a6; set => SetField(ref _a6, JointLimitEnvelope.Clamp(value, _minA6, _maxA6)); }
    /// <summary>External axis 1 — rotary bed (KRL degrees) or rail (mm).</summary>
    public double E1 { get => _e1; set => SetField(ref _e1, JointLimitEnvelope.Clamp(value, _minE1, _maxE1)); }

    /// <summary>Rotary-bed (E1) axis calibration: scan the board across E1 rotations to find the bed centre.</summary>
    public RotaryBedCalibrationViewModel BedCalibration { get; } = new();

    // -- Rotary bed manual adjust ---------------------------------------------

    private bool   _isRotaryBed;
    private double _bedCenterX, _bedCenterY, _bedCenterZ, _bedDiameter;
    private double _bedRotationSign = -1;
    private double _bedOrientationOffsetDeg = RotaryBedCellConfig.DefaultOrientationOffsetDeg;
    private bool   _suppressBedCallback;

    /// <summary>True when the active cell's bed is a circular rotary turntable (shows the ROTARY BED panel).</summary>
    public bool IsRotaryBed { get => _isRotaryBed; private set => SetField(ref _isRotaryBed, value); }

    private bool _isRobotRail;
    /// <summary>True when E1 drives a linear rail (mm) rather than a rotary bed (deg).</summary>
    public bool IsRobotRail { get => _isRobotRail; private set => SetField(ref _isRobotRail, value); }

    public string E1UnitLabel => IsRobotRail ? "mm" : "°";

    /// <summary>Rotary-bed centre X in world/ROBROOT mm (rotation axis + grid datum). Editable.</summary>
    public double BedCenterX { get => _bedCenterX; set { if (SetField(ref _bedCenterX, value)) FireBedEdited(); } }
    /// <summary>Rotary-bed centre Y in world/ROBROOT mm. Editable.</summary>
    public double BedCenterY { get => _bedCenterY; set { if (SetField(ref _bedCenterY, value)) FireBedEdited(); } }
    /// <summary>Rotary-bed surface height Z in world/ROBROOT mm. Editable.</summary>
    public double BedCenterZ { get => _bedCenterZ; set { if (SetField(ref _bedCenterZ, value)) FireBedEdited(); } }
    /// <summary>Rotary-bed diameter in mm (circular grid). Editable.</summary>
    public double BedDiameter { get => _bedDiameter; set { if (SetField(ref _bedDiameter, value)) FireBedEdited(); } }

    /// <summary>E1→scene rotation sign (+1 CCW / −1 CW about world +Z). Set by rotation calibration.</summary>
    public double BedRotationSign
    {
        get => _bedRotationSign;
        set { if (SetField(ref _bedRotationSign, value)) { OnPropertyChanged(nameof(IsE1Reversed)); FireBedEdited(); } }
    }

    /// <summary>Checkbox-friendly view of <see cref="BedRotationSign"/> (true = −1, CW).</summary>
    public bool IsE1Reversed
    {
        get => _bedRotationSign < 0;
        set => BedRotationSign = value ? -1 : 1;
    }

    /// <summary>
    /// Rotary bed hole-grid phase offset (degrees about vertical through centre). Normally set by bed-cal;
    /// default <see cref="RotaryBedCellConfig.DefaultOrientationOffsetDeg"/>.
    /// </summary>
    public double BedOrientationOffsetDeg
    {
        get => _bedOrientationOffsetDeg;
        set
        {
            if (SetField(ref _bedOrientationOffsetDeg, Math.Round(value, 3)))
                OnBedOrientationEdited?.Invoke(_bedOrientationOffsetDeg);
        }
    }

    /// <summary>Invoked when any rotary-bed field is edited. Args: centre x, y, z (mm), diameter (mm), rotation sign.</summary>
    internal Action<double, double, double, double, double>? OnBedEdited { get; set; }

    /// <summary>Invoked when <see cref="BedOrientationOffsetDeg"/> is edited (degrees).</summary>
    internal Action<double>? OnBedOrientationEdited { get; set; }

    private void FireBedEdited()
    {
        if (!_suppressBedCallback)
            OnBedEdited?.Invoke(_bedCenterX, _bedCenterY, _bedCenterZ, _bedDiameter, _bedRotationSign);
    }

    /// <summary>Loads the rotary-bed fields from the active cell (no callback fired).</summary>
    public void ConfigureBed(double x, double y, double z, double diameter, double rotationSign,
        double orientationOffsetDeg, bool isRotary)
    {
        _suppressBedCallback = true;
        BedCenterX      = Math.Round(x, 2);
        BedCenterY      = Math.Round(y, 2);
        BedCenterZ      = Math.Round(z, 2);
        BedDiameter     = Math.Round(diameter, 2);
        BedRotationSign = rotationSign;
        _bedOrientationOffsetDeg = Math.Round(orientationOffsetDeg, 3);
        OnPropertyChanged(nameof(BedOrientationOffsetDeg));
        IsRotaryBed     = isRotary;
        _suppressBedCallback = false;
    }

    /// <summary>Loads E1 limits/units from the active cell rail config.</summary>
    public void ConfigureRail(RobotRailCellConfig? rail)
    {
        IsRobotRail = rail is not null;
        OnPropertyChanged(nameof(E1UnitLabel));
        _loadingLimits = true;
        if (rail is { } r)
        {
            MinE1 = r.MinMm;
            MaxE1 = r.MaxMm;
        }
        else if (!IsRotaryBed)
        {
            MinE1 = -360;
            MaxE1 = 360;
        }
        _loadingLimits = false;
        NotifySimLimits();
    }

    /// <summary>
    /// Applies a rotary-bed calibration result (centre + rotation sign) as a single edit:
    /// updates the fields and fires <see cref="OnBedEdited"/> once for a live + persisted update.
    /// Diameter is left untouched (it isn't measured by the circle fit).
    /// </summary>
    public void ApplyBedCalibration(double x, double y, double z, double rotationSign)
    {
        _suppressBedCallback = true;
        BedCenterX      = Math.Round(x, 2);
        BedCenterY      = Math.Round(y, 2);
        BedCenterZ      = Math.Round(z, 2);
        BedRotationSign = rotationSign;
        _suppressBedCallback = false;
        FireBedEdited();
    }

    // -- C3Bridge connection ---------------------------------------------------

    private readonly RobotSyncService _sync = new();
    private string _bridgeIp   = "192.168.0.1";
    private int    _bridgePort = 7000;

    private ConnectionStatus _connectionStatus = ConnectionStatus.Disconnected;
    public ConnectionStatus ConnectionStatus
    {
        get => _connectionStatus;
        private set
        {
            if (!SetField(ref _connectionStatus, value)) return;
            OnPropertyChanged(nameof(IsConnected));
            OnPropertyChanged(nameof(SyncButtonLabel));
        }
    }

    /// <summary>True while a live C3Bridge session is active.</summary>
    public bool IsConnected => _connectionStatus == ConnectionStatus.Ready;

    /// <summary>Fired on the UI thread when live KUKA I/O variables are batch-read.</summary>
    public event EventHandler<IReadOnlyDictionary<string, string>>? IoSnapshotUpdated;

    /// <summary>Button label -- "Sync Robot" when disconnected, "Desync Robot" when live.</summary>
    public string SyncButtonLabel => IsConnected ? "Desync Robot" : "Sync Robot";

    public ICommand ConnectCommand { get; }

    /// <summary>Updates the target IP/port from the loaded cell config.</summary>
    public void SetBridgeConfig(string ip, int port)
    {
        _bridgeIp   = ip;
        _bridgePort = port;
    }

    // -- Bed-calibration handshake (auto E1 sweep) ----------------------------
    // The C3Bridge client allows one request in flight, so the auto-cal orchestration
    // pauses streaming, then drives these directly.

    /// <summary>Pauses the live polling loop (keeps the TCP connection open).</summary>
    public void PauseStreaming() => _sync.StopStreaming();

    /// <summary>Resumes the live polling loop if connected.</summary>
    public void ResumeStreaming() { if (_sync.IsConnected) _sync.StartStreaming(100); }

    /// <summary>Enables periodic KUKA I/O reads on the existing C3Bridge stream.</summary>
    public void SetLiveIoPolling(bool enabled)
    {
        _sync.IoPollingEnabled = enabled;
        _sync.SetIoPollVariables(enabled ? Lfam3LiveIoCatalog.KukaPollVariables : []);
    }

    /// <summary>Writes a KRL digital output. Pauses streaming briefly.</summary>
    public async Task<bool> TryWriteKukaDigitalAsync(string varName, bool value)
    {
        if (!_sync.IsConnected) return false;
        PauseStreaming();
        try
        {
            await _sync.SetBoolAsync(varName, value);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            ResumeStreaming();
        }
    }

    /// <summary>Reads a KRL <c>$FLAG[idx]</c> (streaming must be paused).</summary>
    public Task<bool> ReadFlagAsync(int idx, CancellationToken ct = default) => _sync.ReadFlagAsync(idx, ct);

    /// <summary>Sets a KRL <c>$FLAG[idx]</c> (streaming must be paused).</summary>
    public Task SetFlagAsync(int idx, bool value, CancellationToken ct = default) => _sync.SetFlagAsync(idx, value, ct);

    /// <summary>Reads $AXIS_ACT [A1..A6, E1] (streaming must be paused).</summary>
    public Task<double[]> ReadAxesAsync(CancellationToken ct = default) => _sync.ReadAxesAsync(ct);

    /// <summary>Sets a global KRL BOOL by name (e.g. a CELL() trigger; streaming must be paused).</summary>
    public Task SetBoolAsync(string name, bool value, CancellationToken ct = default) => _sync.SetBoolAsync(name, value, ct);

    /// <summary>Selects + starts a KRL program by name via C3 program-control (streaming must be paused).</summary>
    public Task<C3BridgeClient.ProgramResult> RunProgramAsync(string programName, CancellationToken ct = default) => _sync.RunProgramAsync(programName, ct);

    /// <summary>Selects a KRL program by name via C3 program-control (streaming must be paused).</summary>
    public Task<C3BridgeClient.ProgramResult> SelectProgramAsync(string programName, CancellationToken ct = default) => _sync.SelectProgramAsync(programName, ct);

    /// <summary>Interpreter control via C3 (e.g. Start=2 after Select). Streaming must be paused.</summary>
    public Task<C3BridgeClient.ProgramResult> ProgramControlAsync(byte command, ushort interpreter = 1, CancellationToken ct = default)
        => _sync.ProgramControlAsync(command, interpreter, ct);

    // -- MASSIVE_SERVER motion command server (variable-driven; no .src reloads) ----------------

    /// <summary>Syncs the host command counter to the controller's current MS_SEQ (call once after connect).</summary>
    public Task<int> InitCommandServerAsync(CancellationToken ct = default) => _sync.InitCommandServerAsync(ct);

    /// <summary>Reads a KRL variable by name and returns the raw controller value.</summary>
    public Task<string> ReadVarAsync(string name, CancellationToken ct = default) => _sync.ReadVarAsync(name, ct);

    /// <summary>Writes a KRL variable by name (BOOL, INT, FRAME, etc.).</summary>
    public Task<string> WriteVarAsync(string name, string value, CancellationToken ct = default) => _sync.WriteVarAsync(name, value, ct);

    /// <summary>Pulses <c>bRunScanPick</c> while <c>CELL</c> is running to execute <c>Scanner_Pick</c>.</summary>
    public Task<string> TriggerScanPickAsync(CancellationToken ct = default) => _sync.WriteVarAsync("bRunScanPick", "TRUE", ct);

    /// <summary>PTP/LIN the tool to a Cartesian pose via MASSIVE_SERVER (returns true on ack).</summary>
    public Task<bool> SendPoseAsync(bool linear, double x, double y, double z, double a, double b, double c,
        int vel, int tool, int baseIndex, int timeoutMs = 60000, CancellationToken ct = default)
        => _sync.SendPoseAsync(linear, x, y, z, a, b, c, vel, tool, baseIndex, timeoutMs, ct);

    /// <summary>PTP to a joint target (A1..A6, E1, KRL deg) via MASSIVE_SERVER.</summary>
    public Task<bool> SendAxesAsync(double a1, double a2, double a3, double a4, double a5, double a6, double e1,
        int vel, int tool = 0, int baseIndex = 0, int timeoutMs = 60000, CancellationToken ct = default)
        => _sync.SendAxesAsync(a1, a2, a3, a4, a5, a6, e1, vel, tool, baseIndex, timeoutMs, ct);

    /// <summary>Move to the controller HOME via MASSIVE_SERVER.</summary>
    public Task<bool> GoHomeAsync(int vel = 20, int timeoutMs = 60000, CancellationToken ct = default)
        => _sync.GoHomeAsync(vel, timeoutMs, ct);

    /// <summary>Applies tool/base on the controller without moving (MS_CMD=5).</summary>
    public Task<bool> SetFrameAsync(int tool, int baseIndex, int timeoutMs = 10000, CancellationToken ct = default)
        => _sync.SetFrameAsync(tool, baseIndex, timeoutMs, ct);

    // Motion handling is integrated into the controller's CELL.SRC loop (the existing dispatcher),
    // so there is no separate server program to deploy or stop.

    /// <summary>
    /// Writes a KUKA <c>BASE_DATA[index]</c> FRAME on the controller (X/Y/Z mm, A/B/C deg ZYX-Euler)
    /// and returns the controller's echoed value. Used to push a rotary-bed calibration back to the
    /// robot so coordinated motion matches the model. Requires a live C3 connection.
    /// </summary>
    public Task<string> WriteBaseDataAsync(int index,
        double x, double y, double z, double a, double b, double c, CancellationToken ct = default)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string frame = string.Format(inv,
            "{{X {0:F3}, Y {1:F3}, Z {2:F3}, A {3:F4}, B {4:F4}, C {5:F4}}}", x, y, z, a, b, c);
        return _sync.WriteVarAsync($"BASE_DATA[{index}]", frame, ct);
    }

    /// <summary>
    /// Copies the bundled bed-calibration KRL to the controller's program folder over SMB.
    /// Returns the destination path. Throws on copy failure (share unreachable / permissions).
    /// </summary>
    public string DeployBedScanProgram()
    {
        const string fileName = "BED_SCAN_CAL.src";
        var src = ResolveBundledKrlPath(fileName)
            ?? throw new System.IO.FileNotFoundException(
                $"Bundled KRL not found ({fileName}). Rebuild the app or copy it to assets/krl/.");
        var folder = $@"\\{_bridgeIp}\krc\ROBOTER\KRC\R1\Program";
        var dest = System.IO.Path.Combine(folder, fileName);
        System.IO.File.Copy(src, dest, overwrite: true);
        return dest;
    }

    /// <summary>Copies the bundled SCAN_TOOL_CAL.src (3D scan-tool hand-eye sweep) to the controller share.</summary>
    public string DeployScanToolProgram()
    {
        const string fileName = "SCAN_TOOL_CAL.src";
        var src = ResolveBundledKrlPath(fileName)
            ?? throw new System.IO.FileNotFoundException(
                $"Bundled KRL not found ({fileName}). Rebuild the app or copy it to assets/krl/.");
        var folder = $@"\\{_bridgeIp}\krc\ROBOTER\KRC\R1\Program";
        var dest = System.IO.Path.Combine(folder, fileName);
        System.IO.File.Copy(src, dest, overwrite: true);
        return dest;
    }

    /// <summary>Locates a file under <c>assets/krl/</c> from the publish dir or repo working tree.</summary>
    internal static string? ResolveBundledKrlPath(string fileName)
    {
        var candidates = new[]
        {
            System.IO.Path.Combine(AppContext.BaseDirectory, "assets", "krl", fileName),
            System.IO.Path.Combine("assets", "krl", fileName),
            System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "assets", "krl", fileName)),
        };
        return candidates.FirstOrDefault(System.IO.File.Exists);
    }

    /// <summary>
    /// Disconnects the C3Bridge live feed if currently connected.
    /// Call before driving joints via IK (e.g. toolpath scrubbing) so the live
    /// position stream does not overwrite the IK result.
    /// </summary>
    public void Desync()
    {
        if (_sync.IsConnected)
            _sync.Disconnect();
    }

    private async void ToggleConnect()
    {
        if (_sync.IsConnected)
        {
            _sync.Disconnect();
            return;
        }

        ConnectionStatus = ConnectionStatus.Syncing;
        try
        {
            await _sync.ConnectAsync(_bridgeIp, _bridgePort);
            _sync.StartStreaming(100);
        }
        catch
        {
            ConnectionStatus = ConnectionStatus.Error;
        }
    }

    // -- Joint limits (driven by cell config) --------------------------------

    /// <summary>Applies joint limits and home position from the loaded cell config.</summary>
    public void Configure(IReadOnlyList<JointConfig> joints, float[] home)
    {
        _loadingLimits = true;
        _joints = joints;
        if (joints.Count >= 6)
        {
            MinA1 = joints[0].MinDeg; MaxA1 = joints[0].MaxDeg;
            MinA2 = joints[1].MinDeg; MaxA2 = joints[1].MaxDeg;
            MinA3 = joints[2].MinDeg; MaxA3 = joints[2].MaxDeg;
            MinA4 = joints[3].MinDeg; MaxA4 = joints[3].MaxDeg;
            MinA5 = joints[4].MinDeg; MaxA5 = joints[4].MaxDeg;
            MinA6 = joints[5].MinDeg; MaxA6 = joints[5].MaxDeg;
        }
        _loadingLimits = false;
        LimitsRequireColdReboot = false;
        LimitsStatus = "";
        NotifySimLimits();
        ApplyViewportJoints(home);
    }

    void SetSoftLimit(ref double field, double value)
    {
        if (!SetField(ref field, value)) return;
        if (_loadingLimits) return;
        NotifySimLimits();
        SyncLiveJointConfigs();
        LimitsRequireColdReboot = true;
        LimitsStatus = IsConnected
            ? "Writing $SOFTN_END / $SOFTP_END — requires a KUKA cold reboot."
            : "Changed locally. Sync Robot to write $SOFTN_END / $SOFTP_END. Requires a KUKA cold reboot.";
        QueuePushSoftLimits();
    }

    void NotifySimLimits()
    {
        OnPropertyChanged(nameof(SimMinA1)); OnPropertyChanged(nameof(SimMaxA1));
        OnPropertyChanged(nameof(SimMinA2)); OnPropertyChanged(nameof(SimMaxA2));
        OnPropertyChanged(nameof(SimMinA3)); OnPropertyChanged(nameof(SimMaxA3));
        OnPropertyChanged(nameof(SimMinA4)); OnPropertyChanged(nameof(SimMaxA4));
        OnPropertyChanged(nameof(SimMinA5)); OnPropertyChanged(nameof(SimMaxA5));
        OnPropertyChanged(nameof(SimMinA6)); OnPropertyChanged(nameof(SimMaxA6));
        OnPropertyChanged(nameof(SimMinE1)); OnPropertyChanged(nameof(SimMaxE1));
        // Re-clamp live angles into the new envelope.
        A1 = A1; A2 = A2; A3 = A3; A4 = A4; A5 = A5; A6 = A6; E1 = E1;
    }

    void SyncLiveJointConfigs()
    {
        if (_joints.Count < 6) return;
        _joints[0].MinDeg = (float)MinA1; _joints[0].MaxDeg = (float)MaxA1;
        _joints[1].MinDeg = (float)MinA2; _joints[1].MaxDeg = (float)MaxA2;
        _joints[2].MinDeg = (float)MinA3; _joints[2].MaxDeg = (float)MaxA3;
        _joints[3].MinDeg = (float)MinA4; _joints[3].MaxDeg = (float)MaxA4;
        _joints[4].MinDeg = (float)MinA5; _joints[4].MaxDeg = (float)MaxA5;
        _joints[5].MinDeg = (float)MinA6; _joints[5].MaxDeg = (float)MaxA6;
    }

    void QueuePushSoftLimits()
    {
        _limitPushTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _limitPushTimer.Tick -= OnLimitPushTick;
        _limitPushTimer.Tick += OnLimitPushTick;
        _limitPushTimer.Stop();
        _limitPushTimer.Start();
    }

    async void OnLimitPushTick(object? sender, EventArgs e)
    {
        _limitPushTimer?.Stop();
        await PushSoftLimitsToRobotAsync();
    }

    async Task PushSoftLimitsToRobotAsync()
    {
        if (!IsConnected)
            return;
        try
        {
            PauseStreaming();
            var pairs = new (string Name, double Value)[]
            {
                ("$SOFTN_END[1]", MinA1), ("$SOFTP_END[1]", MaxA1),
                ("$SOFTN_END[2]", MinA2), ("$SOFTP_END[2]", MaxA2),
                ("$SOFTN_END[3]", MinA3), ("$SOFTP_END[3]", MaxA3),
                ("$SOFTN_END[4]", MinA4), ("$SOFTP_END[4]", MaxA4),
                ("$SOFTN_END[5]", MinA5), ("$SOFTP_END[5]", MaxA5),
                ("$SOFTN_END[6]", MinA6), ("$SOFTP_END[6]", MaxA6),
                ("$SOFTN_END[7]", MinE1), ("$SOFTP_END[7]", MaxE1),
            };
            foreach (var (name, value) in pairs)
                await _sync.WriteVarAsync(name, value.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture));
            LimitsStatus = "Wrote $SOFTN_END / $SOFTP_END. Requires a KUKA cold reboot.";
        }
        catch (Exception ex)
        {
            LimitsStatus = "Write failed: " + ex.Message;
        }
        finally
        {
            ResumeStreaming();
        }
    }

    /// <summary>Sets the viewport robot to <paramref name="home"/> (A1–A6, KRL degrees) without moving the real robot.</summary>
    public void ApplyViewportJoints(float[] home)
    {
        if (home.Length < 6) return;
        A1 = home[0]; A2 = home[1]; A3 = home[2];
        A4 = home[3]; A5 = home[4]; A6 = home[5];
    }

    private float _defaultToolheadA;
    private float _defaultToolheadB;
    private float _defaultToolheadC;

    /// <summary>Cell default toolhead ABC (KUKA ZYX deg) — used by Go To Bed Center IK orientation.</summary>
    public void SetDefaultToolheadOrientation(double a, double b, double c)
    {
        _defaultToolheadA = (float)a;
        _defaultToolheadB = (float)b;
        _defaultToolheadC = (float)c;
    }

    // -- TCP readout -----------------------------------------------------------

    private double _tcpX, _tcpY, _tcpZ;
    private double _tcpA, _tcpB, _tcpC;

    /// <summary>TCP X in the current BASE # (mm). Live = $POS_ACT; otherwise scene FK.</summary>
    public double TcpX { get => _tcpX; set => SetField(ref _tcpX, value); }
    /// <summary>TCP Y in the current BASE # (mm).</summary>
    public double TcpY { get => _tcpY; set => SetField(ref _tcpY, value); }
    /// <summary>TCP Z in the current BASE # (mm).</summary>
    public double TcpZ { get => _tcpZ; set => SetField(ref _tcpZ, value); }
    /// <summary>TCP A (Euler Z) in the current BASE #, degrees.</summary>
    public double TcpA { get => _tcpA; set => SetField(ref _tcpA, value); }
    /// <summary>TCP B (Euler Y) in the current BASE #, degrees.</summary>
    public double TcpB { get => _tcpB; set => SetField(ref _tcpB, value); }
    /// <summary>TCP C (Euler X) in the current BASE #, degrees.</summary>
    public double TcpC { get => _tcpC; set => SetField(ref _tcpC, value); }

    /// <summary>Section header: TCP ACTUAL (BASE n).</summary>
    public string TcpActualTitle
        => KrlBaseIndex > 0 ? $"TCP ACTUAL (BASE {KrlBaseIndex})" : "TCP ACTUAL";

    // -- Flange readout (ROBROOT frame, from scene graph) ---------------------

    private double _flangeX, _flangeY, _flangeZ;

    /// <summary>Flange X position in ROBROOT frame (mm) -- from scene graph FK.</summary>
    public double FlangeX { get => _flangeX; set => SetField(ref _flangeX, value); }
    /// <summary>Flange Y position in ROBROOT frame (mm) -- from scene graph FK.</summary>
    public double FlangeY { get => _flangeY; set => SetField(ref _flangeY, value); }
    /// <summary>Flange Z position in ROBROOT frame (mm) -- from scene graph FK.</summary>
    public double FlangeZ { get => _flangeZ; set => SetField(ref _flangeZ, value); }

    // -- Rendered TCP readout (scene world frame, from scene graph FK) --------
    // TcpX/Y/Z above are overwritten by the controller's BASE-frame pose while
    // synced, so they cannot be used to check where the app actually DRAWS the
    // nozzle. These hold the scene-graph answer and are never overwritten by the
    // live sync -- `cal-check` diffs them against the controller to measure the
    // cell calibration error.

    private double _sceneTcpX, _sceneTcpY, _sceneTcpZ;

    /// <summary>Rendered nozzle-tip X in scene world mm -- never overwritten by live sync.</summary>
    public double SceneTcpX { get => _sceneTcpX; set => SetField(ref _sceneTcpX, value); }
    /// <summary>Rendered nozzle-tip Y in scene world mm.</summary>
    public double SceneTcpY { get => _sceneTcpY; set => SetField(ref _sceneTcpY, value); }
    /// <summary>Rendered nozzle-tip Z in scene world mm.</summary>
    public double SceneTcpZ { get => _sceneTcpZ; set => SetField(ref _sceneTcpZ, value); }

    // -- Controller TCP readout (BASE frame, straight off the controller) ------
    // TcpX/Y/Z are written by BOTH the live sync and SyncTcpReadout, so while the
    // robot is moving they race and whichever ran last wins. These are written by
    // the sync handler ONLY, so `cal-check` always compares two genuinely
    // independent answers instead of the scene against itself.

    private double _ctlTcpX, _ctlTcpY, _ctlTcpZ, _ctlTcpA, _ctlTcpB, _ctlTcpC;

    /// <summary>Controller-reported TCP X in BASE-frame mm.</summary>
    public double CtlTcpX { get => _ctlTcpX; set => SetField(ref _ctlTcpX, value); }
    /// <summary>Controller-reported TCP Y in BASE-frame mm.</summary>
    public double CtlTcpY { get => _ctlTcpY; set => SetField(ref _ctlTcpY, value); }
    /// <summary>Controller-reported TCP Z in BASE-frame mm.</summary>
    public double CtlTcpZ { get => _ctlTcpZ; set => SetField(ref _ctlTcpZ, value); }
    /// <summary>Controller-reported TCP A (deg).</summary>
    public double CtlTcpA { get => _ctlTcpA; set => SetField(ref _ctlTcpA, value); }
    /// <summary>Controller-reported TCP B (deg).</summary>
    public double CtlTcpB { get => _ctlTcpB; set => SetField(ref _ctlTcpB, value); }
    /// <summary>Controller-reported TCP C (deg).</summary>
    public double CtlTcpC { get => _ctlTcpC; set => SetField(ref _ctlTcpC, value); }

    // -- Solver FK readout (ROBROOT frame, from solver FK) --------------------
    // Updated by GoToBedCenter to show what the solver thinks the position is.
    // Should match FlangeX/Y/Z; a mismatch reveals an FK discrepancy.

    private double _solverFkX, _solverFkY, _solverFkZ;

    /// <summary>Solver FK flange X in ROBROOT frame (mm) -- from IK solver's internal FK.</summary>
    public double SolverFkX { get => _solverFkX; set => SetField(ref _solverFkX, value); }
    /// <summary>Solver FK flange Y in ROBROOT frame (mm).</summary>
    public double SolverFkY { get => _solverFkY; set => SetField(ref _solverFkY, value); }
    /// <summary>Solver FK flange Z in ROBROOT frame (mm).</summary>
    public double SolverFkZ { get => _solverFkZ; set => SetField(ref _solverFkZ, value); }

    // -- IK target orientation ------------------------------------------------
    // Orientation held fixed while dragging the TCP marker in the viewport.

    private double _ikTargetA = 0;
    private double _ikTargetB = 0;
    private double _ikTargetC = 0;

    /// <summary>Target TCP A (Euler Z) in degrees for IK drag.</summary>
    public double IkTargetA { get => _ikTargetA; set => SetField(ref _ikTargetA, value); }
    /// <summary>Target TCP B (Euler Y) in degrees for IK drag.</summary>
    public double IkTargetB { get => _ikTargetB; set => SetField(ref _ikTargetB, value); }
    /// <summary>Target TCP C (Euler X) in degrees for IK drag.</summary>
    public double IkTargetC { get => _ikTargetC; set => SetField(ref _ikTargetC, value); }

    // -- Tool selection --------------------------------------------------------

    private IReadOnlyList<ToolCellConfig> _toolLibrary = [];
    private int _selectedToolIndex = 0;

    /// <summary>Display names for the available tool heads.</summary>
    public ObservableCollection<string> ToolNames { get; } = [];

    /// <summary>Index of the currently selected tool in <see cref="ToolNames"/>.</summary>
    public int SelectedToolIndex
    {
        get => _selectedToolIndex;
        set
        {
            if (!SetField(ref _selectedToolIndex, value)) return;
            if ((uint)value < (uint)_toolLibrary.Count)
            {
                // Keep ROBOT FRAMES TOOL dropdown and KrlToolIndex in sync.
                // Set the backing field directly (not the property) to avoid re-entering
                // KrlToolSelectedIndex.set, which only calls back when i != _selectedToolIndex anyway.
                int krl  = _toolLibrary[value].KrlIndex;
                int slot = _krlToolIndices.IndexOf(krl);
                if (slot >= 0 && slot != _krlToolSelectedIndex)
                {
                    _krlToolSelectedIndex = slot;
                    OnPropertyChanged(nameof(KrlToolSelectedIndex));
                }
                if (KrlToolIndex != krl)
                {
                    KrlToolIndex = krl;
                    OnPropertyChanged(nameof(KrlToolIndex));
                }
                OnToolSelected?.Invoke(_toolLibrary[value]);
                LoadToolTcpEdit(_toolLibrary[value]);
            }
        }
    }

    /// <summary>
    /// Callback invoked when the user selects a different tool.
    /// Wired by <c>ViewportView</c> to trigger the async load + swap.
    /// </summary>
    internal Action<ToolCellConfig>? OnToolSelected { get; set; }

    /// <summary>Populates the tool selector from the cell's effective tool list.</summary>
    public void SetToolLibrary(IReadOnlyList<ToolCellConfig> tools)
    {
        _toolLibrary = tools;
        ToolNames.Clear();
        foreach (var t in tools)
            ToolNames.Add(string.IsNullOrEmpty(t.Name) ? t.ModelPath : t.Name);
        int def = 0;
        for (int i = 0; i < tools.Count; i++)
        {
            if (tools[i].Default) { def = i; break; }
        }
        _selectedToolIndex = def;
        OnPropertyChanged(nameof(SelectedToolIndex));
        if (tools.Count > 0)
            LoadToolTcpEdit(tools[def]);
    }

    // -- TCP offset editing -------------------------------------------------------

    private double _editTcpX, _editTcpY, _editTcpZ, _editTcpA, _editTcpB, _editTcpC;
    private bool _suppressTcpCallback;

    public double EditTcpX { get => _editTcpX; set { if (SetField(ref _editTcpX, value)) FireTcpEdited(); } }
    public double EditTcpY { get => _editTcpY; set { if (SetField(ref _editTcpY, value)) FireTcpEdited(); } }
    public double EditTcpZ { get => _editTcpZ; set { if (SetField(ref _editTcpZ, value)) FireTcpEdited(); } }
    public double EditTcpA { get => _editTcpA; set { if (SetField(ref _editTcpA, value)) FireTcpEdited(); } }
    public double EditTcpB { get => _editTcpB; set { if (SetField(ref _editTcpB, value)) FireTcpEdited(); } }
    public double EditTcpC { get => _editTcpC; set { if (SetField(ref _editTcpC, value)) FireTcpEdited(); } }

    internal Action<double, double, double, double, double, double>? OnTcpOffsetEdited { get; set; }

    // -- Tool axis convention (display triad only) -----------------------------

    public IReadOnlyList<ToolAxisConventionOption> ToolAxisConventionOptions { get; } =
        ToolAxisConventionOption.All;

    private ToolAxisConventionOption _selectedToolAxisConvention = ToolAxisConventionOption.Default;

    /// <summary>Which taught tool axis is drawn as forward (away from the flange).</summary>
    public ToolAxisConventionOption SelectedToolAxisConvention
    {
        get => _selectedToolAxisConvention ?? ToolAxisConventionOption.Default;
        set
        {
            var next = value ?? ToolAxisConventionOption.Default;
            if (!SetField(ref _selectedToolAxisConvention, next)) return;
            OnPropertyChanged(nameof(SelectedToolAxisConventionIndex));
            OnToolAxisConventionChanged?.Invoke(next.Kind);
        }
    }

    /// <summary>ComboBox index twin of <see cref="SelectedToolAxisConvention"/> (avoids Avalonia SelectedItem nulls).</summary>
    public int SelectedToolAxisConventionIndex
    {
        get => (int)SelectedToolAxisConvention.Kind;
        set
        {
            var opt = ToolAxisConventionOption.All.FirstOrDefault(o => (int)o.Kind == value)
                      ?? ToolAxisConventionOption.Default;
            SelectedToolAxisConvention = opt;
        }
    }

    internal Action<ToolAxisConvention>? OnToolAxisConventionChanged { get; set; }

    private void FireTcpEdited()
    {
        if (!_suppressTcpCallback)
            OnTcpOffsetEdited?.Invoke(_editTcpX, _editTcpY, _editTcpZ, _editTcpA, _editTcpB, _editTcpC);
    }

    private void LoadToolTcpEdit(ToolCellConfig t)
    {
        _suppressTcpCallback = true;
        EditTcpX = t.TcpX;
        EditTcpY = t.TcpY;
        EditTcpZ = t.TcpZ;
        EditTcpA = t.TcpA;
        EditTcpB = t.TcpB;
        EditTcpC = t.TcpC;
        _suppressTcpCallback = false;
    }

    /// <summary>Selects the cell tool that maps to the given KRL TOOL_DATA index (e.g. the
    /// calibrated scanner at index 6), mounting it and loading its TCP. Returns false if no
    /// tool in the active cell uses that index.</summary>
    public bool SelectToolByKrlIndex(int krlIndex)
    {
        for (int i = 0; i < _toolLibrary.Count; i++)
        {
            if (_toolLibrary[i].KrlIndex == krlIndex)
            {
                SelectedToolIndex = i;   // syncs KrlToolIndex, mounts the tool, loads its TCP
                return true;
            }
        }
        return false;
    }

    /// <summary>Pushes a TCP offset through the live edit path: updates the IK/render TCP and
    /// persists it to the currently selected tool in the cell JSON (same as a manual TCP edit).</summary>
    public void ApplyTcpOffset(double x, double y, double z, double a, double b, double c)
    {
        // Set the backing fields with the save callback suppressed, then fire it once so the
        // viewport rebuilds and saves a single time (not six times, once per field).
        _suppressTcpCallback = true;
        EditTcpX = x; EditTcpY = y; EditTcpZ = z;
        EditTcpA = a; EditTcpB = b; EditTcpC = c;
        _suppressTcpCallback = false;
        FireTcpEdited();
    }

    // -- KRL frame dropdowns ---------------------------------------------------

    private readonly List<int> _krlToolIndices = [];
    private readonly List<int> _krlBaseIndices = [];

    public ObservableCollection<string> KrlToolOptions { get; } = [];
    public ObservableCollection<string> KrlBaseOptions { get; } = [];

    private int _krlToolSelectedIndex = -1;
    private int _krlBaseSelectedIndex = -1;

    public int KrlToolSelectedIndex
    {
        get => _krlToolSelectedIndex;
        set
        {
            if (!SetField(ref _krlToolSelectedIndex, value)) return;
            if ((uint)value < (uint)_krlToolIndices.Count)
            {
                KrlToolIndex = _krlToolIndices[value];
                OnPropertyChanged(nameof(KrlToolIndex));

                // Sync the viewport tool so the TCP gizmo updates to match the selected KRL tool.
                for (int i = 0; i < _toolLibrary.Count; i++)
                {
                    if (_toolLibrary[i].KrlIndex == KrlToolIndex && i != _selectedToolIndex)
                    {
                        SelectedToolIndex = i;
                        break;
                    }
                }
            }
        }
    }

    public int KrlBaseSelectedIndex
    {
        get => _krlBaseSelectedIndex;
        set
        {
            if (!SetField(ref _krlBaseSelectedIndex, value)) return;
            if ((uint)value < (uint)_krlBaseIndices.Count)
            {
                KrlBaseIndex = _krlBaseIndices[value];
                OnPropertyChanged(nameof(KrlBaseIndex));
                OnPropertyChanged(nameof(TcpActualTitle));
            }
        }
    }

    public int KrlToolIndex { get; private set; }
    public int KrlBaseIndex { get; private set; }

    // -- Nozzle sizing ----------------------------------------------------------
    // Placeholder preset list -- Nick is supplying the real preset values once he's
    // seen this in the app.

    private static readonly double[] NozzleDiameterPresetsMm = [4.0, 6.0, 8.0, 10.0];

    /// <summary>Preset nozzle/bead diameters shown between TOOL # and BASE #.</summary>
    public ObservableCollection<string> NozzleSizeOptions { get; } =
        new(NozzleDiameterPresetsMm.Select(mm => $"{mm:0.#} mm"));

    private int _nozzleSizeSelectedIndex = 1; // 6 mm -- matches SliceSettings.BeadWidth's default

    public int NozzleSizeSelectedIndex
    {
        get => _nozzleSizeSelectedIndex;
        set
        {
            if ((uint)value >= (uint)NozzleDiameterPresetsMm.Length) return;
            if (!SetField(ref _nozzleSizeSelectedIndex, value)) return;
            OnPropertyChanged(nameof(NozzleDiameterMm));
        }
    }

    /// <summary>Selected nozzle diameter in mm -- feeds the slicer's bead width.</summary>
    public double NozzleDiameterMm => NozzleDiameterPresetsMm[_nozzleSizeSelectedIndex];

    /// <summary>Populates the KRL Tool and Base dropdowns from cell data.</summary>
    public void SetKrlFrameOptions(
        IReadOnlyList<ToolCellConfig> tools,
        IReadOnlyList<KrlBaseEntry>   bases,
        int                           currentToolIndex,
        int                           currentBaseIndex)
    {
        _krlToolIndices.Clear();
        KrlToolOptions.Clear();
        foreach (var t in tools.Where(t => t.KrlIndex > 0).OrderBy(t => t.KrlIndex))
        {
            _krlToolIndices.Add(t.KrlIndex);
            KrlToolOptions.Add($"{t.KrlIndex}: {t.Name}");
        }

        _krlBaseIndices.Clear();
        KrlBaseOptions.Clear();
        foreach (var b in bases.OrderBy(b => b.Index))
        {
            _krlBaseIndices.Add(b.Index);
            KrlBaseOptions.Add($"{b.Index}: {b.Name}");
        }

        // Prefer the cell's designated default tool (e.g. LFAM 3 → HV Extruder / TOOL_NO 1)
        // so each cell exports with its intended KRL tool. Falls back to the caller's current
        // selection, then the lowest-index tool. Called only on cell swap, so honoring the
        // cell default here is correct (it doesn't override a mid-session dropdown pick).
        int defaultKrlTool = tools.FirstOrDefault(t => t.Default && t.KrlIndex > 0)?.KrlIndex ?? 0;
        int wantToolIndex  = defaultKrlTool > 0 ? defaultKrlTool : currentToolIndex;
        var ti = _krlToolIndices.IndexOf(wantToolIndex);
        _krlToolSelectedIndex = ti >= 0 ? ti : 0;
        OnPropertyChanged(nameof(KrlToolSelectedIndex));
        if (_krlToolIndices.Count > 0)
        {
            KrlToolIndex = _krlToolIndices[Math.Max(0, _krlToolSelectedIndex)];
            OnPropertyChanged(nameof(KrlToolIndex));
        }

        var bi = _krlBaseIndices.IndexOf(currentBaseIndex);
        _krlBaseSelectedIndex = bi >= 0 ? bi : 0;
        OnPropertyChanged(nameof(KrlBaseSelectedIndex));
        if (_krlBaseIndices.Count > 0)
        {
            KrlBaseIndex = _krlBaseIndices[Math.Max(0, _krlBaseSelectedIndex)];
            OnPropertyChanged(nameof(KrlBaseIndex));
            OnPropertyChanged(nameof(TcpActualTitle));
        }
    }

    // -- IK --------------------------------------------------------------------

    private Vector3 _bedCenterRobot;
    private Vector3 _tcpOffsetLocal;
    private Vector3 _robotWorldPos;

    /// <summary>
    /// GLTF-based numerical IK solver. Set by <c>ViewportView</c> once the robot
    /// model and cell config are both loaded.
    /// </summary>
    internal GltfNumericalIkSolver? IkSolver { get; set; }

    /// <summary>
    /// Supplies bed-center position and TCP offset so <see cref="GoToBedCenterCommand"/> can run.
    /// <paramref name="bedCenterRobot"/> is the nozzle target in ROBROOT frame (mm).
    /// <paramref name="robotWorldPos"/> is the robot base in world/scene frame (mm) -- used for the TCP readout.
    /// </summary>
    public void SetIkData(Vector3 bedCenterRobot, Vector3 tcpOffset, Vector3 robotWorldPos)
    {
        _bedCenterRobot = bedCenterRobot;
        _tcpOffsetLocal = tcpOffset;
        _robotWorldPos  = robotWorldPos;
    }

    // -- BASE frame readout (from cell config) ---------------------------------

    private double _baseX, _baseY, _baseZ;

    /// <summary>BASE frame origin X in ROBROOT frame (mm), sourced from cell config.</summary>
    public double BaseX { get => _baseX; private set => SetField(ref _baseX, value); }
    /// <summary>BASE frame origin Y in ROBROOT frame (mm), sourced from cell config.</summary>
    public double BaseY { get => _baseY; private set => SetField(ref _baseY, value); }
    /// <summary>BASE frame origin Z in ROBROOT frame (mm), sourced from cell config.</summary>
    public double BaseZ { get => _baseZ; private set => SetField(ref _baseZ, value); }

    /// <summary>Updates the BASE frame origin display from the loaded cell's bed.baseData.</summary>
    public void SetBaseFrameData(float x, float y, float z)
    {
        BaseX = Math.Round(x, 2);
        BaseY = Math.Round(y, 2);
        BaseZ = Math.Round(z, 2);
    }

    /// <summary>
    /// Writes TCP ACTUAL in the current BASE (scene world minus ROBROOT minus BASE_DATA).
    /// No-op while live-synced — <c>TcpUpdated</c> already has controller $POS_ACT.
    /// </summary>
    public void SetTcpActualFromSceneWorld(
        double worldX, double worldY, double worldZ,
        double aDeg, double bDeg, double cDeg,
        double robrootX, double robrootY, double robrootZ)
    {
        if (IsConnected) return;
        TcpX = Math.Round(worldX - robrootX - BaseX, 1);
        TcpY = Math.Round(worldY - robrootY - BaseY, 1);
        TcpZ = Math.Round(worldZ - robrootZ - BaseZ, 1);
        TcpA = Math.Round(aDeg, 2);
        TcpB = Math.Round(bDeg, 2);
        TcpC = Math.Round(cDeg, 2);
    }

    public ICommand GoToBedCenterCommand { get; }

    // -- Save as home position -------------------------------------------------

    private string _newHomePositionName = "Home Target 1";

    /// <summary>Name field for a new user-saved home position (bound to the TextBox in the Robot tab).</summary>
    public string NewHomePositionName
    {
        get => _newHomePositionName;
        set => SetField(ref _newHomePositionName, value);
    }

    /// <summary>Updates the suggested name after a cell load or a save (e.g. "Home Target 3").</summary>
    public void SetNextPositionName(int nextIndex)
        => NewHomePositionName = $"Home Target {nextIndex}";

    /// <summary>Callback invoked when the user saves the current orientation as a home position.</summary>
    internal Action<string, float[]>? OnSaveHomePositionRequested { get; set; }

    public ICommand SaveAsHomePositionCommand { get; }

    private void SaveAsHomePosition()
    {
        var name   = string.IsNullOrWhiteSpace(NewHomePositionName)
                         ? "Home Target 1"
                         : NewHomePositionName.Trim();
        var angles = new float[] { (float)A1, (float)A2, (float)A3, (float)A4, (float)A5, (float)A6 };
        OnSaveHomePositionRequested?.Invoke(name, angles);
    }

    public RobotPanelViewModel()
    {
        GoToBedCenterCommand       = new RelayCommand(GoToBedCenter);
        ConnectCommand             = new RelayCommand(ToggleConnect);
        SaveAsHomePositionCommand  = new RelayCommand(SaveAsHomePosition);

        _sync.Connected     += (_, _)  => Dispatcher.UIThread.Post(() => ConnectionStatus = ConnectionStatus.Ready);
        _sync.Disconnected  += (_, _)  => Dispatcher.UIThread.Post(() => ConnectionStatus = ConnectionStatus.Disconnected);

        _sync.AxesUpdated   += (_, axes) => Dispatcher.UIThread.Post(() =>
        {
            A1 = Math.Round(axes[0], 2);
            A2 = Math.Round(axes[1], 2);
            A3 = Math.Round(axes[2], 2);
            A4 = Math.Round(axes[3], 2);
            A5 = Math.Round(axes[4], 2);
            A6 = Math.Round(axes[5], 2);
            if (axes.Length > 6) E1 = Math.Round(axes[6], 2);
        });

        _sync.TcpUpdated += (_, pos) => Dispatcher.UIThread.Post(() =>
        {
            TcpX = Math.Round(pos.X, 1);
            TcpY = Math.Round(pos.Y, 1);
            TcpZ = Math.Round(pos.Z, 1);
            TcpA = Math.Round(pos.A, 3);
            TcpB = Math.Round(pos.B, 3);
            TcpC = Math.Round(pos.C, 3);

            // Uncontested copy for calibration comparisons -- see CtlTcpX.
            CtlTcpX = TcpX; CtlTcpY = TcpY; CtlTcpZ = TcpZ;
            CtlTcpA = TcpA; CtlTcpB = TcpB; CtlTcpC = TcpC;
        });

        _sync.IoSnapshotUpdated += (_, snapshot) =>
            Dispatcher.UIThread.Post(() => IoSnapshotUpdated?.Invoke(this, snapshot));
    }

    private void GoToBedCenter()
    {
        if (IkSolver is null) return;

        var target = new OpenTK.Mathematics.Vector3(_bedCenterRobot.X, _bedCenterRobot.Y, _bedCenterRobot.Z);
        var seed   = new float[] { (float)A1, (float)A2, (float)A3, (float)A4, (float)A5, (float)A6 };
        var rot    = IkSolver.TargetRotFromKukaAbc(_defaultToolheadA, _defaultToolheadB, _defaultToolheadC);
        var result = IkSolver.Solve(target, seed, rot);
        if (result is null) return;

        var solverTcp = IkSolver.ComputeTcpPosScene(result);

        SolverFkX = Math.Round(solverTcp.X - _robotWorldPos.X, 1);
        SolverFkY = Math.Round(solverTcp.Y - _robotWorldPos.Y, 1);
        SolverFkZ = Math.Round(solverTcp.Z - _robotWorldPos.Z, 1);

        A1 = Math.Round(result[0], 2);
        A2 = Math.Round(result[1], 2);
        A3 = Math.Round(result[2], 2);
        A4 = Math.Round(result[3], 2);
        A5 = Math.Round(result[4], 2);
        A6 = Math.Round(result[5], 2);
    }
}
