using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Tests;

/// <summary>
/// The Caracol extruder drops to idle if the robot goes quiet, then reports not-ready and the
/// program stops at <c>WAIT FOR $IN[6]</c>. Verified 2026-07-29 on three production files:
/// Caracol's own Eidos output restates an unchanging <c>RPM = 96.4115</c> 826 times in 5.4 h
/// (worst gap 34.4 s); one of our stop/start-heavy panels incidentally stayed under 55.6 s and
/// survived; a smooth continuous loop emitted 6 screw commands in 3.65 h and died at 8:48.
/// These tests walk generated KRL the same way that analysis walked the .src files.
/// </summary>
public class ExtruderKeepAliveTest
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// One long continuous loop per layer and no travels — the Scene 08 shape. Each layer is a
    /// square ring of side <paramref name="sideMm"/>, stitched straight to the next layer.
    /// </summary>
    private static Toolpath ContinuousLoopTower(int layers, float sideMm)
    {
        var tp = new Toolpath();
        for (int li = 0; li < layers; li++)
        {
            float z = 3f + li * 3f;
            var layer = new ToolpathLayer(li, z) { PlaneNormal = Vector3.UnitZ, Height = 3f };
            Vector3 P(float x, float y) => new(x, y, z);
            // Closed ring, subdivided so a single move never dominates the timing.
            const int Seg = 10;
            var corners = new[] { P(0, 0), P(sideMm, 0), P(sideMm, sideMm), P(0, sideMm), P(0, 0) };
            for (int c = 0; c < corners.Length - 1; c++)
                for (int k = 0; k < Seg; k++)
                {
                    var a = Vector3.Lerp(corners[c], corners[c + 1], k / (float)Seg);
                    var b = Vector3.Lerp(corners[c], corners[c + 1], (k + 1) / (float)Seg);
                    layer.Moves.Add(new ToolpathMove(a, b, MoveKind.Extrude));
                }
            tp.Layers.Add(layer);
        }
        return tp;
    }

    private static KrlExportSettings Settings(bool keepAlive = true, float maxSilence = 30f) => new()
    {
        ProgramName              = "keepalive",
        PrintSpeedMps            = 0.06f,       // 60 mm/s, our standard
        TravelSpeedMps           = 0.6f,
        ExtrusionRpmPercent      = 60f,
        ExtruderKeepAliveEnabled = keepAlive,
        MaxExtruderSilenceSec    = maxSilence,
    };

    // A line the extruder actually hears: screw speed on either path, in either form.
    // The TRIGGER form matters — that is how the analog path emits without breaking
    // continuous path, and an earlier detector that missed it reported false silence.
    private static readonly Regex ScrewCmd = new(
        @"(^\s*RPM\s*=|^\s*\$ANOUT\[4\]\s*=|TRIGGER\b.*\$ANOUT\[4\]\s*=)",
        RegexOptions.Compiled);
    private static readonly Regex VelLine  = new(@"^\s*\$VEL\.CP\s*=\s*([\d.]+)", RegexOptions.Compiled);
    private static readonly Regex LinXyz   = new(
        @"^\s*LIN\s*\{X\s*(-?[\d.]+),\s*Y\s*(-?[\d.]+),\s*Z\s*(-?[\d.]+)", RegexOptions.Compiled);

    /// <summary>Longest stretch of seconds between screw commands in generated KRL.</summary>
    private static float LongestSilenceSec(string krl)
    {
        float vel = 0.1f, sinceCmd = 0f, worst = 0f;
        Vector3? prev = null;
        bool started = false;

        foreach (var line in krl.Split('\n'))
        {
            var v = VelLine.Match(line);
            if (v.Success)
            {
                float parsed = float.Parse(v.Groups[1].Value, Inv);
                if (parsed > 0f) vel = parsed;
                continue;
            }
            if (ScrewCmd.IsMatch(line))
            {
                if (started) worst = MathF.Max(worst, sinceCmd);
                sinceCmd = 0f;
                started  = true;
                continue;
            }
            var m = LinXyz.Match(line);
            if (!m.Success) continue;
            var cur = new Vector3(
                float.Parse(m.Groups[1].Value, Inv),
                float.Parse(m.Groups[2].Value, Inv),
                float.Parse(m.Groups[3].Value, Inv));
            if (prev is { } p && started)
                sinceCmd += (Vector3.Distance(p, cur) / 1000f) / vel;
            prev = cur;
        }
        return MathF.Max(worst, sinceCmd);
    }

    private static int ScrewCommandCount(string krl)
        => krl.Split('\n').Count(l => ScrewCmd.IsMatch(l));

    [Fact]
    public void Continuous_loop_never_exceeds_the_configured_silence_cap()
    {
        // 2 m per side at 60 mm/s ≈ 133 s per layer — far past the cap, so mid-layer
        // heartbeats are required, not just per-layer ones.
        var krl = KrlExporter.Export(ContinuousLoopTower(layers: 6, sideMm: 2000f), Settings());

        float worst = LongestSilenceSec(krl);
        Assert.True(worst <= 30f + 0.5f,
            $"longest extruder silence was {worst:0.0}s, cap is 30s");
    }

    [Fact]
    public void Without_keepalive_the_same_part_goes_silent_for_minutes()
    {
        // Guards the diagnosis itself: this is the Scene 08 failure reproduced in a test.
        var tp = ContinuousLoopTower(layers: 6, sideMm: 2000f);
        float withOut = LongestSilenceSec(KrlExporter.Export(tp, Settings(keepAlive: false)));
        float with    = LongestSilenceSec(KrlExporter.Export(tp, Settings()));

        Assert.True(withOut > 120f, $"expected long silence without keep-alive, got {withOut:0.0}s");
        Assert.True(with < withOut / 2f, "keep-alive should dramatically shorten the worst gap");
    }

    [Fact]
    public void Every_layer_gets_at_least_one_screw_command()
    {
        // Eidos parity: short layers still get a restatement even though nothing changed and
        // the silence cap was never reached. 200mm sides at 60mm/s ≈ 13s per layer (< 30s cap).
        const int Layers = 8;
        var krl = KrlExporter.Export(ContinuousLoopTower(Layers, sideMm: 200f), Settings());

        Assert.True(ScrewCommandCount(krl) >= Layers,
            $"expected ≥{Layers} screw commands (one per layer), got {ScrewCommandCount(krl)}");
    }

    [Fact]
    public void Keepalive_restates_the_current_rate_and_does_not_change_motion()
    {
        // The heartbeat must be a pure restatement: no extra motion, no rate change.
        var tp   = ContinuousLoopTower(layers: 4, sideMm: 2000f);
        var on   = KrlExporter.Export(tp, Settings());
        var off  = KrlExporter.Export(tp, Settings(keepAlive: false));

        static string[] Moves(string krl) =>
            krl.Split('\n').Where(l => LinXyz.IsMatch(l)).Select(l => l.Trim()).ToArray();

        Assert.Equal(Moves(off), Moves(on));          // identical motion
        Assert.Contains("keep-alive", on, StringComparison.Ordinal);

        // Every keep-alive line must carry the same rate as the surrounding extrusion.
        var rates = krlRates(on);
        Assert.True(rates.Distinct().Count() == 1,
            $"keep-alive changed the screw rate: {string.Join(", ", rates.Distinct())}");

        static List<string> krlRates(string krl) =>
            krl.Split('\n')
               .Where(l => ScrewCmd.IsMatch(l) && !l.Contains("0.00", StringComparison.Ordinal))
               .Select(l => l.Split(';')[0].Trim())
               .ToList();
    }

    [Fact]
    public void Analog_keepalive_uses_TRIGGER_so_it_cannot_break_continuous_path()
    {
        // A bare "$ANOUT[4] = x" between motions forces an advance-run stop, collapsing the
        // $ADVANCE look-ahead. Shipped once and showed up in the field as periodic marks on
        // a 17 h print (4,261 of them). Every keep-alive must ride the motion as a TRIGGER.
        var krl = KrlExporter.Export(ContinuousLoopTower(layers: 5, sideMm: 2000f), Settings());

        var bare = krl.Split('\n')
            .Where(l => l.Contains("keep-alive", StringComparison.Ordinal))
            .Where(l => !l.TrimStart().StartsWith("TRIGGER", StringComparison.Ordinal))
            .ToList();

        Assert.True(bare.Count == 0,
            $"{bare.Count} keep-alive line(s) are bare assignments: {string.Join(" | ", bare.Take(3))}");
        Assert.Contains("TRIGGER", krl, StringComparison.Ordinal);
    }

    [Fact]
    public void Disabled_keepalive_emits_no_keepalive_lines()
    {
        var krl = KrlExporter.Export(ContinuousLoopTower(4, 2000f), Settings(keepAlive: false));
        Assert.DoesNotContain("keep-alive", krl, StringComparison.Ordinal);
    }
}
