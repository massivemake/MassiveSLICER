namespace MassiveSlicer.App;

/// <summary>
/// A world-axis ring on the toolhead writes the TOOLHEAD GLOBAL ORIENTATION
/// sliders. Panel X is KUKA C, panel Y is KUKA B, panel Z is KUKA A.
/// </summary>
internal static class ToolheadOrientationDrag
{
    /// <summary>
    /// Adds <paramref name="deltaDeg"/> to one slider. Axis 0 is the X ring
    /// (C), 1 is the Y ring (B), 2 is the Z ring (A). Any other axis leaves
    /// the angles alone. The result stays inside −180..180.
    /// </summary>
    public static (float A, float B, float C) Apply(
        float a, float b, float c, int axis, float deltaDeg)
    {
        return axis switch
        {
            0 => (a, b, Wrap(c + deltaDeg)),
            1 => (a, Wrap(b + deltaDeg), c),
            2 => (Wrap(a + deltaDeg), b, c),
            _ => (a, b, c),
        };
    }

    /// <summary>Folds a degree value into −180..180. −180 and 180 both stay put.</summary>
    public static float Wrap(float deg)
    {
        float x = deg % 360f;
        if (x > 180f) x -= 360f;
        else if (x < -180f) x += 360f;
        return x;
    }
}
