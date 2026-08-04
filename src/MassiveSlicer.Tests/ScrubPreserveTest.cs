using System.Numerics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.ViewModels;

namespace MassiveSlicer.Tests;

/// <summary>
/// Where the timeline lands after a re-slice, and therefore where the robot gets posed.
/// </summary>
/// <remarks>
/// Jeff, 2026-08-03: scaling a part made "the robot head jump to a different spot". Measured on
/// the running app — a 50% scale swung A5 by 38.7°. The cause was here rather than in the IK: the
/// scrub position was preserved as an absolute move number, and a resize changes the move count
/// wholesale (~95,000 → ~35,000 on the test part), so the old index clamped straight to the end of
/// the new path. His call: "the arm should stick to whatever position in the print it was in."
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
    public void A_reslice_that_shrinks_the_path_keeps_the_same_fraction_of_it()
    {
        var vm = AtFraction(95_206, 0.40);
        Assert.Equal(38_082, vm.ToolpathScrubIndex);

        vm.ResetScrubIndex(34_659, PathWith(34_659), preservePosition: true);

        // 40% of the new path, not the old move number clamped to the end.
        Assert.Equal(13_863, vm.ToolpathScrubIndex);
        Assert.NotEqual(34_659, vm.ToolpathScrubIndex);
    }

    [Fact]
    public void A_reslice_that_grows_the_path_keeps_the_same_fraction_too()
    {
        var vm = AtFraction(1_000, 0.25);

        vm.ResetScrubIndex(4_000, PathWith(4_000), preservePosition: true);

        Assert.Equal(1_000, vm.ToolpathScrubIndex);
    }

    [Fact]
    public void A_same_length_reslice_lands_on_the_same_move()
    {
        // The ordinary case — a settings change that does not resize the part — must be untouched.
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
    public void A_caller_supplied_fraction_beats_a_corrupted_live_index()
    {
        // The exact numbers traced out of the running app, 2026-08-03. Scaling a part to 50%
        // takes the path from 95,206 moves to 34,659. Partway through the re-slice the layer-high
        // slider's two-way binding writes 34,659 — a NEW-path index — into the scrub while
        // ToolpathScrubMax is still 95,206. Reading the live index then says "36% through" for a
        // user who was looking at the whole path, and only a third of the part gets drawn.
        // Jeff saw exactly that: "Its only going like 1/3 up the mesh."
        var vm = new ViewportViewModel();
        vm.ResetScrubIndex(95_206, PathWith(95_206));
        Assert.Equal(95_206, vm.ToolpathScrubIndex);      // whole path visible

        vm.ToolpathScrubIndex = 34_659;                   // the binding's stale write

        vm.ResetScrubIndex(34_659, PathWith(34_659), preservePosition: true,
                           preserveFraction: 1.0);        // captured before the slice began

        Assert.Equal(34_659, vm.ToolpathScrubIndex);      // still the whole path
    }

    [Fact]
    public void A_supplied_fraction_is_honoured_mid_path()
    {
        // Guards against "just always land at the end", which would pass the test above and
        // silently undo the robot-position fix.
        var vm = AtFraction(95_206, 0.40);

        vm.ResetScrubIndex(34_659, PathWith(34_659), preservePosition: true,
                           preserveFraction: 0.40);

        Assert.Equal(13_864, vm.ToolpathScrubIndex);
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
}
