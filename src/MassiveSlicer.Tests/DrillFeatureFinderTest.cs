using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;
using MassiveSlicer.ViewModels;
using Xunit;

namespace MassiveSlicer.Tests;

public sealed class DrillFeatureFinderTest
{
    [Fact]
    public void Ring_finds_the_round_hole_and_keeps_the_outer_rim_as_an_edge()
    {
        Ring(new Vector3(50, 50, 10), 40f, 10f, out var pos, out var idx);
        var survey = DrillFeatureFinder.Find(pos, idx);
        Assert.Single(survey.Holes);
        var hole = survey.Holes[0];
        Assert.Equal(20f, hole.DetectedDiameterMm, 1);
        Assert.InRange(hole.Position.X, 49f, 51f);
        Assert.InRange(hole.Position.Y, 49f, 51f);
        Assert.True(hole.Axis.Z > 0.5f, $"axis {hole.Axis}");
        Assert.Equal(DrillCutKind.Round, hole.Cut);
        Assert.Equal("Hole", hole.ShapeName);
        Assert.NotEmpty(survey.Edges);
    }

    [Fact]
    public void Two_rings_find_two_holes()
    {
        Ring(new Vector3(20, 20, 5), 12f, 4f, out var aPos, out var aIdx);
        Ring(new Vector3(70, 20, 5), 12f, 5f, out var bPos, out var bIdx);
        var pos = new Vector3[aPos.Length + bPos.Length];
        aPos.CopyTo(pos, 0);
        bPos.CopyTo(pos, aPos.Length);
        var idx = new int[aIdx.Length + bIdx.Length];
        aIdx.CopyTo(idx, 0);
        for (int i = 0; i < bIdx.Length; i++)
            idx[aIdx.Length + i] = bIdx[i] + aPos.Length;
        var survey = DrillFeatureFinder.Find(pos, idx);
        Assert.Equal(2, survey.Holes.Count);
        Assert.Contains(survey.Holes, h => MathF.Abs(h.DetectedDiameterMm - 8f) < 1f);
        Assert.Contains(survey.Holes, h => MathF.Abs(h.DetectedDiameterMm - 10f) < 1f);
    }

    [Fact]
    public void A_closed_plate_finds_the_through_hole_once()
    {
        ClosedPlate(out var pos, out var idx);
        var survey = DrillFeatureFinder.Find(pos, idx);
        var hole = Assert.Single(survey.Holes);
        Assert.InRange(hole.DetectedDiameterMm, 15f, 17f);
        Assert.InRange(hole.SpanMm, 11f, 13f);
        Assert.True(hole.Axis.Z > 0.5f, $"axis {hole.Axis}");
        Assert.InRange(hole.Position.Z, 11f, 13f);
        Assert.Equal(DrillCutKind.Round, hole.Cut);
        Assert.NotEmpty(survey.Edges);
    }

    [Fact]
    public void A_narrow_ring_is_a_hole_when_the_middle_is_open()
    {
        // The face outside the rim is only 1 mm wide, so samples past the rim
        // miss it. The opening is still a hole.
        Ring(new Vector3(0, 0, 5), 11f, 10f, out var pos, out var idx);
        var survey = DrillFeatureFinder.Find(pos, idx);
        var hole = Assert.Single(survey.Holes);
        Assert.InRange(hole.DetectedDiameterMm, 19f, 22f);
        Assert.NotNull(hole.Rim);
        Assert.True(hole.Rim!.Count >= 6);
    }

    [Fact]
    public void A_wall_that_crosses_the_hole_plane_does_not_hide_the_hole()
    {
        Ring(new Vector3(50, 50, 10), 40f, 10f, out var ringPos, out var ringIdx);
        var pos = new Vector3[ringPos.Length + 3];
        ringPos.CopyTo(pos, 0);
        pos[ringPos.Length] = new Vector3(0, 0, 0);
        pos[ringPos.Length + 1] = new Vector3(100, 0, 0);
        pos[ringPos.Length + 2] = new Vector3(50, 100, 40);
        var idx = new int[ringIdx.Length + 3];
        ringIdx.CopyTo(idx, 0);
        idx[ringIdx.Length] = ringPos.Length;
        idx[ringIdx.Length + 1] = ringPos.Length + 1;
        idx[ringIdx.Length + 2] = ringPos.Length + 2;
        var survey = DrillFeatureFinder.Find(pos, idx);
        Assert.Contains(survey.Holes, h => MathF.Abs(h.DetectedDiameterMm - 20f) < 1.5f);
    }

