namespace MassiveSlicer.App.Views;

/// <summary>
/// Pure pointer policy for the viewport. Kept out of the 18k-line code-behind
/// so click-vs-orbit / paint-miss can be unit-tested without Avalonia.
/// </summary>
public static class ViewportPointerPolicy
{
    /// <summary>
    /// Left release is a click-to-select when we saw the matching press and the
    /// pointer did not move more than the drag slop. True even if that button is
    /// also the orbit/pan binding (Mol3D left-drag, Maya+Alt) — a click must
    /// still select; only an actual drag orbits.
    /// </summary>
    public static bool IsClickSelectRelease(bool sawLeftPress, bool leftDragged)
        => sawLeftPress && !leftDragged;

    /// <summary>
    /// After stopping an orbit/pan binding, return (consume the event, skip
    /// scene pick) only when the gesture was a drag. A click falls through.
    /// </summary>
    public static bool ConsumeOrbitPanRelease(bool isOrbitOrPanButton, bool leftDragged)
        => isOrbitOrPanButton && leftDragged;
}
