using System.Numerics;

namespace MassiveSlicer.Core.Slicing.TreeSupport;

/// <summary>
/// Bed-rooted tree support plan: per-layer closed outline centerlines in plane-local 2D.
/// Each tree is a simple rectangle that is narrow on the bed and flares out toward
/// the supported tip geometry — not a cluster of circular posts.
/// </summary>
public sealed class TreeSupportPlan
{
    public TreeSupportLayerPlan[] Layers { get; }

    public int TreesBorn { get; set; }
    public int DemandPoints { get; set; }

    public TreeSupportPlan(int layerCount)
    {
        Layers = new TreeSupportLayerPlan[layerCount];
        for (int i = 0; i < layerCount; i++)
            Layers[i] = new TreeSupportLayerPlan();
    }
}

public sealed class TreeSupportLayerPlan
{
    /// <summary>
    /// Closed outline centerlines (typically a 4-corner rectangle, last point may
    /// equal first). Generator inflates each into a single dual-wall loop.
    /// </summary>
    public List<List<Vector2>> Branches { get; } = [];
}