    [Fact]
    public void A_closed_puck_has_edges_and_no_hole()
    {
        ClosedPuck(out var pos, out var idx);
        var survey = DrillFeatureFinder.Find(pos, idx);
        Assert.Empty(survey.Holes);
        Assert.NotEmpty(survey.Edges);
    }

    [Fact]
    public void A_slot_is_milled_along_its_length()
    {
        Slot(out var pos, out var idx);
        var survey = DrillFeatureFinder.Find(pos, idx);
        var hole = Assert.Single(survey.Holes);
        Assert.Equal(DrillCutKind.Profile, hole.Cut);
        Assert.Equal("Slot", hole.ShapeName);
        Assert.NotEmpty(survey.Edges);

        var tp = AdaMillPlanner.Generate(Req([FromSite(hole, 8f)]));
        var cut = CutPoints(tp, -1f);
        Assert.True(cut.Count >= 2, "slot path has no cut");
        float minX = cut.Min(p => p.X), maxX = cut.Max(p => p.X);
        Assert.True(maxX - minX > 10f, $"slot travel {minX:0.0}..{maxX:0.0}");
        Assert.All(cut, p => Assert.InRange(p.Y, 36f, 44f));
    }

    [Fact]
    public void Hole_depth_follows_the_wall_at_that_hole()
    {
        Ring(new Vector3(50, 50, 10), 40f, 10f, out var pos, out var idx);
        // A lower ring of vertices sits in the hole's probe and is not part of the rim.
        var withWall = new Vector3[pos.Length + 16];
        pos.CopyTo(withWall, 0);
        for (int i = 0; i < 16; i++)
        {
            float a = MathF.Tau * i / 16f;
            withWall[pos.Length + i] = new Vector3(50 + MathF.Cos(a) * 9f, 50 + MathF.Sin(a) * 9f, 0);
        }
        var survey = DrillFeatureFinder.Find(withWall, idx);
        var hole = Assert.Single(survey.Holes);
        Assert.InRange(hole.SpanMm, 8f, 12f);
        Assert.Equal(DrillCutKind.Round, hole.Cut);
        Assert.InRange(hole.Position.Z, 9f, 11f);
        Assert.Equal(15f, hole.DepthMm(5f), 1);
        hole.DepthOverrideMm = 7f;
        Assert.Equal(7f, hole.DepthMm(5f), 0.01f);
    }

    [Fact]
    public void An_off_hole_is_only_the_circle_and_the_on_line_matches_the_drill_depth()
    {
        var rim = new List<Vector3> { new(5, 0, 10), new(0, 5, 10), new(5, 0, 10) };
        var off = new DrillSite
        {
            Id = 1,
            Kind = DrillSiteKind.Hole,
            Position = new Vector3(0, 0, 10),
            Axis = Vector3.UnitZ,
            DetectedDiameterMm = 10,
            SpanMm = 40,
            Included = false,
            Rim = rim,
        };
        var waiting = DrillMarkers.Build([off], [], -1, 5f);
        Assert.NotEmpty(waiting);
        Assert.All(waiting, s => Assert.Equal(10f, s.Pos.Z, 3));

        var on = new DrillSite
        {
            Id = 2,
            Kind = DrillSiteKind.Hole,
            Position = new Vector3(0, 0, 10),
            Axis = Vector3.UnitZ,
            DetectedDiameterMm = 10,
            SpanMm = 8,
            Included = true,
            Rim = rim,
        };
        var marked = DrillMarkers.Build([on], [], 2, 2f);
        float tip = marked.Min(s => s.Pos.Z);
        Assert.Equal(0f, tip, 3);
        Assert.Contains(marked, s => s.Pos.Z > 9f);

        on.DepthOverrideMm = 12.5f;
        marked = DrillMarkers.Build([on], [], 2, 2f);
        tip = marked.Min(s => s.Pos.Z);
        Assert.Equal(-2.5f, tip, 3);
    }

