using System.Numerics;
using System.Runtime.CompilerServices;
using System.Linq;
using Clipper2Lib;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing;

/// <summary>
/// Angled-planar slicer. Like <see cref="PlanarSlicer"/> but cuts with planes tilted at
/// <see cref="SliceSettings.TiltAngle"/> / <see cref="SliceSettings.TiltAngleX"/> degrees
/// from horizontal. Contours are projected to a plane-local 2D frame for chaining and
/// Clipper2 bead-width offsetting, then unprojected back to 3D for move emission.
/// </summary>
public static class AngledPlanarSlicer
{
    // -- Public entry point ----------------------------------------------------

    public static Toolpath Slice(
        IReadOnlyList<Vector3[]> meshes,
        SliceSettings settings)
    {
        float ty = settings.TiltAngle  * MathF.PI / 180f;
        float tx = settings.TiltAngleX * MathF.PI / 180f;
        // Rotate Z-up normal first around Y (leans toward ±X), then around X (leans toward ±Y).
        var normal = Vector3.Normalize(new Vector3(
            MathF.Sin(ty),
            -MathF.Sin(tx) * MathF.Cos(ty),
             MathF.Cos(tx) * MathF.Cos(ty)));

        // Local 2D frame in the cutting plane.
        // u = cross(worldY, normal) → "x-slope" direction; zero Z for pure Y-tilt.
        // v = cross(normal, u)     → "y-slope" direction; equals worldY for pure Y-tilt.
        // Both are unit vectors perpendicular to normal, so projecting to (u,v) preserves distances.
        var worldY = new Vector3(0f, 1f, 0f);
        var u = Vector3.Normalize(Vector3.Cross(worldY, normal));
        var v = Vector3.Cross(normal, u);

        // Extent along the plane normal and in world XY (for seam ray origin).
        float tMin = float.MaxValue, tMax = float.MinValue;
        float xMin = float.MaxValue, xMax = float.MinValue;
        float yMin = float.MaxValue, yMax = float.MinValue;
        foreach (var verts in meshes)
            foreach (var vert in verts)
            {
                float t = Vector3.Dot(vert, normal);
                if (t < tMin) tMin = t; if (t > tMax) tMax = t;
                if (vert.X < xMin) xMin = vert.X; if (vert.X > xMax) xMax = vert.X;
                if (vert.Y < yMin) yMin = vert.Y; if (vert.Y > yMax) yMax = vert.Y;
            }

        if (tMax <= tMin) return new Toolpath();

        var sd = settings.SeamDirection;
        float sdLen = sd.Length();
        if (sdLen < 1e-6f) sd = new Vector2(0f, 1f); else sd /= sdLen;
        float cx    = (xMin + xMax) * 0.5f;
        float cy    = (yMin + yMax) * 0.5f;
        float reach = (xMax - xMin + yMax - yMin) + 10f;
        var seamOriginXY = new Vector2(cx + sd.X * reach, cy + sd.Y * reach);

        // Project seam direction to plane-local once — independent of planeD.
        var sd3d = new Vector3(sd.X, sd.Y, 0f);
        sd3d -= Vector3.Dot(sd3d, normal) * normal;
        float sd3dLen = sd3d.Length();
        if (sd3dLen < 1e-6f) sd3d = u; else sd3d /= sd3dLen;
        var seamDirLocal = new Vector2(Vector3.Dot(sd3d, u), Vector3.Dot(sd3d, v));

        var   toolpath   = new Toolpath();
        int   idx        = 0;
        var   prevTracks = new List<ContourTrack>();
        Vector3? prevEnd = null;

        var steps = new List<float>();
        for (float st = tMin + settings.FirstLayerHeight; st < tMax - 1e-4f; st += settings.LayerHeight)
            steps.Add(st);

        // ── Lightning Bridge pre-pass (see PlanarSlicer): cache contours for every
        //    plane, then build the top-down finger plan in plane-local 2D.
        //    Constant tilt ⇒ same (u,v,n) every layer; still pass per-layer frames
        //    (Origin = n·step) so Target Support Selections / paint projection use
        //    the real tilted plane equation — not world-Z planar assumptions.
        List<List<List<Vector2>>>? lightningCache = null;
        Lightning.LightningPlan? lightningPlan = null;
        TreeSupport.TreeSupportPlan? treePlan = null;
        bool hasFormboundPaint = PaintSupportStyleUtil.HasFormboundPaint(settings.PaintMarks);
        bool hasTreePaint = PaintSupportStyleUtil.HasTreePaint(settings.PaintMarks);
        // Target Support Selections: Formbound only from paint, not the global dropdown.
        bool formboundActive = settings.LightningTargetSupportSelections
            ? hasFormboundPaint
            : (Lightning.LightningPlanner.IsFormboundPattern(settings.InfillPattern)
               || hasFormboundPaint);
        bool needLightning = formboundActive || settings.XBracingEnabled || hasTreePaint;
        if (needLightning)
        {
            bool surfaceMode = settings.SlicingMode == SlicingMode.Surface;
            lightningCache = new(steps.Count);
            var fillPolysPerLayer = new List<List<List<Vector2>>>(steps.Count);
            var heights = new List<float>(steps.Count);
            // Constant-tilt frames (parallel planes). Formbound paint demand keys
            // off signed distance along normal — works for Angled, not only MultiPlanar.
            var frames = new List<(Vector3 Origin, Vector3 U, Vector3 V)>(steps.Count);
            for (int si = 0; si < steps.Count; si++)
            {
                var contours = ComputeInsetContours(meshes, normal, steps[si], normal * steps[si], u, v, settings);
                lightningCache.Add(contours);
                fillPolysPerLayer.Add(FilterFillPolys(contours, surfaceMode));
                heights.Add(si == 0 ? settings.FirstLayerHeight : settings.LayerHeight);
                frames.Add((normal * steps[si], u, v));
            }
            // The oracle probes just BELOW the plane: the demanding solid occupies
            // the layer beneath it, and a grazing plane itself is ambiguous.
            var meshTester = new Lightning.MeshInsideTester(meshes);
            float halfBand = FormboundPaintHalfBandMm(settings, angledConstantTilt: true);
            Func<int, (Vector3 Origin, Vector3 Normal, Vector3 U, Vector3 V)> frameOf =
                li => (normal * steps[li], normal, u, v);

            if (formboundActive)
            {
                // Constant-tilt is a PARALLEL-plane stack: the plane-local demand
                // path is correct here. Passing frames activates the multi-planar
                // reprojection / gravity machinery, which mis-classifies demand on
                // parallel planes (lost perimeter + floating fingers). Multi-planar
                // (SliceMultiPlanar) genuinely rotates frames and keeps them.
                lightningPlan = Lightning.LightningPlanner.Build(fillPolysPerLayer, heights, settings,
                    solidAt: (li, p) => meshTester.IsInside(
                        normal * (steps[li] - 0.4f * heights[li]) + u * p.X + v * p.Y),
                    manualDemand: ToolpathPaintFilter.ProjectBridgeMarks(
                        settings.PaintMarks, steps.Count, frameOf,
                        halfBandMm: halfBand,
                        targetSupportSelectionsOnly: settings.LightningTargetSupportSelections,
                        styleFilter: PaintSupportStyleUtil.IsFormbound));
            }
            else
                lightningPlan = new Lightning.LightningPlan(steps.Count);

            if (hasTreePaint)
            {
                var treeDemand = ToolpathPaintFilter.ProjectBridgeMarks(
                    settings.PaintMarks, steps.Count, frameOf,
                    halfBandMm: halfBand,
                    targetSupportSelectionsOnly: true,
                    styleFilter: PaintSupportStyleUtil.IsTree);
                treePlan = TreeSupport.TreeSupportPlanner.Build(
                    fillPolysPerLayer, heights, settings, treeDemand, frames);
            }

            if (settings.XBracingEnabled)
            {
                var polysForX = new List<List<List<Vector2>>>(steps.Count);
                for (int si = 0; si < steps.Count; si++)
                {
                    var fill = fillPolysPerLayer[si];
                    if (fill.Count > 0) polysForX.Add(fill);
                    else polysForX.Add(lightningCache![si]);
                }
                Lightning.XBracingPlanner.Apply(
                    lightningPlan, polysForX, steps, heights, settings);
            }

            // Generator oracles: SolidAt probes both sides of the plane (fresh
            // islands have material only above their first plane); SolidAtPlane
            // probes exactly at it (a real contour's interior is solid there).
            for (int li = 0; li < steps.Count; li++)
            {
                int cap = li;
                lightningPlan.Layers[li].SolidAt = p =>
                    meshTester.IsInside(normal * (steps[cap] - 0.4f * heights[cap]) + u * p.X + v * p.Y)
                    || meshTester.IsInside(normal * (steps[cap] + 0.4f * heights[cap]) + u * p.X + v * p.Y);
                lightningPlan.Layers[li].SolidAtPlane = p =>
                    meshTester.IsInside(normal * steps[cap] + u * p.X + v * p.Y);
            }
        }

        for (int si = 0; si < steps.Count; si++)
        {
            float step = steps[si];
            // origin = closest point on this plane to the world origin.
            var origin = normal * step;

            // Project seam ray origin to plane-local (depends on planeD via origin).
            var seamOriginLocal = ToLocal(seamOriginXY, normal, step, origin, u, v);

            float repZ = normal.Z > 1e-6f ? step / normal.Z : step;
            var   layer = new ToolpathLayer(idx++, repZ) { PlaneNormal = normal };

            bool isLastLayer = si == steps.Count - 1;
            prevTracks = BuildLayer(meshes, normal, step, origin, u, v,
                seamOriginLocal, seamDirLocal, settings, prevTracks, layer, isLastLayer,
                cachedContours: lightningCache?[si],
                lightningPlan:  lightningPlan?.Layers[si],
                treePlan:       treePlan?.Layers[si],
                prevEnd: prevEnd);

            if (layer.Moves.Count > 0)
            {
                toolpath.Layers.Add(layer);
                prevEnd = layer.Moves[^1].To;
            }
        }

        if (settings.PaintMarks.Count > 0)
            ToolpathPaintFilter.ApplyRemovals(toolpath, settings.PaintMarks);

        if (lightningPlan is not null)
            toolpath.FormboundStats = lightningPlan.ToStats();

        return toolpath;
    }

