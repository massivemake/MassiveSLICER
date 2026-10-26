using System.Globalization;
using Avalonia.Media;
using Avalonia.Threading;
using MassiveSlicer.Commands;
using MassiveSlicer.Controls;
using MassiveSlicer.Core.Kinematics;
using MassiveSlicer.ViewModels.Base;

namespace MassiveSlicer.ViewModels;

/// <summary>One show/hide chip on the joint graph. Session only.</summary>
public sealed class JointGraphChip : ViewModelBase
{
    public JointGraphChip(string name, int axis, IBrush swatch, RelayCommand toggle)
    {
        Name = name;
        Axis = axis;
        Swatch = swatch;
        ToggleCommand = toggle;
        _tip = name;
    }

    public IBrush Swatch { get; }

    public string Name { get; }
    public int Axis { get; }
    public RelayCommand ToggleCommand { get; }

    bool _isOn = true;
    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (SetField(ref _isOn, value))
                OnPropertyChanged(nameof(Opacity));
        }
    }

    public double Opacity => _isOn ? 1 : 0.35;

    bool _visible = true;
    public bool Visible
    {
        get => _visible;
        set => SetField(ref _visible, value);
    }

    string _tip;
    public string Tip
    {
        get => _tip;
        set => SetField(ref _tip, value);
    }

    string _readout = "";
    public string Readout
    {
        get => _readout;
        set => SetField(ref _readout, value);
    }
}

public sealed partial class ViewportViewModel
{
    static readonly string[] JointGraphNames = ["A1", "A2", "A3", "A4", "A5", "A6", "E1"];
    static readonly Color[] JointGraphColors =
    [
        Color.FromRgb(126, 182, 255),
        Color.FromRgb(232, 160, 96),
        Color.FromRgb(126, 216, 126),
        Color.FromRgb(224, 122, 208),
        Color.FromRgb(242, 213, 107),
        Color.FromRgb(143, 208, 208),
        Color.FromRgb(201, 160, 255),
    ];

    JointGraphChip[] _jointChips = [];
    float[][]? _jointSolutions;
    float[]? _jointE1;
    bool _jointHasE1;
    (float Min, float Max)[] _jointArmStops = [];
    float _jointE1Min = -360f;
    float _jointE1Max = 360f;
    string _jointE1Unit = "°";
    JointSpan[]?[]? _jointRaw;
    bool _jointRawReady;
    int _jointGraphGeneration;

    /// <summary>The view pushes the stored reachability solutions. Opening the graph does not solve.</summary>
    internal Action? JointGraphSync { get; set; }

    public IReadOnlyList<JointGraphChip> JointGraphChips => _jointChips;

    bool _showJointGraph;
    public bool ShowJointGraph
    {
        get => _showJointGraph;
        private set
        {
            if (!SetField(ref _showJointGraph, value)) return;
            NotifyJointGraphChrome();
        }
    }

    bool _hasJointGraphData;
    public bool HasJointGraphData
    {
        get => _hasJointGraphData;
        private set
        {
            if (!SetField(ref _hasJointGraphData, value)) return;
            NotifyJointGraphChrome();
        }
    }

    public bool ShowJointE1 => _jointHasE1;

    public bool ShowJointGraphEmpty => ShowJointGraph && !HasJointGraphSource;

    public bool ShowJointGraphBuilding => ShowJointGraph && HasJointGraphSource && !HasJointGraphData;

    bool HasJointGraphSource =>
        _jointSolutions is { Length: > 0 } sols && sols.Length == _toolpathScrubMax;

    IReadOnlyList<JointGraphTrace> _jointGraphTraces = [];
    public IReadOnlyList<JointGraphTrace> JointGraphTraces
    {
        get => _jointGraphTraces;
        private set => SetField(ref _jointGraphTraces, value);
    }

    double _jointGraphPlayheadX;
    public double JointGraphPlayheadX
    {
        get => _jointGraphPlayheadX;
        private set => SetField(ref _jointGraphPlayheadX, value);
    }

