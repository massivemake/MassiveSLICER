namespace MassiveSlicer.Core.Scanning;

/// <summary>
/// E1 sweep schedule for app-orchestrated bed calibration (driven via CELL MS_AXIS — no separate KRL).
/// </summary>
public static class BedScanCalSweep
{
    /// <summary>Default matches legacy BED_SCAN_CAL.src: 9 × 40° from −180°.</summary>
    public static IReadOnlyList<double> DefaultE1Angles(int steps = 9, double startDeg = -180, double stepDeg = 40)
    {
        var list = new double[steps];
        for (int i = 0; i < steps; i++)
            list[i] = startDeg + i * stepDeg;
        return list;
    }

    /// <summary>Bed-cal E1 schedule (9 × 40° from −180°). Independent of <c>bedScan.scanSteps</c> (pre-print scan).</summary>
    public static IReadOnlyList<double> E1AnglesForCell(Models.BedScanConfig? _)
        => DefaultE1Angles();

    /// <summary>Y vantage offsets (mm) for multi-pose bed cal; defaults to centre + −300 mm Y.</summary>
    public static IReadOnlyList<float> VantageOffsetsY(Models.BedScanConfig? bedScan)
    {
        var o = bedScan?.BedCalVantageOffsetsY;
        if (o is { Length: > 0 })
            return o;
        return [0f, -300f];
    }
}