    /// <summary>
    /// Multi-Planar slicing: the cutting plane's tilt interpolates through the guide
    /// plane stack (height % → angle). Outside the first/last guide the angle
    /// <b>holds</b> and keeps projecting along that same tilt until the mesh is fully
    /// covered — end guides never truncate the stack. Because consecutive planes are
    /// not parallel, each layer is a wedge: per-move <see cref="ToolpathMove.HeightScale"/>
    /// records the local thickness relative to the nominal layer height so export can
    /// scale extrusion RPM and the preview can draw the true bead height. The per-layer
    /// tilt change is clamped so the thin side never drops below a quarter layer.
    /// </summary>
    public static Toolpath SliceMultiPlanar(IReadOnlyList<Vector3[]> meshes, SliceSettings settings)
    {
        // Bounds and the vertical axis the guide planes anchor to.
        float xMin = float.MaxValue, xMax = float.MinValue;
        float yMin = float.MaxValue, yMax = float.MinValue;
        float zMin = float.MaxValue, zMax = float.MinValue;
        foreach (var verts in meshes)
            foreach (var vert in verts)
            {
                if (vert.X < xMin) xMin = vert.X; if (vert.X > xMax) xMax = vert.X;
                if (vert.Y < yMin) yMin = vert.Y; if (vert.Y > yMax) yMax = vert.Y;
                if (vert.Z < zMin) zMin = vert.Z; if (vert.Z > zMax) zMax = vert.Z;
            }
        if (zMax <= zMin) return new Toolpath();

        float cx = (xMin + xMax) * 0.5f;
        float cy = (yMin + yMax) * 0.5f;
        bool axisX = settings.MultiPlanarAxisX;
        // Thickness varies along the lean direction: X for tilt-about-Y, Y for
        // tilt-about-X — the lever arm is the part's largest reach on that axis.
        float lever = axisX
            ? MathF.Max(MathF.Max(yMax - cy, cy - yMin), 1f)
            : MathF.Max(MathF.Max(xMax - cx, cx - xMin), 1f);

        float layerH = MathF.Max(settings.LayerHeight, 0.1f);

        // Guide stack, sorted by height. First/last angles hold and project past
        // those heights — guides only shape the tilt, they do not start/stop the cut.
        var guides = (settings.MultiPlanarPlanes is { Count: >= 2 } g
                ? g.OrderBy(pl => pl.HeightPct).ToArray()
                : [new MultiPlanarPlane(0f, 0f), new MultiPlanarPlane(100f, 30f)]);

        // Planes must not cross inside the part: thickness at the far edge is
        // layerH − Δθ·lever, so cap the per-layer rotation at 75% of the nominal.
        float maxStepRad = 0.75f * layerH / lever;

        float ThetaAt(float f)
        {
            float pct = f * 100f;
            // Below first guide → hold first angle (project past the bottom guide).
            if (pct <= guides[0].HeightPct) return guides[0].AngleDeg * MathF.PI / 180f;
            for (int k = 1; k < guides.Length; k++)
            {
                if (pct > guides[k].HeightPct) continue;
                float span = MathF.Max(guides[k].HeightPct - guides[k - 1].HeightPct, 1e-3f);
                float t = (pct - guides[k - 1].HeightPct) / span;
                float a = guides[k - 1].AngleDeg + (guides[k].AngleDeg - guides[k - 1].AngleDeg) * t;
                return a * MathF.PI / 180f;
            }
            // Above last guide → hold last angle (project past the top guide).
            return guides[^1].AngleDeg * MathF.PI / 180f;
        }

        var toolpath  = new Toolpath();
        var prevTracks = new List<ContourTrack>();
        int idx = 0;
        Vector3? prevEnd = null;

        var sd = settings.SeamDirection;
        float sdLen = sd.Length();
        if (sdLen < 1e-6f) sd = new Vector2(0f, 1f); else sd /= sdLen;
        float reach = (xMax - xMin + yMax - yMin) + 10f;
        var seamOriginXY = new Vector2(cx + sd.X * reach, cy + sd.Y * reach);

        // ── Pre-compute the whole plane march (needed twice for lightning). Frames
        //    are anchored at the part's central axis: consecutive frames rotate at
        //    most maxStepRad, so a point's plane-local coordinates drift by at most
        //    0.75·layerH per layer at the far edge — under the lightning planner's
        //    support radius, which is what lets its cross-layer propagation work on
        //    a slowly rotating frame stack.
        //
        //    Z range is padded by lever·tan(max|θ|) so a tilted first/last plane
        //    still reaches the high/low corners of the AABB. March continues with
        //    the held end angles until the plane no longer intersects the mesh —
        //    never stopping at a guide height or at raw zMax alone.
        var march = new List<(float H, float Theta, Vector3 Normal, float PlaneD, Vector3 Origin, Vector3 U, Vector3 V)>();
        {
            float maxAbsTheta = 0f;
            foreach (var gp in guides)
                maxAbsTheta = MathF.Max(maxAbsTheta, MathF.Abs(gp.AngleDeg) * MathF.PI / 180f);
            float zPad = lever * MathF.Tan(MathF.Min(maxAbsTheta, 75f * MathF.PI / 180f))
                       + layerH * 2f;

            var aabbMin = new Vector3(xMin, yMin, zMin);
            var aabbMax = new Vector3(xMax, yMax, zMax);

            // Does infinite plane n·x = planeD intersect the mesh AABB?
            static bool PlaneHitsAabb(Vector3 n, float planeD, Vector3 bmin, Vector3 bmax)
            {
                // Min/max of n·x over the box = sum of min/max contributions per axis.
                float tMin = 0f, tMax = 0f;
                if (n.X >= 0f) { tMin += n.X * bmin.X; tMax += n.X * bmax.X; }
                else           { tMin += n.X * bmax.X; tMax += n.X * bmin.X; }
                if (n.Y >= 0f) { tMin += n.Y * bmin.Y; tMax += n.Y * bmax.Y; }
                else           { tMin += n.Y * bmax.Y; tMax += n.Y * bmin.Y; }
                if (n.Z >= 0f) { tMin += n.Z * bmin.Z; tMax += n.Z * bmax.Z; }
                else           { tMin += n.Z * bmax.Z; tMax += n.Z * bmin.Z; }
                const float eps = 1e-3f;
                return planeD >= tMin - eps && planeD <= tMax + eps;
            }

            // Start below the mesh so the first held angle can still cut the low
            // corner under tilt; first-layer offset matches the classic bottom pad.
            float h0 = zMin - zPad + MathF.Max(settings.FirstLayerHeight, layerH * 0.5f);
            float hCeil = zMax + zPad + layerH * 4f;
            float thetaWalk = ThetaAt(0f);
            var refAxis = axisX ? new Vector3(1f, 0f, 0f) : new Vector3(0f, 1f, 0f);
            bool seenHit = false;
            int emptyAfterHit = 0;
            const int emptyStop = 3; // a few empty planes past the mesh → done
            int safety = 0;
            const int maxLayers = 200_000;

            while (h0 < hCeil && safety++ < maxLayers)
            {
                // Height fraction for guide interpolation is relative to the mesh
                // body only — outside [zMin,zMax] f clamps so end angles hold.
                float f = Math.Clamp((h0 - zMin) / (zMax - zMin), 0f, 1f);
                float theta = Math.Clamp(ThetaAt(f), thetaWalk - maxStepRad, thetaWalk + maxStepRad);
                var normal = axisX
                    ? Vector3.Normalize(new Vector3(0f, -MathF.Sin(theta), MathF.Cos(theta)))
                    : Vector3.Normalize(new Vector3(MathF.Sin(theta), 0f, MathF.Cos(theta)));
                var anchor = new Vector3(cx, cy, h0);
                float planeD = Vector3.Dot(anchor, normal);
                bool hits = PlaneHitsAabb(normal, planeD, aabbMin, aabbMax);

                if (hits)
                {
                    seenHit = true;
                    emptyAfterHit = 0;
                    var u = Vector3.Normalize(Vector3.Cross(refAxis, normal));
                    var v = Vector3.Cross(normal, u);
                    march.Add((h0, theta, normal, planeD, anchor, u, v));
                }
                else if (seenHit)
                {
                    // Past the mesh along this stack — stop after a few empties so a
                    // grazing miss mid-part doesn't abort early.
                    emptyAfterHit++;
                    if (emptyAfterHit >= emptyStop) break;
                }
                // else: still approaching the mesh from below — keep walking

                thetaWalk = theta;
                h0 += layerH / MathF.Max(MathF.Cos(theta), 0.2f);
            }
        }
        if (march.Count == 0) return toolpath;

        // ── Formbound / X-bracing pre-pass: cache every plane's contours, then build
        //    the top-down finger plan across the (slowly rotating) frame stack.
        List<List<List<Vector2>>>? lightningCache = null;
        Lightning.LightningPlan? lightningPlan = null;
        TreeSupport.TreeSupportPlan? treePlan = null;
        bool hasFormboundPaintMp = PaintSupportStyleUtil.HasFormboundPaint(settings.PaintMarks);
        bool hasTreePaintMp = PaintSupportStyleUtil.HasTreePaint(settings.PaintMarks);
        bool formboundActiveMp = settings.LightningTargetSupportSelections
            ? hasFormboundPaintMp
            : (Lightning.LightningPlanner.IsFormboundPattern(settings.InfillPattern)
               || hasFormboundPaintMp);
        bool needLightningMp = formboundActiveMp || settings.XBracingEnabled || hasTreePaintMp;
        if (needLightningMp)
        {
            bool surfaceMode = settings.SlicingMode == SlicingMode.Surface;
            var dedupedMeshes = meshes;
            lightningCache = new(march.Count);
            var fillPolysPerLayer = new List<List<List<Vector2>>>(march.Count);
            var heights = new List<float>(march.Count);
            var frames  = new List<(Vector3 Origin, Vector3 U, Vector3 V)>(march.Count);
            foreach (var st in march)
            {
                var contours = ComputeInsetContours(dedupedMeshes, st.Normal, st.PlaneD, st.Origin, st.U, st.V, settings);
                lightningCache.Add(contours);
                fillPolysPerLayer.Add(FilterFillPolys(contours, surfaceMode));
                heights.Add(layerH);
                frames.Add((st.Origin, st.U, st.V));
            }
            // The oracle probes just BELOW the plane: the demanding solid occupies
            // the layer beneath it, and a grazing plane itself is ambiguous.
            var meshTester = new Lightning.MeshInsideTester(meshes);
            float halfBand = FormboundPaintHalfBandMm(settings, angledConstantTilt: false);
            Func<int, (Vector3 Origin, Vector3 Normal, Vector3 U, Vector3 V)> frameOf =
                li => (march[li].Origin, march[li].Normal, march[li].U, march[li].V);

            if (formboundActiveMp)
            {
                lightningPlan = Lightning.LightningPlanner.Build(fillPolysPerLayer, heights, settings, frames,
                    solidAt: (li, p) => meshTester.IsInside(
                        march[li].Origin - march[li].Normal * (0.4f * layerH)
                        + march[li].U * p.X + march[li].V * p.Y),
                    manualDemand: ToolpathPaintFilter.ProjectBridgeMarks(
                        settings.PaintMarks, march.Count, frameOf,
                        halfBandMm: halfBand,
                        targetSupportSelectionsOnly: settings.LightningTargetSupportSelections,
                        styleFilter: PaintSupportStyleUtil.IsFormbound));
            }
            else
                lightningPlan = new Lightning.LightningPlan(march.Count);

            if (hasTreePaintMp)
            {
                var treeDemand = ToolpathPaintFilter.ProjectBridgeMarks(
                    settings.PaintMarks, march.Count, frameOf,
                    halfBandMm: halfBand,
                    targetSupportSelectionsOnly: true,
                    styleFilter: PaintSupportStyleUtil.IsTree);
                treePlan = TreeSupport.TreeSupportPlanner.Build(
                    fillPolysPerLayer, heights, settings, treeDemand, frames);
            }

            if (settings.XBracingEnabled)
            {
                var zProxy = new float[march.Count];
                for (int i = 0; i < march.Count; i++) zProxy[i] = i * layerH;
                Lightning.XBracingPlanner.Apply(
                    lightningPlan, fillPolysPerLayer, zProxy, heights, settings);
            }

            // Generator oracles: SolidAt probes both sides of the plane (fresh
            // islands have material only above their first plane); SolidAtPlane
            // probes exactly at it (a real contour's interior is solid there).
            for (int li = 0; li < march.Count; li++)
            {
                int cap = li;
                lightningPlan.Layers[li].SolidAt = p =>
                    meshTester.IsInside(march[cap].Origin - march[cap].Normal * (0.4f * layerH)
                        + march[cap].U * p.X + march[cap].V * p.Y)
                    || meshTester.IsInside(march[cap].Origin + march[cap].Normal * (0.4f * layerH)
                        + march[cap].U * p.X + march[cap].V * p.Y);
                lightningPlan.Layers[li].SolidAtPlane = p =>
                    meshTester.IsInside(march[cap].Origin + march[cap].U * p.X + march[cap].V * p.Y);
            }
        }

        Vector3 nPrev = default; float dPrev = 0f; bool hasPrev = false;

        for (int si = 0; si < march.Count; si++)
        {
            var (h, theta, normal, planeD, origin, u, v) = march[si];

            var sd3d = new Vector3(sd.X, sd.Y, 0f);
            sd3d -= Vector3.Dot(sd3d, normal) * normal;
            float sd3dLen = sd3d.Length();
            if (sd3dLen < 1e-6f) sd3d = u; else sd3d /= sd3dLen;
            var seamDirLocal    = new Vector2(Vector3.Dot(sd3d, u), Vector3.Dot(sd3d, v));
            var seamOriginLocal = ToLocal(seamOriginXY, normal, planeD, origin, u, v);

            // Use world-space plane origin Z for layer.Z (NOT march height h).
            // Storing h (often ≪ 0 on multiplanar stacks) broke 2D slice grid/HUD
            // and made tree geometry appear far below the bed.
            var layer = new ToolpathLayer(toolpath.Layers.Count, origin.Z)
            {
                PlaneNormal = normal,
                Height = layerH,
            };
            idx++;

            bool isLastLayer = si == march.Count - 1;
            prevTracks = BuildLayer(meshes, normal, planeD, origin, u, v,
                seamOriginLocal, seamDirLocal, settings, prevTracks, layer, isLastLayer,
                cachedContours: lightningCache?[si],
                lightningPlan:  lightningPlan?.Layers[si],
                treePlan:       treePlan?.Layers[si],
                prevEnd: prevEnd);

            // Wedge thickness: distance from each move to the PREVIOUS plane, relative
            // to nominal. First layer sits on the bed at nominal thickness.
            if (hasPrev && layer.Moves.Count > 0)
            {
                for (int mi = 0; mi < layer.Moves.Count; mi++)
                {
                    var move = layer.Moves[mi];
                    if (move.Kind != MoveKind.Extrude) continue;
                    var mid = (move.From + move.To) * 0.5f;
                    float thick = MathF.Abs(Vector3.Dot(mid, nPrev) - dPrev);
                    float scale = Math.Clamp(thick / layerH, 0.25f, 3f);
                    layer.Moves[mi] = move with { HeightScale = scale };
                }
            }

            // Prefer representative Z from actual extrusion mids (stable for 2D slice).
            if (layer.Moves.Count > 0)
            {
                float zSum = 0f;
                int zN = 0;
                foreach (var mv in layer.Moves)
                {
                    if (mv.Kind != MoveKind.Extrude || mv.IsLayerStitch || mv.IsLayerChange) continue;
                    zSum += (mv.From.Z + mv.To.Z) * 0.5f;
                    zN++;
                    if (zN >= 32) break;
                }
                if (zN > 0)
                {
                    // ToolpathLayer.Z is set in ctor only — rebuild with better Z.
                    var fixedLayer = new ToolpathLayer(layer.Index, zSum / zN)
                    {
                        PlaneNormal = layer.PlaneNormal,
                        Height = layer.Height,
                        ThermalTempC = layer.ThermalTempC,
                    };
                    fixedLayer.Moves.AddRange(layer.Moves);
                    fixedLayer.Contours.AddRange(layer.Contours);
                    layer = fixedLayer;
                }

                toolpath.Layers.Add(layer);
                prevEnd = layer.Moves[^1].To;
            }

            nPrev = normal; dPrev = planeD; hasPrev = true;
        }

        if (settings.PaintMarks.Count > 0)
            ToolpathPaintFilter.ApplyRemovals(toolpath, settings.PaintMarks);

        if (lightningPlan is not null)
            toolpath.FormboundStats = lightningPlan.ToStats();

        return toolpath;
    }

