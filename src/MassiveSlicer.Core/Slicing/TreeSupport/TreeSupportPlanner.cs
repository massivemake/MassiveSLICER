using System.Numerics;
using Clipper2Lib;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing.Lightning;

namespace MassiveSlicer.Core.Slicing.TreeSupport;

/// <summary>
/// Tree support: compact rectangular dual-wall outline per demand cluster.
/// On every layer the outline is <b>snapped to the nearest part exterior</b>
/// (never left floating at a distant projection). Multiplanar uses full 3D tip
/// anchors projected into each plane's UV, then wall-snapped.
/// </summary>
public static class TreeSupportPlanner
{
    private const float OutsideClearanceBeads = 1.75f;
    private const float BaseHalfBeads = 1.15f;
    private const float TipPadBeads = 0.6f;
    private const float MaxHalfBeads = 8f;
    /// <summary>If projected center is farther than this from any wall, skip the layer.</summary>
    private const float MaxAttachBeads = 25f;

    public static TreeSupportPlan Build(
        IReadOnlyList<List<List<Vector2>>> fillPolysPerLayer,
        IReadOnlyList<float> layerHeights,
        SliceSettings settings,
        IReadOnlyList<ManualDemandLayer>? treeDemand,
        IReadOnlyList<(Vector3 Origin, Vector3 U, Vector3 V)>? frames = null)
    {
        int n = fillPolysPerLayer.Count;
        var plan = new TreeSupportPlan(n);
        if (n == 0 || treeDemand is null) return plan;

        float bead = MathF.Max(settings.BeadWidth, 0.1f);
        float spacing = settings.LightningBranchSpacingMm > 0f
            ? settings.LightningBranchSpacingMm
            : 4f * bead;
        float maxHalf = bead * MaxHalfBeads;
        float maxAttach = bead * MaxAttachBeads;
        _ = layerHeights;

        // Collect demand with full 3D world positions (multiplanar-safe).
        var demands = new List<(int Layer, Vector3 World3, Vector2 PlaneUV)>();
        for (int li = 0; li < n && li < treeDemand.Count; li++)
        {
            var dem = treeDemand[li];
            if (dem is null || !dem.HasAny) continue;
            var run = dem.SupportBar.Count > 0 ? dem.SupportBar : dem.ColumnFoot;
            foreach (var p in run)
            {
                var w = PlaneLocalToWorld(li, p, frames);
                demands.Add((li, w, p));
            }
        }
        plan.DemandPoints = demands.Count;
        if (demands.Count == 0) return plan;

        float clusterR = MathF.Max(spacing * 1.25f, bead * 4f);
        var clusters = ClusterDemands(demands, clusterR);
        plan.TreesBorn = clusters.Count;

        var regions = new PathsD[n];
        for (int i = 0; i < n; i++)
            regions[i] = LightningPlanner.ToPathsD(fillPolysPerLayer[i], bead);

        foreach (var cluster in clusters)
        {
            int topLayer = 0;
            foreach (var (li, _, _) in cluster)
                if (li > topLayer) topLayer = li;
            topLayer = Math.Min(topLayer, n - 1);

            // Tip-layer UV AABB from demand samples (on tip plane).
            float tipMinU = float.MaxValue, tipMaxU = float.MinValue;
            float tipMinV = float.MaxValue, tipMaxV = float.MinValue;
            var tipWorldSum = Vector3.Zero;
            int tipN = 0;
            foreach (var (li, w3, uv) in cluster)
            {
                Vector2 uvTip;
                if (li == topLayer)
                    uvTip = uv;
                else if (frames is not null)
                    uvTip = World3ToPlaneLocal(topLayer, w3, frames);
                else
                    uvTip = new Vector2(w3.X, w3.Y);

                if (uvTip.X < tipMinU) tipMinU = uvTip.X;
                if (uvTip.X > tipMaxU) tipMaxU = uvTip.X;
                if (uvTip.Y < tipMinV) tipMinV = uvTip.Y;
                if (uvTip.Y > tipMaxV) tipMaxV = uvTip.Y;
                tipWorldSum += w3;
                tipN++;
            }
            if (tipN == 0) continue;

            float tipPad = bead * TipPadBeads;
            float tipHalfU = Math.Clamp((tipMaxU - tipMinU) * 0.5f + tipPad, bead * BaseHalfBeads, maxHalf);
            float tipHalfV = Math.Clamp((tipMaxV - tipMinV) * 0.5f + tipPad, bead * BaseHalfBeads, maxHalf);
            bool longAlongU = tipHalfU >= tipHalfV;
            float tipLong = Math.Clamp(MathF.Max(tipHalfU, tipHalfV), bead * BaseHalfBeads, maxHalf);
            float tipThick = Math.Clamp(MathF.Min(tipHalfU, tipHalfV), bead * 0.55f, bead * 1.25f);
            float baseLong = MathF.Max(MathF.Min(tipLong, bead * BaseHalfBeads * 1.4f), bead * 0.9f);
            float baseThick = MathF.Max(MathF.Min(tipThick, bead * 0.7f), bead * 0.55f);

            // Tip center: wall-snapped on the tip layer.
            var tipCenterUv = new Vector2((tipMinU + tipMaxU) * 0.5f, (tipMinV + tipMaxV) * 0.5f);
            if (regions[topLayer].Count > 0)
            {
                if (!TrySnapOutsideWall(regions[topLayer], tipCenterUv, bead, maxAttach, out tipCenterUv))
                    tipCenterUv = PlaceOutsidePart(regions[topLayer], tipCenterUv, bead);
            }
            var tipWorld3 = PlaneLocalToWorld(topLayer, tipCenterUv, frames);
            // Track wall-attached world anchor as we walk down (follows multiplanar wall).
            var anchorWorld = tipWorld3;

            for (int li = topLayer; li >= 0; li--)
            {
                float t = topLayer <= 0 ? 1f : li / (float)topLayer;
                float flare = t * t;
                float halfLong = baseLong + (tipLong - baseLong) * flare;
                float halfThick = baseThick + (tipThick - baseThick) * flare;

                bool hasPart = regions[li].Count > 0;
                // Need solid walls to attach to — skip empty planes (except tip already done).
                if (!hasPart)
                    continue;

                // Aim UV: project current wall anchor onto this plane.
                Vector2 aimUv = frames is null
                    ? new Vector2(anchorWorld.X, anchorWorld.Y)
                    : World3ToPlaneLocal(li, anchorWorld, frames);

                // ALWAYS snap to nearest exterior — never keep a distant "already outside" UV.
                if (!TrySnapOutsideWall(regions[li], aimUv, bead, maxAttach, out var centerUv))
                    continue; // no wall within attach range — skip (prevents orphan far trees)

                // Update anchor so lower layers follow the wall, not a fixed world ray.
                anchorWorld = PlaneLocalToWorld(li, centerUv, frames);

                float hu = longAlongU ? halfLong : halfThick;
                float hv = longAlongU ? halfThick : halfLong;
                hu = MathF.Min(hu, maxHalf);
                hv = MathF.Min(hv, maxHalf);

                plan.Layers[li].Branches.Add([
                    new Vector2(centerUv.X - hu, centerUv.Y - hv),
                    new Vector2(centerUv.X + hu, centerUv.Y - hv),
                    new Vector2(centerUv.X + hu, centerUv.Y + hv),
                    new Vector2(centerUv.X - hu, centerUv.Y + hv),
                    new Vector2(centerUv.X - hu, centerUv.Y - hv),
                ]);
            }
        }

        int layersWith = 0;
        for (int li = 0; li < n; li++)
            if (plan.Layers[li].Branches.Count > 0) layersWith++;

        int maxDemandLi = 0;
        foreach (var (li, _, _) in demands)
            if (li > maxDemandLi) maxDemandLi = li;

        System.Console.WriteLine(
            $"[tree-support] plan: layers={n} demand={plan.DemandPoints} " +
            $"clusters={plan.TreesBorn} withBranches={layersWith}/{n} " +
            $"tipLayer={maxDemandLi} wallSnap=true maxAttach={maxAttach:0.#}mm");
        return plan;
    }

