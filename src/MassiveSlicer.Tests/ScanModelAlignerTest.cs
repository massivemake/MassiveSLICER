using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;

namespace MassiveSlicer.Tests;

/// <summary>
/// The scan is the milling position. Alignment may move the model only, and only
/// when the print sits outside the model by about the stock thickness.
/// </summary>
public sealed class ScanModelAlignerTest
{
    private static ScanModelAligner.ScanModelAlignRequest Request(
        float stock = 4f, float tol = 1.5f, float shift = 50f, float turn = 20f)
        => new(stock, tol, 0.5f, shift, turn);

    [Fact]
    public void Fit_puts_the_model_inside_a_larger_scan_and_leaves_the_scan()
    {
        var model = Corner(stock: 0f);
        var scan = Corner(stock: 4f);
        var scanBefore = scan.Points.ToArray();
        var moved = Move(model, yawDeg: 8f, shift: new Vector3(12f, -6f, 3f));

        var result = ScanModelAligner.Align(moved.Points, moved.Normals, scan.Points, Request());

        Assert.True(result.Accepted, result.Message);
        Assert.Equal(scanBefore, scan.Points);
        for (int i = 0; i < model.Points.Count; i++)
        {
            var back = ScanModelAligner.TransformPoint(moved.Points[i], result.ModelWorldDelta);
            Assert.True((back - model.Points[i]).Length < 1.0f, result.Message);
        }
    }

    [Fact]
    public void Extra_stock_on_a_closed_shape_is_rejected_and_does_not_move_the_model()
    {
        var model = Box(stock: 0f);
        var scan = Box(stock: 12f);
        var result = ScanModelAligner.Align(model.Points, model.Normals, scan.Points, Request(stock: 4f, tol: 1.5f));

        Assert.False(result.Accepted);
        Assert.Contains("Not applied", result.Message);
        Assert.Equal(Matrix4.Identity, result.ModelWorldDelta);
    }

    [Fact]
    public void A_far_model_is_located_before_the_fine_match()
    {
        var model = Corner(stock: 0f);
        var scan = Corner(stock: 4f);
        var scanBefore = scan.Points.ToArray();
        var moved = Move(model, yawDeg: 25f, shift: new Vector3(400f, -220f, 180f));

        var result = ScanModelAligner.Align(
            moved.Points, moved.Normals, scan.Points, Request(shift: 80f, turn: 20f));

        Assert.True(result.Accepted, result.Message);
        Assert.Equal(scanBefore, scan.Points);
        Assert.True(result.TranslationMm > 100f, result.Message);
        for (int i = 0; i < model.Points.Count; i++)
        {
            var back = ScanModelAligner.TransformPoint(moved.Points[i], result.ModelWorldDelta);
            var err = (back - model.Points[i]).Length;
            Assert.True(err < 1.5f, $"{result.Message} err={err:0.00}");
        }
    }

    [Fact]
    public void A_model_moved_many_metres_is_still_located()
    {
        var model = Corner(stock: 0f);
        var scan = Corner(stock: 4f);
        var moved = Move(model, yawDeg: 10f, shift: new Vector3(6000f, -2500f, 1800f));

        var result = ScanModelAligner.Align(moved.Points, moved.Normals, scan.Points, Request());

        Assert.True(result.Accepted, result.Message);
        Assert.True(result.TranslationMm > 1000f, result.Message);
        for (int i = 0; i < model.Points.Count; i++)
        {
            var back = ScanModelAligner.TransformPoint(moved.Points[i], result.ModelWorldDelta);
            var err = (back - model.Points[i]).Length;
            Assert.True(err < 1.5f, $"{result.Message} err={err:0.00}");
        }
    }

    [Fact]
    public void A_model_moved_past_twenty_metres_is_still_located()
    {
        var model = Corner(stock: 0f);
        var scan = Corner(stock: 4f);
        var moved = Move(model, yawDeg: 0f, shift: new Vector3(30000f, -4000f, 2000f));

        var result = ScanModelAligner.Align(moved.Points, moved.Normals, scan.Points, Request());

        Assert.True(result.Accepted, result.Message);
        Assert.True(result.TranslationMm > 10000f, result.Message);
    }

