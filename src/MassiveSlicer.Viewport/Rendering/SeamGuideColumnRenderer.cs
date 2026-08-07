using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Rendering;

/// <summary>
/// Renders seam guides as full-height vertical columns.
/// <para>
/// The slicer uses only a guide's XY (<c>PlanarSlicer</c>: <c>SeamGuidePoints.Select(g =&gt; g.ToXY())</c>),
/// so one guide moves the seam on every layer bottom to top. A column shows that; the point marker
/// this replaced implied the seam only shifted near the clicked height, and at a few mm across it
/// was invisible on metre-scale panels.
/// </para>
/// Distinct from <see cref="SeamGuideRenderer"/>, which stays a generic sphere-marker renderer
/// shared by curved-boundary and sequence-path markers.
/// </summary>
public sealed class SeamGuideColumnRenderer : IDisposable
{
    private const string VertSrc = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNormal;
        uniform mat4 uMVP;
        uniform mat3 uNormalMat;
        out vec3 vNormal;
        void main()
        {
            vNormal = normalize(aNormal * uNormalMat);
            gl_Position = vec4(aPos, 1.0) * uMVP;
        }
        """;

    private const string FragSrc = """
        #version 330 core
        in vec3 vNormal;
        uniform vec3 uColor;
        out vec4 fragColor;
        void main()
        {
            // Nearly flat: a guide should read as a drawn line, not a shaded 3D tube.
            float d = max(dot(normalize(vNormal), normalize(vec3(0.3, 0.5, 1.0))), 0.0);
            fragColor = vec4(uColor * (0.85 + d * 0.15), 1.0);
        }
        """;

    private readonly Shader _shader = new(VertSrc, FragSrc);
    private int _vao, _vbo, _count;
    private int _selVao, _selVbo, _selCount;
    private int _prevVao, _prevVbo, _prevCount;

    /// <summary>
    /// Rebuilds the guides. Each is a polyline swept bottom to top, so a guide hugs the wall it
    /// seams instead of standing as a straight vertical line beside a flaring part.
    /// <paramref name="previewPath"/> is the un-placed hover ghost, or null.
    /// </summary>
    public void Update(IReadOnlyList<IReadOnlyList<Vector3>> paths, int selectedIndex,
        IReadOnlyList<Vector3>? previewPath,
        float radius, float selectedRadius)
    {
        Release();

        var verts    = new List<float>();
        var selVerts = new List<float>();
        for (int i = 0; i < paths.Count; i++)
            AppendTube(i == selectedIndex ? selVerts : verts, paths[i],
                i == selectedIndex ? selectedRadius : radius);

        if (verts.Count > 0)
        {
            _count = verts.Count / 6;
            (_vao, _vbo) = BuildVao([.. verts]);
        }

        if (selVerts.Count > 0)
        {
            _selCount = selVerts.Count / 6;
            (_selVao, _selVbo) = BuildVao([.. selVerts]);
        }

        if (previewPath is { Count: > 0 })
        {
            var prevVerts = new List<float>();
            AppendTube(prevVerts, previewPath, radius);
            if (prevVerts.Count > 0)
            {
                _prevCount = prevVerts.Count / 6;
                (_prevVao, _prevVbo) = BuildVao([.. prevVerts]);
            }
        }
    }

    public void Draw(Matrix4 mvp, Vector3 color, Vector3 selectedColor, Vector3 previewColor)
    {
        _shader.Use();
        _shader.SetMatrix4("uMVP", ref mvp);
        var normalMat = new Matrix3(mvp.Row0.Xyz, mvp.Row1.Xyz, mvp.Row2.Xyz);
        _shader.SetMatrix3("uNormalMat", ref normalMat);

        DrawBatch(_vao, _count, color);
        DrawBatch(_selVao, _selCount, selectedColor);
        DrawBatch(_prevVao, _prevCount, previewColor);
    }

    private void DrawBatch(int vao, int count, Vector3 color)
    {
        if (count <= 0) return;
        _shader.SetVector3("uColor", color);
        GL.BindVertexArray(vao);
        GL.DrawArrays(PrimitiveType.Triangles, 0, count);
        GL.BindVertexArray(0);
    }

    public void Dispose() => Release();

    private void Release()
    {
        if (_vao != 0) { GL.DeleteVertexArray(_vao); GL.DeleteBuffer(_vbo); }
        if (_selVao != 0) { GL.DeleteVertexArray(_selVao); GL.DeleteBuffer(_selVbo); }
        if (_prevVao != 0) { GL.DeleteVertexArray(_prevVao); GL.DeleteBuffer(_prevVbo); }
        _vao = _vbo = _count = 0;
        _selVao = _selVbo = _selCount = 0;
        _prevVao = _prevVbo = _prevCount = 0;
    }

    /// <summary>
    /// Hexagonal tube swept through <paramref name="path"/>, capped at both ends. The cross
    /// section stays horizontal: guides run essentially bottom to top, and a horizontal ring
    /// keeps the on-screen thickness even without needing a swept frame.
    /// </summary>
    private static void AppendTube(List<float> verts, IReadOnlyList<Vector3> path, float r)
    {
        if (path.Count < 2) return;

        const int Sides = 6;
        var offs = new Vector3[Sides];
        for (int j = 0; j < Sides; j++)
        {
            float u = 2f * MathF.PI * j / Sides;
            offs[j] = new Vector3(MathF.Cos(u), MathF.Sin(u), 0f) * r;
        }

        for (int k = 0; k + 1 < path.Count; k++)
        {
            var a = path[k];
            var b = path[k + 1];
            if ((b - a).LengthSquared < 1e-8f) continue;   // duplicate sample: no wall to build

            for (int j = 0; j < Sides; j++)
            {
                var o0 = offs[j];
                var o1 = offs[(j + 1) % Sides];
                AddTri(verts, a + o0, a + o1, b + o1);
                AddTri(verts, a + o0, b + o1, b + o0);
            }
        }

        var lo = path[0];
        var hi = path[^1];
        for (int j = 0; j < Sides; j++)
        {
            var o0 = offs[j];
            var o1 = offs[(j + 1) % Sides];
            AddTri(verts, lo, lo + o1, lo + o0);   // bottom cap
            AddTri(verts, hi, hi + o0, hi + o1);   // top cap
        }
    }

    private static void AddTri(List<float> verts, Vector3 a, Vector3 b, Vector3 c)
    {
        var cross = Vector3.Cross(b - a, c - a);
        // A sliver triangle (samples nearly coincident) normalises to NaN and paints the guide
        // black; fall back to up rather than emitting garbage.
        var n = cross.LengthSquared > 1e-12f ? Vector3.Normalize(cross) : Vector3.UnitZ;
        WriteVert(verts, a, n);
        WriteVert(verts, b, n);
        WriteVert(verts, c, n);
    }

    private static void WriteVert(List<float> verts, Vector3 p, Vector3 n)
    {
        verts.Add(p.X); verts.Add(p.Y); verts.Add(p.Z);
        verts.Add(n.X); verts.Add(n.Y); verts.Add(n.Z);
    }

    private static (int vao, int vbo) BuildVao(float[] data)
    {
        int vao = GL.GenVertexArray();
        int vbo = GL.GenBuffer();
        GL.BindVertexArray(vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, data.Length * sizeof(float), data, BufferUsageHint.StaticDraw);
        int stride = 6 * sizeof(float);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
        GL.BindVertexArray(0);
        return (vao, vbo);
    }
}
