using System.Numerics;

namespace MassiveSlicer.Core.Models;

public enum PaintMarkKind
{
    /// <summary>Beads under this mark need support: the Formbound planner grows
    /// fingers beneath them (manual demand, mesh-oracle veto bypassed — the user
    /// explicitly asked).</summary>
    Bridge,

    /// <summary>Beads inside this mark are removed from the toolpath (artifact
    /// cleanup / manual deletions); the gap is spliced with a travel.</summary>
    Remove,

    /// <summary>Edit-mode Offset path: parallel copies of a selected span were
    /// inserted into the toolpath (not a paint dab — list-entry only).</summary>
    Offset,
}

/// <summary>
/// Role of a Bridge mark when steering a paint-driven Formbound column.
/// </summary>
public enum PaintBridgeRole
{
    /// <summary>Generic / legacy Bridge demand (treated as support-bar samples).</summary>
    None = 0,

    /// <summary>
    /// Samples along the Support selection — open a full-width T bar at this height.
    /// </summary>
    SupportBar = 1,

    /// <summary>
    /// Single foot at the mid of the bridge Target — one perimeter mouth; column
    /// rises from here. Never opens additional perimeter breaks.
    /// </summary>
    ColumnFoot = 2,
}

/// <summary>
/// Support generator style for a Bridge mark / edit-mode Support modification.
/// Stored per mark so mixed areas on one part can use different generators.
/// </summary>
public enum PaintSupportStyle
{
    /// <summary>Formbound Buttress — wall mouth → horizontal T bar.</summary>
    FormboundButtress = 0,

    /// <summary>Formbound Bridge — wall-rooted radial dual-wall fingers.</summary>
    FormboundBridge = 1,

    /// <summary>Tree Support — bed-rooted branching single-bead paths.</summary>
    Tree = 2,

    /// <summary>Structural Support — 2×4 pocket / cylinder wrap spliced into the wall
    /// at a fixed anchor so the neck stacks vertically (StructuralSupportPlanner).</summary>
    StructuralSupport = 3,
}

/// <summary>
/// Which side of the wall Formbound grows on for a paint selection.
/// Inside = interior notch (default). Outside = sacrificial exterior fin.
/// </summary>
public enum PaintSupportSide
{
    Inside = 0,
    Outside = 1,
}

/// <summary>Helpers for <see cref="PaintSupportSide"/> ↔ UI strings.</summary>
public static class PaintSupportSideUtil
{
    public const string LabelInside = "Inside";
    public const string LabelOutside = "Outside";

    public static readonly string[] AllLabels = [LabelInside, LabelOutside];

    public static PaintSupportSide FromLabel(string? label) =>
        string.Equals(label, LabelOutside, StringComparison.OrdinalIgnoreCase)
            ? PaintSupportSide.Outside
            : PaintSupportSide.Inside;

    public static string ToLabel(PaintSupportSide s) =>
        s == PaintSupportSide.Outside ? LabelOutside : LabelInside;

    public static bool IsOutside(PaintSupportSide s) => s == PaintSupportSide.Outside;
}

/// <summary>Helpers for <see cref="PaintSupportStyle"/> ↔ UI / workspace strings.</summary>
public static class PaintSupportStyleUtil
{
    public const string LabelButtress = "Formbound Buttress";
    public const string LabelBridge = "Formbound Bridge";
    public const string LabelTree = "Tree Support";
    public const string LabelStructural = "Structural Support";

    public static readonly string[] AllLabels =
        [LabelButtress, LabelBridge, LabelTree, LabelStructural];

    public static bool IsFormbound(PaintSupportStyle s) =>
        s is PaintSupportStyle.FormboundButtress or PaintSupportStyle.FormboundBridge;

    public static bool IsTree(PaintSupportStyle s) => s == PaintSupportStyle.Tree;

