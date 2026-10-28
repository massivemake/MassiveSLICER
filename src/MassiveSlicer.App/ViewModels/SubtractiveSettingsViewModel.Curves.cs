using System.Collections.ObjectModel;
using System.Numerics;
using MassiveSlicer.Commands;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;

namespace MassiveSlicer.ViewModels;

public sealed class MillCurveRow
{
    public required MillCurve Curve { get; init; }
    public string Label { get; set; } = "";
}

public sealed partial class SubtractiveSettingsViewModel
{
    readonly List<MillCurve> _curves = [];
    int _selectedCurve = -1;
    double _curveSideOffsetMm;
    string _curveSideDisplay = "Outside";
    bool _curveIncludeToolRadius;
    string _curveStatusText = "Import an OBJ, DXF, or SVG, or add edges found on the part.";
    bool _curveStatusIsError;

    public ObservableCollection<MillCurveRow> CurveRows { get; } = [];

    public static IReadOnlyList<string> CurveSideOptions { get; } = ["Outside", "Inside", "On the line"];

    public RelayCommand ImportCurvesCommand { get; private set; } = null!;
    public RelayCommand RemoveCurveCommand { get; private set; } = null!;
    public RelayCommand FlipCurveCommand { get; private set; } = null!;
    public RelayCommand<MillCurveRow> SelectCurveCommand { get; private set; } = null!;

    public event EventHandler? ImportCurvesRequested;

    public bool HasCurves => _curves.Count > 0;
    public bool HasSelectedCurve => (uint)_selectedCurve < (uint)_curves.Count;
    public bool HasIncludedCurves => _curves.Exists(c => c.Included && c.Points.Count >= 2);

    public string SelectedCurveLabel => HasSelectedCurve ? CurveRows[_selectedCurve].Label : "";

    public string CurveStatusText
    {
        get => _curveStatusText;
        private set => SetField(ref _curveStatusText, value);
    }

    public bool CurveStatusIsError
    {
        get => _curveStatusIsError;
        private set => SetField(ref _curveStatusIsError, value);
    }

    public double CurveSideOffsetMm
    {
        get => _curveSideOffsetMm;
        set => SetField(ref _curveSideOffsetMm, Math.Max(0, value));
    }

    public string CurveSideDisplay
    {
        get => _curveSideDisplay;
        set => SetField(ref _curveSideDisplay, string.IsNullOrWhiteSpace(value) ? "Outside" : value);
    }

    public bool CurveIncludeToolRadius
    {
        get => _curveIncludeToolRadius;
        set => SetField(ref _curveIncludeToolRadius, value);
    }

    public CurveOffsetSide CurveSide => _curveSideDisplay switch
    {
        "Inside" => CurveOffsetSide.Inside,
        "On the line" or nameof(CurveOffsetSide.OnLine) => CurveOffsetSide.OnLine,
        _ => CurveOffsetSide.Outside,
    };

    /// <summary>Raised when the curve list changes so a live mill path can rebuild.</summary>
    public int CurveRevision => _curves.Count;

    void InitCurveCommands()
    {
        ImportCurvesCommand = new RelayCommand(() => ImportCurvesRequested?.Invoke(this, EventArgs.Empty));
        RemoveCurveCommand = new RelayCommand(RemoveSelectedCurve, () => HasSelectedCurve);
        FlipCurveCommand = new RelayCommand(FlipSelectedCurve, () => HasSelectedCurve);
        SelectCurveCommand = new RelayCommand<MillCurveRow>(SelectCurve);
    }

    public void NoteCurveStatus(string text, bool error = false)
    {
        CurveStatusIsError = error;
        CurveStatusText = text;
    }

    public void ImportCurveFile(string path, float? modelTopZ)
    {
        var load = CurveFileReader.Read(path);
        if (load.Curves.Count == 0)
        {
            CurveStatusIsError = true;
            CurveStatusText = load.Error.Length > 0 ? load.Error : "That file has no curves.";
            return;
        }
        if (load.Flat && modelTopZ is float top)
            CurveFileReader.LiftFlat(load.Curves, top);
        int added = AddCurves(load.Curves);
        string place = !load.Flat
            ? "Kept the file height."
            : modelTopZ is float z
                ? $"Placed on the model top at Z {z:0.#} mm."
                : "No model selected, so the curves stay at Z 0.";
        CurveStatusIsError = false;
        CurveStatusText = $"Imported {added} curve(s). {load.UnitNote} {place}";
    }

    public int AddCurves(IReadOnlyList<MillCurve> incoming)
    {
        int added = 0;
        foreach (var curve in incoming)
        {
            if (curve.Points.Count < 2) continue;
            if (_curves.Any(existing => SameCurve(existing, curve))) continue;
            _curves.Add(CloneCurve(curve));
            added++;
        }
        if (added == 0) return 0;
        _selectedCurve = _curves.Count - 1;
        RebuildRows();
        NotifyCurves();
        return added;
    }

    public List<MillCurve> CollectIncludedCurves()
    {
        var list = new List<MillCurve>();
        foreach (var curve in _curves)
        {
            if (curve.Included && curve.Points.Count >= 2)
                list.Add(CloneCurve(curve));
        }
        return list;
    }

    public List<MillCurve> CaptureCurves()
    {
        var list = new List<MillCurve>(_curves.Count);
        foreach (var curve in _curves)
            list.Add(CloneCurve(curve));
        return list;
    }

