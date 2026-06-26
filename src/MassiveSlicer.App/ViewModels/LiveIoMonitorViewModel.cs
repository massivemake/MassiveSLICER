using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Threading;
using MassiveSlicer.Commands;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;
using MassiveSlicer.ViewModels.Base;

namespace MassiveSlicer.ViewModels;

public sealed class LiveIoSectionViewModel : ViewModelBase
{
    public string Title { get; }
    public ObservableCollection<LiveIoSignalViewModel> Signals { get; } = [];
    public ObservableCollection<LiveIoSignalViewModel> InputSignals { get; } = [];
    public ObservableCollection<LiveIoSignalViewModel> OutputSignals { get; } = [];

    public bool UsesSplitIoLayout { get; }

    /// <summary>Robot section shows live axis + TCP readout from C3Bridge sync.</summary>
    public bool ShowRobotPose { get; }

    /// <summary>Robot I/O uses Position | Inputs | Outputs columns.</summary>
    public bool UsesRobotTripleLayout => ShowRobotPose;

    /// <summary>Extruder-style Inputs | Outputs only (no position column).</summary>
    public bool UsesDualIoLayout => UsesSplitIoLayout && !ShowRobotPose;

    private bool _isRobotPoseLive;
    public bool IsRobotPoseLive
    {
        get => _isRobotPoseLive;
        set
        {
            if (!SetField(ref _isRobotPoseLive, value)) return;
            NotifyRobotPoseLines();
        }
    }

    private double _a1, _a2, _a3, _a4, _a5, _a6, _e1;
    private double _tcpX, _tcpY, _tcpZ, _tcpA, _tcpB, _tcpC;

    public double A1 { get => _a1; set => SetField(ref _a1, value); }
    public double A2 { get => _a2; set => SetField(ref _a2, value); }
    public double A3 { get => _a3; set => SetField(ref _a3, value); }
    public double A4 { get => _a4; set => SetField(ref _a4, value); }
    public double A5 { get => _a5; set => SetField(ref _a5, value); }
    public double A6 { get => _a6; set => SetField(ref _a6, value); }
    public double E1 { get => _e1; set => SetField(ref _e1, value); }
    public double TcpX { get => _tcpX; set => SetField(ref _tcpX, value); }
    public double TcpY { get => _tcpY; set => SetField(ref _tcpY, value); }
    public double TcpZ { get => _tcpZ; set => SetField(ref _tcpZ, value); }
    public double TcpA { get => _tcpA; set => SetField(ref _tcpA, value); }
    public double TcpB { get => _tcpB; set => SetField(ref _tcpB, value); }
    public double TcpC { get => _tcpC; set => SetField(ref _tcpC, value); }

    public string TcpPositionLine =>
        IsRobotPoseLive ? $"X {TcpX:F1}  Y {TcpY:F1}  Z {TcpZ:F1} mm" : "—";

    public string TcpOrientationLine =>
        IsRobotPoseLive ? $"A {TcpA:F2}  B {TcpB:F2}  C {TcpC:F2}°" : "—";

    private string _statusLine = "—";
    public string StatusLine
    {
        get => _statusLine;
        set => SetField(ref _statusLine, value);
    }

    public int PhaseNumber { get; init; }
    public bool IsPhaseLive { get; init; }

    internal LiveIoSectionViewModel(string title)
    {
        Title = title;
        UsesSplitIoLayout = title is "Robot (KUKA)" or "Pellet Extruder";
        ShowRobotPose = title == "Robot (KUKA)";
    }

    internal void ClearRobotPose()
    {
        IsRobotPoseLive = false;
        A1 = A2 = A3 = A4 = A5 = A6 = E1 = 0;
        TcpX = TcpY = TcpZ = TcpA = TcpB = TcpC = 0;
        NotifyRobotPoseLines();
    }

    internal void ApplyRobotPose(
        double a1, double a2, double a3, double a4, double a5, double a6, double e1,
        double tcpX, double tcpY, double tcpZ, double tcpA, double tcpB, double tcpC)
    {
        IsRobotPoseLive = true;
        A1 = a1; A2 = a2; A3 = a3; A4 = a4; A5 = a5; A6 = a6; E1 = e1;
        TcpX = tcpX; TcpY = tcpY; TcpZ = tcpZ;
        TcpA = tcpA; TcpB = tcpB; TcpC = tcpC;
        NotifyRobotPoseLines();
    }

