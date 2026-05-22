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
        out vec4 fragColor;
        void main() { fragColor = vec4(vColor, 1.0); }
        """;

    private static readonly Vector3 TravelColor  = new(0.35f, 0.35f, 0.35f);
    private static readonly Vector3 ExtrudeColor = new(0.2f, 0.9f, 1.0f);

    public ToolpathRenderer(Toolpath toolpath)
    {
        _shader = new Shader(VertSrc, FragSrc);
        Upload(toolpath);
    }

    private void Upload(Toolpath toolpath)
    {
        // Count total vertices (2 per move segment).
        int total = 0;
        foreach (var layer in toolpath.Layers)
            total += layer.Moves.Count * 2;

        if (total == 0) return;
        _vertexCount = total;

        // Pack interleaved [x,y,z, r,g,b].
        var data = new float[total * 6];
        int di = 0;

        foreach (var layer in toolpath.Layers)
        {
            foreach (var move in layer.Moves)
            {
                var c = move.Kind == MoveKind.Extrude ? ExtrudeColor : TravelColor;
                data[di++] = move.From.X; data[di++] = move.From.Y; data[di++] = move.From.Z;
                data[di++] = c.X;         data[di++] = c.Y;         data[di++] = c.Z;
                data[di++] = move.To.X;   data[di++] = move.To.Y;   data[di++] = move.To.Z;
                data[di++] = c.X;         data[di++] = c.Y;         data[di++] = c.Z;
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
    }

    public void Draw(Matrix4 mvp)
    {
        if (_vertexCount == 0 || _disposed) return;

        GL.Disable(EnableCap.DepthTest);
        _shader.Use();
        _shader.SetMatrix4("uMVP", ref mvp);
        GL.BindVertexArray(_vao);
        GL.DrawArrays(PrimitiveType.Lines, 0, _vertexCount);
        GL.BindVertexArray(0);
        GL.Enable(EnableCap.DepthTest);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_vao != 0) { GL.DeleteVertexArray(_vao); GL.DeleteBuffer(_vbo); }
        _shader.Dispose();
    }

}
