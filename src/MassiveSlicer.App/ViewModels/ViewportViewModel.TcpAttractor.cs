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

/// <summary>One TCP Adjust handle: a sphere of influence and a cylinder for the extruder angle.</summary>
public sealed class TcpAttractorItem : ViewModelBase
{
    bool _suppress;
    double _radiusMm = 300;
    double _leanXDeg;
    double _leanYDeg;
    bool _isSelected;

    public TcpAttractorItem(string name, SceneNode node, OutlinerItemViewModel outliner)
    {
        Name = name;
        Node = node;
        Outliner = outliner;
    }

    public string Name { get; }
    public SceneNode Node { get; }
    public OutlinerItemViewModel Outliner { get; }

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
            if (!_suppress) Changed?.Invoke();
        }
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

    public void SetReadout(double radiusMm, double leanXDeg, double leanYDeg)
    {
        _suppress = true;
        RadiusMm = radiusMm;
        LeanXDeg = leanXDeg;
        LeanYDeg = leanYDeg;
        _suppress = false;
    }
}

public sealed partial class ViewportViewModel
{
    const float TcpAttractorDefaultRadius = 300f;

    readonly List<TcpAttractorItem> _tcpAttractorItems = [];

    public ObservableCollection<TcpAttractorItem> TcpAttractors { get; } = [];

    TcpAttractorItem? _selectedTcpAttractor;

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

    RelayCommand? _addTcpAttractorCommand;
    RelayCommand? _removeTcpAttractorCommand;
    RelayCommand<TcpAttractorItem>? _selectTcpAttractorCommand;

    public bool IsTcpAttractorNode(SceneNode? node)
    {
        if (node is null) return false;
        foreach (var item in _tcpAttractorItems)
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
        return null;
    }

    public void AddTcpAttractor() => AddTcpAttractor(null, notify: true, select: true);

    public void RemoveSelectedTcpAttractor()
    {
        if (_selectedTcpAttractor is { } item)
            RemoveTcpAttractor(item);
    }

    public void RemoveTcpAttractor(TcpAttractorItem item)
    {
        item.Changed = null;
        _tcpAttractorItems.Remove(item);
        TcpAttractors.Remove(item);
        OutlinerItems.Remove(item.Outliner);
        PendingRemoveNodes.Enqueue(item.Node);
        if (ReferenceEquals(_selectedTcpAttractor, item))
            SelectedTcpAttractor = _tcpAttractorItems.FirstOrDefault();
        else
            OnPropertyChanged(nameof(HasTcpAttractors));
        OnModelGeometryChanged?.Invoke();
        NotifyRenderNeeded();
    }

    /// <summary>Job reset already queued the nodes. Drop the bookkeeping without removing them twice.</summary>
    internal void ForgetTcpAttractors()
    {
        foreach (var item in _tcpAttractorItems)
            item.Changed = null;
        _tcpAttractorItems.Clear();
        TcpAttractors.Clear();
        _selectedTcpAttractor = null;
        OnPropertyChanged(nameof(SelectedTcpAttractor));
        OnPropertyChanged(nameof(HasSelectedTcpAttractor));
        OnPropertyChanged(nameof(HasTcpAttractors));
        OnPropertyChanged(nameof(SelectedTcpAttractorName));
    }

    internal void NoteTcpAttractorSelection(OutlinerItemViewModel? item)
    {
        SelectedTcpAttractor = item is { IsTcpAttractor: true }
            ? _tcpAttractorItems.FirstOrDefault(a => ReferenceEquals(a.Outliner, item))
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
    }

    public List<TcpAttractor> CaptureTcpAttractors()
    {
        var list = new List<TcpAttractor>(_tcpAttractorItems.Count);
        foreach (var item in _tcpAttractorItems)
            list.Add(SampleOf(item));
        return list;
    }

