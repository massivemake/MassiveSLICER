using System.Globalization;
using System.Text.RegularExpressions;

namespace MassiveSlicer.Core.Models;

/// <summary>
/// Caracol Code Editor / RobotCodeEditor 1.0.6 inject recipe.
/// Applied by <see cref="IO.CodeEditorSrcInjector"/> when Travel Moves is on.
/// Time offsets always use the job print speed (mm/s). Stop <c>$VEL.CP</c>
/// is rewritten to half that speed at export.
/// </summary>
public sealed class CodeEditorInjectSettings
{
    public static readonly string[] UnitOptions = ["Millimeters", "Meters", "Milliseconds", "Seconds"];
    public static readonly string[] DirectionOptions = ["Before", "After"];

    private static readonly Regex VelCp = new(
        @"\$VEL\.CP\s*=\s*[-\d.]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Unused leftover. Timing always uses export print speed.</summary>
    public double SpeedMmS { get; set; }

    public double ShortTravelThresholdMm { get; set; } = 1.0;
    public double ToleranceMm { get; set; } = 0.01;

    public string StartExtrudingCommand { get; set; } = "$OUT[7] = TRUE\nWAIT SEC 0.5";
    public string StopExtrudingCommand { get; set; } =
        "TRIGGER WHEN DISTANCE=0 DELAY=0 DO $OUT[7] = FALSE\n$VEL.CP = 0.050000";

    public double StopDistance { get; set; } = 350.0;
    public string StopUnits { get; set; } = "Milliseconds";
    /// <summary>Before = walk back along the bead to <c>;travel start</c>. After = into the travel.</summary>
    public string StopDirection { get; set; } = "Before";

    public string EnterUrmCommand { get; set; } = "TRIGGER WHEN DISTANCE=0 DELAY=0 DO $OUT[8] = TRUE";
    public string ExitUrmCommand { get; set; } = "TRIGGER WHEN DISTANCE=0 DELAY=0 DO $OUT[8] = FALSE";
    public double EnterUrmDistance { get; set; } = 3500.0;
    public string EnterUrmUnits { get; set; } = "Milliseconds";
    public string EnterUrmDirection { get; set; } = "Before";
    public double ExitUrmDistance { get; set; } = 3500.0;
    public string ExitUrmUnits { get; set; } = "Milliseconds";
    public string ExitUrmDirection { get; set; } = "After";

    public bool AlwaysInsert { get; set; } = true;

    /// <summary>
    /// PointLoader cannot stream CAD <c>TRIGGER WHEN DISTANCE</c>. When true, those
    /// lines become the inner <c>$OUT[...]</c> assignment on its own line.
    /// </summary>
    public bool PointLoaderSafeIo { get; set; } = true;

    public CodeEditorInjectSettings Clone() => new()
    {
        SpeedMmS = SpeedMmS,
        ShortTravelThresholdMm = ShortTravelThresholdMm,
        ToleranceMm = ToleranceMm,
        StartExtrudingCommand = StartExtrudingCommand,
        StopExtrudingCommand = StopExtrudingCommand,
        StopDistance = StopDistance,
        StopUnits = StopUnits,
        StopDirection = StopDirection,
        EnterUrmCommand = EnterUrmCommand,
        ExitUrmCommand = ExitUrmCommand,
        EnterUrmDistance = EnterUrmDistance,
        EnterUrmUnits = EnterUrmUnits,
        EnterUrmDirection = EnterUrmDirection,
        ExitUrmDistance = ExitUrmDistance,
        ExitUrmUnits = ExitUrmUnits,
        ExitUrmDirection = ExitUrmDirection,
        AlwaysInsert = AlwaysInsert,
        PointLoaderSafeIo = PointLoaderSafeIo,
    };

    public static bool IsBefore(string? direction)
        => !string.Equals(direction, "After", StringComparison.OrdinalIgnoreCase);

    public static double DistanceMm(string? units, double value, double speedMmS) => units switch
    {
        "Meters" => value * 1000.0,
        "Milliseconds" => value * speedMmS / 1000.0,
        "Seconds" => value * speedMmS,
        _ => value,
    };

    public static double HalfPrintSpeedMps(double printSpeedMps)
        => Math.Max(0.001, printSpeedMps * 0.5);

    public static string HalfPrintVelLine(double printSpeedMps)
        => "$VEL.CP = " + HalfPrintSpeedMps(printSpeedMps).ToString("F6", CultureInfo.InvariantCulture);

    /// <summary>Rewrite or append the stop <c>$VEL.CP</c> line to half of print speed.</summary>
    public static string WithHalfPrintVel(string? command, double printSpeedMps)
    {
        string line = HalfPrintVelLine(printSpeedMps);
        if (string.IsNullOrWhiteSpace(command))
            return line;
        if (VelCp.IsMatch(command))
            return VelCp.Replace(command, line);
        return command.TrimEnd() + "\n" + line;
    }
}
