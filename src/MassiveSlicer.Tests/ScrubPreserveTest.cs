using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.ViewModels;

namespace MassiveSlicer.Tests;

/// <summary>
/// How much of the toolpath stays drawn across a re-slice — and therefore both what Body view
/// renders and where the robot gets posed.
/// </summary>
/// <remarks>
/// Two separate faults met here, 2026-08-03, and fixing either alone looked like it made things
/// worse:
/// <list type="number">
/// <item>A bound control wrote a NEW-path move index into the scrub while ToolpathScrubMax still
/// held the OLD path's total, mid-swap. Fixed by the _scrubResetting guard.</item>
/// <item>The position was preserved as an absolute move number. Shrinking the path clamped it to
/// the end; growing it left it where it was — coming back from 25% to full size drew 11% of the
/// part. Fixed by preserving the fraction instead, which only works once (1) is fixed.</item>
/// </list>
/// </remarks>
public class ScrubPreserveTest
{
    /// <summary>A toolpath with <paramref name="moves"/> moves spread over two layers.</summary>
    private static Toolpath PathWith(int moves)
    {
        var path = new Toolpath();
        for (int layer = 0; layer < 2; layer++)
        {
            var l = new ToolpathLayer(layer, layer * 3f);
            int count = layer == 0 ? (moves + 1) / 2 : moves / 2;
            for (int i = 0; i < count; i++)
                l.Moves.Add(new ToolpathMove(Vector3.Zero, Vector3.UnitX, MoveKind.Extrude));
            path.Layers.Add(l);
        }
        return path;
    }

    private static ViewportViewModel AtFraction(int max, double fraction)
    {
        var vm = new ViewportViewModel();
        vm.ResetScrubIndex(max, PathWith(max));               // first select: lands at the end
        vm.ToolpathScrubIndex = (int)(max * fraction);
        return vm;
    }

    [Fact]
    public void A_full_path_stays_full_when_the_part_shrinks()
    {
        // 95,206 -> 34,659 is a real 50% scale on Jeff's test part.
        var vm = new ViewportViewModel();
        vm.ResetScrubIndex(95_206, PathWith(95_206));
        Assert.Equal(95_206, vm.ToolpathScrubIndex);

        vm.ResetScrubIndex(34_659, PathWith(34_659), preservePosition: true);

        Assert.Equal(34_659, vm.ToolpathScrubIndex);
    }

    [Fact]
    public void A_full_path_stays_full_when_the_part_grows_again()
    {
        // Scale Reset, coming back from 25%. The absolute-index rule left this at 10,433 of
        // 95,206 — 11% of the part drawn — which is what "reset gave me the wrong sized
        // toolpath" actually was.
        var vm = new ViewportViewModel();
        vm.ResetScrubIndex(10_433, PathWith(10_433));
        Assert.Equal(10_433, vm.ToolpathScrubIndex);

        vm.ResetScrubIndex(95_206, PathWith(95_206), preservePosition: true);

        Assert.Equal(95_206, vm.ToolpathScrubIndex);
    }

    [Fact]
    public void A_part_way_position_keeps_its_fraction_of_the_path()
    {
        // Guards against "just always land at the end", which would pass both tests above while
        // silently undoing the robot-position behaviour Jeff asked for: "the arm should stick to
        // whatever position in the print it was in."
        var vm = AtFraction(95_206, 0.40);
        Assert.Equal(38_082, vm.ToolpathScrubIndex);

        vm.ResetScrubIndex(34_659, PathWith(34_659), preservePosition: true);

        Assert.Equal(13_863, vm.ToolpathScrubIndex);
    }

    [Fact]
    public void A_same_length_reslice_lands_on_the_same_move()
    {
        // A settings change that does not resize the part must be untouched.
        var vm = AtFraction(4_200, 0.5);

        vm.ResetScrubIndex(4_200, PathWith(4_200), preservePosition: true);

        Assert.Equal(2_100, vm.ToolpathScrubIndex);
    }

    [Fact]
    public void A_fresh_selection_still_lands_at_the_end_of_the_path()
    {
        // preservePosition: false is first-select, and landing on the finished part is deliberate.
        var vm = AtFraction(4_200, 0.5);

        vm.ResetScrubIndex(900, PathWith(900), preservePosition: false);

        Assert.Equal(900, vm.ToolpathScrubIndex);
    }

    [Fact]
    public void A_blank_window_is_never_preserved()
    {
        // Scrub index is an exclusive end, so 0 draws nothing. Re-arming edit scrub with a stale
        // 0 used to leave the viewport empty; the fraction maths must not reintroduce that.
        var vm = new ViewportViewModel();
        vm.ResetScrubIndex(5_000, PathWith(5_000));
        vm.ToolpathScrubIndex = 0;

        vm.ResetScrubIndex(3_000, PathWith(3_000), preservePosition: true);

        Assert.Equal(3_000, vm.ToolpathScrubIndex);
    }

    [Fact]
    public void A_write_arriving_mid_swap_cannot_corrupt_the_position()
    {
        // The layer-high slider's two-way binding fires while RebuildScrubLayerEnds has already
        // described the new path but the max still holds the old one. Traced live: a full
        // 95,206/95,206 path came back as 34,659, and the fraction rule then drew 36% of the part
        // and posed the robot to match. ResetScrubIndex must be atomic against such writes.
        var vm = new ViewportViewModel();
        vm.ResetScrubIndex(95_206, PathWith(95_206));

        // Stand in for the binding: a write is the only thing that could land in that window.
        vm.ToolpathScrubIndex = 34_659;
        Assert.Equal(34_659, vm.ToolpathScrubIndex);   // an ordinary edit is still honoured

        vm.ResetScrubIndex(34_659, PathWith(34_659), preservePosition: true);

        // 34,659 of 95,206 is 36.4%; had the mid-swap write been treated as the user's position
        // this would be 12,617 and the part would render a third built.
        Assert.Equal(12_617, vm.ToolpathScrubIndex);
    }
}