    public const double JointGraphHeightDefault = 200;
    public const double JointGraphHeightMin = 80;
    public const double JointGraphHeightMax = 720;

    double _jointGraphHeight = JointGraphHeightDefault;

    /// <summary>Chart height in pixels. Session only. Home does not change it.</summary>
    public double JointGraphHeight
    {
        get => _jointGraphHeight;
        set => SetField(ref _jointGraphHeight, Math.Clamp(value, JointGraphHeightMin, JointGraphHeightMax));
    }

    JointGraphFrame _jointGraphFrame = JointGraphFrame.Home;

    /// <summary>Visible window of the chart. Session only.</summary>
    public JointGraphFrame JointGraphFrame
    {
        get => _jointGraphFrame;
        private set
        {
            if (!SetField(ref _jointGraphFrame, value)) return;
            OnPropertyChanged(nameof(JointGraphViewIsHome));
        }
    }

    public bool JointGraphViewIsHome => _jointGraphFrame.IsHome;

    /// <summary>Wheel zoom. A positive delta zooms in around a point in chart fractions.</summary>
    internal void ZoomJointGraph(double anchorX, double anchorY, double wheelDelta)
    {
        if (wheelDelta == 0 || double.IsNaN(wheelDelta)) return;
        double factor = Math.Exp(-0.18 * wheelDelta);
        JointGraphFrame = _jointGraphFrame.Zoom(anchorX, anchorY, factor);
    }

    /// <summary>Drag the chart. Deltas are fractions of the full program and the full joint range.</summary>
    internal void PanJointGraph(double dataDx, double dataDy)
    {
        JointGraphFrame = _jointGraphFrame.Pan(dataDx, dataDy);
    }

    /// <summary>Full timeline and full joint range. The dragged height stays.</summary>
    internal void ResetJointGraphView()
    {
        JointGraphFrame = JointGraphFrame.Home;
    }

    public RelayCommand ToggleJointGraphCommand => _toggleJointGraphCommand ??= new RelayCommand(ToggleJointGraph);
    RelayCommand? _toggleJointGraphCommand;

    internal void InitJointGraph()
    {
        _jointChips = new JointGraphChip[JointGraphNames.Length];
        for (int i = 0; i < JointGraphNames.Length; i++)
        {
            int axis = i;
            _jointChips[i] = new JointGraphChip(
                JointGraphNames[i], axis, new SolidColorBrush(JointGraphColors[axis]),
                new RelayCommand(() => ToggleJointAxis(axis)));
        }
        _jointChips[6].Visible = false;
    }

    internal void ToggleJointGraph()
    {
        ShowJointGraph = !ShowJointGraph;
        JointGraphSync?.Invoke();
    }

    internal void ToggleJointAxis(int axis)
    {
        if ((uint)axis >= (uint)_jointChips.Length) return;
        _jointChips[axis].IsOn = !_jointChips[axis].IsOn;
        if (_jointRawReady)
            PublishJointRows();
    }

