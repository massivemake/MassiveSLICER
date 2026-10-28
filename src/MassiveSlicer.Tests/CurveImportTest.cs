using System.Numerics;
using System.Text.Json;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;

namespace MassiveSlicer.Tests;

public sealed class CurveImportTest
{
    static List<Vector3> Square() =>
    [
        new(0, 0, 0), new(10, 0, 0), new(10, 10, 0), new(0, 10, 0),
    ];

    [Fact]
    public void Closed_square_grows_outside_and_shrinks_inside()
    {
        var outside = CurveOffset.Shift(Square(), closed: true, flipped: false,
            CurveOffsetSide.Outside, 2f, includeToolRadius: false, toolDiameterMm: 6f, stockToLeaveMm: 0f);
        var inside = CurveOffset.Shift(Square(), closed: true, flipped: false,
            CurveOffsetSide.Inside, 2f, includeToolRadius: false, toolDiameterMm: 6f, stockToLeaveMm: 0f);
        Assert.True(outside.Min(p => p.X) < -1.5f, $"outside minX={outside.Min(p => p.X)}");
        Assert.True(outside.Max(p => p.X) > 11.5f, $"outside maxX={outside.Max(p => p.X)}");
        Assert.True(inside.Min(p => p.X) > 1.5f, $"inside minX={inside.Min(p => p.X)}");
        Assert.True(inside.Max(p => p.X) < 8.5f, $"inside maxX={inside.Max(p => p.X)}");
    }

    [Fact]
    public void Open_line_shifts_to_the_chosen_side_and_flip_swaps_it()
    {
        Vector3[] line = [new(0, 0, 0), new(10, 0, 0)];
        var outside = CurveOffset.Shift(line, closed: false, flipped: false,
            CurveOffsetSide.Outside, 2f, false, 6f, 0f);
        var flipped = CurveOffset.Shift(line, closed: false, flipped: true,
            CurveOffsetSide.Outside, 2f, false, 6f, 0f);
        Assert.All(outside, p => Assert.InRange(p.Y, 1.5f, 2.5f));
        Assert.All(flipped, p => Assert.InRange(p.Y, -2.5f, -1.5f));
    }

    [Fact]
    public void Zero_offset_leaves_the_points_where_they_were()
    {
        var same = CurveOffset.Shift(Square(), true, false, CurveOffsetSide.Outside, 0f, false, 6f, 0f);
        Assert.Equal(4, same.Count);
        Assert.Equal(0f, same[0].X);
        Assert.Equal(10f, same[2].Y);
        var onLine = CurveOffset.Shift(Square(), true, false, CurveOffsetSide.OnLine, 8f, true, 6f, 0f);
        Assert.Equal(0f, onLine[0].X);
        Assert.Equal(0f, onLine[0].Y);
    }

    [Fact]
    public void Tool_radius_and_stock_add_on_the_outside()
    {
        var withRadius = CurveOffset.Shift(Square(), true, false, CurveOffsetSide.Outside, 0f, true, 6f, 0f);
        Assert.True(withRadius.Min(p => p.X) < -2.5f);
        var withStock = CurveOffset.Shift(Square(), true, false, CurveOffsetSide.OnLine, 0f, false, 6f, 1f);
        Assert.True(withStock.Min(p => p.X) < -0.5f);
    }

    [Fact]
    public void Obj_lines_become_one_chain()
    {
        string path = WriteTemp("curve.obj", """
            v 0 0 0
            v 10 0 0
            v 10 10 5
            l 1 2
            l 2 3
            """);
        var load = CurveFileReader.Read(path);
        Assert.Equal("", load.Error);
        Assert.Single(load.Curves);
        Assert.Equal(3, load.Curves[0].Points.Count);
        Assert.False(load.Curves[0].Closed);
        Assert.False(load.Flat);
        Assert.Equal(5f, load.Curves[0].Points[2].Z);
    }

