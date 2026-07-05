namespace MassiveSlicer.App.Plasticity;

/// <summary>
/// A solid/sheet mesh received from the Plasticity server. Positions and normals are raw
/// triples (xyz) in Plasticity's units (metres); <see cref="Indices"/> are triangle triples.
/// The bridge converts these into engine mesh data (scaling metres→millimetres).
/// </summary>
internal sealed class PlasticityObject
{
    /// <summary>Stable Plasticity object id — the key used to add/update/delete the scene node.</summary>
    public int Id { get; init; }

    /// <summary>Document version this geometry belongs to (informational).</summary>
    public int Version { get; init; }

    /// <summary>Object name shown in Plasticity; used as the scene-node/outliner label.</summary>
    public string Name { get; init; } = "";

    public PlasticityObjectType Type { get; init; }

    /// <summary>Vertex positions, 3 floats per vertex.</summary>
    public float[] Positions { get; init; } = System.Array.Empty<float>();

    /// <summary>Triangle indices, 3 per face.</summary>
    public int[] Indices { get; init; } = System.Array.Empty<int>();

    /// <summary>Vertex normals, 3 floats per vertex (may be empty — recomputed if absent).</summary>
    public float[] Normals { get; init; } = System.Array.Empty<float>();
}
