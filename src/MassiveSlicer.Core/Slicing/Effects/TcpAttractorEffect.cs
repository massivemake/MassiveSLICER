using System.Numerics;
using MassiveSlicer.Core.Kinematics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing.Effects;

/// <summary>
/// Blends each move's approach normal toward the TCP attractors that cover it.
/// Weight is a smoothstep of distance: 1 on the stick, 0 at the radius. Attractors
/// add. Opposite pulls cancel, and agreeing pulls stack up to one full lean.
/// <see cref="Apply"/> leaves
/// mill moves and layer stitches alone. <see cref="ApplyMill"/> tilts a mill path
/// the same way: the bit stays on the downward side of the amber bar, so a 36°
/// lean is 36° off world-down. A deflector does not lock that bar. Inside its
/// volume it turns the chosen frame back toward the way out. A move outside every
/// volume keeps the normal it had.
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

    /// <summary>How far behind the cutter the flange is sampled, so a table volume can catch the wrist.</summary>
    public const float FlangeSampleBackMm = 400f;

    public static float Influence(Vector3 point, Vector3 center, float radiusMm)
    {
        float radius = MathF.Max(radiusMm, 1f);
        float dist = Vector3.Distance(point, center);
        if (dist >= radius) return 0f;
        float t = 1f - dist / radius;
        return t * t * (3f - 2f * t);
    }

    /// <summary>
    /// Radial falloff around <paramref name="axis"/> through <paramref name="center"/>.
    /// Half-height equals the radius, so this cylinder contains the equal-radius sphere.
    /// </summary>
    public static float CylinderInfluence(Vector3 point, Vector3 center, Vector3 axis, float radiusMm)
        => CylinderInfluence(point, center, axis, radiusMm, radiusMm);

    /// <summary>
    /// Same radial falloff, with an independent half-height along <paramref name="axis"/>.
    /// </summary>
    public static float CylinderInfluence(
        Vector3 point, Vector3 center, Vector3 axis, float radiusMm, float halfHeightMm)
    {
        float radius = MathF.Max(radiusMm, 1f);
        float half = MathF.Max(halfHeightMm, 1f);
        var ax = axis.LengthSquared() < 1e-8f ? Vector3.UnitZ : Vector3.Normalize(axis);
        var rel = point - center;
        float axial = Vector3.Dot(rel, ax);
        if (MathF.Abs(axial) > half) return 0f;
        float dist = (rel - ax * axial).Length();
        if (dist >= radius) return 0f;
        float t = 1f - dist / radius;
        return t * t * (3f - 2f * t);
    }

    /// <summary>
    /// Half-length of a cylinder along its axis. A cylinder with
    /// <see cref="TcpAttractor.HeightMm"/> set uses half of that. Zero keeps
    /// half-height equal to the radius, for attractors and deflectors.
    /// </summary>
    public static float CylinderHalfHeight(TcpAttractor attractor)
    {
        float radius = MathF.Max(attractor.RadiusMm, 1f);
        if (attractor.HeightMm > 1f)
            return MathF.Max(attractor.HeightMm * 0.5f, 1f);
        return radius;
    }

    public static float Influence(Vector3 point, TcpAttractor attractor)
        => attractor.Shape == TcpAdjustShape.Cylinder
            ? CylinderInfluence(
                point, attractor.Position, attractor.Axis, attractor.RadiusMm, CylinderHalfHeight(attractor))
            : Influence(point, attractor.Position, attractor.RadiusMm);

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
    /// of the stored surface normal. On the cylinder that cutter lies along the
    /// amber bar and points down it; farther out it keeps the surface aim.
    /// Print <see cref="Apply"/> still leaves mill moves alone.
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

                var baseNormal = move.Normal.LengthSquared() > 1e-8f ? move.Normal : layer.PlaneNormal;
                var travel = move.To - move.From;
                var pulls = new List<(TcpAttractor Attractor, float Weight, Vector3 Sample)>();
                float bestDeflect = 0f;
                TcpAttractor deflector = default;
                Vector3 deflectSample = default;
                bool haveDeflector = false;
                foreach (var a in live)
                {
                    var onPath = ClosestOnSegment(move.From, move.To, a.Position);
                    var sample = SamplePoint(onPath, baseNormal, a);
                    float w = Influence(sample, a);
                    if (w <= 1e-4f) continue;
                    if (a.Deflect)
                    {
                        // A deflector pushes out of its own volume. The strongest
                        // push wins. It is applied after the attractors add.
                        if (w <= bestDeflect) continue;
                        bestDeflect = w;
                        deflector = a;
                        deflectSample = sample;
                        haveDeflector = true;
                        continue;
                    }
                    pulls.Add((a, w, sample));
                }

                if (pulls.Count == 0 && !haveDeflector)
                {
                    nl.Moves.Add(move);
                    continue;
                }

                var aimed = pulls.Count == 0
                    ? new AimedPose(baseNormal, move.TcpYawDeg)
                    : CombineAttractors(baseNormal, move.TcpYawDeg, includeMill, pulls, travel);
                if (haveDeflector)
                    aimed = ResolveAim(aimed.Normal, aimed.YawDeg, deflector, includeMill, bestDeflect, deflectSample, travel);
                nl.Moves.Add(move with
                {
                    Normal = aimed.Normal,
                    TcpYawDeg = aimed.YawDeg,
                });
            }
            dst.Layers.Add(nl);
        }

        return dst;
    }

    /// <summary>Flange handles sample behind the tip, along the current body axis.</summary>
    public static Vector3 SamplePoint(Vector3 pathPoint, Vector3 normal, TcpAttractor attractor)
    {
        if (attractor.Frame != TcpAdjustFrame.Flange) return pathPoint;
        var body = normal.LengthSquared() > 1e-8f ? Vector3.Normalize(normal) : Vector3.UnitZ;
        return pathPoint + body * FlangeSampleBackMm;
    }

    /// <summary>
    /// Direction that moves <paramref name="point"/> out of the volume the fastest.
    /// Zero at a sphere center, or on a cylinder axis at mid-height, where every
    /// way out is the same length.
    /// </summary>
    public static Vector3 VolumeOutward(Vector3 point, TcpAttractor volume)
    {
        float radius = MathF.Max(volume.RadiusMm, 1f);
        var rel = point - volume.Position;
        if (volume.Shape == TcpAdjustShape.Cylinder)
        {
            var ax = volume.Axis.LengthSquared() < 1e-8f ? Vector3.UnitZ : Vector3.Normalize(volume.Axis);
            float axial = Vector3.Dot(rel, ax);
            var radial = rel - ax * axial;
            float radLen = radial.Length();
            float wallRoom = radius - radLen;
            float capRoom = CylinderHalfHeight(volume) - MathF.Abs(axial);
            if (radLen > 1e-3f && wallRoom <= capRoom + 1e-3f)
                return Vector3.Normalize(radial);
            if (MathF.Abs(axial) > 1e-3f)
                return MathF.Sign(axial) * ax;
            return Vector3.Zero;
        }

        if (rel.LengthSquared() < 1e-6f) return Vector3.Zero;
        return Vector3.Normalize(rel);
    }

    /// <summary>
    /// One attractor keeps <see cref="ResolveAim"/>. Several that aim the same
    /// TCP direction add their arrows: weight times the direction the arrow
    /// points. Opposite arrows cancel. The length of the sum, capped at 1, is
    /// how far the tool turns toward whatever is left.
    /// </summary>
    static AimedPose CombineAttractors(
        Vector3 baseNormal, float yawDeg, bool mill,
        List<(TcpAttractor Attractor, float Weight, Vector3 Sample)> pulls,
        Vector3 travel)
    {
        if (pulls.Count == 1)
        {
            var only = pulls[0];
            return ResolveAim(baseNormal, yawDeg, only.Attractor, mill, only.Weight, only.Sample, travel);
        }

        var normal = baseNormal;
        var yaw = yawDeg;
        var pending = new List<(TcpAttractor Attractor, float Weight, Vector3 Sample)>(pulls);
        while (pending.Count > 0)
        {
            var seed = pending[0];
            Vector3 sum = Vector3.Zero;
            Vector3 sample = seed.Sample;
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                var pull = pending[i];
                if (pull.Attractor.Frame != seed.Attractor.Frame) continue;
                if (Vector3.Dot(pull.Attractor.AimUnit, seed.Attractor.AimUnit) < 0.999f) continue;
                sum += InfluenceDirection(pull.Attractor) * pull.Weight;
                sample = pull.Sample;
                pending.RemoveAt(i);
            }

            float magnitude = sum.Length();
            if (magnitude < 1e-5f) continue;
            var dir = sum / magnitude;
            bool flange = seed.Attractor.Frame == TcpAdjustFrame.Flange;
            var axis = flange ? dir : -dir;
            var combined = new TcpAttractor(
                Vector3.Zero, 1f, axis, Frame: seed.Attractor.Frame, AimDir: seed.Attractor.AimDir);
            var step = ResolveAim(normal, yaw, combined, mill, MathF.Min(1f, magnitude), sample, travel);
            normal = step.Normal;
            yaw = step.YawDeg;
        }

        var kept = normal.LengthSquared() > 1e-8f ? Vector3.Normalize(normal) : Vector3.UnitZ;
        return new AimedPose(kept, yaw);
    }

    readonly record struct AimedPose(Vector3 Normal, float YawDeg);

    /// <summary>
    /// Stored normal and spin after one handle. +Z keeps the historical lean and the
    /// move's yaw. Any other TCP direction is swung onto the stick, holding the
    /// twist around that direction as still as the turn allows. +X and +Y keep
    /// the older axis construction.
    /// </summary>
    static AimedPose ResolveAim(
        Vector3 baseNormal, float yawDeg, TcpAttractor attractor, bool mill,
        float weight, Vector3 sample, Vector3 travel)
    {
        // A detractor is the same lean with the stick reversed, so the TCP
        // direction points away from the bar. The volume itself stays put.
        if (!attractor.Deflect && attractor.Detract)
        {
            var bar = attractor.Axis.LengthSquared() < 1e-8f ? Vector3.UnitZ : attractor.Axis;
            attractor = attractor with { Axis = -bar };
        }

        var aim = attractor.AimUnit;
        if (attractor.Deflect || aim.Z > 0.999f)
            return new AimedPose(
                AimedNormal(baseNormal, attractor, mill, weight, sample, travel), yawDeg);
        if (aim.X > 0.999f || aim.Y > 0.999f)
            return SteerSideAxis(baseNormal, yawDeg, attractor, mill, weight, wantX: aim.X > 0.999f);
        return SteerUnit(baseNormal, yawDeg, attractor, mill, weight, aim);
    }

    /// <summary>
    /// Tool +X/+Y point down the stick. Flange +X/+Y point along the stick.
    /// On this spindle flange +X is the cutter, so flange X matches tool Z.
    /// </summary>
    static AimedPose SteerSideAxis(
        Vector3 baseNormal, float yawDeg, TcpAttractor attractor, bool mill, float weight, bool wantX)
    {
        var from = baseNormal.LengthSquared() > 1e-8f ? Vector3.Normalize(baseNormal) : Vector3.UnitZ;
        var bar = attractor.Axis.LengthSquared() > 1e-8f ? Vector3.Normalize(attractor.Axis) : Vector3.UnitZ;
        bool flange = attractor.Frame == TcpAdjustFrame.Flange;

        if (!mill)
            return SteerPrint(from, yawDeg, bar, weight, wantX);

        var (x, y, z) = KukaOrientation.MillToolAxes(from, yawDeg);
        if (flange && wantX)
        {
            // Flange +X = −tool Z. Pointing it along the stick swings the cutter.
            return new AimedPose(BlendNormal(-z, bar, weight), yawDeg);
        }

        bool lockToolX = wantX || flange;
        Vector3 current = flange ? -x : (wantX ? x : y);
        Vector3 desired = flange ? bar : -bar;
        var steered = BlendNormal(current, desired, weight);
        Vector3 toolX;
        Vector3 toolZ;
        if (lockToolX)
        {
            toolX = flange ? -steered : steered;
            toolZ = Orthonormal(z, toolX);
            var toolY = Vector3.Normalize(Vector3.Cross(toolZ, toolX));
            toolZ = Vector3.Cross(toolX, toolY);
        }
        else
        {
            toolZ = Orthonormal(z, steered);
            toolX = Vector3.Normalize(Vector3.Cross(steered, toolZ));
            toolZ = Vector3.Cross(toolX, steered);
        }

        var stored = -toolZ;
        var (x0, y0, _) = KukaOrientation.MillToolAxes(stored, 0f);
        return new AimedPose(stored, YawAligning(x0, y0, toolX));
    }

    static AimedPose SteerPrint(Vector3 from, float yawDeg, Vector3 bar, float weight, bool wantX)
    {
        var (px, py, _) = KukaOrientation.PrintToolAxes(from, yawDeg);
        if (wantX)
            return new AimedPose(-BlendNormal(px, -bar, weight), yawDeg);

        var steeredY = BlendNormal(py, -bar, weight);
        var keepX = Orthonormal(px, steeredY);
        var printY = Vector3.Cross(Vector3.Cross(keepX, steeredY), keepX);
        if (printY.LengthSquared() < 1e-8f) printY = Perpendicular(keepX);
        else printY = Vector3.Normalize(printY);
        var normal = -keepX;
        var zero = KukaOrientation.PrintToolAxes(normal, 0f);
        return new AimedPose(normal, YawAligning(zero.Y, zero.Z, printY));
    }

    /// <summary>
    /// Swing the TCP so <paramref name="aim"/> (unit, in that frame) lines up with
    /// the stick. Tool directions point down the stick. Flange directions point
    /// along it. The swing is the shortest turn that carries the current direction.
    /// </summary>
    static AimedPose SteerUnit(
        Vector3 baseNormal, float yawDeg, TcpAttractor attractor, bool mill, float weight, Vector3 aim)
    {
        var from = baseNormal.LengthSquared() > 1e-8f ? Vector3.Normalize(baseNormal) : Vector3.UnitZ;
        var bar = attractor.Axis.LengthSquared() > 1e-8f ? Vector3.Normalize(attractor.Axis) : Vector3.UnitZ;
        bool flange = attractor.Frame == TcpAdjustFrame.Flange;
        if (!mill)
            return SteerPrintUnit(from, yawDeg, bar, weight, aim);

        var (x, y, z) = KukaOrientation.MillToolAxes(from, yawDeg);
        var current = flange
            ? -aim.Y * x + aim.Z * y - aim.X * z
            : aim.X * x + aim.Y * y + aim.Z * z;
        if (current.LengthSquared() < 1e-8f) current = flange ? -z : z;
        else current = Vector3.Normalize(current);
        var steered = BlendNormal(current, flange ? bar : -bar, weight);
        Swing(ref x, ref y, ref z, current, steered);
        var stored = -z;
        var (x0, y0, _) = KukaOrientation.MillToolAxes(stored, 0f);
        return new AimedPose(stored, YawAligning(x0, y0, x));
    }

    static AimedPose SteerPrintUnit(Vector3 from, float yawDeg, Vector3 bar, float weight, Vector3 aim)
    {
        var (x, y, z) = KukaOrientation.PrintToolAxes(from, yawDeg);
        var current = aim.X * x + aim.Y * y + aim.Z * z;
        if (current.LengthSquared() < 1e-8f) current = x;
        else current = Vector3.Normalize(current);
        var steered = BlendNormal(current, -bar, weight);
        Swing(ref x, ref y, ref z, current, steered);
        var normal = -x;
        var zero = KukaOrientation.PrintToolAxes(normal, 0f);
        return new AimedPose(normal, YawAligning(zero.Y, zero.Z, y));
    }

    /// <summary>Shortest rotation that carries <paramref name="fromDir"/> onto <paramref name="toDir"/>, applied to the frame.</summary>
    static void Swing(ref Vector3 x, ref Vector3 y, ref Vector3 z, Vector3 fromDir, Vector3 toDir)
    {
        var a = Vector3.Normalize(fromDir);
        var b = Vector3.Normalize(toDir);
        float d = Math.Clamp(Vector3.Dot(a, b), -1f, 1f);
        if (d > 0.9995f) return;
        Vector3 axis;
        float ang;
        if (d < -0.9995f)
        {
            axis = Perpendicular(a);
            ang = MathF.PI;
        }
        else
        {
            axis = Vector3.Normalize(Vector3.Cross(a, b));
            ang = MathF.Acos(d);
        }

        float c = MathF.Cos(ang), s = MathF.Sin(ang);
        x = Rotate(x, axis, s, c);
        y = Rotate(y, axis, s, c);
        z = Rotate(z, axis, s, c);
    }

    static Vector3 Rotate(Vector3 v, Vector3 axis, float s, float c)
        => v * c + Vector3.Cross(axis, v) * s + axis * Vector3.Dot(axis, v) * (1f - c);

    /// <summary>Component of <paramref name="keep"/> perpendicular to <paramref name="locked"/>, or a fallback.</summary>
    static Vector3 Orthonormal(Vector3 keep, Vector3 locked)
    {
        var side = keep - locked * Vector3.Dot(keep, locked);
        return side.LengthSquared() < 1e-8f ? Perpendicular(locked) : Vector3.Normalize(side);
    }

    static Vector3 Perpendicular(Vector3 axis)
    {
        var hint = MathF.Abs(axis.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX;
        return Vector3.Normalize(Vector3.Cross(axis, hint));
    }

    /// <summary><c>atan2</c> of the spin that takes <paramref name="from"/> toward <paramref name="to"/> in the plane of <paramref name="from"/> × <paramref name="side"/>.</summary>
    static float YawAligning(Vector3 from, Vector3 side, Vector3 to)
        => MathF.Atan2(Vector3.Dot(to, side), Vector3.Dot(to, from)) * R2D;

    /// <summary>
    /// World direction the TCP arrow lines up with once this handle has full
    /// weight. Tool directions point down the bar. Flange directions point
    /// along it. A detractor reverses the bar first. A deflector has no aim.
    /// </summary>
    public static Vector3 InfluenceDirection(TcpAttractor attractor)
    {
        if (attractor.Deflect)
            return Vector3.Zero;
        var axis = attractor.Axis.LengthSquared() > 1e-8f
            ? Vector3.Normalize(attractor.Axis)
            : Vector3.UnitZ;
        if (attractor.Detract)
            axis = -axis;
        return attractor.Frame == TcpAdjustFrame.Flange ? axis : -axis;
    }

    /// <summary>
    /// Stored path normal after one handle. An attractor still leans onto its bar.
    /// A deflector does not: inside the volume it turns the chosen frame back
    /// toward the way out, and it leaves an aim that is already heading out.
    /// </summary>
    public static Vector3 AimedNormal(
        Vector3 baseNormal, TcpAttractor attractor, bool mill, float weight, Vector3 sample, Vector3 travel)
    {
        var from = baseNormal.LengthSquared() > 1e-8f ? Vector3.Normalize(baseNormal) : Vector3.UnitZ;
        if (attractor.Deflect)
            return DeflectedNormal(from, attractor, mill, weight, sample, travel);

        var axis = attractor.Axis.LengthSquared() > 1e-8f
            ? Vector3.Normalize(attractor.Axis)
            : Vector3.UnitZ;

        if (attractor.Frame == TcpAdjustFrame.Flange)
            return BlendNormal(from, FlangeStoredNormal(axis, mill), weight);

        if (mill)
        {
            var cutter = -from;
            return -BlendNormal(cutter, -axis, weight);
        }

        return BlendNormal(from, axis, weight);
    }

    /// <summary>
    /// Turn <paramref name="from"/> only as far as it is aimed into the volume,
    /// or as far as the move travels deeper in. Weight is the volume influence.
    /// </summary>
    static Vector3 DeflectedNormal(
        Vector3 from, TcpAttractor attractor, bool mill, float weight, Vector3 sample, Vector3 travel)
    {
        var outward = VolumeOutward(sample, attractor);
        if (outward.LengthSquared() < 1e-8f || weight <= 1e-4f) return from;

        // Mill tool: the cutter is the opposite of the stored normal. Flange and
        // print store the body direction itself, and the flange sits along it.
        bool steerCutter = mill && attractor.Frame != TcpAdjustFrame.Flange;
        var current = steerCutter ? -from : from;
        if (current.LengthSquared() < 1e-8f) current = Vector3.UnitZ;
        else current = Vector3.Normalize(current);

        float aligned = Math.Clamp(Vector3.Dot(current, outward), -1f, 1f);
        float aimIn = Math.Clamp(0.5f * (1f - aligned), 0f, 1f);
        float approach = 0f;
        if (travel.LengthSquared() > 1e-6f)
            approach = Math.Clamp(-Vector3.Dot(Vector3.Normalize(travel), outward), 0f, 1f);
        float push = Math.Clamp(weight, 0f, 1f) * MathF.Max(aimIn, approach);
        if (push <= 1e-4f) return from;

        var steered = BlendNormal(current, outward, push);
        return steerCutter ? -steered : steered;
    }

    /// <summary>
    /// Mill stores the outward normal, opposite the cutter. The cutter is flange −X,
    /// so that stored normal is flange +X while flange +Z lies on <paramref name="bar"/>.
    /// Print stores the body axis, which follows the bar directly.
    /// </summary>
    public static Vector3 FlangeStoredNormal(Vector3 bar, bool mill)
    {
        var z = bar.LengthSquared() > 1e-8f ? Vector3.Normalize(bar) : Vector3.UnitZ;
        if (!mill) return z;
        var x = Vector3.Cross(Vector3.UnitZ, z);
        if (x.LengthSquared() < 1e-6f) x = Vector3.UnitX;
        else x = Vector3.Normalize(x);
        return x;
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
