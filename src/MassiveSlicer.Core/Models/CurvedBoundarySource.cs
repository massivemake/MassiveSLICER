namespace MassiveSlicer.Core.Models;

/// <summary>How LOW/HIGH curved-slicing boundaries are supplied.</summary>
public enum CurvedBoundarySource
{
    /// <summary>Infer bottom and top Z vertex rings on the welded mesh.</summary>
    AutoDetect,
    /// <summary>User-picked edge loops in the viewport.</summary>
    ViewportPick,
    /// <summary>Vertex index arrays loaded from JSON (compas_slicer format).</summary>
    JsonImport
}