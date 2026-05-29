using MassiveSlicer.Viewport.Scene;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Rendering;

/// <summary>
/// Renders translate, scale, and rotate gizmos.
/// Caller clears depth before Draw so the gizmo always appears on top.
/// </summary>
public sealed class GizmoRenderer : IDisposable
{
    private readonly Shader _shader;

    // Translate
    private int _transLinesVao, _transLinesVbo, _transLineCount;
    private int _transConesVao, _transConesVbo, _transConeCount;

    // Scale
    private int _scaleShaftsVao, _scaleShaftsVbo, _scaleShaftCount;
    private int _scaleBoxesVao,  _scaleBoxesVbo,  _scaleBoxCount;
    private int _scaleCenterVao, _scaleCenterVbo, _scaleCenterCount;

    // Rotate rings (normal + highlight)
    private int _rotateVao, _rotateVbo, _rotateCountPerRing;
    private int _rotateHlVao, _rotateHlVbo; // bright versions for active ring

    // Axis highlight (shared across modes)
    private int _axisHighlightVao, _axisHighlightVbo;

    private bool _disposed;

    private const float ShaftEnd = 0.80f;
    private const float TipEnd   = 1.00f;
    private const float ConeR    = 0.08f;
    private const int   ConeSegs = 8;
    private const float BoxHalf  = 0.06f;  // half-size of scale box tip
    private const float RingR    = 0.90f;  // rotate ring radius (< 1 so tips are clear)
    private const int   RingSegs = 48;

    private static readonly Vector3 ColX   = new(1.00f, 0.20f, 0.20f);
    private static readonly Vector3 ColY   = new(0.20f, 1.00f, 0.20f);
    private static readonly Vector3 ColZ   = new(0.30f, 0.50f, 1.00f);
    private static readonly Vector3 ColXHl = new(1.00f, 0.70f, 0.70f); // bright highlight colours
    private static readonly Vector3 ColYHl = new(0.70f, 1.00f, 0.70f);
    private static readonly Vector3 ColZHl = new(0.70f, 0.88f, 1.00f);

