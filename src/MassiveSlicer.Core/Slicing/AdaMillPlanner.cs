using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing;

/// <summary>
/// AdaOne subtractive planner. Routes each <see cref="MillOperationKind"/> to a real path:
/// planar facing/clearing (raster), multi-axis finishing (geodesic surface offsets), cutout (closed
/// polyline + axial stepdown), contouring (waterline), drilling (plunge/peck), swarf (side
/// contact), morph (blend top/bottom loops).
/// </summary>
public static class AdaMillPlanner
{
    public static Toolpath Generate(AdaMillRequest req)
    {
        var s = req.Settings;
        if (req.Positions.Count == 0 || req.Indices.Count < 3)
            return new Toolpath();

        return s.Operation switch
        {
            MillOperationKind.PlanarFacing       => Planar(req, lockAxis: true),
            MillOperationKind.PlanarClearing     => Projection(req),
            MillOperationKind.MultiAxisFinishing => Finishing(req),
            MillOperationKind.Cutout             => Cutout(req),
            MillOperationKind.Contouring         => Contouring(req),
            MillOperationKind.Drilling           => Drilling(req),
            MillOperationKind.Swarf              => Swarf(req),
            MillOperationKind.Morph              => Morph(req),
            _                                    => new Toolpath(),
        };
    }

    static Bounds BoundsOf(IReadOnlyList<Vector3> p)
    {
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        foreach (var v in p)
        {
            if (v.X < minX) minX = v.X; if (v.X > maxX) maxX = v.X;
            if (v.Y < minY) minY = v.Y; if (v.Y > maxY) maxY = v.Y;
            if (v.Z < minZ) minZ = v.Z; if (v.Z > maxZ) maxZ = v.Z;
        }
        return new Bounds(minX, minY, minZ, maxX, maxY, maxZ);
    }

    readonly record struct Bounds(float MinX, float MinY, float MinZ, float MaxX, float MaxY, float MaxZ)
    {
        public float Height => MaxZ - MinZ;
        public Vector3 Center => new((MinX + MaxX) * 0.5f, (MinY + MaxY) * 0.5f, (MinZ + MaxZ) * 0.5f);
    }

    static Toolpath Projection(AdaMillRequest req)
    {
        var s = req.Settings;
        var bounds = BoundsOf(req.Positions);
        var center = s.HasProjectionCenter
            ? s.ProjectionCenter
            : new Vector3(bounds.Center.X, bounds.Center.Y, bounds.MinZ);
        var guide = new ProjectionSetup
        {
            Shape = s.ProjectionShape,
            Center = center,
            BaseDiameterMm = s.ProjectionBaseDiameterMm,
            TopDiameterMm = s.ProjectionTopDiameterMm,
            HeightMm = s.ProjectionHeightMm,
            Inward = s.ProjectionInward,
            NormalInfluence = s.ProjectionNormalInfluence,
            SliceDirection = s.ProjectionSliceDirection,
            UseE1 = s.ProjectionUseE1 && s.ProjectionShape == ProjectionShape.Cylinder,
            RotaryCenter = s.HasProjectionRotary ? s.ProjectionRotaryCenter : center,
            RotarySign = MathF.Abs(s.ProjectionRotarySign) < 0.1f ? -1f : s.ProjectionRotarySign,
            AxisX = s.ProjectionAxisX.LengthSquared() > 1e-8f ? s.ProjectionAxisX : Vector3.UnitX,
            AxisZ = s.ProjectionAxisZ.LengthSquared() > 1e-8f ? s.ProjectionAxisZ : Vector3.UnitZ,
        };
        var tp = ProjectionMill.Generate(
            req.Positions, req.Normals, req.Indices, guide, s.ToMillSettings(), req.ReportProgress);
        ApplyEngagement(tp, s);
        return tp;
    }

    static Toolpath Planar(AdaMillRequest req, bool lockAxis)
    {
        var s = req.Settings;
        var approach = req.ApproachAxis is { } a && a.LengthSquared() > 1e-12f
            ? Vector3.Normalize(a)
            : (s.PlanarFacingNormal.LengthSquared() > 1e-12f
                ? Vector3.Normalize(s.PlanarFacingNormal)
                : Vector3.UnitZ);

        int cuts = Math.Max(1, s.AxialPassCount);
        var tp = new Toolpath();
        for (int i = 0; i < cuts; i++)
        {
            float extra = -i * MathF.Max(0.05f, s.StepdownMm);
            var millI = MillAtOffset(s, extra);
            var pass = SurfaceFollowMillGenerator.Generate(
                req.Positions, req.Normals, req.Indices, millI,
                approachAxis: approach, lockToolToApproach: lockAxis);
            Append(tp, pass, i);
        }
        ApplyEngagement(tp, s);
        return tp;
    }

    static Toolpath Finishing(AdaMillRequest req)
    {
        var s = req.Settings;
        var mill = s.ToMillSettings();
        var tp = GeodesicFinishMill.Generate(req.Positions, req.Normals, req.Indices, mill);
        if (s.StabilizeHeadRotation)
            SmoothMillNormals(tp);
        ApplyEngagement(tp, s);
        return tp;
    }