    [Fact]
    public void Ctrl_marks_an_edge_alt_drops_the_list_and_shift_places_a_face_hole()
    {
        var holeRim = new List<Vector3> { new(10, 0, 0), new(0, 10, 0), new(-10, 0, 0), new(10, 0, 0) };
        var hole = new DrillSite
        {
            Id = 1,
            Kind = DrillSiteKind.Hole,
            Position = Vector3.Zero,
            Axis = Vector3.UnitZ,
            DetectedDiameterMm = 20,
            SpanMm = 8,
            Included = false,
            Rim = holeRim,
        };
        var edge = new List<Vector3> { new(100, 0, 0), new(120, 0, 0), new(120, 20, 0), new(100, 0, 0) };
        var vm = new ViewportViewModel();
        vm.SetDrillSurvey(new DrillSurvey { Holes = [hole], Edges = [edge] }, []);
        Assert.Empty(vm.DrillHoles);

        Click(vm, new Vector3(10, 0, 0), DrillClickKind.MarkEdge);
        Assert.Single(vm.DrillHoles);
        Assert.StartsWith("Hole", vm.DrillHoles[0].Name);

        Click(vm, new Vector3(300, 0, 0), DrillClickKind.MarkEdge);
        Assert.Single(vm.DrillHoles);

        Click(vm, new Vector3(10, 0, 0), DrillClickKind.RemoveListed);
        Assert.Empty(vm.DrillHoles);
        Assert.Empty(vm.CollectDrillPoints());

        Click(vm, new Vector3(110, 0, 0), DrillClickKind.MarkEdge);
        var marked = Assert.Single(vm.CollectDrillPoints());
        Assert.StartsWith("Hole", vm.DrillHoles[0].Name);

        Click(vm, new Vector3(200, 0, 0), DrillClickKind.AddFace);
        Assert.Equal(2, vm.DrillHoles.Count);
        Assert.True(vm.SelectedDrillIsFace);

        Click(vm, new Vector3(200, 0, 0), DrillClickKind.RemoveListed);
        Assert.Equal(2, vm.DrillHoles.Count);

        Click(vm, new Vector3(200, 0, 0), DrillClickKind.RemoveFace);
        var left = Assert.Single(vm.DrillHoles);
        Assert.StartsWith("Hole", left.Name);
        Assert.False(vm.SelectedDrillIsFace);

        Click(vm, marked.Position, DrillClickKind.RemoveListed);
        Assert.Empty(vm.DrillHoles);
        Click(vm, new Vector3(110, 0, 0), DrillClickKind.MarkEdge);
        Assert.Single(vm.DrillHoles);
    }

    static void Click(ViewportViewModel vm, Vector3 hit, DrillClickKind kind) =>
        vm.ApplyDrillClick(hit, Vector3.UnitZ, -Vector3.UnitZ, kind, 6f);

    [Fact]
    public void Empty_drill_list_cuts_nothing()
    {
        var tp = AdaMillPlanner.Generate(Req(new DrillPoint[0]));
        Assert.Empty(tp.Layers);
    }

    [Fact]
    public void A_point_plunges_along_its_own_axis_by_its_own_depth()
    {
        var mouth = new Vector3(10, 20, 30);
        var tp = AdaMillPlanner.Generate(Req(
        [
            new DrillPoint(mouth, Vector3.UnitX, 15f, 6f),
        ]));
        var mills = Mills(tp);
        Assert.NotEmpty(mills);
        Assert.All(mills, m => Assert.True(m.Normal.X > 0.9f, $"normal {m.Normal}"));
        var tip = mills.SelectMany(m => new[] { m.From, m.To })
            .OrderBy(p => Vector3.Dot(p - mouth, Vector3.UnitX))
            .First();
        Assert.InRange(Vector3.Dot(mouth - tip, Vector3.UnitX), 14.5f, 15.5f);
    }

    [Fact]
    public void A_wider_hole_bores_a_circle_at_that_diameter()
    {
        var mouth = new Vector3(0, 0, 40);
        var tp = AdaMillPlanner.Generate(Req(
        [
            new DrillPoint(mouth, Vector3.UnitZ, 12f, 20f),
            new DrillPoint(new Vector3(80, 0, 40), Vector3.UnitZ, 4f, 6f),
        ]));
        var mills = Mills(tp);
        float pathR = (20f - 6f) * 0.5f;
        Assert.Contains(mills, m => Radial(m.To, mouth) > pathR - 0.6f && Radial(m.To, mouth) < pathR + 0.6f
            && m.To.Z < 30f);
        Assert.Contains(mills, m => MathF.Abs(m.To.X - 80f) < 0.6f && m.To.Z < 38f);
    }

