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

    // -- URM latches at the handshake ---------------------------------------------------------

    /// <summary>
    /// ⭐⭐ The one that matters most. URM is a start/stop protocol: the Caracol latches screw speed
    /// at the <c>$OUT[7]</c>/<c>$OUT[8]</c> handshake and holds it for the segment. A bare
    /// <c>RPM = x</c> written mid-path moves the pendant's "set" field and nothing else.
    ///
    /// Verified against a known-good print: it writes RPM exactly FIVE times in 174,000 lines —
    /// init, MAT idle, once inside the start handshake, and off at the end. Mid-path rate change had
    /// never been done on this machine. A real export then carried 1,717 unhandshaken writes and the
    /// extruder stopped following its setpoint entirely, including manual booth entry.
    /// </summary>
    [Fact]
    public void URM_does_not_emit_unhandshaken_mid_path_rate_changes_by_default()
    {
        // Off by default: mid-path rate change must be opted into, never assumed.
        Assert.False(Urm().AllowSubLayerRpmChange);

        // Two beads at different rates in ONE continuous path — no travel, so no handshake.
        var krl = KrlExporter.Export(Beads(1.0f, 0.75f), Urm());

        var rates = RpmLines(krl)
            .Select(l => System.Text.RegularExpressions.Regex.Match(l, @"RPM = ([\d.]+)"))
            .Where(m => m.Success)
            .Select(m => float.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
            .Where(v => v > 1f)
            .Distinct()
            .ToList();

        Assert.Single(rates);
        Assert.DoesNotContain(RpmLines(krl), l => l.Contains("rpm change"));
    }

    /// <summary>A dropped correction must be stated in the program, not silently omitted.</summary>
    [Fact]
    public void A_dropped_correction_is_announced_in_the_program()
    {
        var krl = KrlExporter.Export(Beads(1.0f, 0.75f), Urm());
        Assert.Contains("FLOW CORRECTION NOT APPLIED", krl);
        Assert.Contains("within-layer extrusion-rate change(s) were ", krl);
    }

    /// <summary>
    /// A single-rate print must gain no warning — that is every historically good URM print.
    /// </summary>
    [Fact]
    public void A_single_rate_urm_print_carries_no_warning()
    {
        var krl = KrlExporter.Export(Beads(1.0f, 1.0f, 1.0f), Urm());
        Assert.DoesNotContain("FLOW CORRECTION NOT APPLIED", krl);
    }

    /// <summary>Non-URM ($ANOUT) exports are unaffected — that path has always changed mid-path.</summary>
    [Fact]
    public void The_anout_path_still_varies_rate_mid_path()
    {
        var s = Urm() with { DigitalStartStopEnabled = false };
        var krl = KrlExporter.Export(Beads(1.0f, 0.75f), s);
        int writes = System.Text.RegularExpressions.Regex.Matches(krl, @"ANOUT\[4\]").Count;
        Assert.True(writes >= 2, $"expected the ANOUT path to still vary; got {writes} write(s)");
    }

    // -- the fix ------------------------------------------------------------------------------

    /// <summary>
    /// ⭐ A rate change in the middle of a path must be a TRIGGER, so it lands where the nozzle is
    /// rather than wherever the advance run has reached.
    /// </summary>
    [Fact]
    public void A_mid_path_rate_change_is_emitted_as_a_synchronised_trigger()
    {
        // 1.0 then 0.75 — an outer wall followed by a crowded arm.
        // Explicitly opted in: this test is about the FORM of the write, not whether URM allows it.
        var krl = KrlExporter.Export(Beads(1.0f, 0.75f), Urm() with { AllowSubLayerRpmChange = true });
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
        var krl = KrlExporter.Export(
            Beads(1.0f, 0.75f), Urm() with { AllowSubLayerRpmChange = true });
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
        var krl = KrlExporter.Export(
            Beads(1.0f, 0.75f), Urm() with { AllowSubLayerRpmChange = true });
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
    /// ⭐ Regression. The deadband must compare against the percentage LAST WRITTEN, not against the
    /// previous raw scale re-resolved through the current layer's settings.
    ///
    /// A scale is meaningless without the settings it resolves through. The first layer carries its
    /// own RPM override, so re-resolving layer 0's scale using layer 1's settings made 22 % and 40 %
    /// both read as 40 — the change looked like zero and got suppressed, and the layer-1 rate was
    /// never written. Caught by KrlExporterTest.Export_first_layer_speed_and_rpm_override, which
    /// this change broke.
    /// </summary>
    [Fact]
    public void A_first_layer_override_boundary_is_not_swallowed_by_the_deadband()
    {
        var tp = new Toolpath();
        foreach (var (idx, z) in new[] { (0, 4f), (1, 8f) })
        {
            var l = new ToolpathLayer(idx, z) { Height = 4f, PlaneNormal = Vector3.UnitZ };
            l.Moves.Add(new ToolpathMove(
                new Vector3(0, 0, z), new Vector3(300f, 0, z), MoveKind.Extrude)
                { Normal = Vector3.UnitZ });
            tp.Layers.Add(l);
        }

        // layer 0 forced low, layer 1 takes the geometric rate
        var s = Urm() with { FirstLayerRpmPercent = 22f };

        var krl = KrlExporter.Export(tp, s);

        // Both rates must appear: the override AND the normal rate that follows it.
        Assert.Contains("RPM = 22", krl);
        var nums = RpmLines(krl)
            .Select(l => System.Text.RegularExpressions.Regex.Match(l, @"RPM = ([\d.]+)"))
            .Where(m => m.Success)
            .Select(m => float.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
            .Where(v => v > 1f)
            .Distinct()
            .ToList();
        Assert.True(nums.Count >= 2,
            $"only {nums.Count} rate(s) written ({string.Join(", ", nums)}) — the layer-1 rate was "
          + "suppressed, so the deadband is comparing a stale scale against new layer settings");
    }

    /// <summary>
    /// The discriminator itself, in one program: a per-LAYER rate change and a sub-layer one, with
    /// the sub-layer gate OFF. The per-layer change must be written and the sub-layer change must
    /// not — no travel anywhere, so nothing but the mechanism tells them apart.
    ///
    /// <para>This is the guarantee that matters. Per-layer rate has printed successfully on these
    /// cells for a long time; the sub-layer path is the new thing. A guard that cannot tell them
    /// apart takes the working mechanism down with the broken one, which is exactly what an earlier
    /// blanket "no rate change mid-path in URM" rule did.</para>
    /// </summary>
    [Fact]
    public void A_per_layer_rate_change_is_kept_while_a_sub_layer_one_is_dropped()
    {
        var tp = new Toolpath();
        foreach (var (idx, z) in new[] { (0, 4f), (1, 8f) })
        {
            var l = new ToolpathLayer(idx, z) { Height = 4f, PlaneNormal = Vector3.UnitZ };
            // Two beads per layer: the second is crowded, so it asks for a sub-layer reduction.
            l.Moves.Add(new ToolpathMove(
                new Vector3(0, 0, z), new Vector3(300f, 0, z), MoveKind.Extrude)
                { Normal = Vector3.UnitZ });
            l.Moves.Add(new ToolpathMove(
                new Vector3(300f, 0, z), new Vector3(600f, 0, z), MoveKind.Extrude)
                { Normal = Vector3.UnitZ, WidthScale = 0.75f });
            tp.Layers.Add(l);
        }

        // Layer 0 forced to 22 %; layer 1 runs at the geometric rate. That is the per-layer change.
        var s = Urm() with { FirstLayerRpmPercent = 22f };
        Assert.False(s.AllowSubLayerRpmChange);

        var krl = KrlExporter.Export(tp, s);

        // Per-layer: both layer rates present.
        Assert.Contains("RPM = 22", krl);
        var rates = RpmLines(krl)
            .Select(l => System.Text.RegularExpressions.Regex.Match(l, @"RPM = ([\d.]+)"))
            .Where(m => m.Success)
            .Select(m => float.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
            .Where(v => v > 1f)
            .Distinct()
            .ToList();
        Assert.True(rates.Count >= 2,
            $"the per-layer rate change was swallowed — only {rates.Count} rate(s): "
          + string.Join(", ", rates));

        // Sub-layer: the 0.75 reduction never reaches the program, and says so.
        Assert.DoesNotContain(RpmLines(krl), l => l.Contains("rpm change"));
        Assert.Contains("SUB-LAYER FLOW CORRECTION NOT APPLIED", krl);
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