    [Fact]
    public void A_face_moved_sideways_by_metres_is_still_located()
    {
        var model = Face(Vector3.Zero);
        var scan = Face(new Vector3(0f, 0f, 4f));
        var moved = Move(model, yawDeg: 0f, shift: new Vector3(2500f, -900f, 0f));

        var result = ScanModelAligner.Align(moved.Points, moved.Normals, scan.Points, Request());

        Assert.True(result.Accepted, result.Message);
        Assert.True(result.TranslationMm > 1000f, result.Message);
        for (int i = 0; i < model.Points.Count; i++)
        {
            var back = ScanModelAligner.TransformPoint(moved.Points[i], result.ModelWorldDelta);
            var err = back - model.Points[i];
            // The face has no features along itself, so the in-plane seat can be a
            // few centimetres off. It still has to leave the 2.5 m start and sit
            // on the scan in Z.
            Assert.True(MathF.Abs(err.Z) < 1.5f, $"{result.Message} z={err.Z:0.00}");
            Assert.True(MathF.Sqrt(err.X * err.X + err.Y * err.Y) < 40f, $"{result.Message} xy={err.X:0.0},{err.Y:0.0}");
        }
    }

    [Fact]
    public void A_far_closed_shape_with_the_wrong_stock_is_not_moved()
    {
        var model = Box(stock: 0f);
        var scan = Box(stock: 12f);
        var moved = Move(model, yawDeg: 0f, shift: new Vector3(500f, -80f, 240f));
        var result = ScanModelAligner.Align(
            moved.Points, moved.Normals, scan.Points, Request(stock: 4f, tol: 1.5f, shift: 40f, turn: 15f));

        Assert.False(result.Accepted);
        Assert.Contains("Not applied", result.Message);
        Assert.Equal(Matrix4.Identity, result.ModelWorldDelta);
    }

    [Fact]
    public void One_scanned_face_matches_without_the_rest_of_the_model()
    {
        var model = Box(stock: 0f);
        var scan = new List<Vector3>();
        for (int i = 0; i < model.Points.Count; i++)
        {
            if (model.Normals[i].Z < 0.99f) continue;
            scan.Add(model.Points[i] + Vector3.UnitZ * 4f);
        }

        var scanBefore = scan.ToArray();
        var moved = Move(model, yawDeg: 6f, shift: new Vector3(10f, -8f, 14f));
        var result = ScanModelAligner.Align(moved.Points, moved.Normals, scan, Request());

        Assert.True(result.Accepted, result.Message);
        Assert.Equal(scanBefore, scan);
        int checkedFace = 0;
        for (int i = 0; i < model.Points.Count; i++)
        {
            if (model.Normals[i].Z < 0.99f) continue;
            var back = ScanModelAligner.TransformPoint(moved.Points[i], result.ModelWorldDelta);
            float err = MathF.Abs(back.Z - model.Points[i].Z);
            Assert.True(err < 1.5f, $"{result.Message} z-err={err:0.00}");
            checkedFace++;
        }

        Assert.True(checkedFace > 12);
    }

    [Fact]
    public void A_large_part_turned_about_x_and_y_is_still_located()
    {
        // A 1.6 m part with three unequal faces. Yaw about Z cannot undo a pitch and a roll.
        var model = Scale(UnevenCorner(), 15f);
        var scan = new List<Vector3>(model.Points.Count);
        for (int i = 0; i < model.Points.Count; i++)
            scan.Add(model.Points[i] + model.Normals[i] * 4f);
        var moved = Pose(model, new Vector3(70f, 25f, 40f), new Vector3(2200f, -1500f, 800f));

        var result = ScanModelAligner.Align(moved.Points, moved.Normals, scan, Request());

        Assert.True(result.Accepted, result.Message);
        Assert.True(result.RotationDeg > 30f, result.Message);
        for (int i = 0; i < model.Points.Count; i++)
        {
            var back = ScanModelAligner.TransformPoint(moved.Points[i], result.ModelWorldDelta);
            var err = (back - model.Points[i]).Length;
            Assert.True(err < 1.5f, $"{result.Message} err={err:0.00}");
        }
    }

