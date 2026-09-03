using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing;

/// <summary>
/// AdaOne subtractive planner. Routes each <see cref="MillOperationKind"/> to a real path:
/// planar facing/clearing (raster), multi-axis finishing (surface follow), cutout (closed
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
            MillOperationKind.PlanarClearing     => Planar(req, lockAxis: true),
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
        var tp = SurfaceFollowMillGenerator.GenerateMultiAxis(
            req.Positions, req.Normals, req.Indices, mill);
        if (s.StabilizeHeadRotation)
            SmoothMillNormals(tp);
        ApplyEngagement(tp, s);
        return tp;
    }

    static Toolpath Cutout(AdaMillRequest req)
    {
        var s = req.Settings;
        var b = BoundsOf(req.Positions);
        float top = s.TopHeightMm > 0 ? s.TopHeightMm : b.MaxZ;
        float depth = MathF.Max(0.05f, s.CutoutCutDepthMm);
        float layer = MathF.Max(0.05f, s.CutoutLayerHeightMm);
        float bottom = s.BottomHeightMm != 0 ? s.BottomHeightMm : top - depth;
        if (s.CutoutMillingDirection == AdaMillingDirection.FromSurface)
            (top, bottom) = (bottom, top);

        var loop = MeshWaterline.OuterLoopOrBounds(req.Positions, req.Indices, top);
        loop = Compensate(loop, s);
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
                var pts = Compensate(ShiftZ(loop, z), s);
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
        var s = req.Settings;
        var b = BoundsOf(req.Positions);
        float top = s.TopHeightMm > 0 ? s.TopHeightMm : b.MaxZ;
        float bottom = (s.BottomHeightMm != 0 ? s.BottomHeightMm : b.MinZ) - MathF.Max(0, s.DrillingBreakthroughMm);
        var holes = req.DrillHoles is { Count: > 0 } h
            ? h
            : (IReadOnlyList<Vector3>)[new Vector3(b.Center.X, b.Center.Y, top)];

        float retract = top + MathF.Max(1f, s.RetractHeightMm);
        float feedZ = top + MathF.Max(0, s.FeedHeightMm);
        var layer = new ToolpathLayer(0, top) { PlaneNormal = Vector3.UnitZ };
        Vector3? last = null;
        foreach (var hole in holes)
        {
            var xy = new Vector3(hole.X, hole.Y, retract);
            if (last is { } p)
                layer.Moves.Add(new ToolpathMove(p, xy, MoveKind.Travel) { IsZHop = true });
            var atFeed = new Vector3(hole.X, hole.Y, feedZ);
            layer.Moves.Add(new ToolpathMove(xy, atFeed, MoveKind.Travel));

            if (s.DrillingPeck && s.DrillingPeckDepthMm > 0.05f)
            {
                float z = top;
                while (z > bottom + 1e-4f)
                {
                    float next = MathF.Max(bottom, z - s.DrillingPeckDepthMm);
                    var from = new Vector3(hole.X, hole.Y, z);
                    var to = new Vector3(hole.X, hole.Y, next);
                    layer.Moves.Add(new ToolpathMove(from, to, MoveKind.Mill) { Normal = Vector3.UnitZ });
                    layer.Moves.Add(new ToolpathMove(to, atFeed, MoveKind.Travel) { IsZHop = true });
                    z = next;
                    if (next <= bottom + 1e-4f) break;
                }
            }
            else
            {
                var tip = new Vector3(hole.X, hole.Y, bottom);
                layer.Moves.Add(new ToolpathMove(atFeed, tip, MoveKind.Mill) { Normal = Vector3.UnitZ });
                layer.Moves.Add(new ToolpathMove(tip, xy, MoveKind.Travel) { IsZHop = true });
            }
            last = xy;
        }
        var tp = new Toolpath();
        if (layer.Moves.Count > 0) tp.Layers.Add(layer);
        return tp;
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

    static void EmitLoop(
        ToolpathLayer layer,
        IReadOnlyList<Vector3> pts,
        AdaMachiningSettings s,
        bool closed,
        bool includeApproach = true,
        Vector3? toolAxis = null)
    {
        if (pts.Count < 2) return;
        var nrm = toolAxis ?? Vector3.UnitZ;
        float retract = pts[0].Z + MathF.Max(1f, s.RetractHeightMm);
        float feed = pts[0].Z + MathF.Max(0, s.FeedHeightMm);
        var start = pts[0];
        if (includeApproach)
        {
            var r0 = new Vector3(start.X, start.Y, retract);
            var f0 = new Vector3(start.X, start.Y, feed);
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
            layer.Moves.Add(new ToolpathMove(end, new Vector3(end.X, end.Y, retract), MoveKind.Travel) { IsZHop = true });
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