    private static readonly string VertSrc = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aColor;
        uniform mat4 uMVP;
        out vec3 vColor;
        void main() {
            gl_Position = vec4(aPos, 1.0) * uMVP;
            vColor = aColor;
        }
        """;

    private static readonly string FragSrc = """
        #version 330 core
        in vec3 vColor;
        out vec4 fragColor;
        void main() { fragColor = vec4(vColor, 1.0); }
        """;

    public GizmoRenderer()
    {
        _shader = new Shader(VertSrc, FragSrc);
        BuildTranslate();
        BuildScale();
        BuildRotate();
        BuildAxisHighlight();
    }

    // -- Draw -----------------------------------------------------------------

    public void Draw(Vector3 worldPos, float scale, Matrix4 viewProj, GizmoMode mode)
    {
        var mvp = Matrix4.CreateScale(scale)
                * Matrix4.CreateTranslation(worldPos.X, worldPos.Y, worldPos.Z)
                * viewProj;

        _shader.Use();
        _shader.SetMatrix4("uMVP", ref mvp);

        switch (mode)
        {
            case GizmoMode.Translate:
                GL.BindVertexArray(_transLinesVao);
                GL.DrawArrays(PrimitiveType.Lines, 0, _transLineCount);
                GL.BindVertexArray(_transConesVao);
                GL.DrawArrays(PrimitiveType.Triangles, 0, _transConeCount);
                break;

            case GizmoMode.Scale:
                GL.BindVertexArray(_scaleShaftsVao);
                GL.DrawArrays(PrimitiveType.Lines, 0, _scaleShaftCount);
                GL.BindVertexArray(_scaleBoxesVao);
                GL.DrawArrays(PrimitiveType.Triangles, 0, _scaleBoxCount);
                GL.BindVertexArray(_scaleCenterVao);
                GL.DrawArrays(PrimitiveType.Triangles, 0, _scaleCenterCount);
                break;

            case GizmoMode.Rotate:
                GL.LineWidth(2.0f);
                GL.BindVertexArray(_rotateVao);
                GL.DrawArrays(PrimitiveType.Lines, 0, _rotateCountPerRing * 3);
                GL.LineWidth(1f);
                break;
        }

        GL.BindVertexArray(0);
    }

    public void DrawAxisHighlight(Vector3 worldPos, GizmoAxis axis, float lineLength, Matrix4 viewProj)
    {
        if (axis == GizmoAxis.None || axis == GizmoAxis.All) return;

        var rotation = axis switch
        {
            GizmoAxis.Y => Matrix4.CreateRotationZ(MathF.PI / 2f),
            GizmoAxis.Z => Matrix4.CreateRotationY(MathF.PI / 2f),
            _           => Matrix4.Identity,
        };

        var mvp = Matrix4.CreateScale(lineLength)
                * rotation
                * Matrix4.CreateTranslation(worldPos.X, worldPos.Y, worldPos.Z)
                * viewProj;

        int startVertex = axis switch
        {
            GizmoAxis.X => 0,
            GizmoAxis.Y => 2,
            _           => 4,
        };

        _shader.Use();
        _shader.SetMatrix4("uMVP", ref mvp);

        GL.LineWidth(2.5f);
        GL.BindVertexArray(_axisHighlightVao);
        GL.DrawArrays(PrimitiveType.Lines, startVertex, 2);
        GL.BindVertexArray(0);
        GL.LineWidth(1f);
    }

    /// <summary>
    /// Redraws only the active ring in a brighter colour and thicker line width.
    /// Call after <see cref="Draw"/> so the highlight renders on top.
    /// </summary>
    public void DrawRingHighlight(Vector3 worldPos, GizmoAxis axis, float scale, Matrix4 viewProj)
    {
        if (axis == GizmoAxis.None) return;

        var mvp = Matrix4.CreateScale(scale)
                * Matrix4.CreateTranslation(worldPos.X, worldPos.Y, worldPos.Z)
                * viewProj;

        int startVertex = axis switch
        {
            GizmoAxis.X => 0,
            GizmoAxis.Y => _rotateCountPerRing,
            _           => _rotateCountPerRing * 2,
        };

        _shader.Use();
        _shader.SetMatrix4("uMVP", ref mvp);

        GL.LineWidth(3.5f);
        GL.BindVertexArray(_rotateHlVao);
        GL.DrawArrays(PrimitiveType.Lines, startVertex, _rotateCountPerRing);
        GL.BindVertexArray(0);
        GL.LineWidth(1f);
    }

    // -- Hit testing ----------------------------------------------------------

    /// <summary>Translate or scale gizmo: segment-based hit test on the three axis shafts.</summary>
    public static GizmoAxis HitTestAxes(
        Vector3 worldPos, float scale,
        float mx, float my, float vpW, float vpH,
        Matrix4 viewProj, GizmoMode mode = GizmoMode.Translate)
    {
        const float Threshold = 10f;

        var origin = ToScreen(worldPos,                                    viewProj, vpW, vpH);
        var tipX   = ToScreen(worldPos + new Vector3(scale, 0f,    0f),    viewProj, vpW, vpH);
        var tipY   = ToScreen(worldPos + new Vector3(0f,    scale, 0f),    viewProj, vpW, vpH);
        var tipZ   = ToScreen(worldPos + new Vector3(0f,    0f,    scale), viewProj, vpW, vpH);

        var   mouse = new Vector2(mx, my);

        if (mode == GizmoMode.Scale && (mouse - origin).Length < 10f)
            return GizmoAxis.All;

        float dX    = SegDist(mouse, origin, tipX);
        float dY    = SegDist(mouse, origin, tipY);
        float dZ    = SegDist(mouse, origin, tipZ);

        float minD = MathF.Min(dX, MathF.Min(dY, dZ));
        if (minD > Threshold) return GizmoAxis.None;
        if (minD == dX) return GizmoAxis.X;
        if (minD == dY) return GizmoAxis.Y;
        return GizmoAxis.Z;
    }

    /// <summary>Rotate gizmo: annular band hit test on the three screen-projected rings.</summary>
    public static GizmoAxis HitTestRings(
        Vector3 worldPos, float scale,
        float mx, float my, float vpW, float vpH,
        Matrix4 viewProj)
    {
        const float Threshold = 8f;

        var mouse = new Vector2(mx, my);

        float BestDist(Vector3 axis1, Vector3 axis2)
        {
            float best = float.MaxValue;
            for (int i = 0; i < RingSegs; i++)
            {
                float a0 = i       * MathF.Tau / RingSegs;
                float a1 = (i + 1) * MathF.Tau / RingSegs;
                var p0 = ToScreen(worldPos + (axis1 * (RingR * MathF.Cos(a0)) + axis2 * (RingR * MathF.Sin(a0))) * scale, viewProj, vpW, vpH);
                var p1 = ToScreen(worldPos + (axis1 * (RingR * MathF.Cos(a1)) + axis2 * (RingR * MathF.Sin(a1))) * scale, viewProj, vpW, vpH);
                best = MathF.Min(best, SegDist(mouse, p0, p1));
            }
            return best;
        }

        float dX = BestDist(Vector3.UnitY, Vector3.UnitZ); // X ring: in YZ plane
        float dY = BestDist(Vector3.UnitX, Vector3.UnitZ); // Y ring: in XZ plane
        float dZ = BestDist(Vector3.UnitX, Vector3.UnitY); // Z ring: in XY plane

        float minD = MathF.Min(dX, MathF.Min(dY, dZ));
        if (minD > Threshold) return GizmoAxis.None;
        if (minD == dX) return GizmoAxis.X;
        if (minD == dY) return GizmoAxis.Y;
        return GizmoAxis.Z;
    }

    // -- Dispose --------------------------------------------------------------

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shader.Dispose();
        GL.DeleteVertexArray(_transLinesVao);  GL.DeleteBuffer(_transLinesVbo);
        GL.DeleteVertexArray(_transConesVao);  GL.DeleteBuffer(_transConesVbo);
        GL.DeleteVertexArray(_scaleShaftsVao);  GL.DeleteBuffer(_scaleShaftsVbo);
        GL.DeleteVertexArray(_scaleBoxesVao);   GL.DeleteBuffer(_scaleBoxesVbo);
        GL.DeleteVertexArray(_scaleCenterVao);  GL.DeleteBuffer(_scaleCenterVbo);
        GL.DeleteVertexArray(_rotateVao);      GL.DeleteBuffer(_rotateVbo);
        GL.DeleteVertexArray(_rotateHlVao);    GL.DeleteBuffer(_rotateHlVbo);
        GL.DeleteVertexArray(_axisHighlightVao); GL.DeleteBuffer(_axisHighlightVbo);
    }

    // -- Geometry construction -------------------------------------------------

    private void BuildTranslate()
    {
        var lines = new List<float>();
        var cones = new List<float>();
        GenTranslateAxis(lines, cones, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, ColX);
        GenTranslateAxis(lines, cones, Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ, ColY);
        GenTranslateAxis(lines, cones, Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY, ColZ);
        _transLineCount = lines.Count / 6;
        _transConeCount = cones.Count / 6;
        Upload(out _transLinesVao, out _transLinesVbo, lines.ToArray());
        Upload(out _transConesVao, out _transConesVbo, cones.ToArray());
    }

    private static void GenTranslateAxis(
        List<float> lines, List<float> cones,
        Vector3 dir, Vector3 perp1, Vector3 perp2, Vector3 col)
    {
        Vert(lines, Vector3.Zero,   col);
        Vert(lines, dir * ShaftEnd, col);

        var tip  = dir * TipEnd;
        var ring = new Vector3[ConeSegs];
        for (int i = 0; i < ConeSegs; i++)
        {
            float a = i * MathF.Tau / ConeSegs;
            ring[i] = dir * ShaftEnd + perp1 * (ConeR * MathF.Cos(a)) + perp2 * (ConeR * MathF.Sin(a));
        }
        for (int i = 0; i < ConeSegs; i++)
        {
            Vert(cones, tip,                       col);
            Vert(cones, ring[i],                   col);
            Vert(cones, ring[(i + 1) % ConeSegs],  col);
        }
    }

    private void BuildScale()
    {
        var shafts = new List<float>();
        var boxes  = new List<float>();
        GenScaleAxis(shafts, boxes, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, ColX);
        GenScaleAxis(shafts, boxes, Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ, ColY);
        GenScaleAxis(shafts, boxes, Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY, ColZ);
        _scaleShaftCount = shafts.Count / 6;
        _scaleBoxCount   = boxes.Count  / 6;
        Upload(out _scaleShaftsVao, out _scaleShaftsVbo, shafts.ToArray());
        Upload(out _scaleBoxesVao,  out _scaleBoxesVbo,  boxes.ToArray());

        var center = new List<float>();
        GenCenterBox(center);
        _scaleCenterCount = center.Count / 6;
        Upload(out _scaleCenterVao, out _scaleCenterVbo, center.ToArray());
    }

    private static void GenCenterBox(List<float> verts)
    {
        var   col = Vector3.One;
        float h   = BoxHalf * 1.2f;
        Vector3[] corners =
        [
            new( h,  h,  h), new( h,  h, -h),
            new( h, -h,  h), new( h, -h, -h),
            new(-h,  h,  h), new(-h,  h, -h),
            new(-h, -h,  h), new(-h, -h, -h),
        ];
        int[][] faces =
        [
            [0,1,3],[0,3,2],
            [4,6,7],[4,7,5],
            [0,2,6],[0,6,4],
            [1,5,7],[1,7,3],
            [0,4,5],[0,5,1],
            [2,3,7],[2,7,6],
        ];
        foreach (var f in faces)
        {
            Vert(verts, corners[f[0]], col);
            Vert(verts, corners[f[1]], col);
            Vert(verts, corners[f[2]], col);
        }
    }

    private static void GenScaleAxis(
        List<float> shafts, List<float> boxes,
        Vector3 dir, Vector3 perp1, Vector3 perp2, Vector3 col)
    {
        Vert(shafts, Vector3.Zero,   col);
        Vert(shafts, dir * ShaftEnd, col);

        // Cube centred at TipEnd along dir.
        var c = dir * TipEnd;
        var h = BoxHalf;
        // 8 box corners in (dir, perp1, perp2) space
        Vector3[] corners =
        [
            c + dir*h + perp1*h + perp2*h,
            c + dir*h + perp1*h - perp2*h,
            c + dir*h - perp1*h + perp2*h,
            c + dir*h - perp1*h - perp2*h,
            c - dir*h + perp1*h + perp2*h,
            c - dir*h + perp1*h - perp2*h,
            c - dir*h - perp1*h + perp2*h,
            c - dir*h - perp1*h - perp2*h,
        ];
        // 12 triangles (2 per face × 6 faces)
        int[][] faces =
        [
            [0,1,3],[0,3,2], // +dir
            [4,6,7],[4,7,5], // -dir
            [0,2,6],[0,6,4], // +perp1
            [1,5,7],[1,7,3], // -perp1
            [0,4,5],[0,5,1], // +perp2
            [2,3,7],[2,7,6], // -perp2
        ];
        foreach (var f in faces)
        {
            Vert(boxes, corners[f[0]], col);
            Vert(boxes, corners[f[1]], col);
            Vert(boxes, corners[f[2]], col);
        }
    }

    private void BuildRotate()
    {
        var verts = new List<float>();
        GenRing(verts, Vector3.UnitY, Vector3.UnitZ, ColX);
        GenRing(verts, Vector3.UnitX, Vector3.UnitZ, ColY);
        GenRing(verts, Vector3.UnitX, Vector3.UnitY, ColZ);
        _rotateCountPerRing = RingSegs * 2;
        Upload(out _rotateVao, out _rotateVbo, verts.ToArray());

        var hlVerts = new List<float>();
        GenRing(hlVerts, Vector3.UnitY, Vector3.UnitZ, ColXHl);
        GenRing(hlVerts, Vector3.UnitX, Vector3.UnitZ, ColYHl);
        GenRing(hlVerts, Vector3.UnitX, Vector3.UnitY, ColZHl);
        Upload(out _rotateHlVao, out _rotateHlVbo, hlVerts.ToArray());
    }

    private static void GenRing(List<float> verts, Vector3 axis1, Vector3 axis2, Vector3 col)
    {
        for (int i = 0; i < RingSegs; i++)
        {
            float a0 = i       * MathF.Tau / RingSegs;
            float a1 = (i + 1) * MathF.Tau / RingSegs;
            Vert(verts, axis1 * (RingR * MathF.Cos(a0)) + axis2 * (RingR * MathF.Sin(a0)), col);
            Vert(verts, axis1 * (RingR * MathF.Cos(a1)) + axis2 * (RingR * MathF.Sin(a1)), col);
        }
    }

    private void BuildAxisHighlight()
    {
        var hlX = new Vector3(1.00f, 0.60f, 0.60f);
        var hlY = new Vector3(0.60f, 1.00f, 0.60f);
        var hlZ = new Vector3(0.60f, 0.72f, 1.00f);
        float[] data =
        [
            -1f, 0f, 0f, hlX.X, hlX.Y, hlX.Z,
             1f, 0f, 0f, hlX.X, hlX.Y, hlX.Z,
            -1f, 0f, 0f, hlY.X, hlY.Y, hlY.Z,
             1f, 0f, 0f, hlY.X, hlY.Y, hlY.Z,
            -1f, 0f, 0f, hlZ.X, hlZ.Y, hlZ.Z,
             1f, 0f, 0f, hlZ.X, hlZ.Y, hlZ.Z,
        ];
        Upload(out _axisHighlightVao, out _axisHighlightVbo, data);
    }

    private static void Vert(List<float> buf, Vector3 pos, Vector3 col)
    {
        buf.Add(pos.X); buf.Add(pos.Y); buf.Add(pos.Z);
        buf.Add(col.X); buf.Add(col.Y); buf.Add(col.Z);
    }

    private static void Upload(out int vao, out int vbo, float[] data)
    {
        vao = GL.GenVertexArray();
        vbo = GL.GenBuffer();
        GL.BindVertexArray(vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, data.Length * sizeof(float), data, BufferUsageHint.StaticDraw);
        int stride = 6 * sizeof(float);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
        GL.EnableVertexAttribArray(1);
        GL.BindVertexArray(0);
    }

    // -- Screen-space utilities ------------------------------------------------

    private static Vector2 ToScreen(Vector3 world, Matrix4 vp, float vpW, float vpH)
    {
        float cx = world.X * vp.M11 + world.Y * vp.M21 + world.Z * vp.M31 + vp.M41;
        float cy = world.X * vp.M12 + world.Y * vp.M22 + world.Z * vp.M32 + vp.M42;
        float cw = world.X * vp.M14 + world.Y * vp.M24 + world.Z * vp.M34 + vp.M44;
        if (MathF.Abs(cw) < 1e-6f) return new Vector2(-9999f);
        float nx = cx / cw;
        float ny = cy / cw;
        return new Vector2((nx + 1f) * 0.5f * vpW, (1f - ny) * 0.5f * vpH);
    }

    private static float SegDist(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab   = b - a;
        float l2 = ab.LengthSquared;
        if (l2 < 1e-6f) return (p - a).Length;
        float t = Math.Clamp(Vector2.Dot(p - a, ab) / l2, 0f, 1f);
        return (p - (a + ab * t)).Length;
    }
}
