using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;
using MassiveSlicer.Core.Slicing.TreeSupport;
using Xunit;

namespace MassiveSlicer.Tests;

public class TreeSupportTest
{
    private const float Bead = 6f;

    [Fact]
    public void PaintSupportStyle_Labels_RoundTrip()
    {
        Assert.Equal(PaintSupportStyle.Tree,
            PaintSupportStyleUtil.FromLabel("Tree Support"));
        Assert.Equal(PaintSupportStyle.FormboundBridge,
            PaintSupportStyleUtil.FromLabel("Formbound Bridge"));
        Assert.Equal("Tree Support",
            PaintSupportStyleUtil.ToLabel(PaintSupportStyle.Tree));
    }

    [Fact]
    public void ProjectBridgeMarks_StyleFilter_SeparatesTreeAndFormbound()
    {
        var marks = new List<PaintMark>
        {
            new(new Vector3(10, 0, 30), 8f, PaintMarkKind.Bridge,
                PaintBridgeRole.SupportBar, PaintSupportStyle.FormboundButtress),
            new(new Vector3(50, 0, 30), 8f, PaintMarkKind.Bridge,
                PaintBridgeRole.SupportBar, PaintSupportStyle.Tree),
        };
        int n = 20;
        float[] zs = Enumerable.Range(0, n).Select(i => i * 3f).ToArray();
        Func<int, (Vector3, Vector3, Vector3, Vector3)> frameOf =
            li => (new Vector3(0, 0, zs[li]), Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY);

        var form = ToolpathPaintFilter.ProjectBridgeMarks(
            marks, n, frameOf, halfBandMm: 10f, targetSupportSelectionsOnly: true,
            styleFilter: PaintSupportStyleUtil.IsFormbound);
        var tree = ToolpathPaintFilter.ProjectBridgeMarks(
            marks, n, frameOf, halfBandMm: 10f, targetSupportSelectionsOnly: true,
            styleFilter: PaintSupportStyleUtil.IsTree);

        Assert.NotNull(form);
        Assert.NotNull(tree);
        Assert.True(form!.Any(d => d.HasAny));
        Assert.True(tree!.Any(d => d.HasAny));
    }

    [Fact]
    public void TreeSupportPlanner_BuildsBedRootedBranchesUnderDemand()
    {
        // Simple square shell stack (10 layers).
        int n = 12;
        var fill = new List<List<List<Vector2>>>(n);
        var heights = new List<float>(n);
        for (int i = 0; i < n; i++)
        {
            fill.Add([
                [
                    new Vector2(-40, -40), new Vector2(40, -40),
                    new Vector2(40, 40), new Vector2(-40, 40),
                ]
            ]);
            heights.Add(3f);
        }

        // Demand near the top outside the square (+X wall exterior).
        var demand = new ManualDemandLayer[n];
        for (int i = 0; i < n; i++) demand[i] = new ManualDemandLayer();
        demand[n - 1].SupportBar.Add(new Vector2(50, 0));
        demand[n - 2].SupportBar.Add(new Vector2(50, 0));

        var settings = new SliceSettings
        {
            BeadWidth = Bead,
            LayerHeight = 3f,
            LightningOverhangDeg = 30f,
            LightningBranchSpacingMm = 24f,
            PaintMarks =
            [
                new PaintMark(new Vector3(50, 0, (n - 1) * 3f), 10f,
                    PaintMarkKind.Bridge, PaintBridgeRole.SupportBar, PaintSupportStyle.Tree),
            ],
        };

        var plan = TreeSupportPlanner.Build(fill, heights, settings, demand);
        Assert.True(plan.DemandPoints > 0);
        Assert.True(plan.TreesBorn > 0);
        // Full column: every layer from bed (0) through tip must have geometry.
        for (int i = 0; i <= n - 1; i++)
            Assert.True(plan.Layers[i].Branches.Count > 0,
                $"expected bed-to-tip tree column; layer {i}/{n - 1} empty");
    }

