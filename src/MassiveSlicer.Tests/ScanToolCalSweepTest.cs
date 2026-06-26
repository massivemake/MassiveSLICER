using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Scanning;

namespace MassiveSlicer.Tests;

public class ScanToolCalSweepTest
{
    [Fact]
    public void PoseDeltas_Has_Nine_Entries_Matching_Krl()
    {
        Assert.Equal(9, ScanToolCalSweep.PoseCount);
        Assert.Equal(9, ScanToolCalSweep.PoseDeltas.Count);
        Assert.Equal(0, ScanToolCalSweep.PoseDeltas[0].A4);
        Assert.Equal(15, ScanToolCalSweep.PoseDeltas[5].A6);
        Assert.Equal(-7, ScanToolCalSweep.PoseDeltas[8].A4);
    }

    [Fact]
    public void PoseDeltasForCell_Uses_Defaults_When_Missing_Or_Wrong_Count()
    {
        Assert.Same(ScanToolCalSweep.PoseDeltas, ScanToolCalSweep.PoseDeltasForCell(null));
        Assert.Same(ScanToolCalSweep.PoseDeltas, ScanToolCalSweep.PoseDeltasForCell(new BedScanConfig()));
        Assert.Same(ScanToolCalSweep.PoseDeltas, ScanToolCalSweep.PoseDeltasForCell(new BedScanConfig
        {
            ScanCalWristDeltas = [new ScanCalWristDeltaConfig { A4 = 1, A5 = 2, A6 = 3 }],
        }));
    }

    [Fact]
    public void PoseDeltasForCell_Loads_Nine_Learned_Entries()
    {
        var bedScan = new BedScanConfig
        {
            ScanCalWristDeltas =
            [
                new() { A4 = 0, A5 = 0, A6 = 0 },
                new() { A4 = 0, A5 = 4, A6 = 0 },
                new() { A4 = 0, A5 = -4, A6 = 0 },
                new() { A4 = 4, A5 = 0, A6 = 0 },
                new() { A4 = -4, A5 = 0, A6 = 0 },
                new() { A4 = 0, A5 = 0, A6 = 7.5f },
                new() { A4 = 0, A5 = 0, A6 = -7.5f },
                new() { A4 = 3.5f, A5 = 3.5f, A6 = 0 },
                new() { A4 = -3.5f, A5 = -3.5f, A6 = 0 },
            ],
        };

        var deltas = ScanToolCalSweep.PoseDeltasForCell(bedScan);
        Assert.Equal(9, deltas.Count);
        Assert.Equal(4, deltas[1].A5);
        Assert.Equal(7.5, deltas[5].A6, 3);
    }

    [Fact]
    public void TrySaveScanCalWristDeltas_Persists_To_BedScan()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ms-scan-cal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "cell.json");
        try
        {
            File.WriteAllText(path, """
                {
                  "name": "t",
                  "robot": { "modelPath": "robot.glb", "joints": [] },
                  "bed": {
                    "origin": { "x": 0, "y": 0, "z": 0 },
                    "width": 1,
                    "depth": 1,
                    "baseData": { "x": 0, "y": 0, "z": 0 }
                  },
                  "bedScan": { "scanSteps": 8 }
                }
                """);

            var learned = ScanToolCalSweep.PoseDeltas
                .Select((d, i) => i == 5 ? new ScanToolCalSweep.WristDelta(d.A4, d.A5, d.A6 * 0.5) : d)
                .ToList();

            Assert.True(CellLoader.TrySaveScanCalWristDeltas(path, learned, out var err), err);

            var cell = CellLoader.Load(path);
            Assert.NotNull(cell.BedScan?.ScanCalWristDeltas);
            Assert.Equal(9, cell.BedScan!.ScanCalWristDeltas!.Length);
            Assert.Equal(7.5f, cell.BedScan.ScanCalWristDeltas[5].A6);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}