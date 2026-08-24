using System.Numerics;
using System.Runtime.CompilerServices;
using System.Linq;
using Clipper2Lib;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing;

/// <summary>
/// Horizontal planar slicer. Intersects triangle meshes with Z-planes and chains
/// the resulting segments into ordered contours, then emits extrude + travel moves.
/// </summary>
public static class PlanarSlicer
{
    // -- Public entry point ----------------------------------------------------

    /// <summary>
    /// Slices all provided meshes and returns a <see cref="Toolpath"/>.
    /// </summary>
    /// <param name="meshes">
    ///   Flat (non-indexed) triangle soups in world space. Each entry is an array
    ///   of positions where every 3 consecutive entries form one triangle.
    /// </param>
    /// <param name="settings">Slice parameters.</param>
    public static Toolpath Slice(
        IReadOnlyList<Vector3[]> meshes,
        SliceSettings settings,
        Action<float>? progress = null)
    {
        s_droppedContours.Clear();

        // -- Compute Z + XY extents across all meshes -------------------------
        float zMin = float.MaxValue, zMax = float.MinValue;
        float xMin = float.MaxValue, xMax = float.MinValue;
        float yMin = float.MaxValue, yMax = float.MinValue;
        foreach (var verts in meshes)
            foreach (var v in verts)
            {
                if (v.Z < zMin) zMin = v.Z; if (v.Z > zMax) zMax = v.Z;
                if (v.X < xMin) xMin = v.X; if (v.X > xMax) xMax = v.X;
                if (v.Y < yMin) yMin = v.Y; if (v.Y > yMax) yMax = v.Y;
            }

        if (zMax <= zMin) return new Toolpath();

        // Seam ray: fired from outside the mesh along SeamDirection.
        // Used only to initialise the arc-length seam parameter on the first layer.
        var sd = settings.SeamDirection;
        float sdLen = sd.Length();
        if (sdLen < 1e-6f) sd = new Vector2(0f, 1f); else sd /= sdLen;
        float cx    = (xMin + xMax) * 0.5f;
        float cy    = (yMin + yMax) * 0.5f;
        float reach = (xMax - xMin + yMax - yMin) + 10f;
        var seamOrigin = new Vector2(cx + sd.X * reach, cy + sd.Y * reach);

        float[] zPositions = settings.AdaptiveLayerHeight
            ? AdaptiveLayerHeights.ComputeZPositions(meshes, zMin, zMax,
                  settings.FirstLayerHeight, settings.MinLayerHeight,
                  settings.LayerHeight, settings.AdaptiveQuality)
            : BuildUniformZPositions(zMin, zMax, settings.FirstLayerHeight, settings.LayerHeight);

        // Tree Support must reach the print bed (Layer 1). If the mesh floats above
        // Z=0, prepend buffer layers so foundation is L1… and the part shifts up —
        // never require "layer −1".
        bool hasTreePaintEarly = PaintSupportStyleUtil.HasTreePaint(settings.PaintMarks);
        if (hasTreePaintEarly)
            zPositions = PrependTreeBedFoundationLayers(zPositions, zMin, settings);

        var toolpath     = new Toolpath();
        ZigZagEnclosedKeptCount = 0;
        var prevTracks   = new List<ContourTrack>();
        ToolpathLayer? prevLayer = null;

        // ── Lightning Bridge pre-pass: contours for every layer first (pass A),
        //    then the top-down finger plan (pass B). Pass C below reuses the cached
        //    contours verbatim so plan and geometry cannot drift.
        List<(List<List<Vector2>> Contours, List<bool> Closed)>? lightningCache = null;
        Lightning.LightningPlan? lightningPlan = null;
        TreeSupport.TreeSupportPlan? treePlan = null;
        bool hasFormboundPaint = PaintSupportStyleUtil.HasFormboundPaint(settings.PaintMarks);
        bool hasTreePaint = hasTreePaintEarly;
        // Target Support Selections: Formbound ONLY when the user painted Support
        // (Formbound style). The FILL PATTERN dropdown alone must not scar the
        // whole part — rest of toolpath stays normal shells.
        bool formboundActive = settings.LightningTargetSupportSelections
            ? hasFormboundPaint
            : (Lightning.LightningPlanner.IsFormboundPattern(settings.InfillPattern)
               || hasFormboundPaint);
        // Zig-zag single-skin prints open faces only — X / tree dual-wall emits stay
        // off (they would re-create a closed back panel). Formbound DOES plan here:
        // it plans over the CLOSED wall rings (pre single-skin extract) and its
        // fingers are spliced into the open path as detours protruding into the
        // wall interior — the backside of the printed skin.
        bool needLightning = !settings.ZigZagSeam
            ? (formboundActive || settings.XBracingEnabled || hasTreePaint)
            : formboundActive;
        if (needLightning)
        {
            bool surfaceMode = settings.SlicingMode == SlicingMode.Surface;
            lightningCache = new(zPositions.Length);
            var fillPolysPerLayer = new List<List<List<Vector2>>>(zPositions.Length);
            var heights = new List<float>(zPositions.Length);
            for (int zi = 0; zi < zPositions.Length; zi++)
            {
                if ((zi & 15) == 0) progress?.Invoke(0.3f * zi / zPositions.Length);
                var cached = ComputeInsetContours(meshes, zPositions[zi], settings);
                lightningCache.Add(cached);
                fillPolysPerLayer.Add(FilterFillPolys(cached.Contours, cached.Closed, surfaceMode));
                // Height between planes (bed buffer first layer uses FirstLayerHeight).
                float prevPlane = zi == 0
                    ? zPositions[0] - MathF.Max(settings.FirstLayerHeight, settings.LayerHeight)
                    : zPositions[zi - 1];
                heights.Add(MathF.Max(0.1f, zPositions[zi] - prevPlane));
            }
            // The oracle probes just BELOW the plane: the demanding solid occupies
            // the layer beneath it, and a grazing plane itself is ambiguous.
            var meshTester = new Lightning.MeshInsideTester(meshes);
            float halfBand = settings.LightningTargetSupportSelections
                ? MathF.Max(settings.LayerHeight * 1.75f, settings.BeadWidth * 1.5f)
                : MathF.Max(settings.LayerHeight * 0.75f, settings.BeadWidth * 0.75f);
            Func<int, (Vector3 Origin, Vector3 Normal, Vector3 U, Vector3 V)> frameOf =
                li => (new Vector3(0f, 0f, zPositions[li]), Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY);

            if (formboundActive)
            {
                // Only Formbound-style paint feeds Lightning; Tree marks are separate.
                var formDemand = ToolpathPaintFilter.ProjectBridgeMarks(
                    settings.PaintMarks, zPositions.Length, frameOf,
                    halfBandMm: halfBand,
                    targetSupportSelectionsOnly: settings.LightningTargetSupportSelections,
                    styleFilter: PaintSupportStyleUtil.IsFormbound);
                // Caller (BuildSliceSettings) should already force InfillPattern from paint.
                // If not, Build still runs but buttress vs bridge follows settings.InfillPattern.
                lightningPlan = Lightning.LightningPlanner.Build(fillPolysPerLayer, heights, settings,
                    solidAt: (li, p) => meshTester.IsInside(
                        new Vector3(p.X, p.Y, zPositions[li] - 0.4f * heights[li])),
                    manualDemand: formDemand);
            }
            else
                lightningPlan = new Lightning.LightningPlan(zPositions.Length);

            if (hasTreePaint && !settings.ZigZagSeam)
            {
                // Tree: pin demand to the painted tip only (target-support band).
                // Planner grows freestanding columns from layer 0 → tip regardless.
                var treeDemand = ToolpathPaintFilter.ProjectBridgeMarks(
                    settings.PaintMarks, zPositions.Length, frameOf,
                    halfBandMm: halfBand,
                    targetSupportSelectionsOnly: true,
                    styleFilter: PaintSupportStyleUtil.IsTree);
                treePlan = TreeSupport.TreeSupportPlanner.Build(
                    fillPolysPerLayer, heights, settings, treeDemand);
                if (treePlan is not null)
                {
                    int with = 0;
                    for (int i = 0; i < treePlan.Layers.Length; i++)
                        if (treePlan.Layers[i].Branches.Count > 0) with++;
                    System.Console.WriteLine(
                        $"[tree-support] planar: treePaint marks → plan branches on " +
                        $"{with}/{treePlan.Layers.Length} layers (must include bed L0)");
                }
            }

            if (settings.XBracingEnabled)
            {
                // Prefer fill polys; if a layer has no fill (open/surface shells),
                // fall back to raw inset contours so freestanding wall panels still brace.
                var polysForX = new List<List<List<Vector2>>>(zPositions.Length);
                for (int zi = 0; zi < zPositions.Length; zi++)
                {
                    var fill = fillPolysPerLayer[zi];
                    if (fill.Count > 0) polysForX.Add(fill);
                    else polysForX.Add(lightningCache![zi].Contours);
                }
                Lightning.XBracingPlanner.Apply(
                    lightningPlan, polysForX, zPositions, heights, settings);
            }

            // Generator oracles: SolidAt probes both sides of the plane (fresh
            // islands have material only above their first plane); SolidAtPlane
            // probes exactly at it (a real contour's interior is solid there).
            for (int li = 0; li < zPositions.Length; li++)
            {
                int cap = li;
                lightningPlan.Layers[li].SolidAt = p =>
                    meshTester.IsInside(new Vector3(p.X, p.Y, zPositions[cap] - 0.4f * heights[cap]))
                    || meshTester.IsInside(new Vector3(p.X, p.Y, zPositions[cap] + 0.4f * heights[cap]));
                lightningPlan.Layers[li].SolidAtPlane = p =>
                    meshTester.IsInside(new Vector3(p.X, p.Y, zPositions[cap]));
            }
        }

        // Cross-layer state for single-skin X hairpins (≥60% support from prior layer).
        Lightning.XBracingPlanner.OpenPathDetourState? xDetourState =
            settings.XBracingEnabled && settings.ZigZagSeam
                ? new Lightning.XBracingPlanner.OpenPathDetourState
                    { PartZMin = zMin, PartZMax = zMax }
                : null;

        for (int zi = 0; zi < zPositions.Length; zi++)
        {
            if ((zi & 15) == 0) progress?.Invoke(zi / (float)zPositions.Length);
            float z           = zPositions[zi];
            // Height relative to previous plane (or bed for the first buffer/mesh layer).
            float prevZ       = zi == 0
                ? MathF.Min(zMin, zPositions[0] - MathF.Max(settings.FirstLayerHeight, settings.LayerHeight))
                : zPositions[zi - 1];
            bool  isLastLayer = zi == zPositions.Length - 1;
            // Contiguous 0-based index among emitted layers (not plane skip index).
            var layer         = new ToolpathLayer(toolpath.Layers.Count, z)
                { Height = MathF.Max(0.1f, z - prevZ) };
            prevTracks = BuildLayer(meshes, z, settings, seamOrigin, sd, prevTracks, layer, isLastLayer,
                cachedContours: lightningCache?[zi],
                lightningPlan:  lightningPlan?.Layers[zi],
                treePlan:       treePlan?.Layers[zi],
                xDetourState:   xDetourState,
                prevEnd:        prevLayer is { Moves.Count: > 0 } pvl ? pvl.Moves[^1].To : null);

            if (layer.Moves.Count > 0)
            {
                // Insert a connecting move from the end of the previous layer to the
                // start of this one.  A large XY jump gets a travel (stop extrusion);
                // a small jump gets an extrude stitch (keep printing through the seam).
                if (prevLayer is { } pl && pl.Moves.Count > 0)
                {
                    var endPos   = pl.Moves[^1].To;
                    var startPos = layer.Moves[0].From;
                    float dx = endPos.X - startPos.X;
                    float dy = endPos.Y - startPos.Y;
                    float xyDist = MathF.Sqrt(dx * dx + dy * dy);

                    if (xyDist > settings.BeadWidth)
                    {
                        layer.Moves.Insert(0, new ToolpathMove(endPos, startPos, MoveKind.Travel)
                            { IsLayerChange = true });
                    }
                    else if (xyDist > 0.01f || MathF.Abs(endPos.Z - startPos.Z) > 0.01f)
                    {
                        // Close enough to stitch without stopping extrusion.
                        layer.Moves.Insert(0, new ToolpathMove(endPos, startPos, MoveKind.Extrude) { IsLayerStitch = true });
                    }
                    // else: identical position (perfect seam alignment) — no move needed.
                }

                toolpath.Layers.Add(layer);
                prevLayer = layer;
            }
        }

        if (settings.PaintMarks.Count > 0)
            ToolpathPaintFilter.ApplyRemovals(toolpath, settings.PaintMarks);

        if (lightningPlan is not null)
            toolpath.FormboundStats = lightningPlan.ToStats();

        if (xDetourState is { FormboundDetours: > 0 })
            System.Console.WriteLine(
                $"[formbound] zig-zag single-skin: {xDetourState.FormboundDetours} fin detour(s) " +
                "spliced into the open path (wall-interior / backside)");

        // Brim is the LAST toolpath step: its footprint is built from the actual
        // layer-0 extrude segments, so X-bracing detours and pattern bulges are
        // enclosed by the offset loops.
        // Structural supports first — brim then wraps the final wall footprint.
        StructuralSupportPlanner.Apply(toolpath, settings);
        DumpBrimFootprint(meshes, zPositions, settings);
        BrimPlanner.Apply(toolpath, settings);

        // Last, so brim and support moves are covered too: flow must follow the REAL layer
        // thickness. Adaptive layer height changes Z spacing and nothing was adjusting RPM
        // for it, so thin layers were given a full nominal layer's worth of material.
        Effects.LayerHeightFlowPostProcessor.Apply(toolpath, settings);

        AttachZigZagWarning(toolpath);
        ReportDroppedContours();
        return toolpath;
    }