    [Fact]
    public void A_large_one_face_turned_on_its_side_is_still_located()
    {
        var model = Scale(UnevenCorner(), 15f);
        var scan = new List<Vector3>();
        for (int i = 0; i < model.Points.Count; i++)
        {
            if (model.Normals[i].Z < 0.99f) continue;
            scan.Add(model.Points[i] + Vector3.UnitZ * 4f);
        }

        var moved = Pose(model, new Vector3(80f, -35f, 20f), new Vector3(1800f, 900f, -700f));
        var result = ScanModelAligner.Align(moved.Points, moved.Normals, scan, Request());

        Assert.True(result.Accepted, result.Message);
        Assert.True(result.RotationDeg > 30f, result.Message);
        int checkedFace = 0;
        for (int i = 0; i < model.Points.Count; i++)
        {
            if (model.Normals[i].Z < 0.99f) continue;
            var back = ScanModelAligner.TransformPoint(moved.Points[i], result.ModelWorldDelta);
            var err = (back - model.Points[i]).Length;
            Assert.True(err < 1.5f, $"{result.Message} err={err:0.00} i={i}");
            checkedFace++;
        }

        Assert.True(checkedFace > 12);
    }

    [Fact]
    public void A_sideways_slide_on_one_face_is_rejected()
    {
        var model = Face(shift: new Vector3(120f, 0f, 2f));
        var scan = Face(shift: new Vector3(0f, 0f, 4f));
        var result = ScanModelAligner.Align(model.Points, model.Normals, scan.Points, Request());

        Assert.False(result.Accepted);
        Assert.Contains("Not applied", result.Message);
        Assert.Equal(Matrix4.Identity, result.ModelWorldDelta);
    }

    [Fact]
    public void A_long_part_is_sampled_from_every_mesh()
    {
        var meshes = new List<MeshData>();
        var worlds = new List<Matrix4>();
        for (int chunk = 0; chunk < 5; chunk++)
        {
            var pts = new Vector3[1000];
            var nrm = new Vector3[1000];
            for (int i = 0; i < pts.Length; i++)
            {
                pts[i] = new Vector3(chunk * 400f, i, 0f);
                nrm[i] = Vector3.UnitZ;
            }

            meshes.Add(new MeshData(pts, nrm, indices: null));
            worlds.Add(Matrix4.Identity);
        }

        var points = new List<Vector3>();
        ScanModelAligner.CollectBalancedSamples(meshes, worlds, 100, points, normals: null);

        Assert.Equal(100, points.Count);
        Assert.Contains(points, p => p.X < 100f);
        Assert.Contains(points, p => p.X > 1500f);
    }

    [Fact]
    public void Nudge_translates_in_world_x_and_yaws_about_the_pivot()
    {
        var pivot = new Vector3(10f, 20f, 30f);
        var point = pivot + new Vector3(0f, 8f, 0f);
        var spun = ScanModelAligner.TransformPoint(
            point, ScanModelAligner.WorldNudge(pivot, Vector3.Zero, new Vector3(0f, 0f, 90f)));
        // Right-handed +90° about Z takes +Y to -X.
        Assert.True((spun - (pivot + new Vector3(-8f, 0f, 0f))).Length < 0.05f);

        var shifted = ScanModelAligner.TransformPoint(
            point, ScanModelAligner.WorldNudge(pivot, new Vector3(5f, 0f, 0f), Vector3.Zero));
        Assert.True((shifted - (point + new Vector3(5f, 0f, 0f))).Length < 0.001f);
    }

    private readonly record struct Cloud(List<Vector3> Points, List<Vector3> Normals);

    private static Cloud Corner(float stock)
    {
        var points = new List<Vector3>();
        var normals = new List<Vector3>();
        AddGrid(points, normals, new Vector3(50f, 0f, 0f), Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, stock);
        AddGrid(points, normals, new Vector3(0f, 50f, 0f), Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ, stock);
        AddGrid(points, normals, new Vector3(0f, 0f, 50f), Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY, stock);
        return new Cloud(points, normals);
    }

