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
    private int _originVao, _originVbo, _originCount;
    private readonly Shader _originShader;

    private bool _disposed;

    private const float OriginAxisLengthMm = 300f;
    private const float OriginDotRadiusMm = 18f;
    private const float OriginArrowHeadMm = 36f;
    private const float OriginLiftZMm = 2f;

    private static readonly Vector4 BorderColour = new(0.00f, 0.90f, 0.90f, 1f); // cyan
    private static readonly Vector4 GridColour   = new(0.05f, 0.35f, 0.08f, 1f); // dark green

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
    private static readonly string OriginVertSrc = """
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

    private static readonly string OriginFragSrc = """
        #version 330 core
        in vec3 vColor;
        out vec4 fragColor;
        void main() {
            fragColor = vec4(vColor, 1.0);
        }
        """;

    /// <param name="baseOrigin">Locked BASE 0,0,0 marker in world-space mm (not affected by E1).</param>
    /// <param name="gridOrigin">Back-left corner of the print-area grid in world-space mm.</param>
    public BedBoundaryRenderer(Vector3 baseOrigin, Vector3 gridOrigin, float width, float depth, Vector3 datum, float diameter = 0f)
    {
        _shader = new Shader(VertSrc, FragSrc);
        _originShader = new Shader(OriginVertSrc, OriginFragSrc);
        BuildOriginMarker(baseOrigin);
        if (diameter > 0f)
        {
            BuildCircleBorder(datum, diameter * 0.5f);
            BuildPolarGrid(datum, diameter * 0.5f);
        }
        else
        {
            BuildBorder(gridOrigin, width, depth);
            BuildGrid(gridOrigin, width, depth, datum);
        }
    }

    /// <summary>
    /// Draws the bed boundary. Grid/border use <paramref name="gridMvp"/> (includes E1 rotation);
    /// the BASE origin marker uses <paramref name="originMvp"/> so it stays world-locked.
    /// </summary>
    public void Draw(Matrix4 gridMvp, Matrix4 originMvp)
    {
        _shader.Use();
        _shader.SetMatrix4("uMVP", ref gridMvp);

        GL.LineWidth(GridWidth);
        _shader.SetVector4("uColor", GridColour);
        GL.BindVertexArray(_gridVao);
        GL.DrawArrays(PrimitiveType.Lines, 0, _gridCount);

        GL.LineWidth(BorderWidth);
        _shader.SetVector4("uColor", BorderColour);
        GL.BindVertexArray(_borderVao);
        GL.DrawArrays(PrimitiveType.Lines, 0, _borderCount);

        GL.LineWidth(4.0f);
        _originShader.Use();
        _originShader.SetMatrix4("uMVP", ref originMvp);
        GL.BindVertexArray(_originVao);
        GL.DrawArrays(PrimitiveType.Lines, 0, _originCount);

        GL.LineWidth(1.0f);
        GL.BindVertexArray(0);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shader.Dispose();
        _originShader.Dispose();
        GL.DeleteVertexArray(_borderVao);
        GL.DeleteBuffer(_borderVbo);
        GL.DeleteVertexArray(_gridVao);
        GL.DeleteBuffer(_gridVbo);
        GL.DeleteVertexArray(_originVao);
        GL.DeleteBuffer(_originVbo);
    }

    /// <summary>
    /// Back-left grid origin: blue dot plus +X (red) and +Y (green) arrows.
    /// </summary>
    private void BuildOriginMarker(Vector3 corner)
    {
        float z = corner.Z + OriginLiftZMm;
        float x0 = corner.X;
        float y0 = corner.Y;
        var verts = new List<float>();

        // Blue dot — small circle at the corner.
        const float br = 0.20f, bg = 0.55f, bb = 1.00f;
        const int dotSegs = 12;
        for (int i = 0; i < dotSegs; i++)
        {
            float a0 = (i       / (float)dotSegs) * MathF.Tau;
            float a1 = ((i + 1) / (float)dotSegs) * MathF.Tau;
            AddOriginVertex(verts, x0 + OriginDotRadiusMm * MathF.Cos(a0), y0 + OriginDotRadiusMm * MathF.Sin(a0), z, br, bg, bb);
            AddOriginVertex(verts, x0 + OriginDotRadiusMm * MathF.Cos(a1), y0 + OriginDotRadiusMm * MathF.Sin(a1), z, br, bg, bb);
        }

        // +X arrow (red).
        const float xr = 0.95f, xg = 0.25f, xb = 0.25f;
        float xTip = x0 + OriginAxisLengthMm;
        AddOriginSegment(verts, x0, y0, z, xTip, y0, z, xr, xg, xb);
        AddOriginSegment(verts, xTip, y0, z, xTip - OriginArrowHeadMm, y0 + OriginArrowHeadMm * 0.45f, z, xr, xg, xb);
        AddOriginSegment(verts, xTip, y0, z, xTip - OriginArrowHeadMm, y0 - OriginArrowHeadMm * 0.45f, z, xr, xg, xb);

        // +Y arrow (green).
        const float yr = 0.25f, yg = 0.90f, yb = 0.35f;
        float yTip = y0 + OriginAxisLengthMm;
        AddOriginSegment(verts, x0, y0, z, x0, yTip, z, yr, yg, yb);
        AddOriginSegment(verts, x0, yTip, z, x0 - OriginArrowHeadMm * 0.45f, yTip - OriginArrowHeadMm, z, yr, yg, yb);
        AddOriginSegment(verts, x0, yTip, z, x0 + OriginArrowHeadMm * 0.45f, yTip - OriginArrowHeadMm, z, yr, yg, yb);

        _originCount = verts.Count / 6;
        (_originVao, _originVbo) = UploadOrigin([.. verts]);
    }

    private static void AddOriginVertex(List<float> verts, float x, float y, float z, float r, float g, float b)
        => verts.AddRange([x, y, z, r, g, b]);

    private static void AddOriginSegment(List<float> verts,
        float x0, float y0, float z0, float x1, float y1, float z1,
        float r, float g, float b)
    {
        AddOriginVertex(verts, x0, y0, z0, r, g, b);
        AddOriginVertex(verts, x1, y1, z1, r, g, b);
    }

    private static (int vao, int vbo) UploadOrigin(float[] verts)
    {
        int vao = GL.GenVertexArray();
        int vbo = GL.GenBuffer();
        GL.BindVertexArray(vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, verts.Length * sizeof(float), verts, BufferUsageHint.StaticDraw);
        int stride = 6 * sizeof(float);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
        GL.EnableVertexAttribArray(1);
        GL.BindVertexArray(0);
        return (vao, vbo);
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
