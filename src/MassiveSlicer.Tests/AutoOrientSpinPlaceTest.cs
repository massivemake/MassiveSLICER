using System.Numerics;
using MassiveSlicer.App.Views;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;
using MassiveSlicer.Viewport.Scene;
using TkMatrix4 = OpenTK.Mathematics.Matrix4;
using TkQuaternion = OpenTK.Mathematics.Quaternion;
using TkVector3 = OpenTK.Mathematics.Vector3;

namespace MassiveSlicer.Tests;

/// <summary>
/// Auto Orient only spins about the world vertical and slides: nothing may tilt the part, a
/// flipped or laid-on-its-side part must stay that way, and the candidate spots must fit the bed.
/// </summary>
public sealed class AutoOrientSpinPlaceTest
{
    private static NodeTransform SomePlacement(TkQuaternion rotation) => new(
        position: new TkVector3(1200f, -340f, 915f),
        rotation: rotation,
        scale:    new TkVector3(1f, 1.5f, 0.8f),
        origin:   new TkVector3(10f, 5117f, 2743f));

    private static void AssertMatrixNear(TkMatrix4 expected, TkMatrix4 actual, float tol)
    {
        for (int r = 0; r < 4; r++)
        for (int c = 0; c < 4; c++)
            Assert.True(MathF.Abs(expected[r, c] - actual[r, c]) <= tol * MathF.Max(1f, MathF.Abs(expected[r, c])),
                $"[{r},{c}] expected {expected[r, c]} got {actual[r, c]}");
    }

    [Theory]
    [InlineData(0f, 0f, 0f, 37f)]
    [InlineData(180f, 0f, 0f, -90f)]    // flipped upside down
    [InlineData(90f, 0f, 25f, 135f)]    // on its side, already spun
    public void RotatedAbout_is_exactly_the_matrix_turn_about_the_pivot(float xDeg, float yDeg, float zDeg, float spinDeg)
    {
        var rot = TkQuaternion.FromEulerAngles(xDeg * MathF.PI / 180f, yDeg * MathF.PI / 180f, zDeg * MathF.PI / 180f);
        var before = SomePlacement(rot);
        var pivot = new TkVector3(1500f, 200f, 0f);
        float rad = spinDeg * MathF.PI / 180f;

        var after = before.RotatedAbout(pivot, TkQuaternion.FromAxisAngle(TkVector3.UnitZ, rad));

        var expected = before.ToMatrix()
                     * TkMatrix4.CreateTranslation(-pivot)
                     * TkMatrix4.CreateRotationZ(rad)
                     * TkMatrix4.CreateTranslation(pivot);
        AssertMatrixNear(expected, after.ToMatrix(), 2e-5f);
    }

    [Theory]
    [InlineData(0f)]      // flat, right way up
    [InlineData(180f)]    // flipped
    [InlineData(90f)]     // on its side
    public void Spinning_never_changes_which_way_the_part_faces_up(float xDeg)
    {
        var before = SomePlacement(TkQuaternion.FromAxisAngle(TkVector3.UnitX, xDeg * MathF.PI / 180f));
        var up = before.LocalAxis(2);
        foreach (float spin in new[] { 15f, 90f, 173f, 345f })
        {
            var after = before.RotatedAbout(new TkVector3(300f, -40f, 0f),
                TkQuaternion.FromAxisAngle(TkVector3.UnitZ, spin * MathF.PI / 180f));
            // The object's own Z keeps exactly the same vertical component — no tilt at all.
            Assert.Equal(up.Z, after.LocalAxis(2).Z, 5);
            // Heights are untouched: the pivot's Z and every point's Z stay put.
            Assert.Equal(before.Position.Z, after.Position.Z, 3);
        }
    }

    [Fact]
    public void Spin_keeps_every_vertex_height_for_placement_and_matrix_nodes()
    {
        var pts = new[] { new TkVector3(0, 0, 0), new TkVector3(400, 0, 0), new TkVector3(0, 250, 90), new TkVector3(-30, 80, 700) };
        var placed = new SceneNode();
        placed.SetPlacement(SomePlacement(TkQuaternion.FromAxisAngle(TkVector3.UnitX, MathF.PI)));
        var matrixDriven = new SceneNode { LocalTransform = SomePlacement(TkQuaternion.Identity).ToMatrix() };

        foreach (var node in new[] { placed, matrixDriven })
        {
            var before = node.WorldTransform;
            ViewportView.SpinNodeAboutWorldVertical(node, new Vector2(1000f, 500f), 63f);
            var after = node.WorldTransform;
            foreach (var p in pts)
                Assert.Equal(TkVector3.TransformPosition(p, before).Z, TkVector3.TransformPosition(p, after).Z, 2);
        }
    }

