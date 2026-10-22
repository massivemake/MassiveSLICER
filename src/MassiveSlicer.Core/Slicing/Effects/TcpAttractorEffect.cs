using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing.Effects;

/// <summary>
/// Blends each move's approach normal toward the nearest TCP attractor.
/// Weight is a smoothstep of distance to the cylinder origin: 1 on the cylinder,
/// 0 at the sphere radius. The strongest attractor wins. <see cref="Apply"/> leaves
/// mill moves and layer stitches alone. <see cref="ApplyMill"/> tilts a mill path
/// the same way, so the cutter matches the amber cylinder. A move outside every
/// sphere keeps the normal it had.
/// </summary>
public static class TcpAttractorEffect
{
    const float D2R = MathF.PI / 180f;
    const float R2D = 180f / MathF.PI;

    /// <summary>
    /// Extruder body axis for lean about world X then world Y, in degrees.
    /// 0,0 is straight up (+Z). Lean stays inside ±80 so the axis does not flip.
    /// </summary>
    public static Vector3 AxisFromLean(float leanXDeg, float leanYDeg)
    {
        float x = Math.Clamp(leanXDeg, -80f, 80f) * D2R;
        float y = Math.Clamp(leanYDeg, -80f, 80f) * D2R;
        float cx = MathF.Cos(x), sx = MathF.Sin(x);
        float cy = MathF.Cos(y), sy = MathF.Sin(y);
        return Vector3.Normalize(new Vector3(sy * cx, -sx, cy * cx));
    }

    /// <summary>Lean angles that reproduce <paramref name="axis"/> via <see cref="AxisFromLean"/>.</summary>
    public static (float LeanXDeg, float LeanYDeg) LeanFromAxis(Vector3 axis)
    {
        if (axis.LengthSquared() < 1e-8f) return (0f, 0f);
        axis = Vector3.Normalize(axis);
        float leanX = MathF.Asin(Math.Clamp(-axis.Y, -1f, 1f)) * R2D;
        float leanY = MathF.Atan2(axis.X, axis.Z) * R2D;
        return (leanX, leanY);
    }

    public static float Influence(Vector3 point, Vector3 center, float radiusMm)
    {
        float radius = MathF.Max(radiusMm, 1f);
        float dist = Vector3.Distance(point, center);
        if (dist >= radius) return 0f;
        float t = 1f - dist / radius;
        return t * t * (3f - 2f * t);
    }

    public static Vector3 BlendNormal(Vector3 from, Vector3 toward, float weight)
    {
        var a = from.LengthSquared() < 1e-8f ? Vector3.UnitZ : Vector3.Normalize(from);
        var b = toward.LengthSquared() < 1e-8f ? Vector3.UnitZ : Vector3.Normalize(toward);
        float t = Math.Clamp(weight, 0f, 1f);
        if (t <= 1e-4f) return a;
        if (t >= 1f - 1e-4f) return b;

        float d = Math.Clamp(Vector3.Dot(a, b), -1f, 1f);
        if (d > 0.9995f)
            return Vector3.Normalize(Vector3.Lerp(a, b, t));
        if (d < -0.9995f)
        {
            var ortho = MathF.Abs(a.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX;
            var side = Vector3.Normalize(Vector3.Cross(a, ortho));
            float ang = MathF.PI * t;
            return Vector3.Normalize(a * MathF.Cos(ang) + side * MathF.Sin(ang));
        }

        float theta = MathF.Acos(d);
        float s = MathF.Sin(theta);
        return Vector3.Normalize(
            a * (MathF.Sin((1f - t) * theta) / s) +
            b * (MathF.Sin(t * theta) / s));
    }

    public static Toolpath Apply(Toolpath source, IReadOnlyList<TcpAttractor> attractors)
        => Blend(source, attractors, includeMill: false);

    /// <summary>
    /// TCP Adjust for a mill toolpath. The spindle cutter (T12 +Z) is the opposite
    /// of the stored surface normal. On the cylinder that cutter matches the amber
    /// axis; farther out it keeps the surface aim. Print <see cref="Apply"/> still
    /// leaves mill moves alone.
    /// </summary>
    public static Toolpath ApplyMill(Toolpath source, IReadOnlyList<TcpAttractor> attractors)
        => Blend(source, attractors, includeMill: true);

    static Toolpath Blend(Toolpath source, IReadOnlyList<TcpAttractor> attractors, bool includeMill)
    {
        if (attractors is null || attractors.Count == 0) return source;

        var live = new List<TcpAttractor>(attractors.Count);
        foreach (var a in attractors)
        {
            if (a.RadiusMm <= 1f || a.Axis.LengthSquared() < 1e-8f) continue;
            live.Add(a with { Axis = Vector3.Normalize(a.Axis) });
        }
        if (live.Count == 0) return source;

        var dst = new Toolpath { FormboundStats = source.FormboundStats };
        if (source.Warnings.Count > 0) dst.Warnings.AddRange(source.Warnings);

        foreach (var layer in source.Layers)
        {
            var nl = new ToolpathLayer(layer.Index, layer.Z)
            {
                Height = layer.Height,
                PlaneNormal = layer.PlaneNormal,
                ThermalTempC = layer.ThermalTempC,
            };
            nl.Contours.AddRange(layer.Contours);
            foreach (var move in layer.Moves)
            {
                if (move.IsLayerStitch || (!includeMill && move.Kind == MoveKind.Mill))
                {
                    nl.Moves.Add(move);
                    continue;
                }

                float bestW = 0f;
                Vector3 bestAxis = Vector3.UnitZ;
                foreach (var a in live)
                {
                    var sample = ClosestOnSegment(move.From, move.To, a.Position);
                    float w = Influence(sample, a.Position, a.RadiusMm);
                    if (w <= bestW) continue;
                    bestW = w;
                    bestAxis = a.Axis;
                }

                if (bestW <= 1e-4f)
                {
                    nl.Moves.Add(move);
                    continue;
                }

                var baseNormal = move.Normal.LengthSquared() > 1e-8f ? move.Normal : layer.PlaneNormal;
                Vector3 aimed;
                if (includeMill)
                {
                    // Cutter points opposite the stored normal. Pull that cutter
                    // toward the cylinder, then store the outward normal again.
                    var cutter = baseNormal.LengthSquared() > 1e-8f
                        ? -Vector3.Normalize(baseNormal)
                        : -Vector3.UnitZ;
                    aimed = -BlendNormal(cutter, bestAxis, bestW);
                }
                else
                    aimed = BlendNormal(baseNormal, bestAxis, bestW);
                nl.Moves.Add(move with { Normal = aimed });
            }
            dst.Layers.Add(nl);
        }

        return dst;
    }

    static Vector3 ClosestOnSegment(Vector3 a, Vector3 b, Vector3 point)
    {
        var ab = b - a;
        float len2 = ab.LengthSquared();
        if (len2 < 1e-8f) return a;
        float t = Math.Clamp(Vector3.Dot(point - a, ab) / len2, 0f, 1f);
        return a + ab * t;
    }
}