    /// <summary>Surface mode fills across closed boundary chains only (1 mm closure).</summary>
    /// <summary>
    /// Half-band (mm) along the cutting-plane normal for projecting edit Support
    /// paint onto Formbound layers. Angled constant-tilt uses a slightly larger
    /// band so bead mids on a tilted path still hit their plane; Multi-Planar uses
    /// the same helper with room for guide-plane drift.
    /// </summary>
    private static float FormboundPaintHalfBandMm(SliceSettings settings, bool angledConstantTilt)
    {
        float lh = MathF.Max(settings.LayerHeight, 0.1f);
        float bead = MathF.Max(settings.BeadWidth, 0.5f);
        if (settings.LightningTargetSupportSelections)
        {
            // Target Support Selections: prefer reliable capture of wall-path marks.
            // Constant tilt: marks lie on parallel planes → band ~ 1.5 layer + bead.
            // Multi-Planar: frames rotate slowly → a bit more slack.
            return angledConstantTilt
                ? MathF.Max(lh * 1.5f, bead * 1.25f)
                : MathF.Max(lh * 1.75f, bead * 1.5f);
        }
        return MathF.Max(lh * 0.75f, bead * 0.75f);
    }

    private static List<List<Vector2>> FilterFillPolys(List<List<Vector2>> contours, bool surfaceMode)
        => surfaceMode
            ? contours.Where(c => c.Count >= 3
                && Vector2.DistanceSquared(c[0], c[^1]) <= 1.0f).ToList()
            : contours;

