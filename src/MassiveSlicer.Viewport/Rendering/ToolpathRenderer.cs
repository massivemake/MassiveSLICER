using System;
using MassiveSlicer.Core.Models;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using NVec3 = System.Numerics.Vector3;

namespace MassiveSlicer.Viewport.Rendering;

/// <summary>
/// Draws a sliced <see cref="Toolpath"/> as coloured line segments.
///
/// <list type="bullet">
///   <item><description><b>Unselected</b> -- extrude moves only, drawn in uniform gray; travel moves and seam points hidden.</description></item>
///   <item><description><b>Selected</b> -- all moves with per-vertex colour (extrude=blue, travel=green) plus yellow/red seam points.</description></item>
/// </list>
///
/// Depth test is disabled so lines are never occluded by mesh geometry.
/// </summary>
public sealed class ToolpathRenderer : IDisposable
{
    // Separate VAOs per category so each can be toggled independently.
    private int  _extrudeVao, _extrudeVbo, _extrudeCount;
    private int  _travelVao,  _travelVbo,  _travelCount;
    private int  _ptVao, _ptVbo;
    private int  _pointCount;
    private int  _beadVao, _beadVbo, _beadCount;
    private int  _beadOverhangVao, _beadOverhangVbo, _beadOverhangCount;
    private int  _orientationVao, _orientationVbo, _orientationCount;
    private int  _singularityPtVao, _singularityPtVbo, _singularityPointCount;
    private int[] _singularityVertexCumulative = [];
    private bool _disposed;

    private readonly Shader _shader;
    private readonly Shader _beadShader;
    private Vector3 _beadMaterialColor;

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
        uniform float uOverride;       // 1 = use uOverrideColor; 0 = per-vertex
        uniform vec3  uOverrideColor;
        out vec4 fragColor;
        void main() {
            fragColor = vec4(uOverride > 0.5 ? uOverrideColor : vColor, 1.0);
        }
        """;

    private static readonly string BeadVertSrc = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNormal;
        uniform mat4 uMVP;
        out vec3 vNormal;
        void main() {
            gl_Position = vec4(aPos, 1.0) * uMVP;
            vNormal = aNormal;
        }
        """;

    private static readonly string BeadFragSrc = """
        #version 330 core
        in vec3 vNormal;
        uniform vec3 uColor;
        out vec4 fragColor;
        void main() {
            vec3 L = normalize(vec3(0.6, 0.4, 1.0));
            vec3 n = normalize(vNormal);
            float d = max(dot(n, L), 0.0);
            float fill = max(dot(n, vec3(-0.3, -0.2, -0.7)), 0.0) * 0.15;
            float light = 0.20 + d * 0.72 + fill;
            fragColor = vec4(uColor * light, 1.0);
        }
        """;

    private static readonly Vector3 UnreachableColor = new(0.9f, 0.18f, 0.1f);

    private Vector3 _extrudeColor     = new(0.1f,  0.45f, 0.9f);
    private Vector3 _millColor        = new(0.95f, 0.6f,  0.1f);
    private Vector3 _travelColor      = new(0.85f, 0.18f, 0.18f);
    private Vector3 _wipeColor        = new(1.0f,  0.53f, 0.0f);
    private Vector3 _retractionColor  = new(0.61f, 0.15f, 0.69f);
    private Vector3 _seamColor        = new(1.0f,  0.9f,  0.0f);
    private Vector3 _unselectedGray   = new(0.38f, 0.38f, 0.38f);

    private float    _beadWidth;
    private float    _beadLayerHeight;
    private Toolpath _toolpath;
    private NVec3    _origin;
    private bool[]?  _reachability;  // per flat-move index; null = all reachable

    // Prefix-sum arrays: cumulative[i] = total VBO vertices for the first i flat moves.
    // Index 0 = 0 (nothing drawn), index _totalMoveCount = full count.
    private int   _totalMoveCount;
    private int[] _extrudeVertexCumulative = [];
    private int[] _travelVertexCumulative  = [];
    private int[] _beadVertexCumulative    = [];
    private int[] _seamVertexCumulative    = [];

    public ToolpathRenderer(Toolpath toolpath, NVec3 origin = default,
        float beadWidth = 6f, float layerHeight = 3f, NVec3 materialColor = default)
    {
        _toolpath   = toolpath;
        _origin     = origin;
        _shader     = new Shader(VertSrc,     FragSrc);
        _beadShader = new Shader(BeadVertSrc, BeadFragSrc);
        Upload(toolpath, origin);
        UploadBead(toolpath, origin, beadWidth, layerHeight,
            materialColor == NVec3.Zero ? new NVec3(0.1f, 0.45f, 0.9f) : materialColor);
    }

