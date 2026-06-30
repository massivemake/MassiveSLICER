namespace MassiveSlicer.Core.Slicing.Curved;

/// <summary>Weighted interpolation scalar field between LOW and HIGH boundary targets.</summary>
public static class InterpolationField
{
    /// <summary>
    /// f_t(v) = (1-t) * d_low(v) - t * d_high(v). Zero isocontour is the layer at parameter t.
    /// </summary>
    public static float[] Compute(float t, BoundaryTarget low, BoundaryTarget high)
    {
        var dLow  = low.Distances;
        var dHigh = high.Distances;
        var field = new float[dLow.Length];
        float oneMinusT = 1f - t;

        for (int i = 0; i < field.Length; i++)
            field[i] = oneMinusT * dLow[i] - t * dHigh[i];

        return field;
    }
}