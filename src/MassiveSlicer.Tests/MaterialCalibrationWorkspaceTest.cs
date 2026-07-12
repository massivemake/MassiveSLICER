using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;
using Xunit;

namespace MassiveSlicer.Tests;

public class MaterialCalibrationWorkspaceTest
{
    [Fact]
    public void Create_WritesMassWithToolpathAndCanReload()
    {
        var cell = CellLoader.Load("assets/cells/LFAM1/lfam1.json");
        Assert.NotNull(cell);

        string dir = Path.Combine(Path.GetTempPath(), $"matcal-ws-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "test.mass");

        try
        {
            var written = MaterialCalibrationWorkspace.Create(new MaterialCalibrationWorkspace.CreateRequest
            {
                SavePath     = path,
                Material     = new MaterialPreset { Name = "ASA - Black", Temperature1 = 230, Temperature2 = 230, Temperature3 = 220 },
                MotorPercent = 50,
                RunTimeSec   = 60,
                Cell         = cell,
                CellPath     = "assets/cells/LFAM1/lfam1.json",
                HomeAngles   = cell.Robot.HomePosition,
                HomeE1Mm     = -500,
            });

            Assert.True(File.Exists(written));
            Assert.True(WorkspaceServiceFileHasModels(written) || WorkspaceLoader.Load(written)?.Models.Count > 0);

            var doc = WorkspaceLoader.Load(written);
            Assert.NotNull(doc);
            Assert.Single(doc!.Models);
            Assert.Single(doc.Models[0].Toolpaths);
            Assert.Equal(50.ToString(System.Globalization.CultureInfo.InvariantCulture), doc.Settings.ExtrusionSpeedOffset);
            Assert.Equal(60, doc.Settings.ExtrusionStartWaitSec);
            Assert.Equal("ASA - Black", doc.Settings.SelectedMaterialPresetName);
            Assert.Contains("Material Calibration", doc.Models[0].Toolpaths[0].Name);
            Assert.NotEmpty(doc.Models[0].Toolpaths[0].Data.Layers);
            Assert.NotEmpty(doc.Models[0].Toolpaths[0].Data.Layers[0].Moves);

            // Mesh sidecar exists
            Assert.False(string.IsNullOrEmpty(doc.Models[0].EmbeddedMeshPath));
            string meshAbs = WorkspaceLoader.ResolveMeshPath(written, doc.Models[0].EmbeddedMeshPath!);
            Assert.True(File.Exists(meshAbs));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    // Local mirror of WorkspaceService.FileHasModels without App project reference
    private static bool WorkspaceServiceFileHasModels(string path)
    {
        var doc = WorkspaceLoader.Load(path);
        return doc is { Models.Count: > 0 };
    }
}