    static Toolpath Cutout(AdaMillRequest req)
    {
        var curves = IncludedCurves(req);
        if (curves.Count > 0)
            return CutoutCurves(req, curves);
        var s = req.Settings;
        var b = BoundsOf(req.Positions);
        float top = s.TopHeightMm > 0 ? s.TopHeightMm : b.MaxZ;
        float depth = MathF.Max(0.05f, s.CutoutCutDepthMm);
        float layer = MathF.Max(0.05f, s.CutoutLayerHeightMm);
        float bottom = s.BottomHeightMm != 0 ? s.BottomHeightMm : top - depth;
        if (s.CutoutMillingDirection == AdaMillingDirection.FromSurface)
            (top, bottom) = (bottom, top);

        var loop = MeshWaterline.OuterLoopOrBounds(req.Positions, req.Indices, top);
        loop = CurveOffset.Apply(loop, closed: true, flipped: false, s);
        var tp = new Toolpath();
        int n = Math.Max(1, (int)MathF.Ceiling(MathF.Abs(top - bottom) / layer));
        for (int i = 0; i < n; i++)
        {
            float t = n == 1 ? 0 : i / (float)(n - 1);
            float z = top + (bottom - top) * t;
            var pts = ShiftZ(loop, z);
            var layerTp = new ToolpathLayer(i, z) { PlaneNormal = Vector3.UnitZ, Height = layer };
            EmitLoop(layerTp, pts, s, closed: true);
            if (layerTp.Moves.Count > 0) tp.Layers.Add(layerTp);
        }
        ApplyEngagement(tp, s);
        return tp;
    }

    static Toolpath Contouring(AdaMillRequest req)
    {
        var curves = IncludedCurves(req);
        if (curves.Count > 0)
            return ContourCurves(req, curves);
        var s = req.Settings;
        var b = BoundsOf(req.Positions);
        float top = s.TopHeightMm > 0 ? s.TopHeightMm : b.MaxZ;
        float bottom = s.ContouringMaxDepthEnabled && s.MaxDepthMm > 0
            ? top - s.MaxDepthMm
            : (s.BottomHeightMm != 0 ? s.BottomHeightMm : b.MinZ);
        float step = MathF.Max(0.05f, s.StepdownMm);
        var tp = new Toolpath();
        int i = 0;
        Vector3? lastEnd = null;
        for (float z = top; z >= bottom - 1e-4f; z -= step, i++)
        {
            var loops = MeshWaterline.SliceClosedLoops(req.Positions, req.Indices, z);
            if (loops.Count == 0)
            {
                var fallback = MeshWaterline.OuterLoopOrBounds(req.Positions, req.Indices, z);
                if (fallback.Count >= 3) loops.Add(fallback);
            }
            var layer = new ToolpathLayer(i, z) { PlaneNormal = Vector3.UnitZ, Height = step };
            foreach (var loop in loops)
            {
                var pts = CurveOffset.Apply(ShiftZ(loop, z), closed: true, flipped: false, s);
                if (s.ContouringWaterfall && lastEnd is { } from && pts.Count > 0)
                    layer.Moves.Add(new ToolpathMove(from, pts[0], MoveKind.Mill) { Normal = Vector3.UnitZ });
                EmitLoop(layer, pts, s, closed: true, includeApproach: lastEnd is null || !s.ContouringWaterfall);
                if (pts.Count > 0) lastEnd = pts[0];
            }
            if (layer.Moves.Count > 0) tp.Layers.Add(layer);
        }
        ApplyEngagement(tp, s);
        return tp;
    }

    static Toolpath Drilling(AdaMillRequest req)
    {
        var curves = IncludedCurves(req);
        if (curves.Count > 0)
            return DrillAlongCurves(req, curves);
        var s = req.Settings;
        if (req.DrillPoints is { } chosen)
        {
            if (chosen.Count == 0) return new Toolpath();
            return DrillPoints(s, chosen);
        }

        var b = BoundsOf(req.Positions);
        float top = s.TopHeightMm > 0 ? s.TopHeightMm : b.MaxZ;
        float bottom = (s.BottomHeightMm != 0 ? s.BottomHeightMm : b.MinZ) - MathF.Max(0, s.DrillingBreakthroughMm);
        var holes = req.DrillHoles is { Count: > 0 } h
            ? h
            : (IReadOnlyList<Vector3>)[new Vector3(b.Center.X, b.Center.Y, top)];
        var points = new DrillPoint[holes.Count];
        for (int i = 0; i < holes.Count; i++)
        {
            var hole = holes[i];
            float depth = top - bottom;
            points[i] = new DrillPoint(
                new Vector3(hole.X, hole.Y, top),
                Vector3.UnitZ,
                depth,
                s.ToolDiameterMm);
        }
        return DrillPoints(s, points);
    }

    static Toolpath DrillPoints(AdaMachiningSettings s, IReadOnlyList<DrillPoint> points)
    {
        float retractH = MathF.Max(1f, s.RetractHeightMm);
        float feedH = MathF.Max(0f, s.FeedHeightMm);
        float tool = MathF.Max(0.1f, s.ToolDiameterMm);
        var layer = new ToolpathLayer(0, points.Count > 0 ? points[0].Position.Z : 0f) { PlaneNormal = Vector3.UnitZ };
        Vector3? last = null;
        foreach (var point in points)
        {
            switch (point.Cut)
            {
                case DrillCutKind.Counterbore:
                {
                    float shoulder = Math.Clamp(point.ShoulderDepthMm, 0.2f, MathF.Max(0.2f, point.DepthMm));
                    EmitRound(layer, s, point with { DepthMm = shoulder }, tool, retractH, feedH, ref last);
                    float minor = point.MinorDiameterMm > 0.5f ? point.MinorDiameterMm : point.DiameterMm;
                    EmitRound(layer, s, point with { DiameterMm = minor, Cut = DrillCutKind.Round }, tool, retractH, feedH, ref last);
                    break;
                }
                case DrillCutKind.Countersink:
                {
                    float minor = point.MinorDiameterMm > 0.05f ? point.MinorDiameterMm : point.DiameterMm;
                    EmitRound(layer, s, point with { DiameterMm = minor, Cut = DrillCutKind.Round }, tool, retractH, feedH, ref last);
                    EmitCone(layer, s, point, tool, retractH, feedH, ref last);
                    break;
                }
                case DrillCutKind.Profile:
                    EmitProfile(layer, s, point, tool, retractH, feedH, ref last);
                    break;
                default:
                    EmitRound(layer, s, point, tool, retractH, feedH, ref last);
                    break;
            }
        }
        var tp = new Toolpath();
        if (layer.Moves.Count > 0) tp.Layers.Add(layer);
        return tp;
    }

