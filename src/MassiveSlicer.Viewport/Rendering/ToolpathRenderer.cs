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
/// <summary>How toolpath extrude lines are coloured.</summary>
public enum ToolpathColorMode
{
    /// <summary>Kind-based colours (extrude/travel/wipe/seam).</summary>
    Normal,
    /// <summary>Gradient by effective print speed (blue slow → red fast).</summary>
    Speed,
    /// <summary>Gradient by extrusion rate / RPM demand (blue low → red high).</summary>
    Rpm,
    /// <summary>Gradient by simulated interlayer temperature (blue cold → red hot).</summary>
    Thermal,
}

public sealed class ToolpathRenderer : IDisposable
{
    // Separate VAOs per category so each can be toggled independently.
    private int  _extrudeVao, _extrudeVbo, _extrudeCount;
    private int  _travelVao,  _travelVbo,  _travelCount;
    private int  _lightningVao, _lightningVbo, _lightningCount;
    private int  _ptVao, _ptVbo;
    private int  _pointCount;
    private int  _beadVao, _beadVbo, _beadEbo, _beadCount;   // _beadCount = index count
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
        uniform float uOpacity;        // line transparency (1 = opaque)
        out vec4 fragColor;
        void main() {
            fragColor = vec4(uOverride > 0.5 ? uOverrideColor : vColor, uOpacity);
        }
        """;

    private static readonly string BeadVertSrc = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNormal;
        uniform mat4 uMVP;
        out vec3 vNormal;
        out vec3 vPos;
        void main() {
            gl_Position = vec4(aPos, 1.0) * uMVP;
            vNormal = aNormal;
            vPos    = aPos;
        }
        """;

    // Glossy semi-metallic plastic matched to the Blender "3dp.001" reference
    // (Principled BSDF: roughness 0.21, metallic 0.73, lime base).
    private static readonly string BeadFragSrc = """
        #version 330 core
        in vec3 vNormal;
        in vec3 vPos;
        uniform vec3 uColor;
        uniform vec3 uEye;
        out vec4 fragColor;
        void main() {
            vec3 L = normalize(vec3(0.6, 0.4, 1.0));
            vec3 n = normalize(vNormal);
            vec3 V = normalize(uEye - vPos);
            float d    = max(dot(n, L), 0.0);
            float fill = max(dot(n, vec3(-0.3, -0.2, -0.7)), 0.0) * 0.15;
            float light = 0.22 + d * 0.62 + fill;
            vec3  H     = normalize(L + V);
            float spec  = pow(max(dot(n, H), 0.0), 64.0);
            float fres  = pow(1.0 - max(dot(n, V), 0.0), 4.0);
            vec3  specCol = mix(vec3(1.0), uColor, 0.55);
            vec3  col = uColor * light + specCol * (spec * 0.9 + fres * 0.12);
            fragColor = vec4(col, 1.0);
        }
        """;

    private static readonly Vector3 UnreachableColor = new(0.9f, 0.18f, 0.1f);

    private Vector3 _extrudeColor     = new(1f, 1f, 1f);
    private Vector3 _millColor        = new(0.95f, 0.6f,  0.1f);
    private Vector3 _travelColor      = new(0.85f, 0.18f, 0.18f);
    private Vector3 _wipeColor        = new(1.0f,  0.53f, 0.0f);
    private Vector3 _retractionColor  = new(0.61f, 0.15f, 0.69f);
    private Vector3 _seamColor        = new(1.0f,  0.9f,  0.0f);
    private Vector3 _unselectedGray   = new(0.38f, 0.38f, 0.38f);

    private float    _beadWidth;
    private float    _beadLayerHeight;

