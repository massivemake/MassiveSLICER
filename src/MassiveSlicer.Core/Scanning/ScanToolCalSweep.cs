namespace MassiveSlicer.Core.Scanning;

/// <summary>
/// Wrist nutation poses for 3D hand-eye calibration (ported from SCAN_TOOL_CAL.src).
/// App drives these via CELL MS_AXIS — no separate KRL program.
/// </summary>
public static class ScanToolCalSweep
{
    public readonly record struct WristDelta(double A4, double A5, double A6);

    /// <summary>Nine wrist-only deltas (deg) about the operator's home pose.</summary>
    public static IReadOnlyList<WristDelta> PoseDeltas { get; } =
    [
        new(0, 0, 0),
        new(0, 8, 0),
        new(0, -8, 0),
        new(8, 0, 0),
        new(-8, 0, 0),
        new(0, 0, 15),
        new(0, 0, -15),
        new(7, 7, 0),
        new(-7, -7, 0),
    ];

    public const int PoseCount = 9;

    /// <summary>
    /// Wrist deltas for this cell — learned <c>bedScan.scanCalWristDeltas</c> when present and
    /// complete, otherwise <see cref="PoseDeltas"/>.
    /// </summary>
    public static IReadOnlyList<WristDelta> PoseDeltasForCell(Models.BedScanConfig? bedScan)
    {
        var custom = bedScan?.ScanCalWristDeltas;
        if (custom is not { Length: PoseCount })
            return PoseDeltas;

        var list = new WristDelta[PoseCount];
        for (int i = 0; i < PoseCount; i++)
            list[i] = new WristDelta(custom[i].A4, custom[i].A5, custom[i].A6);
        return list;
    }

    /// <summary>KRL used tool #5 (uncalibrated working scan tool) during the sweep.</summary>
    public const int CalToolIndex = 5;

    /// <summary>Hand-eye result is saved to tool #6.</summary>
    public const int ResultToolIndex = 6;

    public const int DefaultVelPct = 15;

    /// <summary>Stop halving wrist tilt below this scale factor.</summary>
    public const double MinScale = 0.125;
}