using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Scene;

/// <summary>
/// CPU-side triangle mesh data ready for upload to the GPU.
/// Produced by <see cref="Loading.GltfLoader"/> and consumed by
/// <see cref="Rendering.MeshRenderer"/> on the GL thread.
/// </summary>
public sealed class MeshData
{
    /// <summary>Vertex positions in world space (Z-up, right-hand).</summary>
    public Vector3[] Positions { get; }

    /// <summary>Per-vertex normals, same length as <see cref="Positions"/>.</summary>
    public Vector3[] Normals { get; }

    /// <summary>Triangle indices, or <c>null</c> for non-indexed geometry.</summary>
    public uint[]? Indices { get; }

    /// <summary>Source name, used as the scene node label.</summary>
    public string Name { get; }

    /// <summary>Axis-aligned bounding box in local (model) space, computed at construction.</summary>
    public (Vector3 Min, Vector3 Max) LocalBounds { get; }

    /// <summary>
    /// PBR base colour extracted from the source file's material (RGBA, 0–1).
    /// Defaults to opaque white when no material data is present.
    /// </summary>
    public Vector4 BaseColor { get; }

    public MeshData(Vector3[] positions, Vector3[] normals, uint[]? indices,
                    string name = "Mesh", Vector4? baseColor = null)
    {
        Positions   = positions;
        Normals     = normals;
        Indices     = indices;
        Name        = name;
        BaseColor   = baseColor ?? Vector4.One;
        LocalBounds = ComputeBounds(positions);
    }

    private static (Vector3, Vector3) ComputeBounds(Vector3[] positions)
    {
        if (positions.Length == 0)
            return (Vector3.Zero, Vector3.Zero);

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var p in positions)
        {
            min = Vector3.ComponentMin(min, p);
            max = Vector3.ComponentMax(max, p);
        }
        return (min, max);
    }
}
