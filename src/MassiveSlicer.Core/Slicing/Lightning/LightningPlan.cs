using System.Numerics;

namespace MassiveSlicer.Core.Slicing.Lightning;

/// <summary>
/// Per-layer lightning finger plan, produced top-down by <see cref="LightningPlanner"/>
/// and consumed bottom-up by <see cref="LightningGenerator"/>. All points are
/// plane-local 2D (world XY for the planar slicer; (u,v) for the angled slicer —
/// its frame is constant across layers, so cross-layer propagation is identical).
/// </summary>
public sealed class LightningPlan
{
    public LightningLayerPlan[] Layers { get; }

    public LightningPlan(int layerCount)
    {
        // One shared dropped-lineage set: emission runs bottom-up, so a tree the
        // generator has to drop at some layer (neck/union guard) must also vanish
        // from every layer above it — otherwise its inherited fingers print in
        // mid-air over the gap.
        var dropped = new HashSet<int>();
        Layers = new LightningLayerPlan[layerCount];
        for (int i = 0; i < layerCount; i++)
            Layers[i] = new LightningLayerPlan { DroppedTrees = dropped };
    }
}

public sealed class LightningLayerPlan
{
    public List<LightningTree> Trees { get; } = [];

    /// <summary>Tree ids removed mid-emission — shared across ALL layers of the plan.</summary>
    public HashSet<int> DroppedTrees { get; init; } = [];
}

/// <summary>One finger tree rooted on a region boundary.</summary>
public sealed class LightningTree
{
    /// <summary>Stable lineage id — survives per-layer cloning, so one tree can be
    /// dropped across every layer at once when it loses its footing.</summary>
    public int Id;

    /// <summary>Root point on the region boundary; re-projected every layer.</summary>
    public Vector2 Anchor;

    /// <summary>External support fin: the slit is UNIONED outside the part instead of
    /// subtracted from it (sacrificial support under outward overhangs).</summary>
    public bool External;

    /// <summary>Branch 0 is the trunk (starts at <see cref="Anchor"/>); later branches
    /// attach to an earlier branch's node (tree merging).</summary>
    public List<LightningBranch> Branches { get; } = [];

    public LightningTree Clone()
    {
        var t = new LightningTree { Id = Id, Anchor = Anchor, External = External };
        foreach (var b in Branches)
            t.Branches.Add(new LightningBranch(new List<Vector2>(b.Centerline))
            {
                ParentBranch = b.ParentBranch,
                ParentNode   = b.ParentNode,
            });
        return t;
    }
}

/// <summary>An open centerline polyline; [0] is the root (anchor or junction), [^1] the tip.</summary>
public sealed class LightningBranch
{
    public List<Vector2> Centerline { get; }

    /// <summary>-1 = rooted at the tree anchor; else index of the parent branch.</summary>
    public int ParentBranch = -1;

    /// <summary>Node index on the parent branch this branch grows from.</summary>
    public int ParentNode;

    public LightningBranch(List<Vector2> centerline) => Centerline = centerline;

    public float ArcLength()
    {
        float len = 0f;
        for (int i = 1; i < Centerline.Count; i++)
            len += Vector2.Distance(Centerline[i - 1], Centerline[i]);
        return len;
    }
}