    // Projects a world-XY point to plane-local (u,v) by solving the plane equation for Z.
    private static Vector2 ToLocal(Vector2 xy, Vector3 normal, float planeD,
        Vector3 origin, Vector3 u, Vector3 v)
    {
        float sz = MathF.Abs(normal.Z) > 1e-6f
            ? (planeD - normal.X * xy.X - normal.Y * xy.Y) / normal.Z
            : origin.Z;
        var rel = new Vector3(xy.X, xy.Y, sz) - origin;
        return new Vector2(Vector3.Dot(rel, u), Vector3.Dot(rel, v));
    }

    // -- Layer construction ----------------------------------------------------

    private static List<ContourTrack> BuildLayer(
        IReadOnlyList<Vector3[]> meshes,
        Vector3 normal, float planeD,
        Vector3 origin, Vector3 u, Vector3 v,
        Vector2 seamOrigin2d, Vector2 seamDir2d,
        SliceSettings settings,
        List<ContourTrack> prevTracks,
        ToolpathLayer layer,
        bool isLastLayer = false,
        List<List<Vector2>>? cachedContours = null,
        Lightning.LightningLayerPlan? lightningPlan = null,
        TreeSupport.TreeSupportLayerPlan? treePlan = null,
        Vector3? prevEnd = null)
    {
        var insetContours = cachedContours
            ?? ComputeInsetContours(meshes, normal, planeD, origin, u, v, settings);
        // Empty mesh cut: still emit freestanding tree columns (bed foundation).
        if (insetContours.Count == 0)
        {
            if (treePlan is { Branches.Count: > 0 })
            {
                int first = layer.Moves.Count;
                float zFloor = origin.Z - MathF.Abs(settings.LayerHeight) * 0.25f;
                TreeSupport.TreeSupportGenerator.Emit(
                    treePlan, planeD, layer, settings.BeadWidth, partFillPolys: null,
                    project: p => origin + p.X * u + p.Y * v,
                    minWorldZ: zFloor);
                for (int i = first; i < layer.Moves.Count; i++)
                    layer.Moves[i] = layer.Moves[i] with { Normal = Vector3.UnitZ };
            }
            return new List<ContourTrack>();
        }

        return BuildLayerBody(settings, layer, normal, planeD, origin, u, v,
            seamOrigin2d, seamDir2d, prevTracks, insetContours, isLastLayer, lightningPlan,
            treePlan, prevEnd);
    }