    [Fact]
    public void TreeSupportPlanner_InsideDemand_StillSnapsOutsideOnEveryLayer()
    {
        // Demand point starts INSIDE the square; thicker lower layers must not
        // swallow the column (re-snap outside per layer).
        int n = 10;
        var fill = new List<List<List<Vector2>>>(n);
        var heights = new List<float>(n);
        for (int i = 0; i < n; i++)
        {
            // Lower half is a larger square (engulfs tip-layer exterior XY).
            float half = i < n / 2 ? 60f : 40f;
            fill.Add([
                [
                    new Vector2(-half, -half), new Vector2(half, -half),
                    new Vector2(half, half), new Vector2(-half, half),
                ]
            ]);
            heights.Add(3f);
        }

        var demand = new ManualDemandLayer[n];
        for (int i = 0; i < n; i++) demand[i] = new ManualDemandLayer();
        // Tip demand near +X wall of upper (smaller) square — inside lower square.
        demand[n - 1].SupportBar.Add(new Vector2(45, 0));

        var plan = TreeSupportPlanner.Build(fill, heights, new SliceSettings
        {
            BeadWidth = Bead,
            LayerHeight = 3f,
            LightningOverhangDeg = 30f,
            LightningBranchSpacingMm = 24f,
        }, demand);

        // Bed layer must still have a post (snapped outside the large lower square).
        Assert.True(plan.Layers[0].Branches.Count > 0, "bed layer must keep tree foundation");
        Assert.True(plan.Layers[n - 1].Branches.Count > 0);
    }

    [Fact]
    public void TreeSupportGenerator_KeepsIslandsSeparate_WithTravelBetween()
    {
        // Two nearby rectangles stay separate; connector is Travel (not extrude-weld).
        static List<Vector2> Rect(float cx, float cy, float hw, float hh) =>
        [
            new(cx - hw, cy - hh), new(cx + hw, cy - hh),
            new(cx + hw, cy + hh), new(cx - hw, cy + hh),
            new(cx - hw, cy - hh),
        ];
        var layerPlan = new TreeSupportLayerPlan();
        layerPlan.Branches.Add(Rect(0, 0, Bead, Bead * 0.5f));
        layerPlan.Branches.Add(Rect(Bead * 4f, 0, Bead, Bead * 0.5f));

        var layer = new ToolpathLayer(0, 0f) { Height = 3f };
        // Prior shell end so first hop is also a travel.
        layer.Moves.Add(new ToolpathMove(
            new Vector3(-50, 0, 0), new Vector3(-40, 0, 0), MoveKind.Extrude));
        TreeSupportGenerator.Emit(layerPlan, z: 0f, layer, Bead, partFillPolys: null);

        int extrudes = layer.Moves.Count(m => m.Kind == MoveKind.Extrude && m.IsLightning);
        int travels = layer.Moves.Count(m => m.Kind == MoveKind.Travel);
        Assert.True(extrudes > 0, "expected extruded tree geometry");
        Assert.True(travels >= 2, $"expected travel to each island, got {travels} travels");
        // Hops into tree geometry must be Travel (never extrude-welded to prior path).
        var firstTree = layer.Moves.FindIndex(m => m.IsLightning && m.Kind == MoveKind.Extrude);
        Assert.True(firstTree > 0 && layer.Moves[firstTree - 1].Kind == MoveKind.Travel);
    }

    [Fact]
    public void TreeSupportPlanner_RectangleOutline_TapersOutTowardTip()
    {
        // Elongated demand at tip → rectangle outline wider at tip than at bed.
        int n = 10;
        var fill = new List<List<List<Vector2>>>(n);
        var heights = new List<float>(n);
        for (int i = 0; i < n; i++)
        {
            fill.Add([[
                new Vector2(-40, -40), new Vector2(40, -40),
                new Vector2(40, 40), new Vector2(-40, 40),
            ]]);
            heights.Add(3f);
        }
        var demand = new ManualDemandLayer[n];
        for (int i = 0; i < n; i++) demand[i] = new ManualDemandLayer();
        // Long tip bar along +X wall exterior.
        for (float y = -24f; y <= 24f; y += 8f)
            demand[n - 1].SupportBar.Add(new Vector2(50, y));

        var plan = TreeSupportPlanner.Build(fill, heights, new SliceSettings
        {
            BeadWidth = Bead,
            LayerHeight = 3f,
            LightningOverhangDeg = 30f,
            LightningBranchSpacingMm = 24f,
        }, demand);

        Assert.True(plan.Layers[0].Branches.Count > 0);
        Assert.True(plan.Layers[n - 1].Branches.Count > 0);

        // Each branch is a closed rectangle (≥4 unique corners).
        var tip = plan.Layers[n - 1].Branches[0];
        var bed = plan.Layers[0].Branches[0];
        Assert.True(tip.Count >= 4, "tip should be rectangle outline");
        Assert.True(bed.Count >= 4, "bed should be rectangle outline");

        float Extent(List<Vector2> br)
        {
            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            int nPts = br.Count;
            if (nPts > 1 && Vector2.DistanceSquared(br[0], br[^1]) < 1e-6f) nPts--;
            for (int i = 0; i < nPts; i++)
            {
                if (br[i].X < minX) minX = br[i].X;
                if (br[i].X > maxX) maxX = br[i].X;
                if (br[i].Y < minY) minY = br[i].Y;
                if (br[i].Y > maxY) maxY = br[i].Y;
            }
            return MathF.Max(maxX - minX, maxY - minY);
        }

        float tipE = Extent(tip);
        float bedE = Extent(bed);
        Assert.True(tipE > bedE * 1.15f,
            $"tip should flare wider than bed; tip={tipE:0.#} bed={bedE:0.#}");
    }

