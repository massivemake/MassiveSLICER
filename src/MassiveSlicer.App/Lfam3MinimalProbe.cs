namespace MassiveSlicer.App;

/// <summary>
/// Debug load for LFAM 3: robot + booster + rotary bed + HV extruder (no stands, spindle/scanner).
/// Bed grid and camera still come from the LFAM 3 cell JSON.
/// Set <see cref="Enabled"/> false to restore the full LFAM 3 cell.
/// </summary>
internal static class Lfam3MinimalProbe
{
    internal const bool Enabled = false;

    internal static bool IsActive(string? cellName)
        => Enabled && cellName?.Contains("LFAM 3", StringComparison.OrdinalIgnoreCase) == true;
}