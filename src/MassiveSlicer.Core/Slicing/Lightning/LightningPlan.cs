using System.Numerics;
using Clipper2Lib;

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

    /// <summary>Planner diagnostics — demand samples flagged, trees created, mesh vetoes.</summary>
    public int DemandFlags { get; set; }
    public int TreesBorn { get; set; }
    public int MeshVetoes { get; set; }
    public int OrphanedLineages { get; set; }
    /// <summary>Demand samples still uncovered after residual + audit (0 = full coverage).</summary>
    public int UncoveredSamples { get; set; }
    /// <summary>Multi-planar re-roots where UV drift forced a wall snap (column continued).</summary>
    public int InheritSkips { get; set; }
    /// <summary>Inherit recoveries that rebuilt a short MaxStep stub on the same lineage.</summary>
    public int InheritReseeds { get; set; }
    /// <summary>Coverage-audit samples satisfied by extending an existing same-side tree.</summary>
    public int AuditExtensions { get; set; }
    /// <summary>Live tree slots after orphan wipe (sum of per-layer tree counts).</summary>
    public int LiveSlots { get; set; }

    /// <summary>Layer + plane-local coordinates of REAL uncovered demand samples
    /// (capped) — surfaced in the app console so a support gap is never silent.</summary>
    public List<string> UncoveredLog { get; } = [];
    public float BarMm { get; set; }
    public float SpacingMm { get; set; }
    public bool MultiPlanar { get; set; }

    /// <summary>Compact diagnostics for the in-app console (no tree geometry).</summary>
    public FormboundPlanStats ToStats() => new()
    {
        LayerCount       = Layers.Length,
        DemandFlags      = DemandFlags,
        TreesBorn        = TreesBorn,
        LiveSlots        = LiveSlots,
        MeshVetoes       = MeshVetoes,
        OrphanedLineages = OrphanedLineages,
        UncoveredSamples = UncoveredSamples,
        InheritSnaps     = InheritSkips,
        InheritRebuilds  = InheritReseeds,
        AuditExtensions  = AuditExtensions,
        BarMm            = BarMm,
        SpacingMm        = SpacingMm,
        MultiPlanar      = MultiPlanar,
        UncoveredLog     = [.. UncoveredLog],
    };

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

    /// <summary>
    /// Corbel pads (plane-local): small outward boundary extensions grown at the
    /// overhang rate over a few layers so a line just past the wall below lands on
    /// material. Unioned into the region at emission.
    /// </summary>
    public PathsD? CorbelPads { get; set; }

    /// <summary>Tree ids removed mid-emission — shared across ALL layers of the plan.</summary>
    public HashSet<int> DroppedTrees { get; init; } = [];

    /// <summary>
    /// Preferred perimeter loop-start (seam) in plane-local XY. Set from the paint
    /// bridge ColumnFoot (target mid) so every layer opens the contour at the same
    /// place as the buttress mouth — a wandering seam that lands on the notch can
    /// look like a broken / reset column.
    /// </summary>
    public Vector2? SeamPinXY { get; set; }

    /// <summary>Mesh-truth oracle in THIS layer's plane-local frame (the same lift
    /// the planner used), probing just below OR above the plane — set by the slicer
    /// after planning. Used to verify recovered single-bead walls (a real wall has
    /// material adjacent to the plane; a fresh wall's material starts above it).</summary>
    public Func<Vector2, bool>? SolidAt { get; set; }

    /// <summary>Mesh-truth oracle probing exactly AT the plane. A real contour's
    /// interior is solid at its own plane by definition of slicing; a parity
    /// phantom's interior is void there — this is what the island veto asks.
    /// (The near-plane probe above can't tell them apart on grazing cuts: the
    /// surface weaves within a millimetre of the plane across the whole island.)</summary>
    public Func<Vector2, bool>? SolidAtPlane { get; set; }
}

