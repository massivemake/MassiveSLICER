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
}