    internal void ToggleJointAxis(string name)
    {
        for (int i = 0; i < JointGraphNames.Length; i++)
        {
            if (JointGraphNames[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                ToggleJointAxis(i);
                return;
            }
        }
    }

    internal string JointGraphStatus()
    {
        string state = ShowJointGraph ? "open" : "closed";
        string data = HasJointGraphSource
            ? (_jointRawReady ? $"{_jointSolutions!.Length:N0} moves" : "reading stored solutions")
            : "no reachability solutions yet";
        var parts = new List<string>();
        foreach (var chip in _jointChips)
        {
            if (!chip.Visible) continue;
            parts.Add(chip.IsOn ? chip.Name : chip.Name + " off");
        }
        string height = string.Create(CultureInfo.InvariantCulture, $"height {_jointGraphHeight:0.#}");
        string view = _jointGraphFrame.IsHome
            ? "view home"
            : string.Create(CultureInfo.InvariantCulture,
                $"view x {_jointGraphFrame.Left * 100:0.#}–{(_jointGraphFrame.Left + _jointGraphFrame.Width) * 100:0.#}%  y {_jointGraphFrame.Bottom * 100:0.#}–{(_jointGraphFrame.Bottom + _jointGraphFrame.Height) * 100:0.#}%");
        return $"[jgraph] {state}  {data}  {string.Join(" ", parts)}  {height}  {view}";
    }

    /// <summary>
    /// Stores the solutions the reachability check already produced.
    /// Does not run IK. A mismatched length is treated as no data.
    /// </summary>
    internal void SetJointGraphSource(
        float[][]? solutions,
        float[]? e1,
        bool hasE1,
        (float Min, float Max)[] armStops,
        float e1Min,
        float e1Max,
        string e1Unit)
    {
        bool same = ReferenceEquals(solutions, _jointSolutions)
            && ReferenceEquals(e1, _jointE1)
            && hasE1 == _jointHasE1
            && e1Min == _jointE1Min
            && e1Max == _jointE1Max
            && e1Unit == _jointE1Unit
            && StopsEqual(armStops);
        _jointSolutions = solutions;
        _jointE1 = e1;
        _jointHasE1 = hasE1;
        _jointArmStops = armStops;
        _jointE1Min = e1Min;
        _jointE1Max = e1Max;
        _jointE1Unit = string.IsNullOrEmpty(e1Unit) ? "°" : e1Unit;
        if (_jointChips.Length > 6)
            _jointChips[6].Visible = hasE1;
        OnPropertyChanged(nameof(ShowJointE1));
        UpdateJointTips();

        bool good = HasJointGraphSource;
        if (!same)
        {
            _jointRawReady = false;
            HasJointGraphData = false;
            JointGraphTraces = [];
        }
        if (!good)
        {
            _jointRawReady = false;
            HasJointGraphData = false;
            JointGraphTraces = [];
            NotifyJointGraphChrome();
            return;
        }
        if (!ShowJointGraph)
        {
            NotifyJointGraphChrome();
            return;
        }
        if (same && _jointRawReady)
        {
            RefreshJointGraphPlayhead();
            return;
        }
        StartJointGraphBuild();
    }

    void StartJointGraphBuild()
    {
        var sols = _jointSolutions;
        var e1 = _jointE1;
        bool hasE1 = _jointHasE1;
        int gen = ++_jointGraphGeneration;
        if (sols is null) return;
        NotifyJointGraphChrome();
        Task.Run(() =>
        {
            var raw = new JointSpan[7][];
            for (int a = 0; a < 6; a++)
                raw[a] = JointTimeline.EnvelopeAxis(sols, a, JointTimeline.Buckets);
            if (hasE1 && e1 is { Length: > 0 } && e1.Length == sols.Length)
                raw[6] = JointTimeline.Envelope(e1, JointTimeline.Buckets);
            else if (hasE1)
                raw[6] = new JointSpan[JointTimeline.Buckets];
            return raw;
        }).ContinueWith(t =>
        {
            if (t.IsFaulted || t.IsCanceled) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (gen != _jointGraphGeneration) return;
                _jointRaw = t.Result;
                _jointRawReady = true;
                PublishJointRows();
            });
        });
    }

    void PublishJointRows()
    {
        var raw = _jointRaw;
        if (raw is null)
        {
            HasJointGraphData = false;
            JointGraphTraces = [];
            return;
        }
        var traces = new List<JointGraphTrace>();
        for (int axis = 0; axis < _jointChips.Length; axis++)
        {
            var chip = _jointChips[axis];
            if (!chip.Visible || !chip.IsOn) continue;
            var spans = axis < raw.Length ? raw[axis] : null;
            if (spans is null) continue;
            var (min, max, unit) = AxisScale(axis);
            var columns = new JointGraphColumn[spans.Length];
            for (int i = 0; i < spans.Length; i++)
            {
                var s = spans[i];
                columns[i] = s.HasValue
                    ? new JointGraphColumn(
                        JointTimeline.Fraction(s.Low, min, max),
                        JointTimeline.Fraction(s.High, min, max),
                        true)
                    : default;
            }
            traces.Add(new JointGraphTrace(chip.Swatch, columns, min, max, unit));
        }
        JointGraphTraces = traces;
        HasJointGraphData = true;
        RefreshJointGraphPlayhead();
    }

    internal void RefreshJointGraphPlayhead()
    {
        if (!ShowJointGraph) return;
        JointGraphPlayheadX = JointPlayheadX();
        if (!_jointRawReady) return;
        int index = _toolpathScrubIndex;
        var sols = _jointSolutions;
        var e1 = _jointE1;
        foreach (var chip in _jointChips)
        {
            if (!chip.Visible || !chip.IsOn)
            {
                if (chip.Readout.Length > 0) chip.Readout = "";
                continue;
            }
            chip.Readout = ReadoutAt(chip.Axis, index, sols, e1);
        }
    }

    /// <summary>Seek from a program fraction. 0 is the first move, 1 is the last.</summary>
    internal void SeekJointGraph(double dataX)
    {
        if (_toolpathScrubMax <= 0) return;
        double fraction = Math.Clamp(dataX, 0, 1);
        ToolpathScrubIndex = (int)Math.Round(fraction * _toolpathScrubMax);
    }

    /// <summary>Playhead as a fraction of the program, independent of the scrub-track width.</summary>
    double JointPlayheadX()
    {
        int max = _toolpathScrubMax;
        if (max <= 0) return 0;
        return Math.Clamp((double)_toolpathScrubIndex / max, 0, 1);
    }

    string ReadoutAt(int axis, int index, float[][]? sols, float[]? e1)
    {
        var (_, _, unit) = AxisScale(axis);
        if (axis < 6)
        {
            if (sols is null || (uint)index >= (uint)sols.Length) return "—";
            var row = sols[index];
            if (row is null || (uint)axis >= (uint)row.Length) return "—";
            return FormatJoint(row[axis], unit);
        }
        if (e1 is null || (uint)index >= (uint)e1.Length) return "—";
        return FormatJoint(e1[index], unit);
    }

    (float Min, float Max, string Unit) AxisScale(int axis)
    {
        if (axis >= 6)
            return (_jointE1Min, _jointE1Max, _jointE1Unit);
        if ((uint)axis < (uint)_jointArmStops.Length)
            return (_jointArmStops[axis].Min, _jointArmStops[axis].Max, "°");
        return (-360f, 360f, "°");
    }

    void UpdateJointTips()
    {
        if (_jointChips.Length == 0) return;
        for (int i = 0; i < _jointChips.Length; i++)
        {
            var (min, max, unit) = AxisScale(i);
            _jointChips[i].Tip =
                $"{JointGraphNames[i]}  {min.ToString("0.#", CultureInfo.InvariantCulture)}{unit} to {max.ToString("0.#", CultureInfo.InvariantCulture)}{unit}. Click to show or hide this line.";
        }
    }

    bool StopsEqual((float Min, float Max)[] stops)
    {
        if (stops.Length != _jointArmStops.Length) return false;
        for (int i = 0; i < stops.Length; i++)
        {
            if (stops[i].Min != _jointArmStops[i].Min || stops[i].Max != _jointArmStops[i].Max)
                return false;
        }
        return true;
    }

    void NotifyJointGraphChrome()
    {
        OnPropertyChanged(nameof(ShowJointGraphEmpty));
        OnPropertyChanged(nameof(ShowJointGraphBuilding));
    }

    static string FormatJoint(float value, string unit)
    {
        if (float.IsNaN(value)) return "—";
        return string.Create(CultureInfo.InvariantCulture, $"{value:0.0}{unit}");
    }
}
