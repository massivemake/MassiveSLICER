using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Rendering;

/// <summary>
/// Draws a per-vertex-coloured polyline — the Multi-Planar "spine" through the
/// centre of every slicing plane, coloured by how much the plane rotation
/// distorts that layer (green = uniform, red = near self-crossing or gap limit).
/// </summary>
public sealed class SpineRenderer : IDisposable
{
    private int _vao, _vbo, _count;
    private bool _disposed;
    private PrimitiveType _primitive = PrimitiveType.LineStrip;

    private readonly Shader _shader;

    private static readonly string VertSrc = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aCol;
        uniform mat4 uMVP;
        out vec3 vCol;
        void main() { vCol = aCol; gl_Position = vec4(aPos, 1.0) * uMVP; }
        """;

    private static readonly string FragSrc = """
        #version 330 core
        in vec3 vCol;
        out vec4 fragColor;
        void main() { fragColor = vec4(vCol, 0.95); }
        """;

    public SpineRenderer() => _shader = new Shader(VertSrc, FragSrc);

    /// <summary>Uploads the polyline. Must be called on the GL thread.</summary>
    public void Update(IReadOnlyList<(Vector3 Pos, Vector3 Color)> points)
    {
        _primitive = PrimitiveType.LineStrip;
        Upload(points);
    }

    /// <summary>
    /// Uploads independent line segments as interleaved A,B pairs (count must be even).
    /// Used for wireframe cages (e.g. X-bracing cylinder).
    /// </summary>
    public void UpdateSegments(IReadOnlyList<(Vector3 Pos, Vector3 Color)> points)
    {
        _primitive = PrimitiveType.Lines;
        Upload(points);
    }

    private void Upload(IReadOnlyList<(Vector3 Pos, Vector3 Color)> points)
    {
        _count = points.Count;
        if (_count < 2) return;

        var data = new float[_count * 6];
        for (int i = 0; i < _count; i++)
        {
            var (pos, col) = points[i];
            data[i * 6 + 0] = pos.X; data[i * 6 + 1] = pos.Y; data[i * 6 + 2] = pos.Z;
            data[i * 6 + 3] = col.X; data[i * 6 + 4] = col.Y; data[i * 6 + 5] = col.Z;
        }

        if (_vao == 0) { _vao = GL.GenVertexArray(); _vbo = GL.GenBuffer(); }
        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, data.Length * sizeof(float), data, BufferUsageHint.DynamicDraw);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), 3 * sizeof(float));
        GL.BindVertexArray(0);
    }

    public void Draw(Matrix4 mvp)
    {
        if (_disposed || _count < 2 || _vao == 0) return;
        _shader.Use();
        _shader.SetMatrix4("uMVP", ref mvp);
        GL.LineWidth(4f);
        GL.BindVertexArray(_vao);
        GL.DrawArrays(_primitive, 0, _count);
        GL.BindVertexArray(0);
        GL.LineWidth(1f);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_vao != 0) { GL.DeleteVertexArray(_vao); GL.DeleteBuffer(_vbo); }
        _shader.Dispose();
    }
}