    /// <summary>
    /// Re-uploads the extrude VBO with per-move reachability colours.
    /// <paramref name="reachable"/>[i] == false colours move i red. Must be called on the GL thread.
    /// </summary>
    public void UpdateReachability(bool[] reachable)
    {
        _reachability = reachable;
        if (_extrudeVao != 0) { GL.DeleteVertexArray(_extrudeVao); GL.DeleteBuffer(_extrudeVbo); }
        _extrudeVao = _extrudeVbo = _extrudeCount = 0;
        var extData = BuildExtrudeData();
        if (extData.Length > 0)
        {
            (_extrudeVao, _extrudeVbo) = BuildVao(extData);
            _extrudeCount = extData.Length / 6;
        }
    }

    /// <summary>
    /// Updates toolpath line colours and rebuilds affected VBOs. Must be called on the GL thread.
    /// </summary>
    public void UpdateColors(Vector3 extrude, Vector3 travel, Vector3 seam, Vector3 unselected,
        Vector3 wipe, Vector3 retraction)
    {
        bool vbosDirty = _extrudeColor != extrude || _travelColor != travel || _seamColor != seam
                      || _wipeColor != wipe || _retractionColor != retraction;
        _extrudeColor     = extrude;
        _travelColor      = travel;
        _seamColor        = seam;
        _unselectedGray   = unselected;
        _wipeColor        = wipe;
        _retractionColor  = retraction;
        if (vbosDirty) RebuildLineVbos();
    }

    private void RebuildLineVbos()
    {
        if (_extrudeVao != 0) { GL.DeleteVertexArray(_extrudeVao); GL.DeleteBuffer(_extrudeVbo); }
        _extrudeVao = _extrudeVbo = _extrudeCount = 0;
        var extData = BuildExtrudeData();
        if (extData.Length > 0) { (_extrudeVao, _extrudeVbo) = BuildVao(extData); _extrudeCount = extData.Length / 6; }

        if (_travelVao != 0) { GL.DeleteVertexArray(_travelVao); GL.DeleteBuffer(_travelVbo); }
        _travelVao = _travelVbo = _travelCount = 0;
        var trData = BuildTravelData();
        if (trData.Length > 0) { (_travelVao, _travelVbo) = BuildVao(trData); _travelCount = trData.Length / 6; }

        if (_ptVao != 0) { GL.DeleteVertexArray(_ptVao); GL.DeleteBuffer(_ptVbo); }
        _ptVao = _ptVbo = _pointCount = 0;
        var ptData = BuildSeamData();
        _pointCount = ptData.Length / 6;
        if (_pointCount > 0) (_ptVao, _ptVbo) = BuildVao(ptData);
    }

    private float[] BuildExtrudeData()
    {
        int extrudeCount = 0;
        foreach (var layer in _toolpath.Layers)
            foreach (var move in layer.Moves)
                if (move.Kind is MoveKind.Extrude or MoveKind.Mill) extrudeCount++;

        var extData = new float[extrudeCount * 2 * 6];
        int ei = 0, mi = 0;

        void WriteVert(NVec3 p, Vector3 c)
        {
            extData[ei++] = p.X - _origin.X; extData[ei++] = p.Y - _origin.Y; extData[ei++] = p.Z - _origin.Z;
            extData[ei++] = c.X;             extData[ei++] = c.Y;             extData[ei++] = c.Z;
        }

        foreach (var layer in _toolpath.Layers)
        {
            foreach (var move in layer.Moves)
            {
                if (move.Kind is MoveKind.Extrude or MoveKind.Mill)
                {
                    Vector3 color;
                    if (_reachability is not null && mi < _reachability.Length && !_reachability[mi])
                        color = UnreachableColor;
                    else if (move.Kind == MoveKind.Mill)
                        color = _millColor;
                    else if (move.IsWipe)
                        color = _wipeColor;
                    else
                        color = _extrudeColor;
                    WriteVert(move.From, color);
                    WriteVert(move.To,   color);
                }
                mi++;
            }
        }
        return extData;
    }

    /// <summary>Creates and populates a VAO+VBO pair. Both handles are returned.</summary>
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

