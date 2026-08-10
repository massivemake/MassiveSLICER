using System.Text.RegularExpressions;

namespace MassiveSlicer.Core.IO;

/// <summary>
/// MassiveDRIVE waypoint <c>notes</c> tokens that tell MassiveSLICER to act during a Movement.
/// Coordinates / E1 and motion stay on MassiveDRIVE; SLICER only reacts to these tags.
/// </summary>
public static class MassiveDriveWaypointNotes
{
    /// <summary>Canonical token for hand-eye capture poses: <c>scan</c>.</summary>
    public const string ScanToken = "scan";

    /// <summary>Canonical token for rotary bed-cal capture poses: <c>bed</c>.</summary>
    public const string BedToken = "bed";

    /// <summary>Default Movement name for hand-eye / Auto-Calibrate Scan Tool.</summary>
    public const string ScannerCalibrationSequenceName = "Scanner Calibration";

    /// <summary>Default Movement name for rotary bed / Auto-Calibrate Bed.</summary>
    public const string BedCalibrationSequenceName = "Bed Calibration";

    static readonly HashSet<string> ScanTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "scan", "capture", "slicer:scan", "slicerscan",
        "handeye", "hand-eye", "hand_eye",
    };

    static readonly HashSet<string> BedTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "bed", "bedscan", "bed-cal", "bed_cal", "slicer:bed",
        "rotary", "e1sweep", "e1-sweep",
    };

    /// <summary>
    /// True when notes ask for a hand-eye Zivid frame
    /// (matches MassiveDRIVE <c>notes_requests_slicer_scan</c>).
    /// </summary>
    public static bool RequestsScan(string? notes)
        => MatchesTokenSet(notes, ScanTokens, bareWords: ["scan", "capture"]);

    /// <summary>
    /// True when notes ask for a rotary bed-cal board sample (+ surface scan)
    /// (matches MassiveDRIVE <c>notes_requests_slicer_bed</c>).
    /// </summary>
    public static bool RequestsBed(string? notes)
        => MatchesTokenSet(notes, BedTokens, bareWords: ["bed"]);

    /// <summary>Any capture dwell MassiveDRIVE opens for SLICER (scan or bed).</summary>
    public static bool RequestsCapture(string? notes)
        => RequestsScan(notes) || RequestsBed(notes);

    static bool MatchesTokenSet(string? notes, HashSet<string> tokens, string[] bareWords)
    {
        var raw = (notes ?? "").Trim();
        if (raw.Length == 0)
            return false;
        if (tokens.Contains(raw))
            return true;

        foreach (var tok in Regex.Split(raw, @"[\s,;|/]+"))
        {
            if (tok.Length > 0 && tokens.Contains(tok))
                return true;
        }

        foreach (var t in tokens)
        {
            if (bareWords.Contains(t, StringComparer.OrdinalIgnoreCase))
            {
                if (Regex.IsMatch(raw, $@"(?i)(?<!\w){Regex.Escape(t)}(?!\w)"))
                    return true;
            }
            else if (raw.Contains(t, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