    // -- Z position helpers ----------------------------------------------------

    private static float[] BuildUniformZPositions(float zMin, float zMax, float firstH, float layerH)
    {
        var positions = new List<float>();
        for (float z = zMin + firstH; z < zMax - 1e-4f; z += layerH)
            positions.Add(z);
        return [.. positions];
    }

    /// <summary>
    /// When Tree Support is active and the mesh bottom sits above the print bed (Z=0),
    /// prepend slice planes from the bed up to the first mesh plane so tree foundation
    /// occupies Layer 1… and the part never needs "negative" layers.
    /// </summary>
    private static float[] PrependTreeBedFoundationLayers(
        float[] zPositions, float meshZMin, SliceSettings settings)
    {
        if (zPositions.Length == 0) return zPositions;

        // Print bed at Z=0. If the mesh already rests on/below the bed, no buffer.
        const float bedZ = 0f;
        float layerH = MathF.Max(settings.LayerHeight, 0.1f);
        float firstH = MathF.Max(settings.FirstLayerHeight, layerH);
        float meshFirst = zPositions[0];

        // Only when the first slice is clearly above the bed.
        if (meshFirst <= bedZ + firstH * 0.5f)
            return zPositions;

        var buffer = new List<float>(64);
        float z = bedZ + firstH;
        // Safety: never insert more than ~2 m of foundation at this layer height.
        int maxBuf = Math.Clamp((int)MathF.Ceiling(2500f / layerH), 1, 800);
        while (z < meshFirst - 0.25f * layerH && buffer.Count < maxBuf)
        {
            buffer.Add(z);
            z += layerH;
        }

        if (buffer.Count == 0) return zPositions;

        var merged = new float[buffer.Count + zPositions.Length];
        buffer.CopyTo(merged, 0);
        zPositions.CopyTo(merged, buffer.Count);
        System.Console.WriteLine(
            $"[tree-support] bed foundation buffer: +{buffer.Count} layers " +
            $"(Z {merged[0]:0.#} → mesh first {meshFirst:0.#} mm, meshZMin={meshZMin:0.#}) " +
            $"→ Layer 1 starts on the bed");
        return merged;
    }

