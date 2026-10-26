using System.Numerics;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing.Effects;

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
        Assert.Equal(0f, mv[0].E1Mm, 3);                     // first PTP has E1 0; LIN holds it
        Assert.Equal(0f, mv[1].E1Mm, 3);
    }

    [Fact]
    public void Parses_inline_E1_and_holds_last_value()
    {
        const string krl = """
            DEF RAIL()
            PTP {X 0.0, Y 0.0, Z 50.0, A 0, B 0, C 0, E1 -1100.520}
            LIN {X 100.0, Y 0.0, Z 50.0, A 0, B 0, C 0, E1 -1800.000, E2 0.000}
            LIN {X 200.0, Y 0.0, Z 50.0, A 0, B 0, C 0}
            END
            """;
        var tp = KrlToolpathParser.Parse(krl, Vector3.Zero, out int moves);
        Assert.Equal(2, moves);
        Assert.True(KrlToolpathParser.HasProgrammedE1(tp));
        Assert.Equal(-1800f, tp.Layers[0].Moves[0].E1Mm, 3);  // LIN target
        Assert.Equal(-1800f, tp.Layers[0].Moves[1].E1Mm, 3);  // omitted E1 holds
        var e1 = KrlToolpathParser.E1PerMove(tp);
        Assert.Equal(new[] { -1800f, -1800f }, e1);
    }

    [Fact]
    public void Krl_zero_plus_print_bed_origin_is_print_bed_origin()
    {
        // LFAM 3 bed.origin — imported {X 0,Y 0,Z 0} must sit here, not at ROBROOT.
        var bed = new Vector3(2135.45f, -52.54f, 916.31f);
        const string krl = """
            DEF FOO()
            PTP {X 0.0, Y 0.0, Z 0.0}
            LIN {X 100.0, Y 0.0, Z 0.0}
            END
            """;
        var tp = KrlToolpathParser.Parse(krl, bed, out int moves);
        Assert.Equal(1, moves);
        Assert.Equal(bed, tp.Layers[0].Moves[0].From);
        Assert.Equal(bed + new Vector3(100, 0, 0), tp.Layers[0].Moves[0].To);
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

    [Fact]
    public void Print_temps_T1_T2_T3_make_LIN_extrude_not_mill()
    {
        const string krl = """
            DEF PRINT()
            ; Mode print
            ; T1 220 C  T2 210 C  T3 200 C
            T1 = 220
            T2 = 210
            T3 = 200
            PTP {X 0.0, Y 0.0, Z 100.0, A 0, B 0, C 0}
            LIN {X 50.0, Y 0.0, Z 100.0, A 0, B 0, C 0}
            END
            """;
        Assert.True(KrlToolpathParser.HasPrintTemperatures(krl));
        Assert.Equal(KrlImportKind.Print, KrlToolpathParser.Classify(krl, selectedKrlTool: 12));
        var tp = KrlToolpathParser.Parse(krl, Vector3.Zero, out int moves);
        Assert.Equal(1, moves);
        Assert.Equal(MoveKind.Extrude, tp.Layers[0].Moves[0].Kind);
    }

    [Fact]
    public void Anout_T1_comment_is_print()
    {
        const string krl = """
            DEF PRINT()
            $ANOUT[1] = 0.2272 ; T1 = 220C
            PTP {X 0.0, Y 0.0, Z 10.0}
            LIN {X 10.0, Y 0.0, Z 10.0}
            END
            """;
        Assert.True(KrlToolpathParser.HasPrintTemperatures(krl));
        Assert.Equal(KrlImportKind.Print, KrlToolpathParser.Classify(krl));
    }

    [Fact]
    public void Selected_T1_without_temps_is_print()
    {
        const string krl = """
            DEF FOO()
            PTP {X 0.0, Y 0.0, Z 10.0}
            LIN {X 10.0, Y 0.0, Z 10.0}
            END
            """;
        Assert.False(KrlToolpathParser.HasPrintTemperatures(krl));
        Assert.Equal(KrlImportKind.Print, KrlToolpathParser.Classify(krl, selectedKrlTool: 1));
        var tp = KrlToolpathParser.Parse(krl, Vector3.Zero, out _, KrlImportKind.Print);
        Assert.Equal(MoveKind.Extrude, tp.Layers[0].Moves[0].Kind);
    }

    [Fact]
    public void Mill_mode_comment_stays_mill_even_with_T1_selected()
    {
        const string krl = """
            DEF MILL()
            ; Mode mill
            ; Spindle RPM 8000
            PTP {X 0.0, Y 0.0, Z 10.0}
            LIN {X 10.0, Y 0.0, Z 10.0}
            END
            """;
        Assert.Equal(KrlImportKind.Mill, KrlToolpathParser.Classify(krl, selectedKrlTool: 1));
        var tp = KrlToolpathParser.Parse(krl, Vector3.Zero, out _);
        Assert.Equal(MoveKind.Mill, tp.Layers[0].Moves[0].Kind);
    }

    [Fact]
    public void Print_layer_height_splits_a_rising_path_so_adaptive_speed_can_vary()
    {
        // The approach sits 47 mm above the first bead. Anchoring the pitch there
        // would keep every later bead in one layer, and the Speed tab would stay one colour.
        const string krl = """
            DEF P()
            ; Mode print
            ; Layer height 3.00 mm
            T1 = 220
            PTP {X 0.0, Y 0.0, Z 50.0, A 0, B 0, C 0}
            LIN {X 0.0, Y 0.0, Z 3.0}
            LIN {X 300.0, Y 0.0, Z 3.0}
            LIN {X 300.0, Y 0.0, Z 6.0}
            LIN {X 340.0, Y 0.0, Z 6.0}
            LIN {X 340.0, Y 0.0, Z 9.0}
            LIN {X 360.0, Y 0.0, Z 9.0}
            END
            """;
        var tp = KrlToolpathParser.Parse(krl, Vector3.Zero, out _, KrlImportKind.Print);
        Assert.Equal(3, tp.Layers.Count);
        Assert.Equal(3f, tp.Layers[0].Z, 2);
        Assert.Equal(6f, tp.Layers[1].Z, 2);
        Assert.Equal(9f, tp.Layers[2].Z, 2);

        LayerSpeedPostProcessor.ApplyInPlace(tp, new SliceSettings
        {
            LayerSpeedAdaptEnabled = true,
            LayerSpeedBasis        = LayerSpeedBasis.CutLength,
            PrintSpeedMps          = 0.1f,
            LayerSpeedMinMmS       = 10f,
            LayerSpeedMaxMmS       = 100f,
        });

        float slow = Scale(tp.Layers[2]);
        float mid  = Scale(tp.Layers[1]);
        float fast = Scale(tp.Layers[0]);
        Assert.True(fast > mid && mid > slow, $"scales {fast:0.###} {mid:0.###} {slow:0.###}");
        Assert.True(fast - slow > 0.5f, $"spread {fast - slow:0.###} is what the Speed tab draws");
    }

    [Fact]
    public void Print_layer_height_splits_a_continuous_rise_with_no_z_jump()
    {
        const string krl = """
            DEF RISE()
            ; Mode print
            ; Layer height 3.00 mm
            T1 = 220
            LIN {X 0.0, Y 0.0, Z 3.0}
            LIN {X 10.0, Y 0.0, Z 4.0}
            LIN {X 20.0, Y 0.0, Z 5.0}
            LIN {X 30.0, Y 0.0, Z 6.0}
            LIN {X 40.0, Y 0.0, Z 7.0}
            LIN {X 50.0, Y 0.0, Z 8.0}
            LIN {X 60.0, Y 0.0, Z 9.0}
            END
            """;
        var tp = KrlToolpathParser.Parse(krl, Vector3.Zero, out _, KrlImportKind.Print);
        Assert.Equal(3, tp.Layers.Count);
        Assert.Equal(3f, tp.Layers[0].Z, 2);
        Assert.Equal(6f, tp.Layers[1].Z, 2);
        Assert.Equal(9f, tp.Layers[2].Z, 2);
    }

    [Fact]
    public void Print_without_a_layer_height_header_stays_one_layer()
    {
        const string krl = """
            DEF FLAT()
            ; Mode print
            T1 = 220
            LIN {X 0.0, Y 0.0, Z 3.0}
            LIN {X 10.0, Y 0.0, Z 6.0}
            LIN {X 20.0, Y 0.0, Z 9.0}
            END
            """;
        var tp = KrlToolpathParser.Parse(krl, Vector3.Zero, out _, KrlImportKind.Print);
        Assert.Single(tp.Layers);
    }

    private static float Scale(ToolpathLayer layer)
        => layer.Moves.First(m => m.Kind == MoveKind.Extrude).PrintSpeedScale;

    [Fact]
    public void Selected_T12_without_temps_is_mill()
    {
        const string krl = """
            DEF FOO()
            PTP {X 0.0, Y 0.0, Z 10.0}
            LIN {X 10.0, Y 0.0, Z 10.0}
            END
            """;
        Assert.Equal(KrlImportKind.Mill, KrlToolpathParser.Classify(krl, selectedKrlTool: 12));
    }
}
