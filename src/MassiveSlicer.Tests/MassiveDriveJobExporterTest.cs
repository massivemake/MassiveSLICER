using System.Numerics;
using System.Text.Json;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing.Effects;

namespace MassiveSlicer.Tests;

public sealed class MassiveDriveJobExporterTest
{
    static Toolpath SamplePath()
    {
        var tp = new Toolpath();
        var layer0 = new ToolpathLayer(0, 10f)
        {
            Height = 3f,
            PlaneNormal = Vector3.UnitZ,
        };
        layer0.Moves.Add(new ToolpathMove(new Vector3(0, 0, 10), new Vector3(100, 0, 10), MoveKind.Extrude)
        {
            Normal = Vector3.UnitZ,
            PrintSpeedScale = 1f,
        });
        layer0.Moves.Add(new ToolpathMove(new Vector3(100, 0, 10), new Vector3(100, 50, 13), MoveKind.Travel)
        {
            IsLayerChange = true,
        });
        tp.Layers.Add(layer0);

        var layer1 = new ToolpathLayer(1, 13f)
        {
            Height = 3f,
            PlaneNormal = Vector3.UnitZ,
        };
        layer1.Moves.Add(new ToolpathMove(new Vector3(100, 50, 13), new Vector3(0, 50, 13), MoveKind.Extrude)
        {
            Normal = Vector3.UnitZ,
            PrintSpeedScale = 0.8f,
        });
        tp.Layers.Add(layer1);
        return tp;
    }

    [Fact]
    public void Export_emits_massivedrive_job_v1_with_print_and_travel()
    {
        var settings = new MassiveDriveExportSettings
        {
            Name = "unit-part",
            CellId = "lfam3",
            JobId = "testid123456",
            PrintSpeedMmS = 50f,
            TravelSpeedMmS = 120f,
            Tool = 1,
            Base = 1,
        };

        var dict = MassiveDriveJobExporter.ExportDict(SamplePath(), settings);
        Assert.Equal("massivedrive.job/v1", dict["format"]);
        Assert.Equal("lfam3", dict["cell_id"]);
        Assert.Equal("testid123456", dict["job_id"]);
        Assert.Equal("unit-part", dict["name"]);

        var segs = Assert.IsType<List<Dictionary<string, object?>>>(dict["segments"]);
        Assert.Equal(3, segs.Count);
        Assert.Equal("print", segs[0]["kind"]);
        Assert.Equal("travel", segs[1]["kind"]);
        Assert.Equal(true, segs[1]["reverse"]);
        Assert.Equal("print", segs[2]["kind"]);
        Assert.Equal(1, segs[2]["layer"]);

        // ABC from UnitZ normal: B ≈ 90° (nozzle down), same as KRL export
        var from0 = Assert.IsType<Dictionary<string, double>>(segs[0]["from"]);
        Assert.Equal(0.0, from0["x"]);
        Assert.Equal(0.0, from0["y"]);
        Assert.Equal(10.0, from0["z"]);
        Assert.InRange(from0["b"], 89.0, 91.0);

        var meta = Assert.IsType<Dictionary<string, object?>>(dict["meta"]);
        Assert.Equal(true, meta["absolute"]);
        Assert.Equal("#BASE", meta["ipo_frame"]);
        Assert.Equal(1, meta["tool"]);
        Assert.Equal(1, meta["base"]);

        // Round-trip JSON
        var json = MassiveDriveJobExporter.ExportJson(SamplePath(), settings);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("massivedrive.job/v1", doc.RootElement.GetProperty("format").GetString());
        Assert.Equal(3, doc.RootElement.GetProperty("segments").GetArrayLength());
    }

