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
}
