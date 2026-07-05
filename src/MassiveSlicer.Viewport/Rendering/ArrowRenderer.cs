using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Rendering;

/// <summary>
/// Renders a single world-space direction arrow (shaft + V arrowhead) as line segments —
/// used as the angled-slice direction helper at the plane perimeter. Rebuild the geometry
/// with <see cref="Update"/> when the direction changes; draw in the overlay pass.
/// </summary>
public sealed class ArrowRenderer : IDisposable
{
    private readonly Shader _shader;
    private int  _vao, _vbo;
    private bool _disposed;
    private bool _hasGeometry;

    private static readonly string VertSrc = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aColor;
        uniform mat4 uMVP;
        out vec3 vColor;
        void main() { gl_Position = vec4(aPos, 1.0) * uMVP; vColor = aColor; }
        """;

    private static readonly string FragSrc = """
        #version 330 core
        in vec3 vColor;
        out vec4 fragColor;
        void main() { fragColor = vec4(vColor, 1.0); }
        """;

    public ArrowRenderer()
    {
        _shader = new Shader(VertSrc, FragSrc);
        _vao = GL.GenVertexArray();
        _vbo = GL.GenBuffer();
        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        int stride = 6 * sizeof(float);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
        GL.EnableVertexAttribArray(1);
        GL.BindVertexArray(0);
    }

    /// <summary>Rebuilds the arrow (world space) from an origin, direction, and length.
    /// A zero-length direction hides the arrow.</summary>
    public void Update(Vector3 origin, Vector3 direction, float length, Vector3 color)
    {
        if (direction.LengthSquared < 1e-9f || length <= 0f)
        {
            _hasGeometry = false;
            return;
        }

        var dir = Vector3.Normalize(direction);
        var tip = origin + dir * length;

        // Perpendicular for the arrowhead V — in the XY plane for a bed-plane arrow.
        var perp = Vector3.Cross(dir, Vector3.UnitZ);
        if (perp.LengthSquared < 1e-9f) perp = Vector3.Cross(dir, Vector3.UnitX);
        perp = Vector3.Normalize(perp);

        float head = length * 0.18f;
        float wing = length * 0.10f;
        var back = tip - dir * head;
        var h1   = back + perp * wing;
        var h2   = back - perp * wing;

        void V(float[] a, int i, Vector3 p)
        {
            a[i] = p.X; a[i + 1] = p.Y; a[i + 2] = p.Z;
            a[i + 3] = color.X; a[i + 4] = color.Y; a[i + 5] = color.Z;
        }

        var verts = new float[6 * 6]; // 3 segments × 2 endpoints × (pos+color)
        V(verts, 0,  origin); V(verts, 6,  tip);   // shaft
        V(verts, 12, tip);    V(verts, 18, h1);    // head wing 1
        V(verts, 24, tip);    V(verts, 30, h2);    // head wing 2

        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, verts.Length * sizeof(float), verts, BufferUsageHint.DynamicDraw);
        GL.BindVertexArray(0);
        _hasGeometry = true;
    }

    /// <summary>Draws the arrow (no-op until <see cref="Update"/> has supplied geometry).</summary>
    public void Draw(Matrix4 mvp)
    {
        if (!_hasGeometry) return;
        _shader.Use();
        _shader.SetMatrix4("uMVP", ref mvp);
        GL.BindVertexArray(_vao);
        GL.DrawArrays(PrimitiveType.Lines, 0, 6);
        GL.BindVertexArray(0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shader.Dispose();
        GL.DeleteVertexArray(_vao);
        GL.DeleteBuffer(_vbo);
    }
}
