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
    private int  _wipeVao, _wipeVbo, _wipeCount;
    private int  _travelVao,  _travelVbo,  _travelCount;
    private int  _lightningVao, _lightningVbo, _lightningCount;
    private int  _ptVao, _ptVbo;
    private int  _pointCount;
    /// <summary>All extrude midpoints (edit Point mode) — denser than seam-only points.</summary>
    private int  _allPtVao, _allPtVbo;
    private int  _allPointCount;
    private int  _beadVao, _beadVbo, _beadEbo, _beadCount;   // _beadCount = index count
    private int  _beadOverhangVao, _beadOverhangVbo, _beadOverhangCount;
    private int  _orientationVao, _orientationVbo, _orientationCount;
    private int  _singularityPtVao, _singularityPtVbo, _singularityPointCount;
    private int[] _singularityVertexCumulative = [];
    private bool _disposed;

    private readonly Shader _shader;
    private readonly Shader _beadShader;
    private readonly Shader _pointShader;
    /// <summary>Null if geometry-shader thick lines failed to compile (fallback to GL lines).</summary>
    private readonly Shader? _depthLineShader;
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

    // Keep GLSL pure ASCII - some drivers choke on unicode in comments (em-dash etc.).
    private static readonly string FragSrc = """
#version 330 core
in vec3 vColor;
uniform float uOverride;       // 1 = use uOverrideColor; 0 = per-vertex
uniform vec3  uOverrideColor;
uniform float uOpacity;        // line transparency (1 = opaque)
uniform float uDashPeriodPx;   // 0 = solid; >0 = screen-space dash period (px)
out vec4 fragColor;
void main() {
    if (uDashPeriodPx > 0.5) {
        // Diagonal screen-space stipple - consistent dash length at any zoom.
        float t = (gl_FragCoord.x + gl_FragCoord.y) / uDashPeriodPx;
        if (fract(t) > 0.55) discard;
    }
    fragColor = vec4(uOverride > 0.5 ? uOverrideColor : vColor, uOpacity);
}
""";

    /// <summary>
    /// Edit Point mode: size + alpha from camera distance so near beads pop and
    /// far ones shrink/fade (~20% opacity) for easier depth reading and picking.
    /// Requires GL_PROGRAM_POINT_SIZE.
    /// </summary>
    // Keep GLSL pure ASCII — some drivers choke on unicode in comments.
    private static readonly string PointVertSrc = """
#version 330 core
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aColor;
uniform mat4  uMVP;
uniform vec3  uEye;
uniform vec3  uPointColor;
uniform float uRefDist;
uniform float uBaseSize;
uniform float uMinSize;
uniform float uMaxSize;
out vec3  vColor;
out float vAlpha;
void main() {
    gl_Position = vec4(aPos, 1.0) * uMVP;
    vColor = uPointColor;
    float dist = max(length(aPos - uEye), 1.0);
    float refD = max(uRefDist, 80.0);
    gl_PointSize = clamp(uBaseSize * (refD / dist), uMinSize, uMaxSize);
    float t = smoothstep(refD * 0.35, refD * 2.8, dist);
    vAlpha = mix(1.0, 0.20, t);
}
""";

    private static readonly string PointFragSrc = """
#version 330 core
in vec3  vColor;
in float vAlpha;
out vec4 fragColor;
void main() {
    vec2 c = gl_PointCoord * 2.0 - 1.0;
    float r2 = dot(c, c);
    if (r2 > 1.0) discard;
    float edge = smoothstep(1.0, 0.55, r2);
    fragColor = vec4(vColor, vAlpha * edge);
}
""";

    /// <summary>
    /// Path edit mode: expand GL_LINES to screen-space quads.
    /// Depth from clip-space (NDC z): near = thick + opaque, far = thin + translucent.
    /// (World-space eye distance was unreliable with local toolpath transforms.)
    /// </summary>
    private static readonly string DepthLineVertSrc = """
#version 330 core
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aColor;
uniform mat4  uMVP;
uniform float uNearWidthPx;
uniform float uFarWidthPx;
out VS_OUT {
    vec3  color;
    float alpha;
    float widthPx;
} vs_out;
void main() {
    gl_Position = vec4(aPos, 1.0) * uMVP;
    vs_out.color = aColor;
    // OpenGL NDC z: -1 = near clip, +1 = far clip. Use that as visual depth.
    float w = max(abs(gl_Position.w), 1e-5);
    float ndcZ = gl_Position.z / w;
    // t = 0 at near, 1 at far
    float t = smoothstep(-0.92, 0.88, ndcZ);
    vs_out.alpha = mix(1.0, 0.18, t);
    vs_out.widthPx = mix(uNearWidthPx, uFarWidthPx, t);
}
""";

    private static readonly string DepthLineGeomSrc = """
#version 330 core
layout(lines) in;
layout(triangle_strip, max_vertices = 4) out;
uniform vec2 uViewport;
in VS_OUT {
    vec3  color;
    float alpha;
    float widthPx;
} gs_in[];
out vec3  vColor;
out float vAlpha;
void main() {
    vec4 p0 = gl_in[0].gl_Position;
    vec4 p1 = gl_in[1].gl_Position;
    float w0abs = abs(p0.w);
    float w1abs = abs(p1.w);
    if (w0abs < 1e-5 || w1abs < 1e-5) return;

    vec2 ndc0 = p0.xy / p0.w;
    vec2 ndc1 = p1.xy / p1.w;
    vec2 vp = max(uViewport, vec2(1.0));
    vec2 s0 = (ndc0 * 0.5 + 0.5) * vp;
    vec2 s1 = (ndc1 * 0.5 + 0.5) * vp;
    vec2 dir = s1 - s0;
    float len = length(dir);
    if (len < 1e-4) dir = vec2(1.0, 0.0);
    else dir /= len;
    vec2 n = vec2(-dir.y, dir.x);

    // Half-width in screen pixels (near endpoints thicker when widthPx is larger).
    float hw0 = max(gs_in[0].widthPx, 1.0) * 0.5;
    float hw1 = max(gs_in[1].widthPx, 1.0) * 0.5;
    vec2 o0 = n * hw0;
    vec2 o1 = n * hw1;
    // Convert pixel offset to clip space (use abs(w) so sign does not flip sides).
    vec4 off0 = vec4((o0 / vp) * 2.0 * w0abs, 0.0, 0.0);
    vec4 off1 = vec4((o1 / vp) * 2.0 * w1abs, 0.0, 0.0);

    vColor = gs_in[0].color; vAlpha = gs_in[0].alpha;
    gl_Position = p0 + off0; EmitVertex();
    gl_Position = p0 - off0; EmitVertex();
    vColor = gs_in[1].color; vAlpha = gs_in[1].alpha;
    gl_Position = p1 + off1; EmitVertex();
    gl_Position = p1 - off1; EmitVertex();
    EndPrimitive();
}
""";

    private static readonly string DepthLineFragSrc = """
#version 330 core
in vec3  vColor;
in float vAlpha;
uniform float uOverride;
uniform vec3  uOverrideColor;
out vec4 fragColor;
void main() {
    fragColor = vec4(uOverride > 0.5 ? uOverrideColor : vColor, vAlpha);
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

    /// <summary>Moves demanding more RPM than the extruder can deliver. Magenta so it reads
    /// as its own fault, distinct from unreachable red and from anything in the gradient.</summary>
    private static readonly Vector3 RpmOverLimitColor = new(1.0f, 0.0f, 0.85f);

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
        public required float[] Hh;           // per-section half layer height (wedge layers vary)
        public required NVec3   Up;           // layer plane normal — beads stack along it (angled prints)
    }
    private List<BeadContour> _beadPlan = [];
    private Toolpath _toolpath;
    private NVec3    _origin;
    private bool[]?  _reachability;  // per flat-move index; null = all reachable

    /// <summary>
    /// Exported RPM (%) per flat-move index, straight from <c>ToolpathRpm.Analyze</c> —
    /// the same numbers the .src is written with. NaN on non-extrusion moves,
    /// null before the first analysis. Drives both the RPM gradient and the over-limit
    /// highlight, so the viewport cannot show an RPM the exporter disagrees with.
    /// </summary>
    private float[]? _rpmPercent;
    private float    _rpmLimit = float.PositiveInfinity;
    private bool     _showRpmOverLimit;

    /// <summary>Total flat move count (scrub/simulation range).</summary>
    public int TotalMoveCount => _totalMoveCount;

    // Prefix-sum arrays: cumulative[i] = total VBO vertices for the first i flat moves.
    // Index 0 = 0 (nothing drawn), index _totalMoveCount = full count.
    private int   _totalMoveCount;
    private int[] _extrudeVertexCumulative = [];
    private int[] _travelVertexCumulative  = [];
    private int[] _lightningVertexCumulative = [];
    private int[] _wipeVertexCumulative = [];
    private static readonly Vector3 LightningColor = new(1.00f, 0.58f, 0.12f);
    private int[] _beadVertexCumulative    = [];
    private int[] _seamVertexCumulative    = [];
    private int[] _allPointVertexCumulative = [];

    public ToolpathRenderer(Toolpath toolpath, NVec3 origin = default,
        float beadWidth = 6f, float layerHeight = 3f, NVec3 materialColor = default,
        Toolpath? beadToolpath = null)
    {
        _toolpath   = toolpath;
        _origin     = origin;
        _shader      = new Shader(VertSrc,      FragSrc);
        _beadShader  = new Shader(BeadVertSrc,  BeadFragSrc);
        _pointShader = new Shader(PointVertSrc, PointFragSrc);
        Shader? depthLines = null;
        try
        {
            depthLines = new Shader(DepthLineVertSrc, DepthLineFragSrc, DepthLineGeomSrc);
        }
        catch (Exception ex)
        {
            System.Console.Error.WriteLine($"[toolpath] depth line shader unavailable: {ex.Message}");
        }
        _depthLineShader = depthLines;
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
        if (_wipeVao != 0) { GL.DeleteVertexArray(_wipeVao); GL.DeleteBuffer(_wipeVbo); }
        _wipeVao = _wipeVbo = _wipeCount = 0;
        var wpData = BuildExtrudeData(wipeOnly: true);
        if (wpData.Length > 0)
        {
            (_wipeVao, _wipeVbo) = BuildVao(wpData);
            _wipeCount = wpData.Length / 6;
        }
    }

    /// <summary>
    /// Supplies the per-move exported RPM (%) and the limit above which a move is flagged.
    /// <paramref name="rpmPercent"/> is indexed by flat move index; NaN where no RPM is written.
    /// Rebuilds the extrude VBOs. Must be called on the GL thread.
    /// </summary>
    public void UpdateRpm(float[]? rpmPercent, float limit)
    {
        _rpmPercent = rpmPercent;
        _rpmLimit   = limit;
        RebuildLineVbos();
    }

    /// <summary>Shows or hides the over-limit RPM highlight. GL thread only.</summary>
    public void SetRpmOverLimitVisible(bool visible)
    {
        if (_showRpmOverLimit == visible) return;
        _showRpmOverLimit = visible;
        // With no analysis yet nothing can be flagged, so skip the rebuild —
        // this runs on every newly uploaded toolpath and they can be huge.
        if (_rpmPercent is not null) RebuildLineVbos();
    }

    /// <summary>True when this move's exported RPM exceeds the limit.</summary>
    private bool IsRpmOverLimit(int flatIndex)
        => _rpmPercent is { } r && flatIndex < r.Length
           && !float.IsNaN(r[flatIndex]) && r[flatIndex] > _rpmLimit;

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
        if (lines && _wipeVao != 0 && _wipeCount > 0)
        {
            GL.BindVertexArray(_wipeVao);
            GL.DrawArrays(PrimitiveType.Lines, 0, _wipeCount);
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

        if (_wipeVao != 0) { GL.DeleteVertexArray(_wipeVao); GL.DeleteBuffer(_wipeVbo); }
        _wipeVao = _wipeVbo = _wipeCount = 0;
        var wpData = BuildExtrudeData(wipeOnly: true);
        if (wpData.Length > 0) { (_wipeVao, _wipeVbo) = BuildVao(wpData); _wipeCount = wpData.Length / 6; }

        if (_travelVao != 0) { GL.DeleteVertexArray(_travelVao); GL.DeleteBuffer(_travelVbo); }
        _travelVao = _travelVbo = _travelCount = 0;
        var trData = BuildTravelData();
        if (trData.Length > 0) { (_travelVao, _travelVbo) = BuildVao(trData); _travelCount = trData.Length / 6; }

        if (_ptVao != 0) { GL.DeleteVertexArray(_ptVao); GL.DeleteBuffer(_ptVbo); }
        _ptVao = _ptVbo = _pointCount = 0;
        var ptData = BuildSeamData();
        _pointCount = ptData.Length / 6;
        if (_pointCount > 0) (_ptVao, _ptVbo) = BuildVao(ptData);

        if (_allPtVao != 0) { GL.DeleteVertexArray(_allPtVao); GL.DeleteBuffer(_allPtVbo); }
        _allPtVao = _allPtVbo = _allPointCount = 0;
        var allPtData = BuildAllPathPointData();
        _allPointCount = allPtData.Length / 6;
        if (_allPointCount > 0) (_allPtVao, _allPtVbo) = BuildVao(allPtData);
    }

    private float[] BuildExtrudeData(bool lightningOnly = false, bool wipeOnly = false)
    {
        // Three display buckets: lightning fingers, wipes, plain extrusion.
        bool Match(ToolpathMove m) => lightningOnly
            ? m.IsLightning
            : wipeOnly
                ? !m.IsLightning && m.IsWipe
                : !m.IsLightning && !m.IsWipe;
        int extrudeCount = 0;
        foreach (var layer in _toolpath.Layers)
            foreach (var move in layer.Moves)
                if (move.Kind is MoveKind.Extrude or MoveKind.Mill
                    && Match(move)) extrudeCount++;

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
            int si = 0;
            foreach (var layer in _toolpath.Layers)
                foreach (var move in layer.Moves)
                {
                    if (move.Kind == MoveKind.Extrude)
                    {
                        float v = MoveScalar(move, layer, si);
                        if (v < scalarMin) scalarMin = v;
                        if (v > scalarMax) scalarMax = v;
                    }
                    si++;
                }
        }
        float scalarRange = scalarMax - scalarMin;

        foreach (var layer in _toolpath.Layers)
        {
            foreach (var move in layer.Moves)
            {
                if (move.Kind is MoveKind.Extrude or MoveKind.Mill
                    && Match(move))
                {
                    Vector3 color;
                    if (_reachability is not null && mi < _reachability.Length && !_reachability[mi])
                        color = UnreachableColor;
                    else if (_showRpmOverLimit && IsRpmOverLimit(mi))
                        color = RpmOverLimitColor;
                    else if (move.Kind == MoveKind.Mill)
                        color = _millColor;
                    else if (_colorMode != ToolpathColorMode.Normal)
                        color = scalarRange < 1e-6f
                            ? GradientColor(0.5f)
                            : GradientColor((MoveScalar(move, layer, mi) - scalarMin) / scalarRange);
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

    /// <summary>Per-move factor for the active gradient mode (normalised later).</summary>
    private float MoveScalar(ToolpathMove move, ToolpathLayer layer, int flatIndex)
    {
        if (_colorMode == ToolpathColorMode.Thermal)
            return float.IsNaN(layer.ThermalTempC) ? 0f : layer.ThermalTempC;
        float speed = move.PrintSpeedScale * (move.IsResumeRamp ? move.ResumeSpeedScale : 1f);
        if (_colorMode == ToolpathColorMode.Speed) return speed;
        // RPM: the real exported percentage when the analysis has run, so the gradient and
        // the .src agree. Falls back to a proportional estimate only before the first slice.
        if (_rpmPercent is { } r && flatIndex < r.Length && !float.IsNaN(r[flatIndex]))
            return r[flatIndex];
        return speed
             * (move.IsResumeRamp ? move.ResumeRpmScale : 1f)
             * (move.IsWipe ? move.WipeRpmScale : 1f)
             * MathF.Max(0.1f, layer.Height * move.HeightScale);
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
        _wipeVertexCumulative      = new int[total + 1];

        int ei = 0, ti = 0, li = 0, wi = 0, fi = 0;
        foreach (var layer in _toolpath.Layers)
        {
            foreach (var move in layer.Moves)
            {
                if (ToolpathMoveKinds.IsCutSegment(move.Kind))
                {
                    if (move.IsLightning) li += 2;
                    else if (move.IsWipe) wi += 2;
                    else ei += 2;
                }
                else if (ToolpathMoveKinds.IsTravelSegment(move.Kind)) ti += 2;
                fi++;
                _extrudeVertexCumulative[fi]   = ei;
                _travelVertexCumulative[fi]    = ti;
                _lightningVertexCumulative[fi] = li;
                _wipeVertexCumulative[fi]      = wi;
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

        var wpData = BuildExtrudeData(wipeOnly: true);
        if (wpData.Length > 0) { (_wipeVao, _wipeVbo) = BuildVao(wpData); _wipeCount = wpData.Length / 6; }

        var trData = BuildTravelData();
        if (trData.Length > 0) { (_travelVao, _travelVbo) = BuildVao(trData); _travelCount = trData.Length / 6; }

        var ptData = BuildSeamData();
        _pointCount = ptData.Length / 6;
        if (_pointCount > 0) (_ptVao, _ptVbo) = BuildVao(ptData);

        var allPtData = BuildAllPathPointData();
        _allPointCount = allPtData.Length / 6;
        if (_allPointCount > 0) (_allPtVao, _allPtVbo) = BuildVao(allPtData);
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

    /// <summary>
    /// One point at the midpoint of every extrude bead — used by edit Point mode so
    /// the user sees every pickable vertex, not only contour seam ends.
    /// </summary>
    private float[] BuildAllPathPointData()
    {
        var events = new List<(int FlatIdx, NVec3 Pos)>();
        int fi = 0;
        foreach (var layer in _toolpath.Layers)
        {
            foreach (var move in layer.Moves)
            {
                if (move.Kind == MoveKind.Extrude
                    && !move.IsLayerStitch && !move.IsLayerChange)
                    events.Add((fi, (move.From + move.To) * 0.5f));
                fi++;
            }
        }

        // Lime vertex fallback (live colour is uPointColor uniform).
        var col = new Vector3(0.55f, 1.0f, 0.18f);
        var ptData = new float[events.Count * 6];
        int pi = 0;
        foreach (var (_, pos) in events)
        {
            ptData[pi++] = pos.X - _origin.X; ptData[pi++] = pos.Y - _origin.Y; ptData[pi++] = pos.Z - _origin.Z;
            ptData[pi++] = col.X;             ptData[pi++] = col.Y;             ptData[pi++] = col.Z;
        }

        _allPointVertexCumulative = new int[_totalMoveCount + 1];
        int ei = 0;
        for (int i = 1; i <= _totalMoveCount; i++)
        {
            _allPointVertexCumulative[i] = _allPointVertexCumulative[i - 1];
            while (ei < events.Count && events[ei].FlatIdx < i)
            {
                _allPointVertexCumulative[i]++;
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
    private static (NVec3 lb, NVec3 rb, NVec3 lt, NVec3 rt) SideNormals(NVec3 r, NVec3 up) => (
        NVec3.Normalize(-r - up), NVec3.Normalize( r - up),
        NVec3.Normalize(-r + up), NVec3.Normalize( r + up));

    /// <summary>
    /// Builds the shared bead plan: contours of chord-error-decimated polyline points.
    /// Unlike fixed-step decimation, this keeps every point needed to represent curves
    /// (e.g. wave effects) faithfully and merges only visually straight runs.
    /// </summary>
    private void BuildBeadPlan(Toolpath toolpath, float layerHeight)
    {
        // Collect raw contours: positions + flat move indices of consecutive cut runs.
        // Half-heights are PER POINT: Multi-Planar wedge layers vary in thickness
        // along the path (ToolpathMove.HeightScale).
        var raw = new List<(List<NVec3> pts, List<int> flats, List<float> hhs, NVec3 up)>();
        int flatIdx = 0;
        foreach (var layer in toolpath.Layers)
        {
            float lh  = layer.Height > 0f ? layer.Height : layerHeight;
            float lhh = lh * 0.5f;
            // Beads stack along the slicing-plane normal, not world Z — on angled
            // prints the cross-section must tilt with the layers or the preview
            // renders overlapping parallelogram-sheared beads.
            var layerUp = layer.PlaneNormal.LengthSquared() > 1e-6f
                ? NVec3.Normalize(layer.PlaneNormal)
                : NVec3.UnitZ;
            List<NVec3>? pts = null; List<int>? flats = null; List<float>? hhs = null;
            foreach (var move in layer.Moves)
            {
                // Wipes are extrude-kind but deposit a ramping-down dribble, not a
                // bead — rendering them as solid geometry grows full-width prongs
                // past every contour end in the printed-part preview.
                if (ToolpathMoveKinds.IsCutSegment(move.Kind) && !move.IsWipe)
                {
                    if (pts is null)
                    {
                        pts = [move.From]; flats = []; hhs = [lhh * move.HeightScale];
                        raw.Add((pts, flats, hhs, layerUp));
                    }
                    pts.Add(move.To);
                    flats!.Add(flatIdx);
                    hhs!.Add(lhh * move.HeightScale);
                }
                else { pts = null; flats = null; hhs = null; }
                flatIdx++;
            }
        }

        // Decimate; if over the segment budget, coarsen the tolerance and retry.
        float eps = BeadChordTolerance;
        for (int attempt = 0; ; attempt++)
        {
            _beadPlan = [];
            long totalSegs = 0;
            foreach (var (pts, flats, hhs, contourUp) in raw)
            {
                var keep = DecimatePolyline(pts, eps);
                int m = keep.Count - 1;
                if (m <= 0) continue;
                var cPts  = new NVec3[m + 1];
                var cHh   = new float[m + 1];
                var first = new int[m];
                var last  = new int[m];
                for (int j = 0; j <= m; j++) { cPts[j] = pts[keep[j]]; cHh[j] = hhs[keep[j]]; }
                for (int j = 0; j <  m; j++)
                {
                    first[j] = flats[keep[j]];
                    last[j]  = flats[keep[j + 1] - 1];
                }

                // Blended cross-section right vectors (same construction as before).
                var rights = new NVec3[m];
                var up = contourUp;
                // Stable in-plane perpendicular of `up` for degenerate segments.
                var upPerp = NVec3.Normalize(NVec3.Cross(up,
                    MathF.Abs(up.X) < 0.9f ? NVec3.UnitX : NVec3.UnitY));
                for (int j = 0; j < m; j++)
                {
                    var d = cPts[j + 1] - cPts[j];
                    var fwd = d.LengthSquared() > 1e-12f
                        ? NVec3.Normalize(d)
                        : (j > 0 ? NVec3.Normalize(cPts[j] - cPts[j - 1]) : NVec3.UnitX);
                    var r = NVec3.Cross(fwd, up);
                    // Segments climbing out of the layer plane (layer stitches, ramps)
                    // run nearly parallel to `up`: the cross product collapses and its
                    // normalized direction is numeric noise that renders as fat twisted
                    // tubes. Within ~9° of parallel, carry the previous right instead.
                    rights[j] = r.LengthSquared() < 0.025f
                        ? (j > 0 ? rights[j - 1] : upPerp)
                        : NVec3.Normalize(r);
                }
                var csR = new NVec3[m + 1];
                csR[0] = rights[0];
                for (int j = 1; j < m; j++)
                {
                    // Opposite rights (180° turnaround) sum to ~zero — normalizing that
                    // is NaN; keep the incoming segment's frame there.
                    var sum = rights[j - 1] + rights[j];
                    csR[j] = sum.LengthSquared() > 1e-6f ? NVec3.Normalize(sum) : rights[j];
                }
                csR[m] = rights[m - 1];

                _beadPlan.Add(new BeadContour { Pts = cPts, CsR = csR, SegFirstFlat = first, SegLastFlat = last, Hh = cHh, Up = contourUp });
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
            var   up = c.Up;

            for (int s = 0; s < m; s++)
            {
                var r  = c.CsR[s];
                var pt = c.Pts[s];
                float hh = c.Hh[s];
                var (nLb, nRb, nLt, nRt) = SideNormals(r, up);
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

    private static NVec3 OrientationColor(float t) => Ramp(_orientationStops, t);

    /// <summary>Piecewise-linear colour ramp through an ordered stop table.</summary>
    private static NVec3 Ramp((float t, float r, float g, float b)[] s, float t)
    {
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

    // Support check — BANDED, not a smooth ramp. The eye cannot separate 25 % from 45 % on a
    // continuous white-to-red gradient, and the interesting boundary is exactly in there. Each
    // class owns a band with a hard edge against its neighbours and a slight gradient inside it,
    // so "on target" reads as on target at a glance and severity is still legible within a class.
    //
    // Bands match BeadSupport.Band* — score 0..0.30 on target, 0.40..0.60 bridged, 0.70..1 failed.
    // The unused slivers between them are what make the edges hard.
    private static readonly (float t, float r, float g, float b)[] _supportCheckStops =
    [
        (0.000f, 0.32f, 0.36f, 0.40f),  // Slate      — stacked square; deliberately dull
        (0.300f, 0.55f, 0.62f, 0.66f),  // Light slate — right at target, still a pass
        (0.400f, 1.00f, 0.78f, 0.20f),  // Amber      — past target but bridged
        (0.600f, 1.00f, 0.60f, 0.00f),  // Deep amber — bridged, at the far end
        (0.700f, 1.00f, 0.22f, 0.18f),  // Red        — FAILED: past target over a real run
        (1.000f, 0.62f, 0.00f, 0.32f),  // Crimson    — failed and far off
    ];

    private static NVec3 SupportCheckColor(float t) => Ramp(_supportCheckStops, t);

    /// <summary>
    /// Bead coloured by the support check: does each bead meet the overlap target the slicer was
    /// given, and if not, is its stretch long enough that the slicer counted it as a miss.
    /// Shares the bead index buffer and the overhang VAO slot — the two are alternative readings
    /// of the same geometry and are never drawn together.
    /// </summary>
    public void UpdateSupportCheck(float[] scoresPerFlatMove)
    {
        if (_beadOverhangVao != 0) { GL.DeleteVertexArray(_beadOverhangVao); GL.DeleteBuffer(_beadOverhangVbo); }
        _beadOverhangVao = _beadOverhangVbo = _beadOverhangCount = 0;
        if (_beadEbo == 0) return;
        var data = BuildBeadColoredData(scoresPerFlatMove, SupportCheckColor);
        if (data.Length > 0)
        {
            (_beadOverhangVao, _beadOverhangVbo) = BuildVao(data);
            GL.BindVertexArray(_beadOverhangVao);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, _beadEbo);   // share bead indices
            GL.BindVertexArray(0);
            _beadOverhangCount = _beadCount;
        }
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
            var up = c.Up;
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
                float hh = c.Hh[s];
                EmitColored(pt - r*hw - up*hh, col);
                EmitColored(pt + r*hw - up*hh, col);
                EmitColored(pt - r*hw + up*hh, col);
                EmitColored(pt + r*hw + up*hh, col);
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
                     bool showLightning = true, int scrubStart = 0,
                     bool showWipe = true,
                     bool showAllPathPoints = false,
                     bool showDepthLines = false,
                     float viewportW = 0f, float viewportH = 0f,
                     float dashPeriodPx = 0f,
                     float lineWidth = 1f)
    {
        if (_disposed) return;

        int extCount   = ScrubCount(_extrudeVertexCumulative, _extrudeCount, scrubIndex);
        int lgCount    = ScrubCount(_lightningVertexCumulative, _lightningCount, scrubIndex);
        int wpCount    = ScrubCount(_wipeVertexCumulative,    _wipeCount,    scrubIndex);
        int trCount    = ScrubCount(_travelVertexCumulative,  _travelCount,  scrubIndex);
        int beadCount  = ScrubCount(_beadVertexCumulative,    _beadCount,    scrubIndex);
        int seamCount  = ScrubCount(_seamVertexCumulative,    _pointCount,   scrubIndex);
        int allPtCount = ScrubCount(_allPointVertexCumulative, _allPointCount, scrubIndex);

        // Layer-window low bound (edit mode): first vertex per buffer to draw.
        int extFirst = 0, lgFirst = 0, wpFirst = 0, trFirst = 0, beadFirst = 0, seamFirst = 0, allPtFirst = 0;
        if (scrubStart > 0)
        {
            extFirst  = Math.Min(ScrubCount(_extrudeVertexCumulative,  _extrudeCount, scrubStart), extCount);
            lgFirst   = Math.Min(ScrubCount(_lightningVertexCumulative, _lightningCount, scrubStart), lgCount);
            wpFirst   = Math.Min(ScrubCount(_wipeVertexCumulative,     _wipeCount,    scrubStart), wpCount);
            trFirst   = Math.Min(ScrubCount(_travelVertexCumulative,   _travelCount,  scrubStart), trCount);
            beadFirst = Math.Min(ScrubCount(_beadVertexCumulative,     _beadCount,    scrubStart), beadCount);
            seamFirst = Math.Min(ScrubCount(_seamVertexCumulative,     _pointCount,   scrubStart), seamCount);
            allPtFirst = Math.Min(ScrubCount(_allPointVertexCumulative, _allPointCount, scrubStart), allPtCount);
        }

        // Path edit mode: depth-cued thick lines (near = 2.5x, far = fade). Prefer the
        // geometry-shader path; fall back to plain GL lines if unavailable.
        // Dashed neighbour layers force the simple line path (depth shader has no dash).
        bool useDepthLines = showDepthLines
            && !showAllPathPoints
            && dashPeriodPx <= 0.5f
            && _depthLineShader is not null
            && viewportW > 1f && viewportH > 1f;

        if (useDepthLines)
        {
            DrawDepthAwareLines(mvp, eyeLocal, viewportW, viewportH,
                selected, showExtrusion, showLightning, showTravel, showWipe,
                extFirst, extCount, lgFirst, lgCount, trFirst, trCount, wpFirst, wpCount);
        }
        else
        {
            _shader.Use();
            _shader.SetMatrix4("uMVP", ref mvp);
            _shader.SetFloat("uOpacity", lineOpacity);
            _shader.SetFloat("uDashPeriodPx", dashPeriodPx);

            // Point edit mode: slightly thicker centre-lines under the dots.
            float appliedWidth = showAllPathPoints ? Math.Max(2.5f, lineWidth) : lineWidth;
            if (appliedWidth > 1.01f)
                GL.LineWidth(appliedWidth);

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
                    GL.DrawArrays(PrimitiveType.Lines, extFirst, extCount - extFirst);
                }
                if (showLightning && lgCount > 0)
                {
                    // Lightning orange is the layer's identity — keep it when unselected.
                    _shader.SetFloat("uOverride", 0f);
                    GL.BindVertexArray(_lightningVao);
                    GL.DrawArrays(PrimitiveType.Lines, lgFirst, lgCount - lgFirst);
                }
                if (showWipe && wpCount > 0)
                {
                    _shader.SetFloat("uOverride", 0f);
                    GL.BindVertexArray(_wipeVao);
                    GL.DrawArrays(PrimitiveType.Lines, wpFirst, wpCount - wpFirst);
                }
            }
            else
            {
                _shader.SetFloat("uOverride", 0f);

                if (showExtrusion && extCount > 0)
                {
                    GL.BindVertexArray(_extrudeVao);
                    GL.DrawArrays(PrimitiveType.Lines, extFirst, extCount - extFirst);
                }

                if (showLightning && lgCount > 0)
                {
                    GL.BindVertexArray(_lightningVao);
                    GL.DrawArrays(PrimitiveType.Lines, lgFirst, lgCount - lgFirst);
                }

                if (showWipe && wpCount > 0)
                {
                    GL.BindVertexArray(_wipeVao);
                    GL.DrawArrays(PrimitiveType.Lines, wpFirst, wpCount - wpFirst);
                }

                if (showTravel && trCount > 0)
                {
                    GL.BindVertexArray(_travelVao);
                    GL.DrawArrays(PrimitiveType.Lines, trFirst, trCount - trFirst);
                }
            }

            if (appliedWidth > 1.01f)
                GL.LineWidth(1f);
            // Reset dash so other draws default to solid.
            _shader.SetFloat("uDashPeriodPx", 0f);
        }

        // Seam / singularity points (selected appearance only).
        if (selected || showAllPathPoints)
        {
            _shader.Use();
            _shader.SetMatrix4("uMVP", ref mvp);
            _shader.SetFloat("uOpacity", 1f);
            _shader.SetFloat("uOverride", 0f);

            if (selected && showSeam && !showAllPathPoints && seamCount > 0)
            {
                GL.PointSize(8f);
                GL.BindVertexArray(_ptVao);
                GL.DrawArrays(PrimitiveType.Points, seamFirst, seamCount - seamFirst);
                GL.PointSize(1f);
            }

            if (selected && _singularityPtVao != 0)
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

        // Edit Point mode: every extrude midpoint. Size + opacity scale with camera
        // distance so near beads are large/solid and far ones shrink to ~20% alpha.
        if (showAllPathPoints && allPtCount > allPtFirst && _allPtVao != 0)
        {
            // eyeLocal is camera position in toolpath-local space (matches aPos).
            float refDist = eyeLocal.Length;
            if (refDist < 80f) refDist = 500f; // sane default when eye is near origin

            _pointShader.Use();
            _pointShader.SetMatrix4("uMVP", ref mvp);
            _pointShader.SetVector3("uEye", eyeLocal);
            // Lime green base beads; hover/select recolour yellow via paint overlay points.
            _pointShader.SetVector3("uPointColor", new Vector3(0.55f, 1.0f, 0.18f));
            _pointShader.SetFloat("uRefDist", refDist);
            _pointShader.SetFloat("uBaseSize", 7f);   // px at ref distance
            _pointShader.SetFloat("uMinSize", 2.0f);
            _pointShader.SetFloat("uMaxSize", 16f);

            GL.Enable(EnableCap.ProgramPointSize);
            // Soft sprites need blending; keep depth test so near dots win ties.
            GL.DepthMask(false);
            GL.BindVertexArray(_allPtVao);
            GL.DrawArrays(PrimitiveType.Points, allPtFirst, allPtCount - allPtFirst);
            GL.DepthMask(true);
            GL.Disable(EnableCap.ProgramPointSize);
        }

        if (showOrientationPreview && _orientationVao != 0 && beadCount > 0)
        {
            _shader.Use();
            _shader.SetFloat("uOpacity", 1f);
            _shader.SetMatrix4("uMVP", ref mvp);
            _shader.SetFloat("uOverride", 0f);
            GL.Disable(EnableCap.CullFace);
            GL.BindVertexArray(_orientationVao);
            GL.DrawElements(PrimitiveType.Triangles,
                Math.Max(0, Math.Min(_orientationCount, beadCount) - beadFirst),
                DrawElementsType.UnsignedInt, (IntPtr)((long)beadFirst * sizeof(uint)));
            GL.Enable(EnableCap.CullFace);
        }
        else if (showBeadOverhang && _beadOverhangVao != 0 && beadCount > 0)
        {
            _shader.Use();
            _shader.SetMatrix4("uMVP", ref mvp);
            _shader.SetFloat("uOverride", 0f);
            GL.Disable(EnableCap.CullFace);
            GL.BindVertexArray(_beadOverhangVao);
            GL.DrawElements(PrimitiveType.Triangles,
                Math.Max(0, Math.Min(_beadOverhangCount, beadCount) - beadFirst),
                DrawElementsType.UnsignedInt, (IntPtr)((long)beadFirst * sizeof(uint)));
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
            GL.DrawElements(PrimitiveType.Triangles, beadCount - beadFirst,
                DrawElementsType.UnsignedInt, (IntPtr)((long)beadFirst * sizeof(uint)));
            GL.Enable(EnableCap.CullFace);
        }

        GL.BindVertexArray(0);
    }

    /// <summary>
    /// Path-edit depth cue: expands each line to a screen-space quad. Near lines are
    /// 2.5x the base width and fully opaque; far lines shrink and fade to ~20%.
    /// </summary>
    private void DrawDepthAwareLines(
        Matrix4 mvp, Vector3 eyeLocal, float viewportW, float viewportH,
        bool selected, bool showExtrusion, bool showLightning, bool showTravel, bool showWipe,
        int extFirst, int extCount, int lgFirst, int lgCount, int trFirst, int trCount,
        int wpFirst, int wpCount)
    {
        if (_depthLineShader is null) return;

        _depthLineShader.Use();
        _depthLineShader.SetMatrix4("uMVP", ref mvp);
        // Screen-space widths (px): near endpoints ~2.5x the far base.
        _depthLineShader.SetFloat("uNearWidthPx", 5.5f);
        _depthLineShader.SetFloat("uFarWidthPx", 2.2f);
        _depthLineShader.SetVector2("uViewport", new Vector2(viewportW, viewportH));

        GL.DepthMask(false); // soft fade without z-fighting thick quads

        if (!selected)
        {
            bool gradient = _colorMode != ToolpathColorMode.Normal;
            _depthLineShader.SetFloat("uOverride", gradient ? 0f : 1f);
            if (!gradient)
                _depthLineShader.SetVector3("uOverrideColor", _unselectedGray);
            if (showExtrusion && extCount > extFirst)
            {
                GL.BindVertexArray(_extrudeVao);
                GL.DrawArrays(PrimitiveType.Lines, extFirst, extCount - extFirst);
            }
            _depthLineShader.SetFloat("uOverride", 0f);
            if (showLightning && lgCount > lgFirst)
            {
                GL.BindVertexArray(_lightningVao);
                GL.DrawArrays(PrimitiveType.Lines, lgFirst, lgCount - lgFirst);
            }
            if (showWipe && wpCount > wpFirst)
            {
                GL.BindVertexArray(_wipeVao);
                GL.DrawArrays(PrimitiveType.Lines, wpFirst, wpCount - wpFirst);
            }
        }
        else
        {
            _depthLineShader.SetFloat("uOverride", 0f);
            if (showExtrusion && extCount > extFirst)
            {
                GL.BindVertexArray(_extrudeVao);
                GL.DrawArrays(PrimitiveType.Lines, extFirst, extCount - extFirst);
            }
            if (showWipe && wpCount > wpFirst)
            {
                GL.BindVertexArray(_wipeVao);
                GL.DrawArrays(PrimitiveType.Lines, wpFirst, wpCount - wpFirst);
            }
            if (showLightning && lgCount > lgFirst)
            {
                GL.BindVertexArray(_lightningVao);
                GL.DrawArrays(PrimitiveType.Lines, lgFirst, lgCount - lgFirst);
            }
            if (showTravel && trCount > trFirst)
            {
                GL.BindVertexArray(_travelVao);
                GL.DrawArrays(PrimitiveType.Lines, trFirst, trCount - trFirst);
            }
        }

        GL.DepthMask(true);
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
        if (_wipeVao          != 0) { GL.DeleteVertexArray(_wipeVao);          GL.DeleteBuffer(_wipeVbo);          }
        if (_ptVao            != 0) { GL.DeleteVertexArray(_ptVao);            GL.DeleteBuffer(_ptVbo);            }
        if (_allPtVao         != 0) { GL.DeleteVertexArray(_allPtVao);         GL.DeleteBuffer(_allPtVbo);         }
        if (_beadVao          != 0) { GL.DeleteVertexArray(_beadVao);          GL.DeleteBuffer(_beadVbo);          }
        if (_beadEbo          != 0) GL.DeleteBuffer(_beadEbo);
        if (_beadOverhangVao  != 0) { GL.DeleteVertexArray(_beadOverhangVao);  GL.DeleteBuffer(_beadOverhangVbo);  }
        if (_orientationVao   != 0) { GL.DeleteVertexArray(_orientationVao);   GL.DeleteBuffer(_orientationVbo);   }
        if (_singularityPtVao != 0) { GL.DeleteVertexArray(_singularityPtVao); GL.DeleteBuffer(_singularityPtVbo); }
        _shader.Dispose();
        _beadShader.Dispose();
        _pointShader.Dispose();
        _depthLineShader?.Dispose();
    }
}
