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

    /// <summary>
    /// Unit cylinder along +Z: base disk in the XY plane at Z=0, top at Z=<paramref name="height"/>.
    /// Units match the caller. Spindle preview passes metres (baked tool mesh + flange ×1000).
    /// </summary>
    public static MeshData CreateCylinder(
        float  radius   = 5f,
        float  height   = 20f,
        int    segments = 24,
        string name     = "Cylinder",
        Vector4? baseColor = null)
    {
        // Floor is near-zero so a 1 mm stick-out in metre-baked tool space (0.001)
        // is not inflated to 50 mm. Degenerate 0 still becomes a sliver.
        radius   = Math.Max(radius, 1e-6f);
        height   = Math.Max(height, 1e-6f);
        segments = Math.Max(segments, 8);

        // Bottom ring, top ring, bottom-cap centre, top-cap centre.
        int ring = segments + 1;
        var positions = new Vector3[ring * 2 + 2];
        var normals   = new Vector3[positions.Length];

        for (int s = 0; s <= segments; s++)
        {
            float theta = 2f * MathF.PI * s / segments;
            float cx = MathF.Cos(theta), sy = MathF.Sin(theta);
            var radial = new Vector3(cx, sy, 0f);
            positions[s]        = new Vector3(cx * radius, sy * radius, 0f);
            normals  [s]        = radial;
            positions[ring + s] = new Vector3(cx * radius, sy * radius, height);
            normals  [ring + s] = radial;
        }

        int iBot = ring * 2;
        int iTop = iBot + 1;
        positions[iBot] = Vector3.Zero;
        normals  [iBot] = -Vector3.UnitZ;
        positions[iTop] = new Vector3(0f, 0f, height);
        normals  [iTop] =  Vector3.UnitZ;

        var indices = new uint[segments * 12];
        int k = 0;
        for (int s = 0; s < segments; s++)
        {
            uint a = (uint)s, b = (uint)(s + 1);
            uint c = (uint)(ring + s), d = (uint)(ring + s + 1);
            // side (outward)
            indices[k++] = a; indices[k++] = c; indices[k++] = b;
            indices[k++] = b; indices[k++] = c; indices[k++] = d;
            // bottom cap (normal -Z)
            indices[k++] = (uint)iBot; indices[k++] = b; indices[k++] = a;
            // top cap (normal +Z)
            indices[k++] = (uint)iTop; indices[k++] = c; indices[k++] = d;
        }

        var color = baseColor ?? new Vector4(0.24f, 0.80f, 0.06f, 1f);
        return new MeshData(positions, normals, indices, name, color, metallic: 0.15f, roughness: 0.45f);
    }
}
