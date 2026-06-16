using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Rendering;

/// <summary>
/// Draws a highlighted border rectangle and internal grid representing the print bed boundary.
/// Border is cyan; internal grid is a dimmer cyan.
/// All coordinates are world-space mm (Z-up).
/// </summary>
public sealed class BedBoundaryRenderer : IDisposable
{
    private readonly Shader _shader;

    private int _borderVao, _borderVbo, _borderCount;
    private int _gridVao,   _gridVbo,   _gridCount;

    private bool _disposed;

    private static readonly Vector4 BorderColour = new(0.00f, 0.90f, 0.90f, 1f); // cyan
    private static readonly Vector4 GridColour   = new(0.00f, 0.60f, 0.60f, 1f); // dimmer cyan

    private const float GridSpacing  = 500f;
    private const float BorderWidth  = 3.0f;
    private const float GridWidth    = 2.5f;

    private static readonly string VertSrc = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        uniform mat4 uMVP;
        void main() {
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
    /// Creates the renderer. Must be called on the GL thread with a valid context current.
    /// </summary>
    /// <param name="origin">Back-left corner of the bed in world-space mm.</param>
    /// <param name="width">Bed extent along +X in mm.</param>
    /// <param name="depth">Bed extent along +Y in mm.</param>
    /// <param name="datum">The 0,0,0 reference the internal grid lines align to (a line passes
    /// through it). For a centre-origin rotary bed this is the bed centre, so a cross sits at the
    /// rotation axis; for a corner-origin bed it equals <paramref name="origin"/>.</param>
    /// <param name="diameter">When &gt; 0, render a circular rotary bed of this diameter centred on
    /// <paramref name="datum"/> (polar grid) instead of a rectangle.</param>
    public BedBoundaryRenderer(Vector3 origin, float width, float depth, Vector3 datum, float diameter = 0f)
    {
        _shader = new Shader(VertSrc, FragSrc);
        if (diameter > 0f)
        {
            BuildCircleBorder(datum, diameter * 0.5f);
            BuildPolarGrid(datum, diameter * 0.5f);
        }
        else
        {
            BuildBorder(origin, width, depth);
            BuildGrid(origin, width, depth, datum);
        }
    }

    /// <summary>Draws the bed boundary using the supplied combined MVP matrix.</summary>
    public void Draw(Matrix4 mvp)
    {
        _shader.Use();
        _shader.SetMatrix4("uMVP", ref mvp);

        GL.LineWidth(GridWidth);
        _shader.SetVector4("uColor", GridColour);
        GL.BindVertexArray(_gridVao);
        GL.DrawArrays(PrimitiveType.Lines, 0, _gridCount);

        GL.LineWidth(BorderWidth);
        _shader.SetVector4("uColor", BorderColour);
        GL.BindVertexArray(_borderVao);
        GL.DrawArrays(PrimitiveType.Lines, 0, _borderCount);

        GL.LineWidth(1.0f);
        GL.BindVertexArray(0);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shader.Dispose();
        GL.DeleteVertexArray(_borderVao);
        GL.DeleteBuffer(_borderVbo);
        GL.DeleteVertexArray(_gridVao);
        GL.DeleteBuffer(_gridVbo);
    }

    private void BuildBorder(Vector3 o, float w, float d)
    {
        float x0 = o.X, y0 = o.Y, x1 = o.X + w, y1 = o.Y + d, z = o.Z;
        float[] verts =
        [
            x0, y0, z,  x1, y0, z,
            x1, y0, z,  x1, y1, z,
            x1, y1, z,  x0, y1, z,
            x0, y1, z,  x0, y0, z,
        ];
        _borderCount = verts.Length / 3;
        (_borderVao, _borderVbo) = Upload(verts);
    }

    private void BuildGrid(Vector3 o, float w, float d, Vector3 datum)
    {
        float x0 = o.X, y0 = o.Y, x1 = o.X + w, y1 = o.Y + d, z = o.Z;
        var verts = new List<float>();

        // Lines are anchored to the datum (datum.X / datum.Y), stepping both directions,
        // so one line passes exactly through the datum (the bed centre for a rotary bed).
        foreach (float y in Ticks(y0, y1, datum.Y))
            verts.AddRange([x0, y, z, x1, y, z]);

        foreach (float x in Ticks(x0, x1, datum.X))
            verts.AddRange([x, y0, z, x, y1, z]);

        _gridCount = verts.Count / 3;
        (_gridVao, _gridVbo) = Upload([.. verts]);
    }

    // -- Circular / rotary bed -------------------------------------------------

    private void BuildCircleBorder(Vector3 c, float radius)
    {
        var verts = new List<float>();
        _borderCount = AppendCircle(verts, c, radius, 120);
        (_borderVao, _borderVbo) = Upload([.. verts]);
    }

    private void BuildPolarGrid(Vector3 c, float radius)
    {
        var verts = new List<float>();

        // Concentric rings every GridSpacing inside the border.
        for (float r = GridSpacing; r < radius; r += GridSpacing)
            AppendCircle(verts, c, r, 96);

        // Radial spokes every 30°, centre → rim.
        for (int deg = 0; deg < 360; deg += 30)
        {
            float a = deg * MathF.PI / 180f;
            verts.AddRange([c.X, c.Y, c.Z,
                            c.X + radius * MathF.Cos(a), c.Y + radius * MathF.Sin(a), c.Z]);
        }

        _gridCount = verts.Count / 3;
        (_gridVao, _gridVbo) = Upload([.. verts]);
    }

    /// <summary>Appends a circle (as consecutive line segments) and returns the vertex count added.</summary>
    private static int AppendCircle(List<float> verts, Vector3 c, float radius, int segments)
    {
        int start = verts.Count / 3;
        for (int i = 0; i < segments; i++)
        {
            float a0 = (i       / (float)segments) * MathF.Tau;
            float a1 = ((i + 1) / (float)segments) * MathF.Tau;
            verts.AddRange([
                c.X + radius * MathF.Cos(a0), c.Y + radius * MathF.Sin(a0), c.Z,
                c.X + radius * MathF.Cos(a1), c.Y + radius * MathF.Sin(a1), c.Z,
            ]);
        }
        return verts.Count / 3 - start;
    }

    /// <summary>Grid-line positions at <paramref name="anchor"/> + k·spacing strictly inside (lo, hi).</summary>
    private static IEnumerable<float> Ticks(float lo, float hi, float anchor)
    {
        int kStart = (int)MathF.Ceiling((lo - anchor) / GridSpacing);
        for (int k = kStart; ; k++)
        {
            float p = anchor + k * GridSpacing;
            if (p >= hi) yield break;
            if (p > lo)  yield return p;
        }
    }

    private static (int vao, int vbo) Upload(float[] verts)
    {
        int vao = GL.GenVertexArray();
        int vbo = GL.GenBuffer();
        GL.BindVertexArray(vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, verts.Length * sizeof(float), verts, BufferUsageHint.StaticDraw);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
        GL.EnableVertexAttribArray(0);
        GL.BindVertexArray(0);
        return (vao, vbo);
    }
}