    [Fact]
    public void A_square_hole_is_milled_around_its_outline()
    {
        SquareHole(out var pos, out var idx);
        var survey = DrillFeatureFinder.Find(pos, idx);
        var hole = Assert.Single(survey.Holes, h => h.ShapeName == "Square");
        Assert.Equal(DrillCutKind.Profile, hole.Cut);
        Assert.DoesNotContain(survey.Holes, h => h.DetectedDiameterMm > 50f);
        Assert.InRange(hole.Position.X, 68f, 72f);
        Assert.InRange(hole.Position.Y, 68f, 72f);

        var tp = AdaMillPlanner.Generate(Req([FromSite(hole, hole.SpanMm)]));
        var cut = CutPoints(tp, 1f);
        Assert.Contains(cut, p => Vector2.Distance(new Vector2(p.X, p.Y), new Vector2(63, 63)) < 2.5f);
        Assert.Contains(cut, p => Vector2.Distance(new Vector2(p.X, p.Y), new Vector2(77, 77)) < 2.5f);
        float reach = cut.Max(p => Vector2.Distance(new Vector2(p.X, p.Y), new Vector2(70, 70)));
        Assert.True(reach > 6f, $"square path stays at the center, reach {reach:0.0}");
    }

    [Fact]
    public void A_square_wall_belongs_to_the_square_cutout()
    {
        SquareHole(out var pos, out var idx);
        var survey = DrillFeatureFinder.Find(pos, idx);
        var square = Assert.Single(survey.Holes, h => h.ShapeName == "Square");
        Assert.DoesNotContain(survey.Holes, h => h.ShapeName == "Slot");
        var walls = survey.Edges.Where(e => DrillFeatureFinder.OpeningForWall(e, survey.Holes) == square).ToList();
        Assert.NotEmpty(walls);
        Assert.All(walls, wall => Assert.Equal("Square", DrillFeatureFinder.OpeningForWall(wall, survey.Holes)!.ShapeName));
    }

    [Fact]
    public void A_half_round_notch_on_the_outer_edge_is_a_hole()
    {
        PlateWithNotch(out var pos, out var idx);
        var survey = DrillFeatureFinder.Find(pos, idx);
        var notch = Assert.Single(survey.Holes, h => h.Cut == DrillCutKind.Round && h.DetectedDiameterMm > 6f);
        Assert.InRange(notch.DetectedDiameterMm, 7.5f, 8.5f);
        Assert.InRange(notch.Position.X, 39f, 41f);
        Assert.InRange(notch.Position.Y, 9f, 11f);
        Assert.DoesNotContain(survey.Holes, h => h.ShapeName == "Slot");
    }

    [Fact]
    public void A_plain_rectangle_outline_is_not_a_semicircle()
    {
        var pos = new[]
        {
            new Vector3(0, 0, 5), new Vector3(40, 0, 5), new Vector3(40, 20, 5), new Vector3(0, 20, 5),
        };
        var idx = new[] { 0, 1, 2, 0, 2, 3 };
        var survey = DrillFeatureFinder.Find(pos, idx);
        Assert.Empty(survey.Holes);
    }

    [Fact]
    public void A_hexagon_is_a_polygon_not_a_round_bore()
    {
        ClosedPlate(6, 40f, 10f, 12f, out var pos, out var idx);
        var survey = DrillFeatureFinder.Find(pos, idx);
        var hole = Assert.Single(survey.Holes, h => h.DetectedDiameterMm < 30f);
        Assert.Equal(DrillCutKind.Profile, hole.Cut);
        Assert.Equal("Polygon", hole.ShapeName);

        var tp = AdaMillPlanner.Generate(Req([FromSite(hole, hole.SpanMm)]));
        var radii = CutPoints(tp, 1f)
            .Select(p => new Vector2(p.X, p.Y).Length())
            .ToList();
        Assert.NotEmpty(radii);
        float spread = radii.Max() - radii.Min();
        Assert.True(spread > 0.45f, $"hex path is one radius, spread {spread:0.00}");
    }

