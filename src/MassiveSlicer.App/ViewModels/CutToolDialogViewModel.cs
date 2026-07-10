using MassiveSlicer.ViewModels.Base;

namespace MassiveSlicer.ViewModels;

/// <summary>Parameters for the Cut Tool: planar split + Formbound alignment tabs + bolt lugs.</summary>
public sealed class CutToolDialogViewModel : ViewModelBase
{
    private double _height = 0;
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

    public string ModelName { get; init; } = "Model";
    public double ModelMinZ { get; init; }
    public double ModelMaxZ { get; init; }

    /// <summary>Cut plane height (world Z for horizontal cut, or offset along normal).</summary>
    public double Height
    {
        get => _height;
        set => SetField(ref _height, value);
    }

    public double NormalX
    {
        get => _normalX;
        set => SetField(ref _normalX, value);
    }

    public double NormalY
    {
        get => _normalY;
        set => SetField(ref _normalY, value);
    }

    public double NormalZ
    {
        get => _normalZ;
        set => SetField(ref _normalZ, value);
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
}