    /// <summary>
    /// Snap <paramref name="pt"/> to the exterior of the nearest wall, within
    /// <paramref name="maxAttachMm"/>. Returns false if no wall is close enough.
    /// </summary>
    private static bool TrySnapOutsideWall(
        PathsD region, Vector2 pt, float bead, float maxAttachMm, out Vector2 outside)
    {
        outside = pt;
        if (region.Count == 0) return false;
        var wall = ClosestOnBoundary(region, pt);
        float dist = Vector2.Distance(pt, wall);
        if (dist > maxAttachMm) return false;

        float clearance = bead * OutsideClearanceBeads;
        // Outward = from solid interior toward exterior. wall - interiorPoint when inside;
        // when outside, wall is on boundary and we want continue outward from wall away from solid.
        var mid = wall; // probe slightly toward pt then reverse if needed
        // Estimate interior direction: from wall toward polygon centroid of largest outer.
        var outer = LargestOuter(region);
        var centroid = Vector2.Zero;
        foreach (var q in outer)
            centroid += new Vector2((float)q.x, (float)q.y);
        centroid /= Math.Max(1, outer.Count);
        var outward = wall - centroid;
        float olen = outward.Length();
        if (olen < 1e-4f)
        {
            // Fallback: from wall away from pt if pt is inside-ish.
            outward = pt - wall;
            olen = outward.Length();
            if (olen < 1e-4f) outward = new Vector2(1f, 0f);
            else outward /= olen;
            // If pt is outside, outward should be pt-wall; if inside wall-centroid is better.
            if (!IsInsideRegion(region, pt))
                outward = (pt - wall);
            olen = outward.Length();
            if (olen > 1e-4f) outward /= olen;
            else outward = new Vector2(1f, 0f);
        }
        else
            outward /= olen;

        outside = wall + outward * clearance;
        // Ensure we landed outside.
        if (IsInsideRegion(region, outside))
            outside = wall + outward * (clearance * 2f);
        return true;
    }