    /// <summary>
    /// Builds prefix-sum arrays so Draw() can clamp each VBO to a scrub index.
    /// Called once during Upload(); safe to skip on color/reachability rebuilds
    /// because the move structure never changes after construction.
    /// </summary>
    private void ComputeMovePrefixSums()
    {
        int total = 0;
        foreach (var layer in _toolpath.Layers)
            total += layer.Moves.Count;
        _totalMoveCount = total;

        _extrudeVertexCumulative = new int[total + 1];
        _travelVertexCumulative  = new int[total + 1];

        int ei = 0, ti = 0, fi = 0;
        foreach (var layer in _toolpath.Layers)
        {
            foreach (var move in layer.Moves)
            {
                if (move.Kind == MoveKind.Extrude) ei += 2;
                else                               ti += 2;
                fi++;
                _extrudeVertexCumulative[fi] = ei;
                _travelVertexCumulative[fi]  = ti;
            }
        }
    }

    /// <summary>
    /// Builds the bead prefix-sum array by mirroring UploadBead's contour logic
    /// without emitting any vertex data. Must be called after ComputeMovePrefixSums.
    /// </summary>
    private void BuildBeadVertexCumulative()
    {
        _beadVertexCumulative = new int[_totalMoveCount + 1];
        int cumulative = 0;

        var contourFlatIndices = new List<List<int>>();
        int flatIdx = 0;
        foreach (var layer in _toolpath.Layers)
        {
            List<int>? cur = null;
            foreach (var move in layer.Moves)
            {
                if (move.Kind == MoveKind.Extrude)
                {
                    if (cur is null) { cur = new List<int>(); contourFlatIndices.Add(cur); }
                    cur.Add(flatIdx);
                }
                else cur = null;
                flatIdx++;
            }
        }

        foreach (var contour in contourFlatIndices)
        {
            int n = contour.Count;
            for (int i = 0; i < n; i++)
            {
                if (i == 0)     cumulative += 6;   // back cap  (1 Quad = 6 verts)
                cumulative += 24;                   // 4 side Quads = 4×6 = 24 verts
                if (i == n - 1) cumulative += 6;   // front cap
                _beadVertexCumulative[contour[i] + 1] = cumulative;
            }
        }

        // Propagate for non-extrude moves so every index has the correct value.
        for (int i = 1; i <= _totalMoveCount; i++)
            if (_beadVertexCumulative[i] == 0)
                _beadVertexCumulative[i] = _beadVertexCumulative[i - 1];
    }

    private void Upload(Toolpath toolpath, NVec3 origin)
    {
        ComputeMovePrefixSums();
        var extData = BuildExtrudeData();
        if (extData.Length > 0) { (_extrudeVao, _extrudeVbo) = BuildVao(extData); _extrudeCount = extData.Length / 6; }

        var trData = BuildTravelData();
        if (trData.Length > 0) { (_travelVao, _travelVbo) = BuildVao(trData); _travelCount = trData.Length / 6; }

        var ptData = BuildSeamData();
        _pointCount = ptData.Length / 6;
        if (_pointCount > 0) (_ptVao, _ptVbo) = BuildVao(ptData);
    }

    private float[] BuildTravelData()
    {
        int travelCount = 0;
        foreach (var layer in _toolpath.Layers)
            foreach (var move in layer.Moves)
                if (move.Kind == MoveKind.Travel) travelCount++;

        var trData = new float[travelCount * 2 * 6];
        int ti = 0;

        void WriteTr(NVec3 p, Vector3 c)
        {
            trData[ti++] = p.X - _origin.X; trData[ti++] = p.Y - _origin.Y; trData[ti++] = p.Z - _origin.Z;
            trData[ti++] = c.X;             trData[ti++] = c.Y;             trData[ti++] = c.Z;
        }

        foreach (var layer in _toolpath.Layers)
            foreach (var move in layer.Moves)
                if (move.Kind == MoveKind.Travel)
                {
                    var color = move.IsZHop ? _retractionColor : _travelColor;
                    WriteTr(move.From, color);
                    WriteTr(move.To,   color);
                }

        return trData;
    }

