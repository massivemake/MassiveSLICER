using System.Numerics;

namespace MassiveSlicer.Core.Slicing;

/// <summary>
/// Bead-overhang heatmap colours. <paramref name="t"/> is 0 when the bead is
/// fully supported and 1 when it is unsupported.
/// </summary>
public static class BeadOverhangPalette
{
    /// <summary>
    /// <paramref name="spectrum"/> false is white to red.
    /// True is blue, through green at the midpoint, to red.
    /// </summary>
    public static Vector3 Color(float t, bool spectrum)
    {
        if (float.IsNaN(t)) t = 0f;
        t = Math.Clamp(t, 0f, 1f);
        if (!spectrum)
            return new Vector3(1f, 1f - t, 1f - t);

        if (t <= 0.5f)
        {
            float u = t * 2f;
            return new Vector3(0f, u, 1f - u);
        }

        float v = (t - 0.5f) * 2f;
        return new Vector3(v, 1f - v, 0f);
    }
}
