using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Rendering;

/// <summary>
/// Draws a semi-transparent quad representing the angled-slicing cutting plane.
/// The face is drawn with low alpha so the mesh behind it remains visible;
/// the border is drawn at full opacity for clarity.
/// </summary>
public sealed class PlanePreviewRenderer : IDisposable
{
    private int  _faceVao, _faceVbo;
    private int  _edgeVao, _edgeVbo;
    private bool _disposed;
    private bool _hasData;

    // Cache last params so the VBO is only rebuilt when something actually changes.
    private Vector3 _lastCenter;
    private Vector3 _lastNormal;
    private float   _lastSize;

    private readonly Shader _shader;

    private static readonly string VertSrc = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        uniform mat4 uMVP;
        void main() { gl_Position = vec4(aPos, 1.0) * uMVP; }
        """;

    private static readonly string FragSrc = """
        #version 330 core
        uniform vec4 uColor;
        out vec4 fragColor;
        void main() { fragColor = uColor; }
        """;

    // Accent blue to match the toolpath extrude colour.
    private static readonly Vector4 FaceColor = new(0.1f, 0.45f, 0.9f, 0.12f);
    private static readonly Vector4 EdgeColor = new(0.1f, 0.45f, 0.9f, 0.75f);

    public PlanePreviewRenderer()
    {
        _shader = new Shader(VertSrc, FragSrc);
    }

    /// <summary>
    /// Updates the plane geometry. Must be called on the GL thread.
    /// Skips the GPU upload when the parameters have not changed.
    /// </summary>
    public void Update(Vector3 center, Vector3 normal, float size)
    {
        if (_hasData && center == _lastCenter && normal == _lastNormal && MathF.Abs(size - _lastSize) < 0.001f)
            return;

        _lastCenter = center;
        _lastNormal = normal;
        _lastSize   = size;
        _hasData    = true;

        // Build two tangent vectors perpendicular to the normal.
        var up  = MathF.Abs(normal.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        var u   = Vector3.Normalize(Vector3.Cross(normal, up));
        var v   = Vector3.Cross(normal, u);

        float h = size * 0.5f;
        var c0  = center - h * u - h * v;
        var c1  = center + h * u - h * v;
        var c2  = center + h * u + h * v;
        var c3  = center - h * u + h * v;

        // ── Face: two triangles ───────────────────────────────────────────────
        float[] faceVerts =
        [
            c0.X, c0.Y, c0.Z,
            c1.X, c1.Y, c1.Z,
            c2.X, c2.Y, c2.Z,
            c0.X, c0.Y, c0.Z,
            c2.X, c2.Y, c2.Z,
            c3.X, c3.Y, c3.Z,
        ];

        if (_faceVao == 0) { _faceVao = GL.GenVertexArray(); _faceVbo = GL.GenBuffer(); }
        GL.BindVertexArray(_faceVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _faceVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, faceVerts.Length * sizeof(float), faceVerts, BufferUsageHint.DynamicDraw);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
        GL.BindVertexArray(0);

        // ── Edge: 4 line segments ─────────────────────────────────────────────
        float[] edgeVerts =
        [
            c0.X, c0.Y, c0.Z,  c1.X, c1.Y, c1.Z,
            c1.X, c1.Y, c1.Z,  c2.X, c2.Y, c2.Z,
            c2.X, c2.Y, c2.Z,  c3.X, c3.Y, c3.Z,
            c3.X, c3.Y, c3.Z,  c0.X, c0.Y, c0.Z,
        ];

        if (_edgeVao == 0) { _edgeVao = GL.GenVertexArray(); _edgeVbo = GL.GenBuffer(); }
        GL.BindVertexArray(_edgeVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _edgeVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, edgeVerts.Length * sizeof(float), edgeVerts, BufferUsageHint.DynamicDraw);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
        GL.BindVertexArray(0);
    }

    public void Draw(Matrix4 mvp)
    {
        if (!_hasData || _disposed) return;

        _shader.Use();
        _shader.SetMatrix4("uMVP", ref mvp);

        GL.Disable(EnableCap.CullFace);

        // Semi-transparent face.
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.DepthMask(false);
        _shader.SetVector4("uColor", FaceColor);
        GL.BindVertexArray(_faceVao);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 6);

        // Solid border.
        GL.DepthMask(true);
        _shader.SetVector4("uColor", EdgeColor);
        GL.BindVertexArray(_edgeVao);
        GL.DrawArrays(PrimitiveType.Lines, 0, 8);

        GL.Disable(EnableCap.Blend);
        GL.Enable(EnableCap.CullFace);
        GL.BindVertexArray(0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_faceVao != 0) { GL.DeleteVertexArray(_faceVao); GL.DeleteBuffer(_faceVbo); }
        if (_edgeVao != 0) { GL.DeleteVertexArray(_edgeVao); GL.DeleteBuffer(_edgeVbo); }
        _shader.Dispose();
    }
}
