using MassiveSlicer.Commands;
using MassiveSlicer.ViewModels.Base;

namespace MassiveSlicer.ViewModels;

/// <summary>One row of the Multi-Planar guide plane stack: a tilt anchored at a
/// height along the part. Edits bump the owner's stamp to trigger a re-slice.</summary>
public sealed class MultiPlanarPlaneRow : ViewModelBase
{
    internal AdditiveSettingsViewModel? Owner;

    public MultiPlanarPlaneRow(double heightPct, double angleDeg)
    {
        _heightPct = heightPct;
        _angleDeg  = angleDeg;
    }

    private double _heightPct;
    public double HeightPct
    {
        get => _heightPct;
        set
        {
            if (SetField(ref _heightPct, Math.Clamp(value, 0.0, 100.0)))
                Owner?.BumpMultiPlanarStamp();
        }
    }

    private double _angleDeg;
    public double AngleDeg
    {
        get => _angleDeg;
        set
        {
            if (SetField(ref _angleDeg, Math.Clamp(value, -80.0, 80.0)))
                Owner?.BumpMultiPlanarStamp();
        }
    }

    public RelayCommand RemoveCommand => _remove ??= new RelayCommand(() => Owner?.RemoveMultiPlanarPlane(this));
    private RelayCommand? _remove;
}
