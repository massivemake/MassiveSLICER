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

        // Round-trip JSON
        var json = MassiveDriveJobExporter.ExportJson(SamplePath(), settings);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("massivedrive.job/v1", doc.RootElement.GetProperty("format").GetString());
        Assert.Equal(3, doc.RootElement.GetProperty("segments").GetArrayLength());
    }

    [Fact]
    public void Export_skips_mill_only_moves()
    {
        var tp = new Toolpath();
        var layer = new ToolpathLayer(0, 0f) { PlaneNormal = Vector3.UnitZ };
        layer.Moves.Add(new ToolpathMove(Vector3.Zero, new Vector3(10, 0, 0), MoveKind.Mill));
        tp.Layers.Add(layer);

        var settings = new MassiveDriveExportSettings { Name = "mill" };
        Assert.Throws<InvalidOperationException>(() =>
            MassiveDriveJobExporter.ExportDict(tp, settings));
    }
}
