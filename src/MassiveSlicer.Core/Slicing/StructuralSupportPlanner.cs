using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing;

/// <summary>
/// Applies <see cref="StructuralSupportSpec"/> modifiers to a sliced toolpath:
/// on every affected layer, the wall path is split at the point nearest the spec's
/// ANCHOR, and a detour is spliced in — neck out to the helper shape, a full wrap
/// of the outline, neck back — as continuous extrusion. The anchor and outline are
/// identical on every layer, so the neck and pocket stack vertically.
/// </summary>
public static class StructuralSupportPlanner
{
    public static void Apply(Toolpath toolpath, SliceSettings settings)
    {
        var specs = settings.StructuralSupports;
        if (specs.Count == 0 || toolpath.Layers.Count == 0) return;

        foreach (var spec in specs)
        {
            if (!spec.Enabled) continue;
            int lo = Math.Max(0, spec.AnchorLayer - Math.Max(0, spec.LayersDown));
            int hi = Math.Min(toolpath.Layers.Count - 1,
                spec.AnchorLayer + Math.Max(0, spec.LayersUp));
            var outline = spec.BuildOutline();
            if (outline.Length < 3) continue;

            for (int li = lo; li <= hi; li++)
                ApplyToLayer(toolpath.Layers[li], spec, outline);
        }
    }

    static void ApplyToLayer(ToolpathLayer layer, StructuralSupportSpec spec, Vector2[] outline)
    {
        var anchor = new Vector2(spec.AnchorX, spec.AnchorY);

        // 1) Find the extrude segment closest to the anchor (XY) and the split point on it.
        int bestMove = -1;
        float bestD2 = float.MaxValue;
        Vector3 bestP = default;
        float bestT = 0f;
        for (int i = 0; i < layer.Moves.Count; i++)
        {
            var mv = layer.Moves[i];
            if (mv.Kind != MoveKind.Extrude || mv.IsWipe || mv.IsResumeRamp) continue;
            var (p, t, d2) = ClosestOnSegmentXY(anchor, mv.From, mv.To);
            if (d2 < bestD2)
            {
                bestD2 = d2;
                bestMove = i;
                bestP = p;
                bestT = t;
            }
        }
        if (bestMove < 0) return;

        var wall = layer.Moves[bestMove];
        float z = bestP.Z;

        // 2) Outline entry: vertex nearest the split point (keeps the neck short).
        int entryIdx = 0;
        float entryD2 = float.MaxValue;
        for (int i = 0; i < outline.Length; i++)
        {
            float dx = outline[i].X - bestP.X, dy = outline[i].Y - bestP.Y;
            float d2 = dx * dx + dy * dy;
            if (d2 < entryD2) { entryD2 = d2; entryIdx = i; }
        }

        // 3) Build the detour: P → entry → full CCW wrap → entry → P.
        //    (Neck legs retrace the same centreline; at bead scale the two passes
        //    fuse into one double-wide neck — same as the hand-modeled version.)
        var detour = new List<ToolpathMove>(outline.Length + 3);
        Vector3 At(Vector2 v) => new(v.X, v.Y, z);
        var entry = At(outline[entryIdx]);

        var prev = bestP;
        void Emit(Vector3 to)
        {
            if (Vector3.DistanceSquared(prev, to) < 1e-6f) return;
            detour.Add(new ToolpathMove(prev, to, MoveKind.Extrude)
            {
                Normal = wall.Normal,
                HeightScale = wall.HeightScale,
            });
            prev = to;
        }

        Emit(entry);                                  // neck out
        for (int k = 1; k <= outline.Length; k++)     // wrap (back to entry)
            Emit(At(outline[(entryIdx + k) % outline.Length]));
        Emit(bestP);                                  // neck back

        if (detour.Count == 0) return;

        // 4) Splice: wall segment splits at P; detour goes between the halves.
        var replaced = new List<ToolpathMove>(detour.Count + 2);
        if (bestT > 1e-4f)
            replaced.Add(wall with { To = bestP });
        replaced.AddRange(detour);
        if (bestT < 1f - 1e-4f)
            replaced.Add(wall with { From = bestP });
        if (replaced.Count == 0) return;

        layer.Moves.RemoveAt(bestMove);
        layer.Moves.InsertRange(bestMove, replaced);

        // Recorded contour spans (if any) are index-based — they no longer match.
        layer.Contours.Clear();
    }

    static (Vector3 p, float t, float d2) ClosestOnSegmentXY(Vector2 q, Vector3 a, Vector3 b)
    {
        float abx = b.X - a.X, aby = b.Y - a.Y;
        float len2 = abx * abx + aby * aby;
        float t = len2 < 1e-12f
            ? 0f
            : Math.Clamp(((q.X - a.X) * abx + (q.Y - a.Y) * aby) / len2, 0f, 1f);
        var p = a + (b - a) * t;
        float dx = p.X - q.X, dy = p.Y - q.Y;
        return (p, t, dx * dx + dy * dy);
    }
}
