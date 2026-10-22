using System.Text.Json;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Tests;

public sealed class MultiPlanarPersistTest
{
    [Fact]
    public void Height_X_and_Y_round_trip_in_a_mass_file()
    {
        var doc = new WorkspaceDocument
        {
            Settings = new AppPreferences
            {
                SliceMethod = "MultiPlanar",
                MultiPlanarPlanes =
                [
                    new GuidePlaneDto { HeightPct = 0, AngleX = 12.5, AngleY = 0 },
                    new GuidePlaneDto { HeightPct = 40, AngleX = 0, AngleY = -8 },
                    new GuidePlaneDto { HeightPct = 100, AngleX = 20, AngleY = 15 },
                ],
            },
            UiSession = new WorkspaceUiSession
            {
                SliceMethod = "MultiPlanar",
                MultiPlanarPlanes =
                [
                    new GuidePlaneDto { HeightPct = 0, AngleX = 12.5, AngleY = 0 },
                    new GuidePlaneDto { HeightPct = 40, AngleX = 0, AngleY = -8 },
                    new GuidePlaneDto { HeightPct = 100, AngleX = 20, AngleY = 15 },
                ],
            },
        };

        var path = Path.Combine(Path.GetTempPath(), $"mp-planes-{Guid.NewGuid():N}.mass");
        try
        {
            WorkspaceLoader.Save(doc, path);
            string json = File.ReadAllText(path);
            Assert.Contains("\"AngleX\":12.5", json);
            Assert.Contains("\"AngleX\":0", json);
            Assert.Contains("\"AngleY\":-8", json);
            Assert.Contains("\"HeightPct\":40", json);

            var loaded = WorkspaceLoader.Load(path);
            Assert.NotNull(loaded);
            var row = loaded!.Settings.MultiPlanarPlanes[2];
            Assert.Equal(100, row.HeightPct);
            Assert.Equal(20, row.AngleX);
            Assert.Equal(15, row.AngleY);
            Assert.Equal(0, loaded.Settings.MultiPlanarPlanes[1].AngleX);
            Assert.Equal(-8, loaded.Settings.MultiPlanarPlanes[1].AngleY);
            Assert.Equal("MultiPlanar", loaded.UiSession?.SliceMethod);
            Assert.Equal(12.5, loaded.UiSession!.MultiPlanarPlanes![0].AngleX);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Old_pair_arrays_still_open()
    {
        string json = """
            {"Settings":{"MultiPlanarPlanes":[[0,15],[100,30,12]]}}
            """;
        var loaded = JsonSerializer.Deserialize<WorkspaceDocument>(json);
        Assert.NotNull(loaded);
        Assert.Equal(15, loaded!.Settings.MultiPlanarPlanes[0].AngleY);
        Assert.Equal(0, loaded.Settings.MultiPlanarPlanes[0].AngleX);
        Assert.Equal(30, loaded.Settings.MultiPlanarPlanes[1].AngleY);
        Assert.Equal(12, loaded.Settings.MultiPlanarPlanes[1].AngleX);
    }
}