    private static bool IsInsideRegion(PathsD region, Vector2 uv)
    {
        if (region.Count == 0) return false;
        return Clipper.PointInPolygon(new PointD(uv.X, uv.Y), LargestOuter(region))
               != PointInPolygonResult.IsOutside;
    }

    private static Vector3 PlaneLocalToWorld(
        int li, Vector2 p,
        IReadOnlyList<(Vector3 Origin, Vector3 U, Vector3 V)>? frames)
    {
        if (frames is null || li < 0 || li >= frames.Count)
            return new Vector3(p.X, p.Y, 0f);
        var f = frames[li];
        return f.Origin + f.U * p.X + f.V * p.Y;
    }

    private static Vector2 World3ToPlaneLocal(
        int li, Vector3 world,
        IReadOnlyList<(Vector3 Origin, Vector3 U, Vector3 V)> frames)
    {
        if (li < 0 || li >= frames.Count)
            return new Vector2(world.X, world.Y);
        var f = frames[li];
        var n = Vector3.Cross(f.U, f.V);
        float nLen = n.Length();
        if (nLen > 1e-8f)
        {
            n /= nLen;
            float dist = Vector3.Dot(n, world - f.Origin);
            world -= n * dist; // closest point on plane
        }
        var rel = world - f.Origin;
        return new Vector2(Vector3.Dot(rel, f.U), Vector3.Dot(rel, f.V));
    }

    private static List<List<(int Layer, Vector3 World3, Vector2 PlaneUV)>> ClusterDemands(
        List<(int Layer, Vector3 World3, Vector2 PlaneUV)> demands, float radius)
    {
        var clusters = new List<List<(int, Vector3, Vector2)>>();
        var used = new bool[demands.Count];
        float r2 = radius * radius;
        for (int i = 0; i < demands.Count; i++)
        {
            if (used[i]) continue;
            var c = new List<(int, Vector3, Vector2)> { demands[i] };
            used[i] = true;
            bool grew;
            do
            {
                grew = false;
                for (int j = 0; j < demands.Count; j++)
                {
                    if (used[j]) continue;
                    foreach (var (_, p, _) in c)
                    {
                        // Cluster by world horizontal proximity.
                        var a = new Vector2(p.X, p.Y);
                        var b = new Vector2(demands[j].World3.X, demands[j].World3.Y);
                        if (Vector2.DistanceSquared(a, b) <= r2)
                        {
                            c.Add(demands[j]);
                            used[j] = true;
                            grew = true;
                            break;
                        }
                    }
                }
            } while (grew);
            clusters.Add(c);
        }
        return clusters;
    }

    private static Vector2 PlaceOutsidePart(PathsD region, Vector2 pt, float bead)
    {
        if (region.Count == 0) return pt;
        if (TrySnapOutsideWall(region, pt, bead, bead * MaxAttachBeads * 4f, out var o))
            return o;
        return pt;
    }

    private static PathD LargestOuter(PathsD region)
    {
        PathD best = region[0];
        double bestA = 0;
        foreach (var p in region)
        {
            double a = Clipper.Area(p);
            if (a > bestA) { bestA = a; best = p; }
        }
        return best;
    }

    private static Vector2 ClosestOnBoundary(PathsD region, Vector2 pt)
    {
        float best = float.MaxValue;
        Vector2 bestP = pt;
        foreach (var path in region)
        {
            for (int i = 0; i < path.Count; i++)
            {
                var a = new Vector2((float)path[i].x, (float)path[i].y);
                var b = new Vector2((float)path[(i + 1) % path.Count].x,
                    (float)path[(i + 1) % path.Count].y);
                var q = ClosestOnSegment(pt, a, b);
                float d = Vector2.DistanceSquared(pt, q);
                if (d < best) { best = d; bestP = q; }
            }
        }
        return bestP;
    }

    private static Vector2 ClosestOnSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float t = Vector2.Dot(p - a, ab);
        float den = ab.LengthSquared();
        if (den < 1e-12f) return a;
        t = Math.Clamp(t / den, 0f, 1f);
        return a + ab * t;
    }
}
