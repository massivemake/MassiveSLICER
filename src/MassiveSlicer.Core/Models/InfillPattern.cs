namespace MassiveSlicer.Core.Models;

public enum InfillPattern
{
    /// <summary>No infill — shells printed as contour loops (default behaviour).</summary>
    None,

    /// <summary>Parallel zigzag lines at a fixed angle. One continuous path per layer.</summary>
    Rectilinear,

    /// <summary>Alternates between 0° and 90° on consecutive layers, producing a grid texture.</summary>
    Grid,

    /// <summary>Cycles through 0°, 60°, and 120° across three consecutive layers.</summary>
    Triangle,

    /// <summary>
    /// Like Grid but all connections follow the polygon perimeter (no travel moves).
    /// On the final layer the entire outer perimeter is traced once to close the path.
    /// </summary>
    GhostMeshGrid,

    /// <summary>
    /// Lightning-style sparse support: thin two-wall "fingers" detour inward from
    /// the perimeter only where upper layers need material below them, then rejoin —
    /// the whole layer stays one continuous extrusion (no travels).
    /// UI label: Formbound Bridge.
    /// </summary>
    LightningBridge,

    /// <summary>
    /// Formbound Buttress: solid multi-bead ramps grown from few perimeter mouths
    /// (preferring interior anchors) that taper up under overhang demand. Continuous
    /// extrusion — mouth width follows bead width; interior fill is multi-bead dense.
    /// </summary>
    FormboundButtress,
}
