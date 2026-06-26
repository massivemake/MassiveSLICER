namespace MassiveSlicer.Viewport.Scene;

/// <summary>Modo-style mesh cleanup toggles for imported triangle geometry.</summary>
public sealed class MeshCleanupOptions
{
    public bool RemoveFloatingVertices { get; set; } = true;
    public bool RemoveOnePointPolygons { get; set; } = true;
    public bool RemoveTwoPointPolygons { get; set; } = true;
    public bool FixDuplicatePointsInPolygons { get; set; } = true;
    public bool RemoveColinearVertices { get; set; } = true;
    public bool FixFaceNormalVectors { get; set; } = true;
    public bool MergeVertices { get; set; } = true;
    public bool UnifyPolygons { get; set; } = true;
    public bool ForceUnify { get; set; }
    public bool FixGaps { get; set; }

    /// <summary>Vertex weld distance in model units (mm).</summary>
    public float MergeEpsilon { get; set; } = 1e-4f;
}