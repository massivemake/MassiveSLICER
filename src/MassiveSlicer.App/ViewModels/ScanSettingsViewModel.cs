using System.Collections.ObjectModel;
using MassiveSlicer.Commands;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Scanning;
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
    private bool _useFakeScanData;
    private bool _isCaptureSubTab = true;
    private string _newQuickPositionName = "Quick Position 1";
    private string _quickPositionStatus = "";

    public const string ScannerDownWaypointName = "scanner-down-bed";
    public const string ScannerDownLabel      = "Scanner Down";
    public const string QuickPositionTag      = "scan-quick";

    public ScanSettingsViewModel()
    {
        TestScanCommand        = new RelayCommand(() => OnTestScanRequested?.Invoke(), () => !IsScanning);
        AlignModelCommand      = new RelayCommand(() => OnAlignModelRequested?.Invoke(), () => !IsAligning);
        ShowCaptureCommand     = new RelayCommand(() => IsCaptureSubTab = true);
        ShowCalibrationCommand = new RelayCommand(() => IsCaptureSubTab = false);
        SaveQuickPositionCommand = new RelayCommand(
            () => OnSaveQuickPositionRequested?.Invoke(NewQuickPositionName),
            () => !string.IsNullOrWhiteSpace(NewQuickPositionName));
        GoToScanPoseCommand = new RelayCommand(GoToScanPose);
        SaveScanPoseCommand = new RelayCommand(SaveScanPose);
        MagicScanCommand = new RelayCommand(StartMagicScan);
        MoveToOverheadCommand = new RelayCommand(MoveToOverhead);
        DetectPartCommand = new RelayCommand(DetectPart);
        PlanFineScanCommand = new RelayCommand(PlanFineScan);
        NewMagicToolpathCommand = new RelayCommand(PlanFineScan);
        RescanMagicCommand = new RelayCommand(RescanMagic);
        ContinueMagicCommand = new RelayCommand(ContinueMagic);
        MergeScansCommand = new RelayCommand(() =>
        {
            bool merged = TryMergeScans?.Invoke() == true;
            MagicScanStatus = merged
                ? "Merged the selected scans as a mesh."
                : "Select at least two scans to merge. Merged Scan rows are left out.";
        });
        ReloadScanPoses(null);
    }

    /// <summary>
    /// When false, Test Scan uses the Zivid camera. When true, Test Scan builds a
    /// view of the model from the simulated camera and does not open the camera.
    /// Hand-eye and bed calibration ignore this flag. Starts off.
    /// </summary>
    public bool UseFakeScanData
    {
        get => _useFakeScanData;
        set
        {
            if (!SetField(ref _useFakeScanData, value)) return;
            OnPropertyChanged(nameof(FakeScanCaption));
        }
    }

    /// <summary>Line under the fake-scan toggle. States whether the camera will be used.</summary>
    public string FakeScanCaption => UseFakeScanData
        ? "ON — Test Scan lifts the simulated scanner, grids the whole model, then moves closer for the fine points. The camera is not used."
        : "Off — Test Scan uses the Zivid camera.";

    public string DescribeFakeScan() => UseFakeScanData
        ? "[scan-fake] on  Test Scan lifts the simulated scanner, grids the whole model, then moves closer. The camera is not used. Hand-eye and bed calibration still use the camera."
        : "[scan-fake] off  Test Scan uses the Zivid camera.";

    /// <summary>Selected Scan Pose joints, or false when none are stored.</summary>
    internal bool TryCopySelectedScanPose(out string name, out float[] angles)
    {
        int idx = _scanPoses.FindIndex(p => p.Name.Equals(_selectedScanPoseName, StringComparison.OrdinalIgnoreCase));
        if (idx < 0 || _scanPoses[idx].Angles.Length < 6)
        {
            name = "";
            angles = [];
            return false;
        }

        name = _scanPoses[idx].Name;
        angles = (float[])_scanPoses[idx].Angles.Clone();
        return true;
    }

    /// <summary>Triggers a single capture from the Zivid camera, or a fake model view when the toggle is on.</summary>
    public RelayCommand TestScanCommand        { get; }

    /// <summary>Moves the model onto the selected scan. The scan is not moved.</summary>
    public RelayCommand AlignModelCommand      { get; }

    internal Action? OnAlignModelRequested { get; set; }
    internal Action? OnAlignOffsetChanged { get; set; }

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

    // -- Scan poses (simulated robot only) ------------------------------------

    private List<(string Name, float[] Angles)> _scanPoses = [];
    private string[] _availableScanPoseNames = [];
    private string _selectedScanPoseName = "";
    private string _newScanPoseName = "Scan Pose 1";
    private string _scanPoseStatus = "";

    /// <summary>Names in the Scan Pose dropdown. Overview is always present.</summary>
    public string[] AvailableScanPoseNames
    {
        get => _availableScanPoseNames;
        private set => SetField(ref _availableScanPoseNames, value);
    }

    /// <summary>
    /// Selected scan pose. Changing it does not move the simulated robot — that is
    /// <see cref="GoToScanPoseCommand"/>.
    /// </summary>
    public string SelectedScanPoseName
    {
        get => _selectedScanPoseName;
        set
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (_scanPoses.FindIndex(p => p.Name.Equals(value, StringComparison.OrdinalIgnoreCase)) < 0)
                return;
            SetField(ref _selectedScanPoseName, value);
        }
    }

    /// <summary>Name written by SAVE AS SCAN POSE. An existing name is overwritten.</summary>
    public string NewScanPoseName
    {
        get => _newScanPoseName;
        set => SetField(ref _newScanPoseName, value);
    }

    /// <summary>Line under Go to Pose / Save. States that the live arm was not moved.</summary>
    public string ScanPoseStatus
    {
        get => _scanPoseStatus;
        private set => SetField(ref _scanPoseStatus, value);
    }

    /// <summary>Puts the viewport robot on the selected pose. Never commands the live arm.</summary>
    public RelayCommand GoToScanPoseCommand { get; }

    /// <summary>Saves the current simulated A1–E1 under <see cref="NewScanPoseName"/>.</summary>
    public RelayCommand SaveScanPoseCommand { get; }

    /// <summary>
    /// Viewport-only apply. Wired to <c>RobotPanelViewModel.ApplyViewportJoints</c>.
    /// Must not be pointed at a MassiveDRIVE or waypoint move.
    /// </summary>
    internal Action<float[]>? OnApplyScanPose { get; set; }

    /// <summary>Current simulated A1–E1. Null when the sliders are unavailable.</summary>
    internal Func<float[]?>? CaptureSimJoints { get; set; }

    /// <summary>Active cell JSON path. The sidecar is written beside it.</summary>
    internal Func<string?>? ActiveCellPath { get; set; }

    /// <summary>Loads the sidecar for <paramref name="cellPath"/>, or Overview alone when there is no cell.</summary>
    public void ReloadScanPoses(string? cellPath)
    {
        var file = string.IsNullOrWhiteSpace(cellPath)
            ? ScanPoses.EnsureOverview(null)
            : CellLoader.LoadScanPoses(cellPath);
        ApplyFile(file);
    }

    /// <summary>One-line catalog for <c>scan-pose status</c>. Does not move anything.</summary>
    public string DescribeScanPoses()
    {
        var names = _scanPoses.Count == 0
            ? ScanPoses.OverviewName
            : string.Join(", ", _scanPoses.Select(p => p.Name));
        var cell = ActiveCellPath?.Invoke();
        var side = string.IsNullOrWhiteSpace(cell)
            ? "(no cell)"
            : CellLoader.ScanPosesSidecarPath(cell);
        return $"[scan-pose] {names}  selected={_selectedScanPoseName}  sidecar={side}  viewport only";
    }

    /// <summary>Selects <paramref name="name"/> when given, then applies that pose to the viewport robot.</summary>
    public void GoToNamedScanPose(string? name)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            var want = name.Trim();
            if (_scanPoses.All(p => !p.Name.Equals(want, StringComparison.OrdinalIgnoreCase)))
            {
                ScanPoseStatus = $"No scan pose named {want}.";
                return;
            }
            SelectedScanPoseName = want;
        }
        GoToScanPose();
    }

    /// <summary>Saves under <paramref name="name"/> when given, otherwise under the name box.</summary>
    public void SaveNamedScanPose(string? name)
    {
        if (!string.IsNullOrWhiteSpace(name))
            NewScanPoseName = name.Trim();
        SaveScanPose();
    }

    void GoToScanPose()
    {
        int idx = _scanPoses.FindIndex(p => p.Name.Equals(_selectedScanPoseName, StringComparison.OrdinalIgnoreCase));
        if (idx < 0 || _scanPoses[idx].Angles.Length < 6)
        {
            ScanPoseStatus = "Pick a scan pose first.";
            return;
        }

        var angles = (float[])_scanPoses[idx].Angles.Clone();
        OnApplyScanPose?.Invoke(angles);
        ScanPoseStatus = $"Viewport at {_scanPoses[idx].Name}. The live arm was not moved.";
    }

    void SaveScanPose()
    {
        var typed = string.IsNullOrWhiteSpace(NewScanPoseName) ? "Scan Pose 1" : NewScanPoseName.Trim();
        var captured = ScanPoses.CopyAngles(CaptureSimJoints?.Invoke());
        if (captured.Length < 6)
        {
            ScanPoseStatus = "No simulated joints to save.";
            return;
        }

        var cell = ActiveCellPath?.Invoke();
        if (string.IsNullOrWhiteSpace(cell))
        {
            ScanPoseStatus = "No cell loaded — pose was not saved.";
            return;
        }

        int idx = _scanPoses.FindIndex(p => p.Name.Equals(typed, StringComparison.OrdinalIgnoreCase));
        var display = idx >= 0 ? _scanPoses[idx].Name : typed;
        if (idx >= 0)
            _scanPoses[idx] = (display, captured);
        else
            _scanPoses.Add((display, captured));

        try
        {
            CellLoader.SaveScanPoses(cell, ToFile(display));
        }
        catch (Exception ex)
        {
            System.Console.Error.WriteLine($"[scan-pose] save failed: {ex.Message}");
            ReloadScanPoses(cell);
            ScanPoseStatus = "Could not save the scan pose.";
            return;
        }

        ApplyFile(CellLoader.LoadScanPoses(cell));
        SelectedScanPoseName = display;
        ScanPoseStatus = $"Saved {display} from the simulated joints.";
    }

    ScanPoseFile ToFile(string selected)
    {
        var file = new ScanPoseFile { Selected = selected };
        foreach (var pose in _scanPoses)
            file.Poses.Add(new ScanPoseConfig { Name = pose.Name, Angles = pose.Angles });
        return file;
    }

    void ApplyFile(ScanPoseFile file)
    {
        var keep = _selectedScanPoseName;
        _scanPoses = file.Poses
            .Select(p => (p.Name, (float[])p.Angles.Clone()))
            .ToList();
        AvailableScanPoseNames = _scanPoses.Select(p => p.Name).ToArray();

        var want = _scanPoses.FirstOrDefault(p => p.Name.Equals(keep, StringComparison.OrdinalIgnoreCase)).Name;
        if (string.IsNullOrEmpty(want))
            want = file.Selected ?? ScanPoses.OverviewName;
        if (_scanPoses.All(p => !p.Name.Equals(want, StringComparison.OrdinalIgnoreCase)))
            want = _scanPoses[0].Name;

        if (!SetField(ref _selectedScanPoseName, want, nameof(SelectedScanPoseName)))
            OnPropertyChanged(nameof(SelectedScanPoseName));
        SuggestNextScanPoseName();
    }

    void SuggestNextScanPoseName()
    {
        var names = _scanPoses.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int n = 1;
        while (names.Contains($"Scan Pose {n}")) n++;
        NewScanPoseName = $"Scan Pose {n}";
    }

    // -- Magic Scan -----------------------------------------------------------

    private string _magicScanMode = "Localization";
    private string _magicScanStatus = "";
    private bool _showMagicMove;
    private bool _showMagicReview;
    private double _bedClearanceMm = 20;
    private double _coarseFieldMm = 500;
    private double _standoffMm = 600;
    private double _footprintMm = 400;
    private double _overlapPercent = 30;
    private string _detectedPartText = "";
    private MagicScanBox? _detectedBox;

    public string[] MagicScanModes { get; } = ["Localization", "Part recreation"];

    /// <summary>Localization seats a loaded model. Part recreation stops at the merged mesh.</summary>
    public string MagicScanMode
    {
        get => _magicScanMode;
        set
        {
            if (value != "Localization" && value != "Part recreation") return;
            SetField(ref _magicScanMode, value);
        }
    }

    public bool IsLocalization => _magicScanMode == "Localization";

    public string MagicScanStatus
    {
        get => _magicScanStatus;
        private set => SetField(ref _magicScanStatus, value);
    }

    /// <summary>True after Magic Scan, before the live arm is allowed to leave.</summary>
    public bool ShowMagicMove
    {
        get => _showMagicMove;
        private set => SetField(ref _showMagicMove, value);
    }

    public bool ShowMagicReview
    {
        get => _showMagicReview;
        private set => SetField(ref _showMagicReview, value);
    }

    public double BedClearanceMm
    {
        get => _bedClearanceMm;
        set => SetField(ref _bedClearanceMm, Math.Clamp(value, 0, 200));
    }

    public double CoarseFieldMm
    {
        get => _coarseFieldMm;
        set => SetField(ref _coarseFieldMm, Math.Clamp(value, 50, 2000));
    }

    public double StandoffMm
    {
        get => _standoffMm;
        set => SetField(ref _standoffMm, Math.Clamp(value, 50, 2000));
    }

    public double FootprintMm
    {
        get => _footprintMm;
        set => SetField(ref _footprintMm, Math.Clamp(value, 50, 2000));
    }

    public double OverlapPercent
    {
        get => _overlapPercent;
        set => SetField(ref _overlapPercent, Math.Clamp(value, 0, 80));
    }

    public string DetectedPartText
    {
        get => _detectedPartText;
        private set => SetField(ref _detectedPartText, value);
    }

    public ObservableCollection<string> MagicScanStations { get; } = [];

    public RelayCommand MagicScanCommand { get; }
    public RelayCommand MoveToOverheadCommand { get; }
    public RelayCommand DetectPartCommand { get; }
    public RelayCommand PlanFineScanCommand { get; }
    public RelayCommand NewMagicToolpathCommand { get; }
    public RelayCommand RescanMagicCommand { get; }
    public RelayCommand ContinueMagicCommand { get; }
    public RelayCommand MergeScansCommand { get; }

    /// <summary>Interleaved world x,y,z from the selected scan, or the bed scans.</summary>
    internal Func<float[]?>? SampleScanPoints { get; set; }

    /// <summary>Bed top Z in millimetres.</summary>
    internal Func<float>? BedZ { get; set; }

    /// <summary>Existing scan merge. Returns false when fewer than two scans can be merged.</summary>
    internal Func<bool>? TryMergeScans { get; set; }

    /// <summary>Existing Match model to scan. Localization only.</summary>
    internal Action? AlignLocalizedModel { get; set; }

    /// <summary>Plays the simulated overhead move and collects fake frames. Viewport only.</summary>
    internal Action? OnFakeScanRequested { get; set; }

    public string DescribeMagicScan()
        => $"[magic-scan] mode={_magicScanMode}  stations={MagicScanStations.Count}  move={(ShowMagicMove ? "waiting" : "idle")}  viewport status only";

    void StartMagicScan()
    {
        ShowMagicMove = true;
        var pose = string.IsNullOrWhiteSpace(_selectedScanPoseName) ? ScanPoses.OverviewName : _selectedScanPoseName;
        MagicScanStatus = $"Ready to move the simulated arm to {pose}. Press Move to overhead. The live arm stays where it is, including when the robot is not synced.";
    }

    /// <summary>
    /// Puts the viewport robot on the selected Scan Pose. Same path as Go to Pose.
    /// Does not call SendAxes, GoToWaypoint, or any other live-arm command.
    /// </summary>
    void MoveToOverhead()
    {
        int idx = _scanPoses.FindIndex(p => p.Name.Equals(_selectedScanPoseName, StringComparison.OrdinalIgnoreCase));
        if (idx < 0 || _scanPoses[idx].Angles.Length < 6)
        {
            MagicScanStatus = "Pick an overhead Scan Pose first.";
            return;
        }

        var angles = (float[])_scanPoses[idx].Angles.Clone();
        var name = _scanPoses[idx].Name;
        if (UseFakeScanData)
        {
            ShowMagicMove = false;
            MagicScanStatus = $"Playing the simulated move to {name}, then collecting fake frames. The live arm is not moving.";
            OnFakeScanRequested?.Invoke();
            return;
        }

        OnApplyScanPose?.Invoke(angles);
        ShowMagicMove = false;
        var points = SampleScanPoints?.Invoke();
        if (points is { Length: >= 3 })
            DetectPart();
        else
            MagicScanStatus = "";

        var detail = MagicScanStatus;
        MagicScanStatus = string.IsNullOrEmpty(detail)
            ? $"Viewport at {name}. The live arm was not moved. Use Test Scan to capture, then Detect part."
            : $"Viewport at {name}. The live arm was not moved. {detail}";
    }

    void DetectPart()
    {
        var points = SampleScanPoints?.Invoke();
        float bed = BedZ?.Invoke() ?? 0f;
        var box = MagicScanPlanner.Detect(points, bed, (float)_bedClearanceMm);
        if (box is null)
        {
            _detectedBox = null;
            DetectedPartText = "No part above the bed in the current scan.";
            MagicScanStatus = "No part above the bed. Capture an overhead scan, or raise Bed clearance if the table was included.";
            ShowMagicReview = false;
            return;
        }

        RememberBox(box.Value);
    }

    void RememberBox(MagicScanBox box, bool askForSecondPose = true)
    {
        _detectedBox = box;
        DetectedPartText = $"Part {box.SizeX:0} × {box.SizeY:0} mm, top {box.Top:0} mm, {box.PointCount} points.";
        bool second = askForSecondPose && MagicScanPlanner.NeedsSecondOverhead(box, (float)_coarseFieldMm);
        ShowMagicMove = second;
        PlanFineScan();
        if (second)
            MagicScanStatus = $"Part is larger than the {CoarseFieldMm:0} mm coarse field. Select a second overhead Scan Pose and press Move to overhead. {MagicScanStations.Count} fine stations are planned from this cloud and are not sent to the arm.";
    }

    /// <summary>Progress line on the Magic Scan card during a simulated scan.</summary>
    internal void SetMagicStatus(string text) => MagicScanStatus = text;

    /// <summary>
    /// Boxes the rough cloud and plans the closer pass. Does not ask for a
    /// second taught pose — the rough grid already covered the part.
    /// </summary>
    internal void ApplyRoughBox(MagicScanBox box, float fineStandoffMm)
    {
        _fineStandoffOverride = fineStandoffMm;
        RememberBox(box, askForSecondPose: false);
        _fineStandoffOverride = null;
    }

    float? _fineStandoffOverride;
    float _plannedFineStandoff;

    void PlanFineScan()
    {
        if (_detectedBox is not { } box)
        {
            MagicScanStatus = "Detect a part before planning the fine path.";
            return;
        }

        float standoff = _fineStandoffOverride ?? (float)_standoffMm;
        _plannedFineStandoff = standoff;
        var stations = MagicScanPlanner.PlanFine(
            box, standoff, (float)_footprintMm, (float)_overlapPercent / 100f);
        MagicScanStations.Clear();
        int n = 1;
        foreach (var s in stations)
            MagicScanStations.Add($"{n++}   X {s.X:0}   Y {s.Y:0}   Z {s.Z:0}");
        ShowMagicReview = MagicScanStations.Count > 0;
        MagicScanStatus = $"{MagicScanStations.Count} fine stations, {_plannedFineStandoff:0} mm above the part. Review the path before continuing. It is not sent to the arm.";
    }

    void RescanMagic()
    {
        MagicScanStations.Clear();
        ShowMagicReview = false;
        _detectedBox = null;
        DetectedPartText = "";
        StartMagicScan();
    }

    void ContinueMagic()
    {
        bool merged = TryMergeScans?.Invoke() == true;
        if (IsLocalization)
            AlignLocalizedModel?.Invoke();

        float planned = _plannedFineStandoff > 0f ? _plannedFineStandoff : (float)_standoffMm;
        string path = MagicScanStations.Count == 0
            ? "No fine path is planned."
            : $"Fine path is {MagicScanStations.Count} stations at {planned:0} mm and was not sent to the arm.";
        if (IsLocalization)
        {
            MagicScanStatus = merged
                ? $"Merged the scans and started the model match. {path}"
                : $"Started the model match. Merge needs two scans. {path}";
        }
        else
        {
            MagicScanStatus = merged
                ? $"Part recreation merged the scans and stops at the mesh. {path}"
                : $"Part recreation did not merge. Select two scans. {path}";
        }
    }

    internal void SetDetectedBoxForTest(MagicScanBox box) => RememberBox(box);

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

    // -- Model-to-scan alignment. The scan stays. These numbers are for this session. --

    private double _stockMm = 3;
    private double _toleranceMm = 2;
    private double _matchPercent = 35;
    private double _maxShiftMm = 100;
    private double _maxTurnDeg = 20;
    private double _offsetX, _offsetY, _offsetZ, _rotateX, _rotateY, _rotateZ;
    private bool _isAligning;
    private bool _suppressOffset;
    private bool _alignStatusIsError;
    private string _alignStatus = "";

    /// <summary>How much thicker the print is than the model, in mm.</summary>
    public double StockMm
    {
        get => _stockMm;
        set => SetField(ref _stockMm, Math.Clamp(value, 0, 40));
    }

    /// <summary>Largest leftover stock error that still counts as a match, in mm.</summary>
    public double ToleranceMm
    {
        get => _toleranceMm;
        set => SetField(ref _toleranceMm, Math.Clamp(value, 0.1, 20));
    }

    /// <summary>Minimum percent of the model that must land on the scan.</summary>
    public double MatchPercent
    {
        get => _matchPercent;
        set => SetField(ref _matchPercent, Math.Clamp(value, 5, 95));
    }

    /// <summary>Largest shift that is still applied. A bigger fit is rejected.</summary>
    public double MaxShiftMm
    {
        get => _maxShiftMm;
        set => SetField(ref _maxShiftMm, Math.Clamp(value, 1, 500));
    }

    /// <summary>Largest turn that is still applied, in degrees. A bigger fit is rejected.</summary>
    public double MaxTurnDeg
    {
        get => _maxTurnDeg;
        set => SetField(ref _maxTurnDeg, Math.Clamp(value, 1, 90));
    }

    public double OffsetX { get => _offsetX; set => SetOffset(ref _offsetX, value, 500); }
    public double OffsetY { get => _offsetY; set => SetOffset(ref _offsetY, value, 500); }
    public double OffsetZ { get => _offsetZ; set => SetOffset(ref _offsetZ, value, 500); }
    public double RotateX { get => _rotateX; set => SetOffset(ref _rotateX, value, 180); }
    public double RotateY { get => _rotateY; set => SetOffset(ref _rotateY, value, 180); }
    public double RotateZ { get => _rotateZ; set => SetOffset(ref _rotateZ, value, 180); }

    public bool IsAligning
    {
        get => _isAligning;
        set
        {
            if (!SetField(ref _isAligning, value)) return;
            AlignModelCommand.RaiseCanExecuteChanged();
        }
    }

    public string AlignStatus => _alignStatus;
    public bool AlignStatusIsError => _alignStatusIsError;
    public bool AlignStatusIsOk => !_alignStatusIsError && _alignStatus.Length > 0;

    internal void SetAlignStatus(string message, bool isError)
    {
        _alignStatus = message;
        _alignStatusIsError = isError;
        OnPropertyChanged(nameof(AlignStatus));
        OnPropertyChanged(nameof(AlignStatusIsError));
        OnPropertyChanged(nameof(AlignStatusIsOk));
    }

    internal void ResetAlignOffsets()
    {
        _suppressOffset = true;
        OffsetX = 0;
        OffsetY = 0;
        OffsetZ = 0;
        RotateX = 0;
        RotateY = 0;
        RotateZ = 0;
        _suppressOffset = false;
    }

    private void SetOffset(ref double field, double value, double limit)
    {
        if (!SetField(ref field, Math.Clamp(value, -limit, limit))) return;
        if (_suppressOffset) return;
        OnAlignOffsetChanged?.Invoke();
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
            .Split((char[])[' ', '_', '-'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? "quick-position" : string.Join("-", parts);
    }

    private static string HumanizeWaypointName(string name)
        => string.Join(' ', name.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static p => char.ToUpperInvariant(p[0]) + p[1..]));
}