    static void EmitRound(
        ToolpathLayer layer, AdaMachiningSettings s, DrillPoint point,
        float tool, float retractH, float feedH, ref Vector3? last)
    {
        var axis = point.Axis.LengthSquared() > 1e-8f ? Vector3.Normalize(point.Axis) : Vector3.UnitZ;
        float depth = MathF.Max(0.05f, point.DepthMm);
        var mouth = point.Position;
        var tipCenter = mouth - axis * depth;
        var retract = mouth + axis * retractH;
        var feed = mouth + axis * feedH;
        float pathR = (point.DiameterMm - tool) * 0.5f;
        bool bore = pathR >= 0.2f;
        DrillMarkers.Basis(axis, out var u, out var v);
        if (!bore)
        {
            var startRetract = retract;
            var startFeed = feed;
            var startTip = tipCenter;
            if (last is { } prev)
                layer.Moves.Add(new ToolpathMove(prev, startRetract, MoveKind.Travel) { IsZHop = true });
            layer.Moves.Add(new ToolpathMove(startRetract, startFeed, MoveKind.Travel));
            if (s.DrillingPeck && s.DrillingPeckDepthMm > 0.05f)
            {
                var cursor = mouth;
                while (Vector3.Dot(cursor - startTip, axis) > 1e-3f)
                {
                    float remain = Vector3.Dot(cursor - startTip, axis);
                    float step = MathF.Min(remain, s.DrillingPeckDepthMm);
                    var next = cursor - axis * step;
                    layer.Moves.Add(new ToolpathMove(cursor, next, MoveKind.Mill) { Normal = axis });
                    layer.Moves.Add(new ToolpathMove(next, startFeed, MoveKind.Travel) { IsZHop = true });
                    cursor = next;
                    if (step <= 1e-4f) break;
                }
            }
            else
                layer.Moves.Add(new ToolpathMove(startFeed, startTip, MoveKind.Mill) { Normal = axis });
            layer.Moves.Add(new ToolpathMove(startTip, startRetract, MoveKind.Travel) { IsZHop = true });
            last = startRetract;
            return;
        }

        var radii = PocketRadii(pathR, 0f, RadialStep(s, tool));
        float pass = DepthPass(s, tool);
        float startR = radii[0];
        var startRetractB = retract + u * startR;
        var startFeedB = feed + u * startR;
        if (last is { } from)
            layer.Moves.Add(new ToolpathMove(from, startRetractB, MoveKind.Travel) { IsZHop = true });
        layer.Moves.Add(new ToolpathMove(startRetractB, startFeedB, MoveKind.Travel));

        var cursorB = startFeedB;
        Vector3 lastTip = mouth;
        float d = MathF.Min(pass, depth);
        while (true)
        {
            var tip = mouth - axis * d;
            var lead = tip + u * startR;
            layer.Moves.Add(new ToolpathMove(cursorB, lead, MoveKind.Mill) { Normal = axis });
            cursorB = lead;
            foreach (float r in radii)
                EmitCircle(layer, tip, axis, u, v, r, ref cursorB);
            lastTip = tip;
            if (d >= depth - 0.01f) break;
            d = MathF.Min(depth, d + pass);
        }

        var lift = mouth + axis * retractH + (cursorB - lastTip);
        layer.Moves.Add(new ToolpathMove(cursorB, lift, MoveKind.Travel) { IsZHop = true });
        last = lift;
    }

    static void EmitCone(
        ToolpathLayer layer, AdaMachiningSettings s, DrillPoint point,
        float tool, float retractH, float feedH, ref Vector3? last)
    {
        var axis = point.Axis.LengthSquared() > 1e-8f ? Vector3.Normalize(point.Axis) : Vector3.UnitZ;
        float shoulder = Math.Clamp(point.ShoulderDepthMm, 0.2f, MathF.Max(0.2f, point.DepthMm));
        float majorR = point.DiameterMm * 0.5f;
        float minorR = (point.MinorDiameterMm > 0.05f ? point.MinorDiameterMm : point.DiameterMm) * 0.5f;
        int steps = Math.Clamp((int)MathF.Round(shoulder / 0.8f), 4, 14);
        DrillMarkers.Basis(axis, out var u, out var v);
        var mouth = point.Position;
        var retract = mouth + axis * retractH;
        float radial = RadialStep(s, tool);
        // The pilot is already drilled. Each cone station clears the ring
        // between that pilot and the cone wall.
        float inner = MathF.Max(0f, minorR - tool * 0.5f);
        bool started = false;
        Vector3 end = retract;
        for (int i = 0; i < steps; i++)
        {
            float t = steps == 1 ? 1f : i / (float)(steps - 1);
            float wallR = majorR + (minorR - majorR) * t;
            float pathR = wallR - tool * 0.5f;
            if (pathR < inner + 0.15f) continue;
            float depth = shoulder * t;
            var center = mouth - axis * depth;
            var radii = PocketRadii(pathR, inner, radial);
            var on = center + u * radii[0];
            if (!started)
            {
                var hop = center + axis * MathF.Max(feedH, 0.5f) + u * radii[0];
                var from = last ?? (retract + u * radii[0]);
                layer.Moves.Add(new ToolpathMove(from, hop, MoveKind.Travel) { IsZHop = true });
                layer.Moves.Add(new ToolpathMove(hop, on, MoveKind.Mill) { Normal = axis });
                started = true;
            }
            else
                layer.Moves.Add(new ToolpathMove(end, on, MoveKind.Mill) { Normal = axis });
            end = on;
            foreach (float r in radii)
                EmitCircle(layer, center, axis, u, v, r, ref end);
        }
        if (!started) return;
        layer.Moves.Add(new ToolpathMove(end, end + axis * retractH, MoveKind.Travel) { IsZHop = true });
        last = end + axis * retractH;
    }