    public List<TcpAttractorPose> CaptureTcpAttractorPoses()
        => CaptureTcpAttractors().Select(TcpAttractorPose.From).ToList();

    public void RestoreTcpAttractors(IReadOnlyList<TcpAttractorPose>? poses)
    {
        if (poses is null) return;
        foreach (var pose in poses)
            AddTcpAttractor(pose, notify: false, select: false);
    }

    /// <summary>Console: tcpadj [list|add|remove|radius &lt;mm&gt;|lean &lt;x&gt; &lt;y&gt;]</summary>
    public string TcpAttractorCommand(string? args)
    {
        var parts = (args ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string verb = parts.Length == 0 ? "list" : parts[0].ToLowerInvariant();
        switch (verb)
        {
            case "list":
                if (_tcpAttractorItems.Count == 0) return "[tcpadj] none";
                return "[tcpadj] " + string.Join("  ", _tcpAttractorItems.Select(Describe));
            case "add":
                AddTcpAttractor();
                return "[tcpadj] added " + Describe(_tcpAttractorItems[^1]);
            case "remove":
                if (_selectedTcpAttractor is null && _tcpAttractorItems.Count == 1)
                    SelectedTcpAttractor = _tcpAttractorItems[0];
                if (_selectedTcpAttractor is null) return "[tcpadj] nothing selected";
                string name = _selectedTcpAttractor.Name;
                RemoveSelectedTcpAttractor();
                return $"[tcpadj] removed {name}";
            case "radius":
                if (parts.Length < 2 || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float radius))
                    return "[tcpadj] usage: tcpadj radius <mm>";
                var radiusItem = SelectedOrOnly();
                if (radiusItem is null) return "[tcpadj] nothing selected";
                radiusItem.RadiusMm = Math.Clamp(radius, 20f, 4000f);
                return "[tcpadj] " + Describe(radiusItem);
            case "lean":
                if (parts.Length < 3
                    || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float leanX)
                    || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float leanY))
                    return "[tcpadj] usage: tcpadj lean <xDeg> <yDeg>";
                var leanItem = SelectedOrOnly();
                if (leanItem is null) return "[tcpadj] nothing selected";
                leanItem.LeanXDeg = leanX;
                leanItem.LeanYDeg = leanY;
                return "[tcpadj] " + Describe(leanItem);
            default:
                return "[tcpadj] usage: tcpadj [list|add|remove|radius <mm>|lean <x> <y>]";
        }
    }

    TcpAttractorItem? SelectedOrOnly()
        => _selectedTcpAttractor ?? (_tcpAttractorItems.Count == 1 ? _tcpAttractorItems[0] : null);

    string Describe(TcpAttractorItem item)
    {
        var sample = SampleOf(item);
        return $"{item.Name} r={item.RadiusMm:0} lean=({item.LeanXDeg:0.#},{item.LeanYDeg:0.#}) " +
               $"at=({sample.Position.X:0},{sample.Position.Y:0},{sample.Position.Z:0})";
    }

    void AddTcpAttractor(TcpAttractorPose? pose, bool notify, bool select)
    {
        int n = _tcpAttractorItems.Count + 1;
        string name = $"TCP {n}";
        float radius = pose is { RadiusMm: > 1f } p ? p.RadiusMm : TcpAttractorDefaultRadius;

        var spawn = new Vector3(80f * (_tcpAttractorItems.Count), 0f, 400f);
        if (ComputeBedCenter() is { } bed)
            spawn = bed + new Vector3(80f * _tcpAttractorItems.Count, 0f, 200f);

        Vector3 axis = Vector3.UnitZ;
        if (pose is not null)
        {
            spawn = new Vector3(pose.X, pose.Y, pose.Z);
            axis = new Vector3(pose.AxisX, pose.AxisY, pose.AxisZ);
            if (axis.LengthSquared < 1e-8f) axis = Vector3.UnitZ;
        }

        var node = BuildTcpAttractorNode(name, radius, spawn, axis);
        PendingNodes.Enqueue(node);

        var outliner = CreateOutlinerItem(node, it =>
        {
            var owned = _tcpAttractorItems.FirstOrDefault(a => ReferenceEquals(a.Outliner, it));
            if (owned is not null) RemoveTcpAttractor(owned);
        }, displayName: name, canDelete: true);
        outliner.IsTcpAttractor = true;
        OutlinerItems.Add(outliner);

        var item = new TcpAttractorItem(name, node, outliner);
        var (leanX, leanY) = TcpAttractorEffect.LeanFromAxis(ToNumerics(axis));
        item.SetReadout(radius, leanX, leanY);
        item.Changed = () => OnTcpAttractorEdited(item);
        _tcpAttractorItems.Add(item);
        TcpAttractors.Add(item);
        OnPropertyChanged(nameof(HasTcpAttractors));

        if (select)
        {
            SelectedTcpAttractor = item;
            RequestSelectNode?.Invoke(node);
        }

        if (notify) OnModelGeometryChanged?.Invoke();
        NotifyRenderNeeded();
    }

    void OnTcpAttractorEdited(TcpAttractorItem item)
    {
        ApplyTcpAttractorVisual(item);
        if (ReferenceEquals(item, _selectedTcpAttractor))
        {
            OnPropertyChanged(nameof(TcpRadiusMm));
            OnPropertyChanged(nameof(TcpLeanXDeg));
            OnPropertyChanged(nameof(TcpLeanYDeg));
        }
        OnModelGeometryChanged?.Invoke();
        NotifyRenderNeeded();
    }

    void ApplyTcpAttractorVisual(TcpAttractorItem item)
    {
        foreach (var child in item.Node.Children)
        {
            if (!child.Name.EndsWith(" Range", StringComparison.Ordinal)) continue;
            child.LocalTransform = Matrix4.CreateScale(MathF.Max((float)item.RadiusMm, 1f));
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
        return new TcpAttractor(pos, (float)item.RadiusMm, axis);
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

    static System.Numerics.Vector3 ToNumerics(Vector3 v) => new(v.X, v.Y, v.Z);
    static Vector3 ToOpenTk(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);

    SceneNode BuildTcpAttractorNode(string name, float radius, Vector3 spawn, Vector3 axis)
    {
        var body = BuildCylinderGeometry(18f, 160f);
        var mesh = new MeshData(body.Pos, body.Nrm, body.Idx, name,
            new Vector4(0.95f, 0.55f, 0.12f, 1f),
            0.15f, 0.45f, uvs: null, tangents: null,
            material: new MaterialData
            {
                BaseColorFactor = new Vector4(0.95f, 0.55f, 0.12f, 1f),
                MetallicFactor = 0.15f,
                RoughnessFactor = 0.45f,
                EmissiveFactor = new Vector3(0.45f, 0.22f, 0.04f),
            });

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

        var shell = BuildSphereGeometry(1f, seg: 28, rings: 16);
        var glow = new MeshData(shell.Pos, shell.Nrm, shell.Idx, name + " Range",
            new Vector4(0.25f, 0.72f, 0.85f, 0.12f),
            0f, 1f, uvs: null, tangents: null,
            material: new MaterialData
            {
                BaseColorFactor = new Vector4(0.25f, 0.72f, 0.85f, 0.12f),
                MetallicFactor = 0f,
                RoughnessFactor = 1f,
                EmissiveFactor = new Vector3(0.08f, 0.25f, 0.30f),
                AlphaMode = AlphaMode.Blend,
            });
        node.AddChild(new SceneNode
        {
            Name = name + " Range",
            PendingMesh = glow,
            Selectable = false,
            PickIgnore = true,
            TranslucentPass = true,
            KeepOwnMaterial = true,
            LocalTransform = Matrix4.CreateScale(MathF.Max(radius, 1f)),
        });
        return node;
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