    /// <summary>
    /// Stages 1–3: mesh∩plane segments (plane-local 2D) → chained contours →
    /// nesting/orientation/offset. Extracted so the Lightning pre-pass can compute
    /// and cache every layer's contours before any moves are emitted.
    /// </summary>
    private static List<List<Vector2>> ComputeInsetContours(
        IReadOnlyList<Vector3[]> meshes,
        Vector3 normal, float planeD,
        Vector3 origin, Vector3 u, Vector3 v,
        SliceSettings settings)
    {
        // ── Stage 1: collect 3D intersection segments, project to plane-local 2D ─
        var perMeshSegs = new List<List<(Vector2 A, Vector2 B)>>(meshes.Count);
        Span<Vector3> buf = stackalloc Vector3[2];
        foreach (var verts in meshes)
        {
            var segs = new List<(Vector2, Vector2)>(64);
            for (int i = 0; i + 2 < verts.Length; i += 3)
            {
                var v0 = verts[i]; var v1 = verts[i + 1]; var v2 = verts[i + 2];
                float d0 = Vector3.Dot(v0, normal) - planeD;
                float d1 = Vector3.Dot(v1, normal) - planeD;
                float d2 = Vector3.Dot(v2, normal) - planeD;
                if (MathF.Abs(d0) < 1e-5f) d0 = d0 >= 0f ? 1e-5f : -1e-5f;
                if (MathF.Abs(d1) < 1e-5f) d1 = d1 >= 0f ? 1e-5f : -1e-5f;
                if (MathF.Abs(d2) < 1e-5f) d2 = d2 >= 0f ? 1e-5f : -1e-5f;
                int count = 0;
                TryEdge(v0, v1, d0, d1, buf, ref count);
                TryEdge(v1, v2, d1, d2, buf, ref count);
                TryEdge(v2, v0, d2, d0, buf, ref count);
                if (count == 2)
                {
                    var relA = buf[0] - origin;
                    var relB = buf[1] - origin;
                    segs.Add((
                        new Vector2(Vector3.Dot(relA, u), Vector3.Dot(relA, v)),
                        new Vector2(Vector3.Dot(relB, u), Vector3.Dot(relB, v))));
                }
            }
            if (segs.Count > 0) perMeshSegs.Add(segs);
        }
        if (perMeshSegs.Count == 0) return new List<List<Vector2>>();

        // ── Stage 2: chain by endpoint proximity in 2D ───────────────────────
        var rawContours = new List<List<Vector2>>();
        foreach (var segs in perMeshSegs)
            rawContours.AddRange(ChainByProximity(segs));

        // ── Stage 3: nesting depth + bead-width offset ───────────────────────
        if (rawContours.Count == 0) return new List<List<Vector2>>();

        bool surfaceMode = settings.SlicingMode == SlicingMode.Surface;

        int nc = rawContours.Count;
        var depths = new int[nc];
        for (int i = 0; i < nc; i++)
        {
            var ci = rawContours[i];
            if (ci.Count == 0) continue;
            var samples = new[] { ci[0], ci[ci.Count / 2], ci[ci.Count - 1] };
            for (int j = 0; j < nc; j++)
            {
                if (i == j) continue;
                int hits = 0;
                foreach (var s in samples) if (PointInPolygon(s, rawContours[j])) hits++;
                if (hits >= 2) depths[i]++;
            }
        }

        if (surfaceMode)
            depths = SurfaceSlicing.FilterContours(rawContours, depths, settings.BeadWidth);
        nc = rawContours.Count;

        float halfBead = settings.BeadWidth * 0.5f;
        float simpTol  = settings.SimplificationTolerance;
        bool  skipInset = settings.DisableContourOffset || surfaceMode;
        var insetContours = new List<List<Vector2>>(nc);
        for (int ci = 0; ci < nc; ci++)
        {
            var  c      = rawContours[ci];
            bool isHole = depths[ci] % 2 != 0;

            bool wantCCW = !isHole;
            bool isCCW   = SignedArea(c) > 0f;
            IReadOnlyList<Vector2> oriented;
            if (wantCCW == isCCW) oriented = c;
            else { var r = new List<Vector2>(c); r.Reverse(); oriented = r; }

            if (skipInset)
            {
                var ol = oriented is List<Vector2> ol2 ? ol2 : oriented.ToList();
                if (ol.Count >= 3)
                    insetContours.Add(simpTol > 0f ? SimplifyContour2D(ol, simpTol) : ol);
            }
            else
            {
                float delta   = isHole ? -halfBead : halfBead;
                var   results = InsetContour2D(oriented, delta);
                foreach (var r in results)
                    if (r.Count >= 3)
                        insetContours.Add(simpTol > 0f ? SimplifyContour2D(r, simpTol) : r);
            }
        }
        return insetContours;
    }

