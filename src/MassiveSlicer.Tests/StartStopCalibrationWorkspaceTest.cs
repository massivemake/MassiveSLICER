using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;
using Xunit;

namespace MassiveSlicer.Tests;

public class StartStopCalibrationWorkspaceTest
{
    [Fact]
    public void Create_WritesEightCellGridWithBakedVaryingSettings()
    {
        var cell = CellLoader.Load("assets/cells/LFAM1/lfam1.json");
        Assert.NotNull(cell);

        string dir = Path.Combine(Path.GetTempPath(), $"sscal-ws-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "test.mass");

        try
        {
            var written = StartStopCalibrationWorkspace.Create(new StartStopCalibrationWorkspace.CreateRequest
            {
                SavePath     = path,
                Cell         = cell,
                CellPath     = "assets/cells/LFAM1/lfam1.json",
                HomeAngles   = cell!.Robot.HomePosition,
                HomeE1Mm     = -500,
                MaterialName = "ASA - White",
                BaseXMm      = 600,
                BaseYMm      = 600,
            });

            Assert.True(File.Exists(written));
            Assert.True(File.Exists(Path.Combine(dir, "TEST_MATRIX.txt")));

            var doc = WorkspaceLoader.Load(written);
            Assert.NotNull(doc);
            Assert.Single(doc!.Models);
            Assert.Contains("8-cell", doc.Models[0].Name);
            Assert.Single(doc.Models[0].Toolpaths);
            Assert.Contains("BAKED", doc.Models[0].Toolpaths[0].Name);

            var tp = doc.Models[0].Toolpaths[0];
            Assert.True(tp.Data.Layers.Count >= 8 * 2, "Expected multiple layers across 8 cells");

            int wipeCount = tp.Data.Layers.Sum(l => l.Moves.Count(m => m.IsWipe));
            int zHopCount = tp.Data.Layers.Sum(l => l.Moves.Count(m => m.IsZHop));
            // T1 has no wipe/hop on intra-cell travels, but later cells do — and inter-cell connectors hop.
            Assert.True(wipeCount > 0, "Matrix includes wipe cells");
            Assert.True(zHopCount > 0, "Matrix includes z-hop cells / connectors");

            // Per-cell resume waits baked on travels (0 / 0.25 / 0.5 / 1.0 s).
            var resumeVals = tp.Data.Layers
                .SelectMany(l => l.Moves)
                .Where(m => m.Kind == "Travel" && m.ResumeWaitSec is not null)
                .Select(m => m.ResumeWaitSec!.Value)
                .Distinct()
                .OrderBy(v => v)
                .ToList();
            Assert.Contains(0f, resumeVals);
            Assert.Contains(0.5f, resumeVals);
            Assert.True(doc.Settings.DigitalStartStopEnabled, "Calibration enables Digital Start/Stop (URM)");

            // T1 (no wipe) vs T4 (wipe 12): first cell layers should have fewer wipes than later.
            int layersPerCell = (int)Math.Round(20.0 / 2.5); // default wall height / layerH
            int firstCellWipes = tp.Data.Layers.Take(layersPerCell)
                .Sum(l => l.Moves.Count(m => m.IsWipe));
            int midCellWipes = tp.Data.Layers.Skip(layersPerCell * 3).Take(layersPerCell)
                .Sum(l => l.Moves.Count(m => m.IsWipe));
            Assert.True(midCellWipes > firstCellWipes,
                $"Expected more wipes mid-matrix than T1 (mid={midCellWipes}, t1={firstCellWipes})");

            var baseMarker = cell.Bed.BaseMarkerWorld(cell.Robot.WorldPosition);
            float bedZ = baseMarker.Z;
            float firstZ = tp.Data.Layers[0].Z;
            Assert.InRange(firstZ, bedZ + 1f, bedZ + 5f);

            // Grid spans beyond a single cell near BASE 600,600.
            var xs = tp.Data.Layers.SelectMany(l => l.Moves).SelectMany(m => new[] { m.From[0], m.To[0] }).ToList();
            Assert.True(xs.Max() - xs.Min() > 200, "Grid should span multiple columns");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void DefaultTestMatrix_HasEightCells()
    {
        Assert.Equal(8, StartStopCalibrationWorkspace.DefaultTestMatrix.Count);
        Assert.Equal(1, StartStopCalibrationWorkspace.DefaultTestMatrix[0].Id);
        Assert.Equal(8, StartStopCalibrationWorkspace.DefaultTestMatrix[^1].Id);
        Assert.Contains(StartStopCalibrationWorkspace.DefaultTestMatrix, t => t.WipeMode == WipeMode.None);
        Assert.Contains(StartStopCalibrationWorkspace.DefaultTestMatrix, t => t.WipeMode == WipeMode.Retrace);
        Assert.Contains(StartStopCalibrationWorkspace.DefaultTestMatrix, t => t.ZHopMm >= 6f);
        Assert.Contains(StartStopCalibrationWorkspace.DefaultTestMatrix, t => t.ResumeWaitMs == 0);
        // T5 + T6 are identical recommended packages (wipe12 / hop3 / resume 500).
        var t5 = StartStopCalibrationWorkspace.DefaultTestMatrix[4];
        var t6 = StartStopCalibrationWorkspace.DefaultTestMatrix[5];
        Assert.Equal(t5.WipeMode, t6.WipeMode);
        Assert.Equal(t5.WipeLengthMm, t6.WipeLengthMm);
        Assert.Equal(t5.WipeRampMm, t6.WipeRampMm);
        Assert.Equal(t5.ZHopMm, t6.ZHopMm);
        Assert.Equal(t5.ResumeWaitMs, t6.ResumeWaitMs);
        Assert.Equal(500, t5.ResumeWaitMs);
        Assert.Equal(12f, t5.WipeLengthMm);
        Assert.Equal(3f, t5.ZHopMm);
        Assert.True(StartStopCalibrationWorkspace.DefaultTestMatrix.Select(t => t.ResumeWaitMs).Distinct().Count() >= 3);
    }

    [Fact]
    public void Create_Lfam2_EightInchCircles_WritesBakedMatrix()
    {
        var cell = CellLoader.Load("assets/cells/LFAM2/lfam2.json");
        Assert.NotNull(cell);

        string dir = Path.Combine(Path.GetTempPath(), $"sscal-circ-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "lfam2_circles.mass");

        try
        {
            const float dia = StartStopCalibrationWorkspace.EightInchDiameterMm; // 203.2 mm
            var written = StartStopCalibrationWorkspace.Create(new StartStopCalibrationWorkspace.CreateRequest
            {
                SavePath           = path,
                Cell               = cell,
                CellPath           = "assets/cells/LFAM2/lfam2.json",
                HomeAngles         = cell!.Robot.HomePosition,
                HomeE1Mm           = 0,
                ToolDataIndex      = 2, // HV Extruder (LFAM2 default)
                MaterialName       = "ASA - White",
                ProjectDisplayName = StartStopCalibrationWorkspace.CirclesLfam2ProjectName,
                CircleDiameterMm   = dia,
                GapMm              = 50,
                BaseXMm            = 500,
                BaseYMm            = 500,
                // Dual 8" rings + gap ≈ 456 mm wide; pitch leaves clearance between cells.
                PitchXMm           = 520,
                PitchYMm           = 280,
                WallHeightMm       = 24,
            });

            Assert.True(File.Exists(written));
            Assert.True(File.Exists(Path.Combine(dir, "TEST_MATRIX_CIRCLES_8IN.txt")));
            string legend = File.ReadAllText(Path.Combine(dir, "TEST_MATRIX_CIRCLES_8IN.txt"));
            Assert.Contains("circle", legend, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("203", legend); // diameter mm

            var doc = WorkspaceLoader.Load(written);
            Assert.NotNull(doc);
            Assert.Contains("LFAM2", doc!.CellPath ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.Contains("circle", doc.Models[0].Name, StringComparison.OrdinalIgnoreCase);
            Assert.True(doc.Settings.DigitalStartStopEnabled);
            Assert.Single(doc.Models[0].Toolpaths);

            var tp = doc.Models[0].Toolpaths[0];
            // 8 cells × ~8 layers
            Assert.True(tp.Data.Layers.Count >= 8 * 2);

            // Path should span roughly 8" diameter (~200 mm) within a cell and wider across the grid.
            var xs = tp.Data.Layers.SelectMany(l => l.Moves).SelectMany(m => new[] { m.From[0], m.To[0] }).ToList();
            var ys = tp.Data.Layers.SelectMany(l => l.Moves).SelectMany(m => new[] { m.From[1], m.To[1] }).ToList();
            Assert.True(xs.Max() - xs.Min() > 400, "Grid of 8in circles should span multiple columns");
            Assert.True(ys.Max() - ys.Min() > 200, "Grid should span at least one row of 8in circles");

            // Per-cell resume tags present.
            var resumeVals = tp.Data.Layers
                .SelectMany(l => l.Moves)
                .Where(m => m.Kind == "Travel" && m.ResumeWaitSec is not null)
                .Select(m => m.ResumeWaitSec!.Value)
                .Distinct()
                .ToList();
            Assert.Contains(0f, resumeVals);
            Assert.Contains(0.5f, resumeVals);

            int wipeCount = tp.Data.Layers.Sum(l => l.Moves.Count(m => m.IsWipe));
            int zHopCount = tp.Data.Layers.Sum(l => l.Moves.Count(m => m.IsZHop));
            Assert.True(wipeCount > 0);
            Assert.True(zHopCount > 0);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
