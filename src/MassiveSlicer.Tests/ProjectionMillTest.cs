using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;
using Xunit;

namespace MassiveSlicer.Tests;

public sealed class ProjectionMillTest
{
    [Fact]
    public void Catalog_names_the_clearing_slot_projection()
    {
        var info = MillOperationInfo.Find(MillOperationKind.Projection);
        Assert.Equal("Projection", info!.DisplayName);
        Assert.Equal(MillOperationKind.PlanarClearing, MillOperationKind.Projection);
    }

    [Fact]
    public void Cylinder_projects_out_onto_a_wall_and_blends_the_normal()
    {
        var wallN = Vector3.Normalize(new Vector3(-1f, 0f, 0.8f));
        Vector3[] pos =
        [
            new(120, -200, 10), new(120, 200, 10), new(120, 200, 90),
            new(120, -200, 10), new(120, 200, 90), new(120, -200, 90),
        ];
        Vector3[] nrm = [wallN, wallN, wallN, wallN, wallN, wallN];
        int[] idx = [0, 1, 2, 3, 4, 5];
        var guide = new ProjectionSetup
        {
            Shape = ProjectionShape.Cylinder,
            Center = Vector3.Zero,
            BaseDiameterMm = 60,
            TopDiameterMm = 60,
            HeightMm = 100,
            NormalInfluence = 0f,
        };
        var mill = new MillSettings { StepoverMm = 25f, FinishAllowanceMm = 0f, RapidZMm = 10f };

        var shape = ProjectionMill.Generate(pos, nrm, idx, guide, mill);
        var cuts = shape.Layers.SelectMany(l => l.Moves).Where(m => m.Kind == MoveKind.Mill).ToList();
        Assert.NotEmpty(cuts);
        Assert.All(cuts, m => Assert.True(MathF.Abs(m.Normal.Z) < 0.2f, $"shape angle normal {m.Normal}"));

        var surface = ProjectionMill.Generate(pos, nrm, idx, guide with { NormalInfluence = 1f }, mill);
        var tilted = surface.Layers.SelectMany(l => l.Moves).First(m => m.Kind == MoveKind.Mill);
        Assert.True(tilted.Normal.Z > 0.3f, $"surface normal {tilted.Normal}");
    }

    [Fact]
    public void E1_movement_keeps_hits_on_one_side_of_the_cylinder()
    {
        Vector3 n = Vector3.UnitX;
        Vector3[] pos =
        [
            new(80, -80, 0), new(80, 80, 0), new(80, 80, 100),
            new(-80, 80, 0), new(80, 80, 0), new(80, 80, 100),
        ];
        // Normals face the cylinder. The reverse side is a backface and must not cut.
        Vector3[] nx = [-n, -n, -n, -Vector3.UnitY, -Vector3.UnitY, -Vector3.UnitY];
        int[] idx = [0, 1, 2, 3, 4, 5];
        var guide = new ProjectionSetup
        {
            Shape = ProjectionShape.Cylinder,
            Center = Vector3.Zero,
            BaseDiameterMm = 40,
            TopDiameterMm = 40,
            HeightMm = 80,
            AxisX = Vector3.UnitX,
            UseE1 = true,
            RotaryCenter = Vector3.Zero,
            RotarySign = -1f,
        };
        var mill = new MillSettings { StepoverMm = 40f, FinishAllowanceMm = 0f, RapidZMm = 5f };
        var tp = ProjectionMill.Generate(pos, nx, idx, guide, mill);
        var cuts = tp.Layers.SelectMany(l => l.Moves).Where(m => m.Kind == MoveKind.Mill && !float.IsNaN(m.E1Mm)).ToList();
        Assert.NotEmpty(cuts);
        Assert.All(cuts, m => Assert.True(MathF.Abs(m.To.Y) < 25f, $"arm left the station Y={m.To.Y:0}"));
        Assert.All(tp.Layers.SelectMany(l => l.Moves), m =>
            Assert.InRange(m.E1Mm, -ProjectionMill.RotaryLimitDeg, ProjectionMill.RotaryLimitDeg));
        Assert.Contains(cuts, m =>
        {
            var onPart = ProjectionMill.OnTable(m.To, m.E1Mm, Vector3.Zero, -1f);
            return MathF.Abs(onPart.X - 80f) < 20f || MathF.Abs(onPart.Y - 80f) < 20f;
        });
    }