    private static List<ContourTrack> BuildLayerBody(
        SliceSettings settings, ToolpathLayer layer,
        Vector3 normal, float planeD,
        Vector3 origin, Vector3 u, Vector3 v,
        Vector2 seamOrigin2d, Vector2 seamDir2d,
        List<ContourTrack> prevTracks,
        List<List<Vector2>> insetContours,
        bool isLastLayer,
        Lightning.LightningLayerPlan? lightningPlan,
        TreeSupport.TreeSupportLayerPlan? treePlan = null,
        Vector3? prevEnd = null)
    {
        bool surfaceMode = settings.SlicingMode == SlicingMode.Surface;
        bool targetSel = settings.LightningTargetSupportSelections;
        bool hasPaintTrees = lightningPlan is not null
            && lightningPlan.Trees.Any(t =>
                (t.Manual || t.PaintColumn) && !lightningPlan.DroppedTrees.Contains(t.Id));
        bool formboundEmit = targetSel
            ? hasPaintTrees
            : (Lightning.LightningPlanner.IsFormboundPattern(settings.InfillPattern)
               || (hasPaintTrees && PaintSupportStyleUtil.HasFormboundPaint(settings.PaintMarks)));
        Vector3 Unproject(Vector2 p) => origin + p.X * u + p.Y * v;

        void EmitTree()
        {
            if (treePlan is null || treePlan.Branches.Count == 0) return;
            int first = layer.Moves.Count;
            // Freestanding trees: pass part only for inside-push / soft clip (generator
            // no longer aggressively Differences expanded part — that wiped bed columns).
            var fill = FilterFillPolys(insetContours, surfaceMode);
            if (fill.Count == 0) fill = insetContours.Where(c => c.Count >= 3).ToList();
            // Never emit below this plane's world height (trees stay on the slice).
            float zFloor = origin.Z - MathF.Abs(settings.LayerHeight) * 0.5f;
            TreeSupport.TreeSupportGenerator.Emit(
                treePlan, planeD, layer, settings.BeadWidth, fill, Unproject,
                minWorldZ: zFloor);
            // Keep world-up tool normal for freestanding columns (not plane-tilted).
            for (int i = first; i < layer.Moves.Count; i++)
                layer.Moves[i] = layer.Moves[i] with { Normal = Vector3.UnitZ };
        }

        // ── Infill mode: replace shell contours with a continuous fill pattern.
        // Contours are plane-local 2D; the projector lifts infill back onto the tilted plane.
        if (settings.InfillPattern != InfillPattern.None || formboundEmit)
        {
            // Surface mode fills across closed boundary chains only (1 mm closure
            // threshold, same as ChainByProximity); open chains keep their paths.
            var fillPolys = FilterFillPolys(insetContours, surfaceMode);
            if (fillPolys.Count > 0)
            {
            float baseAngle = settings.InfillAngleDeg;
            float infillAngle = settings.InfillPattern switch
            {
                InfillPattern.Grid          => baseAngle + (layer.Index % 2) * 90f,
                InfillPattern.GhostMeshGrid => baseAngle + (layer.Index % 2) * 90f,
                InfillPattern.Triangle      => baseAngle + (layer.Index % 3) * 60f,
                _                           => baseAngle,
            };
            float infillSpacing = settings.InfillSpacingMm > 0f
                ? settings.InfillSpacingMm
                : settings.BeadWidth;

            int firstInfillMove = layer.Moves.Count;
            if (formboundEmit
                || (settings.XBracingEnabled && lightningPlan is not null && !targetSel))
            {
                Lightning.LightningGenerator.EmitLightning(fillPolys, lightningPlan, planeD, layer,
                    settings.BeadWidth, settings.LightningTipLoopRadiusMm, Unproject, prevEnd,
                    localSupportOnly: targetSel);
            }
            else if (settings.InfillPattern == InfillPattern.GhostMeshGrid)
                InfillGenerator.EmitGhostMesh(fillPolys, planeD, layer, infillSpacing, infillAngle,
                                              isLastLayer, Unproject, settings.BeadWidth);
            else if (settings.InfillPattern != InfillPattern.None
                     && !Lightning.LightningPlanner.IsFormboundPattern(settings.InfillPattern))
                InfillGenerator.Emit(fillPolys, planeD, layer, infillSpacing, infillAngle, Unproject);
            else
            {
                // Target Support with no paint trees this layer → clean shells.
                goto ShellPath;
            }

            // Infill moves need the plane normal for tool orientation on tilted layers.
            for (int i = firstInfillMove; i < layer.Moves.Count; i++)
                layer.Moves[i] = layer.Moves[i] with { Normal = normal };

            EmitTree();
            return new List<ContourTrack>();
            }
        }

        if (settings.XBracingEnabled && lightningPlan is not null)
        {
            var fillPolys = FilterFillPolys(insetContours, settings.SlicingMode == SlicingMode.Surface);
            if (fillPolys.Count > 0)
            {
                int first = layer.Moves.Count;
                Lightning.LightningGenerator.EmitLightning(fillPolys, lightningPlan, planeD, layer,
                    settings.BeadWidth, settings.LightningTipLoopRadiusMm, Unproject, prevEnd,
                    localSupportOnly: targetSel && hasPaintTrees);
                for (int i = first; i < layer.Moves.Count; i++)
                    layer.Moves[i] = layer.Moves[i] with { Normal = normal };
                EmitTree();
                return new List<ContourTrack>();
            }
        }

        ShellPath:
        var tracks = AssignSeams(insetContours, prevTracks, seamOrigin2d, seamDir2d);
        EmitContours(tracks.Select(t => (IEnumerable<Vector2>)t.Contour),
            origin, u, v, normal, layer);
        EmitTree();
        return tracks;
    }

