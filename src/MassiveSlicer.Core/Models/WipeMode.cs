namespace MassiveSlicer.Core.Models;

/// <summary>How the nozzle wipes filament before a travel move.</summary>
public enum WipeMode
{
    /// <summary>No wipe inserted.</summary>
    None,

    /// <summary>Retrace backward along the last extrusion segment.</summary>
    Retrace,

    /// <summary>Continue forward along the print direction past the travel start point.</summary>
    SameDirection,

    /// <summary>
    /// Leave the bead at 90° in the layer plane. Of the two sides, pick the one
    /// toward the closer of the previous bead and the next bead.
    /// </summary>
    Perpendicular,
}