    [Fact]
    public void Export_stamps_slicer_rpm_pct_first_layer_vs_rest()
    {
        var tp = new Toolpath();
        var layer0 = new ToolpathLayer(0, 3f) { Height = 3f, PlaneNormal = Vector3.UnitZ };
        layer0.Moves.Add(new ToolpathMove(new Vector3(0, 0, 3), new Vector3(40, 0, 3), MoveKind.Extrude)
        {
            Normal = Vector3.UnitZ,
            PrintSpeedScale = 1f,
        });
        layer0.Moves.Add(new ToolpathMove(new Vector3(40, 0, 3), new Vector3(50, 10, 6), MoveKind.Travel));
        tp.Layers.Add(layer0);
        var layer1 = new ToolpathLayer(1, 6f) { Height = 3f, PlaneNormal = Vector3.UnitZ };
        layer1.Moves.Add(new ToolpathMove(new Vector3(50, 10, 6), new Vector3(90, 10, 6), MoveKind.Extrude)
        {
            Normal = Vector3.UnitZ,
            PrintSpeedScale = 1f,
        });
        tp.Layers.Add(layer1);

        var settings = new MassiveDriveExportSettings
        {
            Name = "rpm",
            CellId = "lfam3",
            PrintSpeedMmS = 80f,
            TravelSpeedMmS = 600f,
            ExtrusionRpmPercent = 62f,
            FirstLayerRpmPercent = 72f,
        };
        var dict = MassiveDriveJobExporter.ExportDict(tp, settings);
        var segs = Assert.IsType<List<Dictionary<string, object?>>>(dict["segments"]);
        Assert.Equal(72, segs[0]["rpm_pct"]);
        Assert.False(segs[1].ContainsKey("rpm_pct"));
        Assert.Equal(62, segs[2]["rpm_pct"]);
        var defaults = Assert.IsType<Dictionary<string, object?>>(dict["defaults"]);
        Assert.Equal(62, defaults["print_rpm_pct"]);
        Assert.Equal(72, defaults["first_layer_rpm_pct"]);
    }

    [Fact]
    public void Export_stamps_first_layer_speed_independent_of_rpm()
    {
        // Same contract as KRL: layer 0 $VEL.CP / RPM are independent of layer 1.
        var tp = new Toolpath();
        var layer0 = new ToolpathLayer(0, 3f) { Height = 3f, PlaneNormal = Vector3.UnitZ };
        layer0.Moves.Add(new ToolpathMove(new Vector3(0, 0, 3), new Vector3(40, 0, 3), MoveKind.Extrude)
        {
            Normal = Vector3.UnitZ,
            PrintSpeedScale = 1f,
        });
        layer0.Moves.Add(new ToolpathMove(new Vector3(40, 0, 3), new Vector3(50, 10, 6), MoveKind.Travel));
        tp.Layers.Add(layer0);
        var layer1 = new ToolpathLayer(1, 6f) { Height = 3f, PlaneNormal = Vector3.UnitZ };
        layer1.Moves.Add(new ToolpathMove(new Vector3(50, 10, 6), new Vector3(90, 10, 6), MoveKind.Extrude)
        {
            Normal = Vector3.UnitZ,
            PrintSpeedScale = 1f,
        });
        tp.Layers.Add(layer1);

        var settings = new MassiveDriveExportSettings
        {
            Name = "first-layer-speed",
            CellId = "lfam3",
            PrintSpeedMmS = 80f,
            TravelSpeedMmS = 600f,
            FirstLayerSpeedMmS = 20f,
            ExtrusionRpmPercent = 62.4048f,
            FirstLayerRpmPercent = 61.8512f,
        };
        var dict = MassiveDriveJobExporter.ExportDict(tp, settings);
        var segs = Assert.IsType<List<Dictionary<string, object?>>>(dict["segments"]);
        Assert.Equal(20.0, Convert.ToDouble(segs[0]["speed_mm_s"]));
        Assert.Equal(62, segs[0]["rpm_pct"]);
        Assert.Equal("travel", segs[1]["kind"]);
        Assert.Equal(600.0, Convert.ToDouble(segs[1]["speed_mm_s"]));
        Assert.Equal(80.0, Convert.ToDouble(segs[2]["speed_mm_s"]));
        Assert.Equal(63, segs[2]["rpm_pct"]);
        var defaults = Assert.IsType<Dictionary<string, object?>>(dict["defaults"]);
        Assert.Equal(80f, Convert.ToSingle(defaults["print_speed_mm_s"]));
        Assert.Equal(20.0, Convert.ToDouble(defaults["first_layer_speed_mm_s"]));
        Assert.Equal(63, defaults["print_rpm_pct"]);
        Assert.Equal(62, defaults["first_layer_rpm_pct"]);
    }