    /// <summary>
    /// Stepover wider than the cutter leaves a web. The radial step stays
    /// inside 80% of the tool so the passes overlap.
    /// </summary>
    static float RadialStep(AdaMachiningSettings s, float tool)
    {
        float cap = MathF.Max(0.2f, tool * 0.8f);
        float step = s.StepoverMm > 0.2f ? s.StepoverMm : cap;
        return Math.Clamp(step, 0.2f, cap);
    }

    static float DepthPass(AdaMachiningSettings s, float tool) =>
        s.StepdownMm > 0.2f ? s.StepdownMm : MathF.Max(0.5f, tool);

    /// <summary>Tool-center radii from the middle out to the wall.</summary>
    static List<float> PocketRadii(float outerR, float innerR, float radial)
    {
        var desc = new List<float>();
        if (outerR < 0.2f)
        {
            desc.Add(0f);
            return desc;
        }
        float step = MathF.Max(0.2f, radial);
        float r = outerR;
        for (int guard = 0; guard < 80; guard++)
        {
            desc.Add(r);
            if (r <= innerR + 0.05f) break;
            float next = r - step;
            if (next < innerR) next = innerR;
            if (next >= r - 0.04f) break;
            r = next;
        }
        desc.Reverse();
        return desc;
    }

    static void EmitCircle(
        ToolpathLayer layer, Vector3 center, Vector3 axis, Vector3 u, Vector3 v,
        float radius, ref Vector3 cursor)
    {
        if (radius < 0.05f)
        {
            if (Vector3.Distance(cursor, center) > 0.05f)
                layer.Moves.Add(new ToolpathMove(cursor, center, MoveKind.Mill) { Normal = axis });
            cursor = center;
            return;
        }
        const int n = 24;
        var start = center + u * radius;
        if (Vector3.Distance(cursor, start) > 0.05f)
            layer.Moves.Add(new ToolpathMove(cursor, start, MoveKind.Mill) { Normal = axis });
        var prev = start;
        for (int i = 1; i <= n; i++)
        {
            float a = MathF.Tau * i / n;
            var p = center + (u * MathF.Cos(a) + v * MathF.Sin(a)) * radius;
            layer.Moves.Add(new ToolpathMove(prev, p, MoveKind.Mill) { Normal = axis });
            prev = p;
        }
        cursor = prev;
    }

    static void EmitProfile(
        ToolpathLayer layer, AdaMachiningSettings s, DrillPoint point,
        float tool, float retractH, float feedH, ref Vector3? last)
    {
        var axis = point.Axis.LengthSquared() > 1e-8f ? Vector3.Normalize(point.Axis) : Vector3.UnitZ;
        var rim = point.Profile;
        var loops = rim is { Count: >= 3 } ? ProfileLoops(rim, tool) : [];
        if (loops.Count == 0)
        {
            if (rim is { Count: >= 2 } && TrySlotCenterline(rim, axis, tool, out var a, out var b))
            {
                EmitCenterline(layer, point, axis, a, b, retractH, feedH, ref last);
                return;
            }
            EmitRound(layer, s, point with { Cut = DrillCutKind.Round }, tool, retractH, feedH, ref last);
            return;
        }

        float depth = MathF.Max(0.05f, point.DepthMm);
        float pass = MathF.Max(0.5f, tool);
        var mouth = point.Position;
        var retract = mouth + axis * retractH;
        for (float d = MathF.Min(pass, depth); ; d = MathF.Min(depth, d))
        {
            for (int li = loops.Count - 1; li >= 0; li--)
            {
                var loop = loops[li];
                var start = OnDepth(loop[0], mouth, axis, d);
                if (last is { } prev && Vector3.Distance(prev, start + axis * feedH) > tool)
                    layer.Moves.Add(new ToolpathMove(prev, start + axis * MathF.Max(feedH, 0.4f), MoveKind.Travel) { IsZHop = true });
                var approach = start + axis * MathF.Max(feedH, 0.4f);
                if (last is not { } here || Vector3.Distance(here, approach) > 0.05f)
                    layer.Moves.Add(new ToolpathMove(last ?? retract, approach, MoveKind.Travel));
                layer.Moves.Add(new ToolpathMove(approach, start, MoveKind.Mill) { Normal = axis });
                var cursor = start;
                for (int i = 1; i < loop.Count; i++)
                {
                    var next = OnDepth(loop[i], mouth, axis, d);
                    layer.Moves.Add(new ToolpathMove(cursor, next, MoveKind.Mill) { Normal = axis });
                    cursor = next;
                }
                if (Vector3.Distance(cursor, start) > 0.05f)
                    layer.Moves.Add(new ToolpathMove(cursor, start, MoveKind.Mill) { Normal = axis });
                last = start;
            }
            if (d >= depth - 0.01f) break;
            d += pass;
        }
        if (last is { } tip)
        {
            layer.Moves.Add(new ToolpathMove(tip, tip + axis * retractH, MoveKind.Travel) { IsZHop = true });
            last = tip + axis * retractH;
        }
    }

