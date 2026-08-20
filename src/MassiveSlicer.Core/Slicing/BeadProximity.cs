using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing;

/// <summary>
/// How close each bead runs to another bead ON THE SAME LAYER — the in-plane counterpart of
/// <see cref="BeadSupport"/>, which measures downward to the layer below.
///
/// <para><b>The problem.</b> Internal walls whose spacing is fixed by the model can sit closer
/// together than the bead is wide. Measured on a real part: arms at a 6 mm pitch printed with an
/// 8 mm bead, so the two runs overlap by 2 mm and that strip is deposited twice — 13.7 % more
/// material than the space holds, over 9.35 % of the print. Nothing upstream knows: the contours
/// are separate loops that merely happen to run alongside each other, and the thickness rules only
/// look up and down, never sideways. Worse, thinning a layer for overhang spreads the bead wider,
/// so the overhang correction makes this failure worse.</para>
///
/// <para><b>⚠️ The trap this class exists to avoid.</b> A naive "nearest other bead in this layer"
/// search finds every contour closing on its own SEAM and reports a gap of zero. Measured on a real
/// 392-layer part it flagged all 392 layers with gaps down to 0.000 mm — entirely artifact. A flow
/// correction built on that would cut flow at every seam on every layer. The filter is cyclic arc
/// distance: two segments far apart in path INDEX can be adjacent in path DISTANCE once the loop
/// wraps. With it applied the sub-5 mm population vanished to exactly 0.000 m and the real
/// population — the arms — stood out cleanly at 5.85-6.0 mm.</para>
/// </summary>
public static class BeadProximity
{
    /// <summary>
    /// Segments nearer than this along the path are the bead's own neighbours, not a parallel run.
    /// Expressed as a multiple of bead width; also applied cyclically so a closed loop's seam is
    /// excluded.
    /// </summary>
    public const float PathSkipBeads = 2.5f;

    // ⛔ There was a PathSkipMoves = 12 here — "ignore neighbours within 12 moves along the path".
    // It was a crude proxy for the arc-distance filter and it silently missed most of the target.
    // Measured on a real part: of four internal arms per layer, only ONE was corrected. The other
    // three are drawn as out-one-wall / U-turn / back-the-other, so their two walls sit just TWO
    // moves apart in the path and the index test discarded them — even though they are ~350 mm
    // apart ALONG the path, far past the arc skip. The one that worked did so only because the
    // path happened to wander 18 moves between its walls.
    //
    // Arc distance is the correct filter and already excludes a bead's own continuation: two
    // adjacent segments are half-their-lengths apart along the path, which for ordinary chords is
    // well inside the skip. Long segments that ARE far apart along the path are exactly what we
    // want to find, and perpendicular connectors are excluded by the direction test instead.

    /// <summary>
    /// |cos| between travel directions for two runs to count as running ALONGSIDE each other.
    /// Beads that merely cross — a junction, an infill line meeting a wall — are not crowding and
    /// genuinely need full flow.
    /// </summary>
    public const float ParallelDot = 0.5f;

    /// <summary>Per-move in-plane clearance. NaN = not measured (travel, or nothing alongside).</summary>
    public static float[] MeasureGaps(Toolpath toolpath, float beadWidthMm)
    {
        int total = 0;
        foreach (var layer in toolpath.Layers) total += layer.Moves.Count;
        var gaps = new float[total];
        Array.Fill(gaps, float.NaN);
        if (total == 0 || beadWidthMm <= 0f) return gaps;

        float cell    = MathF.Max(beadWidthMm, 0.5f);
        float arcSkip = PathSkipBeads * beadWidthMm;

        int flat = 0;
        foreach (var layer in toolpath.Layers)
        {
            var moves = layer.Moves;
            int n = moves.Count;

            // Arc length along the layer, so path-adjacency can be judged by DISTANCE as well as
            // by index — and cyclically, which is what excludes the seam.
            var arc = new float[n + 1];
            for (int i = 0; i < n; i++)
                arc[i + 1] = arc[i] + Vector3.Distance(moves[i].From, moves[i].To);
            float totalArc = arc[n];

            var grid = new Dictionary<(int, int), List<int>>();
            for (int i = 0; i < n; i++)
            {
                if (!ToolpathMoveKinds.IsCutSegment(moves[i].Kind)) continue;
                Insert(grid, moves[i].From, moves[i].To, cell, i);
            }

            for (int i = 0; i < n; i++, flat++)
            {
                var move = moves[i];
                if (!ToolpathMoveKinds.IsCutSegment(move.Kind)) continue;

                var d = move.To - move.From;
                float len = new Vector2(d.X, d.Y).Length();
                if (len < 1e-6f) continue;
                float ux = d.X / len, uy = d.Y / len;

                float mx = (move.From.X + move.To.X) * 0.5f;
                float my = (move.From.Y + move.To.Y) * 0.5f;

                int cx = (int)MathF.Floor(mx / cell), cy = (int)MathF.Floor(my / cell);
                float best = float.PositiveInfinity;

                for (int gx = cx - 1; gx <= cx + 1; gx++)
                for (int gy = cy - 1; gy <= cy + 1; gy++)
                {
                    if (!grid.TryGetValue((gx, gy), out var bucket)) continue;
                    foreach (int j in bucket)
                    {
                        if (j == i) continue;

                        // Cyclic: a closed contour's last segment is adjacent to its first.
                        float da = MathF.Abs(arc[j] - arc[i]);
                        da = MathF.Min(da, totalArc - da);
                        if (da <= arcSkip) continue;

                        var o  = moves[j];
                        var od = o.To - o.From;
                        float ol = new Vector2(od.X, od.Y).Length();
                        if (ol < 1e-6f) continue;
                        if (MathF.Abs(ux * od.X / ol + uy * od.Y / ol) < ParallelDot) continue;

                        float dist = SegmentDistance2D(mx, my, o.From, o.To);
                        if (dist < best) best = dist;
                    }
                }

                if (best < beadWidthMm) gaps[flat] = best;
            }
        }
        return gaps;
    }

