using System.Numerics;

namespace MassiveSlicer.Core.Kinematics;

/// <summary>
/// Position offset of a toolhead key, in millimetres. Each key keeps its own offset.
/// A move that lands on a key uses that key only, so editing one key does not move
/// the others. Elsewhere the offsets blend inside their windows and fade out at the edge.
/// </summary>
public static class TcpKeyframeOffset
{
    public readonly record struct Sample(int Index, Vector3 Offset, int InfluenceLeft, int InfluenceRight);

    public static Vector3 At(double moveIndex, IReadOnlyList<Sample> keys)
    {
        if (keys is null || keys.Count == 0) return Vector3.Zero;

        for (int i = 0; i < keys.Count; i++)
        {
            if (Math.Abs(moveIndex - keys[i].Index) < 0.5)
                return keys[i].Offset;
        }

        double wsum = 0;
        var acc = Vector3.Zero;
        for (int i = 0; i < keys.Count; i++)
        {
            var k = keys[i];
            double d = moveIndex - k.Index;
            double span = d < 0 ? Math.Max(k.InfluenceLeft, 1) : Math.Max(k.InfluenceRight, 1);
            double t = Math.Abs(d) / span;
            if (t >= 1.0) continue;
            double x = 1.0 - t;
            double wgt = x * x * (3.0 - 2.0 * x);
            acc += k.Offset * (float)wgt;
            wsum += wgt;
        }
        if (wsum <= 0) return Vector3.Zero;
        return acc / (float)wsum * (float)Math.Min(wsum, 1.0);
    }
}