    [Fact]
    public void Export_stamps_per_move_rpm_when_speed_or_flow_scales()
    {
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 3f) { Height = 3f, PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 3), new Vector3(40, 0, 3), MoveKind.Extrude)
        {
            Normal = Vector3.UnitZ,
            PrintSpeedScale = 1f,
        });
        layer.Moves.Add(new ToolpathMove(new Vector3(40, 0, 3), new Vector3(80, 0, 3), MoveKind.Extrude)
        {
            Normal = Vector3.UnitZ,
            PrintSpeedScale = 0.5f,
        });
        layer.Moves.Add(new ToolpathMove(new Vector3(80, 0, 3), new Vector3(120, 0, 3), MoveKind.Extrude)
        {
            Normal = Vector3.UnitZ,
            PrintSpeedScale = 1f,
            HeightScale = 1.5f,
        });
        tp.Layers.Add(layer);

        var settings = new MassiveDriveExportSettings
        {
            Name = "var-rpm",
            PrintSpeedMmS = 80f,
            ExtrusionRpmPercent = 60f,
        };
        var segs = Assert.IsType<List<Dictionary<string, object?>>>(
            MassiveDriveJobExporter.ExportDict(tp, settings)["segments"]);
        Assert.Equal(60, segs[0]["rpm_pct"]);
        Assert.Equal(30, segs[1]["rpm_pct"]);
        Assert.Equal(90, segs[2]["rpm_pct"]);
    }

    [Fact]
    public void Export_stamps_rpm_from_geometry_when_percent_omitted()
    {
        var settings = new MassiveDriveExportSettings
        {
            Name = "geom-rpm",
            PrintSpeedMmS = 80f,
            TravelSpeedMmS = 600f,
            BeadWidthMm = 6f,
            LayerHeightMm = 3f,
            FlowRate = 0.463f,
        };
        var dict = MassiveDriveJobExporter.ExportDict(SamplePath(), settings);
        var segs = Assert.IsType<List<Dictionary<string, object?>>>(dict["segments"]);
        Assert.True(segs[0].ContainsKey("rpm_pct"));
        Assert.True((int)segs[0]["rpm_pct"]! > 0);
        var defaults = Assert.IsType<Dictionary<string, object?>>(dict["defaults"]);
        Assert.True(defaults.ContainsKey("print_rpm_pct"));
        Assert.Equal(defaults["print_rpm_pct"], defaults["first_layer_rpm_pct"]);
    }

    [Fact]
    public void Export_applies_toolhead_offset_to_abc()
    {
        var settings = new MassiveDriveExportSettings
        {
            Name = "tilt",
            ToolheadOffsetB = -15f,
        };
        var segs = Assert.IsType<List<Dictionary<string, object?>>>(
            MassiveDriveJobExporter.ExportDict(SamplePath(), settings)["segments"]);
        var from0 = Assert.IsType<Dictionary<string, double>>(segs[0]["from"]);
        // With B toolhead offset, ABC should not be pure (0, 90, 0)
        Assert.False(Math.Abs(from0["a"]) < 0.01 && Math.Abs(from0["b"] - 90) < 0.01 && Math.Abs(from0["c"]) < 0.01);
    }

    [Fact]
    public void Export_emits_mill_package_with_spindle_rpm()
    {
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 0f) { PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(new ToolpathMove(Vector3.Zero, new Vector3(10, 0, 0), MoveKind.Mill)
        {
            Normal = Vector3.UnitZ,
        });
        tp.Layers.Add(layer);

        var settings = new MassiveDriveExportSettings
        {
            Name = "mill",
            Tool = 12,
            MillOrientation = true,
            SpindleRpm = 1800f,
            PrintSpeedMmS = 10.44f,
            TravelSpeedMmS = 80f,
            TravelReverse = false,
        };
        var dict = MassiveDriveJobExporter.ExportDict(tp, settings);
        var segs = Assert.IsType<List<Dictionary<string, object?>>>(dict["segments"]);
        Assert.Equal("mill", segs[0]["kind"]);
        var meta = Assert.IsType<Dictionary<string, object?>>(dict["meta"]);
        Assert.Equal(true, meta["spindle"]);
        Assert.Equal(1800f, meta["spindle_rpm"]);
        Assert.Equal(true, meta["absolute"]);
        Assert.Equal("Milling Start", meta["approach_waypoint"]);
        var frames = Assert.IsType<Dictionary<string, int>>(dict["frames"]);
        Assert.Equal(12, frames["tool"]);
    }

    [Fact]
    public void Export_maps_lfam3_world_xyz_to_src_base()
    {
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 919.31f) { PlaneNormal = Vector3.UnitZ, Height = 3f };
        layer.Moves.Add(new ToolpathMove(
            new Vector3(1955.45f, -162.54f, 919.31f),
            new Vector3(2035.45f, -162.54f, 919.31f),
            MoveKind.Extrude)
        {
            Normal = Vector3.UnitZ,
            PrintSpeedScale = 1f,
        });
        tp.Layers.Add(layer);

        var settings = new MassiveDriveExportSettings
        {
            Name = "Six squares",
            Tool = 1,
            Base = 1,
            PrintSpeedMmS = 40f,
            TravelSpeedMmS = 40f,
            RobrootWorldPos = new Vector3(0, 0, 1000),
            BaseDataOffset = new Vector3(2135.45f, -52.54f, -83.69f),
            SliceBedWorldZ = 916.31f,
            BedOrigin = new Vector3(2135.45f, -52.54f, 916.31f),
        };

        var dict = MassiveDriveJobExporter.ExportDict(tp, settings);
        var segs = Assert.IsType<List<Dictionary<string, object?>>>(dict["segments"]);
        var from0 = Assert.IsType<Dictionary<string, double>>(segs[0]["from"]);
        // Same as SRC: print-bed BASE, not $POS_ACT world 919.
        Assert.InRange(from0["x"], -180.1, -179.9);
        Assert.InRange(from0["y"], -110.1, -109.9);
        Assert.InRange(from0["z"], 2.9, 3.1);
        var frames = Assert.IsType<Dictionary<string, int>>(dict["frames"]);
        Assert.Equal(1, frames["tool"]);
        Assert.Equal(1, frames["base"]);
        var meta = Assert.IsType<Dictionary<string, object?>>(dict["meta"]);
        Assert.Equal(true, meta["absolute"]);
        Assert.Equal("base", meta["frame"]);
        var bed = Assert.IsType<Dictionary<string, double>>(meta["bed_origin"]);
        Assert.InRange(bed["z"], 916.2, 916.4);
    }

    [Fact]
    public void Export_stitches_z_hop_onto_last_print_to_not_from()
    {
        // Closed island then hop tagged at the last print's From (slicer viewport
        // draws the two as separate lines; Drive polyline would reverse the edge).
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 3f) { Height = 3f, PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(new ToolpathMove(new Vector3(100, 0, 3), new Vector3(0, 0, 3), MoveKind.Extrude)
        {
            Normal = Vector3.UnitZ,
            PrintSpeedScale = 1f,
        });
        layer.Moves.Add(new ToolpathMove(new Vector3(100, 0, 3), new Vector3(100, 0, 23), MoveKind.Travel)
        {
            IsZHop = true,
            Normal = Vector3.UnitZ,
        });
        layer.Moves.Add(new ToolpathMove(new Vector3(100, 0, 23), new Vector3(50, 40, 23), MoveKind.Travel)
        {
            IsZHop = true,
            Normal = Vector3.UnitZ,
        });
        layer.Moves.Add(new ToolpathMove(new Vector3(50, 40, 23), new Vector3(50, 40, 3), MoveKind.Travel)
        {
            IsZHop = true,
            Normal = Vector3.UnitZ,
        });
        layer.Moves.Add(new ToolpathMove(new Vector3(50, 40, 3), new Vector3(60, 40, 3), MoveKind.Extrude)
        {
            Normal = Vector3.UnitZ,
            PrintSpeedScale = 1f,
        });
        tp.Layers.Add(layer);

        var segs = Assert.IsType<List<Dictionary<string, object?>>>(
            MassiveDriveJobExporter.ExportDict(tp, new MassiveDriveExportSettings { Name = "stitch" })["segments"]);
        Assert.Equal(5, segs.Count);

        var hop0 = Assert.IsType<Dictionary<string, double>>(segs[1]["from"]);
        var hop1 = Assert.IsType<Dictionary<string, double>>(segs[1]["to"]);
        Assert.Equal("travel", segs[1]["kind"]);
        Assert.InRange(hop0["x"], -0.05, 0.05);
        Assert.InRange(hop0["y"], -0.05, 0.05);
        Assert.InRange(hop0["z"], 2.9, 3.1);
        Assert.InRange(hop1["x"], -0.05, 0.05);
        Assert.InRange(hop1["y"], -0.05, 0.05);
        Assert.InRange(hop1["z"], 22.9, 23.1);

        var xyFrom = Assert.IsType<Dictionary<string, double>>(segs[2]["from"]);
        var xyTo = Assert.IsType<Dictionary<string, double>>(segs[2]["to"]);
        Assert.InRange(xyFrom["x"], -0.05, 0.05);
        Assert.InRange(xyFrom["z"], 22.9, 23.1);
        Assert.InRange(xyTo["x"], 49.9, 50.1);
        Assert.InRange(xyTo["y"], 39.9, 40.1);

        AssertNoGaps(segs);
    }

    [Fact]
    public void Export_leaves_already_continuous_hops_alone()
    {
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 3f) { Height = 3f, PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 3), new Vector3(10, 0, 3), MoveKind.Extrude)
        {
            Normal = Vector3.UnitZ,
        });
        layer.Moves.Add(new ToolpathMove(new Vector3(10, 0, 3), new Vector3(10, 0, 23), MoveKind.Travel)
        {
            IsZHop = true,
            Normal = Vector3.UnitZ,
        });
        tp.Layers.Add(layer);

        var segs = Assert.IsType<List<Dictionary<string, object?>>>(
            MassiveDriveJobExporter.ExportDict(tp, new MassiveDriveExportSettings { Name = "cont" })["segments"]);
        var hopFrom = Assert.IsType<Dictionary<string, double>>(segs[1]["from"]);
        Assert.InRange(hopFrom["x"], 9.9, 10.1);
        Assert.InRange(hopFrom["z"], 2.9, 3.1);
        AssertNoGaps(segs);
    }

    [Fact]
    public void Export_snaps_wipe_onto_last_print_to_without_reverse_stitch()
    {
        // Same island-close as shop make: last print closed to To, wipe tagged
        // at From (seam). Must wipe from To — no reverse travel along the edge.
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 3f) { Height = 3f, PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(new ToolpathMove(new Vector3(100, 0, 3), new Vector3(0, 0, 3), MoveKind.Extrude)
        {
            Normal = Vector3.UnitZ,
            PrintSpeedScale = 1f,
        });
        layer.Moves.Add(new ToolpathMove(new Vector3(100, 0, 3), new Vector3(135, 0, 8), MoveKind.Extrude)
        {
            Normal = Vector3.UnitZ,
            IsWipe = true,
            WipeRpmScale = 1f,
        });
        layer.Moves.Add(new ToolpathMove(new Vector3(135, 0, 8), new Vector3(50, 40, 3), MoveKind.Travel)
        {
            Normal = Vector3.UnitZ,
        });
        tp.Layers.Add(layer);

        var segs = Assert.IsType<List<Dictionary<string, object?>>>(
            MassiveDriveJobExporter.ExportDict(tp, new MassiveDriveExportSettings { Name = "wipe-stitch" })["segments"]);
        Assert.Equal(3, segs.Count);
        Assert.Equal("print", segs[0]["kind"]);
        var wipeMeta = Assert.IsType<Dictionary<string, object?>>(segs[1]["meta"]);
        Assert.Equal(true, wipeMeta["wipe"]);
        var wipeFrom = Assert.IsType<Dictionary<string, double>>(segs[1]["from"]);
        var wipeTo = Assert.IsType<Dictionary<string, double>>(segs[1]["to"]);
        Assert.InRange(wipeFrom["x"], -0.05, 0.05);
        Assert.InRange(wipeFrom["y"], -0.05, 0.05);
        Assert.InRange(wipeFrom["z"], 2.9, 3.1);
        Assert.InRange(wipeTo["x"], 34.9, 35.1);
        Assert.InRange(wipeTo["z"], 7.9, 8.1);
        Assert.Equal("travel", segs[2]["kind"]);
        AssertNoGaps(segs);
    }

    [Fact]
    public void Export_wipe_uses_wipe_speed_not_print_or_first_layer()
    {
        // Shop make 824ff2a069f1: wipe was kind=print @ 80/20. WIPE card is 600.
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 3f) { Height = 3f, PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 3), new Vector3(10, 0, 3), MoveKind.Extrude)
        {
            Normal = Vector3.UnitZ,
            PrintSpeedScale = 1f,
        });
        layer.Moves.Add(new ToolpathMove(new Vector3(10, 0, 3), new Vector3(45, 0, 3), MoveKind.Extrude)
        {
            Normal = Vector3.UnitZ,
            IsWipe = true,
            WipeRpmScale = 1f,
        });
        layer.Moves.Add(new ToolpathMove(new Vector3(45, 0, 3), new Vector3(80, 20, 3), MoveKind.Travel)
        {
            Normal = Vector3.UnitZ,
        });
        tp.Layers.Add(layer);

        var dict = MassiveDriveJobExporter.ExportDict(tp, new MassiveDriveExportSettings
        {
            Name = "wipe-spd",
            PrintSpeedMmS = 80f,
            TravelSpeedMmS = 600f,
            WipeSpeedMmS = 600f,
            FirstLayerSpeedMmS = 20f,
        });
        var segs = Assert.IsType<List<Dictionary<string, object?>>>(dict["segments"]);
        Assert.Equal(3, segs.Count);
        Assert.Equal(20.0, Convert.ToDouble(segs[0]["speed_mm_s"]));
        var wipeMeta = Assert.IsType<Dictionary<string, object?>>(segs[1]["meta"]);
        Assert.Equal(true, wipeMeta["wipe"]);
        Assert.Equal(600.0, Convert.ToDouble(segs[1]["speed_mm_s"]));
        Assert.Equal(600.0, Convert.ToDouble(segs[2]["speed_mm_s"]));
        var defaults = Assert.IsType<Dictionary<string, object?>>(dict["defaults"]);
        Assert.Equal(600.0, Convert.ToDouble(defaults["wipe_speed_mm_s"]));
        AssertNoGaps(segs);
    }

    [Fact]
    public void Export_stamps_pre_travel_start_and_post_travel_end_meta()
    {
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 10f) { Height = 3f, PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(new ToolpathMove(new Vector3(0, 0, 10), new Vector3(200, 0, 10), MoveKind.Extrude)
            { Normal = Vector3.UnitZ });
        layer.Moves.Add(new ToolpathMove(new Vector3(200, 0, 10), new Vector3(210, 0, 10), MoveKind.Extrude)
            { Normal = Vector3.UnitZ, IsWipe = true });
        layer.Moves.Add(new ToolpathMove(new Vector3(210, 0, 10), new Vector3(250, 0, 10), MoveKind.Travel));
        layer.Moves.Add(new ToolpathMove(new Vector3(250, 0, 10), new Vector3(450, 0, 10), MoveKind.Extrude)
            { Normal = Vector3.UnitZ });
        tp.Layers.Add(layer);

        var segs = Assert.IsType<List<Dictionary<string, object?>>>(
            MassiveDriveJobExporter.ExportDict(tp, new MassiveDriveExportSettings { Name = "tags" })["segments"]);
        AssertNoGaps(segs);

        var pre = segs.Single(s =>
            s.TryGetValue("meta", out var raw) && raw is Dictionary<string, object?> m
            && m.TryGetValue("pre_travel_start", out var v) && v is true);
        var preFrom = Assert.IsType<Dictionary<string, double>>(pre["from"]);
        var preTo = Assert.IsType<Dictionary<string, double>>(pre["to"]);
        Assert.InRange(preFrom["x"], 99.9, 100.1);
        Assert.InRange(preTo["x"], 199.9, 200.1);
        var preMeta = Assert.IsType<Dictionary<string, object?>>(pre["meta"]);
        Assert.Equal(TravelMarkerPostProcessor.PreTravelStartComment, preMeta["comment"]);

        var post = segs.Single(s =>
            s.TryGetValue("meta", out var raw) && raw is Dictionary<string, object?> m
            && m.TryGetValue("post_travel_start", out var v) && v is true);
        var postTo = Assert.IsType<Dictionary<string, double>>(post["to"]);
        Assert.InRange(postTo["x"], 349.9, 350.1);
        var postMeta = Assert.IsType<Dictionary<string, object?>>(post["meta"]);
        Assert.Equal(true, postMeta["post_travel_end"]);
        Assert.Equal(TravelMarkerPostProcessor.PostTravelStartComment, postMeta["comment"]);
    }

    static void AssertNoGaps(List<Dictionary<string, object?>> segs)
    {
        Dictionary<string, double>? prevTo = null;
        foreach (var seg in segs)
        {
            var from = Assert.IsType<Dictionary<string, double>>(seg["from"]);
            var to = Assert.IsType<Dictionary<string, double>>(seg["to"]);
            if (prevTo is not null)
            {
                double dx = from["x"] - prevTo["x"];
                double dy = from["y"] - prevTo["y"];
                double dz = from["z"] - prevTo["z"];
                Assert.True(dx * dx + dy * dy + dz * dz < 0.25,
                    $"gap before i={seg["i"]} kind={seg["kind"]} {prevTo["x"]},{prevTo["y"]},{prevTo["z"]} → {from["x"]},{from["y"]},{from["z"]}");
            }
            prevTo = to;
        }
    }
}
