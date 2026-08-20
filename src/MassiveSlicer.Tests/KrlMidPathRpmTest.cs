using System.Numerics;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Tests;

/// <summary>
/// Mid-path extrusion-rate changes in URM / Digital Start-Stop mode.
///
/// <para><b>The bug these pin.</b> In URM mode the exporter emitted a bare <c>RPM = x</c> and
/// silently ignored its own <c>useTrigger</c> flag — while the $ANOUT path honoured it. A bare KRL
/// assignment executes in the ADVANCE RUN, and the program header sets <c>$ADVANCE=5</c>, so the
/// write fires up to five motion blocks before the arm reaches that point.</para>
///
/// <para>That was harmless for years because RPM was written ONCE per print: one early assignment
/// lands before any motion and never changes. When flow started varying mid-layer — adaptive layer
/// height, then proximity correction — every write began firing ahead of the nozzle in bursts.
/// Measured on a real 1,611 m part: 1,717 setpoint changes, 338 distinct values, the closest pair
/// 0.14 mm apart, and 37 % of changes smaller than one RPM point. On the machine the commanded
/// setpoint moved while the real screw speed stopped following it. Jeff: <i>"real always follows
/// set, just not in our new exports"</i>, and he correctly picked mid-layer RPM change as the one
/// thing that had never been done before.</para>
/// </summary>
public sealed class KrlMidPathRpmTest
{
    /// <summary>URM mode, so RPM goes out as Caracol MAT rather than $ANOUT.</summary>
    private static KrlExportSettings Urm() => new()
    {
        ProgramName             = "midpath_rpm",
        Temperature1            = 280f,
        Temperature2            = 285f,
        Temperature3            = 290f,
        BeadWidthMm             = 8f,
        LayerHeightMm           = 4f,
        FlowRate                = 0.5379f,
        PrintSpeedMps           = 0.092f,
        DigitalStartStopEnabled = true,
    };

    /// <summary>
    /// One layer of long straight beads. <paramref name="widthScales"/> gives each bead its own
    /// flow factor — the shape proximity correction produces on an arm.
    /// </summary>
    private static Toolpath Beads(params float[] widthScales)
    {
        var tp = new Toolpath();
        var l  = new ToolpathLayer(0, 4f) { Height = 4f, PlaneNormal = Vector3.UnitZ };
        float x = 0f;
        foreach (var w in widthScales)
        {
            l.Moves.Add(new ToolpathMove(
                new Vector3(x, 0, 4f), new Vector3(x + 300f, 0, 4f), MoveKind.Extrude)
            {
                Normal = Vector3.UnitZ,
                WidthScale = w,
            });
            x += 300f;
        }
        tp.Layers.Add(l);
        return tp;
    }

    private static List<string> RpmLines(string krl) =>
        krl.Split('\n').Select(l => l.Trim())
           .Where(l => l.Contains("RPM =") && !l.StartsWith(";"))
           .ToList();

    // -- the fix ------------------------------------------------------------------------------

    /// <summary>
    /// ⭐ A rate change in the middle of a path must be a TRIGGER, so it lands where the nozzle is
    /// rather than wherever the advance run has reached.
    /// </summary>
    [Fact]
    public void A_mid_path_rate_change_is_emitted_as_a_synchronised_trigger()
    {
        // 1.0 then 0.75 — an outer wall followed by a crowded arm.
        var krl = KrlExporter.Export(Beads(1.0f, 0.75f), Urm());
        var lines = RpmLines(krl);

        // The change itself must be synchronised.
        Assert.Contains(lines, l => l.StartsWith("TRIGGER WHEN DISTANCE=0 DELAY=0 DO RPM ="));

        // And it must be the CHANGE that is triggered, not just any RPM line.
        var change = lines.First(l => l.Contains("rpm change"));
        Assert.StartsWith("TRIGGER WHEN DISTANCE=0 DELAY=0 DO RPM =", change);
    }

