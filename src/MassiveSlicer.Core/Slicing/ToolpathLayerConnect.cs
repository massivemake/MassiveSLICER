using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing;

/// <summary>
/// Inserts the hop from the previous layer's last point to this layer's first
/// point. Planar already did this; Angled / Multi-Planar did not, so the
/// exporter printed the jump as a <c>C_VEL</c> bead.
/// </summary>
public static class ToolpathLayerConnect
{
    /// <summary>
    /// XY farther than <paramref name="beadWidthMm"/> → travel (<c>;layer change</c>).
    /// Smaller gap with any XYZ delta → stitch (keep printing).
    /// </summary>
    public static void Insert(ToolpathLayer layer, Vector3 prevEnd, float beadWidthMm)
    {
        if (layer.Moves.Count == 0) return;
        var start = FirstPathPoint(layer);
        float dx = prevEnd.X - start.X;
        float dy = prevEnd.Y - start.Y;
        float xy = MathF.Sqrt(dx * dx + dy * dy);
        float threshold = MathF.Max(beadWidthMm, 0.1f);

        if (xy > threshold)
        {
            layer.Moves.Insert(0, new ToolpathMove(prevEnd, start, MoveKind.Travel)
            {
                IsLayerChange = true,
            });
        }
        else if (xy > 0.01f || MathF.Abs(prevEnd.Z - start.Z) > 0.01f)
        {
            layer.Moves.Insert(0, new ToolpathMove(prevEnd, start, MoveKind.Extrude)
            {
                IsLayerStitch = true,
            });
        }
    }

    static Vector3 FirstPathPoint(ToolpathLayer layer)
    {
        foreach (var m in layer.Moves)
        {
            if (m.IsLayerChange || m.IsZHop) continue;
            return m.From;
        }
        return layer.Moves[0].From;
    }
}
