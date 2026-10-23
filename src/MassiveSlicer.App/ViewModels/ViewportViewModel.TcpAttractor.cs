using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using MassiveSlicer.Commands;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing.Effects;
using MassiveSlicer.ViewModels.Base;
using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;

namespace MassiveSlicer.ViewModels;

/// <summary>One TCP Adjust handle: an influence volume and a bar for the aim.</summary>
public sealed class TcpAttractorItem : ViewModelBase
{
    bool _suppress;
    double _radiusMm = 300;
    double _heightMm;
    double _influence = 1;
    double _leanXDeg;
    double _leanYDeg;
    bool _isSelected;
    string _shape = "Spherical";
    string _frame = "Tool";
    bool _detract;
    float _aimX;
    float _aimY;
    float _aimZ = 1f;

    public TcpAttractorItem(string name, SceneNode node, OutlinerItemViewModel outliner, bool deflector = false)
    {
        Name = name;
        Node = node;
        Outliner = outliner;
        IsDeflector = deflector;
    }

    public string Name { get; }
    public SceneNode Node { get; }
    public OutlinerItemViewModel Outliner { get; }
    public bool IsDeflector { get; }
    /// <summary>True when the range mesh currently drawn is a cylinder.</summary>
    public bool RangeIsCylinder { get; set; }

    /// <summary>True when the amber arrowhead currently points back down the bar.</summary>
    public bool ArrowPointsNegative { get; set; }

    /// <summary>Last scale baked into the amber arrow. 1 matches the historical length.</summary>
    public float ArrowScale { get; set; } = 1f;

    /// <summary>Raised when the user edits radius or lean. Readout sync does not raise it.</summary>
    public Action? Changed { get; set; }

    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
    }

    public double RadiusMm
    {
        get => _radiusMm;
        set
        {
            if (Math.Abs(_radiusMm - value) < 0.05) return;
            _radiusMm = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(EffectiveHeightMm));
            if (!_suppress) Changed?.Invoke();
        }
    }

    /// <summary>
    /// Strength on top of the distance fade. 1 is the historical lean.
    /// The sphere or cylinder size does not change with this.
    /// </summary>
    public double Influence
    {
        get => _influence;
        set
        {
            double next = Math.Clamp(value, 0d, 2d);
            if (Math.Abs(_influence - next) < 0.005) return;
            _influence = next;
            OnPropertyChanged();
            if (!_suppress) Changed?.Invoke();
        }
    }

    /// <summary>Full cylinder height. Zero means the height still equals the diameter.</summary>
    public double HeightMm
    {
        get => _heightMm;
        set
        {
            if (Math.Abs(_heightMm - value) < 0.05) return;
            _heightMm = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(EffectiveHeightMm));
            if (!_suppress) Changed?.Invoke();
        }
    }

    /// <summary>Height the cylinder is drawn and saved at. An unset height tracks the diameter.</summary>
    public double EffectiveHeightMm => _heightMm > 1 ? _heightMm : Math.Max(_radiusMm, 1) * 2;

    /// <summary>
    /// Sets the cylinder diameter. The first time, an implicit height is stored so
    /// later diameter edits do not stretch the length.
    /// </summary>
    public void SetDiameterMm(double diameterMm)
    {
        double next = Math.Clamp(diameterMm * 0.5, 20d, 4000d);
        if (_heightMm <= 1)
        {
            double keep = EffectiveHeightMm;
            SetReadout(next, _leanXDeg, _leanYDeg, heightMm: keep);
            Changed?.Invoke();
        }
        else RadiusMm = next;
    }

    public double LeanXDeg
    {
        get => _leanXDeg;
        set
        {
            if (Math.Abs(_leanXDeg - value) < 0.05) return;
            _leanXDeg = value;
            OnPropertyChanged();
            if (!_suppress) Changed?.Invoke();
        }
    }

    public double LeanYDeg
    {
        get => _leanYDeg;
        set
        {
            if (Math.Abs(_leanYDeg - value) < 0.05) return;
            _leanYDeg = value;
            OnPropertyChanged();
            if (!_suppress) Changed?.Invoke();
        }
    }

    public string Shape
    {
        get => _shape;
        set
        {
            if (_shape == value || string.IsNullOrWhiteSpace(value)) return;
            _shape = value;
            OnPropertyChanged();
            if (!_suppress) Changed?.Invoke();
        }
    }

    public string Frame
    {
        get => _frame;
        set
        {
            if (_frame == value || string.IsNullOrWhiteSpace(value)) return;
            _frame = value;
            OnPropertyChanged();
            if (!_suppress) Changed?.Invoke();
        }
    }

    /// <summary>True leans the TCP direction away from the stick. False leans toward it.</summary>
    public bool Detract
    {
        get => _detract;
        set
        {
            if (_detract == value) return;
            _detract = value;
            OnPropertyChanged();
            if (!_suppress) Changed?.Invoke();
        }
    }

    public float AimX => _aimX;
    public float AimY => _aimY;
    public float AimZ => _aimZ;

    /// <summary>Unit direction in the selected TCP. Zero collapses to +Z.</summary>
    public Vector3 AimUnit => new(_aimX, _aimY, _aimZ);

    /// <summary>Stores a unit vector. The three components are normalized together.</summary>
    public void SetAim(float x, float y, float z)
    {
        var v = new Vector3(x, y, z);
        if (v.LengthSquared < 1e-10f) v = Vector3.UnitZ;
        else v = Vector3.Normalize(v);
        if (MathF.Abs(_aimX - v.X) < 5e-4f
            && MathF.Abs(_aimY - v.Y) < 5e-4f
            && MathF.Abs(_aimZ - v.Z) < 5e-4f)
            return;
        _aimX = v.X;
        _aimY = v.Y;
        _aimZ = v.Z;
        OnPropertyChanged(nameof(AimX));
        OnPropertyChanged(nameof(AimY));
        OnPropertyChanged(nameof(AimZ));
        if (!_suppress) Changed?.Invoke();
    }

    public void SetReadout(double radiusMm, double leanXDeg, double leanYDeg, string? shape = null, string? frame = null, Vector3? aimDir = null, double? heightMm = null, bool? detract = null, double? influence = null)
    {
        _suppress = true;
        RadiusMm = radiusMm;
        LeanXDeg = leanXDeg;
        LeanYDeg = leanYDeg;
        if (shape is not null) Shape = shape;
        if (frame is not null) Frame = frame;
        if (aimDir is { } aim) SetAim(aim.X, aim.Y, aim.Z);
        if (heightMm is { } height) HeightMm = height;
        if (detract is { } away) Detract = away;
        if (influence is { } weight) Influence = weight;
        _suppress = false;
    }
}

public sealed partial class ViewportViewModel
{
    const float TcpAttractorDefaultRadius = 300f;
    /// <summary>New deflectors start larger so a volume on the base can reach the wrist.</summary>
    const float TcpDeflectorDefaultRadius = 800f;