    private float[] BuildSeamData()
    {
        // Collect one event per seam point, keyed by the flat-move index that makes it visible.
        // Start seam → triggered by the first extrude move of a contour.
        // End seam   → triggered by the last  extrude move of a contour.
        var events = new List<(int FlatIdx, NVec3 Pos)>();

        int fi = 0;
        foreach (var layer in _toolpath.Layers)
        {
            int   firstFi  = -1;
            NVec3 firstPos = default;
            int   lastFi   = -1;
            NVec3 lastPos  = default;

            foreach (var move in layer.Moves)
            {
                if (move.Kind == MoveKind.Extrude)
                {
                    if (firstFi < 0) { firstFi = fi; firstPos = move.From; }
                    lastFi = fi; lastPos = move.To;
                }
                else if (firstFi >= 0)
                {
                    events.Add((firstFi, firstPos));
                    events.Add((lastFi,  lastPos));
                    firstFi = -1;
                }
                fi++;
            }
            if (firstFi >= 0)
            {
                events.Add((firstFi, firstPos));
                events.Add((lastFi,  lastPos));
            }
        }

        // Sort so VBO entries are ordered by appearance time.
        events.Sort((a, b) => a.FlatIdx.CompareTo(b.FlatIdx));

        // Build VBO.
        var ptData = new float[events.Count * 6];
        int pi = 0;
        foreach (var (_, pos) in events)
        {
            ptData[pi++] = pos.X - _origin.X; ptData[pi++] = pos.Y - _origin.Y; ptData[pi++] = pos.Z - _origin.Z;
            ptData[pi++] = _seamColor.X;      ptData[pi++] = _seamColor.Y;      ptData[pi++] = _seamColor.Z;
        }

        // Build prefix-sum: _seamVertexCumulative[i] = seam points visible after i flat moves.
        // An event at FlatIdx fi becomes visible once move fi has been drawn (i.e. at cumulative[fi+1]).
        _seamVertexCumulative = new int[_totalMoveCount + 1];
        int ei = 0;
        for (int i = 1; i <= _totalMoveCount; i++)
        {
            _seamVertexCumulative[i] = _seamVertexCumulative[i - 1];
            while (ei < events.Count && events[ei].FlatIdx < i)
            {
                _seamVertexCumulative[i]++;
                ei++;
            }
        }

        return ptData;
    }

