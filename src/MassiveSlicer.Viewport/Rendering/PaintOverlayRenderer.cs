using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Rendering;

/// <summary>
/// Draws paint-edit overlays: coloured LINE SEGMENTS (mark spheres as circles,
/// path hover polylines) plus optional GL POINTS for Point-mode hover / selection
/// (recolour a bead yellow without a spherical helper).
/// </summary>
public sealed class PaintOverlayRenderer : IDisposable
{
    private int _lineVao, _lineVbo, _lineCount;
    private int _ptVao, _ptVbo, _ptCount;
    private bool _disposed;

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

    // Soft round sprites for hover/selected beads (shader sets gl_PointSize).
    private readonly Shader _pointShader;

    private static readonly string PointVertSrc = """
#version 330 core
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aCol;
uniform mat4  uMVP;
uniform float uPointSize;
out vec3 vCol;
void main() {
    vCol = aCol;
    gl_Position = vec4(aPos, 1.0) * uMVP;
    gl_PointSize = uPointSize;
}
""";

    private static readonly string PointFragSrc = """
#version 330 core
in vec3 vCol;
out vec4 fragColor;
void main() {
    vec2 c = gl_PointCoord * 2.0 - 1.0;
    float r2 = dot(c, c);
    if (r2 > 1.0) discard;
    float edge = smoothstep(1.0, 0.45, r2);
    fragColor = vec4(vCol, edge);
}
""";

    public PaintOverlayRenderer()
    {
        _shader      = new Shader(VertSrc, FragSrc);
        _pointShader = new Shader(PointVertSrc, PointFragSrc);
    }

    /// <summary>
    /// Uploads line segments (pairs) and optional highlight points. GL thread only.
    /// Either list may be empty.
    /// </summary>
    public void Update(
        IReadOnlyList<(Vector3 Pos, Vector3 Color)> segments,
        IReadOnlyList<(Vector3 Pos, Vector3 Color)>? highlightPoints = null)
    {
        // ── Lines ──────────────────────────────────────────────────────────
        _lineCount = segments.Count - segments.Count % 2;
        if (_lineCount >= 2)
        {
            var data = new float[_lineCount * 6];
            for (int i = 0; i < _lineCount; i++)
            {
                var (pos, col) = segments[i];
                data[i * 6 + 0] = pos.X; data[i * 6 + 1] = pos.Y; data[i * 6 + 2] = pos.Z;
                data[i * 6 + 3] = col.X; data[i * 6 + 4] = col.Y; data[i * 6 + 5] = col.Z;
            }
            if (_lineVao == 0) { _lineVao = GL.GenVertexArray(); _lineVbo = GL.GenBuffer(); }
            GL.BindVertexArray(_lineVao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _lineVbo);
            GL.BufferData(BufferTarget.ArrayBuffer, data.Length * sizeof(float), data, BufferUsageHint.DynamicDraw);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), 0);
            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), 3 * sizeof(float));
            GL.BindVertexArray(0);
        }
        else
        {
            _lineCount = 0;
        }

        // ── Highlight points (hover / selection recolour) ───────────────────
        _ptCount = highlightPoints?.Count ?? 0;
        if (_ptCount > 0 && highlightPoints is not null)
        {
            var data = new float[_ptCount * 6];
            for (int i = 0; i < _ptCount; i++)
            {
                var (pos, col) = highlightPoints[i];
                data[i * 6 + 0] = pos.X; data[i * 6 + 1] = pos.Y; data[i * 6 + 2] = pos.Z;
                data[i * 6 + 3] = col.X; data[i * 6 + 4] = col.Y; data[i * 6 + 5] = col.Z;
            }
            if (_ptVao == 0) { _ptVao = GL.GenVertexArray(); _ptVbo = GL.GenBuffer(); }
            GL.BindVertexArray(_ptVao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _ptVbo);
            GL.BufferData(BufferTarget.ArrayBuffer, data.Length * sizeof(float), data, BufferUsageHint.DynamicDraw);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), 0);
            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), 3 * sizeof(float));
            GL.BindVertexArray(0);
        }
    }

    public void Draw(Matrix4 mvp)
    {
        if (_disposed) return;
        GL.Disable(EnableCap.DepthTest);

        if (_lineCount >= 2 && _lineVao != 0)
        {
            _shader.Use();
            _shader.SetMatrix4("uMVP", ref mvp);
            GL.LineWidth(6f);
            GL.BindVertexArray(_lineVao);
            GL.DrawArrays(PrimitiveType.Lines, 0, _lineCount);
            GL.BindVertexArray(0);
            GL.LineWidth(1f);
        }

        if (_ptCount > 0 && _ptVao != 0)
        {
            _pointShader.Use();
            _pointShader.SetMatrix4("uMVP", ref mvp);
            // Slightly larger than base path points so hover/select reads clearly.
            _pointShader.SetFloat("uPointSize", 11f);
            GL.Enable(EnableCap.ProgramPointSize);
            GL.DepthMask(false);
            GL.BindVertexArray(_ptVao);
            GL.DrawArrays(PrimitiveType.Points, 0, _ptCount);
            GL.BindVertexArray(0);
            GL.DepthMask(true);
            GL.Disable(EnableCap.ProgramPointSize);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_lineVao != 0) { GL.DeleteVertexArray(_lineVao); GL.DeleteBuffer(_lineVbo); }
        if (_ptVao   != 0) { GL.DeleteVertexArray(_ptVao);   GL.DeleteBuffer(_ptVbo); }
        _shader.Dispose();
        _pointShader.Dispose();
    }
}