    /// <summary>
    /// One entry per contour of the bead mesh: the decimated polyline points (cross-section
    /// centres), the blended cross-section right vectors, the flat move index range covered
    /// by each segment, and the half layer height.  Shared by the bead, overhang and
    /// orientation builders so all three stay geometrically identical.
    /// </summary>
    private sealed class BeadContour
    {
        public required NVec3[] Pts;          // m+1 cross-section centres
        public required NVec3[] CsR;          // m+1 blended right vectors
        public required int[]   SegFirstFlat; // m: first flat move index covered by segment j
        public required int[]   SegLastFlat;  // m: last  flat move index covered by segment j
        public required float   Hh;           // half layer height
    }
    private List<BeadContour> _beadPlan = [];
    private Toolpath _toolpath;
    private NVec3    _origin;
    private bool[]?  _reachability;  // per flat-move index; null = all reachable

    /// <summary>Total flat move count (scrub/simulation range).</summary>
    public int TotalMoveCount => _totalMoveCount;

    // Prefix-sum arrays: cumulative[i] = total VBO vertices for the first i flat moves.
    // Index 0 = 0 (nothing drawn), index _totalMoveCount = full count.
    private int   _totalMoveCount;
    private int[] _extrudeVertexCumulative = [];
    private int[] _travelVertexCumulative  = [];
    private int[] _lightningVertexCumulative = [];
    private static readonly Vector3 LightningColor = new(1.00f, 0.58f, 0.12f);
    private int[] _beadVertexCumulative    = [];
    private int[] _seamVertexCumulative    = [];

    public ToolpathRenderer(Toolpath toolpath, NVec3 origin = default,
        float beadWidth = 6f, float layerHeight = 3f, NVec3 materialColor = default,
        Toolpath? beadToolpath = null)
    {
        _toolpath   = toolpath;
        _origin     = origin;
        _shader     = new Shader(VertSrc,     FragSrc);
        _beadShader = new Shader(BeadVertSrc, BeadFragSrc);
        Upload(toolpath, origin);
        UploadBead(beadToolpath ?? toolpath, origin, beadWidth, layerHeight,
            materialColor == NVec3.Zero ? NVec3.One : materialColor);
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
        if (_lightningVao != 0) { GL.DeleteVertexArray(_lightningVao); GL.DeleteBuffer(_lightningVbo); }
        _lightningVao = _lightningVbo = _lightningCount = 0;
        var lgData = BuildExtrudeData(lightningOnly: true);
        if (lgData.Length > 0)
        {
            (_lightningVao, _lightningVbo) = BuildVao(lgData);
            _lightningCount = lgData.Length / 6;
        }
    }

    /// <summary>
    /// Sets the bead surface colour. Applied as a shader uniform at draw time —
    /// takes effect immediately with no VBO rebuild. Safe to call from any thread.
    /// </summary>
    public void SetBeadColor(Vector3 color) => _beadMaterialColor = color;

    private ToolpathColorMode _colorMode = ToolpathColorMode.Normal;

    /// <summary>Switches the extrude-line colour mode and rebuilds VBOs. GL thread only.</summary>
    /// <summary>
    /// Cavity exclusion: writes alpha-0 (mask-off) fragments over this toolpath's
    /// pixels in the normal-prepass buffer so the composite skips cavity shading
    /// there. Caller binds the normal FBO and sets Lequal/DepthMask(false).
    /// </summary>
    public void DrawCavityPunch(Matrix4 mvp, bool lines, bool bead)
    {
        if (_disposed) return;
        _shader.Use();
        _shader.SetMatrix4("uMVP", ref mvp);
        _shader.SetFloat("uOverride", 1f);
        _shader.SetVector3("uOverrideColor", Vector3.Zero);
        _shader.SetFloat("uOpacity", 0f);           // alpha 0 = cavity mask off

        if (lines && _extrudeVao != 0 && _extrudeCount > 0)
        {
            GL.BindVertexArray(_extrudeVao);
            GL.DrawArrays(PrimitiveType.Lines, 0, _extrudeCount);
        }
        if (bead && _beadVao != 0 && _beadCount > 0)
        {
            GL.Disable(EnableCap.CullFace);
            GL.BindVertexArray(_beadVao);
            GL.DrawElements(PrimitiveType.Triangles, _beadCount, DrawElementsType.UnsignedInt, 0);
            GL.Enable(EnableCap.CullFace);
        }
        GL.BindVertexArray(0);
    }

