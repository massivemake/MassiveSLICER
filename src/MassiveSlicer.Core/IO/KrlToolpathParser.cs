using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.IO;

/// <summary>Print (extruder T1/T2/T3) vs mill (spindle) for an imported <c>.src</c>.</summary>
public enum KrlImportKind { Print, Mill }

/// <summary>
/// Parses KUKA KRL (.src) Cartesian motion — <c>LIN</c>/<c>PTP</c> with an inline frame
/// <c>{X .., Y .., Z .. [, A .., B .., C ..] [, E1 ..]}</c> — into a <see cref="Toolpath"/> for visualisation
/// and scrubbing. Positions are the literal KRL frame values plus <c>worldOffset</c>
/// (Print Bed 0,0,0 = <c>bed.origin</c>) so they land in scene/world space.
/// <c>E1</c> is kept on each move (last-known hold when a later frame omits it) so LFAM 1
/// rail simulation can replay the program.
/// Print programs (<see cref="KrlImportKind.Print"/>): <c>LIN</c> → <see cref="MoveKind.Extrude"/>.
/// Mill programs: <c>LIN</c> → <see cref="MoveKind.Mill"/>. <c>PTP</c> → <see cref="MoveKind.Travel"/>.
/// Moves to named targets (e.g. <c>PTP apos</c>) and joint <c>AXIS</c> frames are skipped — only
/// inline Cartesian frames carry a toolpath.
/// </summary>
public static class KrlToolpathParser
{
    // LIN/PTP (not the _REL variants — the trailing \b excludes "LIN_REL") followed by a { ... } frame.
    private static readonly Regex MoveRe = new(
        @"\b(LIN|PTP)\b[^\{\r\n]*\{([^}]*)\}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // CadCommands / header: T1 = 220, $ANOUT comment "; T1 = 220C", wipe dip T1 = 180.
    private static readonly Regex PrintTempAssignRe = new(
        @"\bT[123]\s*=\s*-?\d",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Export comment block: "; T1 220 C  T2 210 C  T3 200 C"
    private static readonly Regex PrintTempCommentRe = new(
        @";\s*T1\s+\d",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SetTRe = new(
        @"\bSET_T[123]\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MillModeRe = new(
        @";\s*Mode mill\b|;\s*Spindle RPM\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PrintModeRe = new(
        @";\s*Mode print\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// True when the SRC sets extruder zone temps (<c>T1</c>/<c>T2</c>/<c>T3</c>).
    /// That is a print program, not a mill path.
    /// </summary>
    public static bool HasPrintTemperatures(string krl)
        => !string.IsNullOrEmpty(krl)
           && (PrintTempAssignRe.IsMatch(krl)
               || PrintTempCommentRe.IsMatch(krl)
               || SetTRe.IsMatch(krl));

    /// <summary>
    /// Print if T1/T2/T3 temps (or <c>; Mode print</c>) are in the file.
    /// Mill if <c>; Mode mill</c> / spindle RPM and no print temps.
    /// Else the mounted KRL tool: T1/T2/T3 = print, T11/T12 = mill.
    /// Ambiguous files stay mill so a LIN-only mill SRC keeps the old kind.
    /// </summary>
    public static KrlImportKind Classify(string krl, int selectedKrlTool = 0)
    {
        if (HasPrintTemperatures(krl) || PrintModeRe.IsMatch(krl ?? ""))
            return KrlImportKind.Print;
        if (!string.IsNullOrEmpty(krl) && MillModeRe.IsMatch(krl))
            return KrlImportKind.Mill;
        if (selectedKrlTool is 1 or 2 or 3)
            return KrlImportKind.Print;
        if (selectedKrlTool is 11 or 12)
            return KrlImportKind.Mill;
        return KrlImportKind.Mill;
    }

    /// <summary>
    /// Builds a toolpath from KRL text. <paramref name="moveCount"/> returns the number of segments
    /// produced (0 = no inline Cartesian moves found). Kind is inferred from the text (no selected tool).
    /// </summary>
    public static Toolpath Parse(string krl, Vector3 worldOffset, out int moveCount)
        => Parse(krl, worldOffset, out moveCount, Classify(krl));

    /// <summary>
    /// Same as <see cref="Parse(string, Vector3, out int)"/> with an explicit print vs mill kind
    /// (selected TOOL # plus file temps).
    /// </summary>
    public static Toolpath Parse(string krl, Vector3 worldOffset, out int moveCount, KrlImportKind kind)
    {
        moveCount = 0;
        var tp = new Toolpath();
        if (string.IsNullOrEmpty(krl)) return tp;

        var pts = new List<(Vector3 P, bool IsLin, float E1)>();
        float lastE1 = float.NaN;
        foreach (Match m in MoveRe.Matches(krl))
        {
            var body = m.Groups[2].Value;
            if (!TryField(body, "X", out float x) || !TryField(body, "Y", out float y) || !TryField(body, "Z", out float z))
                continue;   // a non-Cartesian frame (e.g. {A1 ..}) — skip
            bool isLin = m.Groups[1].Value.StartsWith("LIN", StringComparison.OrdinalIgnoreCase);
            if (TryField(body, "E1", out float e1))
                lastE1 = e1;
            pts.Add((new Vector3(x, y, z) + worldOffset, isLin, lastE1));
        }
        if (pts.Count < 2) return tp;

        var cut = kind == KrlImportKind.Print ? MoveKind.Extrude : MoveKind.Mill;
        var layer = new ToolpathLayer(0, pts[0].P.Z);
        for (int i = 1; i < pts.Count; i++)
        {
            layer.Moves.Add(new ToolpathMove(
                pts[i - 1].P, pts[i].P,
                pts[i].IsLin ? cut : MoveKind.Travel)
            {
                E1Mm = pts[i].E1,
            });
            moveCount++;
        }
        if (layer.Moves.Count > 0) tp.Layers.Add(layer);
        return tp;
    }

    /// <summary>True when any imported move carries a programmed rail E1.</summary>
    public static bool HasProgrammedE1(Toolpath tp)
    {
        foreach (var layer in tp.Layers)
            foreach (var m in layer.Moves)
                if (!float.IsNaN(m.E1Mm))
                    return true;
        return false;
    }

    /// <summary>Per-move E1 for scrub/playback (holds last known value).</summary>
    public static float[] E1PerMove(Toolpath tp)
    {
        int n = 0;
        foreach (var layer in tp.Layers) n += layer.Moves.Count;
        var a = new float[n];
        int i = 0;
        float last = float.NaN;
        foreach (var layer in tp.Layers)
            foreach (var m in layer.Moves)
            {
                if (!float.IsNaN(m.E1Mm)) last = m.E1Mm;
                a[i++] = last;
            }
        return a;
    }

    private static bool TryField(string body, string name, out float val)
    {
        val = 0f;
        var m = Regex.Match(body, $@"\b{name}\s+(-?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
        return m.Success
            && float.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out val);
    }
}