    [Fact]
    public void A_counterbore_cuts_the_step_then_the_through_hole()
    {
        SteppedHole(majorR: 10f, minorR: 4f, top: 20f, step: 14f, cone: false, out var pos, out var idx);
        var survey = DrillFeatureFinder.Find(pos, idx);
        var hole = Assert.Single(survey.Holes);
        Assert.Equal(DrillCutKind.Counterbore, hole.Cut);
        Assert.InRange(hole.DetectedDiameterMm, 18f, 22f);
        Assert.InRange(hole.MinorDiameterMm, 6.5f, 9.5f);
        Assert.InRange(hole.ShoulderDepthMm, 4f, 8.5f);

        var mouth = new Vector3(0, 0, 20);
        var tp = AdaMillPlanner.Generate(Req(
        [
            new DrillPoint(mouth, Vector3.UnitZ, 20f, 20f, DrillCutKind.Counterbore, 8f, 6f),
        ]));
        var mills = Mills(tp);
        Assert.Contains(mills, m => Radial(m.To, mouth) > 6.2f && Radial(m.To, mouth) < 7.8f && m.To.Z > 12f && m.To.Z < 16f);
        Assert.Contains(mills, m => Radial(m.To, mouth) < 1.6f && m.To.Z < 2f);
        Assert.DoesNotContain(mills, m => Radial(m.To, mouth) > 5f && m.To.Z < 2f);
    }

    [Fact]
    public void A_gentle_countersink_stays_a_countersink()
    {
        SteppedHole(majorR: 8f, minorR: 3f, top: 24f, step: 8f, cone: true, out var pos, out var idx);
        var hole = Assert.Single(DrillFeatureFinder.Find(pos, idx).Holes);
        Assert.Equal(DrillCutKind.Countersink, hole.Cut);
        Assert.InRange(hole.ShoulderDepthMm, 12f, 18f);
        Assert.InRange(hole.MinorDiameterMm, 5f, 8f);
    }

    [Fact]
    public void A_short_countersink_is_not_a_counterbore()
    {
        SteppedHole(majorR: 10f, minorR: 5f, top: 30f, step: 26f, cone: true, out var pos, out var idx);
        var hole = Assert.Single(DrillFeatureFinder.Find(pos, idx).Holes);
        Assert.Equal(DrillCutKind.Countersink, hole.Cut);
        Assert.InRange(hole.DetectedDiameterMm, 18f, 22f);
        Assert.InRange(hole.MinorDiameterMm, 8f, 12f);
        Assert.InRange(hole.ShoulderDepthMm, 2.5f, 7f);
    }

    [Fact]
    public void A_large_hole_clears_the_middle()
    {
        var mouth = new Vector3(0, 0, 20);
        var tp = AdaMillPlanner.Generate(Req(
        [
            new DrillPoint(mouth, Vector3.UnitZ, 10f, 30f),
        ]));
        var deep = Mills(tp).Where(m => m.To.Z < 12f).ToList();
        Assert.Contains(deep, m => Radial(m.To, mouth) > 10f);
        Assert.Contains(deep, m => Radial(m.To, mouth) < 2f);
    }

    [Fact]
    public void A_counterbore_clears_the_wide_section()
    {
        var mouth = new Vector3(0, 0, 20);
        var tp = AdaMillPlanner.Generate(Req(
        [
            new DrillPoint(mouth, Vector3.UnitZ, 20f, 30f, DrillCutKind.Counterbore, 8f, 6f),
        ]));
        var mills = Mills(tp);
        Assert.Contains(mills, m => Radial(m.To, mouth) > 10f && m.To.Z > 12f && m.To.Z < 16f);
        Assert.Contains(mills, m => Radial(m.To, mouth) < 3f && m.To.Z > 12f && m.To.Z < 16f);
        Assert.DoesNotContain(mills, m => Radial(m.To, mouth) > 5f && m.To.Z < 2f);
    }

    [Fact]
    public void A_countersink_clears_between_the_mouth_and_the_pilot()
    {
        var mouth = new Vector3(0, 0, 30);
        var tp = AdaMillPlanner.Generate(Req(
        [
            new DrillPoint(mouth, Vector3.UnitZ, 20f, 40f, DrillCutKind.Countersink, 8f, 6f),
        ]));
        var mills = Mills(tp);
        Assert.Contains(mills, m => Radial(m.To, mouth) > 14f && m.To.Z > 27f);
        Assert.Contains(mills, m => Radial(m.To, mouth) < 8f && Radial(m.To, mouth) > 2f && m.To.Z > 26f);
    }