    // Unprojects 2D plane-local contours to 3D and emits toolpath moves.
    private static void EmitContours(
        IEnumerable<IEnumerable<Vector2>> contours,
        Vector3 origin, Vector3 u, Vector3 v,
        Vector3 normal,
        ToolpathLayer layer)
    {
        var lastPos = new Vector3(float.NaN);
        foreach (var c in contours)
        {
            Vector3? first = null;
            Vector3 prev = default;
            int count = 0;
            foreach (var p2d in c)
            {
                var p3d = origin + p2d.X * u + p2d.Y * v;
                if (count == 0)
                {
                    first = p3d;
                    if (!float.IsNaN(lastPos.X))
                        layer.Moves.Add(new ToolpathMove(lastPos, p3d, MoveKind.Travel) { Normal = normal });
                }
                else
                {
                    layer.Moves.Add(new ToolpathMove(prev, p3d, MoveKind.Extrude) { Normal = normal });
                }
                prev = p3d; count++;
            }
            if (count > 2 && first.HasValue)
            {
                // Always close the loop. Clipper2 polygons have a genuine last→first edge
                // that can be longer than 1mm, so capping on distance caused a visible gap.
                // Only skip the closing move when the gap is effectively zero (first == last).
                float gapSq = (prev - first.Value).LengthSquared();
                if (gapSq > 1e-8f)
                    layer.Moves.Add(new ToolpathMove(prev, first.Value, MoveKind.Extrude) { Normal = normal });
                lastPos = first.Value;
            }
            else if (count > 0)
            {
                lastPos = prev;
            }
        }
    }

