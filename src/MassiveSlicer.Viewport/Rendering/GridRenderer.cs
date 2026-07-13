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

    // 2D Slice Plane Viewer overlay grid (rebuild when bead spacing changes).
    private int   _sliceVao, _sliceVbo, _sliceCount;
    private float _sliceSpacing = -1f;
    private float _sliceExtent  = 5000f;

    private bool _disposed;

    // Grid extents and spacing (all in mm).
    private const float MinorSpacing  =   100f;
    private const float MinorExtent   =  2000f;
    private const float MajorSpacing  =  1000f;
    private const float MajorExtent   = 10000f;
    // Colours (RGBA).
    private static readonly Vector4 MinorColour = new(0.18f, 0.22f, 0.30f, 1f);
    private static readonly Vector4 MajorColour = new(0.28f, 0.33f, 0.45f, 1f);
    /// <summary>Subtle white — 2D slice-view measurement grid (needs GL blend).</summary>
    private static readonly Vector4 SliceGridColour = new(1f, 1f, 1f, 0.07f);

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

    /// <summary>
    /// 2D Slice Plane Viewer grid: white @ low alpha (requires blending).
    /// Drawn in the XY plane at <paramref name="planeZ"/> so it sits with the active slice.
    /// Spacing is clamped so we never emit a near-solid mesh of lines.
    /// </summary>
    public void DrawSliceGrid(Matrix4 mvp, float spacingMm, float planeZ,
        float centerX = 0f, float centerY = 0f, float extentMm = 4000f)
    {
        // Floor spacing so a half-bead grid never densifies into a white sheet.
        if (spacingMm < 2f) spacingMm = 2f;
        // Cap extent so line count stays reasonable (~2 * 2 * (extent/spacing) lines).
        float maxExtent = MathF.Min(extentMm, spacingMm * 200f); // ≤ ~400 lines per axis
        EnsureSliceGrid(spacingMm, maxExtent);

        // Shift grid from Z=0 / origin-centered verts into the slice plane.
        var model = Matrix4.CreateTranslation(centerX, centerY, planeZ);
        var gridMvp = model * mvp;

        _shader.Use();
        _shader.SetMatrix4("uMVP", ref gridMvp);
        _shader.SetVector4("uColor", SliceGridColour);

        // Alpha only works with blending — without it the white grid paints solid
        // and washes out toolpaths / looks like a bright sheet.
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.Disable(EnableCap.DepthTest);
        GL.DepthMask(false);
        GL.BindVertexArray(_sliceVao);
        GL.DrawArrays(PrimitiveType.Lines, 0, _sliceCount);
        GL.BindVertexArray(0);
        GL.DepthMask(true);
        GL.Enable(EnableCap.DepthTest);
        // Leave blend enabled — later translucent passes expect it; solid passes re-set.
    }

    private void EnsureSliceGrid(float spacing, float extent)
    {
        if (_sliceVao != 0
            && MathF.Abs(_sliceSpacing - spacing) < 1e-4f
            && MathF.Abs(_sliceExtent - extent) < 1e-3f)
            return;

        if (_sliceVao != 0)
        {
            GL.DeleteVertexArray(_sliceVao);
            GL.DeleteBuffer(_sliceVbo);
            _sliceVao = _sliceVbo = _sliceCount = 0;
        }

        _sliceSpacing = spacing;
        _sliceExtent  = extent;
        var verts = BuildGridLines(spacing, extent);
        _sliceCount = verts.Length / 3;
        (_sliceVao, _sliceVbo) = UploadLineVerts(verts);
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
        if (_sliceVao != 0)
        {
            GL.DeleteVertexArray(_sliceVao);
            GL.DeleteBuffer(_sliceVbo);
        }
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