    private static Cloud Box(float stock)
    {
        var points = new List<Vector3>();
        var normals = new List<Vector3>();
        AddGrid(points, normals, new Vector3(50f, 0f, 0f), Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, stock);
        AddGrid(points, normals, new Vector3(-50f, 0f, 0f), -Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, stock);
        AddGrid(points, normals, new Vector3(0f, 50f, 0f), Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ, stock);
        AddGrid(points, normals, new Vector3(0f, -50f, 0f), -Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ, stock);
        AddGrid(points, normals, new Vector3(0f, 0f, 50f), Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY, stock);
        AddGrid(points, normals, new Vector3(0f, 0f, -50f), -Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY, stock);
        return new Cloud(points, normals);
    }

    private static Cloud Face(Vector3 shift)
    {
        var points = new List<Vector3>();
        var normals = new List<Vector3>();
        AddGrid(points, normals, new Vector3(0f, 0f, 0f), Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY, stock: 0f);
        for (int i = 0; i < points.Count; i++)
            points[i] += shift;
        return new Cloud(points, normals);
    }

    private static void AddGrid(
        List<Vector3> points, List<Vector3> normals,
        Vector3 origin, Vector3 normal, Vector3 axisU, Vector3 axisV, float stock)
    {
        for (int u = -3; u <= 3; u++)
        {
            for (int v = -3; v <= 3; v++)
            {
                points.Add(origin + axisU * (u * 12f) + axisV * (v * 12f) + normal * stock);
                normals.Add(normal);
            }
        }
    }

    /// <summary>Three faces at different distances, plus a tab so the flat face is not a square.</summary>
    private static Cloud UnevenCorner()
    {
        var points = new List<Vector3>();
        var normals = new List<Vector3>();
        AddGrid(points, normals, new Vector3(90f, 0f, 0f), Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, 0f);
        AddGrid(points, normals, new Vector3(0f, 40f, 0f), Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ, 0f);
        AddGrid(points, normals, new Vector3(0f, 0f, 25f), Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY, 0f);
        points.Add(new Vector3(70f, 8f, 25f));
        points.Add(new Vector3(82f, 0f, 25f));
        points.Add(new Vector3(82f, 16f, 25f));
        normals.Add(Vector3.UnitZ);
        normals.Add(Vector3.UnitZ);
        normals.Add(Vector3.UnitZ);
        return new Cloud(points, normals);
    }

    private static Cloud Scale(Cloud source, float factor)
    {
        var points = new List<Vector3>(source.Points.Count);
        for (int i = 0; i < source.Points.Count; i++)
            points.Add(source.Points[i] * factor);
        return new Cloud(points, source.Normals);
    }

    private static Cloud Pose(Cloud source, Vector3 rotateDeg, Vector3 shift)
    {
        var pose = ScanModelAligner.WorldNudge(Vector3.Zero, shift, rotateDeg);
        var points = new List<Vector3>(source.Points.Count);
        var normals = new List<Vector3>(source.Normals.Count);
        for (int i = 0; i < source.Points.Count; i++)
        {
            points.Add(ScanModelAligner.TransformPoint(source.Points[i], pose));
            normals.Add(ScanModelAligner.TransformNormal(source.Normals[i], pose));
        }
        return new Cloud(points, normals);
    }

    private static Cloud Move(Cloud source, float yawDeg, Vector3 shift)
    {
        var yaw = Matrix4.CreateRotationZ(MathHelper.DegreesToRadians(yawDeg));
        yaw.M41 = shift.X;
        yaw.M42 = shift.Y;
        yaw.M43 = shift.Z;
        var points = new List<Vector3>(source.Points.Count);
        var normals = new List<Vector3>(source.Normals.Count);
        for (int i = 0; i < source.Points.Count; i++)
        {
            points.Add(ScanModelAligner.TransformPoint(source.Points[i], yaw));
            normals.Add(ScanModelAligner.TransformNormal(source.Normals[i], yaw));
        }
        return new Cloud(points, normals);
    }
}
