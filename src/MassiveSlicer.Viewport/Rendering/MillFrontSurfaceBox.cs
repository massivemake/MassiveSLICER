using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Rendering;

/// <summary>
/// Box / lasso mill paint that only marks the <b>front surface</b>:
/// front-facing triangles (normal toward the camera) and, among those, the
/// nearest depth at each screen cell so the far side of a solid is not painted
/// through the part.
/// </summary>
public static class MillFrontSurfaceBox
{
    public const float CellPx = 4f;
    public const float DepthSlack = 1.035f;

    public static void CreateDepthBuffer(float vpW, float vpH, out float[] zmin, out int gw, out int gh)
    {
        gw = Math.Max(1, (int)MathF.Ceiling(vpW / CellPx));
        gh = Math.Max(1, (int)MathF.Ceiling(vpH / CellPx));
        zmin = new float[gw * gh];
        Array.Fill(zmin, float.MaxValue);
    }

    public static void AccumulateDepth(
        MeshData mesh,
        Matrix4 world,
        Vector3 cameraEye,
        Func<Vector3, Vector3> projectToScreenDepth,
        Func<float, float, bool> inside,
        float[] zmin, int gw, int gh)
    {
        int tris = Picker.TriangleCount(mesh);
        for (int t = 0; t < tris; t++)
        {
            if (!TryFrontTriangle(mesh, world, cameraEye, t, out var w0, out var w1, out var w2))
                continue;
            WriteProjected(projectToScreenDepth(w0), inside, zmin, gw, gh);
            WriteProjected(projectToScreenDepth(w1), inside, zmin, gw, gh);
            WriteProjected(projectToScreenDepth(w2), inside, zmin, gw, gh);
            WriteProjected(projectToScreenDepth((w0 + w1 + w2) / 3f), inside, zmin, gw, gh);
        }
    }

    public static void CollectVisibleVerts(
        MeshData mesh,
        Matrix4 world,
        Vector3 cameraEye,
        Func<Vector3, Vector3> projectToScreenDepth,
        Func<float, float, bool> inside,
        float[] zmin, int gw, int gh,
        HashSet<int> into)
    {
        int tris = Picker.TriangleCount(mesh);
        for (int t = 0; t < tris; t++)
        {
            if (!TryFrontTriangle(mesh, world, cameraEye, t,
                    out var w0, out var w1, out var w2, out int i0, out int i1, out int i2))
                continue;
            AcceptVert(i0, w0, projectToScreenDepth, inside, zmin, gw, gh, into);
            AcceptVert(i1, w1, projectToScreenDepth, inside, zmin, gw, gh, into);
            AcceptVert(i2, w2, projectToScreenDepth, inside, zmin, gw, gh, into);
        }
    }

    public static bool IsFrontFacing(Vector3 w0, Vector3 w1, Vector3 w2, Vector3 cameraEye)
    {
        var n = Vector3.Cross(w1 - w0, w2 - w0);
        if (n.LengthSquared < 1e-18f) return false;
        var centroid = (w0 + w1 + w2) / 3f;
        return Vector3.Dot(n, cameraEye - centroid) > 0f;
    }

    static bool TryFrontTriangle(
        MeshData mesh, Matrix4 world, Vector3 cameraEye, int t,
        out Vector3 w0, out Vector3 w1, out Vector3 w2)
        => TryFrontTriangle(mesh, world, cameraEye, t, out w0, out w1, out w2, out _, out _, out _);

    static bool TryFrontTriangle(
        MeshData mesh, Matrix4 world, Vector3 cameraEye, int t,
        out Vector3 w0, out Vector3 w1, out Vector3 w2,
        out int i0, out int i1, out int i2)
    {
        w0 = w1 = w2 = default;
        i0 = i1 = i2 = -1;
        if (!TryTriIndices(mesh, t, out i0, out i1, out i2))
            return false;
        w0 = Vector3.TransformPosition(mesh.Positions[i0], world);
        w1 = Vector3.TransformPosition(mesh.Positions[i1], world);
        w2 = Vector3.TransformPosition(mesh.Positions[i2], world);
        return IsFrontFacing(w0, w1, w2, cameraEye);
    }

    static void WriteProjected(Vector3 scr, Func<float, float, bool> inside, float[] zmin, int gw, int gh)
    {
        if (float.IsNaN(scr.X) || !inside(scr.X, scr.Y)) return;
        int cx = (int)(scr.X / CellPx);
        int cy = (int)(scr.Y / CellPx);
        if ((uint)cx >= (uint)gw || (uint)cy >= (uint)gh) return;
        int i = cy * gw + cx;
        if (scr.Z < zmin[i]) zmin[i] = scr.Z;
    }

    static void AcceptVert(
        int index, Vector3 world,
        Func<Vector3, Vector3> project,
        Func<float, float, bool> inside,
        float[] zmin, int gw, int gh,
        HashSet<int> into)
    {
        var scr = project(world);
        if (float.IsNaN(scr.X) || !inside(scr.X, scr.Y)) return;
        int cx = (int)(scr.X / CellPx);
        int cy = (int)(scr.Y / CellPx);
        if ((uint)cx >= (uint)gw || (uint)cy >= (uint)gh) return;
        float front = zmin[cy * gw + cx];
        if (front >= float.MaxValue * 0.5f) return;
        if (scr.Z > front * DepthSlack) return;
        into.Add(index);
    }

    static bool TryTriIndices(MeshData mesh, int t, out int i0, out int i1, out int i2)
    {
        i0 = i1 = i2 = 0;
        if (mesh.Indices is { } idx)
        {
            int i = t * 3;
            if (i + 2 >= idx.Length) return false;
            i0 = (int)idx[i]; i1 = (int)idx[i + 1]; i2 = (int)idx[i + 2];
            return true;
        }
        i0 = t * 3; i1 = i0 + 1; i2 = i0 + 2;
        return i2 < mesh.Positions.Length;
    }
}
