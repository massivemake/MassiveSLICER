using System.Numerics;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Tests;

public class KrlToolpathParserTest
{
    [Fact]
    public void Parses_LIN_PTP_frames_with_offset_and_kinds()
    {
        const string krl = """
            DEF FOO()
            PTP {X 0.0, Y 0.0, Z 100.0, A 0, B 0, C 0, E1 0.000}
            LIN {X 50.0, Y 0.0, Z 100.0, A 0, B 0, C 0}
            PTP {E6POS: X 50.0, Y 25.0, Z 200.0}
            PTP apos                  ; joint target — no frame, skipped
            END
            """;
        var off = new Vector3(1000, 0, -100);
        var tp = KrlToolpathParser.Parse(krl, off, out int moves);

        Assert.Equal(2, moves);                       // 3 frames → 2 segments; "PTP apos" skipped
        Assert.Single(tp.Layers);
        var mv = tp.Layers[0].Moves;
        Assert.Equal(MoveKind.Mill, mv[0].Kind);      // LIN target
        Assert.Equal(MoveKind.Travel, mv[1].Kind);    // PTP target
        Assert.Equal(new Vector3(1000, 0, 0), mv[0].From);    // (0,0,100)+off
        Assert.Equal(new Vector3(1050, 0, 0), mv[0].To);      // (50,0,100)+off
        Assert.Equal(new Vector3(1050, 25, 100), mv[1].To);   // (50,25,200)+off
    }

    [Fact]
    public void Joint_only_program_yields_no_moves()
    {
        const string krl = """
            DEF BED()
            apos = $AXIS_ACT
            apos.E1 = -180.0
            PTP apos
            apos.E1 = 0.0
            PTP apos
            END
            """;
        var tp = KrlToolpathParser.Parse(krl, Vector3.Zero, out int moves);
        Assert.Equal(0, moves);
        Assert.Empty(tp.Layers);
    }
}