    [Fact]
    public void Full_turn_stays_inside_the_rotary_stops()
    {
        var pos = new List<Vector3>();
        var nrm = new List<Vector3>();
        var idx = new List<int>();
        const int panes = 16;
        for (int i = 0; i < panes; i++)
        {
            float a0 = i / (float)panes * MathF.Tau;
            float a1 = (i + 1) / (float)panes * MathF.Tau;
            var p0 = new Vector3(MathF.Cos(a0), MathF.Sin(a0), 0f) * 100f;
            var p1 = new Vector3(MathF.Cos(a1), MathF.Sin(a1), 0f) * 100f;
            var p2 = p1 + new Vector3(0f, 0f, 80f);
            var p3 = p0 + new Vector3(0f, 0f, 80f);
            var n = -Vector3.Normalize(new Vector3(MathF.Cos((a0 + a1) * 0.5f), MathF.Sin((a0 + a1) * 0.5f), 0f));
            int b = pos.Count;
            pos.AddRange([p0, p1, p2, p0, p2, p3]);
            for (int k = 0; k < 6; k++) nrm.Add(n);
            idx.AddRange([b, b + 1, b + 2, b + 3, b + 4, b + 5]);
        }
        var guide = new ProjectionSetup
        {
            Center = Vector3.Zero,
            BaseDiameterMm = 40,
            TopDiameterMm = 40,
            HeightMm = 60,
            AxisX = Vector3.UnitX,
            UseE1 = true,
            RotaryCenter = Vector3.Zero,
            RotarySign = -1f,
            SliceDirection = ProjectionSliceDirection.Vertical,
        };
        var tp = ProjectionMill.Generate(pos, nrm, idx, guide, new MillSettings { StepoverMm = 20f, FinishAllowanceMm = 0f, RapidZMm = 8f });
        var moves = tp.Layers.SelectMany(l => l.Moves).ToList();
        Assert.NotEmpty(moves);
        Assert.All(moves, m => Assert.InRange(m.E1Mm, -180f, 180f));
        float? cutE1 = null;
        foreach (var m in moves)
        {
            if (m.Kind != MoveKind.Mill)
            {
                cutE1 = null;
                continue;
            }
            if (cutE1 is float prev)
                Assert.True(MathF.Abs(m.E1Mm - prev) <= 90f, $"cut crossed the rotary stop {prev:0} → {m.E1Mm:0}");
            cutE1 = m.E1Mm;
        }
    }

    [Fact]
    public void Rotary_rewind_is_not_a_one_millisecond_snap()
    {
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 0f);
        var station = new System.Numerics.Vector3(100f, 0f, 50f);
        layer.Moves.Add(new ToolpathMove(station, station, MoveKind.Travel) { E1Mm = 170f });
        layer.Moves.Add(new ToolpathMove(station, station, MoveKind.Travel) { E1Mm = -170f });
        tp.Layers.Add(layer);
        var (times, _) = MassiveSlicer.Viewport.Validation.ToolpathFeasibilityEvaluator.BuildMoveProfile(
            tp, 60f, 150f, 120f);
        Assert.True(times[1] > 1000f, $"rewind took {times[1]:0} ms");
    }

    [Fact]
    public void Backface_does_not_receive_a_cut()
    {
        Vector3[] pos =
        [
            new(120, -200, 10), new(120, 200, 10), new(120, 200, 90),
            new(120, -200, 10), new(120, 200, 90), new(120, -200, 90),
        ];
        var away = Vector3.UnitX;
        Vector3[] nrm = [away, away, away, away, away, away];
        int[] idx = [0, 1, 2, 3, 4, 5];
        var guide = new ProjectionSetup
        {
            Center = Vector3.Zero,
            BaseDiameterMm = 60,
            TopDiameterMm = 60,
            HeightMm = 100,
        };
        var tp = ProjectionMill.Generate(pos, nrm, idx, guide, new MillSettings { StepoverMm = 25f, FinishAllowanceMm = 0f });
        var cuts = tp.Layers.SelectMany(l => l.Moves).Where(m => m.Kind == MoveKind.Mill);
        Assert.Empty(cuts);
    }

    [Fact]
    public void Vertical_slice_steps_in_height_before_it_steps_around()
    {
        var wallN = Vector3.Normalize(new Vector3(-1f, 0f, 0.2f));
        Vector3[] pos =
        [
            new(120, -200, 0), new(120, 200, 0), new(120, 200, 120),
            new(120, -200, 0), new(120, 200, 120), new(120, -200, 120),
        ];
        Vector3[] nrm = [wallN, wallN, wallN, wallN, wallN, wallN];
        int[] idx = [0, 1, 2, 3, 4, 5];
        var guide = new ProjectionSetup
        {
            Center = Vector3.Zero,
            BaseDiameterMm = 60,
            TopDiameterMm = 60,
            HeightMm = 100,
            SliceDirection = ProjectionSliceDirection.Vertical,
        };
        var tp = ProjectionMill.Generate(pos, nrm, idx, guide, new MillSettings { StepoverMm = 25f, FinishAllowanceMm = 0f });
        var cuts = tp.Layers.SelectMany(l => l.Moves).Where(m => m.Kind == MoveKind.Mill).Take(3).ToList();
        Assert.True(cuts.Count >= 2);
        float dz = MathF.Abs(cuts[0].To.Z - cuts[0].From.Z);
        float dy = MathF.Abs(cuts[0].To.Y - cuts[0].From.Y);
        Assert.True(dz > dy, $"vertical pass should climb, dz={dz:0} dy={dy:0}");
    }
}