/// <summary>One finger / buttress tree rooted on a region boundary.</summary>
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

    /// <summary>Cavity support: the demand hangs over a MODELED interior void (a
    /// region hole — inside the part's outer envelope but not in material). Realized
    /// like a fin (Union — the tube bulges INTO the hole from its wall) but grows at
    /// the normal overhang MaxStep and is structural, never gated by the sacrificial
    /// fins setting. Anchors on the cavity wall (interior mouths).</summary>
    public bool Cavity;

    /// <summary>Born from a user Support mark (paint / Edit-mode selection) rather
    /// than automatic demand. Manual columns get the lenient inherit path (re-seat
    /// over drifting cavities instead of orphaning) — the user explicitly asked for
    /// support there.</summary>
    public bool Manual;

    /// <summary>Island umbilical: this cavity tube reaches from one region component
    /// to another so the layer stays ONE continuous line (no travel ever starts an
    /// island). Kept at full length on every layer where the island is still
    /// disconnected; below the island it retracts like a normal column.</summary>
    public bool Connector;

    /// <summary>
    /// Paint-driven column: single perimeter mouth under a bridge target / support
    /// paint job. May branch off itself, but the planner must never birth a second
    /// perimeter mouth for this lineage.
    /// </summary>
    public bool PaintColumn;

    /// <summary>Branch 0 is the trunk (starts at <see cref="Anchor"/>); later branches
    /// attach to an earlier branch's node. Formbound Buttress uses trunk = wall approach
    /// and two leaf branches = the horizontal support bar (T morph).</summary>
    public List<LightningBranch> Branches { get; } = [];

    public LightningTree Clone()
    {
        var t = new LightningTree
        {
            Id = Id, Anchor = Anchor, External = External,
            Cavity = Cavity, Connector = Connector, PaintColumn = PaintColumn,
            Manual = Manual,
        };
        foreach (var b in Branches)
            t.Branches.Add(new LightningBranch(new List<Vector2>(b.Centerline))
            {
                ParentBranch = b.ParentBranch,
                ParentNode   = b.ParentNode,
            });
        return t;
    }
}

/// <summary>
/// Formbound planner diagnostics carried on the toolpath so the App console can
/// show demand/coverage after a slice (System.Console alone is invisible in-app).
/// </summary>
public sealed class FormboundPlanStats
{
    public int LayerCount { get; init; }
    public int DemandFlags { get; init; }
    public int TreesBorn { get; init; }
    public int LiveSlots { get; init; }
    public int MeshVetoes { get; init; }
    public int OrphanedLineages { get; init; }
    public int UncoveredSamples { get; init; }
    /// <summary>Multi-planar forced wall snaps that continued the column.</summary>
    public int InheritSnaps { get; init; }
    /// <summary>Same-lineage MaxStep rebuilds during inherit.</summary>
    public int InheritRebuilds { get; init; }
    /// <summary>Audit samples covered by growing an existing tree (not a new birth).</summary>
    public int AuditExtensions { get; init; }
    public float BarMm { get; init; }
    public float SpacingMm { get; init; }
    public bool MultiPlanar { get; init; }

    /// <summary>Per-sample "UNCOVERED layer N at (x,y)" lines (capped).</summary>
    public IReadOnlyList<string> UncoveredLog { get; init; } = [];

    public string ToLogLine() =>
        $"[formbound] plan: layers={LayerCount} demand={DemandFlags} treesBorn={TreesBorn} " +
        $"liveSlots={LiveSlots} meshVetoes={MeshVetoes} orphaned={OrphanedLineages} " +
        $"uncovered={UncoveredSamples} inheritSnap={InheritSnaps} rebuild={InheritRebuilds} " +
        $"auditExtend={AuditExtensions} bar={BarMm:0.#}mm spacing={SpacingMm:0.#}mm multiPlanar={MultiPlanar}" +
        (UncoveredSamples > 0 ? "  ⚠ incomplete overhang coverage" : "  ✓ full demand coverage");
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