    [Fact]
    public void A_countersink_cuts_a_cone_then_the_pilot()
    {
        SteppedHole(majorR: 12f, minorR: 4f, top: 24f, step: 12f, cone: true, out var pos, out var idx);
        var survey = DrillFeatureFinder.Find(pos, idx);
        var hole = Assert.Single(survey.Holes);
        Assert.Equal(DrillCutKind.Countersink, hole.Cut);
        Assert.InRange(hole.DetectedDiameterMm, 20f, 26f);
        Assert.InRange(hole.MinorDiameterMm, 6.5f, 9.5f);
        Assert.InRange(hole.ShoulderDepthMm, 8f, 14f);

        var mouth = new Vector3(0, 0, 30);
        var tp = AdaMillPlanner.Generate(Req(
        [
            new DrillPoint(mouth, Vector3.UnitZ, 20f, 24f, DrillCutKind.Countersink, 8f, 12f),
        ]));
        var mills = Mills(tp);
        Assert.Contains(mills, m => Radial(m.To, mouth) > 7f && m.To.Z > 27f);
        Assert.Contains(mills, m => Radial(m.To, mouth) > 1.5f && Radial(m.To, mouth) < 4.5f && m.To.Z > 18f && m.To.Z < 23f);
        Assert.Contains(mills, m => Radial(m.To, mouth) < 1.6f && m.To.Z < 12f);
    }

    [Fact]
    public void Ctrl_click_on_a_square_keeps_the_outline()
    {
        var square = new List<Vector3>
        {
            new(0, 0, 0), new(20, 0, 0), new(20, 20, 0), new(0, 20, 0), new(0, 0, 0),
        };
        var vm = new ViewportViewModel();
        vm.SetDrillSurvey(new DrillSurvey { Edges = [square] }, []);
        Click(vm, new Vector3(10, 0, 0), DrillClickKind.MarkEdge);
        var row = Assert.Single(vm.DrillHoles);
        Assert.StartsWith("Square", row.Name);
        var point = Assert.Single(vm.CollectDrillPoints());
        Assert.Equal(DrillCutKind.Profile, point.Cut);
        Assert.NotNull(point.Profile);
        Assert.True(point.Profile!.Count >= 4);
    }

    static DrillPoint FromSite(DrillSite hole, float depth) => new(
        hole.Position,
        hole.Axis.LengthSquared() > 1e-8f ? Vector3.Normalize(hole.Axis) : Vector3.UnitZ,
        depth,
        hole.DiameterMm,
        hole.Cut,
        hole.MinorDiameterMm,
        hole.ShoulderDepthMm,
        hole.Rim);

    static List<Vector3> CutPoints(Toolpath tp, float maxZ)
    {
        var pts = new List<Vector3>();
        foreach (var m in Mills(tp))
        {
            if (m.From.Z <= maxZ) pts.Add(m.From);
            if (m.To.Z <= maxZ) pts.Add(m.To);
        }
        return pts;
    }

    static float Radial(Vector3 p, Vector3 mouth) =>
        new Vector2(p.X - mouth.X, p.Y - mouth.Y).Length();

    static List<ToolpathMove> Mills(Toolpath tp) =>
        tp.Layers.SelectMany(l => l.Moves).Where(m => m.Kind == MoveKind.Mill).ToList();

    static AdaMillRequest Req(IReadOnlyList<DrillPoint> points) => new()
    {
        Settings = new AdaMachiningSettings
        {
            Operation = MillOperationKind.Drilling,
            ToolDiameterMm = 6,
            RetractHeightMm = 10,
            FeedHeightMm = 2,
            DrillingBreakthroughMm = 5,
        },
        Positions = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
        Normals = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
        Indices = [0, 1, 2],
        DrillPoints = points,
    };

