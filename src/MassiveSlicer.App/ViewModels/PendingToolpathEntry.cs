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
    System.Numerics.Vector3 MaterialColor);

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
}