    [Fact]
    public void TreeSupportPlanner_WorldUp_KeepsSameWorldXY_OnTiltedFrames()
    {
        // Tilted stack: columns must share world XY (vertical tower).
        int n = 8;
        var fill = new List<List<List<Vector2>>>(n);
        var heights = new List<float>(n);
        var frames = new List<(Vector3 Origin, Vector3 U, Vector3 V)>(n);
        float ang = 15f * MathF.PI / 180f;
        var u = Vector3.UnitX;
        var v = new Vector3(0f, MathF.Cos(ang), MathF.Sin(ang));
        for (int i = 0; i < n; i++)
        {
            fill.Add([[
                new Vector2(-40, -40), new Vector2(40, -40),
                new Vector2(40, 40), new Vector2(-40, 40),
            ]]);
            heights.Add(3f);
            frames.Add((new Vector3(0, 0, i * 3f), u, v));
        }

        var demand = new ManualDemandLayer[n];
        for (int i = 0; i < n; i++) demand[i] = new ManualDemandLayer();
        demand[n - 1].SupportBar.Add(new Vector2(50, 5));

        var plan = TreeSupportPlanner.Build(fill, heights, new SliceSettings
        {
            BeadWidth = Bead,
            LayerHeight = 3f,
            LightningOverhangDeg = 30f,
        }, demand, frames);

        Assert.True(plan.Layers[0].Branches.Count > 0);
        Assert.True(plan.Layers[n - 1].Branches.Count > 0);

        // Wall-snapped centers stay outside the +X face of the square (not free-floating).
        Vector2 CenterUv(int li)
        {
            var br = plan.Layers[li].Branches[0];
            int nPts = br.Count;
            if (nPts > 1 && Vector2.DistanceSquared(br[0], br[^1]) < 1e-6f) nPts--;
            var mid = Vector2.Zero;
            for (int i = 0; i < nPts; i++) mid += br[i];
            return mid / Math.Max(1, nPts);
        }

        var baseUv = CenterUv(0);
        var topUv = CenterUv(n - 1);
        // Outside +X wall of the 40mm half-square (≈ x > 40).
        Assert.True(baseUv.X > 35f, $"bed tree should hug +X exterior, got X={baseUv.X:0.#}");
        Assert.True(topUv.X > 35f, $"tip tree should hug +X exterior, got X={topUv.X:0.#}");
    }

    [Fact]
    public void HasTreePaint_DetectsStyle()
    {
        var marks = new[]
        {
            new PaintMark(Vector3.Zero, 5f, PaintMarkKind.Bridge,
                SupportStyle: PaintSupportStyle.Tree),
        };
        Assert.True(PaintSupportStyleUtil.HasTreePaint(marks));
        Assert.False(PaintSupportStyleUtil.HasFormboundPaint(marks));
    }

    private static Vector3[] BoxMesh(float hx, float hy, float hz)
    {
        // Axis-aligned box as triangle soup (12 tris).
        var c = new Vector3[]
        {
            new(-hx, -hy, 0), new(hx, -hy, 0), new(hx, hy, 0), new(-hx, hy, 0),
            new(-hx, -hy, hz), new(hx, -hy, hz), new(hx, hy, hz), new(-hx, hy, hz),
        };
        int[] q(int a, int b, int d, int e) => [a, b, d, a, d, e];
        var idx = new List<int>();
        idx.AddRange(q(0, 1, 2, 3)); // bottom
        idx.AddRange(q(4, 7, 6, 5)); // top
        idx.AddRange(q(0, 4, 5, 1)); // -Y
        idx.AddRange(q(1, 5, 6, 2)); // +X
        idx.AddRange(q(2, 6, 7, 3)); // +Y
        idx.AddRange(q(3, 7, 4, 0)); // -X
        var tris = new List<Vector3>();
        for (int i = 0; i + 2 < idx.Count; i += 3)
            tris.AddRange([c[idx[i]], c[idx[i + 1]], c[idx[i + 2]]]);
        return [.. tris];
    }

