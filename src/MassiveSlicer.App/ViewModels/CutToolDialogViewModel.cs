using System.Numerics;
using MassiveSlicer.ViewModels.Base;

namespace MassiveSlicer.ViewModels;

/// <summary>
/// Interactive Cut Tool session state: ghosted plane + gizmo-driven pose, plus
/// Formbound tabs / bolt-lug options. Bound to the floating viewport panel (not a modal).
/// </summary>
public sealed class CutToolDialogViewModel : ViewModelBase
{
    private double _centerX;
    private double _centerY;
    private double _centerZ;
    private double _normalX;
    private double _normalY;
    private double _normalZ = 1;
    private double _connectorSpacing = 40;
    private double _tabWidth = 12;
    private double _tabDepth = 8;
    private double _tabHeight = 6;
    private double _boltDiameter = 6;
    private double _boltLugDiameter = 14;
    private double _minCornerRadius = 3;
    private bool _addConnectors = true;
    private bool _placeOnCut = true;
    private float _planeSize = 100f;
    private bool _suppressHeightNotify;

    public string ModelName { get; set; } = "Model";
    public double ModelMinZ { get; set; }
    public double ModelMaxZ { get; set; }

    /// <summary>World-space center of the cut plane (gizmo pivot).</summary>
    public double CenterX
    {
        get => _centerX;
        set
        {
            if (SetField(ref _centerX, value))
                NotifyHeightFromCenter();
        }
    }

    public double CenterY
    {
        get => _centerY;
        set
        {
            if (SetField(ref _centerY, value))
                NotifyHeightFromCenter();
        }
    }

    public double CenterZ
    {
        get => _centerZ;
        set
        {
            if (SetField(ref _centerZ, value))
                NotifyHeightFromCenter();
        }
    }

    /// <summary>Offset of the plane along its normal from world origin (= Dot(center, n)).</summary>
    public double Height
    {
        get
        {
            var n = UnitNormal();
            return _centerX * n.X + _centerY * n.Y + _centerZ * n.Z;
        }
        set
        {
            if (_suppressHeightNotify) return;
            var n = UnitNormal();
            double cur = _centerX * n.X + _centerY * n.Y + _centerZ * n.Z;
            double d = value - cur;
            if (Math.Abs(d) < 1e-9) return;
            _centerX += n.X * d;
            _centerY += n.Y * d;
            _centerZ += n.Z * d;
            OnPropertyChanged(nameof(CenterX));
            OnPropertyChanged(nameof(CenterY));
            OnPropertyChanged(nameof(CenterZ));
            OnPropertyChanged(nameof(Height));
            OnChanged?.Invoke();
        }
    }

    public double NormalX
    {
        get => _normalX;
        set
        {
            if (SetField(ref _normalX, value))
            {
                NormalizeNormalsInPlace();
                NotifyHeightFromCenter();
                OnChanged?.Invoke();
            }
        }
    }

    public double NormalY
    {
        get => _normalY;
        set
        {
            if (SetField(ref _normalY, value))
            {
                NormalizeNormalsInPlace();
                NotifyHeightFromCenter();
                OnChanged?.Invoke();
            }
        }
    }

    public double NormalZ
    {
        get => _normalZ;
        set
        {
            if (SetField(ref _normalZ, value))
            {
                NormalizeNormalsInPlace();
                NotifyHeightFromCenter();
                OnChanged?.Invoke();
            }
        }
    }

    public bool AddConnectors
    {
        get => _addConnectors;
        set => SetField(ref _addConnectors, value);
    }

    /// <summary>Keep both halves in place at the cut (vs. separating for display).</summary>
    public bool PlaceOnCut
    {
        get => _placeOnCut;
        set => SetField(ref _placeOnCut, value);
    }

    public double ConnectorSpacing
    {
        get => _connectorSpacing;
        set => SetField(ref _connectorSpacing, Math.Clamp(value, 10, 500));
    }

    public double TabWidth
    {
        get => _tabWidth;
        set => SetField(ref _tabWidth, Math.Clamp(value, 4, 80));
    }

    public double TabDepth
    {
        get => _tabDepth;
        set => SetField(ref _tabDepth, Math.Clamp(value, 2, 40));
    }