    static void Ring(Vector3 center, float outerR, float innerR, out Vector3[] pos, out int[] idx)
    {
        const int n = 16;
        pos = new Vector3[n * 2];
        var tris = new List<int>();
        for (int i = 0; i < n; i++)
        {
            float a = MathF.Tau * i / n;
            pos[i] = new Vector3(center.X + MathF.Cos(a) * outerR, center.Y + MathF.Sin(a) * outerR, center.Z);
            pos[n + i] = new Vector3(center.X + MathF.Cos(a) * innerR, center.Y + MathF.Sin(a) * innerR, center.Z);
        }
        for (int i = 0; i < n; i++)
        {
            int i0 = i;
            int i1 = (i + 1) % n;
            int j0 = n + i;
            int j1 = n + (i + 1) % n;
            tris.Add(i0); tris.Add(i1); tris.Add(j1);
            tris.Add(i0); tris.Add(j1); tris.Add(j0);
        }
        idx = tris.ToArray();
    }

    static void ClosedPlate(out Vector3[] pos, out int[] idx) =>
        ClosedPlate(16, 40f, 8f, 12f, out pos, out idx);

    static void ClosedPlate(int n, float outer, float inner, float height, out Vector3[] pos, out int[] idx)
    {
        float z0 = 0f, z1 = height;
        pos = new Vector3[n * 4];
        for (int i = 0; i < n; i++)
        {
            float a = MathF.Tau * i / n;
            float c = MathF.Cos(a), s = MathF.Sin(a);
            pos[i] = new Vector3(c * outer, s * outer, z1);
            pos[n + i] = new Vector3(c * inner, s * inner, z1);
            pos[2 * n + i] = new Vector3(c * outer, s * outer, z0);
            pos[3 * n + i] = new Vector3(c * inner, s * inner, z0);
        }
        var tris = new List<int>();
        void Tri(int a, int b, int c) { tris.Add(a); tris.Add(b); tris.Add(c); }
        for (int i = 0; i < n; i++)
        {
            int i1 = (i + 1) % n;
            int to = i, to1 = i1;
            int ti = n + i, ti1 = n + i1;
            int bo = 2 * n + i, bo1 = 2 * n + i1;
            int bi = 3 * n + i, bi1 = 3 * n + i1;
            Tri(to, ti1, ti);
            Tri(to, to1, ti1);
            Tri(bo, bi, bi1);
            Tri(bo, bi1, bo1);
            Tri(to, bo, bo1);
            Tri(to, bo1, to1);
            Tri(ti, bi1, bi);
            Tri(ti, ti1, bi1);
        }
        idx = tris.ToArray();
    }

    static void ClosedPuck(out Vector3[] pos, out int[] idx)
    {
        const int n = 16;
        const float radius = 20f, height = 10f;
        pos = new Vector3[2 + 2 * n];
        pos[0] = new Vector3(0, 0, height);
        pos[1] = new Vector3(0, 0, 0);
        for (int i = 0; i < n; i++)
        {
            float a = MathF.Tau * i / n;
            pos[2 + i] = new Vector3(MathF.Cos(a) * radius, MathF.Sin(a) * radius, height);
            pos[2 + n + i] = new Vector3(MathF.Cos(a) * radius, MathF.Sin(a) * radius, 0);
        }
        var tris = new List<int>();
        void Tri(int a, int b, int c) { tris.Add(a); tris.Add(b); tris.Add(c); }
        for (int i = 0; i < n; i++)
        {
            int i1 = (i + 1) % n;
            int top = 2 + i, top1 = 2 + i1;
            int bot = 2 + n + i, bot1 = 2 + n + i1;
            Tri(0, top, top1);
            Tri(1, bot1, bot);
            Tri(top, bot, bot1);
            Tri(top, bot1, top1);
        }
        idx = tris.ToArray();
    }

    static void PlateWithNotch(out Vector3[] pos, out int[] idx)
    {
        var outline = new List<Vector3>
        {
            new(0, 0, 10), new(0, 20, 10), new(40, 20, 10),
        };
        for (int i = 0; i <= 8; i++)
        {
            float ang = MathF.PI / 2f + MathF.PI * i / 8f;
            outline.Add(new Vector3(40f + MathF.Cos(ang) * 4f, 10f + MathF.Sin(ang) * 4f, 10f));
        }
        outline.Add(new Vector3(40, 0, 10));
        var verts = new List<Vector3> { new(20, 10, 10) };
        verts.AddRange(outline);
        var tris = new List<int>();
        for (int i = 0; i < outline.Count; i++)
        {
            int i1 = i == outline.Count - 1 ? 1 : i + 2;
            tris.Add(0);
            tris.Add(i + 1);
            tris.Add(i1);
        }
        pos = verts.ToArray();
        idx = tris.ToArray();
    }

