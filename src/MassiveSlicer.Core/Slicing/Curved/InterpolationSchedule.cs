namespace MassiveSlicer.Core.Slicing.Curved;

/// <summary>Layer count and interpolation parameter schedule (compas InterpolationSlicer).</summary>
public static class InterpolationSchedule
{
    public static int FindNoOfIsocurves(BoundaryTarget low, BoundaryTarget high, float avgLayerHeight)
    {
        float avgDs0 = low.GetAvgDistanceFromOther(high);
        float avgDs1 = high.GetAvgDistanceFromOther(low);
        int n = (int)((avgDs0 + avgDs1) * 0.5f / Math.Max(avgLayerHeight, 1e-4f));
        return Math.Max(1, n);
    }

    /// <summary>Returns t values in (0, 1], ending at 0.997.</summary>
    public static List<float> GetInterpolationParameters(int numberOfCurves)
    {
        var tList = new List<float>(numberOfCurves + 1);
        for (int i = 1; i <= numberOfCurves; i++)
            tList.Add(i / (float)(numberOfCurves + 1));
        tList.Add(0.997f);
        return tList;
    }
}