    private void UploadBead(Toolpath toolpath, NVec3 origin,
        float beadWidth, float layerHeight, NVec3 matColor)
    {
        _beadMaterialColor = new Vector3(matColor.X, matColor.Y, matColor.Z);
        _beadWidth       = beadWidth;
        _beadLayerHeight = layerHeight;

        int extrudeCount = 0;
        foreach (var layer in toolpath.Layers)
            foreach (var move in layer.Moves)
                if (move.Kind == MoveKind.Extrude) extrudeCount++;
        if (extrudeCount == 0) return;

        float hw = beadWidth * 0.5f;
        var   up = NVec3.UnitZ;

        // 4 side quads + 2 caps = 12 tris = 36 verts per segment (upper bound)
        var data = new float[extrudeCount * 36 * 6];
        int di = 0;

        void WV(NVec3 p, NVec3 n)
        {
            data[di++] = p.X - origin.X; data[di++] = p.Y - origin.Y; data[di++] = p.Z - origin.Z;
            data[di++] = n.X;            data[di++] = n.Y;            data[di++] = n.Z;
        }

        void Quad(NVec3 p0, NVec3 n0, NVec3 p1, NVec3 n1,
                  NVec3 p2, NVec3 n2, NVec3 p3, NVec3 n3)
        {
            WV(p0, n0); WV(p1, n1); WV(p2, n2);
            WV(p0, n0); WV(p2, n2); WV(p3, n3);
        }

        // Side normals: blend of adjacent face normals only (no fwd component).
        // This makes normals identical on both sides of a junction regardless of
        // how the segment direction changes, eliminating shading seams.
        static (NVec3 lb, NVec3 rb, NVec3 lt, NVec3 rt) SideNormals(NVec3 r) => (
            NVec3.Normalize(-r - NVec3.UnitZ), NVec3.Normalize( r - NVec3.UnitZ),
            NVec3.Normalize(-r + NVec3.UnitZ), NVec3.Normalize( r + NVec3.UnitZ));

        // hh is per-contour (layer height can vary with adaptive slicing).
        static (NVec3 lb, NVec3 rb, NVec3 lt, NVec3 rt) Corners(NVec3 pt, NVec3 r, float hw, float hh, NVec3 up)
            => (pt - r*hw - up*hh, pt + r*hw - up*hh,
                pt - r*hw + up*hh, pt + r*hw + up*hh);

        // Group consecutive extrude moves within each layer into contours.
        // Each contour stores its half-height so adaptive layers use the correct bead size.
        // Caps are only emitted at contour start/end; interior junctions get blended normals.
        var contours = new List<(List<ToolpathMove> moves, float hh)>();
        foreach (var layer in toolpath.Layers)
        {
            float lh  = layer.Height > 0f ? layer.Height : layerHeight;
            float lhh = lh * 0.5f;
            List<ToolpathMove>? cur = null;
            foreach (var move in layer.Moves)
            {
                if (move.Kind == MoveKind.Extrude)
                {
                    if (cur is null) { cur = []; contours.Add((cur, lhh)); }
                    cur.Add(move);
                }
                else cur = null;
            }
        }

        foreach (var (contour, hh) in contours)
        {
            int n = contour.Count;
            if (n == 0) continue;

            // Per-segment forward and right vectors.
            var fwds   = new NVec3[n];
            var rights = new NVec3[n];
            for (int i = 0; i < n; i++)
            {
                var d = contour[i].To - contour[i].From;
                fwds[i] = d.LengthSquared() > 1e-12f
                    ? NVec3.Normalize(d)
                    : (i > 0 ? fwds[i - 1] : NVec3.UnitX);
                var r = NVec3.Cross(fwds[i], up);
                if (r.LengthSquared() < 1e-6f) r = NVec3.Cross(fwds[i], NVec3.UnitX);
                rights[i] = NVec3.Normalize(r);
            }

            // Cross-section right vectors: averaged at interior junctions.
            // cs[0] = contour start, cs[i] = junction between seg i-1 and seg i, cs[n] = contour end.
            var csR = new NVec3[n + 1];
            csR[0] = rights[0];
            for (int i = 1; i < n; i++) csR[i] = NVec3.Normalize(rights[i - 1] + rights[i]);
            csR[n] = rights[n - 1];

            // Back cap (flat normal = -fwd of first segment).
            {
                var (lb, rb, lt, rt) = Corners(contour[0].From, csR[0], hw, hh, up);
                var cn = -fwds[0];
                Quad(rb, cn, lb, cn, lt, cn, rt, cn);
            }

            // Four side quads per segment, using blended cross-section normals.
            for (int i = 0; i < n; i++)
            {
                var (lbA, rbA, ltA, rtA) = Corners(contour[i].From, csR[i],     hw, hh, up);
                var (lbB, rbB, ltB, rtB) = Corners(contour[i].To,   csR[i + 1], hw, hh, up);
                var (nLbA, nRbA, nLtA, nRtA) = SideNormals(csR[i]);
                var (nLbB, nRbB, nLtB, nRtB) = SideNormals(csR[i + 1]);

                Quad(ltA, nLtA, rtA, nRtA, rtB, nRtB, ltB, nLtB);  // top
                Quad(rbA, nRbA, lbA, nLbA, lbB, nLbB, rbB, nRbB);  // bottom
                Quad(lbA, nLbA, ltA, nLtA, ltB, nLtB, lbB, nLbB);  // left
                Quad(rtA, nRtA, rbA, nRbA, rbB, nRbB, rtB, nRtB);  // right
            }

            // Front cap (flat normal = +fwd of last segment).
            {
                var (lb, rb, lt, rt) = Corners(contour[n - 1].To, csR[n], hw, hh, up);
                var cn = fwds[n - 1];
                Quad(lb, cn, lt, cn, rt, cn, rb, cn);
            }
        }

        _beadCount = di / 6;
        if (_beadCount > 0)
        {
            float[] upload = di == data.Length ? data : data[..di];
            (_beadVao, _beadVbo) = BuildVao(upload);
        }
        BuildBeadVertexCumulative();
    }

    /// <summary>
    /// Builds or rebuilds the bead-overhang VAO. Each segment is coloured white→red
    /// by its overhang value (0 = fully supported, 1 = fully unsupported).
    /// Must be called on the GL thread.
    /// </summary>
    public void UpdateBeadOverhang(float[] scoresPerFlatMove)
    {
        if (_beadOverhangVao != 0) { GL.DeleteVertexArray(_beadOverhangVao); GL.DeleteBuffer(_beadOverhangVbo); }
        _beadOverhangVao = _beadOverhangVbo = _beadOverhangCount = 0;
        var data = BuildBeadColoredData(scoresPerFlatMove, t => new NVec3(1f, 1f - t, 1f - t));
        if (data.Length > 0) { (_beadOverhangVao, _beadOverhangVbo) = BuildVao(data); _beadOverhangCount = data.Length / 6; }
    }

    // Stops normalised to [0,1] where 1.0 = 3 °/mm (matches maxDegPerMm in the compute pass).
    // deg/mm:  0.0     0.25    0.5     0.75    1.0     1.5     2.0     3.0+
    private static readonly (float t, float r, float g, float b)[] _orientationStops =
    [
        (0.000f, 0.00f, 0.00f, 0.50f),  // Dark Blue   — Excellent
        (0.083f, 0.00f, 1.00f, 1.00f),  // Cyan        — Very safe
        (0.167f, 0.00f, 0.80f, 0.00f),  // Green       — Safe
        (0.250f, 1.00f, 1.00f, 0.00f),  // Yellow      — Approaching limits
        (0.333f, 1.00f, 0.50f, 0.00f),  // Orange      — Warning
        (0.500f, 1.00f, 0.00f, 0.00f),  // Red         — Significant slowdown
        (0.667f, 1.00f, 0.00f, 1.00f),  // Magenta     — Severe
        (1.000f, 0.50f, 0.00f, 0.80f),  // Purple      — Extreme
    ];

