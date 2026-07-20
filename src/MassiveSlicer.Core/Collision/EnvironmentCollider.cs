using System.Numerics;

namespace MassiveSlicer.Core.Collision;

/// <summary>
/// Immutable snapshot of cell environment geometry (bed, stands, booster frame,
/// tool holders) as world-space triangles under a BVH. Triangles — not hulls —
/// deliberately: a convex hull of a frame would fill the robot's working window
/// with phantom solid; triangle leaves stay exact for concave environments.
/// </summary>
public sealed class EnvironmentCollider
{
    readonly Vector3[] _verts;      // flattened: tri i = verts[3i], [3i+1], [3i+2]
    readonly string[] _triSource;   // per-triangle source node name (hit reporting)
    readonly Bvh _bvh;

    public int TriangleCount => _triSource.Length;
    public Aabb Bounds => _bvh.Bounds;

    public EnvironmentCollider(Vector3[] flattenedTriVerts, string[] perTriSourceName)
    {
        if (flattenedTriVerts.Length != perTriSourceName.Length * 3)
            throw new ArgumentException("verts must be 3 per triangle");
        _verts = flattenedTriVerts;
        _triSource = perTriSourceName;

        var leaves = new Aabb[perTriSourceName.Length];
        for (int i = 0; i < leaves.Length; i++)
        {
            var a = _verts[3 * i];
            var b = _verts[3 * i + 1];
            var c = _verts[3 * i + 2];
            leaves[i] = new Aabb(
                Vector3.Min(a, Vector3.Min(b, c)),
                Vector3.Max(a, Vector3.Max(b, c)));
        }
        _bvh = new Bvh(leaves);
    }

    public TriangleSupport Triangle(int idx) => new(
        _verts[3 * idx], _verts[3 * idx + 1], _verts[3 * idx + 2]);

    public string SourceName(int idx) => _triSource[idx];

    /// <summary>Highest Z of the triangle — nozzle-plane gating for wrist/tool links.</summary>
    public float TriangleTopZ(int idx) => MathF.Max(
        _verts[3 * idx].Z, MathF.Max(_verts[3 * idx + 1].Z, _verts[3 * idx + 2].Z));

    /// <summary>Appends indices of triangles whose AABB overlaps <paramref name="box"/>.</summary>
    public void Query(in Aabb box, List<int> hits) => _bvh.Query(box, hits);
}
