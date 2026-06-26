using System;
using System.Linq;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;
using Xunit;
using Xunit.Abstractions;

namespace MassiveSlicer.Tests;

/// <summary>Validates the relief-milling anti-gouge inverse offset and basic toolpath shape.</summary>
public class ReliefMillSlicerTest(ITestOutputHelper output)
{
    private static ReliefMap SinglePitMap()
    {
        const int n = 21;
        var samples = new float[n * n];
        for (int i = 0; i < samples.Length; i++) samples[i] = 1f;   // white = top surface
        samples[10 * n + 10] = 0f;                                   // one black cell = 10mm-deep pit
        return new ReliefMap
        {
            Samples = samples, Cols = n, Rows = n,
            OriginX = 0f, OriginY = 0f, WidthMm = 200f, LengthMm = 200f,
            HeightScaleMm = 10f, ReferencePlaneZ = 0f,
        };
    }

    private static float MinMillZ(Toolpath tp) =>
        tp.Layers.SelectMany(l => l.Moves).Where(m => m.Kind == MoveKind.Mill)
          .SelectMany(m => new[] { m.From.Z, m.To.Z }).DefaultIfEmpty(0f).Min();

    private static float MaxMillZ(Toolpath tp) =>
        tp.Layers.SelectMany(l => l.Moves).Where(m => m.Kind == MoveKind.Mill)
          .SelectMany(m => new[] { m.From.Z, m.To.Z }).DefaultIfEmpty(0f).Max();

    [Fact]
    public void BallTool_DoesNotGougeNarrowPit_ButSmallerToolReachesDeeper()
    {
        var map = SinglePitMap();

        var bigBall = new MillSettings
        {
            ToolDiameterMm = 40f, ToolEnd = ToolEndType.Ball,
            StepoverMm = 10f, StepdownMm = 5f, FinishAllowanceMm = 0f,
        };
        var tpBig = ReliefMillSlicer.Slice(map, bigBall);
        Assert.NotEmpty(tpBig.Layers);

        float minBig = MinMillZ(tpBig);
        float maxBig = MaxMillZ(tpBig);
        output.WriteLine($"40mm ball: minZ={minBig:F2} maxZ={maxBig:F2}");

        // A 40mm ball physically cannot reach the bottom of a 10mm-wide, 10mm-deep pit.
        // It is constrained by the white rim, so the deepest cut must stay well above -10.
        Assert.True(minBig > -5f, $"40mm ball gouged the narrow pit: minZ={minBig}");
        // Flat white surface should be carved at ~ReferencePlaneZ (0), not above it.
        Assert.True(maxBig <= 0.05f, $"cut above reference plane: maxZ={maxBig}");

        // A tiny tool with fine stepover should descend much closer to the true -10 pit bottom.
        var tinyBall = new MillSettings
        {
            ToolDiameterMm = 2f, ToolEnd = ToolEndType.Ball,
            StepoverMm = 2f, StepdownMm = 5f, FinishAllowanceMm = 0f,
        };
        float minTiny = MinMillZ(ReliefMillSlicer.Slice(map, tinyBall));
        output.WriteLine($"2mm ball: minZ={minTiny:F2}");

        Assert.True(minTiny < minBig - 2f, $"smaller tool should reach deeper: tiny={minTiny} big={minBig}");
        Assert.True(minTiny <= -8f, $"2mm ball should nearly reach the -10 pit bottom: minZ={minTiny}");
    }

    [Fact]
    public void FlatMap_ProducesSkimAtReferencePlane()
    {
        const int n = 8;
        var samples = Enumerable.Repeat(1f, n * n).ToArray();   // uniform white
        var map = new ReliefMap
        {
            Samples = samples, Cols = n, Rows = n,
            OriginX = 0f, OriginY = 0f, WidthMm = 80f, LengthMm = 80f,
            HeightScaleMm = 5f, ReferencePlaneZ = 0f,
        };
        var tp = ReliefMillSlicer.Slice(map, new MillSettings
        {
            ToolDiameterMm = 6f, ToolEnd = ToolEndType.Ball, StepoverMm = 10f,
            StepdownMm = 5f, FinishAllowanceMm = 0f,
        });

        Assert.NotEmpty(tp.Layers);
        // Uniform white => finish skim flat at the reference plane.
        Assert.All(tp.Layers.SelectMany(l => l.Moves).Where(m => m.Kind == MoveKind.Mill),
            m => Assert.InRange(m.To.Z, -0.05f, 0.05f));
    }

    [Fact]
    public void MillKrlExport_IsSpindleProgram_NotExtrusion()
    {
        // Radial dome relief: white centre (high, ~referenceZ), black edges (low, -scale).
        const int n = 64;
        var samples = new float[n * n];
        float c = (n - 1) / 2f, maxd = c;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float d = MathF.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / maxd;
                samples[y * n + x] = 1f - MathF.Min(d, 1f);
            }

        var map = new ReliefMap
        {
            Samples = samples, Cols = n, Rows = n,
            OriginX = 0f, OriginY = 0f, WidthMm = 80f, LengthMm = 80f,
            HeightScaleMm = 5f, ReferencePlaneZ = 0f,
        };
        var tp = ReliefMillSlicer.Slice(map, new MillSettings
        {
            ToolDiameterMm = 4f, ToolEnd = ToolEndType.Ball,
            StepoverMm = 4f, StepdownMm = 2f, FinishAllowanceMm = 0.3f,
        });

        Assert.NotEmpty(tp.Layers);
        Assert.Contains(tp.Layers.SelectMany(l => l.Moves), m => m.Kind == MoveKind.Mill);

        var krl = KrlExporter.Export(tp, new KrlExportSettings
        {
            ProgramName = "ReliefTest", IsMilling = true, ToolDataIndex = 3,
            SpindleRpm = 12000f, CuttingFeedMmMin = 2500f, PlungeFeedMmMin = 800f,
            ApproachZMm = 30f,
        });
        output.WriteLine($"KRL {krl.Length} chars, layers={tp.Layers.Count}, " +
                         $"millZ in [{MinMillZ(tp):F2},{MaxMillZ(tp):F2}]");

        Assert.Contains("LIN", krl);                  // motion commands present
        Assert.Contains("$VEL.CP", krl);              // cartesian feed set (mill program)
        Assert.Contains("TOOL_NO 3", krl);            // spindle tool index, not extruder
        Assert.DoesNotContain("$ANOUT[1]", krl);      // no extruder temperature channel
        Assert.DoesNotContain("$ANOUT[4]", krl);      // no extruder RPM channel

        // Carved Z stays within the relief envelope: never above referenceZ, never below -scale.
        Assert.True(MaxMillZ(tp) <= 0.05f, $"cut above reference plane: {MaxMillZ(tp)}");
        Assert.True(MinMillZ(tp) >= -5.05f, $"cut below relief floor: {MinMillZ(tp)}");
    }
}