    readonly List<TcpAttractorItem> _tcpAttractorItems = [];
    readonly List<TcpAttractorItem> _tcpDeflectorItems = [];

    public ObservableCollection<TcpAttractorItem> TcpAttractors { get; } = [];
    public ObservableCollection<TcpAttractorItem> TcpDeflectors { get; } = [];

    public static string[] TcpShapeOptions { get; } = ["Spherical", "Cylindrical"];
    public static string[] TcpFrameOptions { get; } = ["Tool", "Robot flange"];

    TcpAttractorItem? _selectedTcpAttractor;
    TcpAttractorItem? _selectedTcpDeflector;

    public TcpAttractorItem? SelectedTcpAttractor
    {
        get => _selectedTcpAttractor;
        private set
        {
            if (ReferenceEquals(_selectedTcpAttractor, value)) return;
            if (_selectedTcpAttractor is not null) _selectedTcpAttractor.IsSelected = false;
            _selectedTcpAttractor = value;
            if (_selectedTcpAttractor is not null) _selectedTcpAttractor.IsSelected = true;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedTcpAttractor));
            OnPropertyChanged(nameof(HasTcpAttractors));
            OnPropertyChanged(nameof(SelectedTcpAttractorName));
            OnPropertyChanged(nameof(TcpRadiusMm));
            OnPropertyChanged(nameof(TcpLeanXDeg));
            OnPropertyChanged(nameof(TcpLeanYDeg));
            OnPropertyChanged(nameof(TcpShape));
            OnPropertyChanged(nameof(TcpIsCylinder));
            OnPropertyChanged(nameof(TcpDiameterMm));
            OnPropertyChanged(nameof(TcpHeightMm));
            OnPropertyChanged(nameof(TcpFrame));
            OnPropertyChanged(nameof(TcpAimX));
            OnPropertyChanged(nameof(TcpAimY));
            OnPropertyChanged(nameof(TcpAimZ));
            OnPropertyChanged(nameof(TcpDetract));
            OnPropertyChanged(nameof(TcpInfluence));
            NotifyRenderNeeded();
        }
    }

    public TcpAttractorItem? SelectedTcpDeflector
    {
        get => _selectedTcpDeflector;
        private set
        {
            if (ReferenceEquals(_selectedTcpDeflector, value)) return;
            if (_selectedTcpDeflector is not null) _selectedTcpDeflector.IsSelected = false;
            _selectedTcpDeflector = value;
            if (_selectedTcpDeflector is not null) _selectedTcpDeflector.IsSelected = true;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedTcpDeflector));
            OnPropertyChanged(nameof(HasTcpDeflectors));
            OnPropertyChanged(nameof(SelectedTcpDeflectorName));
            OnPropertyChanged(nameof(DeflectorRadiusMm));
            OnPropertyChanged(nameof(DeflectorDiameterMm));
            OnPropertyChanged(nameof(DeflectorHeightMm));
            OnPropertyChanged(nameof(DeflectorIsCylinder));
            OnPropertyChanged(nameof(DeflectorLeanXDeg));
            OnPropertyChanged(nameof(DeflectorLeanYDeg));
            OnPropertyChanged(nameof(DeflectorShape));
            OnPropertyChanged(nameof(DeflectorFrame));
        }
    }

    public bool HasSelectedTcpAttractor => _selectedTcpAttractor is not null;
    public bool HasTcpAttractors => TcpAttractors.Count > 0;
    public string SelectedTcpAttractorName => _selectedTcpAttractor?.Name ?? "";

    public double TcpRadiusMm
    {
        get => _selectedTcpAttractor?.RadiusMm ?? TcpAttractorDefaultRadius;
        set { if (_selectedTcpAttractor is { } item) item.RadiusMm = value; }
    }

    /// <summary>Cylinder diameter. Sphere attractors keep <see cref="TcpRadiusMm"/>.</summary>
    public double TcpDiameterMm
    {
        get => (_selectedTcpAttractor?.RadiusMm ?? TcpAttractorDefaultRadius) * 2.0;
        set { _selectedTcpAttractor?.SetDiameterMm(value); }
    }

    /// <summary>Full cylinder length along the stick. Unset heights still read as the diameter.</summary>
    public double TcpHeightMm
    {
        get => _selectedTcpAttractor?.EffectiveHeightMm ?? TcpAttractorDefaultRadius * 2.0;
        set
        {
            if (_selectedTcpAttractor is { } item)
                item.HeightMm = Math.Clamp(value, 40d, 8000d);
        }
    }

    public bool TcpIsCylinder => _selectedTcpAttractor?.Shape == "Cylindrical";

    public double TcpLeanXDeg
    {
        get => _selectedTcpAttractor?.LeanXDeg ?? 0;
        set { if (_selectedTcpAttractor is { } item) item.LeanXDeg = value; }
    }

    public double TcpLeanYDeg
    {
        get => _selectedTcpAttractor?.LeanYDeg ?? 0;
        set { if (_selectedTcpAttractor is { } item) item.LeanYDeg = value; }
    }

    public string TcpShape
    {
        get => _selectedTcpAttractor?.Shape ?? "Spherical";
        set { if (_selectedTcpAttractor is { } item) item.Shape = value; }
    }

    public string TcpFrame
    {
        get => _selectedTcpAttractor?.Frame ?? "Tool";
        set { if (_selectedTcpAttractor is { } item) item.Frame = value; }
    }

    public double TcpAimX
    {
        get => _selectedTcpAttractor?.AimX ?? 0;
        set => SetSelectedAim(0, value);
    }

    public double TcpAimY
    {
        get => _selectedTcpAttractor?.AimY ?? 0;
        set => SetSelectedAim(1, value);
    }

    public double TcpAimZ
    {
        get => _selectedTcpAttractor?.AimZ ?? 1;
        set => SetSelectedAim(2, value);
    }

    /// <summary>Unit aim of the selected attractor, in that TCP. +Z when none is selected.</summary>
    public Vector3 AttractorAimUnit => _selectedTcpAttractor?.AimUnit ?? Vector3.UnitZ;

    public bool AttractorAimOnFlange => _selectedTcpAttractor?.Frame == "Robot flange";

    /// <summary>Checked aims the selected handle away from the stick.</summary>
    public bool TcpDetract
    {
        get => _selectedTcpAttractor?.Detract ?? false;
        set { if (_selectedTcpAttractor is { } item) item.Detract = value; }
    }

    /// <summary>Strength of the selected attractor. 1 matches the lean from before this slider.</summary>
    public double TcpInfluence
    {
        get => _selectedTcpAttractor?.Influence ?? 1;
        set { if (_selectedTcpAttractor is { } item) item.Influence = value; }
    }

    void SetSelectedAim(int index, double value)
    {
        if (_selectedTcpAttractor is not { } item) return;
        float x = index == 0 ? (float)value : item.AimX;
        float y = index == 1 ? (float)value : item.AimY;
        float z = index == 2 ? (float)value : item.AimZ;
        float current = index switch { 0 => item.AimX, 1 => item.AimY, _ => item.AimZ };
        if (Math.Abs(current - value) < 0.0005) return;
        item.SetAim(x, y, z);
    }

    public bool HasSelectedTcpDeflector => _selectedTcpDeflector is not null;
    public bool HasTcpDeflectors => TcpDeflectors.Count > 0;
    public string SelectedTcpDeflectorName => _selectedTcpDeflector?.Name ?? "";

    public double DeflectorRadiusMm
    {
        get => _selectedTcpDeflector?.RadiusMm ?? TcpAttractorDefaultRadius;
        set { if (_selectedTcpDeflector is { } item) item.RadiusMm = value; }
    }

    /// <summary>Cylinder diameter. Sphere deflectors keep <see cref="DeflectorRadiusMm"/>.</summary>
    public double DeflectorDiameterMm
    {
        get => (_selectedTcpDeflector?.RadiusMm ?? TcpDeflectorDefaultRadius) * 2.0;
        set { _selectedTcpDeflector?.SetDiameterMm(value); }
    }

    /// <summary>Full cylinder length along the stick. Unset heights still read as the diameter.</summary>
    public double DeflectorHeightMm
    {
        get => _selectedTcpDeflector?.EffectiveHeightMm ?? TcpDeflectorDefaultRadius * 2.0;
        set
        {
            if (_selectedTcpDeflector is { } item)
                item.HeightMm = Math.Clamp(value, 40d, 8000d);
        }
    }

    public bool DeflectorIsCylinder => _selectedTcpDeflector?.Shape == "Cylindrical";

    public double DeflectorLeanXDeg
    {
        get => _selectedTcpDeflector?.LeanXDeg ?? 0;
        set { if (_selectedTcpDeflector is { } item) item.LeanXDeg = value; }
    }

    public double DeflectorLeanYDeg
    {
        get => _selectedTcpDeflector?.LeanYDeg ?? 0;
        set { if (_selectedTcpDeflector is { } item) item.LeanYDeg = value; }
    }

    public string DeflectorShape
    {
        get => _selectedTcpDeflector?.Shape ?? "Spherical";
        set { if (_selectedTcpDeflector is { } item) item.Shape = value; }
    }

    public string DeflectorFrame
    {
        get => _selectedTcpDeflector?.Frame ?? "Robot flange";
        set { if (_selectedTcpDeflector is { } item) item.Frame = value; }
    }

    /// <summary>Wired by the viewport: select the handle so the move/rotate gizmo grabs it.</summary>
    internal Action<SceneNode>? RequestSelectNode { get; set; }

    public ICommand AddTcpAttractorCommand => _addTcpAttractorCommand ??= new RelayCommand(() => AddTcpAttractor());
    public ICommand RemoveTcpAttractorCommand => _removeTcpAttractorCommand ??= new RelayCommand(RemoveSelectedTcpAttractor);
    public ICommand SelectTcpAttractorCommand => _selectTcpAttractorCommand ??=
        new RelayCommand<TcpAttractorItem>(item =>
        {
            if (item is null) return;
            SelectedTcpAttractor = item;
            RequestSelectNode?.Invoke(item.Node);
        });

    public ICommand AddTcpDeflectorCommand => _addTcpDeflectorCommand ??= new RelayCommand(() => AddTcpDeflector());
    public ICommand RemoveTcpDeflectorCommand => _removeTcpDeflectorCommand ??= new RelayCommand(RemoveSelectedTcpDeflector);
    public ICommand SelectTcpDeflectorCommand => _selectTcpDeflectorCommand ??=
        new RelayCommand<TcpAttractorItem>(item =>
        {
            if (item is null) return;
            SelectedTcpDeflector = item;
            RequestSelectNode?.Invoke(item.Node);
        });

    RelayCommand? _addTcpAttractorCommand;
    RelayCommand? _removeTcpAttractorCommand;
    RelayCommand<TcpAttractorItem>? _selectTcpAttractorCommand;
    RelayCommand? _addTcpDeflectorCommand;
    RelayCommand? _removeTcpDeflectorCommand;
    RelayCommand<TcpAttractorItem>? _selectTcpDeflectorCommand;

    public bool IsTcpAttractorNode(SceneNode? node)
    {
        if (node is null) return false;
        foreach (var item in _tcpAttractorItems)
            if (item.Node == node || item.Node.SelfAndDescendants().Any(n => n == node))
                return true;
        foreach (var item in _tcpDeflectorItems)
            if (item.Node == node || item.Node.SelfAndDescendants().Any(n => n == node))
                return true;
        return false;
    }

    public TcpAttractorItem? FindTcpAttractor(SceneNode? node)
    {
        if (node is null) return null;
        foreach (var item in _tcpAttractorItems)
            if (item.Node == node || item.Node.SelfAndDescendants().Any(n => n == node))
                return item;
        foreach (var item in _tcpDeflectorItems)
            if (item.Node == node || item.Node.SelfAndDescendants().Any(n => n == node))
                return item;
        return null;
    }

    public void AddTcpAttractor() => AddTcpHandle(deflector: false, pose: null, notify: true, select: true);

    public void AddTcpDeflector() => AddTcpHandle(deflector: true, pose: null, notify: true, select: true);

    public void RemoveSelectedTcpDeflector()
    {
        if (_selectedTcpDeflector is { } item)
            RemoveTcpAttractor(item);
    }

    public void RemoveSelectedTcpAttractor()
    {
        if (_selectedTcpAttractor is { } item)
            RemoveTcpAttractor(item);
    }

    public void RemoveTcpAttractor(TcpAttractorItem item)
    {
        item.Changed = null;
        _tcpAttractorItems.Remove(item);
        _tcpDeflectorItems.Remove(item);
        TcpAttractors.Remove(item);
        TcpDeflectors.Remove(item);
        OutlinerItems.Remove(item.Outliner);
        PendingRemoveNodes.Enqueue(item.Node);
        if (ReferenceEquals(_selectedTcpAttractor, item))
            SelectedTcpAttractor = _tcpAttractorItems.FirstOrDefault();
        else if (ReferenceEquals(_selectedTcpDeflector, item))
            SelectedTcpDeflector = _tcpDeflectorItems.FirstOrDefault();
        else
        {
            OnPropertyChanged(nameof(HasTcpAttractors));
            OnPropertyChanged(nameof(HasTcpDeflectors));
        }
        OnMillAttractorsChanged?.Invoke();
        NotifyRenderNeeded();
    }

    /// <summary>Job reset already queued the nodes. Drop the bookkeeping without removing them twice.</summary>
    internal void ForgetTcpAttractors()
    {
        foreach (var item in _tcpAttractorItems)
            item.Changed = null;
        foreach (var item in _tcpDeflectorItems)
            item.Changed = null;
        _tcpAttractorItems.Clear();
        _tcpDeflectorItems.Clear();
        TcpAttractors.Clear();
        TcpDeflectors.Clear();
        _selectedTcpAttractor = null;
        _selectedTcpDeflector = null;
        OnPropertyChanged(nameof(SelectedTcpAttractor));
        OnPropertyChanged(nameof(SelectedTcpDeflector));
        OnPropertyChanged(nameof(HasSelectedTcpAttractor));
        OnPropertyChanged(nameof(HasSelectedTcpDeflector));
        OnPropertyChanged(nameof(HasTcpAttractors));
        OnPropertyChanged(nameof(HasTcpDeflectors));
        OnPropertyChanged(nameof(SelectedTcpAttractorName));
        OnPropertyChanged(nameof(SelectedTcpDeflectorName));
    }

    internal void NoteTcpAttractorSelection(OutlinerItemViewModel? item)
    {
        SelectedTcpAttractor = item is { IsTcpAttractor: true, IsTcpDeflector: false }
            ? _tcpAttractorItems.FirstOrDefault(a => ReferenceEquals(a.Outliner, item))
            : null;
        SelectedTcpDeflector = item is { IsTcpDeflector: true }
            ? _tcpDeflectorItems.FirstOrDefault(a => ReferenceEquals(a.Outliner, item))
            : null;
    }

    public void SyncTcpAttractorReadout(SceneNode node)
    {
        var item = FindTcpAttractor(node);
        if (item is null) return;
        var axis = BodyAxis(node.WorldTransform);
        var (leanX, leanY) = TcpAttractorEffect.LeanFromAxis(ToNumerics(axis));
        item.SetReadout(item.RadiusMm, leanX, leanY);
        if (ReferenceEquals(item, _selectedTcpAttractor))
        {
            OnPropertyChanged(nameof(TcpLeanXDeg));
            OnPropertyChanged(nameof(TcpLeanYDeg));
        }
        else if (ReferenceEquals(item, _selectedTcpDeflector))
        {
            OnPropertyChanged(nameof(DeflectorLeanXDeg));
            OnPropertyChanged(nameof(DeflectorLeanYDeg));
        }
    }

    public List<TcpAttractor> CaptureTcpAttractors() => CaptureHandles(_tcpAttractorItems);

    public List<TcpAttractor> CaptureTcpAdjustments()
    {
        var list = CaptureHandles(_tcpAttractorItems);
        list.AddRange(CaptureHandles(_tcpDeflectorItems));
        return list;
    }

    public List<TcpAttractorPose> CaptureTcpAttractorPoses()
    {
        var poses = new List<TcpAttractorPose>();
        foreach (var item in _tcpAttractorItems.Concat(_tcpDeflectorItems))
        {
            var pose = TcpAttractorPose.From(SampleOf(item));
            pose.Name = item.Name;
            poses.Add(pose);
        }
        return poses;
    }

    public void RestoreTcpAttractors(IReadOnlyList<TcpAttractorPose>? poses)
    {
        if (poses is null) return;
        foreach (var pose in poses)
            AddTcpHandle(pose.Deflect, pose, notify: false, select: false);
    }

    List<TcpAttractor> CaptureHandles(List<TcpAttractorItem> items)
    {
        var list = new List<TcpAttractor>(items.Count);
        foreach (var item in items)
            list.Add(SampleOf(item));
        return list;
    }

    /// <summary>
    /// Console: tcpadj [list|add|remove|radius|lean|shape|frame].
    /// Prefix deflect for the deflector list: tcpadj deflect add.
    /// </summary>
    public string TcpAttractorCommand(string? args)
    {
        var parts = (args ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length > 0 && parts[0].Equals("deflect", StringComparison.OrdinalIgnoreCase))
        {
            var rest = parts.Length == 1 ? Array.Empty<string>() : parts[1..];
            return AdjustCommand(rest, deflector: true);
        }
        return AdjustCommand(parts, deflector: false);
    }

    string AdjustCommand(string[] parts, bool deflector)
    {
        var items = deflector ? _tcpDeflectorItems : _tcpAttractorItems;
        string verb = parts.Length == 0
            ? (deflector ? "add" : "list")
            : parts[0].ToLowerInvariant();
        string usage = deflector
            ? "[tcpadj] usage: tcpadj deflect [add|remove|radius <mm>|diameter <mm>|height <mm>|lean <x> <y>|shape <Spherical|Cylindrical>|frame <Tool|Flange>]"
            : "[tcpadj] usage: tcpadj [list|add|deflect|remove|radius <mm>|diameter <mm>|height <mm>|influence <0-2>|lean <x> <y>|shape <Spherical|Cylindrical>|frame <Tool|Flange>|aim <x> <y> <z>|axis <X|Y|Z>|detract <on|off>]";
        switch (verb)
        {
            case "list":
                if (!deflector)
                {
                    if (_tcpAttractorItems.Count == 0 && _tcpDeflectorItems.Count == 0) return "[tcpadj] none";
                    var both = _tcpAttractorItems.Concat(_tcpDeflectorItems);
                    return "[tcpadj] " + string.Join("  ", both.Select(Describe));
                }
                if (items.Count == 0) return "[tcpadj] none";
                return "[tcpadj] " + string.Join("  ", items.Select(Describe));
            case "add":
                if (deflector) AddTcpDeflector();
                else AddTcpAttractor();
                return "[tcpadj] added " + Describe(items[^1]);
            case "remove":
                var removing = SelectedOf(deflector);
                if (removing is null) return "[tcpadj] nothing selected";
                string name = removing.Name;
                RemoveTcpAttractor(removing);
                return $"[tcpadj] removed {name}";
            case "influence":
                if (deflector) return "[tcpadj] deflectors have no influence";
                if (parts.Length < 2 || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float influence))
                    return usage;
                var influenceItem = SelectedOf(false);
                if (influenceItem is null) return "[tcpadj] nothing selected";
                influenceItem.Influence = influence;
                return "[tcpadj] " + Describe(influenceItem);
            case "radius":
                if (parts.Length < 2 || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float radius))
                    return usage;
                var radiusItem = SelectedOf(deflector);
                if (radiusItem is null) return "[tcpadj] nothing selected";
                radiusItem.RadiusMm = Math.Clamp(radius, 20f, 4000f);
                return "[tcpadj] " + Describe(radiusItem);
            case "diameter":
                if (parts.Length < 2
                    || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float diameter))
                    return usage;
                var diameterItem = SelectedOf(deflector);
                if (diameterItem is null) return "[tcpadj] nothing selected";
                diameterItem.SetDiameterMm(diameter);
                return "[tcpadj] " + Describe(diameterItem);
            case "height":
                if (parts.Length < 2
                    || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float height))
                    return usage;
                var heightItem = SelectedOf(deflector);
                if (heightItem is null) return "[tcpadj] nothing selected";
                heightItem.HeightMm = Math.Clamp(height, 40f, 8000f);
                return "[tcpadj] " + Describe(heightItem);
            case "lean":
                if (parts.Length < 3
                    || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float leanX)
                    || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float leanY))
                    return usage;
                var leanItem = SelectedOf(deflector);
                if (leanItem is null) return "[tcpadj] nothing selected";
                leanItem.LeanXDeg = leanX;
                leanItem.LeanYDeg = leanY;
                return "[tcpadj] " + Describe(leanItem);
            case "shape":
                if (parts.Length < 2 || ParseShape(parts[1]) is not { } shape)
                    return usage;
                var shapeItem = SelectedOf(deflector);
                if (shapeItem is null) return "[tcpadj] nothing selected";
                shapeItem.Shape = shape;
                return "[tcpadj] " + Describe(shapeItem);
            case "frame":
                string frameRaw = string.Join(' ', parts.Skip(1));
                if (ParseFrame(frameRaw) is not { } frame)
                    return usage;
                var frameItem = SelectedOf(deflector);
                if (frameItem is null) return "[tcpadj] nothing selected";
                frameItem.Frame = frame;
                return "[tcpadj] " + Describe(frameItem);
            case "detract":
                if (deflector) return usage;
                bool detractOn;
                if (parts.Length < 2)
                    detractOn = SelectedOf(false) is { } current && !current.Detract;
                else if (parts[1].Equals("on", StringComparison.OrdinalIgnoreCase)
                    || parts[1].Equals("true", StringComparison.OrdinalIgnoreCase))
                    detractOn = true;
                else if (parts[1].Equals("off", StringComparison.OrdinalIgnoreCase)
                    || parts[1].Equals("false", StringComparison.OrdinalIgnoreCase))
                    detractOn = false;
                else
                    return usage;
                var detractItem = SelectedOf(false);
                if (detractItem is null) return "[tcpadj] nothing selected";
                detractItem.Detract = detractOn;
                return "[tcpadj] " + Describe(detractItem);
            case "aim":
                if (deflector || parts.Length < 4
                    || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float aimX)
                    || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float aimY)
                    || !float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float aimZ))
                    return usage;
                var vectorItem = SelectedOf(false);
                if (vectorItem is null) return "[tcpadj] nothing selected";
                vectorItem.SetAim(aimX, aimY, aimZ);
                return "[tcpadj] " + Describe(vectorItem);
            case "axis":
                if (deflector || parts.Length < 2 || ParseAim(parts[1]) is not { } axisAim)
                    return usage;
                var aimItem = SelectedOf(false);
                if (aimItem is null) return "[tcpadj] nothing selected";
                aimItem.SetAim(axisAim.X, axisAim.Y, axisAim.Z);
                return "[tcpadj] " + Describe(aimItem);
            default:
                return usage;
        }
    }

    TcpAttractorItem? SelectedOf(bool deflector)
    {
        if (deflector)
            return _selectedTcpDeflector ?? (_tcpDeflectorItems.Count == 1 ? _tcpDeflectorItems[0] : null);
        return _selectedTcpAttractor ?? (_tcpAttractorItems.Count == 1 ? _tcpAttractorItems[0] : null);
    }

    static string? ParseShape(string raw) => raw.ToLowerInvariant() switch
    {
        "spherical" or "sphere" => "Spherical",
        "cylindrical" or "cylinder" => "Cylindrical",
        _ => null,
    };

    static string? ParseFrame(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "tool" => "Tool",
        "flange" or "robot" or "robot flange" => "Robot flange",
        _ => null,
    };

    static Vector3? ParseAim(string raw) => raw.Trim().ToUpperInvariant() switch
    {
        "X" or "+X" => Vector3.UnitX,
        "Y" or "+Y" => Vector3.UnitY,
        "Z" or "+Z" => Vector3.UnitZ,
        "-X" => -Vector3.UnitX,
        "-Y" => -Vector3.UnitY,
        "-Z" => -Vector3.UnitZ,
        _ => null,
    };

    string Describe(TcpAttractorItem item)
    {
        var sample = SampleOf(item);
        var u = item.AimUnit;
        string aim = item.IsDeflector
            ? ""
            : FormattableString.Invariant($" aim=({u.X:0.00},{u.Y:0.00},{u.Z:0.00})")
              + (item.Detract ? " detract" : "");
        string cylinderSize = item.Shape == "Cylindrical"
            ? $" dia={item.RadiusMm * 2:0} h={item.EffectiveHeightMm:0}"
            : "";
        string weight = item.IsDeflector ? "" : FormattableString.Invariant($" inf={item.Influence:0.##}");
        return $"{item.Name} r={item.RadiusMm:0}{cylinderSize}{weight} lean=({item.LeanXDeg:0.#},{item.LeanYDeg:0.#}) " +
               $"shape={item.Shape} frame={item.Frame}{aim} " +
               $"at=({sample.Position.X:0},{sample.Position.Y:0},{sample.Position.Z:0})";
    }

    /// <summary>Next free attractor1, attractor2, … name. Older TCP handles are left as they are.</summary>
    string NextAttractorName()
    {
        int n = 1;
        while (_tcpAttractorItems.Any(a =>
            string.Equals(a.Name, $"attractor{n}", StringComparison.OrdinalIgnoreCase)))
            n++;
        return $"attractor{n}";
    }

    void AddTcpHandle(bool deflector, TcpAttractorPose? pose, bool notify, bool select)
    {
        var items = deflector ? _tcpDeflectorItems : _tcpAttractorItems;
        int n = items.Count + 1;
        string name = deflector
            ? $"DEF {n}"
            : !string.IsNullOrWhiteSpace(pose?.Name)
                ? pose!.Name!
                : pose is not null
                    ? $"TCP {n}"
                    : NextAttractorName();
        float fallback = deflector ? TcpDeflectorDefaultRadius : TcpAttractorDefaultRadius;
        float radius = pose is { RadiusMm: > 1f } p ? p.RadiusMm : fallback;

        int stagger = _tcpAttractorItems.Count + _tcpDeflectorItems.Count;
        var spawn = ActiveBaseOrigin() + new Vector3(80f * stagger, 0f, 0f);
        Vector3 axis = Vector3.UnitZ;
        if (pose is not null)
        {
            spawn = new Vector3(pose.X, pose.Y, pose.Z);
            axis = new Vector3(pose.AxisX, pose.AxisY, pose.AxisZ);
            if (axis.LengthSquared < 1e-8f) axis = Vector3.UnitZ;
        }

        bool cylinder = pose?.Shape == nameof(TcpAdjustShape.Cylinder);
        float height = 0f;
        if (pose is { HeightMm: > 1f }) height = pose.HeightMm;
        string frame = pose?.Frame == nameof(TcpAdjustFrame.Flange) ? "Robot flange"
            : deflector ? "Robot flange" : "Tool";
        if (pose?.Frame == nameof(TcpAdjustFrame.Tool)) frame = "Tool";
        bool detract = !deflector && pose is { Detract: true };
        float influence = !deflector && pose is not null
            ? Math.Clamp(pose.Influence, 0f, 2f)
            : 1f;
        bool pointNegative = !deflector && StickArrowPointsNegative(frame, detract);
        Vector3 aim = Vector3.UnitZ;
        if (!deflector && pose is not null)
        {
            var parsed = pose.ToAttractor().AimDir;
            aim = new Vector3(parsed.X, parsed.Y, parsed.Z);
        }

        var node = BuildTcpAttractorNode(name, radius, spawn, axis, deflector, cylinder, height, pointNegative, influence);
        PendingNodes.Enqueue(node);

        var outliner = CreateOutlinerItem(node, it =>
        {
            var owned = items.FirstOrDefault(a => ReferenceEquals(a.Outliner, it));
            if (owned is not null) RemoveTcpAttractor(owned);
        }, displayName: name, canDelete: true);
        // Both flags: every existing "skip attractors" filter also skips deflectors.
        // The deflector flag is what the icon, the menu, and the outliner dump use.
        outliner.IsTcpAttractor = true;
        outliner.IsTcpDeflector = deflector;
        OutlinerItems.Add(outliner);

        var item = new TcpAttractorItem(name, node, outliner, deflector);
        var (leanX, leanY) = TcpAttractorEffect.LeanFromAxis(ToNumerics(axis));
        item.SetReadout(radius, leanX, leanY, cylinder ? "Cylindrical" : "Spherical", frame, deflector ? null : aim, height,
            detract: detract, influence: influence);
        item.ArrowPointsNegative = pointNegative;
        item.ArrowScale = deflector ? 1f : ArrowVisualScale(influence);
        item.RangeIsCylinder = cylinder;
        item.Changed = () => OnTcpAttractorEdited(item);
        items.Add(item);
        if (deflector) TcpDeflectors.Add(item);
        else TcpAttractors.Add(item);
        OnPropertyChanged(deflector ? nameof(HasTcpDeflectors) : nameof(HasTcpAttractors));

        if (select)
        {
            if (deflector) SelectedTcpDeflector = item;
            else SelectedTcpAttractor = item;
            RequestSelectNode?.Invoke(node);
        }

        if (notify) OnMillAttractorsChanged?.Invoke();
        NotifyRenderNeeded();
    }

    /// <summary>World origin of the BASE the robot is using. Not the robot origin.</summary>
    Vector3 ActiveBaseOrigin()
    {
        if (ActiveCell is { } cell)
        {
            int baseNo = Robot is { KrlBaseIndex: > 0 } robot ? robot.KrlBaseIndex : 1;
            var origin = PrintSurface.ForKrlBase(cell, baseNo).Origin;
            return new Vector3(origin.X, origin.Y, origin.Z);
        }
        return new Vector3(0f, 0f, 400f);
    }

    void OnTcpAttractorEdited(TcpAttractorItem item)
    {
        ApplyTcpAttractorVisual(item);
        if (ReferenceEquals(item, _selectedTcpAttractor))
        {
            OnPropertyChanged(nameof(TcpRadiusMm));
            OnPropertyChanged(nameof(TcpLeanXDeg));
            OnPropertyChanged(nameof(TcpLeanYDeg));
            OnPropertyChanged(nameof(TcpShape));
            OnPropertyChanged(nameof(TcpIsCylinder));
            OnPropertyChanged(nameof(TcpDiameterMm));
            OnPropertyChanged(nameof(TcpHeightMm));
            OnPropertyChanged(nameof(TcpFrame));
            OnPropertyChanged(nameof(TcpAimX));
            OnPropertyChanged(nameof(TcpAimY));
            OnPropertyChanged(nameof(TcpAimZ));
            OnPropertyChanged(nameof(TcpDetract));
            OnPropertyChanged(nameof(TcpInfluence));
            NotifyRenderNeeded();
        }
        else if (ReferenceEquals(item, _selectedTcpDeflector))
        {
            OnPropertyChanged(nameof(DeflectorRadiusMm));
            OnPropertyChanged(nameof(DeflectorDiameterMm));
            OnPropertyChanged(nameof(DeflectorHeightMm));
            OnPropertyChanged(nameof(DeflectorIsCylinder));
            OnPropertyChanged(nameof(DeflectorLeanXDeg));
            OnPropertyChanged(nameof(DeflectorLeanYDeg));
            OnPropertyChanged(nameof(DeflectorShape));
            OnPropertyChanged(nameof(DeflectorFrame));
        }
        OnMillAttractorsChanged?.Invoke();
        NotifyRenderNeeded();
    }

    void ApplyTcpAttractorVisual(TcpAttractorItem item)
    {
        if (!item.IsDeflector)
        {
            bool pointNegative = StickArrowPointsNegative(item.Frame, item.Detract);
            float arrowScale = ArrowVisualScale((float)item.Influence);
            if (pointNegative != item.ArrowPointsNegative || MathF.Abs(arrowScale - item.ArrowScale) > 0.001f)
            {
                item.Node.PendingMesh = BuildStickMesh(item.Name, pointNegative, arrowScale);
                PendingModelRefresh.Enqueue(item.Node);
                item.ArrowPointsNegative = pointNegative;
                item.ArrowScale = arrowScale;
            }
        }

        bool cylinder = item.Shape == "Cylindrical";
        foreach (var child in item.Node.Children)
        {
            if (!child.Name.EndsWith(" Range", StringComparison.Ordinal)) continue;
            if (cylinder != item.RangeIsCylinder)
            {
                child.PendingMesh = cylinder
                    ? BuildRangeMesh(item.Name, cylinder: true, item.IsDeflector)
                    : BuildRangeMesh(item.Name, cylinder: false, item.IsDeflector);
                PendingModelRefresh.Enqueue(child);
                item.RangeIsCylinder = cylinder;
            }
            child.LocalTransform = RangeScale(
                (float)item.RadiusMm, cylinder, item.IsDeflector, (float)item.HeightMm);
        }

        // Lean is a world axis (nozzle at the origin, body along +axis). The handle may sit
        // under a moved bed, so the local matrix is that world pose pulled back into the parent.
        var axis = TcpAttractorEffect.AxisFromLean((float)item.LeanXDeg, (float)item.LeanYDeg);
        var worldNow = item.Node.WorldTransform;
        var worldPos = new Vector3(worldNow.M41, worldNow.M42, worldNow.M43);
        var world = RotationFromUnitZ(ToOpenTk(axis)) * Matrix4.CreateTranslation(worldPos);
        var parent = item.Node.Parent?.WorldTransform ?? Matrix4.Identity;
        Matrix4.Invert(parent, out var invParent);
        item.Node.LocalTransform = world * invParent;
    }

    TcpAttractor SampleOf(TcpAttractorItem item)
    {
        var world = item.Node.WorldTransform;
        var pos = new System.Numerics.Vector3(world.M41, world.M42, world.M43);
        var axis = ToNumerics(BodyAxis(world));
        var shape = item.Shape == "Cylindrical" ? TcpAdjustShape.Cylinder : TcpAdjustShape.Sphere;
        var frame = item.Frame == "Robot flange" ? TcpAdjustFrame.Flange : TcpAdjustFrame.Tool;
        var aim = item.IsDeflector
            ? System.Numerics.Vector3.UnitZ
            : new System.Numerics.Vector3(item.AimX, item.AimY, item.AimZ);
        float height = (float)item.HeightMm;
        return new TcpAttractor(
            pos, (float)item.RadiusMm, axis, shape, frame, item.IsDeflector, aim, height,
            Detract: !item.IsDeflector && item.Detract,
            Influence: item.IsDeflector ? 1f : (float)item.Influence);
    }

    static Vector3 BodyAxis(Matrix4 world)
    {
        var axis = new Vector3(world.M31, world.M32, world.M33);
        return axis.LengthSquared < 1e-8f ? Vector3.UnitZ : Vector3.Normalize(axis);
    }

    static Matrix4 RotationFromUnitZ(Vector3 axis)
    {
        axis = axis.LengthSquared < 1e-8f ? Vector3.UnitZ : Vector3.Normalize(axis);
        float d = Math.Clamp(Vector3.Dot(Vector3.UnitZ, axis), -1f, 1f);
        if (d > 0.9999f) return Matrix4.Identity;
        if (d < -0.9999f) return Matrix4.CreateRotationX(MathF.PI);
        var pivot = Vector3.Normalize(Vector3.Cross(Vector3.UnitZ, axis));
        return Matrix4.CreateFromAxisAngle(pivot, MathF.Acos(d));
    }

    /// <summary>
    /// Unit cylinder mesh spans 1 mm of radius and 1 mm of half-height.
    /// A deflector with a stored height stretches only along the stick.
    /// </summary>
    static Matrix4 RangeScale(float radiusMm, bool cylinder, bool deflector, float heightMm)
    {
        float radial = MathF.Max(radiusMm, 1f);
        float axial = radial;
        if (cylinder && heightMm > 1f)
            axial = MathF.Max(heightMm * 0.5f, 1f);
        return Matrix4.CreateScale(radial, radial, cylinder ? axial : radial);
    }

    static System.Numerics.Vector3 ToNumerics(Vector3 v) => new(v.X, v.Y, v.Z);
    static Vector3 ToOpenTk(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);

    /// <summary>
    /// Tool arrows point down the bar, so the head sits at the handle.
    /// Flange arrows point along the bar, so the head sits at the far end.
    /// A detractor swaps the two. The shaft stays on the +Z side either way.
    /// </summary>
    static bool StickArrowPointsNegative(string frame, bool detract)
    {
        var axis = System.Numerics.Vector3.UnitZ;
        var dir = TcpAttractorEffect.InfluenceDirection(new TcpAttractor(
            default, 1f, axis,
            Frame: frame == "Robot flange" ? TcpAdjustFrame.Flange : TcpAdjustFrame.Tool,
            Detract: detract));
        return System.Numerics.Vector3.Dot(dir, axis) < 0f;
    }

    /// <summary>Arrow size for an influence of 1. A zero weight stays a small positive mesh.</summary>
    static float ArrowVisualScale(float influence) => MathF.Max(0.05f, influence);

    SceneNode BuildTcpAttractorNode(string name, float radius, Vector3 spawn, Vector3 axis, bool deflector, bool cylinder, float heightMm = 0f, bool pointNegative = false, float influence = 1f)
    {
        var mesh = deflector
            ? BuildBarMesh(name, BuildCylinderGeometry(18f, 160f), deflector: true)
            : BuildStickMesh(name, pointNegative, ArrowVisualScale(influence));

        var node = new SceneNode
        {
            Name = name,
            PendingMesh = mesh,
            Selectable = true,
            KeepOwnMaterial = true,
            CullFaces = false,
            LocalTransform = RotationFromUnitZ(axis) * Matrix4.CreateTranslation(spawn),
        };
        node.EnsurePlacement(Vector3.Zero);

        node.AddChild(new SceneNode
        {
            Name = name + " Range",
            PendingMesh = BuildRangeMesh(name, cylinder, deflector),
            Selectable = false,
            PickIgnore = true,
            TranslucentPass = true,
            KeepOwnMaterial = true,
            LocalTransform = RangeScale(radius, cylinder, deflector, heightMm),
        });
        return node;
    }

    MeshData BuildStickMesh(string name, bool pointNegative, float scale = 1f)
        => BuildBarMesh(name, BuildStickArrow(pointNegative, scale), deflector: false);

    static MeshData BuildBarMesh(string name, (Vector3[] Pos, Vector3[] Nrm, uint[] Idx) body, bool deflector)
    {
        var bar = deflector
            ? new Vector4(0.35f, 0.62f, 0.95f, 1f)
            : new Vector4(0.95f, 0.55f, 0.12f, 1f);
        var barGlow = deflector
            ? new Vector3(0.08f, 0.18f, 0.40f)
            : new Vector3(0.45f, 0.22f, 0.04f);
        return new MeshData(body.Pos, body.Nrm, body.Idx, name,
            bar,
            0.15f, 0.45f, uvs: null, tangents: null,
            material: new MaterialData
            {
                BaseColorFactor = bar,
                MetallicFactor = 0.15f,
                RoughnessFactor = 0.45f,
                EmissiveFactor = barGlow,
            });
    }

    /// <summary>
    /// Influence 1 fits inside the old amber handle: a cylinder of radius 18 mm
    /// and length 160 mm. The head stays inside that radius, and the tail stays
    /// inside that length. A downward arrow only pokes a short tip past the origin.
    /// </summary>
    static (Vector3[] Pos, Vector3[] Nrm, uint[] Idx) BuildStickArrow(bool pointNegative, float scale = 1f)
    {
        const float length = 152f;
        const float tipPast = 12f;
        const float shaftRadius = 9f;
        const float headRadius = 16f;
        const float headLength = 40f;
        scale = MathF.Max(scale, 0.05f);
        float tipZ = (pointNegative ? -tipPast : length) * scale;
        float tailZ = (pointNegative ? length : 0f) * scale;
        return BuildArrow(
            tailZ, tipZ,
            shaftRadius * scale, headRadius * scale, headLength * scale);
    }

    static (Vector3[] Pos, Vector3[] Nrm, uint[] Idx) BuildArrow(
        float tailZ, float tipZ, float shaftRadius, float headRadius, float headLength, int seg = 24)
    {
        float dir = tipZ >= tailZ ? 1f : -1f;
        float headBaseZ = tipZ - dir * headLength;
        float shaftLow = MathF.Min(tailZ, headBaseZ);
        float shaftHigh = MathF.Max(tailZ, headBaseZ);
        var shaft = BuildCylinderGeometry(shaftRadius, MathF.Max(shaftHigh - shaftLow, 1f), seg);

        var positions = new List<Vector3>(shaft.Pos.Length + (seg + 1) * 4);
        var normals = new List<Vector3>(shaft.Nrm.Length + (seg + 1) * 4);
        var indices = new List<uint>(shaft.Idx.Length + seg * 9);
        for (int i = 0; i < shaft.Pos.Length; i++)
        {
            positions.Add(shaft.Pos[i] + new Vector3(0f, 0f, shaftLow));
            normals.Add(shaft.Nrm[i]);
        }
        indices.AddRange(shaft.Idx);

        float nLen = MathF.Sqrt(headLength * headLength + headRadius * headRadius);
        float nRad = headLength / nLen;
        float nZ = dir * headRadius / nLen;
        uint ring = (uint)positions.Count;
        for (int i = 0; i <= seg; i++)
        {
            float u = i / (float)seg * MathF.Tau;
            float c = MathF.Cos(u), s = MathF.Sin(u);
            positions.Add(new Vector3(c * headRadius, s * headRadius, headBaseZ));
            normals.Add(new Vector3(c * nRad, s * nRad, nZ));
        }
        uint tips = (uint)positions.Count;
        for (int i = 0; i <= seg; i++)
        {
            float u = i / (float)seg * MathF.Tau;
            float c = MathF.Cos(u), s = MathF.Sin(u);
            positions.Add(new Vector3(0f, 0f, tipZ));
            normals.Add(new Vector3(c * nRad, s * nRad, nZ));
        }
        for (int i = 0; i < seg; i++)
        {
            uint a = ring + (uint)i;
            uint b = tips + (uint)i;
            uint c = ring + (uint)i + 1;
            uint d = tips + (uint)i + 1;
            if (dir > 0f)
                indices.AddRange([a, b, c, c, b, d]);
            else
                indices.AddRange([a, c, b, c, d, b]);
        }

        // Close the underside of the head. Normal faces back toward the tail.
        var under = new Vector3(0f, 0f, -dir);
        uint center = (uint)positions.Count;
        positions.Add(new Vector3(0f, 0f, headBaseZ));
        normals.Add(under);
        uint underRing = (uint)positions.Count;
        for (int i = 0; i <= seg; i++)
        {
            float u = i / (float)seg * MathF.Tau;
            positions.Add(new Vector3(MathF.Cos(u) * headRadius, MathF.Sin(u) * headRadius, headBaseZ));
            normals.Add(under);
        }
        for (int i = 0; i < seg; i++)
        {
            uint e = underRing + (uint)i;
            uint f = underRing + (uint)i + 1;
            if (dir > 0f)
                indices.AddRange([center, f, e]);
            else
                indices.AddRange([center, e, f]);
        }

        return (positions.ToArray(), normals.ToArray(), indices.ToArray());
    }

    MeshData BuildRangeMesh(string name, bool cylinder, bool deflector)
    {
        var color = deflector
            ? new Vector4(0.90f, 0.32f, 0.22f, 0.14f)
            : new Vector4(0.25f, 0.72f, 0.85f, 0.12f);
        var emissive = deflector
            ? new Vector3(0.35f, 0.08f, 0.05f)
            : new Vector3(0.08f, 0.25f, 0.30f);
        Vector3[] pos;
        Vector3[] nrm;
        uint[] idx;
        if (cylinder)
        {
            var raw = BuildCylinderGeometry(1f, 2f, seg: 28);
            pos = new Vector3[raw.Pos.Length];
            for (int i = 0; i < pos.Length; i++)
                pos[i] = raw.Pos[i] + new Vector3(0f, 0f, -1f);
            nrm = raw.Nrm;
            idx = raw.Idx;
        }
        else
        {
            var shell = BuildSphereGeometry(1f, seg: 28, rings: 16);
            pos = shell.Pos;
            nrm = shell.Nrm;
            idx = shell.Idx;
        }
        return new MeshData(pos, nrm, idx, name + " Range", color,
            0f, 1f, uvs: null, tangents: null,
            material: new MaterialData
            {
                BaseColorFactor = color,
                MetallicFactor = 0f,
                RoughnessFactor = 1f,
                EmissiveFactor = emissive,
                AlphaMode = AlphaMode.Blend,
            });
    }

    static (Vector3[] Pos, Vector3[] Nrm, uint[] Idx) BuildCylinderGeometry(float radius, float height, int seg = 24)
    {
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var indices = new List<uint>();

        uint Ring(float z, float ny)
        {
            uint start = (uint)positions.Count;
            for (int i = 0; i <= seg; i++)
            {
                float u = i / (float)seg * MathF.Tau;
                float c = MathF.Cos(u), s = MathF.Sin(u);
                positions.Add(new Vector3(c * radius, s * radius, z));
                normals.Add(Vector3.Normalize(new Vector3(c, s, ny)));
            }
            return start;
        }

        uint bottom = Ring(0f, 0f);
        uint top = Ring(height, 0f);
        for (int i = 0; i < seg; i++)
        {
            uint a = bottom + (uint)i;
            uint b = top + (uint)i;
            indices.AddRange([a, b, a + 1, a + 1, b, b + 1]);
        }

        uint Cap(float z, bool up)
        {
            uint center = (uint)positions.Count;
            var n = up ? Vector3.UnitZ : -Vector3.UnitZ;
            positions.Add(new Vector3(0f, 0f, z));
            normals.Add(n);
            uint start = (uint)positions.Count;
            for (int i = 0; i <= seg; i++)
            {
                float u = i / (float)seg * MathF.Tau;
                positions.Add(new Vector3(MathF.Cos(u) * radius, MathF.Sin(u) * radius, z));
                normals.Add(n);
            }
            for (int i = 0; i < seg; i++)
            {
                if (up) indices.AddRange([center, start + (uint)i, start + (uint)i + 1]);
                else indices.AddRange([center, start + (uint)i + 1, start + (uint)i]);
            }
            return center;
        }

        Cap(0f, up: false);
        Cap(height, up: true);
        return (positions.ToArray(), normals.ToArray(), indices.ToArray());
    }
}
