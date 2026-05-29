using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Rendering;

/// <summary>
/// Renders a multi-tier ground grid in the XY plane (Z = 0).
/// Uses three separate VBOs for minor lines, major lines, and world-axis lines
/// so each tier can be drawn with a distinct colour in a single draw call.
/// </summary>
public sealed class GridRenderer : IDisposable
{
    private readonly Shader _shader;

    private int _minorVao, _minorVbo, _minorCount;
    private int _majorVao, _majorVbo, _majorCount;

    private bool _disposed;

    // Grid extents and spacing (all in mm).
    private const float MinorSpacing  =   100f;
    private const float MinorExtent   =  2000f;
    private const float MajorSpacing  =  1000f;
    private const float MajorExtent   = 10000f;
    // Colours (RGBA).
    private static readonly Vector4 MinorColour = new(0.18f, 0.22f, 0.30f, 1f);
    private static readonly Vector4 MajorColour = new(0.28f, 0.33f, 0.45f, 1f);

    private static readonly string VertSrc = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        uniform mat4 uMVP;
        void main() {
            // OpenTK 4.x uses row-vector convention: v * M, not M * v.
            gl_Position = vec4(aPos, 1.0) * uMVP;
        }
        """;

    private static readonly string FragSrc = """
        #version 330 core
        uniform vec4 uColor;
        out vec4 fragColor;
        void main() {
            fragColor = uColor;
        }
        """;

    /// <summary>
    /// Initialises the grid renderer. Must be called on the OpenGL thread
    /// after a valid context has been made current.
    /// </summary>
    public GridRenderer()
    {
        _shader = new Shader(VertSrc, FragSrc);
        BuildMinorGrid();
        BuildMajorGrid();
    }

    /// <summary>
    /// Draws the grid using the supplied combined MVP matrix.
    /// The model matrix is identity -- the grid lives at world origin.
    /// </summary>
    /// <param name="mvp">Combined model-view-projection matrix.</param>
    public void Draw(Matrix4 mvp)
    {
        _shader.Use();

        // Minor lines
        _shader.SetMatrix4("uMVP", ref mvp);
        _shader.SetVector4("uColor", MinorColour);
        GL.BindVertexArray(_minorVao);
        GL.DrawArrays(PrimitiveType.Lines, 0, _minorCount);

        // Major lines
        _shader.SetVector4("uColor", MajorColour);
        GL.BindVertexArray(_majorVao);
        GL.DrawArrays(PrimitiveType.Lines, 0, _majorCount);

        GL.BindVertexArray(0);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _shader.Dispose();
        GL.DeleteVertexArray(_minorVao);
        GL.DeleteBuffer(_minorVbo);
        GL.DeleteVertexArray(_majorVao);
        GL.DeleteBuffer(_majorVbo);
    }

    // -- Private builders ----------------------------------------------------

    private void BuildMinorGrid()
    {
        var verts = BuildGridLines(MinorSpacing, MinorExtent);
        _minorCount = verts.Length / 3;
        (_minorVao, _minorVbo) = UploadLineVerts(verts);
    }

    private void BuildMajorGrid()
    {
        var verts = BuildGridLines(MajorSpacing, MajorExtent);
        _majorCount = verts.Length / 3;
        (_majorVao, _majorVbo) = UploadLineVerts(verts);
    }

    /// <summary>
    /// Generates XY-plane grid line vertices (Z = 0) for lines spaced <paramref name="spacing"/>
    /// apart, running from −<paramref name="extent"/> to +<paramref name="extent"/>.
    /// Lines parallel to X and parallel to Y are both generated.
    /// Returns a flat array of (x, y, z) triplets.
    /// </summary>
    private static float[] BuildGridLines(float spacing, float extent)
    {
        int steps = (int)(extent / spacing);
        var verts = new List<float>();

        for (int i = -steps; i <= steps; i++)
        {
            float t = i * spacing;

            // Line parallel to X axis at Y = t
            verts.AddRange([-extent, t, 0f, extent, t, 0f]);
            // Line parallel to Y axis at X = t
            verts.AddRange([t, -extent, 0f, t, extent, 0f]);
        }

        return [.. verts];
    }

    private static (int vao, int vbo) UploadLineVerts(float[] verts)
    {
        int vao = GL.GenVertexArray();
        int vbo = GL.GenBuffer();

        GL.BindVertexArray(vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, verts.Length * sizeof(float), verts, BufferUsageHint.StaticDraw);

        // layout(location = 0) in vec3 aPos
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
        GL.EnableVertexAttribArray(0);

        GL.BindVertexArray(0);
        return (vao, vbo);
    }
}
