namespace MassiveSlicer.App;

/// <summary>
/// Which moves a click may select in the 2D slice viewer.
/// The drawn band is the active layer plus the ghost layers below it.
/// A click on those lines must land in this window — the old code only
/// kept the active layer, and a midpoint reject dropped the ends of a
/// long top-down wall before the segment test ran.
/// </summary>
public static class SlicePlanePick
{
    public static (int Start, int Limit) Window(int currentLayer, int[] ends, int belowCount)
    {
        if (ends.Length == 0) return (0, 0);
        int cur = Math.Clamp(currentLayer, 0, ends.Length - 1);
        int lo = Math.Max(0, cur - Math.Max(0, belowCount));
        int start = lo <= 0 ? 0 : ends[lo - 1];
        return (start, ends[cur]);
    }
}