    private static NVec3 OrientationColor(float t)
    {
        var s = _orientationStops;
        if (t <= s[0].t) return new NVec3(s[0].r, s[0].g, s[0].b);
        for (int i = 1; i < s.Length; i++)
        {
            if (t <= s[i].t)
            {
                float f = (t - s[i - 1].t) / (s[i].t - s[i - 1].t);
                return new NVec3(
                    s[i - 1].r + f * (s[i].r - s[i - 1].r),
                    s[i - 1].g + f * (s[i].g - s[i - 1].g),
                    s[i - 1].b + f * (s[i].b - s[i - 1].b));
            }
        }
        return new NVec3(s[^1].r, s[^1].g, s[^1].b);
    }

    public void UpdateBeadOrientation(float[] scoresPerFlatMove)
    {
        if (_orientationVao != 0) { GL.DeleteVertexArray(_orientationVao); GL.DeleteBuffer(_orientationVbo); }
        _orientationVao = _orientationVbo = _orientationCount = 0;
        var data = BuildBeadColoredData(scoresPerFlatMove, OrientationColor);
        if (data.Length > 0) { (_orientationVao, _orientationVbo) = BuildVao(data); _orientationCount = data.Length / 6; }
    }

    private float[] BuildBeadColoredData(float[] scoresPerFlatMove, Func<float, NVec3> colorFromScore)
    {
        float hw = _beadWidth * 0.5f;
        var   up = NVec3.UnitZ;

        int extrudeCount = 0;
        foreach (var layer in _toolpath.Layers)
            foreach (var move in layer.Moves)
                if (move.Kind == MoveKind.Extrude) extrudeCount++;
        if (extrudeCount == 0) return [];

        var data = new float[extrudeCount * 36 * 6];
        int di = 0;

        void WV(NVec3 p, NVec3 c)
        {
            data[di++] = p.X - _origin.X; data[di++] = p.Y - _origin.Y; data[di++] = p.Z - _origin.Z;
            data[di++] = c.X;             data[di++] = c.Y;             data[di++] = c.Z;
        }

        void Quad(NVec3 p0, NVec3 p1, NVec3 p2, NVec3 p3, NVec3 col)
        {
            WV(p0, col); WV(p1, col); WV(p2, col);
            WV(p0, col); WV(p2, col); WV(p3, col);
        }

        static (NVec3 lb, NVec3 rb, NVec3 lt, NVec3 rt) Corners(NVec3 pt, NVec3 r, float hw, float hh, NVec3 up)
            => (pt - r*hw - up*hh, pt + r*hw - up*hh,
                pt - r*hw + up*hh, pt + r*hw + up*hh);

        // Group consecutive extrude moves into contours, tracking flat move indices and layer height.
        var contours = new List<(List<(ToolpathMove move, int flatIdx)> moves, float hh)>();
        int flatIdx = 0;
        foreach (var layer in _toolpath.Layers)
        {
            float lh  = layer.Height > 0f ? layer.Height : _beadLayerHeight;
            float lhh = lh * 0.5f;
            List<(ToolpathMove, int)>? cur = null;
            foreach (var move in layer.Moves)
            {
                if (move.Kind == MoveKind.Extrude)
                {
                    if (cur is null) { cur = []; contours.Add((cur, lhh)); }
                    cur.Add((move, flatIdx));
                }
                else cur = null;
                flatIdx++;
            }
        }

        foreach (var (contour, hh) in contours)
        {
            int n = contour.Count;
            if (n == 0) continue;

            // Per-segment forward and right vectors (same as UploadBead).
            var fwds   = new NVec3[n];
            var rights = new NVec3[n];
            for (int i = 0; i < n; i++)
            {
                var d = contour[i].move.To - contour[i].move.From;
                fwds[i] = d.LengthSquared() > 1e-12f
                    ? NVec3.Normalize(d)
                    : (i > 0 ? fwds[i - 1] : NVec3.UnitX);
                var r = NVec3.Cross(fwds[i], up);
                if (r.LengthSquared() < 1e-6f) r = NVec3.Cross(fwds[i], NVec3.UnitX);
                rights[i] = NVec3.Normalize(r);
            }

            var csR = new NVec3[n + 1];
            csR[0] = rights[0];
            for (int i = 1; i < n; i++) csR[i] = NVec3.Normalize(rights[i - 1] + rights[i]);
            csR[n] = rights[n - 1];

            NVec3 ColorAt(int fi)
            {
                float t = fi >= 0 && fi < scoresPerFlatMove.Length ? scoresPerFlatMove[fi] : 0f;
                return colorFromScore(t);
            }

            // Back cap.
            {
                var c = ColorAt(contour[0].flatIdx);
                var (lb, rb, lt, rt) = Corners(contour[0].move.From, csR[0], hw, hh, up);
                Quad(rb, lb, lt, rt, c);
            }

            // Four side quads per segment.
            for (int i = 0; i < n; i++)
            {
                var c = ColorAt(contour[i].flatIdx);
                var (lbA, rbA, ltA, rtA) = Corners(contour[i].move.From, csR[i],     hw, hh, up);
                var (lbB, rbB, ltB, rtB) = Corners(contour[i].move.To,   csR[i + 1], hw, hh, up);
                Quad(ltA, rtA, rtB, ltB, c); // top
                Quad(rbA, lbA, lbB, rbB, c); // bottom
                Quad(lbA, ltA, ltB, lbB, c); // left
                Quad(rtA, rbA, rbB, rtB, c); // right
            }

            // Front cap.
            {
                var c = ColorAt(contour[n - 1].flatIdx);
                var (lb, rb, lt, rt) = Corners(contour[n - 1].move.To, csR[n], hw, hh, up);
                Quad(lb, lt, rt, rb, c);
            }
        }

        return di == data.Length ? data : data[..di];
    }