    /// <summary>
    /// Flow factor for a bead whose nearest parallel neighbour is <paramref name="gapMm"/> away:
    /// <b>extrusion width equals line spacing</b>. The bead owns its pitch, so the factor is simply
    /// <c>gap / beadWidth</c>.
    ///
    /// <para><b>Why the pitch and not half a bead on the free side.</b> An earlier version gave a
    /// one-side-crowded bead <c>halfBead + gap/2</c> — 4 + 3 of 8 mm, so 0.875 at a 6 mm pitch. That
    /// smuggles in an assumption: that the bead's outer edge extends half a bead beyond its
    /// centreline, which is only true when that outer surface came from a contour inset by half a
    /// bead. For a feature made of exactly two passes it is false, and it silently widened the
    /// feature: two passes at 6 mm pitch came out 14 mm instead of the 12 mm two abutting 6 mm beads
    /// would have made. Jeff caught it — the arms need 12, not 16.</para>
    ///
    /// <para>Mirroring the gap fixes it and generalises: a group of N parallel passes at pitch p is
    /// N x p wide, each bead owns p, and the group's outer edges land where two abutting beads of
    /// width p would have put them. At a 6 mm pitch with an 8 mm bead that is 6/8 = <b>0.75</b>.</para>
    ///
    /// <para>A volume correction, so it is independent of the bead's cross-sectional SHAPE — the
    /// rounded spread appears identically in the single-bead and merged cases and cancels.</para>
    ///
    /// <para>⚠️ Uses the NEAREST gap. For a bead crowded on both sides at different pitches that is
    /// the tighter of the two, which under-feeds slightly rather than over-feeding — the safer
    /// direction, since over-extrusion is the failure being fixed.</para>
    /// </summary>
    public static float ScaleForGap(float gapMm, float beadWidthMm)
    {
        if (beadWidthMm <= 0f || float.IsNaN(gapMm) || gapMm >= beadWidthMm) return 1f;
        return Math.Clamp(MathF.Max(gapMm, 0f) / beadWidthMm, MinScale, 1f);
    }

    /// <summary>
    /// Floor on the correction. A pitch far below the bead width means the paths are wrong, not that
    /// flow should go to nothing — clamping keeps a pathological gap from commanding a dry extruder.
    /// </summary>
    public const float MinScale = 0.05f;

    private static void Insert(
        Dictionary<(int, int), List<int>> grid, Vector3 a, Vector3 b, float cell, int index)
    {
        int x0 = (int)MathF.Floor(MathF.Min(a.X, b.X) / cell), x1 = (int)MathF.Floor(MathF.Max(a.X, b.X) / cell);
        int y0 = (int)MathF.Floor(MathF.Min(a.Y, b.Y) / cell), y1 = (int)MathF.Floor(MathF.Max(a.Y, b.Y) / cell);
        for (int x = x0; x <= x1; x++)
        for (int y = y0; y <= y1; y++)
        {
            if (!grid.TryGetValue((x, y), out var list)) grid[(x, y)] = list = [];
            list.Add(index);
        }
    }

    /// <summary>Point-to-SEGMENT in XY. Never point-to-point — see <see cref="BeadSupport"/>.</summary>
    private static float SegmentDistance2D(float px, float py, Vector3 a, Vector3 b)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y;
        float l2 = dx * dx + dy * dy;
        if (l2 < 1e-10f) return MathF.Sqrt((px - a.X) * (px - a.X) + (py - a.Y) * (py - a.Y));
        float t  = Math.Clamp(((px - a.X) * dx + (py - a.Y) * dy) / l2, 0f, 1f);
        float ex = a.X + t * dx - px, ey = a.Y + t * dy - py;
        return MathF.Sqrt(ex * ex + ey * ey);
    }
}