    void NotifyRobotPoseLines()
    {
        OnPropertyChanged(nameof(TcpPositionLine));
        OnPropertyChanged(nameof(TcpOrientationLine));
    }

    internal void PartitionInputOutputSignals()
    {
        if (!UsesSplitIoLayout) return;
        InputSignals.Clear();
        OutputSignals.Clear();
        foreach (var row in Signals)
        {
            if (row.Config.Kind is LiveIoSignalKind.DigitalInput or LiveIoSignalKind.AnalogInput)
                InputSignals.Add(row);
            else
                OutputSignals.Add(row);
        }
    }
}

public sealed class LiveIoSignalViewModel : ViewModelBase
{
    internal LiveIoSignalConfig Config { get; }
    LiveIoMonitorViewModel _owner;

    public string Label => Config.Label;
    public string KindLabel => Config.Kind switch
    {
        LiveIoSignalKind.DigitalInput  => "DI",
        LiveIoSignalKind.DigitalOutput => "DO",
        LiveIoSignalKind.AnalogInput   => "AI",
        LiveIoSignalKind.AnalogOutput  => "AO",
        _                              => "—",
    };

    public bool IsDigital => Config.Kind is LiveIoSignalKind.DigitalInput or LiveIoSignalKind.DigitalOutput;
    public bool IsWritable => Config.Writable && Config.Source is
        LiveIoSource.Kuka or LiveIoSource.ExtruderIo28 or LiveIoSource.ExtruderBridge;

    bool? _lastBool;
    internal bool? LastBool => _lastBool;

    private string _displayValue = "—";
    public string DisplayValue
    {
        get => _displayValue;
        set => SetField(ref _displayValue, value);
    }

    private bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (!SetField(ref _isActive, value)) return;
            OnPropertyChanged(nameof(ShowInactive));
        }
    }

    private bool _isWarning;
    public bool IsWarning
    {
        get => _isWarning;
        set => SetField(ref _isWarning, value);
    }

    private bool _isFault;
    public bool IsFault
    {
        get => _isFault;
        set => SetField(ref _isFault, value);
    }

    public bool ShowInactive => IsDigital && !IsActive && !IsWarning && !IsFault;

    private string? _confirmMessage;
    public string? ConfirmMessage
    {
        get => _confirmMessage;
        set
        {
            if (!SetField(ref _confirmMessage, value)) return;
            OnPropertyChanged(nameof(IsConfirmPending));
        }
    }

    public bool IsConfirmPending => !string.IsNullOrEmpty(ConfirmMessage);

    public ICommand RequestToggleCommand { get; }
    public ICommand ConfirmToggleCommand  { get; }
    public ICommand CancelToggleCommand   { get; }

    internal LiveIoSignalViewModel(LiveIoSignalConfig config, LiveIoMonitorViewModel owner)
    {
        Config = config;
        _owner = owner;
        RequestToggleCommand = new RelayCommand(RequestToggle, () => IsWritable && !IsConfirmPending);
        ConfirmToggleCommand  = new RelayCommand(() => _ = owner.ConfirmToggleAsync(this), () => IsConfirmPending);
        CancelToggleCommand   = new RelayCommand(() => ConfirmMessage = null);
    }

    void RequestToggle()
    {
        if (!IsWritable) return;
        bool next = !(_lastBool ?? false);
        ConfirmMessage = $"Force {Label} {(next ? "ON" : "OFF")}?";
    }

    internal void ApplyRaw(string? raw)
    {
        DisplayValue = LiveIoValueFormatter.FormatDisplay(Config, raw);
        if (IsDigital)
        {
            var b = LiveIoValueFormatter.TryParseBool(raw);
            _lastBool = b;
            IsActive  = LiveIoValueFormatter.IsActiveIndicator(Config, b);
            IsWarning = LiveIoValueFormatter.IsWarningIndicator(Config, b);
            IsFault   = LiveIoValueFormatter.IsFaultIndicator(Config, b);
        }
        else
        {
            IsActive = false;
            IsWarning = false;
            IsFault = false;
        }
    }

    internal void MarkUnavailable()
    {
        DisplayValue = "—";
        _lastBool = null;
        IsActive = IsWarning = IsFault = false;
    }
}