    // Returns the number of VBO vertices to draw when the scrubber is at `scrubIndex`.
    // scrubIndex == int.MaxValue (default) means show everything.
    private int ScrubCount(int[] cumulative, int totalCount, int scrubIndex)
    {
        if (scrubIndex >= _totalMoveCount || _totalMoveCount == 0) return totalCount;
        if (scrubIndex <= 0) return 0;
        return cumulative[scrubIndex];
    }

    public void Draw(Matrix4 mvp, bool selected = false,
                     bool showExtrusion = true, bool showTravel = true, bool showSeam = true,
                     bool showBead = false, bool showBeadOverhang = false,
                     bool showOrientationPreview = false, int scrubIndex = int.MaxValue)
    {
        if (_disposed) return;

        _shader.Use();
        _shader.SetMatrix4("uMVP", ref mvp);

        int extCount   = ScrubCount(_extrudeVertexCumulative, _extrudeCount, scrubIndex);
        int trCount    = ScrubCount(_travelVertexCumulative,  _travelCount,  scrubIndex);
        int beadCount  = ScrubCount(_beadVertexCumulative,    _beadCount,    scrubIndex);
        int seamCount  = ScrubCount(_seamVertexCumulative,    _pointCount,   scrubIndex);

        if (!selected)
        {
            if (showExtrusion && extCount > 0)
            {
                _shader.SetFloat("uOverride", 1f);
                _shader.SetVector3("uOverrideColor", _unselectedGray);
                GL.BindVertexArray(_extrudeVao);
                GL.DrawArrays(PrimitiveType.Lines, 0, extCount);
            }
        }
        else
        {
            _shader.SetFloat("uOverride", 0f);

            if (showExtrusion && extCount > 0)
            {
                GL.BindVertexArray(_extrudeVao);
                GL.DrawArrays(PrimitiveType.Lines, 0, extCount);
            }

            if (showTravel && trCount > 0)
            {
                GL.BindVertexArray(_travelVao);
                GL.DrawArrays(PrimitiveType.Lines, 0, trCount);
            }

            if (showSeam && seamCount > 0)
            {
                GL.PointSize(8f);
                GL.BindVertexArray(_ptVao);
                GL.DrawArrays(PrimitiveType.Points, 0, seamCount);
                GL.PointSize(1f);
            }

            if (_singularityPtVao != 0)
            {
                int singCount = ScrubCount(_singularityVertexCumulative, _singularityPointCount, scrubIndex);
                if (singCount > 0)
                {
                    GL.PointSize(8f);
                    GL.BindVertexArray(_singularityPtVao);
                    GL.DrawArrays(PrimitiveType.Points, 0, singCount);
                    GL.PointSize(1f);
                }
            }
        }

        if (showOrientationPreview && _orientationVao != 0 && beadCount > 0)
        {
            _shader.Use();
            _shader.SetMatrix4("uMVP", ref mvp);
            _shader.SetFloat("uOverride", 0f);
            GL.Disable(EnableCap.CullFace);
            GL.BindVertexArray(_orientationVao);
            GL.DrawArrays(PrimitiveType.Triangles, 0, Math.Min(_orientationCount, beadCount));
            GL.Enable(EnableCap.CullFace);
        }
        else if (showBeadOverhang && _beadOverhangVao != 0 && beadCount > 0)
        {
            _shader.Use();
            _shader.SetMatrix4("uMVP", ref mvp);
            _shader.SetFloat("uOverride", 0f);
            GL.Disable(EnableCap.CullFace);
            GL.BindVertexArray(_beadOverhangVao);
            GL.DrawArrays(PrimitiveType.Triangles, 0, Math.Min(_beadOverhangCount, beadCount));
            GL.Enable(EnableCap.CullFace);
        }
        else if (showBead && beadCount > 0)
        {
            _beadShader.Use();
            _beadShader.SetMatrix4("uMVP", ref mvp);
            _beadShader.SetVector3("uColor", _beadMaterialColor);
            GL.Disable(EnableCap.CullFace);
            GL.BindVertexArray(_beadVao);
            GL.DrawArrays(PrimitiveType.Triangles, 0, beadCount);
            GL.Enable(EnableCap.CullFace);
        }

        GL.BindVertexArray(0);
    }