    public void ReplaceCurves(IReadOnlyList<MillCurve>? curves)
    {
        _curves.Clear();
        if (curves is not null)
        {
            foreach (var curve in curves)
            {
                if (curve.Points.Count >= 2)
                    _curves.Add(CloneCurve(curve));
            }
        }
        _selectedCurve = _curves.Count > 0 ? 0 : -1;
        RebuildRows();
        NotifyCurves();
    }

    public IReadOnlyList<(Vector3 Pos, Vector3 Color)>? CurveMarkerSegments()
    {
        if (_curves.Count == 0) return null;
        var settings = new AdaMachiningSettings
        {
            CurveSide = CurveSide,
            CurveSideOffsetMm = (float)_curveSideOffsetMm,
            CurveIncludeToolRadius = _curveIncludeToolRadius,
            ToolDiameterMm = (float)ToolDiameterMm,
            StockToLeaveMm = (float)FinishAllowanceMm,
        };
        var segs = new List<(Vector3, Vector3)>();
        var source = new Vector3(0.45f, 0.70f, 0.85f);
        var selected = new Vector3(1f, 0.90f, 0.20f);
        var offset = new Vector3(0.25f, 0.82f, 0.35f);
        for (int i = 0; i < _curves.Count; i++)
        {
            var curve = _curves[i];
            if (!curve.Included || curve.Points.Count < 2) continue;
            AppendChain(segs, curve.Points, curve.Closed, i == _selectedCurve ? selected : source);
            var shifted = CurveOffset.Apply(curve.Points, curve.Closed, curve.Flipped, settings);
            AppendChain(segs, shifted, curve.Closed, offset);
        }
        return segs.Count >= 2 ? segs : null;
    }

    void SelectCurve(MillCurveRow? row)
    {
        if (row is null) return;
        int index = -1;
        for (int i = 0; i < _curves.Count; i++)
        {
            if (!ReferenceEquals(_curves[i], row.Curve)) continue;
            index = i;
            break;
        }
        if (index < 0) return;
        _selectedCurve = index;
        OnPropertyChanged(nameof(HasSelectedCurve));
        OnPropertyChanged(nameof(SelectedCurveLabel));
        RemoveCurveCommand.RaiseCanExecuteChanged();
        FlipCurveCommand.RaiseCanExecuteChanged();
    }

    void RemoveSelectedCurve()
    {
        if (!HasSelectedCurve) return;
        _curves.RemoveAt(_selectedCurve);
        if (_selectedCurve >= _curves.Count) _selectedCurve = _curves.Count - 1;
        RebuildRows();
        NotifyCurves();
        CurveStatusIsError = false;
        CurveStatusText = _curves.Count == 0
            ? "Import an OBJ, DXF, or SVG, or add edges found on the part."
            : $"{_curves.Count} curve(s) in the list.";
    }

    void FlipSelectedCurve()
    {
        if (!HasSelectedCurve) return;
        var curve = _curves[_selectedCurve];
        curve.Flipped = !curve.Flipped;
        CurveRows[_selectedCurve].Label = LabelFor(curve);
        OnPropertyChanged(nameof(SelectedCurveLabel));
        OnPropertyChanged(nameof(CurveRows));
        NotifyCurves();
    }

    void RebuildRows()
    {
        CurveRows.Clear();
        foreach (var curve in _curves)
            CurveRows.Add(new MillCurveRow { Curve = curve, Label = LabelFor(curve) });
        OnPropertyChanged(nameof(HasCurves));
        OnPropertyChanged(nameof(HasSelectedCurve));
        OnPropertyChanged(nameof(SelectedCurveLabel));
        RemoveCurveCommand.RaiseCanExecuteChanged();
        FlipCurveCommand.RaiseCanExecuteChanged();
    }

    void NotifyCurves()
    {
        OnPropertyChanged(nameof(HasIncludedCurves));
        OnPropertyChanged(nameof(CurveRevision));
    }

    static string LabelFor(MillCurve curve)
    {
        string shape = curve.Closed ? "closed" : "open";
        string flip = curve.Flipped ? " · flipped" : "";
        return $"{curve.Name} · {shape} · {LengthMm(curve):0.#} mm{flip}";
    }

    static float LengthMm(MillCurve curve)
    {
        float length = 0f;
        var pts = curve.Points;
        int last = curve.Closed ? pts.Count : pts.Count - 1;
        for (int i = 0; i < last; i++)
            length += Vector3.Distance(pts[i], pts[(i + 1) % pts.Count]);
        return length;
    }

    static bool SameCurve(MillCurve a, MillCurve b)
    {
        if (a.Points.Count != b.Points.Count || a.Closed != b.Closed) return false;
        if (a.Points.Count == 0) return true;
        int mid = a.Points.Count / 2;
        return Vector3.Distance(a.Points[0], b.Points[0]) <= 0.05f
            && Vector3.Distance(a.Points[^1], b.Points[^1]) <= 0.05f
            && Vector3.Distance(a.Points[mid], b.Points[mid]) <= 0.05f;
    }

    static MillCurve CloneCurve(MillCurve curve) => new()
    {
        Name = curve.Name,
        Closed = curve.Closed,
        Included = curve.Included,
        Flipped = curve.Flipped,
        Points = [.. curve.Points],
    };

    static void AppendChain(List<(Vector3 Pos, Vector3 Color)> segs, IReadOnlyList<Vector3> pts, bool closed, Vector3 color)
    {
        if (pts.Count < 2) return;
        int last = closed ? pts.Count : pts.Count - 1;
        for (int i = 0; i < last; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % pts.Count];
            if (Vector3.DistanceSquared(a, b) < 1e-8f) continue;
            segs.Add((a, color));
            segs.Add((b, color));
        }
    }
}
