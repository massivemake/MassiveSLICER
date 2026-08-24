using System.Numerics;
using System.Text.Json;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;

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
        layer0.Moves.Add(new ToolpathMove(new Vector3(100, 0, 10), new Vector3(100, 50, 20), MoveKind.Travel)
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
}