    // -- Layer construction ----------------------------------------------------

    private static List<ContourTrack> BuildLayer(
        IReadOnlyList<Vector3[]> meshes,
        float z,
        SliceSettings settings,
        Vector2 seamOrigin,
        Vector2 seamDir,
        List<ContourTrack> prevTracks,
        ToolpathLayer layer,
        bool isLastLayer = false,
        (List<List<Vector2>> Contours, List<bool> Closed)? cachedContours = null,
        Lightning.LightningLayerPlan? lightningPlan = null,
        TreeSupport.TreeSupportLayerPlan? treePlan = null,
        Lightning.XBracingPlanner.OpenPathDetourState? xDetourState = null,
        Vector3? prevEnd = null)
    {
        bool surfaceMode = settings.SlicingMode == SlicingMode.Surface;

        List<List<Vector2>> insetContours;
        List<bool> insetClosed;
        List<(Vector2 pos, Vector3 normal)>? normalLookup = null;

        if (cachedContours is { } cc)
        {
            // Lightning pre-pass already computed this layer's contours — reuse them
            // verbatim so the plan and the emitted geometry can never drift apart.
            // Clone lists: single-skin / X detours mutate contours in place.
            insetContours = cc.Contours.Select(c => new List<Vector2>(c)).ToList();
            insetClosed = new List<bool>(cc.Closed);
        }
        else
        {
            normalLookup = settings.OverhangOrientation
                ? new List<(Vector2 pos, Vector3 normal)>()
                : null;
            (insetContours, insetClosed) = ComputeInsetContours(meshes, z, settings, normalLookup);
        }
        // Empty mesh cut: still emit freestanding tree columns (bed foundation under
        // overhangs where lower planes miss the solid).
        if (insetContours.Count == 0)
        {
            EmitTreeSupportIfAny(treePlan, z, layer, settings, partFillPolys: null);
            return new List<ContourTrack>();
        }

        return BuildLayerBody(settings, layer, z, isLastLayer, insetContours, insetClosed,
            normalLookup, seamOrigin, seamDir, prevTracks, lightningPlan, treePlan, xDetourState, prevEnd);
    }
    /// <summary>
    /// Diagnostic: writes layer 0's MESH cross-section to CSV when
    /// <c>MASSIVESLICER_BRIM_DUMP</c> names a path. The brim footprint is currently derived from
    /// the layer-0 TOOLPATH, which drags every internal wall, seam and unfilled gap into it; this
    /// dumps what the MESH alone says the first layer looks like, so the two can be compared on a
    /// real part instead of argued about. Off unless the variable is set.
    /// </summary>
    private static void DumpBrimFootprint(
        IReadOnlyList<Vector3[]> meshes, float[] zPositions, SliceSettings settings)
    {
        string? path = Environment.GetEnvironmentVariable("MASSIVESLICER_BRIM_DUMP");
        if (string.IsNullOrWhiteSpace(path) || zPositions.Length == 0) return;
        try
        {
            var (contours, closed) = ComputeInsetContours(meshes, zPositions[0], settings);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("contour,closed,points,signed_area,x,y");
            for (int c = 0; c < contours.Count; c++)
            {
                var pts = contours[c];
                double a2 = 0;
                for (int i = 0; i < pts.Count; i++)
                {
                    var p = pts[i]; var q = pts[(i + 1) % pts.Count];
                    a2 += p.X * q.Y - q.X * p.Y;
                }
                bool isClosed = c < closed.Count && closed[c];
                foreach (var p in pts)
                    sb.AppendLine($"{c},{isClosed},{pts.Count},{a2 / 2:0.###},{p.X:0.###},{p.Y:0.###}");
            }
            System.IO.File.WriteAllText(path, sb.ToString());
            System.Console.WriteLine(
                $"[brim-dump] layer0 z={zPositions[0]:0.##} contours={contours.Count} -> {path}");
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"[brim-dump] failed: {ex.Message}");
        }
    }



    /// <summary>
    /// Stages 1–3 of layer construction: mesh∩plane segments → chained contours →
    /// nesting/orientation/offset. Extracted so the Lightning pre-pass can compute
    /// (and cache) every layer's contours before any moves are emitted.
    /// </summary>
    private static (List<List<Vector2>> Contours, List<bool> Closed) ComputeInsetContours(
        IReadOnlyList<Vector3[]> meshes,
        float z,
        SliceSettings settings,
        List<(Vector2 pos, Vector3 normal)>? normalLookup = null)
    {
        bool surfaceMode = settings.SlicingMode == SlicingMode.Surface;
        var empty = (new List<List<Vector2>>(), new List<bool>());

        var perMeshSegs = new List<List<(Vector2 A, Vector2 B)>>(meshes.Count);
        foreach (var verts in meshes)
        {
            var segs = new List<(Vector2, Vector2)>(64);
            // Face normals only when the user explicitly enables overhang orientation in Surface mode.
            CollectSegments(verts, z, segs, normalLookup, surfaceMode && settings.OverhangOrientation);
            if (segs.Count > 0) perMeshSegs.Add(segs);
        }
        if (perMeshSegs.Count == 0) return empty;

        // ── Stage 2: chain by endpoint proximity (per mesh) ─────────────────
        // Adjacent segments from a manifold mesh share an endpoint to floating-point
        // precision. A greedy nearest-neighbour walk is sufficient and avoids all the
        // graph/degree-3+/pruning machinery that caused corner artifacts.
        var rawContours = new List<List<Vector2>>();
        foreach (var segs in perMeshSegs)
            rawContours.AddRange(ChainByProximity(segs));

        // Discard anything too small to extrude before it reaches nesting/offset.
        DropUnprintableContours(rawContours, z, settings.BeadWidth);

        // ── Stage 3: nesting depth + contour offset ──────────────────────────
        if (rawContours.Count == 0) return empty;

        // Determine nesting depth via point-in-polygon so outer (even depth) and
        // hole (odd depth) contours can be distinguished, then orient and offset each.
        // Clipper2 InflatePaths contracts a CCW path with -delta and a CW path with
        // +delta, so holes need flipped delta to move their boundary into the material.
        //
        // Robustness: vote with three sample points (first, middle, last vertex) rather
        // than a single vertex so that a vertex landing on another contour's boundary
        // (common in non-manifold / disconnected-shell models at shared Z levels)
        // doesn't flip the depth count for the whole contour.
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
        var insetClosed   = new List<bool>(nc);
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
                {
                    // Open mesh boundary chains have a large gap between first and last vertex.
                    // Use the same 1mm² threshold as ChainByProximity to detect closure.
                    bool closed = Dist2(ol[0], ol[^1]) <= 1.0f;
                    insetContours.Add(simpTol > 0f ? SimplifyContour2D(ol, simpTol) : ol);
                    insetClosed.Add(closed);
                }
            }
            else
            {
                float delta   = isHole ? -halfBead : halfBead;
                var   results = InsetContour2D(oriented, delta);
                if (results.Count >= 1)
                {
                    foreach (var r in results)
                        if (r.Count >= 3)
                        {
                            insetContours.Add(simpTol > 0f ? SimplifyContour2D(r, simpTol) : r);
                            insetClosed.Add(true); // Clipper2 output is always a closed polygon
                        }
                }
            }
        }
        return (insetContours, insetClosed);
    }

    private static List<ContourTrack> BuildLayerBody(
        SliceSettings settings, ToolpathLayer layer, float z, bool isLastLayer,
        List<List<Vector2>> insetContours, List<bool> insetClosed,
        List<(Vector2 pos, Vector3 normal)>? normalLookup,
        Vector2 seamOrigin, Vector2 seamDir, List<ContourTrack> prevTracks,
        Lightning.LightningLayerPlan? lightningPlan,
        TreeSupport.TreeSupportLayerPlan? treePlan = null,
        Lightning.XBracingPlanner.OpenPathDetourState? xDetourState = null,
        Vector3? prevEnd = null)
    {
        bool surfaceMode = settings.SlicingMode == SlicingMode.Surface;

        // Zig-zag single-skin mode: closed wall loops print as ONE long open face
        // (no back panel). Even layers A→B, odd layers B→A, lift between layers.
        // Formbound dual-wall region emit would re-create a back panel — skip it.
        // X-bracing uses open-path hairpin detours instead (see below).
        bool singleSkinZigZag = settings.ZigZagSeam;
        // Keep pre-extract wall rings so X hairpins can clamp depth to real thickness
        // (75mm into a 25mm wall was shooting through as exterior spikes).
        List<List<Vector2>>? wallRingsForX = null;
        if (singleSkinZigZag)
        {
            if (settings.XBracingEnabled)
            {
                wallRingsForX = new List<List<Vector2>>(insetContours.Count);
                for (int i = 0; i < insetContours.Count; i++)
                {
                    bool isClosed = i >= insetClosed.Count || insetClosed[i];
                    wallRingsForX.Add(isClosed && insetContours[i].Count >= 3
                        ? new List<Vector2>(insetContours[i])
                        : new List<Vector2>());
                }
            }
            ExtractSingleSkinOpenFaces(insetContours, insetClosed, settings.BeadWidth);
            if (!settings.ZigZagAllowSameLayerTravel)
                KeepLongestOpenFaceOnly(insetContours, insetClosed);
        }

        // Single-skin X-bracing: hairpin detours into the wall along the open path.
        // Depth grows ≤ MaxStep/layer; each hairpin ≥60% supported by previous layer.
        if (settings.XBracingEnabled && singleSkinZigZag)
        {
            // layer.Index == 0 is the first slice plane (on the bed). Do not use
            // absolute world Z — the mesh bottom is usually zMin ≫ 0 on the cell bed.
            int hp = Lightning.XBracingPlanner.ApplyOpenPathDetours(
                insetContours, insetClosed, z, layer.Height, settings, xDetourState,
                isBedLayer: layer.Index == 0,
                wallSolidRings: wallRingsForX);
            if (hp > 0 && layer.Index == 0 && xDetourState is not null)
            {
                float maxD = 0f;
                foreach (var h in xDetourState.PrevList)
                    maxD = MathF.Max(maxD, h.Depth);
                System.Console.WriteLine(
                    $"[x-bracing] zig-zag BED layer hairpins={hp} maxPinDepth={maxD:0.#} " +
                    $"(want={settings.XBracingDepthMm:0.#} span={settings.XBracingSpanMm:0.#} z={z:0.#})");
            }
        }

        // Target Support Selections: Formbound only when this layer has paint trees.
        // Otherwise fall through to normal shells so unselected geometry is untouched.
        bool targetSel = settings.LightningTargetSupportSelections;
        bool hasPaintTrees = lightningPlan is not null
            && lightningPlan.Trees.Any(t =>
                (t.Manual || t.PaintColumn) && !lightningPlan.DroppedTrees.Contains(t.Id));
        bool formboundEmit = targetSel
            ? hasPaintTrees
            : (Lightning.LightningPlanner.IsFormboundPattern(settings.InfillPattern)
               || (lightningPlan is not null
                   && PaintSupportStyleUtil.HasFormboundPaint(settings.PaintMarks)
                   && hasPaintTrees));

        // Zig-zag single-skin Formbound: planned fingers splice into the open wall
        // path as dual-wall detours protruding into the WALL INTERIOR — the backside
        // of the printed skin — under the overhang demand the planner found above.
        // (Applied after X-bracing so the X pin state marches on the clean path.)
        if (singleSkinZigZag && formboundEmit && lightningPlan is not null)
        {
            int fins = Lightning.XBracingPlanner.ApplyFormboundOpenPathDetours(
                insetContours, insetClosed, lightningPlan, settings.BeadWidth);
            if (fins > 0 && xDetourState is not null)
                xDetourState.FormboundDetours += fins;
        }

        // ── Infill mode: replace shell contours with a continuous fill pattern.
        // Surface mode fills across CLOSED boundary chains (open chains can't bound
        // a region, so layers without any closed chain keep their boundary paths).
        // Zig-zag single-skin always takes the shell path below (never Formbound fill).
        if (!singleSkinZigZag && (settings.InfillPattern != InfillPattern.None || formboundEmit))
        {
            var fillPolys = FilterFillPolys(insetContours, insetClosed, surfaceMode);
            // X-bracing / Formbound need a region; open surface shells often leave
            // fillPolys empty — fall back to all inset contours so braces still emit.
            if (fillPolys.Count == 0
                && (settings.XBracingEnabled || formboundEmit))
                fillPolys = insetContours.Where(c => c.Count >= 3).ToList();

            if (fillPolys.Count > 0)
            {
                float baseAngle = settings.InfillAngleDeg;
                float angle = settings.InfillPattern switch
                {
                    InfillPattern.Grid          => baseAngle + (layer.Index % 2) * 90f,
                    InfillPattern.GhostMeshGrid => baseAngle + (layer.Index % 2) * 90f,
                    InfillPattern.Triangle      => baseAngle + (layer.Index % 3) * 60f,
                    _                           => baseAngle,
                };
                float spacing = settings.InfillSpacingMm > 0f
                    ? settings.InfillSpacingMm
                    : settings.BeadWidth;
                if (formboundEmit
                    || (settings.XBracingEnabled && lightningPlan is not null
                        && !targetSel)) // X alone may still use lightning emit
                {
                    // Target Support: local notches only — no global fillet/weld.
                    Lightning.LightningGenerator.EmitLightning(fillPolys, lightningPlan, z, layer,
                        settings.BeadWidth, settings.LightningTipLoopRadiusMm, null, prevEnd,
                        localSupportOnly: targetSel);
                    EmitTreeSupportIfAny(treePlan, z, layer, settings, fillPolys);
                    return new List<ContourTrack>();
                }
                if (settings.XBracingEnabled && lightningPlan is not null && targetSel
                    && !formboundEmit)
                {
                    // X-bracing with target-sel but no paint formbound this layer:
                    // still need X emit if X trees exist — without local-only strip of paint.
                    bool hasX = lightningPlan.Trees.Any(t =>
                        !lightningPlan.DroppedTrees.Contains(t.Id));
                    if (hasX)
                    {
                        Lightning.LightningGenerator.EmitLightning(fillPolys, lightningPlan, z, layer,
                            settings.BeadWidth, settings.LightningTipLoopRadiusMm, null, prevEnd,
                            localSupportOnly: false);
                        EmitTreeSupportIfAny(treePlan, z, layer, settings, fillPolys);
                        return new List<ContourTrack>();
                    }
                }
                if (settings.InfillPattern == InfillPattern.GhostMeshGrid)
                    InfillGenerator.EmitGhostMesh(fillPolys, z, layer, spacing, angle, isLastLayer,
                                                  insetStepMm: settings.BeadWidth);
                else if (settings.InfillPattern != InfillPattern.None
                         && !Lightning.LightningPlanner.IsFormboundPattern(settings.InfillPattern))
                    InfillGenerator.Emit(fillPolys, z, layer, spacing, angle);
                else
                {
                    // Formbound pattern under Target Support with no paint trees this
                    // layer, or tree-only: normal shells (geometry untouched).
                    goto ShellPath;
                }
                EmitTreeSupportIfAny(treePlan, z, layer, settings, fillPolys);
                return new List<ContourTrack>();
            }
        }

        // Shells + X-bracing (no zig-zag single-skin): notched perimeter via Lightning.
        // Under zig-zag single-skin, X dual-wall would rebuild a back panel — skip.
        if (!singleSkinZigZag && settings.XBracingEnabled && lightningPlan is not null)
        {
            var fillPolys = FilterFillPolys(insetContours, insetClosed, surfaceMode);
            if (fillPolys.Count == 0)
                fillPolys = insetContours.Where(c => c.Count >= 3).ToList();
            if (fillPolys.Count > 0)
            {
                Lightning.LightningGenerator.EmitLightning(fillPolys, lightningPlan, z, layer,
                    settings.BeadWidth, settings.LightningTipLoopRadiusMm, null, prevEnd,
                    localSupportOnly: targetSel && hasPaintTrees);
                EmitTreeSupportIfAny(treePlan, z, layer, settings, fillPolys);
                return new List<ContourTrack>();
            }
        }

        ShellPath:
        var guideXY = settings.SeamGuidePoints.Select(g => g.ToXY()).ToList();
        var tracks = AssignSeams(insetContours, insetClosed, prevTracks, seamOrigin, seamDir, guideXY);

        // Assign per-vertex normals for overhang orientation: aim the EXTRUDER AT
        // THE PREVIOUS LAYER'S BEAD. Tilt direction = this vertex's XY shift from
        // the nearest previous-layer path point; tilt angle = the local overhang
        // angle atan(shift / layerHeight), capped at Max tilt. Supported wall (no
        // shift) prints straight down. The old face-normal approach leaned the
        // nozzle the full Max tilt outward even on perfectly vertical wall; it
        // remains only as the fallback when no previous layer exists nearby.
        if (normalLookup != null && normalLookup.Count > 0)
        {
            float maxTiltRad = settings.MaxOverhangTiltDeg * (MathF.PI / 180f);
            var prevGrid = BuildPrevContourGrid(prevTracks);
            float lhN = MathF.Max(layer.Height, 0.1f);
            foreach (var track in tracks)
            {
                track.Normals = new List<Vector3>(track.Contour.Count);
                foreach (var pt in track.Contour)
                {
                    var fallback = ClampNormalTilt(NearestNormal(pt, normalLookup), maxTiltRad);
                    track.Normals.Add(OverhangNormalTowardPrevLayer(
                        pt, prevGrid, lhN, maxTiltRad, fallback));
                }
            }
        }

        ContourSeamPlanner.EmitOptimizedContours(tracks, z, layer, settings.ZigZagSeam, layer.Index);
        var partPolys = FilterFillPolys(insetContours, insetClosed, surfaceMode);
        if (partPolys.Count == 0)
            partPolys = insetContours.Where(c => c.Count >= 3).ToList();
        EmitTreeSupportIfAny(treePlan, z, layer, settings, partPolys);
        return tracks;
    }

    private static void EmitTreeSupportIfAny(
        TreeSupport.TreeSupportLayerPlan? treePlan,
        float z,
        ToolpathLayer layer,
        SliceSettings settings,
        List<List<Vector2>>? partFillPolys,
        float? minWorldZ = null)
    {
        if (treePlan is null || treePlan.Branches.Count == 0) return;
        // Floor at this layer's Z for planar (constant-Z); never extrude below bed.
        float floor = minWorldZ ?? z;
        TreeSupport.TreeSupportGenerator.Emit(
            treePlan, z, layer, settings.BeadWidth, partFillPolys,
            project: null, minWorldZ: floor);
    }

    /// <summary>
    /// Zig-zag single-skin: each closed wall loop becomes the longest open face only
    /// (front OR back, not both). Printing is one continuous line that reverses each
    /// layer — end of line → Z hop → reverse direction.
    /// <b>Ring-like</b> contours (columns, tubes, near-circular islands) stay fully
    /// closed so half-circle skins are not produced.
    /// Shared with <see cref="AngledPlanarSlicer"/> (Multi-Planar / Angled).
    /// </summary>
    /// <summary>
    /// Per-Slice() tally of enclosed contours the thin-wall guard kept closed under
    /// zig-zag (layers slice sequentially on one thread; reset at the top of each
    /// Slice pass, read at the end to attach a <see cref="Toolpath.Warnings"/> entry).
    /// </summary>
    [ThreadStatic] internal static int ZigZagEnclosedKeptCount;

    /// <summary>Adds the zig-zag/enclosed-model warning when the guard fired, and resets the tally.</summary>
    internal static void AttachZigZagWarning(Toolpath toolpath)
    {
        if (ZigZagEnclosedKeptCount <= 0) return;
        toolpath.Warnings.Add(
            $"Zig-zag seam is a single-wall mode: {ZigZagEnclosedKeptCount} enclosed contour(s) " +
            "were kept as closed loops instead of being cut open. For enclosed models use Seam mode \"Normal\".");
        ZigZagEnclosedKeptCount = 0;
    }

    internal static void ExtractSingleSkinOpenFaces(
        List<List<Vector2>> contours, List<bool> closed, float beadWidth)
    {
        for (int i = 0; i < contours.Count; i++)
        {
            if (i < closed.Count && !closed[i]) continue; // already open
            var c = contours[i];
            if (c.Count < 4) continue;

            // Columns / tubes / circular bumps: print the full closed loop.
            if (IsRingLikeContour(c))
                continue;

            // Thin-wall test: a wall panel's contour loop hugs the wall, so its mean
            // width (2·area/perimeter) stays within a few beads (walls up to ~4 beads
            // thick still single-skin — see ZigZagSingleSkinTest's 20mm wall / 6mm
            // bead). An ENCLOSED solid's perimeter ring encloses real area — mean
            // width in the tens-to-hundreds of mm — and skinning it amputates the
            // model (whole outline sections silently deleted). Keep those closed; the
            // caller reports it so the user learns zig-zag is a single-wall mode.
            if (AverageRingWidth(c) > beadWidth * 4f)
            {
                ZigZagEnclosedKeptCount++;
                continue;
            }

            var face = LongestOpenFace(c);
            if (face.Count < 2) continue;
            // Orient so left-of-travel points into the original closed wall (for X hairpins).
            OrientOpenFaceIntoPolygon(face, c);
            contours[i] = face;
            if (i < closed.Count) closed[i] = false;
        }
    }

    /// <summary>
    /// Mean width of a closed ring, 2·area/perimeter: ≈0 for an inset centreline loop,
    /// ≈ wall thickness for a thin-wall outline, and far larger for an enclosed solid.
    /// </summary>
    internal static float AverageRingWidth(IReadOnlyList<Vector2> ring)
    {
        int n = ring.Count;
        if (n > 2 && Dist2(ring[0], ring[^1]) < 1e-6f) n--;
        if (n < 3) return 0f;
        float area2 = 0f, perim = 0f;
        for (int i = 0; i < n; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % n];
            area2 += a.X * b.Y - b.X * a.Y;
            perim += Vector2.Distance(a, b);
        }
        return perim < 1e-3f ? 0f : MathF.Abs(area2) / perim; // (|area2|/2)·2/perim
    }

    /// <summary>
    /// True for near-circular / compact closed contours that should stay full rings
    /// under zig-zag (not split into a half-perimeter open skin).
    /// Thin elongated wall loops (high aspect ratio) return false → open face extract.
    /// </summary>
    internal static bool IsRingLikeContour(IReadOnlyList<Vector2> ring)
    {
        int n = ring.Count;
        if (n < 6) return false;
        // Drop duplicate closing vertex if present.
        if (Dist2(ring[0], ring[^1]) < 1e-6f)
            n--;
        if (n < 6) return false;

        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;
        float perim = 0f;
        for (int i = 0; i < n; i++)
        {
            var p = ring[i];
            if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
            var q = ring[(i + 1) % n];
            perim += Vector2.Distance(p, q);
        }
        float w = maxX - minX;
        float h = maxY - minY;
        float shortSide = MathF.Min(w, h);
        float longSide  = MathF.Max(w, h);
        if (shortSide < 1e-3f || perim < 1e-3f) return false;

        // Thin wall loops are long and skinny (aspect often 5–20). Rings / columns
        // are compact (aspect near 1). Threshold ~2.5 keeps mild ellipses closed.
        float aspect = longSide / shortSide;
        if (aspect > 2.5f) return false;

        // Compactness: 4πA / P² is 1 for a circle, lower for skinny shapes.
        // Use shoelace area on the n-vertex ring.
        float area2 = 0f;
        for (int i = 0; i < n; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % n];
            area2 += a.X * b.Y - b.X * a.Y;
        }
        float area = MathF.Abs(area2) * 0.5f;
        float compactness = 4f * MathF.PI * area / (perim * perim);
        // Circles ~1; thin rectangles ~0.1–0.3; require reasonably plump.
        return compactness >= 0.45f;
    }

    /// <summary>
    /// When same-layer travel is disallowed, keep only the longest open face on the layer.
    /// </summary>
    internal static void KeepLongestOpenFaceOnly(List<List<Vector2>> contours, List<bool> closed)
    {
        if (contours.Count <= 1) return;
        // Enclosed rings kept closed by the thin-wall guard mean island travels are
        // unavoidable on this layer — pruning to one path would silently delete model
        // geometry. Only prune when the layer is purely open skins (the original
        // single-panel zig-zag case this rule was written for).
        for (int i = 0; i < contours.Count; i++)
            if (i >= closed.Count || closed[i]) return;
        int best = -1;
        float bestLen = -1f;
        for (int i = 0; i < contours.Count; i++)
        {
            var c = contours[i];
            if (c.Count < 2) continue;
            float len = 0f;
            for (int k = 1; k < c.Count; k++)
                len += Vector2.Distance(c[k - 1], c[k]);
            if (len > bestLen) { bestLen = len; best = i; }
        }
        if (best < 0) return;
        var keepC = contours[best];
        bool keepClosed = best < closed.Count && closed[best];
        contours.Clear();
        closed.Clear();
        contours.Add(keepC);
        closed.Add(keepClosed);
    }

    /// <summary>
    /// Reverse <paramref name="face"/> if needed so the left normal at mid-path
    /// points into <paramref name="closedRing"/> (wall interior / thickness).
    /// </summary>
    private static void OrientOpenFaceIntoPolygon(List<Vector2> face, List<Vector2> closedRing)
    {
        if (face.Count < 2 || closedRing.Count < 3) return;
        int mid = face.Count / 2;
        var a = face[Math.Max(0, mid - 1)];
        var b = face[Math.Min(face.Count - 1, mid + 1)];
        var tan = b - a;
        float tl = tan.Length();
        if (tl < 1e-6f) return;
        tan /= tl;
        var left = new Vector2(-tan.Y, tan.X);
        var probe = face[mid] + left * 2f;
        if (!PointInPolygon(probe, closedRing))
            face.Reverse();
    }

    /// <summary>Longest near-straight run of edges on a closed ring (one skin of a wall).</summary>
    private static List<Vector2> LongestOpenFace(List<Vector2> closedRing)
    {
        int n = closedRing.Count;
        // Drop duplicate closing vertex if present.
        if (n > 2 && Dist2(closedRing[0], closedRing[^1]) < 1e-6f)
            n--;
        if (n < 3) return new List<Vector2>(closedRing);

        float bestLen = -1f;
        int bestI0 = 0, bestCount = n;

        for (int start = 0; start < n; start++)
        {
            float runLen = 0f;
            int count = 1;
            for (int k = 0; k < n - 1; k++)
            {
                int i0 = (start + k) % n;
                int i1 = (start + k + 1) % n;
                int i2 = (start + k + 2) % n;
                var t0 = closedRing[i1] - closedRing[i0];
                var t1 = closedRing[i2] - closedRing[i1];
                float l0 = t0.Length(), l1 = t1.Length();
                runLen += l0;
                count++;
                float turn = 0f;
                if (l0 > 1e-6f && l1 > 1e-6f)
                    turn = MathF.Abs(MathF.Acos(Math.Clamp(Vector2.Dot(t0 / l0, t1 / l1), -1f, 1f)));
                // Sharp corner (> ~35°) ends this face.
                if (turn > 0.6f)
                    break;
            }
            if (runLen > bestLen)
            {
                bestLen = runLen;
                bestI0 = start;
                bestCount = count;
            }
        }

        // Prefer a face that is a real side of the wall, not the whole perimeter.
        if (bestCount >= n - 1)
        {
            // Smooth loop (curved wall): take half the perimeter as one "skin"
            // by projecting onto the dominant axis of the bounding box.
            return LongestSkinByProjection(closedRing, n);
        }

        var face = new List<Vector2>(bestCount);
        for (int k = 0; k < bestCount; k++)
            face.Add(closedRing[(bestI0 + k) % n]);
        return face;
    }

    /// <summary>
    /// For smooth closed rings, pick the chain of vertices on the side of the
    /// bounding-box long axis that forms one continuous outer skin.
    /// </summary>
    private static List<Vector2> LongestSkinByProjection(List<Vector2> ring, int n)
    {
        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;
        for (int i = 0; i < n; i++)
        {
            var p = ring[i];
            if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
        }
        bool longInX = (maxX - minX) >= (maxY - minY);
        // Side with larger average |offset| from center along the short axis =
        // outer face of a thin wall (or pick max extent).
        float mid = longInX ? 0.5f * (minY + maxY) : 0.5f * (minX + maxX);

        // Walk the ring and find the longest contiguous run on the "high" side of mid.
        // Try both high and low sides; keep the longer run.
        List<Vector2> best = new();
        foreach (bool high in new[] { true, false })
        {
            var runs = new List<List<Vector2>>();
            List<Vector2>? cur = null;
            for (int i = 0; i < n; i++)
            {
                var p = ring[i];
                float v = longInX ? p.Y : p.X;
                bool onSide = high ? v >= mid : v <= mid;
                if (onSide)
                {
                    cur ??= new List<Vector2>();
                    cur.Add(p);
                }
                else if (cur is not null)
                {
                    runs.Add(cur);
                    cur = null;
                }
            }
            if (cur is not null) runs.Add(cur);
            // Merge wrap-around.
            if (runs.Count >= 2
                && (longInX ? ring[0].Y >= mid == high : ring[0].X >= mid == high)
                && (longInX ? ring[n - 1].Y >= mid == high : ring[n - 1].X >= mid == high))
            {
                var merged = new List<Vector2>(runs[^1]);
                merged.AddRange(runs[0]);
                runs[0] = merged;
                runs.RemoveAt(runs.Count - 1);
            }
            foreach (var r in runs)
            {
                float len = 0;
                for (int k = 1; k < r.Count; k++)
                    len += Vector2.Distance(r[k - 1], r[k]);
                float bestLen = 0;
                for (int k = 1; k < best.Count; k++)
                    bestLen += Vector2.Distance(best[k - 1], best[k]);
                if (len > bestLen) best = r;
            }
        }
        return best.Count >= 2 ? best : ring.Take(n).ToList();
    }

    /// <summary>Surface mode fills across CLOSED boundary chains only.</summary>
    private static List<List<Vector2>> FilterFillPolys(
        List<List<Vector2>> contours, List<bool> closed, bool surfaceMode)
    {
        if (!surfaceMode) return contours;
        var polys = new List<List<Vector2>>();
        for (int ci = 0; ci < contours.Count; ci++)
            if (closed[ci]) polys.Add(contours[ci]);
        return polys;
    }

    // -- Intersection / segment collection -------------------------------------

    private static void CollectSegments(
        Vector3[] verts,
        float z,
        List<(Vector2, Vector2)> segments,
        List<(Vector2 pos, Vector3 normal)>? normalLookup = null,
        bool surfaceNormals = false)
    {
        Span<Vector2> pts = stackalloc Vector2[2];

        // verts is a flat triangle soup -- every 3 entries = one triangle.
        for (int i = 0; i + 2 < verts.Length; i += 3)
        {
            var v0 = verts[i];
            var v1 = verts[i + 1];
            var v2 = verts[i + 2];

            float d0 = v0.Z - z;
            float d1 = v1.Z - z;
            float d2 = v2.Z - z;

            // Push nearly-on-plane vertices slightly off to avoid degenerate intersections.
            if (MathF.Abs(d0) < 1e-5f) d0 = d0 >= 0f ? 1e-5f : -1e-5f;
            if (MathF.Abs(d1) < 1e-5f) d1 = d1 >= 0f ? 1e-5f : -1e-5f;
            if (MathF.Abs(d2) < 1e-5f) d2 = d2 >= 0f ? 1e-5f : -1e-5f;

            int count = 0;
            TryEdge(v0, v1, d0, d1, pts, ref count);
            TryEdge(v1, v2, d1, d2, pts, ref count);
            TryEdge(v2, v0, d2, d0, pts, ref count);

            if (count == 2)
            {
                segments.Add((pts[0], pts[1]));
                if (normalLookup != null)
                {
                    var e1 = v1 - v0; var e2 = v2 - v0;
                    var fn = Vector3.Cross(e1, e2);
                    float fnLen2 = fn.LengthSquared();
                    Vector3 nDir;
                    if (surfaceNormals && fnLen2 > 1e-10f)
                    {
                        // Surface/cladding: tool follows the mesh face normal at the intersection.
                        nDir = Vector3.Normalize(fn);
                    }
                    else if (fnLen2 > 1e-10f)
                    {
                        // Normal/solid: gradient of layer height along the surface.
                        float dz1 = v1.Z - v0.Z, dz2 = v2.Z - v0.Z;
                        var grad = (-dz1 * Vector3.Cross(fn, e2) + dz2 * Vector3.Cross(fn, e1)) / fnLen2;
                        float gLen = grad.Length();
                        nDir = gLen > 1e-6f ? grad / gLen : Vector3.UnitZ;
                    }
                    else
                    {
                        nDir = Vector3.UnitZ;
                    }
                    normalLookup.Add(((pts[0] + pts[1]) * 0.5f, nDir));
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void TryEdge(
        Vector3 a, Vector3 b,
        float da, float db,
        Span<Vector2> pts, ref int count)
    {
        if (count >= 2) return;
        if (da * db >= 0f) return; // same side -- no crossing

        float t = da / (da - db);
        pts[count++] = new Vector2(
            a.X + t * (b.X - a.X),
            a.Y + t * (b.Y - a.Y));
    }

    // -- Contour extraction ---------------------------------------------------

    // Chains raw intersection segments into contours by greedily connecting nearest endpoints.
    // Extends from BOTH the head and tail so a chain grows in both directions regardless of
    // which direction the seed segment happened to be oriented. This prevents open-boundary
    // meshes (e.g. a split cube) from producing split chains that look like doubled walls.
    /// <summary>
    /// Drops chained contours that are too short to extrude.
    ///
    /// <see cref="CollectSegments"/> nudges a vertex sitting exactly on the slice plane to
    /// the +Z side. When the geometry around that vertex is BELOW the plane, the triangle
    /// then reads as a real crossing and yields a sub-micron segment — the plane grazing a
    /// tip. Flipping the nudge direction does not help; it just moves the failure to
    /// downward-pointing tips. Several such segments at one vertex chain into a 3+ point
    /// "contour" of zero length, which survives every later stage: the ≥3 point test in
    /// <see cref="ChainByProximity"/>, and the centroid-inside-a-bigger-contour test in
    /// SurfaceSlicing.FilterContours (it sits ON the main contour's boundary, so the
    /// point-in-polygon test says no). It then costs a full travel out and back.
    ///
    /// Observed on the Dragon column: one plane landed on the tessellation vertices where
    /// the internal cross-brace meets the wall, and the layer paid a 1.6 m round trip to
    /// print two zero-length loops.
    ///
    /// One bead width is a deliberately loose floor — a genuinely printable closed loop is
    /// at least a bead across, so its perimeter is ~2 bead widths or more.
    /// </summary>
    private static void DropUnprintableContours(List<List<Vector2>> contours, float z, float beadWidth)
    {
        float minLen = MathF.Max(beadWidth, 1f);

        for (int i = contours.Count - 1; i >= 0; i--)
        {
            var c = contours[i];

            // Chains are polylines; a closed ring repeats its first point at the end,
            // so this is the full perimeter without adding a closing edge.
            float len = 0f;
            for (int k = 1; k < c.Count; k++) len += Vector2.Distance(c[k - 1], c[k]);
            if (len >= minLen) continue;

            contours.RemoveAt(i);
            s_droppedContours.Add((z, c.Count > 0 ? c[0] : default, len));
        }
    }

    /// <summary>Unprintable contours discarded this slice: (layer Z, where, path length).</summary>
    private static readonly List<(float Z, Vector2 At, float Len)> s_droppedContours = [];

    /// <summary>Console summary of what <see cref="DropUnprintableContours"/> removed.</summary>
    private static void ReportDroppedContours()
    {
        if (s_droppedContours.Count == 0) return;

        int distinctLayers = s_droppedContours.Select(d => d.Z).Distinct().Count();
        System.Console.WriteLine(
            $"[slice] dropped {s_droppedContours.Count} unprintable contour(s) on " +
            $"{distinctLayers} layer(s) — too short to extrude, each would have cost a travel:");

        foreach (var d in s_droppedContours.Take(8))
            System.Console.WriteLine(
                $"[slice]   Z={d.Z:F3}  at ({d.At.X:F2}, {d.At.Y:F2})  length {d.Len:F4}mm");

        if (s_droppedContours.Count > 8)
            System.Console.WriteLine($"[slice]   …and {s_droppedContours.Count - 8} more");
    }

    private static List<List<Vector2>> ChainByProximity(List<(Vector2 A, Vector2 B)> segs)
    {
        int n = segs.Count;
        var used     = new bool[n];
        var contours = new List<List<Vector2>>();
        var grid     = new SegmentEndpointGrid(segs);

        for (int start = 0; start < n; start++)
        {
            if (used[start]) continue;
            used[start] = true;

            var chain = new List<Vector2> { segs[start].A, segs[start].B };

            // Endpoint spatial hash — the full O(n) scan per step made this O(n²),
            // which never finished on dense sections (Multi-Planar V80 drone hang).
            bool anyProgress = true;
            while (anyProgress)
            {
                anyProgress = false;

                // Extend from tail
                {
                    int bi = grid.FindNearest(chain[^1], used, out bool flip, out float best);
                    if (bi >= 0 && best <= 1.0f)
                    {
                        used[bi] = true;
                        chain.Add(flip ? segs[bi].A : segs[bi].B);
                        anyProgress = true;
                    }
                }

                // Extend from head (A≈head → prepend B; B≈head → prepend A)
                {
                    int bi = grid.FindNearest(chain[0], used, out bool flip, out float best);
                    if (bi >= 0 && best <= 1.0f)
                    {
                        used[bi] = true;
                        chain.Insert(0, flip ? segs[bi].A : segs[bi].B);
                        anyProgress = true;
                    }
                }
            }

            if (chain.Count >= 3)
                contours.Add(chain);
        }

        return contours;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Dist2(Vector2 a, Vector2 b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y;
        return dx * dx + dy * dy;
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

    // -- Topology-aware seam assignment ----------------------------------------

    // Minimum overlap fraction (of the smaller contour's vertices) to consider
    // two contours on adjacent layers to be the "same" feature.
    private const float OverlapThreshold = 0.05f;

    private static List<ContourTrack> AssignSeams(
        List<List<Vector2>> contours,
        List<bool>          closedFlags,
        List<ContourTrack>  prevTracks,
        Vector2 seamOrigin, Vector2 seamDir,
        IReadOnlyList<Vector2> seamGuides)
    {
        var tracks = new List<ContourTrack>(contours.Count);
        for (int i = 0; i < contours.Count; i++)
        {
            var raw     = contours[i];
            var contour = new List<Vector2>(raw);

            // Find best parent via XY overlap.
            float bestScore = 0f;
            ContourTrack? bestParent = null;
            foreach (var prev in prevTracks)
            {
                float score = OverlapScore(prev.Contour, contour);
                if (score > bestScore) { bestScore = score; bestParent = prev; }
            }

            // Birth (no parent) -> initialize seam from ray.
            // Continuous / split / merge -> project from parent seam.
            Vector2 seamRef = (bestParent != null && bestScore >= OverlapThreshold)
                ? bestParent.SeamXY
                : new Vector2(float.NaN, float.NaN);

            if (closedFlags[i])
            {
                if (float.IsNaN(seamRef.X) && seamGuides.Count > 0)
                {
                    var guide = ContourSeamPlanner.NearestGuideToContour(contour, seamGuides);
                    ContourSeamPlanner.AlignSeamToGuide(contour, guide, ref seamRef);
                }
                else
                    ContourSeamPlanner.AlignSeamFromRay(contour, seamOrigin, seamDir, ref seamRef);
            }
            tracks.Add(new ContourTrack(contour, seamRef, closedFlags[i]));
        }
        return tracks;
    }

    // Approximate overlap ratio using vertex-in-polygon sampling.
    // Returns max(fraction of A's vertices inside B, fraction of B's inside A).
    private static float OverlapScore(List<Vector2> a, List<Vector2> b)
    {
        int aInB = 0, bInA = 0;
        foreach (var p in a) if (PointInPolygon(p, b)) aInB++;
        foreach (var p in b) if (PointInPolygon(p, a)) bInA++;
        float rA = a.Count > 0 ? (float)aInB / a.Count : 0f;
        float rB = b.Count > 0 ? (float)bInA / b.Count : 0f;
        return MathF.Max(rA, rB);
    }

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

    // -- Per-contour seam tracking ---------------------------------------------

    public sealed class ContourTrack(List<Vector2> contour, Vector2 seamXY, bool isClosed)
    {
        public readonly List<Vector2>  Contour  = contour;
        public readonly Vector2        SeamXY   = seamXY;
        public readonly bool           IsClosed = isClosed;
        // Per-vertex surface normals from mesh face intersection. Null = use layer.PlaneNormal.
        public List<Vector3>?          Normals;
    }

    // -- Clipper2 contour offset --------------------------------------------------

    // Outer (CCW) contours contract with delta = +halfBead → Clipper receives -halfBead.
    // Hole (CW) contours contract with delta = -halfBead → Clipper receives +halfBead.
    // Callers must orient contours and choose delta sign before calling here.
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

    // -- Douglas-Peucker contour simplification --------------------------------

    // Removes the intermediate collinear vertices Clipper2 adds on straight segments,
    // keeping only points that deviate more than `tolerance` from the simplified line.
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

    // -- Overhang orientation helpers ------------------------------------------

    private static Vector3 NearestNormal(Vector2 pt, List<(Vector2 pos, Vector3 normal)> lookup)
    {
        float best   = float.MaxValue;
        var   result = Vector3.UnitZ;
        foreach (var (pos, normal) in lookup)
        {
            float d = Dist2(pt, pos);
            if (d < best) { best = d; result = normal; }
        }
        return result;
    }

    // Clamps the normal so that its angle from straight-down (+Z) does not exceed maxTiltRad.
    // This prevents the robot from tilting to unreachable configurations on near-vertical or
    // inverted surfaces.
    /// <summary>Spatial hash over the previous layer's contour segments (16 mm cells,
    /// 3×3 ring lookup) so per-vertex nearest-previous-bead queries stay O(1).</summary>
    private const float PrevGridCell = 16f;

    private static Dictionary<(int X, int Y), List<(Vector2 A, Vector2 B)>>? BuildPrevContourGrid(
        List<ContourTrack> prevTracks)
    {
        if (prevTracks.Count == 0) return null;
        var grid = new Dictionary<(int, int), List<(Vector2, Vector2)>>();
        void Add(Vector2 p, (Vector2, Vector2) seg)
        {
            var k = ((int)MathF.Floor(p.X / PrevGridCell), (int)MathF.Floor(p.Y / PrevGridCell));
            if (!grid.TryGetValue(k, out var list))
                grid[k] = list = new List<(Vector2, Vector2)>(4);
            list.Add(seg);
        }
        foreach (var t in prevTracks)
        {
            var c = t.Contour;
            for (int i = 1; i < c.Count; i++)
            {
                var seg = (c[i - 1], c[i]);
                Add(c[i - 1], seg);
                Add(c[i], seg);
                Add((c[i - 1] + c[i]) * 0.5f, seg);
            }
        }
        return grid.Count > 0 ? grid : null;
    }

    /// <summary>
    /// Tool axis for overhang orientation: aim the extruder back at the previous
    /// layer's bead so overhang extrusion is pressed onto supported material.
    /// Tilt direction = the vertex's XY shift from the nearest previous-layer path
    /// point; tilt angle = atan(shift / layerHeight) capped at the user's Max tilt.
    /// Stacked wall (no shift) → vertical tool. No previous material within one
    /// grid ring (~16 mm) → <paramref name="fallback"/> (mesh-face orientation).
    /// </summary>
    private static Vector3 OverhangNormalTowardPrevLayer(
        Vector2 pt, Dictionary<(int X, int Y), List<(Vector2 A, Vector2 B)>>? prevGrid,
        float layerHeight, float maxTiltRad, Vector3 fallback)
    {
        if (prevGrid is null) return fallback;
        int kx = (int)MathF.Floor(pt.X / PrevGridCell);
        int ky = (int)MathF.Floor(pt.Y / PrevGridCell);
        float bestSq = float.MaxValue;
        Vector2 bestQ = pt;
        for (int dx = -1; dx <= 1; dx++)
        for (int dy = -1; dy <= 1; dy++)
        {
            if (!prevGrid.TryGetValue((kx + dx, ky + dy), out var segs)) continue;
            foreach (var (a, b) in segs)
            {
                var ab = b - a;
                float l2 = ab.LengthSquared();
                float t = l2 < 1e-12f ? 0f : Math.Clamp(Vector2.Dot(pt - a, ab) / l2, 0f, 1f);
                var q = a + ab * t;
                float d2 = Vector2.DistanceSquared(pt, q);
                if (d2 < bestSq) { bestSq = d2; bestQ = q; }
            }
        }
        if (bestSq >= float.MaxValue * 0.5f) return fallback;   // no previous bead nearby
        float shift = MathF.Sqrt(bestSq);
        if (shift < 0.05f) return Vector3.UnitZ;                // stacked — print straight down
        float tilt = MathF.Min(MathF.Atan(shift / layerHeight), maxTiltRad);
        var dir = (pt - bestQ) / shift;
        float s = MathF.Sin(tilt);
        return new Vector3(dir.X * s, dir.Y * s, MathF.Cos(tilt));
    }

    private static Vector3 ClampNormalTilt(Vector3 n, float maxTiltRad)
    {
        float minZ = MathF.Cos(maxTiltRad); // e.g. cos(45°) ≈ 0.707
        if (n.Z >= minZ) return Vector3.Normalize(n);
        // Tilt exceeds limit — keep XY direction, clamp Z up to minZ.
        var   xy      = new Vector2(n.X, n.Y);
        float xyLen   = xy.Length();
        if (xyLen < 1e-6f) return Vector3.UnitZ;
        float xyTarget = MathF.Sqrt(MathF.Max(0f, 1f - minZ * minZ));
        return Vector3.Normalize(new Vector3(xy.X / xyLen * xyTarget, xy.Y / xyLen * xyTarget, minZ));
    }
}