    // -- Intersection / segment collection (3D) --------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void TryEdge(
        Vector3 a, Vector3 b,
        float da, float db,
        Span<Vector3> pts, ref int count)
    {
        if (count >= 2) return;
        if (da * db >= 0f) return;
        float t = da / (da - db);
        pts[count++] = a + t * (b - a);
    }

    // -- Contour chaining (2D) -------------------------------------------------

    // Greedy nearest-endpoint walk — identical logic to PlanarSlicer.ChainByProximity.
    private static List<List<Vector2>> ChainByProximity(List<(Vector2 A, Vector2 B)> segs)
        => ChainGrid(segs);

    private static List<List<Vector2>> ChainGrid(List<(Vector2 A, Vector2 B)> segs)
    {
        int n = segs.Count;
        var used     = new bool[n];
        var contours = new List<List<Vector2>>();
        // Endpoint spatial hash — the full O(n) scan per step made this O(n²),
        // which never finished on dense Multi-Planar sections (V80 drone hang).
        var grid = new SegmentEndpointGrid(segs);

        for (int start = 0; start < n; start++)
        {
            if (used[start]) continue;
            used[start] = true;

            var chain = new List<Vector2> { segs[start].A, segs[start].B };

            while (true)
            {
                int bi = grid.FindNearest(chain[^1], used, out bool flip, out float best);
                if (bi < 0 || best > 1.0f) break;

                used[bi] = true;
                chain.Add(flip ? segs[bi].A : segs[bi].B);
            }

            if (chain.Count >= 3)
                contours.Add(chain);
        }

        return contours;
    }

    // -- Clipper2 contour offset (2D) ------------------------------------------

    private static List<List<Vector2>> InsetContour2D(IReadOnlyList<Vector2> contour, float delta)
    {
        var path = new PathD(contour.Count);
        foreach (var p in contour)
            path.Add(new PointD(p.X, p.Y));
        var result = Clipper.InflatePaths(
            new PathsD { path }, -delta,
            JoinType.Miter, EndType.Polygon, miterLimit: 3.0);
        return result
            .Select(r => r.Select(p => new Vector2((float)p.x, (float)p.y)).ToList())
            .ToList();
    }

    // -- Douglas-Peucker simplification (2D) -----------------------------------

    private static List<Vector2> SimplifyContour2D(List<Vector2> pts, float tolerance)
    {
        int n = pts.Count;
        if (n <= 3) return pts;
        float tolSq = tolerance * tolerance;
        var keep = new bool[n];
        keep[0] = keep[n - 1] = true;
        DPReduce2D(pts, 0, n - 1, tolSq, keep);
        var result = new List<Vector2>(n);
        for (int i = 0; i < n; i++)
            if (keep[i]) result.Add(pts[i]);
        return result.Count >= 3 ? result : pts;
    }

    private static void DPReduce2D(IReadOnlyList<Vector2> pts, int lo, int hi,
        float tolSq, bool[] keep)
    {
        if (hi - lo < 2) return;
        float abx = pts[hi].X - pts[lo].X, aby = pts[hi].Y - pts[lo].Y;
        float abLen2 = abx * abx + aby * aby;
        float maxDSq = 0; int maxI = lo + 1;
        for (int i = lo + 1; i < hi; i++)
        {
            float cx = pts[i].X - pts[lo].X, cy = pts[i].Y - pts[lo].Y;
            float dSq = abLen2 < 1e-10f
                ? cx * cx + cy * cy
                : (cx * aby - cy * abx) * (cx * aby - cy * abx) / abLen2;
            if (dSq > maxDSq) { maxDSq = dSq; maxI = i; }
        }
        if (maxDSq <= tolSq) return;
        keep[maxI] = true;
        DPReduce2D(pts, lo, maxI, tolSq, keep);
        DPReduce2D(pts, maxI, hi, tolSq, keep);
    }

    // -- Seam alignment (2D) ---------------------------------------------------

    private static void AlignSeam(
        List<Vector2> contour,
        Vector2 seamOrigin, Vector2 seamDir,
        ref Vector2 prevSeamXY)
    {
        if (contour.Count < 3) return;

        int n = contour.Count;
        int bestEdge;
        float bestT;

        if (float.IsNaN(prevSeamXY.X))
        {
            SeamEdgeFromRay(contour, seamOrigin, seamDir, out bestEdge, out bestT);
        }
        else
        {
            bestEdge = 0; bestT = 0f;
            float bestDist = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                var a = contour[i];
                var b = contour[(i + 1) % n];
                float t = ClosestT(a, b, prevSeamXY);
                var pt = a + t * (b - a);
                float d = Dist2(pt, prevSeamXY);
                if (d < bestDist) { bestDist = d; bestEdge = i; bestT = t; }
            }
        }

        var pa     = contour[bestEdge];
        var pb     = contour[(bestEdge + 1) % n];
        var seamPt = pa + bestT * (pb - pa);

        int insertAt = bestEdge + 1;
        if      (Dist2(seamPt, pa) < 1e-4f) insertAt = bestEdge;
        else if (Dist2(seamPt, pb) < 1e-4f) insertAt = (bestEdge + 1) % n;
        else                                contour.Insert(insertAt, seamPt);

        if (insertAt % contour.Count != 0)
        {
            var rotated = new List<Vector2>(contour.Count);
            rotated.AddRange(contour.GetRange(insertAt, contour.Count - insertAt));
            rotated.AddRange(contour.GetRange(0, insertAt));
            contour.Clear();
            contour.AddRange(rotated);
        }

        prevSeamXY = contour[0];
    }

    private static void SeamEdgeFromRay(
        List<Vector2> contour, Vector2 seamOrigin, Vector2 seamDir,
        out int edge, out float t)
    {
        var   rayDir   = -seamDir;
        float bestRayT = float.MaxValue;
        edge = 0; t = 0f;

        for (int i = 0; i < contour.Count; i++)
        {
            var a = contour[i];
            var b = contour[(i + 1) % contour.Count];
            if (RaySegment(seamOrigin, rayDir, a, b, out float rayT, out float segS) && rayT < bestRayT)
            {
                bestRayT = rayT; edge = i; t = segS;
            }
        }
    }

    private static bool RaySegment(Vector2 origin, Vector2 dir, Vector2 a, Vector2 b,
        out float t, out float s)
    {
        var   ab  = b - a;
        float den = dir.X * ab.Y - dir.Y * ab.X;
        if (MathF.Abs(den) < 1e-9f) { t = s = 0f; return false; }
        var ao = a - origin;
        t = (ao.X * ab.Y - ao.Y * ab.X) / den;
        s = (ao.X * dir.Y - ao.Y * dir.X) / den;
        return t > -1e-4f && s >= -1e-4f && s <= 1f + 1e-4f;
    }

    // -- Topology-aware seam assignment (2D) -----------------------------------

    private const float OverlapThreshold = 0.05f;

    private static List<ContourTrack> AssignSeams(
        List<List<Vector2>> contours,
        List<ContourTrack> prevTracks,
        Vector2 seamOrigin, Vector2 seamDir)
    {
        var tracks = new List<ContourTrack>(contours.Count);
        foreach (var raw in contours)
        {
            var contour = new List<Vector2>(raw);

            float bestScore = 0f;
            ContourTrack? bestParent = null;
            foreach (var prev in prevTracks)
            {
                float score = OverlapScore(prev.Contour, contour);
                if (score > bestScore) { bestScore = score; bestParent = prev; }
            }

            Vector2 seamRef = (bestParent != null && bestScore >= OverlapThreshold)
                ? bestParent.SeamXY
                : new Vector2(float.NaN, float.NaN);

            AlignSeam(contour, seamOrigin, seamDir, ref seamRef);
            tracks.Add(new ContourTrack(contour, seamRef));
        }
        return tracks;
    }

    private static float OverlapScore(List<Vector2> a, List<Vector2> b)
    {
        int aInB = 0, bInA = 0;
        foreach (var p in a) if (PointInPolygon(p, b)) aInB++;
        foreach (var p in b) if (PointInPolygon(p, a)) bInA++;
        float rA = a.Count > 0 ? (float)aInB / a.Count : 0f;
        float rB = b.Count > 0 ? (float)bInA / b.Count : 0f;
        return MathF.Max(rA, rB);
    }

    // -- Geometry helpers ------------------------------------------------------

    private static bool PointInPolygon(Vector2 p, List<Vector2> poly)
    {
        int n = poly.Count;
        bool inside = false;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            var pi = poly[i]; var pj = poly[j];
            if ((pi.Y > p.Y) != (pj.Y > p.Y) &&
                p.X < (pj.X - pi.X) * (p.Y - pi.Y) / (pj.Y - pi.Y) + pi.X)
                inside = !inside;
        }
        return inside;
    }

    private static float SignedArea(List<Vector2> poly)
    {
        float area = 0f;
        int n = poly.Count;
        for (int i = 0; i < n; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % n];
            area += a.X * b.Y - b.X * a.Y;
        }
        return area * 0.5f;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Dist2(Vector2 a, Vector2 b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    private static float ClosestT(Vector2 a, Vector2 b, Vector2 p)
    {
        var ab = b - a;
        float d = ab.LengthSquared();
        if (d < 1e-10f) return 0f;
        return Math.Clamp(Vector2.Dot(p - a, ab) / d, 0f, 1f);
    }

    // -- Per-contour seam tracking ---------------------------------------------

    private sealed class ContourTrack(List<Vector2> contour, Vector2 seamXY)
    {
        public readonly List<Vector2> Contour = contour;
        public readonly Vector2 SeamXY = seamXY;
    }
}