    [Fact]
    public void Dxf_inches_line_arc_and_bulge_convert_to_millimetres()
    {
        string path = WriteTemp("curve.dxf", """
            0
            SECTION
            2
            HEADER
            9
            $INSUNITS
            70
            4
            0
            ENDSEC
            0
            SECTION
            2
            ENTITIES
            0
            LINE
            10
            0
            20
            0
            30
            0
            11
            10
            21
            0
            31
            0
            0
            CIRCLE
            10
            0
            20
            0
            30
            0
            40
            10
            0
            LWPOLYLINE
            90
            2
            70
            0
            10
            0
            20
            0
            42
            -1
            10
            10
            20
            0
            0
            ENDSEC
            0
            EOF
            """);
        var load = CurveFileReader.Read(path);
        Assert.Equal("", load.Error);
        Assert.Equal(3, load.Curves.Count);
        Assert.Equal(10f, load.Curves[0].Points[1].X, 3);
        var circle = load.Curves[1];
        Assert.True(circle.Closed);
        Assert.True(circle.Points.Count >= 12);
        Assert.All(circle.Points, p => Assert.InRange(MathF.Sqrt(p.X * p.X + p.Y * p.Y), 9.5f, 10.5f));
        Assert.True(load.Curves[2].Points.Max(p => p.Y) > 4f, "bulge should bow off the chord");
    }

    [Fact]
    public void Dxf_inches_scale_and_binary_is_refused()
    {
        string inches = WriteTemp("inches.dxf", """
            0
            SECTION
            2
            HEADER
            9
            $INSUNITS
            70
            1
            0
            ENDSEC
            0
            SECTION
            2
            ENTITIES
            0
            LINE
            10
            0
            20
            0
            11
            1
            21
            0
            0
            ENDSEC
            0
            EOF
            """);
        var load = CurveFileReader.Read(inches);
        Assert.InRange(load.Curves[0].Points[1].X, 25.3f, 25.5f);

        string binary = WriteTemp("bin.dxf", "AutoCAD Binary DXF\r\n");
        var refused = CurveFileReader.Read(binary);
        Assert.Contains("ASCII", refused.Error);
        Assert.Empty(refused.Curves);
    }

    [Fact]
    public void Svg_path_flips_y_and_closes()
    {
        string path = WriteTemp("curve.svg", """
            <svg xmlns="http://www.w3.org/2000/svg" width="100mm" height="100mm" viewBox="0 0 100 100">
              <path d="M 0 0 L 10 0 L 10 10 Z"/>
            </svg>
            """);
        var load = CurveFileReader.Read(path);
        Assert.Equal("", load.Error);
        Assert.Single(load.Curves);
        Assert.True(load.Curves[0].Closed);
        Assert.True(load.Flat);
        Assert.Contains(load.Curves[0].Points, p => p.Y < -9f);
        CurveFileReader.LiftFlat(load.Curves, 40f);
        Assert.All(load.Curves[0].Points, p => Assert.InRange(p.Z, 39.9f, 40.1f));
    }

    [Fact]
    public void Contouring_follows_the_curve_instead_of_a_waterline()
    {
        var req = BoxReq(MillOperationKind.Contouring);
        req.Settings.ContouringMaxDepthEnabled = true;
        req.Settings.MaxDepthMm = 4f;
        req.Settings.StepdownMm = 2f;
        req = WithCurves(req,
        [
            new MillCurve
            {
                Name = "far",
                Included = true,
                Points = [new(500, 0, 100), new(530, 0, 100)],
            },
        ]);
        var tp = AdaMillPlanner.Generate(req);
        var moves = tp.Layers.SelectMany(l => l.Moves).ToList();
        Assert.Contains(moves, m => m.Kind == MoveKind.Mill && MathF.Abs(m.To.X - m.From.X) > 5f);
        Assert.All(moves, m =>
        {
            Assert.True(m.From.X > 400f && m.To.X > 400f, $"waterline leaked in at {m.From}");
        });
    }