    /// <summary>
    /// Builds or rebuilds the singularity-point VBO from a per-move flag array.
    /// Each flagged move gets a purple GL_POINT at its midpoint. Must be called on the GL thread.
    /// </summary>
    public void UpdateSingularityPoints(bool[] singularity)
    {
        if (_singularityPtVao != 0) { GL.DeleteVertexArray(_singularityPtVao); GL.DeleteBuffer(_singularityPtVbo); }
        _singularityPtVao = _singularityPtVbo = _singularityPointCount = 0;
        _singularityVertexCumulative = new int[_totalMoveCount + 1];

        var events = new List<(int FlatIdx, NVec3 Pos)>();
        int fi = 0;
        foreach (var layer in _toolpath.Layers)
            foreach (var move in layer.Moves)
            {
                if (fi < singularity.Length && singularity[fi])
                    events.Add((fi, (move.From + move.To) * 0.5f));
                fi++;
            }

        if (events.Count > 0)
        {
            var ptData = new float[events.Count * 6];
            int pi = 0;
            var col = new Vector3(0.60f, 0.15f, 0.90f); // purple
            foreach (var (_, pos) in events)
            {
                ptData[pi++] = pos.X - _origin.X; ptData[pi++] = pos.Y - _origin.Y; ptData[pi++] = pos.Z - _origin.Z;
                ptData[pi++] = col.X;              ptData[pi++] = col.Y;              ptData[pi++] = col.Z;
            }
            (_singularityPtVao, _singularityPtVbo) = BuildVao(ptData);
            _singularityPointCount = events.Count;
        }

        // Prefix sum: singularity point at move fi becomes visible after fi+1 ticks.
        int ei = 0;
        for (int i = 1; i <= _totalMoveCount; i++)
        {
            _singularityVertexCumulative[i] = _singularityVertexCumulative[i - 1];
            while (ei < events.Count && events[ei].FlatIdx < i)
            { _singularityVertexCumulative[i]++; ei++; }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_extrudeVao       != 0) { GL.DeleteVertexArray(_extrudeVao);       GL.DeleteBuffer(_extrudeVbo);       }
        if (_travelVao        != 0) { GL.DeleteVertexArray(_travelVao);        GL.DeleteBuffer(_travelVbo);        }
        if (_ptVao            != 0) { GL.DeleteVertexArray(_ptVao);            GL.DeleteBuffer(_ptVbo);            }
        if (_beadVao          != 0) { GL.DeleteVertexArray(_beadVao);          GL.DeleteBuffer(_beadVbo);          }
        if (_beadOverhangVao  != 0) { GL.DeleteVertexArray(_beadOverhangVao);  GL.DeleteBuffer(_beadOverhangVbo);  }
        if (_orientationVao   != 0) { GL.DeleteVertexArray(_orientationVao);   GL.DeleteBuffer(_orientationVbo);   }
        if (_singularityPtVao != 0) { GL.DeleteVertexArray(_singularityPtVao); GL.DeleteBuffer(_singularityPtVbo); }
        _shader.Dispose();
        _beadShader.Dispose();
    }
}
