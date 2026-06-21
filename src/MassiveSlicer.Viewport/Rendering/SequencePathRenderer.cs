using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Viewport.Rendering;

/// <summary>Draws tool-change sequence paths, waypoint markers, and a travelling marker.</summary>
public sealed class SequencePathRenderer : IDisposable
{
    static readonly Vector3 LinOn  = new(0.35f, 0.98f, 0.55f);
    static readonly Vector3 LinOff = new(0.18f, 0.55f, 0.32f);
    static readonly Vector3 PtpOn  = new(0.58f, 0.68f, 1.00f);
    static readonly Vector3 PtpOff = new(0.28f, 0.32f, 0.72f);

    const float PathHalfWidthMm = 3.5f;

    const string VertSrc = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aColor;
        uniform mat4 uMVP;
        out vec3 vColor;
        void main()
        {
            vColor = aColor;
            gl_Position = vec4(aPos, 1.0) * uMVP;
        }
        """;

    const string FragSrc = """
        #version 330 core
        in vec3 vColor;
        out vec4 fragColor;
        void main()
        {
            fragColor = vec4(vColor, 1.0);
        }
        """;

    readonly Shader _lineShader = new(VertSrc, FragSrc);
    readonly SeamGuideRenderer _travelMarker = new();
    readonly SeamGuideRenderer _waypointMarkers = new();
    int _vao, _vbo, _triCount;
    Vector3 _markerPos;
    bool _hasMarker;
    IReadOnlyList<Vector3> _waypointWorld = [];
    bool _waypointsDirty = true;

    public void SetWaypointMarkers(IReadOnlyList<Vector3> worldPositions, int selectedIndex = -1)
    {
        _waypointWorld = worldPositions;
        _waypointSelectedIndex = selectedIndex;
        _waypointsDirty = true;
    }

    int _waypointSelectedIndex = -1;

    public void UpdateProgress(
        IReadOnlyList<Vector3> denseRobroot,
        Vector3 robrootOffset,
        IReadOnlyList<float> cum,
        IReadOnlyList<KrlMoveKind> segMove,
        float progress,
        Vector3 markerRobroot)
    {
        _hasMarker = true;
        _markerPos = markerRobroot + robrootOffset;

        if (_vao != 0) { GL.DeleteVertexArray(_vao); GL.DeleteBuffer(_vbo); }
        _vao = _vbo = 0;
        _triCount = 0;
        if (denseRobroot.Count < 2 || segMove.Count == 0) return;

        float pos = progress * cum[^1];
        var verts = new List<float>(denseRobroot.Count * 36);
        for (int i = 0; i < denseRobroot.Count - 1; i++)
        {
            var a = denseRobroot[i] + robrootOffset;
            var b = denseRobroot[i + 1] + robrootOffset;
            var mv = segMove[Math.Min(i, segMove.Count - 1)];
            var ca = SegmentColor(cum[i], pos, mv);
            var cb = SegmentColor(cum[i + 1], pos, mv);
             var c = (ca + cb) * 0.5f;
            AppendSegmentQuad(verts, a, b, c, PathHalfWidthMm);
        }

        if (verts.Count == 0) return;

        _triCount = verts.Count / 6;
        _vao = GL.GenVertexArray();
        _vbo = GL.GenBuffer();
        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, verts.Count * sizeof(float), verts.ToArray(), BufferUsageHint.DynamicDraw);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), 0);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), 3 * sizeof(float));
        GL.EnableVertexAttribArray(1);
        GL.BindVertexArray(0);

        _travelMarker.Update([_markerPos], selectedIndex: 0, radius: 40f, selectedRadius: 40f);
    }

    static Vector3 SegmentColor(float cumDist, float pos, KrlMoveKind move)
        => cumDist <= pos
            ? (move == KrlMoveKind.Lin ? LinOn : PtpOn)
            : (move == KrlMoveKind.Lin ? LinOff : PtpOff);

    static void AppendSegmentQuad(List<float> verts, Vector3 a, Vector3 b, Vector3 color, float halfWidth)
    {
        var dir = b - a;
        if (dir.LengthSquared < 1e-4f) return;
        dir = Vector3.Normalize(dir);

        var perp = Vector3.Cross(dir, Vector3.UnitZ);
        if (perp.LengthSquared < 1e-4f)
            perp = Vector3.Cross(dir, Vector3.UnitY);
        perp = Vector3.Normalize(perp) * halfWidth;

        var a0 = a - perp;
        var a1 = a + perp;
        var b0 = b - perp;
        var b1 = b + perp;

        WriteTri(verts, a0, a1, b1, color);
        WriteTri(verts, a0, b1, b0, color);
    }

    static void WriteTri(List<float> verts, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 color)
    {
        WriteVert(verts, p0, color);
        WriteVert(verts, p1, color);
        WriteVert(verts, p2, color);
    }

    static void WriteVert(List<float> verts, Vector3 p, Vector3 c)
    {
        verts.Add(p.X); verts.Add(p.Y); verts.Add(p.Z);
        verts.Add(c.X); verts.Add(c.Y); verts.Add(c.Z);
    }

    public void Clear()
    {
        _hasMarker = false;
        _waypointWorld = [];
        _waypointsDirty = true;
        if (_vao != 0) { GL.DeleteVertexArray(_vao); GL.DeleteBuffer(_vbo); }
        _vao = _vbo = 0;
        _triCount = 0;
        _travelMarker.Update([]);
        _waypointMarkers.Update([]);
    }

    public void Draw(Matrix4 mvp)
    {
        if (_waypointsDirty)
        {
            _waypointMarkers.Update(_waypointWorld, _waypointSelectedIndex, radius: 28f, selectedRadius: 40f);
            _waypointsDirty = false;
        }

        if (_triCount > 0)
        {
            _lineShader.Use();
            _lineShader.SetMatrix4("uMVP", ref mvp);
            GL.Disable(EnableCap.CullFace);
            GL.BindVertexArray(_vao);
            GL.DrawArrays(PrimitiveType.Triangles, 0, _triCount);
            GL.BindVertexArray(0);
            GL.Enable(EnableCap.CullFace);
        }

        if (_waypointWorld.Count > 0)
            _waypointMarkers.Draw(mvp, new Vector3(0.98f, 0.98f, 0.98f), new Vector3(0.35f, 0.85f, 0.95f));

        if (_hasMarker)
            _travelMarker.Draw(mvp, new Vector3(0.99f, 0.88f, 0.28f), new Vector3(0.99f, 0.88f, 0.28f));
    }

    public void Dispose()
    {
        Clear();
        _travelMarker.Dispose();
        _waypointMarkers.Dispose();
        _lineShader.Dispose();
    }
}