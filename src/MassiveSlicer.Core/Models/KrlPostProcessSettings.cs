namespace MassiveSlicer.Core.Models;

/// <summary>
/// Factory defaults for KRL Post-Processing (Rules + Header + Footer).
/// Stored in repo <c>assets/krl_postprocess.json</c> so a rebuild and a GitHub
/// clone keep the same recipe. Null / missing fields leave in-code defaults.
/// </summary>
public sealed class KrlPostProcessSettings
{
    public string HeaderText { get; set; } = "";
    public string FooterText { get; set; } = "";
    public string DefaultHeaderText { get; set; } = "";
    public string DefaultFooterText { get; set; } = "";

    /// <summary>True once Rules have been written to the factory file.</summary>
    public bool RulesSaved { get; set; }

    public bool? RobotModeEnabled { get; set; }
    public bool? TravelStartStopEnabled { get; set; }
    public bool? ExtruderAirEnabled { get; set; }
    public double? ApoCvel { get; set; }

    public CodeEditorInjectSettings? CodeEditorInject { get; set; }

    public bool? SmoothRotation { get; set; }
    public int? SmoothRotationRadius { get; set; }
    public double? SmoothRotationMaxRateDegPerMm { get; set; }
    public double? OrientationLookAheadMm { get; set; }
    public double? OrientationSigmaMm { get; set; }

    public double? ExtrusionStartWaitSec { get; set; }
    public double? ExtrusionResumeWaitSec { get; set; }
    public double? SsPreTravelWaitSec { get; set; }
    public double? SsResumePrimePercent { get; set; }

    public bool? ResumeRampEnabled { get; set; }
    public double? ResumeRampStartSpeed { get; set; }
    public double? ResumeRampStartRpmPercent { get; set; }
    public double? ResumeRampDistanceMm { get; set; }
    public int? ResumeRampSteps { get; set; }
}
