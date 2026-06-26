using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using MassiveSlicer.Viewport.Scene;

namespace MassiveSlicer.Viewport.Rendering;

/// <summary>Renders seam guide markers as shaded spheres.</summary>
public sealed class SeamGuideRenderer : IDisposable
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
            float d = max(dot(normalize(vNormal), normalize(vec3(0.3, 0.5, 1.0))), 0.0);
            fragColor = vec4(uColor * (0.35 + d * 0.65), 1.0);
        }
        """;

    private readonly Shader _shader = new(VertSrc, FragSrc);
    private int _vao, _vbo, _count;
    private int _selVao, _selVbo, _selCount;

    public void Update(IReadOnlyList<Vector3> points, int selectedIndex = -1,
        float radius = 4f, float selectedRadius = 7f)
    {
        if (_vao != 0) { GL.DeleteVertexArray(_vao); GL.DeleteBuffer(_vbo); }
        if (_selVao != 0) { GL.DeleteVertexArray(_selVao); GL.DeleteBuffer(_selVbo); }
        _vao = _vbo = _count = 0;
        _selVao = _selVbo = _selCount = 0;
        if (points.Count == 0) return;

        var verts    = new List<float>();
        var selVerts = new List<float>();
        for (int i = 0; i < points.Count; i++)
        {
            if (i == selectedIndex)
                AppendSphere(selVerts, points[i], selectedRadius, 10, 7);
            else
                AppendSphere(verts, points[i], radius, 8, 6);
        }

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
    }

    public void Draw(Matrix4 mvp, Vector3 color, Vector3 selectedColor)
    {
        _shader.Use();
        _shader.SetMatrix4("uMVP", ref mvp);
        var normalMat = new Matrix3(mvp.Row0.Xyz, mvp.Row1.Xyz, mvp.Row2.Xyz);
        _shader.SetMatrix3("uNormalMat", ref normalMat);

        if (_count > 0)
        {
            _shader.SetVector3("uColor", color);
            GL.BindVertexArray(_vao);
            GL.DrawArrays(PrimitiveType.Triangles, 0, _count);
            GL.BindVertexArray(0);
        }

        if (_selCount > 0)
        {
            _shader.SetVector3("uColor", selectedColor);
            GL.BindVertexArray(_selVao);
            GL.DrawArrays(PrimitiveType.Triangles, 0, _selCount);
            GL.BindVertexArray(0);
        }
    }

    public void Dispose()
    {
        if (_vao != 0) { GL.DeleteVertexArray(_vao); GL.DeleteBuffer(_vbo); }
        if (_selVao != 0) { GL.DeleteVertexArray(_selVao); GL.DeleteBuffer(_selVbo); }
    }

    private static void AppendSphere(List<float> verts, Vector3 center, float r, int slices, int stacks)
    {
        for (int i = 0; i < stacks; i++)
        {
            float v0 = MathF.PI * i / stacks;
            float v1 = MathF.PI * (i + 1) / stacks;
            for (int j = 0; j < slices; j++)
            {
                float u0 = 2f * MathF.PI * j / slices;
                float u1 = 2f * MathF.PI * (j + 1) / slices;
                var p00 = SpherePoint(center, r, u0, v0);
                var p10 = SpherePoint(center, r, u1, v0);
                var p01 = SpherePoint(center, r, u0, v1);
                var p11 = SpherePoint(center, r, u1, v1);
                AddTri(verts, p00, p10, p11);
                AddTri(verts, p00, p11, p01);
            }
        }
    }

    private static Vector3 SpherePoint(Vector3 c, float r, float u, float v)
    {
        float sinV = MathF.Sin(v);
        return c + new Vector3(sinV * MathF.Cos(u), sinV * MathF.Sin(u), MathF.Cos(v)) * r;
    }

    private static void AddTri(List<float> verts, Vector3 a, Vector3 b, Vector3 c)
    {
        var n = Vector3.Normalize(Vector3.Cross(b - a, c - a));
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