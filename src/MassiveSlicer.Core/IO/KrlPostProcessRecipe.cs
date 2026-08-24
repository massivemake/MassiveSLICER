using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.IO;

/// <summary>
/// Overlay the KRL Post-Processing settings-menu / Lab recipe onto an export.
/// Every SRC / Send-to-Robot path must go through this so header, footer,
/// Robot Mode, and Travel Moves cannot drift from the synced recipe.
/// </summary>
public static class KrlPostProcessRecipe
{
    public static KrlExportSettings Apply(KrlExportSettings export, KrlPostProcessSettings recipe)
    {
        return export with
        {
            HeaderTemplate = string.IsNullOrWhiteSpace(recipe.HeaderText)
                ? export.HeaderTemplate
                : recipe.HeaderText,
            FooterTemplate = string.IsNullOrWhiteSpace(recipe.FooterText)
                ? export.FooterTemplate
                : recipe.FooterText,
            RobotModeEnabled = recipe.RobotModeEnabled ?? export.RobotModeEnabled,
            TravelStartStopEnabled = recipe.TravelStartStopEnabled ?? export.TravelStartStopEnabled,
            DigitalStartStopEnabled = false,
            ExtruderAirEnabled = recipe.ExtruderAirEnabled ?? export.ExtruderAirEnabled,
            ApoCvel = recipe.ApoCvel is { } apo
                ? (int)Math.Clamp(apo, 0, 100)
                : export.ApoCvel,
            OrientationLookAheadMm = recipe.OrientationLookAheadMm is { } look
                ? (float)look
                : export.OrientationLookAheadMm,
            OrientationSigmaMm = recipe.OrientationSigmaMm is { } sigma
                ? (float)sigma
                : export.OrientationSigmaMm,
            ExtrusionStartWaitSec = recipe.ExtrusionStartWaitSec is { } startWait
                ? (float)startWait
                : export.ExtrusionStartWaitSec,
            ExtrusionResumeWaitSec = recipe.ExtrusionResumeWaitSec is { } resumeWait
                ? (float)resumeWait
                : export.ExtrusionResumeWaitSec,
            SsPreTravelWaitSec = recipe.SsPreTravelWaitSec is { } preTravel
                ? (float)preTravel
                : export.SsPreTravelWaitSec,
            SsResumePrimePercent = recipe.SsResumePrimePercent is { } prime
                ? (float)prime
                : export.SsResumePrimePercent,
        };
    }
}