    static List<List<Vector3>> ProfileLoops(IReadOnlyList<Vector3> rim, float tool)
    {
        var loops = new List<List<Vector3>>();
        float prev = float.MaxValue;
        for (int i = 0; i < 24; i++)
        {
            float extra = i * tool * 0.55f;
            var loop = CurveOffset.Shift(rim, true, false, CurveOffsetSide.Inside, extra, true, tool, 0f);
            if (loop.Count < 3) break;
            float size = LoopSize(loop);
            if (size > prev + 0.5f || size < tool * 0.2f) break;
            loops.Add(loop);
            prev = size;
            if (size < tool * 0.85f) break;
        }
        return loops;
    }

    static float LoopSize(List<Vector3> loop)
    {
        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
        foreach (var p in loop)
        {
            if (p.X < minX) minX = p.X;
            if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.Y > maxY) maxY = p.Y;
        }
        return MathF.Max(maxX - minX, maxY - minY);
    }

    static bool TrySlotCenterline(IReadOnlyList<Vector3> rim, Vector3 axis, float tool, out Vector3 a, out Vector3 b)
    {
        a = b = default;
        DrillMarkers.Basis(axis, out var u, out var v);
        float minU = float.MaxValue, maxU = float.MinValue, minV = float.MaxValue, maxV = float.MinValue;
        var atMinU = Vector3.Zero; var atMaxU = Vector3.Zero; var atMinV = Vector3.Zero; var atMaxV = Vector3.Zero;
        foreach (var p in rim)
        {
            float uu = Vector3.Dot(p, u);
            float vv = Vector3.Dot(p, v);
            if (uu < minU) { minU = uu; atMinU = p; }
            if (uu > maxU) { maxU = uu; atMaxU = p; }
            if (vv < minV) { minV = vv; atMinV = p; }
            if (vv > maxV) { maxV = vv; atMaxV = p; }
        }
        float du = maxU - minU;
        float dv = maxV - minV;
        if (MathF.Max(du, dv) < tool * 0.5f) return false;
        if (du >= dv)
        {
            var dir = Vector3.Normalize(atMaxU - atMinU);
            float pull = MathF.Min(tool * 0.5f, du * 0.45f);
            a = atMinU + dir * pull;
            b = atMaxU - dir * pull;
        }
        else
        {
            var dir = Vector3.Normalize(atMaxV - atMinV);
            float pull = MathF.Min(tool * 0.5f, dv * 0.45f);
            a = atMinV + dir * pull;
            b = atMaxV - dir * pull;
        }
        return Vector3.Distance(a, b) > 0.4f;
    }

    static void EmitCenterline(
        ToolpathLayer layer, DrillPoint point, Vector3 axis, Vector3 a, Vector3 b,
        float retractH, float feedH, ref Vector3? last)
    {
        float depth = MathF.Max(0.05f, point.DepthMm);
        var start = OnDepth(a, point.Position, axis, depth);
        var end = OnDepth(b, point.Position, axis, depth);
        var retract = point.Position + axis * retractH;
        var approach = start + axis * MathF.Max(feedH, 0.4f);
        layer.Moves.Add(new ToolpathMove(last ?? retract, approach, MoveKind.Travel) { IsZHop = true });
        layer.Moves.Add(new ToolpathMove(approach, start, MoveKind.Mill) { Normal = axis });
        layer.Moves.Add(new ToolpathMove(start, end, MoveKind.Mill) { Normal = axis });
        layer.Moves.Add(new ToolpathMove(end, end + axis * retractH, MoveKind.Travel) { IsZHop = true });
        last = end + axis * retractH;
    }

    static Vector3 OnDepth(Vector3 point, Vector3 mouth, Vector3 axis, float depth)
    {
        float along = Vector3.Dot(point - mouth, axis);
        return point - axis * along - axis * depth;
    }

    static Toolpath Swarf(AdaMillRequest req)
    {
        var s = req.Settings;
        var b = BoundsOf(req.Positions);
        float z = (b.MinZ + b.MaxZ) * 0.5f;
        var loop = MeshWaterline.OuterLoopOrBounds(req.Positions, req.Indices, z);
        loop = Compensate(loop, s);
        float lean = s.SwarfLeanDeg * (MathF.PI / 180f);
        float lead = s.SwarfLeadDeg * (MathF.PI / 180f);
        var layer = new ToolpathLayer(0, z) { PlaneNormal = Vector3.UnitZ };
        EmitLoop(layer, loop, s, closed: true, toolAxis: AxisFromLeadLean(loop, lean, lead));
        var tp = new Toolpath();
        if (layer.Moves.Count > 0) tp.Layers.Add(layer);
        ApplyEngagement(tp, s);
        return tp;
    }

    static Toolpath Morph(AdaMillRequest req)
    {
        var s = req.Settings;
        var b = BoundsOf(req.Positions);
        float top = s.TopHeightMm > 0 ? s.TopHeightMm : b.MaxZ;
        float bottom = s.BottomHeightMm != 0 ? s.BottomHeightMm : b.MinZ;
        int steps = Math.Max(2, s.MorphSteps);
        var topLoop = MeshWaterline.OuterLoopOrBounds(req.Positions, req.Indices, top);
        var botLoop = MeshWaterline.OuterLoopOrBounds(req.Positions, req.Indices, bottom);
        int n = Math.Max(topLoop.Count, botLoop.Count);
        n = Math.Max(n, 8);
        var a = MeshWaterline.ResampleClosed(topLoop, n);
        var c = MeshWaterline.ResampleClosed(botLoop, n);
        var tp = new Toolpath();
        for (int i = 0; i < steps; i++)
        {
            float t = i / (float)(steps - 1);
            var loop = new List<Vector3>(n);
            for (int k = 0; k < n; k++)
                loop.Add(Vector3.Lerp(a[k], c[k], t));
            loop = Compensate(loop, s);
            float z = top + (bottom - top) * t;
            var layer = new ToolpathLayer(i, z) { PlaneNormal = Vector3.UnitZ };
            EmitLoop(layer, loop, s, closed: true);
            if (layer.Moves.Count > 0) tp.Layers.Add(layer);
        }
        ApplyEngagement(tp, s);
        return tp;
    }

    static Vector3 AxisFromLeadLean(IReadOnlyList<Vector3> loop, float lean, float lead)
    {
        // Default: tool points into the wall (outward XY, slightly down).
        var axis = Vector3.Normalize(new Vector3(MathF.Sin(lean), 0, -MathF.Cos(lean)));
        if (MathF.Abs(lead) > 1e-4f)
        {
            axis = Vector3.Normalize(new Vector3(
                axis.X * MathF.Cos(lead) - axis.Y * MathF.Sin(lead),
                axis.X * MathF.Sin(lead) + axis.Y * MathF.Cos(lead),
                axis.Z));
        }
        _ = loop;
        return axis;
    }

    static MillSettings MillAtOffset(AdaMachiningSettings s, float extra)
    {
        var m = s.ToMillSettings();
        return new MillSettings
        {
            ToolDiameterMm    = m.ToolDiameterMm,
            ToolEnd           = m.ToolEnd,
            StepoverMm        = m.StepoverMm,
            StepdownMm        = m.StepdownMm,
            FinishAllowanceMm = m.FinishAllowanceMm,
            OffsetDistanceMm  = m.OffsetDistanceMm + extra,
            FeedRateMmMin     = m.FeedRateMmMin,
            PlungeFeedMmMin   = m.PlungeFeedMmMin,
            RapidZMm          = m.RapidZMm,
            SpindleRpm        = m.SpindleRpm,
            MaxDepthMm        = m.MaxDepthMm,
        };
    }

    static List<Vector3> ShiftZ(IReadOnlyList<Vector3> loop, float z)
    {
        var pts = new List<Vector3>(loop.Count);
        foreach (var p in loop)
            pts.Add(new Vector3(p.X, p.Y, z));
        return pts;
    }

    static List<Vector3> Compensate(IReadOnlyList<Vector3> loop, AdaMachiningSettings s)
    {
        float r = s.ToolDiameterMm * 0.5f;
        float d = s.ToolCompensation switch
        {
            AdaToolCompensation.Left  => r,
            AdaToolCompensation.Right => -r,
            _ => 0,
        };
        d += s.StockToLeaveMm;
        if (MathF.Abs(d) < 1e-4f) return loop as List<Vector3> ?? [.. loop];
        return OffsetXy(loop, d);
    }

    static List<Vector3> OffsetXy(IReadOnlyList<Vector3> loop, float d)
    {
        int n = loop.Count;
        if (n < 3) return [.. loop];
        var outPts = new List<Vector3>(n);
        for (int i = 0; i < n; i++)
        {
            var prev = loop[(i - 1 + n) % n];
            var cur = loop[i];
            var next = loop[(i + 1) % n];
            var t1 = Vector3.Normalize(new Vector3(cur.X - prev.X, cur.Y - prev.Y, 0));
            var t2 = Vector3.Normalize(new Vector3(next.X - cur.X, next.Y - cur.Y, 0));
            if (t1.LengthSquared() < 1e-12f) t1 = t2;
            if (t2.LengthSquared() < 1e-12f) t2 = t1;
            var n1 = new Vector3(-t1.Y, t1.X, 0);
            var n2 = new Vector3(-t2.Y, t2.X, 0);
            var nn = n1 + n2;
            if (nn.LengthSquared() < 1e-12f) nn = n1;
            nn = Vector3.Normalize(nn);
            outPts.Add(cur + nn * d);
        }
        return outPts;
    }

    static List<MillCurve> IncludedCurves(AdaMillRequest req)
    {
        var list = new List<MillCurve>();
        if (req.Curves is null) return list;
        foreach (var curve in req.Curves)
        {
            if (curve.Included && curve.Points is { Count: >= 2 })
                list.Add(curve);
        }
        return list;
    }

    static Toolpath ContourCurves(AdaMillRequest req, List<MillCurve> curves)
    {
        var s = req.Settings;
        var tp = new Toolpath();
        int layerIndex = 0;
        foreach (var curve in curves)
        {
            var down = CurveOffset.DownAxis(curve.Points, curve.Closed);
            var up = -down;
            float step = MathF.Max(0.05f, s.StepdownMm);
            float travel = ContourTravel(s, down, curve.Points[0], req.Positions);
            int passes = travel < 1e-3f ? 1 : (int)MathF.Floor(travel / step + 1e-3f) + 1;
            Vector3? lastEnd = null;
            for (int i = 0; i < passes; i++)
            {
                float dist = MathF.Min(travel, i * step);
                var shifted = Translate(CurveOffset.Apply(curve.Points, curve.Closed, curve.Flipped, s), down * dist);
                if (shifted.Count < 2) continue;
                var layer = new ToolpathLayer(layerIndex++, shifted[0].Z) { PlaneNormal = up, Height = step };
                if (s.ContouringWaterfall && lastEnd is { } from)
                    layer.Moves.Add(new ToolpathMove(from, shifted[0], MoveKind.Mill) { Normal = up });
                EmitLoop(layer, shifted, s, curve.Closed,
                    includeApproach: lastEnd is null || !s.ContouringWaterfall, toolAxis: up, approachUp: up);
                lastEnd = curve.Closed ? shifted[0] : shifted[^1];
                if (layer.Moves.Count > 0) tp.Layers.Add(layer);
            }
        }
        ApplyEngagement(tp, s);
        return tp;
    }

    static Toolpath CutoutCurves(AdaMillRequest req, List<MillCurve> curves)
    {
        var s = req.Settings;
        float depth = MathF.Max(0.05f, s.CutoutCutDepthMm);
        float layerH = MathF.Max(0.05f, s.CutoutLayerHeightMm);
        int n = Math.Max(1, (int)MathF.Ceiling(depth / layerH));
        bool fromSurface = s.CutoutMillingDirection == AdaMillingDirection.FromSurface;
        var tp = new Toolpath();
        int layerIndex = 0;
        foreach (var curve in curves)
        {
            var down = CurveOffset.DownAxis(curve.Points, curve.Closed);
            var up = -down;
            var outline = CurveOffset.Apply(curve.Points, curve.Closed, curve.Flipped, s);
            for (int i = 0; i < n; i++)
            {
                float t = n == 1 ? 0f : i / (float)(n - 1);
                float dist = fromSurface ? depth * (1f - t) : depth * t;
                var pts = Translate([.. outline], down * dist);
                if (pts.Count < 2) continue;
                var layer = new ToolpathLayer(layerIndex++, pts[0].Z) { PlaneNormal = up, Height = layerH };
                EmitLoop(layer, pts, s, curve.Closed, toolAxis: up, approachUp: up);
                if (layer.Moves.Count > 0) tp.Layers.Add(layer);
            }
        }
        ApplyEngagement(tp, s);
        return tp;
    }

    static Toolpath DrillAlongCurves(AdaMillRequest req, List<MillCurve> curves)
    {
        var s = req.Settings;
        float retractH = MathF.Max(1f, s.RetractHeightMm);
        float feedH = MathF.Max(0f, s.FeedHeightMm);
        float probe = MathF.Max(8f, s.ToolDiameterMm);
        var layer = new ToolpathLayer(0, 0f) { PlaneNormal = Vector3.UnitZ };
        Vector3? last = null;
        foreach (var curve in curves)
        {
            var path = CurveOffset.Apply(curve.Points, curve.Closed, curve.Flipped, s);
            if (path.Count < 2) continue;
            var down = CurveOffset.DownAxis(path, curve.Closed);
            var up = -down;
            float span = DrillFeatureFinder.MeasureSpan(req.Positions, path[0], up, probe);
            if (span < 0.05f)
                span = MathF.Max(0.05f, FarAlong(req.Positions, path[0], down));
            float depth = span + MathF.Max(0f, s.DrillingBreakthroughMm);
            var mouth = path[0];
            var tip = mouth - up * depth;
            var retract = mouth + up * retractH;
            var feed = mouth + up * feedH;
            if (last is { } prev)
                layer.Moves.Add(new ToolpathMove(prev, retract, MoveKind.Travel) { IsZHop = true });
            layer.Moves.Add(new ToolpathMove(retract, feed, MoveKind.Travel));
            if (s.DrillingPeck && s.DrillingPeckDepthMm > 0.05f)
            {
                var cursor = mouth;
                while (Vector3.Dot(cursor - tip, up) > 1e-3f)
                {
                    float remain = Vector3.Dot(cursor - tip, up);
                    float peck = MathF.Min(remain, s.DrillingPeckDepthMm);
                    var next = cursor - up * peck;
                    layer.Moves.Add(new ToolpathMove(cursor, next, MoveKind.Mill) { Normal = up });
                    layer.Moves.Add(new ToolpathMove(next, feed, MoveKind.Travel) { IsZHop = true });
                    cursor = next;
                    if (peck <= 1e-4f) break;
                }
            }
            layer.Moves.Add(new ToolpathMove(feed, tip, MoveKind.Mill) { Normal = up });
            var prevP = tip;
            int count = curve.Closed ? path.Count : path.Count - 1;
            for (int i = 0; i < count; i++)
            {
                var b = path[(i + 1) % path.Count] - up * depth;
                if (Vector3.DistanceSquared(prevP, b) < 1e-10f) continue;
                layer.Moves.Add(new ToolpathMove(prevP, b, MoveKind.Mill) { Normal = up });
                prevP = b;
            }
            var endRetract = prevP + up * (depth + retractH);
            layer.Moves.Add(new ToolpathMove(prevP, endRetract, MoveKind.Travel) { IsZHop = true });
            last = endRetract;
        }
        var tp = new Toolpath();
        if (layer.Moves.Count > 0) tp.Layers.Add(layer);
        return tp;
    }

    static float ContourTravel(AdaMachiningSettings s, Vector3 down, Vector3 origin, IReadOnlyList<Vector3> mesh)
    {
        if (s.ContouringMaxDepthEnabled && s.MaxDepthMm > 0f)
            return s.MaxDepthMm;
        if (s.BottomHeightMm != 0f || s.TopHeightMm > 0f)
        {
            float top = s.TopHeightMm > 0f ? s.TopHeightMm : origin.Z;
            float bottom = s.BottomHeightMm != 0f ? s.BottomHeightMm : origin.Z;
            return MathF.Abs(top - bottom);
        }
        return MathF.Max(0f, FarAlong(mesh, origin, down));
    }

    static float FarAlong(IReadOnlyList<Vector3> mesh, Vector3 origin, Vector3 down)
    {
        float here = Vector3.Dot(origin, down);
        float far = here;
        for (int i = 0; i < mesh.Count; i++)
        {
            float t = Vector3.Dot(mesh[i], down);
            if (t > far) far = t;
        }
        return far - here;
    }

    static List<Vector3> Translate(List<Vector3> pts, Vector3 delta)
    {
        if (delta.LengthSquared() < 1e-12f) return pts;
        for (int i = 0; i < pts.Count; i++)
            pts[i] += delta;
        return pts;
    }

    static void EmitLoop(
        ToolpathLayer layer,
        IReadOnlyList<Vector3> pts,
        AdaMachiningSettings s,
        bool closed,
        bool includeApproach = true,
        Vector3? toolAxis = null,
        Vector3? approachUp = null)
    {
        if (pts.Count < 2) return;
        var nrm = toolAxis ?? Vector3.UnitZ;
        float retractH = MathF.Max(1f, s.RetractHeightMm);
        float feedH = MathF.Max(0, s.FeedHeightMm);
        var start = pts[0];
        var up = approachUp is { } axis && axis.LengthSquared() > 1e-8f ? Vector3.Normalize(axis) : Vector3.UnitZ;
        if (includeApproach)
        {
            var r0 = approachUp is null ? new Vector3(start.X, start.Y, start.Z + retractH) : start + up * retractH;
            var f0 = approachUp is null ? new Vector3(start.X, start.Y, start.Z + feedH) : start + up * feedH;
            if (layer.Moves.Count > 0)
                layer.Moves.Add(new ToolpathMove(layer.Moves[^1].To, r0, MoveKind.Travel) { IsZHop = true });
            layer.Moves.Add(new ToolpathMove(r0, f0, MoveKind.Travel));
            layer.Moves.Add(new ToolpathMove(f0, start, MoveKind.Travel));
        }

        int last = closed ? pts.Count : pts.Count - 1;
        for (int i = 0; i < last; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % pts.Count];
            if (Vector3.DistanceSquared(a, b) < 1e-10f) continue;
            layer.Moves.Add(new ToolpathMove(a, b, MoveKind.Mill) { Normal = nrm });
        }
        if (includeApproach && layer.Moves.Count > 0)
        {
            var end = layer.Moves[^1].To;
            var endUp = approachUp is null ? new Vector3(end.X, end.Y, end.Z + retractH) : end + up * retractH;
            layer.Moves.Add(new ToolpathMove(end, endUp, MoveKind.Travel) { IsZHop = true });
        }
    }

    static void Append(Toolpath dest, Toolpath src, int indexBias)
    {
        foreach (var layer in src.Layers)
        {
            var copy = new ToolpathLayer(dest.Layers.Count + indexBias, layer.Z)
            {
                PlaneNormal = layer.PlaneNormal,
                Height = layer.Height,
            };
            copy.Moves.AddRange(layer.Moves);
            dest.Layers.Add(copy);
        }
    }

    static void SmoothMillNormals(Toolpath tp)
    {
        foreach (var layer in tp.Layers)
        {
            Vector3 acc = Vector3.Zero;
            int n = 0;
            for (int i = 0; i < layer.Moves.Count; i++)
            {
                var m = layer.Moves[i];
                if (m.Kind != MoveKind.Mill || m.Normal == Vector3.Zero) continue;
                acc = n == 0 ? m.Normal : Vector3.Normalize(acc * 0.7f + m.Normal * 0.3f);
                n++;
                layer.Moves[i] = m with { Normal = acc };
            }
        }
    }

    static void ApplyEngagement(Toolpath tp, AdaMachiningSettings s)
    {
        if (s.LeadInMm <= 0 && s.LeadOutMm <= 0) return;
        foreach (var layer in tp.Layers)
        {
            int first = layer.Moves.FindIndex(m => m.Kind == MoveKind.Mill);
            int last = layer.Moves.FindLastIndex(m => m.Kind == MoveKind.Mill);
            if (first < 0 || last < 0) continue;
            if (s.LeadInMm > 0)
            {
                var m = layer.Moves[first];
                var dir = m.To - m.From;
                if (dir.LengthSquared() > 1e-8f)
                {
                    dir = Vector3.Normalize(dir);
                    var from = m.From - dir * s.LeadInMm;
                    layer.Moves.Insert(first, new ToolpathMove(from, m.From, MoveKind.Mill) { Normal = m.Normal });
                    last++;
                }
            }
            if (s.LeadOutMm > 0)
            {
                var m = layer.Moves[last];
                var dir = m.To - m.From;
                if (dir.LengthSquared() > 1e-8f)
                {
                    dir = Vector3.Normalize(dir);
                    var to = m.To + dir * s.LeadOutMm;
                    layer.Moves.Insert(last + 1, new ToolpathMove(m.To, to, MoveKind.Mill) { Normal = m.Normal });
                }
            }
        }
    }
}
