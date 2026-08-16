using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.IO;

/// <summary>
/// Parses KUKA KRL (.src) Cartesian motion — <c>LIN</c>/<c>PTP</c> with an inline frame
/// <c>{X .., Y .., Z .. [, A .., B .., C ..] [, E1 ..]}</c> — into a <see cref="Toolpath"/> for visualisation
/// and scrubbing. Positions are the literal KRL frame values plus <c>worldOffset</c>
/// (Print Bed 0,0,0 = <c>bed.origin</c>) so they land in scene/world space.
/// <c>E1</c> is kept on each move (last-known hold when a later frame omits it) so LFAM 1
/// rail simulation can replay the program. <c>LIN</c> → cut (<see cref="MoveKind.Mill"/>);
/// <c>PTP</c> → <see cref="MoveKind.Travel"/>.
/// Moves to named targets (e.g. <c>PTP apos</c>) and joint <c>AXIS</c> frames are skipped — only
/// inline Cartesian frames carry a toolpath.
/// </summary>
public static class KrlToolpathParser
{
    // LIN/PTP (not the _REL variants — the trailing \b excludes "LIN_REL") followed by a { ... } frame.
    private static readonly Regex MoveRe = new(
        @"\b(LIN|PTP)\b[^\{\r\n]*\{([^}]*)\}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Builds a toolpath from KRL text. <paramref name="moveCount"/> returns the number of segments
    /// produced (0 = no inline Cartesian moves found).
    /// </summary>
    public static Toolpath Parse(string krl, Vector3 worldOffset, out int moveCount)
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

        var layer = new ToolpathLayer(0, pts[0].P.Z);
        for (int i = 1; i < pts.Count; i++)
        {
            layer.Moves.Add(new ToolpathMove(
                pts[i - 1].P, pts[i].P,
                pts[i].IsLin ? MoveKind.Mill : MoveKind.Travel)
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
