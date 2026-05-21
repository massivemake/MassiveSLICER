using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Scene;

/// <summary>Generates procedural CPU-side mesh data for common primitives.</summary>
public static class MeshFactory
{
    /// <summary>
    /// Creates a UV sphere centred at the origin in Z-up space.
    /// </summary>
    /// <param name="radius">Sphere radius in scene units (mm).</param>
    /// <param name="rings">Number of latitude bands (min 3).</param>
    /// <param name="segments">Number of longitude segments (min 3).</param>
    /// <param name="name">Label stored in the returned <see cref="MeshData"/>.</param>
    public static MeshData CreateSphere(
        float  radius   = 40f,
        int    rings    = 12,
        int    segments = 24,
        string name     = "Sphere")
    {
        rings    = Math.Max(rings,    3);
        segments = Math.Max(segments, 3);

        int vCount = (rings + 1) * (segments + 1);
        var positions = new Vector3[vCount];
        var normals   = new Vector3[vCount];

        for (int r = 0; r <= rings; r++)
        {
            float phi = MathF.PI * r / rings;           // 0 = north pole, PI = south pole
            float sp  = MathF.Sin(phi), cp = MathF.Cos(phi);

            for (int s = 0; s <= segments; s++)
            {
                float theta = 2f * MathF.PI * s / segments;
                var n = new Vector3(sp * MathF.Cos(theta), sp * MathF.Sin(theta), cp);
                int  idx = r * (segments + 1) + s;
                positions[idx] = n * radius;
                normals  [idx] = n;
            }
        }

        int   triCount = rings * segments * 6;
        var   indices  = new uint[triCount];
        int   k        = 0;

        for (int r = 0; r < rings; r++)
        {
            for (int s = 0; s < segments; s++)
            {
                uint a = (uint)( r      * (segments + 1) + s    );
                uint b = (uint)( r      * (segments + 1) + s + 1);
                uint c = (uint)((r + 1) * (segments + 1) + s    );
                uint d = (uint)((r + 1) * (segments + 1) + s + 1);
                indices[k++] = a; indices[k++] = c; indices[k++] = b;
                indices[k++] = b; indices[k++] = c; indices[k++] = d;
            }
        }

        return new MeshData(positions, normals, indices, name);
    }
}
