using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Rendering;

/// <summary>
/// Renders world-space X/Y/Z axis lines at the origin.
/// Uses a single VAO with per-vertex colour so all three axes draw in one call.
/// X = red, Y = green, Z = blue -- standard robotics/CAD convention.
/// </summary>
public sealed class AxisRenderer : IDisposable
{
    private readonly Shader _shader;
    private int  _vao, _vbo;
    private bool _disposed;

    private const float DefaultLength = 200f; // mm

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
        void main() {
            fragColor = vec4(vColor, 1.0);
        }
        """;

    /// <summary>
    /// Allocates GPU resources. Must be called on the OpenGL thread after the
    /// context is current. Custom colors and length are optional — defaults give
    /// the standard red/green/blue 200mm axes.
    /// </summary>
    public AxisRenderer(
        float xR = 0.90f, float xG = 0.22f, float xB = 0.22f,
        float yR = 0.22f, float yG = 0.80f, float yB = 0.30f,
        float zR = 0.25f, float zG = 0.45f, float zB = 0.95f,
        float length = DefaultLength)
    {
        _shader = new Shader(VertSrc, FragSrc);

        // Six vertices: 3 axis lines × 2 endpoints.
        // Layout per vertex: x, y, z, r, g, b
        float[] verts =
        [
            // X axis
            0f,     0f, 0f,   xR, xG, xB,
            length, 0f, 0f,   xR, xG, xB,
            // Y axis
            0f,     0f,     0f,   yR, yG, yB,
            0f, length,     0f,   yR, yG, yB,
            // Z axis
            0f, 0f,     0f,   zR, zG, zB,
            0f, 0f, length,   zR, zG, zB,
        ];

        _vao = GL.GenVertexArray();
        _vbo = GL.GenBuffer();

        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, verts.Length * sizeof(float), verts, BufferUsageHint.StaticDraw);

        int stride = 6 * sizeof(float);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
        GL.EnableVertexAttribArray(1);

        GL.BindVertexArray(0);
    }

    /// <summary>Draws the three axis lines with the supplied MVP matrix.</summary>
    public void Draw(Matrix4 mvp)
    {
        _shader.Use();
        _shader.SetMatrix4("uMVP", ref mvp);
        GL.BindVertexArray(_vao);
        GL.DrawArrays(PrimitiveType.Lines, 0, 6);
        GL.BindVertexArray(0);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shader.Dispose();
        GL.DeleteVertexArray(_vao);
        GL.DeleteBuffer(_vbo);
    }
}
