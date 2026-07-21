using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Scene;

/// <summary>Merges several meshes' geometry into one flat MeshData (re-based indices, no
/// welding — <see cref="MeshIslands"/> handles connectivity afterward if that matters).</summary>
public static class MeshConcat
{
    public static MeshData Concat(IReadOnlyList<MeshData> meshes)
    {
        var pos = new List<Vector3>();
        var nrm = new List<Vector3>();
        var idx = new List<uint>();
        foreach (var m in meshes)
        {
            uint baseI = (uint)pos.Count;
            pos.AddRange(m.Positions);
            nrm.AddRange(m.Normals);
            int triCount = m.Indices is { Length: > 0 } mi ? mi.Length / 3 : m.Positions.Length / 3;
            for (int t = 0; t < triCount * 3; t++)
                idx.Add(baseI + (m.Indices is { } mi2 ? mi2[t] : (uint)t));
        }
        var first = meshes[0];
        return new MeshData(pos.ToArray(), nrm.ToArray(), idx.ToArray(), first.Name, first.BaseColor, first.Metallic, first.Roughness);
    }
}
