using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.IO;

/// <summary>
/// Speed and extrusion RPM the timeline shows for one extrude move.
/// Same inputs as KRL export: the layer settings from <see cref="ToolpathRpm.ForLayer"/>,
/// then that move's speed and RPM scales. An absolute RPM override wins outright.
/// </summary>
public static class ToolpathMotionReadout
{
    public readonly record struct Demand(double SpeedMmS, float RpmPercent);

    public static Demand ForExtrude(ToolpathMove move, KrlExportSettings layerSettings)
    {
        float sScale = Math.Max(move.PrintSpeedScale, 1e-6f);
        float rpmScale = sScale * Math.Max(move.HeightScale, 1e-6f);
        if (move.IsResumeRamp)
        {
            sScale *= Math.Max(move.ResumeSpeedScale, 1e-6f);
            rpmScale *= Math.Max(move.ResumeRpmScale, 1e-6f);
        }

        // Robot speed does not take HeightScale. Flow does. Matches KrlExporter.
        double speed = layerSettings.PrintSpeedMps * 1000.0 * sScale;
        float pct = move.RpmPercentOverride is { } abs
            ? Math.Min(Math.Max(abs, 0f), 100f)
            : Math.Min(ToolpathRpm.BasePercent(layerSettings) * Math.Max(rpmScale, 0f), 100f);
        return new Demand(speed, pct);
    }
}