    public static PaintSupportStyle FromLabel(string? label) => label switch
    {
        LabelBridge or "Lightning Bridge" or "Formbound Bridge" => PaintSupportStyle.FormboundBridge,
        LabelTree or "Tree" or "Tree Support" => PaintSupportStyle.Tree,
        LabelStructural or "Structural" or "Structural Support" => PaintSupportStyle.StructuralSupport,
        _ => PaintSupportStyle.FormboundButtress,
    };

    public static string ToLabel(PaintSupportStyle s) => s switch
    {
        PaintSupportStyle.FormboundBridge => LabelBridge,
        PaintSupportStyle.Tree => LabelTree,
        PaintSupportStyle.StructuralSupport => LabelStructural,
        _ => LabelButtress,
    };

    /// <summary>Map style → global InfillPattern used when only one Formbound style is active.</summary>
    public static InfillPattern ToFormboundPattern(PaintSupportStyle s) => s switch
    {
        PaintSupportStyle.FormboundBridge => InfillPattern.LightningBridge,
        _ => InfillPattern.FormboundButtress,
    };

    public static bool HasFormboundPaint(IEnumerable<PaintMark> marks) =>
        marks.Any(m => m.Kind == PaintMarkKind.Bridge && IsFormbound(m.SupportStyle));

    public static bool HasTreePaint(IEnumerable<PaintMark> marks) =>
        marks.Any(m => m.Kind == PaintMarkKind.Bridge && IsTree(m.SupportStyle));

    public static bool HasAnySupportPaint(IEnumerable<PaintMark> marks) =>
        marks.Any(m => m.Kind == PaintMarkKind.Bridge);

    /// <summary>
    /// Pick the Formbound InfillPattern from paint marks (majority of Formbound styles).
    /// Returns null when no Formbound paint exists.
    /// </summary>
    public static InfillPattern? ResolveFormboundPatternFromPaint(IEnumerable<PaintMark> marks)
    {
        int buttress = 0, bridge = 0;
        foreach (var m in marks)
        {
            if (m.Kind != PaintMarkKind.Bridge || !IsFormbound(m.SupportStyle)) continue;
            if (m.SupportStyle == PaintSupportStyle.FormboundBridge) bridge++;
            else buttress++;
        }
        if (buttress + bridge == 0) return null;
        return bridge > buttress ? InfillPattern.LightningBridge : InfillPattern.FormboundButtress;
    }
}

/// <summary>One painted brush dab in WORLD space. World-space (not move indices)
/// so marks survive re-slices and setting changes; they persist with the
/// workspace.</summary>
public sealed record PaintMark(
    Vector3 Center,
    float Radius,
    PaintMarkKind Kind,
    PaintBridgeRole BridgeRole = PaintBridgeRole.None,
    PaintSupportStyle SupportStyle = PaintSupportStyle.FormboundButtress,
    PaintSupportSide SupportSide = PaintSupportSide.Inside);

/// <summary>
/// Plane-local manual demand for one slicing layer: support-bar samples (T width)
/// and optional column-foot (single mouth / aim). Parallel <see cref="SupportBarSides"/>
/// / <see cref="ColumnFootSides"/> lists carry per-sample Inside/Outside (same count).
/// </summary>
public sealed class ManualDemandLayer
{
    public List<Vector2> SupportBar { get; } = [];
    public List<PaintSupportSide> SupportBarSides { get; } = [];
    public List<Vector2> ColumnFoot { get; } = [];
    public List<PaintSupportSide> ColumnFootSides { get; } = [];
    public bool HasAny => SupportBar.Count > 0 || ColumnFoot.Count > 0;

    public PaintSupportSide SideAtBar(int i) =>
        i >= 0 && i < SupportBarSides.Count ? SupportBarSides[i] : PaintSupportSide.Inside;

    public PaintSupportSide SideAtFoot(int i) =>
        i >= 0 && i < ColumnFootSides.Count ? ColumnFootSides[i] : PaintSupportSide.Inside;
}