    static void SquareHole(out Vector3[] pos, out int[] idx)
    {
        pos =
        [
            new(0, 0, 12), new(100, 0, 12), new(100, 100, 12), new(0, 100, 12),
            new(60, 60, 12), new(80, 60, 12), new(80, 80, 12), new(60, 80, 12),
            new(0, 0, 0), new(100, 0, 0), new(100, 100, 0), new(0, 100, 0),
            new(60, 60, 0), new(80, 60, 0), new(80, 80, 0), new(60, 80, 0),
        ];
        var tris = new List<int>();
        void Tri(int a, int b, int c) { tris.Add(a); tris.Add(b); tris.Add(c); }
        void Top(int o, int o1, int i, int i1) { Tri(o, i1, i); Tri(o, o1, i1); }
        void Bot(int o, int o1, int i, int i1) { Tri(o, i, i1); Tri(o, i1, o1); }
        void Wall(int t, int t1, int b, int b1) { Tri(t, b, b1); Tri(t, b1, t1); }
        Top(0, 1, 4, 5); Top(1, 2, 5, 6); Top(2, 3, 6, 7); Top(3, 0, 7, 4);
        Bot(8, 9, 12, 13); Bot(9, 10, 13, 14); Bot(10, 11, 14, 15); Bot(11, 8, 15, 12);
        Wall(0, 1, 8, 9); Wall(1, 2, 9, 10); Wall(2, 3, 10, 11); Wall(3, 0, 11, 8);
        Wall(4, 5, 12, 13); Wall(5, 6, 13, 14); Wall(6, 7, 14, 15); Wall(7, 4, 15, 12);
        idx = tris.ToArray();
    }

    static void SteppedHole(float majorR, float minorR, float top, float step, bool cone, out Vector3[] pos, out int[] idx)
    {
        const int n = 32;
        const float outer = 30f;
        var verts = new List<Vector3>(n * 6);
        void Ring(float z, float radius)
        {
            for (int i = 0; i < n; i++)
            {
                float a = MathF.Tau * i / n;
                verts.Add(new Vector3(MathF.Cos(a) * radius, MathF.Sin(a) * radius, z));
            }
        }
        Ring(top, outer);
        Ring(top, majorR);
        if (!cone) Ring(step, majorR);
        Ring(step, minorR);
        Ring(0f, minorR);
        Ring(0f, outer);
        var tris = new List<int>();
        void Tri(int a, int b, int c) { tris.Add(a); tris.Add(b); tris.Add(c); }
        void Band(int r0, int r1, bool bottom)
        {
            for (int i = 0; i < n; i++)
            {
                int i1 = (i + 1) % n;
                int a = r0 * n + i, a1 = r0 * n + i1;
                int b = r1 * n + i, b1 = r1 * n + i1;
                if (bottom)
                {
                    Tri(a, b, b1);
                    Tri(a, b1, a1);
                }
                else
                {
                    Tri(a, b1, b);
                    Tri(a, a1, b1);
                }
            }
        }
        // 0 outer top, 1 major top, then shoulder or cone, then minor bottom, outer bottom.
        Band(0, 1, bottom: false);
        if (!cone)
        {
            Band(1, 2, bottom: false);
            Band(2, 3, bottom: false);
            Band(3, 4, bottom: false);
            Band(5, 4, bottom: true);
            Band(0, 5, bottom: false);
        }
        else
        {
            Band(1, 2, bottom: false);
            Band(2, 3, bottom: false);
            Band(4, 3, bottom: true);
            Band(0, 4, bottom: false);
        }
        pos = verts.ToArray();
        idx = tris.ToArray();
    }

    static void Slot(out Vector3[] pos, out int[] idx)
    {
        pos =
        [
            new(0, 0, 0), new(80, 0, 0), new(80, 80, 0), new(0, 80, 0),
            new(30, 36, 0), new(50, 36, 0), new(50, 44, 0), new(30, 44, 0),
        ];
        idx =
        [
            0, 1, 5, 0, 5, 4,
            1, 2, 6, 1, 6, 5,
            2, 3, 7, 2, 7, 6,
            3, 0, 4, 3, 4, 7,
        ];
    }
}