    /// <summary>Diagnostics: the gradient mode this renderer's VBOs were built with.</summary>
    public ToolpathColorMode ColorMode => _colorMode;

    public void SetColorMode(ToolpathColorMode mode)
    {
        if (_colorMode == mode) return;
        _colorMode = mode;
        RebuildLineVbos();
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

        if (_lightningVao != 0) { GL.DeleteVertexArray(_lightningVao); GL.DeleteBuffer(_lightningVbo); }
        _lightningVao = _lightningVbo = _lightningCount = 0;
        var lgData = BuildExtrudeData(lightningOnly: true);
        if (lgData.Length > 0) { (_lightningVao, _lightningVbo) = BuildVao(lgData); _lightningCount = lgData.Length / 6; }

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

    private float[] BuildExtrudeData(bool lightningOnly = false)
    {
        int extrudeCount = 0;
        foreach (var layer in _toolpath.Layers)
            foreach (var move in layer.Moves)
                if (move.Kind is MoveKind.Extrude or MoveKind.Mill
                    && move.IsLightning == lightningOnly) extrudeCount++;

        var extData = new float[extrudeCount * 2 * 6];
        int ei = 0, mi = 0;

        void WriteVert(NVec3 p, Vector3 c)
        {
            extData[ei++] = p.X - _origin.X; extData[ei++] = p.Y - _origin.Y; extData[ei++] = p.Z - _origin.Z;
            extData[ei++] = c.X;             extData[ei++] = c.Y;             extData[ei++] = c.Z;
        }

        // Speed/RPM gradients: normalise the per-move factor over the whole toolpath.
        float scalarMin = float.MaxValue, scalarMax = float.MinValue;
        if (_colorMode != ToolpathColorMode.Normal)
        {
            foreach (var layer in _toolpath.Layers)
                foreach (var move in layer.Moves)
                    if (move.Kind == MoveKind.Extrude)
                    {
                        float v = MoveScalar(move, layer);
                        if (v < scalarMin) scalarMin = v;
                        if (v > scalarMax) scalarMax = v;
                    }
        }
        float scalarRange = scalarMax - scalarMin;

        foreach (var layer in _toolpath.Layers)
        {
            foreach (var move in layer.Moves)
            {
                if (move.Kind is MoveKind.Extrude or MoveKind.Mill
                    && move.IsLightning == lightningOnly)
                {
                    Vector3 color;
                    if (_reachability is not null && mi < _reachability.Length && !_reachability[mi])
                        color = UnreachableColor;
                    else if (move.Kind == MoveKind.Mill)
                        color = _millColor;
                    else if (_colorMode != ToolpathColorMode.Normal)
                        color = scalarRange < 1e-6f
                            ? GradientColor(0.5f)
                            : GradientColor((MoveScalar(move, layer) - scalarMin) / scalarRange);
                    else if (lightningOnly)
                        color = LightningColor;
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

    /// <summary>Per-move factor for the active gradient mode (relative units — normalised later).</summary>
    private float MoveScalar(ToolpathMove move, ToolpathLayer layer)
    {
        if (_colorMode == ToolpathColorMode.Thermal)
            return float.IsNaN(layer.ThermalTempC) ? 0f : layer.ThermalTempC;
        float speed = move.PrintSpeedScale * (move.IsResumeRamp ? move.ResumeSpeedScale : 1f);
        if (_colorMode == ToolpathColorMode.Speed) return speed;
        // RPM demand ∝ speed · layer height (bead width constant per slice) · ramp/wipe scales.
        float rpm = speed
                  * (move.IsResumeRamp ? move.ResumeRpmScale : 1f)
                  * (move.IsWipe ? move.WipeRpmScale : 1f)
                  * MathF.Max(0.1f, layer.Height);
        return rpm;
    }

    /// <summary>Blue → green → red gradient over t ∈ [0,1].</summary>
    private static Vector3 GradientColor(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        var lo  = new Vector3(0.20f, 0.45f, 1.00f);
        var mid = new Vector3(0.25f, 0.85f, 0.30f);
        var hi  = new Vector3(1.00f, 0.25f, 0.15f);
        return t < 0.5f ? Vector3.Lerp(lo, mid, t * 2f) : Vector3.Lerp(mid, hi, (t - 0.5f) * 2f);
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

        _extrudeVertexCumulative   = new int[total + 1];
        _travelVertexCumulative    = new int[total + 1];
        _lightningVertexCumulative = new int[total + 1];

        int ei = 0, ti = 0, li = 0, fi = 0;
        foreach (var layer in _toolpath.Layers)
        {
            foreach (var move in layer.Moves)
            {
                if (ToolpathMoveKinds.IsCutSegment(move.Kind))
                {
                    if (move.IsLightning) li += 2; else ei += 2;
                }
                else if (ToolpathMoveKinds.IsTravelSegment(move.Kind)) ti += 2;
                fi++;
                _extrudeVertexCumulative[fi]   = ei;
                _travelVertexCumulative[fi]    = ti;
                _lightningVertexCumulative[fi] = li;
            }
        }
    }

    /// <summary>
    /// Builds the bead prefix-sum array (in INDEX counts, matching UploadBead's emission
    /// order: back cap, segments, front cap per contour) from the shared bead plan.
    /// Must be called after ComputeMovePrefixSums.
    /// </summary>
    private void BuildBeadVertexCumulative()
    {
        _beadVertexCumulative = new int[_totalMoveCount + 1];
        int cumulative = 0;

        foreach (var c in _beadPlan)
        {
            int m = c.Pts.Length - 1;   // segments
            for (int j = 0; j < m; j++)
            {
                if (j == 0)     cumulative += 6;   // back cap  (2 tris)
                cumulative += 24;                   // 4 side quads = 8 tris
                if (j == m - 1) cumulative += 6;   // front cap
                _beadVertexCumulative[c.SegLastFlat[j] + 1] = cumulative;
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

        var lgData = BuildExtrudeData(lightningOnly: true);
        if (lgData.Length > 0) { (_lightningVao, _lightningVbo) = BuildVao(lgData); _lightningCount = lgData.Length / 6; }

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

    // Chord-error tolerance for bead decimation: points are dropped only where the
    // path stays within this distance of the straight chord — invisible at bead scale,
    // but preserves wave/curve shape exactly where it matters.
    private const float BeadChordTolerance = 0.35f;
    private const int   MaxBeadSegments    = 1_000_000;

    // Side normals: blend of adjacent face normals only (no fwd component).
    // Identical on both sides of a junction, eliminating shading seams.
    private static (NVec3 lb, NVec3 rb, NVec3 lt, NVec3 rt) SideNormals(NVec3 r) => (
        NVec3.Normalize(-r - NVec3.UnitZ), NVec3.Normalize( r - NVec3.UnitZ),
        NVec3.Normalize(-r + NVec3.UnitZ), NVec3.Normalize( r + NVec3.UnitZ));

    /// <summary>
    /// Builds the shared bead plan: contours of chord-error-decimated polyline points.
    /// Unlike fixed-step decimation, this keeps every point needed to represent curves
    /// (e.g. wave effects) faithfully and merges only visually straight runs.
    /// </summary>
    private void BuildBeadPlan(Toolpath toolpath, float layerHeight)
    {
        // Collect raw contours: positions + flat move indices of consecutive cut runs.
        var raw = new List<(List<NVec3> pts, List<int> flats, float hh)>();
        int flatIdx = 0;
        foreach (var layer in toolpath.Layers)
        {
            float lh  = layer.Height > 0f ? layer.Height : layerHeight;
            float lhh = lh * 0.5f;
            List<NVec3>? pts = null; List<int>? flats = null;
            foreach (var move in layer.Moves)
            {
                if (ToolpathMoveKinds.IsCutSegment(move.Kind))
                {
                    if (pts is null)
                    {
                        pts = [move.From]; flats = [];
                        raw.Add((pts, flats, lhh));
                    }
                    pts.Add(move.To);
                    flats!.Add(flatIdx);
                }
                else { pts = null; flats = null; }
                flatIdx++;
            }
        }

        // Decimate; if over the segment budget, coarsen the tolerance and retry.
        float eps = BeadChordTolerance;
        for (int attempt = 0; ; attempt++)
        {
            _beadPlan = [];
            long totalSegs = 0;
            foreach (var (pts, flats, hh) in raw)
            {
                var keep = DecimatePolyline(pts, eps);
                int m = keep.Count - 1;
                if (m <= 0) continue;
                var cPts  = new NVec3[m + 1];
                var first = new int[m];
                var last  = new int[m];
                for (int j = 0; j <= m; j++) cPts[j] = pts[keep[j]];
                for (int j = 0; j <  m; j++)
                {
                    first[j] = flats[keep[j]];
                    last[j]  = flats[keep[j + 1] - 1];
                }

                // Blended cross-section right vectors (same construction as before).
                var rights = new NVec3[m];
                var up = NVec3.UnitZ;
                for (int j = 0; j < m; j++)
                {
                    var d = cPts[j + 1] - cPts[j];
                    var fwd = d.LengthSquared() > 1e-12f
                        ? NVec3.Normalize(d)
                        : (j > 0 ? NVec3.Normalize(cPts[j] - cPts[j - 1]) : NVec3.UnitX);
                    var r = NVec3.Cross(fwd, up);
                    if (r.LengthSquared() < 1e-6f) r = NVec3.Cross(fwd, NVec3.UnitX);
                    rights[j] = NVec3.Normalize(r);
                }
                var csR = new NVec3[m + 1];
                csR[0] = rights[0];
                for (int j = 1; j < m; j++) csR[j] = NVec3.Normalize(rights[j - 1] + rights[j]);
                csR[m] = rights[m - 1];

                _beadPlan.Add(new BeadContour { Pts = cPts, CsR = csR, SegFirstFlat = first, SegLastFlat = last, Hh = hh });
                totalSegs += m;
            }
            if (totalSegs <= MaxBeadSegments || attempt >= 4) break;
            eps *= 2f;
        }
    }

    /// <summary>Greedy chord-error decimation: returns kept indices (always includes ends).</summary>
    private static List<int> DecimatePolyline(List<NVec3> pts, float eps)
    {
        var keep = new List<int> { 0 };
        int n = pts.Count;
        int a = 0;
        while (a < n - 1)
        {
            int k = a + 1;
            while (k + 1 < n && k - a < 200 && ChordOk(pts, a, k + 1, eps)) k++;
            keep.Add(k);
            a = k;
        }
        return keep;
    }

    private static bool ChordOk(List<NVec3> pts, int a, int b, float eps)
    {
        var A = pts[a];
        var ab = pts[b] - A;
        float len2 = ab.LengthSquared();
        float eps2 = eps * eps;
        for (int i = a + 1; i < b; i++)
        {
            var ap = pts[i] - A;
            float t = len2 > 1e-12f ? Math.Clamp(NVec3.Dot(ap, ab) / len2, 0f, 1f) : 0f;
            if ((ap - ab * t).LengthSquared() > eps2) return false;
        }
        return true;
    }

    private void EmitCorner(float[] a, ref int i, NVec3 p, NVec3 n)
    {
        a[i++] = p.X - _origin.X; a[i++] = p.Y - _origin.Y; a[i++] = p.Z - _origin.Z;
        a[i++] = n.X;             a[i++] = n.Y;             a[i++] = n.Z;
    }

    private static void AddTri(uint[] a, ref int i, uint x, uint y, uint z)
    { a[i++] = x; a[i++] = y; a[i++] = z; }

    private static void AddQuad(uint[] a, ref int i, uint p0, uint p1, uint p2, uint p3)
    { AddTri(a, ref i, p0, p1, p2); AddTri(a, ref i, p0, p2, p3); }

    private void UploadBead(Toolpath toolpath, NVec3 origin,
        float beadWidth, float layerHeight, NVec3 matColor)
    {
        _beadMaterialColor = new Vector3(matColor.X, matColor.Y, matColor.Z);
        _beadWidth       = beadWidth;
        _beadLayerHeight = layerHeight;

        BuildBeadPlan(toolpath, layerHeight);

        // Travel-only paths have no bead geometry — still build prefix sums for scrubbing.
        if (_beadPlan.Count == 0)
        {
            BuildBeadVertexCumulative();
            return;
        }

        float hw = beadWidth * 0.5f;
        var   up = NVec3.UnitZ;

        int sections = 0, segs = 0;
        foreach (var c in _beadPlan) { sections += c.Pts.Length; segs += c.Pts.Length - 1; }

        // Indexed mesh: 4 shared corner verts per cross-section (~16× leaner than the old
        // 36-verts-per-segment triangle soup), so full wave toolpaths fit in GPU memory.
        var verts = new float[sections * 4 * 6];
        var idx   = new uint[segs * 24 + _beadPlan.Count * 12];
        int vi = 0, ii = 0;
        uint baseV = 0;

        foreach (var c in _beadPlan)
        {
            int   m  = c.Pts.Length;   // cross-sections in this contour (segments + 1)
            float hh = c.Hh;

            for (int s = 0; s < m; s++)
            {
                var r  = c.CsR[s];
                var pt = c.Pts[s];
                var (nLb, nRb, nLt, nRt) = SideNormals(r);
                // corner order: 0=lb, 1=rb, 2=lt, 3=rt
                EmitCorner(verts, ref vi, pt - r*hw - up*hh, nLb);
                EmitCorner(verts, ref vi, pt + r*hw - up*hh, nRb);
                EmitCorner(verts, ref vi, pt - r*hw + up*hh, nLt);
                EmitCorner(verts, ref vi, pt + r*hw + up*hh, nRt);
            }

            uint V(int s, int k) => baseV + (uint)(s * 4 + k);

            // Back cap.
            AddTri(idx, ref ii, V(0,1), V(0,0), V(0,2));
            AddTri(idx, ref ii, V(0,1), V(0,2), V(0,3));

            for (int s = 0; s < m - 1; s++)
            {
                AddQuad(idx, ref ii, V(s,2), V(s,3), V(s+1,3), V(s+1,2));  // top
                AddQuad(idx, ref ii, V(s,1), V(s,0), V(s+1,0), V(s+1,1));  // bottom
                AddQuad(idx, ref ii, V(s,0), V(s,2), V(s+1,2), V(s+1,0));  // left
                AddQuad(idx, ref ii, V(s,3), V(s,1), V(s+1,1), V(s+1,3));  // right
            }

            // Front cap.
            AddTri(idx, ref ii, V(m-1,0), V(m-1,2), V(m-1,3));
            AddTri(idx, ref ii, V(m-1,0), V(m-1,3), V(m-1,1));

            baseV += (uint)(m * 4);
        }

        (_beadVao, _beadVbo) = BuildVao(verts);
        _beadEbo = GL.GenBuffer();
        GL.BindVertexArray(_beadVao);
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, _beadEbo);
        GL.BufferData(BufferTarget.ElementArrayBuffer, ii * sizeof(uint), idx, BufferUsageHint.StaticDraw);
        GL.BindVertexArray(0);
        _beadCount = ii;

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
        if (_beadEbo == 0) return;
        var data = BuildBeadColoredData(scoresPerFlatMove, t => new NVec3(1f, 1f - t, 1f - t));
        if (data.Length > 0)
        {
            (_beadOverhangVao, _beadOverhangVbo) = BuildVao(data);
            GL.BindVertexArray(_beadOverhangVao);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, _beadEbo);   // share bead indices
            GL.BindVertexArray(0);
            _beadOverhangCount = _beadCount;
        }
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
        if (_beadEbo == 0) return;
        var data = BuildBeadColoredData(scoresPerFlatMove, OrientationColor);
        if (data.Length > 0)
        {
            (_orientationVao, _orientationVbo) = BuildVao(data);
            GL.BindVertexArray(_orientationVao);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, _beadEbo);   // share bead indices
            GL.BindVertexArray(0);
            _orientationCount = _beadCount;
        }
    }

    /// <summary>
    /// Builds per-cross-section coloured vertices matching the bead plan geometry exactly
    /// (same sections, same order), so the shared bead index buffer can render them.
    /// </summary>
    private float[] BuildBeadColoredData(float[] scoresPerFlatMove, Func<float, NVec3> colorFromScore)
    {
        int sections = 0;
        foreach (var c in _beadPlan) sections += c.Pts.Length;
        if (sections == 0) return [];

        float hw = _beadWidth * 0.5f;
        var   up = NVec3.UnitZ;
        var   verts = new float[sections * 4 * 6];
        int   vi = 0;

        void EmitColored(NVec3 p, NVec3 col)
        {
            verts[vi++] = p.X - _origin.X; verts[vi++] = p.Y - _origin.Y; verts[vi++] = p.Z - _origin.Z;
            verts[vi++] = col.X;           verts[vi++] = col.Y;           verts[vi++] = col.Z;
        }

        foreach (var c in _beadPlan)
        {
            int m = c.Pts.Length;
            for (int s = 0; s < m; s++)
            {
                // Section s is coloured by the segment it starts (last section reuses
                // the final segment's colour).
                int seg = Math.Min(s, m - 2);
                int fi  = c.SegFirstFlat[seg];
                float t = fi >= 0 && fi < scoresPerFlatMove.Length ? scoresPerFlatMove[fi] : 0f;
                var col = colorFromScore(t);

                var r  = c.CsR[s];
                var pt = c.Pts[s];
                EmitColored(pt - r*hw - up*c.Hh, col);
                EmitColored(pt + r*hw - up*c.Hh, col);
                EmitColored(pt - r*hw + up*c.Hh, col);
                EmitColored(pt + r*hw + up*c.Hh, col);
            }
        }

        return verts;
    }

    // Returns the number of VBO vertices to draw when the scrubber is at `scrubIndex`.
    // scrubIndex == int.MaxValue (default) means show everything.
    private int ScrubCount(int[] cumulative, int totalCount, int scrubIndex)
    {
        if (scrubIndex >= _totalMoveCount || _totalMoveCount == 0) return totalCount;
        if (scrubIndex <= 0) return 0;
        if ((uint)scrubIndex >= (uint)cumulative.Length) return totalCount;
        return Math.Min(cumulative[scrubIndex], totalCount);
    }

    public void Draw(Matrix4 mvp, bool selected = false,
                     bool showExtrusion = true, bool showTravel = true, bool showSeam = true,
                     bool showBead = false, bool showBeadOverhang = false,
                     bool showOrientationPreview = false, int scrubIndex = int.MaxValue,
                     Vector3 eyeLocal = default, float lineOpacity = 1f,
                     bool showLightning = true)
    {
        if (_disposed) return;

        _shader.Use();
        _shader.SetMatrix4("uMVP", ref mvp);
        _shader.SetFloat("uOpacity", lineOpacity);

        int extCount   = ScrubCount(_extrudeVertexCumulative, _extrudeCount, scrubIndex);
        int lgCount    = ScrubCount(_lightningVertexCumulative, _lightningCount, scrubIndex);
        int trCount    = ScrubCount(_travelVertexCumulative,  _travelCount,  scrubIndex);
        int beadCount  = ScrubCount(_beadVertexCumulative,    _beadCount,    scrubIndex);
        int seamCount  = ScrubCount(_seamVertexCumulative,    _pointCount,   scrubIndex);

        if (!selected)
        {
            if (showExtrusion && extCount > 0)
            {
                // Speed/RPM gradients ARE the information — never flatten them to the
                // unselected override colour (the persistent timeline leaves toolpaths
                // unselected, so the override was hiding the gradient entirely).
                bool gradient = _colorMode != ToolpathColorMode.Normal;
                _shader.SetFloat("uOverride", gradient ? 0f : 1f);
                if (!gradient)
                    _shader.SetVector3("uOverrideColor", _unselectedGray);
                GL.BindVertexArray(_extrudeVao);
                GL.DrawArrays(PrimitiveType.Lines, 0, extCount);
            }
            if (showLightning && lgCount > 0)
            {
                // Lightning orange is the layer's identity — keep it when unselected.
                _shader.SetFloat("uOverride", 0f);
                GL.BindVertexArray(_lightningVao);
                GL.DrawArrays(PrimitiveType.Lines, 0, lgCount);
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

            if (showLightning && lgCount > 0)
            {
                GL.BindVertexArray(_lightningVao);
                GL.DrawArrays(PrimitiveType.Lines, 0, lgCount);
            }

            if (showTravel && trCount > 0)
            {
                GL.BindVertexArray(_travelVao);
                GL.DrawArrays(PrimitiveType.Lines, 0, trCount);
            }

            _shader.SetFloat("uOpacity", 1f);
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
            _shader.SetFloat("uOpacity", 1f);
            _shader.SetMatrix4("uMVP", ref mvp);
            _shader.SetFloat("uOverride", 0f);
            GL.Disable(EnableCap.CullFace);
            GL.BindVertexArray(_orientationVao);
            GL.DrawElements(PrimitiveType.Triangles, Math.Min(_orientationCount, beadCount),
                DrawElementsType.UnsignedInt, 0);
            GL.Enable(EnableCap.CullFace);
        }
        else if (showBeadOverhang && _beadOverhangVao != 0 && beadCount > 0)
        {
            _shader.Use();
            _shader.SetMatrix4("uMVP", ref mvp);
            _shader.SetFloat("uOverride", 0f);
            GL.Disable(EnableCap.CullFace);
            GL.BindVertexArray(_beadOverhangVao);
            GL.DrawElements(PrimitiveType.Triangles, Math.Min(_beadOverhangCount, beadCount),
                DrawElementsType.UnsignedInt, 0);
            GL.Enable(EnableCap.CullFace);
        }
        else if (showBead && beadCount > 0)
        {
            _beadShader.Use();
            _beadShader.SetMatrix4("uMVP", ref mvp);
            _beadShader.SetVector3("uColor", _beadMaterialColor);
            _beadShader.SetVector3("uEye",   eyeLocal);
            GL.Disable(EnableCap.CullFace);
            GL.BindVertexArray(_beadVao);
            GL.DrawElements(PrimitiveType.Triangles, beadCount, DrawElementsType.UnsignedInt, 0);
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
        if (_lightningVao     != 0) { GL.DeleteVertexArray(_lightningVao);     GL.DeleteBuffer(_lightningVbo);     }
        if (_ptVao            != 0) { GL.DeleteVertexArray(_ptVao);            GL.DeleteBuffer(_ptVbo);            }
        if (_beadVao          != 0) { GL.DeleteVertexArray(_beadVao);          GL.DeleteBuffer(_beadVbo);          }
        if (_beadEbo          != 0) GL.DeleteBuffer(_beadEbo);
        if (_beadOverhangVao  != 0) { GL.DeleteVertexArray(_beadOverhangVao);  GL.DeleteBuffer(_beadOverhangVbo);  }
        if (_orientationVao   != 0) { GL.DeleteVertexArray(_orientationVao);   GL.DeleteBuffer(_orientationVbo);   }
        if (_singularityPtVao != 0) { GL.DeleteVertexArray(_singularityPtVao); GL.DeleteBuffer(_singularityPtVbo); }
        _shader.Dispose();
        _beadShader.Dispose();
    }
}