    public double TabHeight
    {
        get => _tabHeight;
        set => SetField(ref _tabHeight, Math.Clamp(value, 2, 40));
    }

    public double BoltDiameter
    {
        get => _boltDiameter;
        set => SetField(ref _boltDiameter, Math.Clamp(value, 2, 30));
    }

    public double BoltLugDiameter
    {
        get => _boltLugDiameter;
        set => SetField(ref _boltLugDiameter, Math.Clamp(value, 6, 50));
    }

    public double MinCornerRadius
    {
        get => _minCornerRadius;
        set => SetField(ref _minCornerRadius, Math.Clamp(value, 1, 20));
    }

    /// <summary>Half-diagonal of the ghosted plane quad (world mm).</summary>
    public float PlaneSize
    {
        get => _planeSize;
        set => SetField(ref _planeSize, Math.Max(10f, value));
    }

    /// <summary>Fired when plane pose changes (typed fields or gizmo).</summary>
    public Action? OnChanged { get; set; }

    public Vector3 Center => new((float)_centerX, (float)_centerY, (float)_centerZ);

    public Vector3 UnitNormal()
    {
        var n = new Vector3((float)_normalX, (float)_normalY, (float)_normalZ);
        float len = n.Length();
        if (len < 1e-8f) return Vector3.UnitZ;
        return n / len;
    }

    /// <summary>Bulk-set plane pose from gizmo drag without recursive height churn.</summary>
    public void SetPose(Vector3 center, Vector3 normal)
    {
        float len = normal.Length();
        if (len < 1e-8f) normal = Vector3.UnitZ;
        else normal /= len;

        bool changed = false;
        if (Math.Abs(_centerX - center.X) > 1e-6 || Math.Abs(_centerY - center.Y) > 1e-6
            || Math.Abs(_centerZ - center.Z) > 1e-6)
        {
            _centerX = center.X;
            _centerY = center.Y;
            _centerZ = center.Z;
            OnPropertyChanged(nameof(CenterX));
            OnPropertyChanged(nameof(CenterY));
            OnPropertyChanged(nameof(CenterZ));
            changed = true;
        }
        if (Math.Abs(_normalX - normal.X) > 1e-6 || Math.Abs(_normalY - normal.Y) > 1e-6
            || Math.Abs(_normalZ - normal.Z) > 1e-6)
        {
            _normalX = normal.X;
            _normalY = normal.Y;
            _normalZ = normal.Z;
            OnPropertyChanged(nameof(NormalX));
            OnPropertyChanged(nameof(NormalY));
            OnPropertyChanged(nameof(NormalZ));
            changed = true;
        }
        if (changed)
        {
            NotifyHeightFromCenter();
            OnChanged?.Invoke();
        }
    }

    public void SetNormalPreset(float x, float y, float z)
    {
        var n = new Vector3(x, y, z);
        float len = n.Length();
        if (len < 1e-8f) return;
        n /= len;
        // Keep plane center; only reorient.
        _normalX = n.X;
        _normalY = n.Y;
        _normalZ = n.Z;
        OnPropertyChanged(nameof(NormalX));
        OnPropertyChanged(nameof(NormalY));
        OnPropertyChanged(nameof(NormalZ));
        NotifyHeightFromCenter();
        OnChanged?.Invoke();
    }

    private void NormalizeNormalsInPlace()
    {
        var n = new Vector3((float)_normalX, (float)_normalY, (float)_normalZ);
        float len = n.Length();
        if (len < 1e-8f)
        {
            _normalX = 0; _normalY = 0; _normalZ = 1;
        }
        else
        {
            _normalX = n.X / len;
            _normalY = n.Y / len;
            _normalZ = n.Z / len;
        }
        OnPropertyChanged(nameof(NormalX));
        OnPropertyChanged(nameof(NormalY));
        OnPropertyChanged(nameof(NormalZ));
    }

    private void NotifyHeightFromCenter()
    {
        _suppressHeightNotify = true;
        OnPropertyChanged(nameof(Height));
        _suppressHeightNotify = false;
        OnChanged?.Invoke();
    }
}
