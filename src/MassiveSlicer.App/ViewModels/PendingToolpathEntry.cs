using MassiveSlicer.Core.Models;
using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;

namespace MassiveSlicer.ViewModels;

/// <summary>Live toolpath data for workspace export.</summary>
public sealed record ToolpathSnapshot(
    Toolpath Smoothed,
    Toolpath Raw,
    float BeadWidth,
    float LayerHeight,
    System.Numerics.Vector3 MaterialColor,
    // Cylinder-projection mills store the cutter at the station. These three say how that
    // station is drawn onto the part right now (null / NaN = the moves are already where
    // they are drawn). The cut plane uses them so a new cut sits on the line, not the table.
    System.Numerics.Vector3? RotaryCenter = null,
    float RotarySign = float.NaN,
    Matrix4? RotarySpin = null);

/// <summary>Queued toolpath upload for the GL render thread.</summary>
public sealed class PendingToolpathEntry
{
    public required Toolpath Toolpath { get; init; }
    public required Toolpath RawToolpath { get; init; }
    public required SceneNode Node { get; init; }
    public float BeadWidth { get; init; } = 6f;
    public float LayerHeight { get; init; } = 3f;
    public System.Numerics.Vector3 MaterialColor { get; init; }

    /// <summary>When set (workspace restore), applied after centroid upload.</summary>
    public Matrix4? LocalTransformOverride { get; init; }

    /// <summary>
    /// When set (Update Slice), the user's pre-replace pose is preserved relative to
    /// <see cref="PreservedOrigin"/> instead of resetting to centroid-only.
    /// </summary>
    public bool PreserveRelativePose { get; init; }

    /// <summary>Toolpath <see cref="SceneNode.LocalTransform"/> captured before re-slice.</summary>
    public Matrix4? PreservedLocalTransform { get; init; }

    /// <summary>Geometry centroid stored at last upload; gizmo edits do not update this.</summary>
    public System.Numerics.Vector3? PreservedOrigin { get; init; }

    /// <summary>
    /// Set for a genuine RE-SLICE, where the incoming toolpath was regenerated from the mesh's
    /// current world vertices and is therefore already in the right place — so the node belongs at
    /// the fresh geometry's own centroid, not at whatever translation it happened to carry before.
    /// </summary>
    /// <remarks>
    /// <see cref="PreserveRelativePose"/> alone restores the pre-replace translation absolutely.
    /// That is right for a restore (workspace load, undo of a delete) where the geometry is
    /// unchanged, and right for a MOVE — the drag-link already shifted the node by the same amount
    /// the new centroid shifted, so the two agree. It is wrong for anything that changes the
    /// geometry's shape, because the centroid moves and the node does not follow: a scale leaves
    /// the drawn path parked on the old centroid while the path itself was rebuilt around a new
    /// one. Measured on the running app — 50% scale put the toolpath 112mm off its mesh, 25% put
    /// it 179mm off, and it grew every time the part was resized again.
    /// </remarks>
    public bool RebaseToFreshCentroid { get; init; }

    /// <summary>
    /// Table angle when a rotary mill path was raycast. Draw spin is live E1 minus this
    /// so the line stays on the mesh. Null uses the E1 at GPU upload.
    /// </summary>
    public float? RotaryE1AtGenerate { get; init; }

    /// <summary>World centre used to unwind E1 hits. The draw rewind must use this same point.</summary>
    public System.Numerics.Vector3? RotaryCenter { get; init; }

    /// <summary>Rotary pivot world matrix at the raycast. Later frames map the path with this.</summary>
    public Matrix4? RotaryWorldAtGenerate { get; init; }
}