    [Fact]
    public void Drilling_follows_a_curve_and_an_empty_selection_does_not()
    {
        var req = WithCurves(BoxReq(MillOperationKind.Drilling),
        [
            new MillCurve
            {
                Included = true,
                Points = [new(2, 10, 20), new(18, 10, 20)],
            },
        ]);
        var tp = AdaMillPlanner.Generate(req);
        var mills = tp.Layers.SelectMany(l => l.Moves).Where(m => m.Kind == MoveKind.Mill).ToList();
        Assert.Contains(mills, m => MathF.Abs(m.To.X - m.From.X) > 5f && MathF.Abs(m.To.Z - m.From.Z) < 0.2f);
        float minZ = mills.Min(m => MathF.Min(m.From.Z, m.To.Z));
        Assert.True(minZ < -4f, $"drill tip Z={minZ}");

        var empty = AdaMillPlanner.Generate(new AdaMillRequest
        {
            Settings = req.Settings,
            Positions = req.Positions,
            Normals = req.Normals,
            Indices = req.Indices,
            DrillPoints = [],
        });
        Assert.Empty(empty.Layers.SelectMany(l => l.Moves));
    }

    [Fact]
    public void Curve_list_round_trips_through_workspace_json()
    {
        var side = new MillSidebarSettings
        {
            CurveSideOffsetMm = 2.5,
            CurveSide = nameof(CurveOffsetSide.Inside),
            CurveIncludeToolRadius = true,
            Curves =
            [
                new MillCurve
                {
                    Name = "pocket",
                    Closed = true,
                    Included = true,
                    Points = [new(1, 2, 3), new(4, 5, 6), new(1, 2, 3)],
                },
            ],
        };
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var back = JsonSerializer.Deserialize<MillSidebarSettings>(JsonSerializer.Serialize(side, opts), opts);
        Assert.NotNull(back);
        Assert.Equal(2.5, back.CurveSideOffsetMm);
        Assert.Equal(nameof(CurveOffsetSide.Inside), back.CurveSide);
        Assert.True(back.CurveIncludeToolRadius);
        Assert.Equal("pocket", back.Curves[0].Name);
        Assert.Equal(6f, back.Curves[0].Points[1].Z);
    }

    static AdaMillRequest WithCurves(AdaMillRequest req, IReadOnlyList<MillCurve> curves) => new()
    {
        Settings = req.Settings,
        Positions = req.Positions,
        Normals = req.Normals,
        Indices = req.Indices,
        DrillPoints = req.Settings.Operation == MillOperationKind.Drilling ? [] : null,
        Curves = curves,
    };

    static AdaMillRequest BoxReq(MillOperationKind op)
    {
        const float s = 20f;
        Vector3[] pos =
        [
            new(0, 0, 0), new(s, 0, 0), new(s, s, 0), new(0, s, 0),
            new(0, 0, s), new(s, 0, s), new(s, s, s), new(0, s, s),
        ];
        var nrm = new Vector3[8];
        for (int i = 0; i < 8; i++)
            nrm[i] = Vector3.Normalize(pos[i] - new Vector3(s / 2, s / 2, s / 2));
        int[] idx =
        [
            0, 1, 2, 0, 2, 3,
            4, 6, 5, 4, 7, 6,
            0, 4, 5, 0, 5, 1,
            3, 2, 6, 3, 6, 7,
            0, 3, 7, 0, 7, 4,
            1, 5, 6, 1, 6, 2,
        ];
        return new AdaMillRequest
        {
            Settings = new AdaMachiningSettings
            {
                Operation = op,
                ToolDiameterMm = 6,
                StepdownMm = 2,
                RetractHeightMm = 10,
                FeedHeightMm = 2,
                DrillingBreakthroughMm = 5,
                CutoutCutDepthMm = 6,
                CutoutLayerHeightMm = 2,
            },
            Positions = pos,
            Normals = nrm,
            Indices = idx,
        };
    }

    static string WriteTemp(string name, string text)
    {
        string dir = Path.Combine(Path.GetTempPath(), "ms-curve-tests");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, text);
        return path;
    }
}