    /// <summary>
    /// The value still has to be right — synchronising it must not disturb the arithmetic.
    /// 8 x 4 x 0.092 x 0.5379 x 60 = 95.01; x 0.75 = 71.26.
    /// </summary>
    [Fact]
    public void The_triggered_value_is_the_correct_reduced_rate()
    {
        var krl = KrlExporter.Export(Beads(1.0f, 0.75f), Urm());
        var change = RpmLines(krl).First(l => l.Contains("rpm change"));

        var num = System.Text.RegularExpressions.Regex.Match(change, @"RPM = ([\d.]+)");
        Assert.True(num.Success, $"no numeric RPM in: {change}");
        float v = float.Parse(num.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(71.26f, v, 1);
    }

    /// <summary>
    /// ⭐ A difference too small to act on must not cost a setpoint write at all. This is the
    /// 6 mm U-turn tip between two arm walls: 71.07596 against 71.26099, 0.26 % apart.
    /// </summary>
    [Fact]
    public void A_change_smaller_than_one_rpm_point_is_not_written()
    {
        // 0.75 then 0.748 -> 71.26 vs 71.07, a 0.19-point difference.
        var krl = KrlExporter.Export(Beads(0.75f, 0.748f), Urm());
        var nums = RpmLines(krl)
            .Select(l => System.Text.RegularExpressions.Regex.Match(l, @"RPM = ([\d.]+)"))
            .Where(m => m.Success)
            .Select(m => float.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
            .Where(v => v > 1f)
            .Distinct()
            .ToList();

        Assert.Single(nums);
        Assert.Equal(71.26f, nums[0], 1);
    }

    /// <summary>A change big enough to matter is still written — the deadband must not swallow real ones.</summary>
    [Fact]
    public void A_change_bigger_than_the_deadband_is_still_written()
    {
        var krl = KrlExporter.Export(Beads(1.0f, 0.75f), Urm());
        var nums = RpmLines(krl)
            .Select(l => System.Text.RegularExpressions.Regex.Match(l, @"RPM = ([\d.]+)"))
            .Where(m => m.Success)
            .Select(m => float.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
            .Where(v => v > 1f)
            .Distinct()
            .ToList();

        Assert.Equal(2, nums.Count);
    }

    /// <summary>
    /// A stop is never a "small change". Suppressing a transition to or from zero would leave the
    /// extruder running through a travel.
    /// </summary>
    [Fact]
    public void A_transition_to_or_from_a_stop_is_never_suppressed()
    {
        Assert.Equal(1.0f, KrlExporter.MinRpmChangePercent, 3);

        var tp = new Toolpath();
        var l  = new ToolpathLayer(0, 4f) { Height = 4f, PlaneNormal = Vector3.UnitZ };
        l.Moves.Add(new ToolpathMove(new Vector3(0,0,4f), new Vector3(300,0,4f), MoveKind.Extrude)
            { Normal = Vector3.UnitZ });
        l.Moves.Add(new ToolpathMove(new Vector3(300,0,4f), new Vector3(600,0,4f), MoveKind.Travel));
        l.Moves.Add(new ToolpathMove(new Vector3(600,0,4f), new Vector3(900,0,4f), MoveKind.Extrude)
            { Normal = Vector3.UnitZ });
        tp.Layers.Add(l);

        var krl = KrlExporter.Export(tp, Urm());
        Assert.Contains(RpmLines(krl), l2 => l2.Contains("RPM = 0"));
    }

    /// <summary>
    /// A print with ONE rate throughout must be unchanged — that is the shape every historically
    /// good print had, and it must not acquire triggers it never needed.
    /// </summary>
    [Fact]
    public void A_single_rate_print_writes_no_mid_path_changes()
    {
        var krl = KrlExporter.Export(Beads(1.0f, 1.0f, 1.0f, 1.0f), Urm());
        Assert.DoesNotContain(RpmLines(krl), l => l.Contains("rpm change"));
    }
}