    [Fact]
    public void PlanarSlicer_TreePaint_EmitsLightningOnBedLayers()
    {
        // Tall box; Tree paint only at the top outside +X face. Full slice must put
        // IsLightning tree beads on the bottom layers (not just the tip band).
        float h = 90f;
        var mesh = BoxMesh(40f, 40f, h);
        float tipZ = h - 6f;
        var settings = new SliceSettings
        {
            LayerHeight = 3f,
            FirstLayerHeight = 3f,
            BeadWidth = Bead,
            LightningOverhangDeg = 30f,
            LightningBranchSpacingMm = 24f,
            LightningTargetSupportSelections = true,
            InfillPattern = InfillPattern.None,
            PaintMarks =
            [
                new PaintMark(new Vector3(42f, 0f, tipZ), Bead * 1.5f,
                    PaintMarkKind.Bridge, PaintBridgeRole.SupportBar, PaintSupportStyle.Tree),
                new PaintMark(new Vector3(42f, 8f, tipZ), Bead * 1.5f,
                    PaintMarkKind.Bridge, PaintBridgeRole.SupportBar, PaintSupportStyle.Tree),
            ],
        };

        var tp = PlanarSlicer.Slice([mesh], settings, null);
        Assert.True(tp.Layers.Count > 10, $"expected tall stack, got {tp.Layers.Count}");

        int lowerTree = 0, upperTree = 0;
        int mid = tp.Layers.Count / 2;
        for (int i = 0; i < tp.Layers.Count; i++)
        {
            bool has = tp.Layers[i].Moves.Any(m =>
                m.Kind == MoveKind.Extrude && m.IsLightning);
            if (!has) continue;
            if (i < mid) lowerTree++;
            else upperTree++;
        }

        Assert.True(upperTree > 0, "expected tree geometry near tip layers");
        Assert.True(lowerTree > 0,
            $"expected bed-side tree columns; lower={lowerTree} upper={upperTree} layers={tp.Layers.Count}");
        // At least a third of the lower half should carry tree posts.
        Assert.True(lowerTree >= mid / 3,
            $"tree columns too sparse on bed side: lower={lowerTree}/{mid}");
    }

    [Fact]
    public void TreeSupportGenerator_EmitsOnLowerLayers_WhenPartEngulfsTipXY()
    {
        // Tip outside small square; lower layers have a large square that engulfs tip XY.
        // Generator must still produce posts on the bed layer (push outside / no silent clip).
        int n = 16;
        var fill = new List<List<List<Vector2>>>(n);
        var heights = new List<float>(n);
        for (int i = 0; i < n; i++)
        {
            float half = i < n - 3 ? 80f : 40f; // lower: large, tip: small
            fill.Add([[
                new Vector2(-half, -half), new Vector2(half, -half),
                new Vector2(half, half), new Vector2(-half, half),
            ]]);
            heights.Add(3f);
        }
        var demand = new ManualDemandLayer[n];
        for (int i = 0; i < n; i++) demand[i] = new ManualDemandLayer();
        demand[n - 1].SupportBar.Add(new Vector2(50, 0)); // outside tip square, inside lower square

        var plan = TreeSupportPlanner.Build(fill, heights, new SliceSettings
        {
            BeadWidth = Bead,
            LayerHeight = 3f,
            LightningOverhangDeg = 30f,
            LightningBranchSpacingMm = 24f,
        }, demand);

        int layersWithEmit = 0;
        for (int li = 0; li < n; li++)
        {
            var layer = new ToolpathLayer(li, li * 3f) { Height = 3f };
            TreeSupportGenerator.Emit(plan.Layers[li], li * 3f, layer, Bead, fill[li]);
            if (layer.Moves.Any(m => m.Kind == MoveKind.Extrude && m.IsLightning))
                layersWithEmit++;
        }

        Assert.True(plan.Layers[0].Branches.Count > 0, "plan bed must have branches");
        Assert.True(layersWithEmit >= n - 1,
            $"expected nearly full-height emit, got {layersWithEmit}/{n} layers with tree extrudes");
    }
}
