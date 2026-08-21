using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Rendering;

/// <summary>
/// Front-surface mill paint: a screen-space min-Z buffer so a near skin occludes
/// the far wall of a box/lasso region.
/// </summary>
public static class MillFrontSurfaceBox
{
    public static void CreateDepthBuffer(float vpW, float vpH, out float[] zmin, out int gw, out int gh)
    {
        gw = Math.Max(1, (int)MathF.Ceiling(vpW));
        gh = Math.Max(1, (int)MathF.Ceiling(vpH));
        zmin = new float[gw * gh];
        Array.Fill(zmin, float.PositiveInfinity);
    }

    public static void AccumulateDepth(
        MeshData mesh,
        Matrix4 world,
        Vector3 eye,
        Func<Vector3, Vector3> project,
        Func<float, float, bool> inside,
        float[] zmin,
        int gw,
        int gh)
    {
        var pos = mesh.Positions;
        for (int i = 0; i < pos.Length; i++)
        {
            var w = Vector3.TransformPosition(pos[i], world);
            var s = project(w);
            if (!inside(s.X, s.Y)) continue;
            if (s.X < 0 || s.Y < 0 || s.X >= gw || s.Y >= gh) continue;
            int px = (int)s.X, py = (int)s.Y;
            float z = (w - eye).Length;
            int idx = py * gw + px;
            if (z < zmin[idx]) zmin[idx] = z;
        }
    }

    public static void CollectVisibleVerts(
        MeshData mesh,
        Matrix4 world,
        Vector3 eye,
        Func<Vector3, Vector3> project,
        Func<float, float, bool> inside,
        float[] zmin,
        int gw,
        int gh,
        HashSet<int> hits)
    {
        const float slop = 0.75f;
        var pos = mesh.Positions;
        for (int i = 0; i < pos.Length; i++)
        {
            var w = Vector3.TransformPosition(pos[i], world);
            var s = project(w);
            if (!inside(s.X, s.Y)) continue;
            if (s.X < 0 || s.Y < 0 || s.X >= gw || s.Y >= gh) continue;
            int px = (int)s.X, py = (int)s.Y;
            float z = (w - eye).Length;
            if (z <= zmin[py * gw + px] + slop)
                hits.Add(i);
        }
    }
}
