using MassiveSlicer.Core.Models;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Rendering;

/// <summary>
/// Draws a sliced <see cref="Toolpath"/> as coloured line segments.
/// Extrude moves are drawn in a uniform accent colour; travel moves are gray.
/// Depth test is disabled so lines are never occluded by mesh geometry.
/// </summary>
public sealed class ToolpathRenderer : IDisposable
{
    private int  _vao, _vbo;
    private int  _vertexCount;
    private int  _ptVao, _ptVbo;
    private int  _pointCount;
    private bool _disposed;

    private readonly Shader _shader;

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
        uniform float uSelected;
        uniform vec3  uSelectColor;
        out vec4 fragColor;
        void main() {
            fragColor = vec4(uSelected > 0.5 ? uSelectColor : vColor, 1.0);
        }
        """;

    private static readonly Vector3 TravelColor  = new(0.18f, 0.42f, 0.18f);
    private static readonly Vector3 ExtrudeColor = new(0.1f, 0.45f, 0.9f);
    private static readonly Vector3 SeamStart    = new(1.0f, 0.9f, 0.0f);  // yellow
    private static readonly Vector3 SeamEnd      = new(1.0f, 0.2f, 0.1f);  // red

    public ToolpathRenderer(Toolpath toolpath, System.Numerics.Vector3 origin = default)
    {
        _shader = new Shader(VertSrc, FragSrc);
        Upload(toolpath, origin);
    }

    private void Upload(Toolpath toolpath, System.Numerics.Vector3 origin)
    {
        // Count total vertices (2 per move segment).
        int total = 0;
        foreach (var layer in toolpath.Layers)
            total += layer.Moves.Count * 2;

        if (total == 0) return;
        _vertexCount = total;

        // Pack interleaved [x,y,z, r,g,b].
        // Points are stored in local space (world - origin) so the node's LocalTransform
        // (set to Translation(origin)) correctly positions them back in world space.
        var data = new float[total * 6];
        int di = 0;

        foreach (var layer in toolpath.Layers)
        {
            foreach (var move in layer.Moves)
            {
                var c = move.Kind == MoveKind.Extrude ? ExtrudeColor : TravelColor;
                data[di++] = move.From.X - origin.X; data[di++] = move.From.Y - origin.Y; data[di++] = move.From.Z - origin.Z;
                data[di++] = c.X;                    data[di++] = c.Y;                    data[di++] = c.Z;
                data[di++] = move.To.X   - origin.X; data[di++] = move.To.Y   - origin.Y; data[di++] = move.To.Z   - origin.Z;
                data[di++] = c.X;                    data[di++] = c.Y;                    data[di++] = c.Z;
            }
        }

        _vao = GL.GenVertexArray();
        _vbo = GL.GenBuffer();
        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, data.Length * sizeof(float), data, BufferUsageHint.StaticDraw);

        int stride = 6 * sizeof(float);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));

        GL.BindVertexArray(0);

        // ── Seam points: start (yellow) and end (red) per contour ────────────
        // Count extrude-runs first so we can allocate exactly.
        int totalContours = 0;
        foreach (var layer in toolpath.Layers)
        {
            bool inEx = false;
            foreach (var m in layer.Moves)
            {
                if (m.Kind == MoveKind.Extrude && !inEx) { totalContours++; inEx = true; }
                else if (m.Kind == MoveKind.Travel)       inEx = false;
            }
        }

        var ptData = new float[totalContours * 2 * 6];
        int pi = 0;

        void WritePoint(System.Numerics.Vector3 p, Vector3 col)
        {
            ptData[pi++] = p.X - origin.X; ptData[pi++] = p.Y - origin.Y; ptData[pi++] = p.Z - origin.Z;
            ptData[pi++] = col.X;           ptData[pi++] = col.Y;           ptData[pi++] = col.Z;
        }

        foreach (var layer in toolpath.Layers)
        {
            bool inExtrude = false;
            System.Numerics.Vector3 extStart = default, extEnd = default;

            foreach (var move in layer.Moves)
            {
                if (move.Kind == MoveKind.Extrude)
                {
                    if (!inExtrude) { extStart = move.From; inExtrude = true; }
                    extEnd = move.To;
                }
                else
                {
                    if (inExtrude)
                    {
                        WritePoint(extStart, SeamStart);
                        WritePoint(extEnd,   SeamEnd);
                        inExtrude = false;
                    }
                }
            }
            if (inExtrude)
            {
                WritePoint(extStart, SeamStart);
                WritePoint(extEnd,   SeamEnd);
            }
        }

        _pointCount = pi / 6;
        if (_pointCount > 0)
        {
            _ptVao = GL.GenVertexArray();
            _ptVbo = GL.GenBuffer();
            GL.BindVertexArray(_ptVao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _ptVbo);
            GL.BufferData(BufferTarget.ArrayBuffer, pi * sizeof(float), ptData, BufferUsageHint.StaticDraw);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), 0);
            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), 3 * sizeof(float));
            GL.BindVertexArray(0);
        }
    }

    private static readonly Vector3 SelectColor = new(0.2f, 1.0f, 0.1f); // bright green

    public void Draw(Matrix4 mvp, bool selected = false)
    {
        if (_vertexCount == 0 || _disposed) return;

        _shader.Use();
        _shader.SetMatrix4("uMVP", ref mvp);
        _shader.SetFloat("uSelected", selected ? 1f : 0f);
        _shader.SetVector3("uSelectColor", SelectColor);
        GL.BindVertexArray(_vao);
        GL.DrawArrays(PrimitiveType.Lines, 0, _vertexCount);

        if (_pointCount > 0)
        {
            GL.PointSize(8f);
            _shader.SetFloat("uSelected", 0f); // always show true colors on seam points
            GL.BindVertexArray(_ptVao);
            GL.DrawArrays(PrimitiveType.Points, 0, _pointCount);
            GL.PointSize(1f);
        }

        GL.BindVertexArray(0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_vao   != 0) { GL.DeleteVertexArray(_vao);   GL.DeleteBuffer(_vbo);   }
        if (_ptVao != 0) { GL.DeleteVertexArray(_ptVao); GL.DeleteBuffer(_ptVbo); }
        _shader.Dispose();
    }

}