    [Fact]
    public void Candidate_transform_spins_about_the_pivot_and_never_moves_Z()
    {
        var pivot = new Vector2(1000f, 500f);
        var m = PlacementSearch.Transform(new PlacementSearch.Candidate(90f, 50f, -20f), pivot);
        var p = Vector3.Transform(new Vector3(1100f, 500f, 812f), m);
        Assert.Equal(1000f + 50f, p.X, 3);
        Assert.Equal(500f + 100f - 20f, p.Y, 3);
        Assert.Equal(812f, p.Z, 5);
        Assert.Equal(new Vector3(1000f + 50f, 500f - 20f, 7f), Vector3.Transform(new Vector3(1000f, 500f, 7f), m));
    }

    private static readonly Vector2[] Square200 =
        [new(-100, -100), new(100, -100), new(100, 100), new(-100, 100)];

    private static Vector2[] Offset(Vector2[] pts, Vector2 d) => pts.Select(p => p + d).ToArray();

    [Fact]
    public void Candidates_start_with_the_current_pose_and_all_others_fit_a_rectangular_bed()
    {
        var bed = new BedCellConfig { Origin = new Float3(0, 0, 0), BaseData = new Float3(0, 0, 0), Width = 1000f, Depth = 600f };
        var part = Offset(Square200, new Vector2(2000f, 0f));   // well away from the bed
        var cands = PlacementSearch.Generate(part, new Vector2(2000f, 0f), bed, Vector2.Zero);

        Assert.True(cands[0].IsCurrentPose);                     // kept even though it is off the bed
        Assert.True(cands.Count > 1);
        foreach (var c in cands.Skip(1))
        {
            var m = PlacementSearch.Transform(c, new Vector2(2000f, 0f));
            var moved = part.Select(p => { var q = Vector3.Transform(new Vector3(p, 0f), m); return new Vector2(q.X, q.Y); }).ToList();
            Assert.True(PlacementSearch.Fits(moved, Vector2.Zero, bed, Vector2.Zero), c.ToString());
        }
        // Every spin step is represented.
        Assert.Equal(24, cands.Skip(1).Select(c => c.SpinDeg).Distinct().Count());
    }

    [Fact]
    public void Rotary_bed_uses_the_disc_not_a_square()
    {
        var bed = new BedCellConfig { Origin = new Float3(0, 0, 0), BaseData = new Float3(0, 0, 0), Diameter = 1000f };
        // 900 mm usable diameter: a 200 mm square fits at the centre but not with a corner past the 450 mm usable radius.
        Assert.True(PlacementSearch.Fits(Square200, Vector2.Zero, bed, Vector2.Zero));
        Assert.False(PlacementSearch.Fits(Square200, new Vector2(360f, 0f), bed, Vector2.Zero));   // corner at 470 mm
        Assert.True(PlacementSearch.Fits(Square200, new Vector2(300f, 0f), bed, Vector2.Zero));
    }

    [Fact]
    public void Ranking_prefers_reach_then_margin_then_staying_put()
    {
        var here = new PlacementSearch.Candidate(0f, 0f, 0f);
        var far  = new PlacementSearch.Candidate(0f, 400f, 0f);
        var spun = new PlacementSearch.Candidate(345f, 0f, 0f);
        var list = new List<(PlacementSearch.Candidate c, PlacementSearch.Score s)>
        {
            (far,  new(0, 20.2f)),
            (here, new(1, 60f)),     // best margin but one sample out of reach: loses
            (spun, new(0, 20.0f)),   // same margin (within 0.5°) as far, but no slide: wins
        };
        list.Sort(PlacementSearch.Compare);
        Assert.Equal(spun, list[0].c);
        Assert.Equal(far, list[1].c);
        Assert.Equal(here, list[2].c);
        Assert.Equal(-15f, PlacementSearch.Wrap(345f));
    }

    [Fact]
    public void Hull_contains_the_footprint()
    {
        var rnd = new Random(7);
        var pts = Enumerable.Range(0, 500).Select(_ => new Vector2(rnd.NextSingle() * 300f, rnd.NextSingle() * 120f)).ToList();
        var hull = PlacementSearch.Hull(pts);
        Assert.InRange(hull.Count, 3, 60);
        Assert.Equal(pts.Min(p => p.X), hull.Min(p => p.X));
        Assert.Equal(pts.Max(p => p.Y), hull.Max(p => p.Y));
    }
}
