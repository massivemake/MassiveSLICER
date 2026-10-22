using MassiveSlicer.Commands;
using MassiveSlicer.ViewModels.Base;

namespace MassiveSlicer.ViewModels;

/// <summary>One Multi-Planar guide. Height is typed. X and Y are independent tilts.</summary>
public sealed class MultiPlanarPlaneRow : ViewModelBase
{
    internal AdditiveSettingsViewModel? Owner;

    public MultiPlanarPlaneRow(double heightPct, double angleY = 0, double angleX = 0)
    {
        _heightPct = heightPct;
        _angleY    = angleY;
        _angleX    = angleX;
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

    private double _angleX;
    /// <summary>Tilt about X, degrees. Leans the plane along Y.</summary>
    public double AngleX
    {
        get => _angleX;
        set
        {
            if (SetField(ref _angleX, Math.Clamp(value, -80.0, 80.0)))
                Owner?.BumpMultiPlanarStamp();
        }
    }

    private double _angleY;
    /// <summary>Tilt about Y, degrees. Leans the plane along X.</summary>
    public double AngleY
    {
        get => _angleY;
        set
        {
            if (SetField(ref _angleY, Math.Clamp(value, -80.0, 80.0)))
                Owner?.BumpMultiPlanarStamp();
        }
    }

    public RelayCommand RemoveCommand => _remove ??= new RelayCommand(() => Owner?.RemoveMultiPlanarPlane(this));
    private RelayCommand? _remove;

    public RelayCommand AlignCommand => _align ??= new RelayCommand(() => Owner?.ToggleAlignPick(this));
    private RelayCommand? _align;

    private bool _isAlignArmed;
    /// <summary>Waiting for a click on the model. The next face sets this plane.</summary>
    public bool IsAlignArmed
    {
        get => _isAlignArmed;
        private set => SetField(ref _isAlignArmed, value);
    }

    internal void SetAlignArmed(bool armed) => IsAlignArmed = armed;

    internal void ApplyFace(double heightPct, double angleX, double angleY)
    {
        _heightPct = Math.Clamp(heightPct, 0.0, 100.0);
        _angleX    = Math.Clamp(angleX, -80.0, 80.0);
        _angleY    = Math.Clamp(angleY, -80.0, 80.0);
        _isAlignArmed = false;
        OnPropertyChanged(nameof(HeightPct));
        OnPropertyChanged(nameof(AngleX));
        OnPropertyChanged(nameof(AngleY));
        OnPropertyChanged(nameof(IsAlignArmed));
        Owner?.BumpMultiPlanarStamp();
        Owner?.SyncAlignPickArmed();
    }
}