/// <summary>Collapsible LFAM 3 live I/O monitor below the workflow timeline.</summary>
public sealed class LiveIoMonitorViewModel : ViewModelBase
{
    public ObservableCollection<LiveIoSectionViewModel> Sections { get; } = [];

    /// <summary>Robot + active workflow phase section(s) only.</summary>
    public ObservableCollection<LiveIoSectionViewModel> VisibleSections { get; } = [];

    bool _showExtruderSection;
    bool _showScannerSection;
    bool _showMillingSection;

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!SetField(ref _isExpanded, value)) return;
            OnPropertyChanged(nameof(ToggleLabel));
            OnPropertyChanged(nameof(ToggleIcon));
            OnPropertyChanged(nameof(Phase1Hint));
            RefreshVisibleSections();
            ApplyPolling();
            ExpandedChanged?.Invoke();
        }
    }

    public string ToggleLabel => IsExpanded ? "Hide Live I/O" : "Show Live I/O";
    public string ToggleIcon  => IsExpanded ? "mdi-chevron-down" : "mdi-chevron-up";

    /// <summary>One-line rollout status for the panel footer.</summary>
    public string PhaseRoadmapSummary => LiveIoPhasePlan.RoadmapSummary;

    /// <summary>Status hint for the active workflow phase I/O group.</summary>
    public string Phase1Hint
    {
        get
        {
            bool kuka = _robot?.IsConnected == true;
            bool bridge = _extruderBridgeLive;
            if (!kuka && bridge)
                return "Pos28 valves live — sync robot for KUKA I/O";
            if (!kuka && !string.IsNullOrEmpty(_extIp))
                return "Sync robot for KUKA I/O · extruder bridge configured";
            if (!kuka)
                return "Sync robot to start KUKA I/O polling";
            if (_showScannerSection)
                return "Scan phase — robot I/O + Pos28 valve pneumatics";
            if (_showMillingSection)
                return "Mill phase — robot I/O + spindle cabinet";
            return "Print phase — robot I/O + pellet extruder";
        }
    }

    public ICommand ToggleExpandedCommand { get; }

    internal event Action? ExpandedChanged;

    RobotPanelViewModel? _robot;
    ExtruderBridgeClient? _extruderBridge;
    MillingModbusClient? _millingBridge;
    CancellationTokenSource? _extruderPollCts;
    CancellationTokenSource? _millingPollCts;
    string? _extIp;
    int _extBridgePort = ExtruderBridgeClient.DefaultPort;
    string? _millIp;
    int _millBridgePort = MillingModbusClient.DefaultPort;
    bool _hasMilling;
    bool _extruderBridgeLive;
    bool _extruderModbusLive;
    bool _millingBridgeLive;
    readonly Dictionary<string, LiveIoSignalViewModel> _signalIndex = new(StringComparer.Ordinal);

    public LiveIoMonitorViewModel()
    {
        ToggleExpandedCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
        LoadCatalog(Lfam3LiveIoCatalog.Default);
    }

    /// <summary>Extruder RevPi bridge endpoint from the loaded cell config.</summary>
    public void SetExtruderBridgeConfig(string? ip, int port = ExtruderBridgeClient.DefaultPort)
    {
        _extIp = string.IsNullOrWhiteSpace(ip) ? null : ip.Trim();
        _extBridgePort = port > 0 ? port : ExtruderBridgeClient.DefaultPort;
        _extruderBridgeLive = false;
        _extruderModbusLive = false;
        UpdateSectionStatus();
        ApplyPolling();
        OnPropertyChanged(nameof(Phase1Hint));
    }

    /// <summary>Milling cabinet bridge endpoint from the loaded cell config.</summary>
    public void SetMillingBridgeConfig(string? ip, bool hasMilling, int port = MillingModbusClient.DefaultPort)
    {
        _hasMilling = hasMilling;
        _millIp = hasMilling && !string.IsNullOrWhiteSpace(ip) ? ip.Trim() : null;
        _millBridgePort = port > 0 ? port : MillingModbusClient.DefaultPort;
        _millingBridgeLive = false;
        UpdateSectionStatus();
        ApplyPolling();
        OnPropertyChanged(nameof(Phase1Hint));
    }

    public void AttachRobot(RobotPanelViewModel? robot)
    {
        if (_robot is not null)
        {
            _robot.IoSnapshotUpdated -= OnIoSnapshotUpdated;
            _robot.PropertyChanged    -= OnRobotPropertyChanged;
        }

        _robot = robot;
        if (_robot is not null)
        {
            _robot.IoSnapshotUpdated += OnIoSnapshotUpdated;
            _robot.PropertyChanged    += OnRobotPropertyChanged;
        }

        UpdateSectionStatus();
        UpdateRobotPoseSection();
        ApplyPolling();
    }

    void LoadCatalog(LiveIoConfig config)
    {
        Sections.Clear();
        _signalIndex.Clear();
        foreach (var section in config.Sections)
        {
            var phase = LiveIoPhasePlan.ForSection(section.Title);
            var vm = new LiveIoSectionViewModel(section.Title)
            {
                PhaseNumber = phase?.Number ?? 0,
                IsPhaseLive = phase?.Status == LiveIoPhaseStatus.Implemented,
            };
            foreach (var signal in section.Signals)
            {
                var row = new LiveIoSignalViewModel(signal, this);
                vm.Signals.Add(row);
                _signalIndex[SignalKey(signal)] = row;
            }
            if (vm.UsesSplitIoLayout)
                vm.PartitionInputOutputSignals();
            Sections.Add(vm);
        }
        UpdateSectionStatus();
        RefreshVisibleSections();
    }

    /// <summary>Filters visible I/O columns to match the LFAM 3 workflow phase.</summary>
    public void UpdateWorkflowPhase(bool showExtruder, bool showScanner, bool showMilling)
    {
        if (_showExtruderSection == showExtruder
            && _showScannerSection == showScanner
            && _showMillingSection == showMilling)
            return;

        _showExtruderSection = showExtruder;
        _showScannerSection  = showScanner;
        _showMillingSection  = showMilling;
        RefreshVisibleSections();
        OnPropertyChanged(nameof(Phase1Hint));
    }

    void RefreshVisibleSections()
    {
        VisibleSections.Clear();
        foreach (var section in Sections)
        {
            bool show = section.Title switch
            {
                "Robot (KUKA)"    => true,
                "Pellet Extruder" => _showExtruderSection,
                "Scanner"         => _showScannerSection,
                "Milling Spindle" => _showMillingSection,
                _                 => false,
            };
            if (show)
                VisibleSections.Add(section);
        }
    }

    static string SignalKey(LiveIoSignalConfig signal) => $"{signal.Source}:{signal.Key}";

    void OnRobotPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RobotPanelViewModel.IsConnected))
        {
            UpdateSectionStatus();
            ApplyPolling();
            UpdateRobotPoseSection();
            OnPropertyChanged(nameof(Phase1Hint));
            return;
        }

        if (IsRobotPoseProperty(e.PropertyName))
            UpdateRobotPoseSection();
    }

    static bool IsRobotPoseProperty(string? name) => name is
        nameof(RobotPanelViewModel.A1) or nameof(RobotPanelViewModel.A2) or
        nameof(RobotPanelViewModel.A3) or nameof(RobotPanelViewModel.A4) or
        nameof(RobotPanelViewModel.A5) or nameof(RobotPanelViewModel.A6) or
        nameof(RobotPanelViewModel.E1) or
        nameof(RobotPanelViewModel.TcpX) or nameof(RobotPanelViewModel.TcpY) or
        nameof(RobotPanelViewModel.TcpZ) or nameof(RobotPanelViewModel.TcpA) or
        nameof(RobotPanelViewModel.TcpB) or nameof(RobotPanelViewModel.TcpC);

    void UpdateRobotPoseSection()
    {
        var section = Sections.FirstOrDefault(s => s.ShowRobotPose);
        if (section is null) return;

        if (_robot?.IsConnected != true)
        {
            section.ClearRobotPose();
            return;
        }

        section.ApplyRobotPose(
            _robot.A1, _robot.A2, _robot.A3, _robot.A4, _robot.A5, _robot.A6, _robot.E1,
            _robot.TcpX, _robot.TcpY, _robot.TcpZ, _robot.TcpA, _robot.TcpB, _robot.TcpC);
    }

    void OnIoSnapshotUpdated(object? sender, IReadOnlyDictionary<string, string> snapshot)
    {
        foreach (var row in _signalIndex.Values.Where(r => r.Config.Source == LiveIoSource.Kuka))
            row.ApplyRaw(snapshot.GetValueOrDefault(row.Config.Key));
    }

    void UpdateSectionStatus()
    {
        bool kukaLive = _robot?.IsConnected == true;
        bool extLive  = _extruderBridgeLive;
        bool mbLive   = _extruderModbusLive;
        bool millLive = _millingBridgeLive;
        foreach (var section in Sections)
        {
            var phase = LiveIoPhasePlan.ForSection(section.Title);
            section.StatusLine = phase switch
            {
                null => "—",
                { Number: 1, Status: LiveIoPhaseStatus.Implemented } when kukaLive
                    => "P1 live · C3Bridge",
                { Number: 1, Status: LiveIoPhaseStatus.Implemented }
                    => "P1 · sync robot",
                { Number: 2 } when extLive && mbLive
                    => "P2 live · bridge + Modbus",
                { Number: 2 } when extLive
                    => "P2 live · bridge (Modbus offline)",
                { Number: 2 } when !string.IsNullOrEmpty(_extIp)
                    => "P2 · bridge offline",
                { Number: 3 } when millLive
                    => "P3 live · bridge",
                { Number: 3 } when _hasMilling && !string.IsNullOrEmpty(_millIp)
                    => "P3 · bridge offline",
                { Status: LiveIoPhaseStatus.Pending } p
                    => $"P{p.Number} pending",
                _ => "—",
            };

            foreach (var sig in section.Signals)
            {
                bool live = sig.Config.Source switch
                {
                    LiveIoSource.Kuka => kukaLive,
                    LiveIoSource.ExtruderIo28 or LiveIoSource.ExtruderBridge => extLive,
                    LiveIoSource.ExtruderModbus => extLive && mbLive,
                    LiveIoSource.MillingModbus => millLive,
                    _ => false,
                };
                if (!live)
                    sig.MarkUnavailable();
            }
        }
    }

    void ApplyPolling()
    {
        bool kukaPoll = IsExpanded && _robot?.IsConnected == true;
        bool extPoll  = IsExpanded && !string.IsNullOrEmpty(_extIp);
        bool millPoll = IsExpanded && _hasMilling && !string.IsNullOrEmpty(_millIp);

        _robot?.SetLiveIoPolling(kukaPoll);
        if (extPoll) StartExtruderPolling();
        else StopExtruderPolling();
        if (millPoll) StartMillingPolling();
        else StopMillingPolling();

        if (!kukaPoll)
        {
            foreach (var row in _signalIndex.Values.Where(r => r.Config.Source == LiveIoSource.Kuka))
                row.MarkUnavailable();
        }
        if (!extPoll)
        {
            _extruderBridgeLive = false;
            _extruderModbusLive = false;
            foreach (var row in _signalIndex.Values.Where(r => r.Config.Source is
                         LiveIoSource.ExtruderIo28 or LiveIoSource.ExtruderBridge or LiveIoSource.ExtruderModbus))
                row.MarkUnavailable();
            UpdateSectionStatus();
        }
        if (!millPoll)
        {
            _millingBridgeLive = false;
            foreach (var row in _signalIndex.Values.Where(r => r.Config.Source == LiveIoSource.MillingModbus))
                row.MarkUnavailable();
            UpdateSectionStatus();
        }
    }

    void StartExtruderPolling()
    {
        StopExtruderPolling();
        if (string.IsNullOrEmpty(_extIp)) return;
        _extruderBridge ??= new ExtruderBridgeClient();
        _extruderPollCts = new CancellationTokenSource();
        _ = RunExtruderPollLoopAsync(_extruderPollCts.Token);
    }

    void StopExtruderPolling()
    {
        if (_extruderPollCts is null) return;
        _extruderPollCts.Cancel();
        _extruderPollCts.Dispose();
        _extruderPollCts = null;
    }

    void StartMillingPolling()
    {
        StopMillingPolling();
        if (string.IsNullOrEmpty(_millIp)) return;
        _millingBridge ??= new MillingModbusClient();
        _millingPollCts = new CancellationTokenSource();
        _ = RunMillingPollLoopAsync(_millingPollCts.Token);
    }

    void StopMillingPolling()
    {
        if (_millingPollCts is null) return;
        _millingPollCts.Cancel();
        _millingPollCts.Dispose();
        _millingPollCts = null;
    }

    async Task RunExtruderPollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            bool ok = false;
            try
            {
                var snap = await _extruderBridge!.ReadAsync(_extIp!, _extBridgePort, ct);
                ok = snap.Ok;
                if (snap.Ok)
                {
                    await Dispatcher.UIThread.InvokeAsync(() => ApplyExtruderSnapshot(snap));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                ok = false;
            }

            if (_extruderBridgeLive != ok)
            {
                _extruderBridgeLive = ok;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    UpdateSectionStatus();
                    OnPropertyChanged(nameof(Phase1Hint));
                });
            }

            try
            {
                await Task.Delay(2000, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    async Task RunMillingPollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            bool ok = false;
            try
            {
                var snap = await _millingBridge!.ReadAsync(_millIp!, _millBridgePort, ct);
                ok = snap.Ok;
                if (snap.Ok)
                {
                    await Dispatcher.UIThread.InvokeAsync(() => ApplyMillingSnapshot(snap));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                ok = false;
            }

            if (_millingBridgeLive != ok)
            {
                _millingBridgeLive = ok;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    UpdateSectionStatus();
                    OnPropertyChanged(nameof(Phase1Hint));
                });
            }

            try
            {
                await Task.Delay(MillingModbusClient.DefaultPollIntervalMs, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    void ApplyExtruderSnapshot(ExtruderBridgeSnapshot snap)
    {
        foreach (var row in _signalIndex.Values.Where(r => r.Config.Source is
                     LiveIoSource.ExtruderIo28 or LiveIoSource.ExtruderBridge))
        {
            if (snap.Io.TryGetValue(row.Config.Key, out var raw))
                row.ApplyRaw(LiveIoValueFormatter.FormatBridgeRaw(raw));
        }

        bool mbLive = snap.ModbusConnected;
        if (_extruderModbusLive != mbLive)
        {
            _extruderModbusLive = mbLive;
            UpdateSectionStatus();
        }

        if (!mbLive) return;

        foreach (var row in _signalIndex.Values.Where(r => r.Config.Source == LiveIoSource.ExtruderModbus))
        {
            if (snap.Modbus.TryGetValue(row.Config.Key, out var raw))
                row.ApplyRaw(LiveIoValueFormatter.FormatBridgeRaw(raw));
        }
    }

    void ApplyMillingSnapshot(MillingBridgeSnapshot snap)
    {
        foreach (var row in _signalIndex.Values.Where(r => r.Config.Source == LiveIoSource.MillingModbus))
        {
            if (snap.Io.TryGetValue(row.Config.Key, out var raw))
                row.ApplyRaw(LiveIoValueFormatter.FormatBridgeRaw(raw));
        }
    }

    internal async Task ConfirmToggleAsync(LiveIoSignalViewModel signal)
    {
        if (signal.ConfirmMessage is null) return;
        bool next = !(signal.LastBool ?? false);
        bool ok = signal.Config.Source switch
        {
            LiveIoSource.Kuka when _robot is not null
                => await _robot.TryWriteKukaDigitalAsync(signal.Config.Key, next),
            LiveIoSource.ExtruderIo28 or LiveIoSource.ExtruderBridge when !string.IsNullOrEmpty(_extIp)
                => await (_extruderBridge ??= new ExtruderBridgeClient())
                    .TryWriteDigitalAsync(_extIp, signal.Config.Key, next, _extBridgePort),
            _ => false,
        };
        signal.ConfirmMessage = null;
        if (!ok)
        {
            signal.DisplayValue = "Write failed";
            return;
        }
        signal.ApplyRaw(next ? "TRUE" : "FALSE");
    }
}