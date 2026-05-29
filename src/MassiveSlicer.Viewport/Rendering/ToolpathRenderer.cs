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

    private static readonly Vector3 TravelColor    = new(0.18f, 0.42f, 0.18f);  // green
    private static readonly Vector3 ExtrudeColor   = new(0.1f,  0.45f, 0.9f);   // blue
    private static readonly Vector3 SeamStart      = new(1.0f,  0.9f,  0.0f);   // yellow
    private static readonly Vector3 SeamEnd        = new(1.0f,  0.2f,  0.1f);   // red
    private static readonly Vector3 UnselectedGray = new(0.38f, 0.38f, 0.38f);  // neutral gray

    public ToolpathRenderer(Toolpath toolpath, NVec3 origin = default,
        float beadWidth = 6f, float layerHeight = 3f, NVec3 materialColor = default)
    {
        _shader     = new Shader(VertSrc,     FragSrc);
        _beadShader = new Shader(BeadVertSrc, BeadFragSrc);
        Upload(toolpath, origin);
        UploadBead(toolpath, origin, beadWidth, layerHeight,
            materialColor == NVec3.Zero ? new NVec3(0.1f, 0.45f, 0.9f) : materialColor);
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

    private void Upload(Toolpath toolpath, System.Numerics.Vector3 origin)
    {
        // -- Count extrude and travel moves separately ------------------------
        int extrudeCount = 0, travelCount = 0;
        foreach (var layer in toolpath.Layers)
            foreach (var move in layer.Moves)
            {
                if (move.Kind == MoveKind.Extrude) extrudeCount++;
                else                               travelCount++;
            }

        // -- Pack interleaved [x,y,z, r,g,b] per vertex (2 verts per segment) -
        // Points stored in local space so the node's LocalTransform(origin) places them correctly.
        var extData = new float[extrudeCount * 2 * 6];
        var trData  = new float[travelCount  * 2 * 6];
        int ei = 0, ti = 0;

        void WriteVert(float[] buf, ref int idx, System.Numerics.Vector3 p, Vector3 c)
        {
            buf[idx++] = p.X - origin.X; buf[idx++] = p.Y - origin.Y; buf[idx++] = p.Z - origin.Z;
            buf[idx++] = c.X;            buf[idx++] = c.Y;            buf[idx++] = c.Z;
        }

        foreach (var layer in toolpath.Layers)
        {
            foreach (var move in layer.Moves)
            {
                if (move.Kind == MoveKind.Extrude)
                {
                    WriteVert(extData, ref ei, move.From, ExtrudeColor);
                    WriteVert(extData, ref ei, move.To,   ExtrudeColor);
                }
                else
                {
                    WriteVert(trData, ref ti, move.From, TravelColor);
                    WriteVert(trData, ref ti, move.To,   TravelColor);
                }
            }
        }

        if (extrudeCount > 0)
        {
            (_extrudeVao, _extrudeVbo) = BuildVao(extData);
            _extrudeCount = extrudeCount * 2;
        }

        if (travelCount > 0)
        {
            (_travelVao, _travelVbo) = BuildVao(trData);
            _travelCount = travelCount * 2;
        }

        // -- Seam points: start (yellow) and end (red) per contour -----------
        int totalContours = 0;
        foreach (var layer in toolpath.Layers)
        {
            bool inEx = false;
            foreach (var m in layer.Moves)
            {
                if (m.Kind == MoveKind.Extrude && !inEx) { totalContours++; inEx = true; }
                else if (m.Kind == MoveKind.Travel)       inEx = false;
            }
        }

        var ptData = new float[totalContours * 2 * 6];
        int pi = 0;

        void WritePoint(System.Numerics.Vector3 p, Vector3 col)
        {
            ptData[pi++] = p.X - origin.X; ptData[pi++] = p.Y - origin.Y; ptData[pi++] = p.Z - origin.Z;
            ptData[pi++] = col.X;           ptData[pi++] = col.Y;           ptData[pi++] = col.Z;
        }

        foreach (var layer in toolpath.Layers)
        {
            bool inExtrude = false;
            System.Numerics.Vector3 extStart = default, extEnd = default;

            foreach (var move in layer.Moves)
            {
                if (move.Kind == MoveKind.Extrude)
                {
                    if (!inExtrude) { extStart = move.From; inExtrude = true; }
                    extEnd = move.To;
                }
                else
                {
                    if (inExtrude)
                    {
                        WritePoint(extStart, SeamStart);
                        WritePoint(extEnd,   SeamEnd);
                        inExtrude = false;
                    }
                }
            }
            if (inExtrude)
            {
                WritePoint(extStart, SeamStart);
                WritePoint(extEnd,   SeamEnd);
            }
        }

        _pointCount = pi / 6;
        if (_pointCount > 0)
            (_ptVao, _ptVbo) = BuildVao(ptData);
    }

    private void UploadBead(Toolpath toolpath, NVec3 origin,
        float beadWidth, float layerHeight, NVec3 matColor)
    {
        _beadMaterialColor = new Vector3(matColor.X, matColor.Y, matColor.Z);

        int extrudeCount = 0;
        foreach (var layer in toolpath.Layers)
            foreach (var move in layer.Moves)
                if (move.Kind == MoveKind.Extrude) extrudeCount++;
        if (extrudeCount == 0) return;

        // 6 faces × 2 triangles × 3 verts × 6 floats (pos + normal) per segment
        var data = new float[extrudeCount * 36 * 6];
        int di = 0;
        float hw = beadWidth   * 0.5f;
        float hh = layerHeight * 0.5f;

        void WV(NVec3 p, NVec3 n)
        {
            data[di++] = p.X - origin.X; data[di++] = p.Y - origin.Y; data[di++] = p.Z - origin.Z;
            data[di++] = n.X;            data[di++] = n.Y;            data[di++] = n.Z;
        }

        foreach (var layer in toolpath.Layers)
        {
            foreach (var move in layer.Moves)
            {
                if (move.Kind != MoveKind.Extrude) continue;
                var A = move.From;
                var B = move.To;
                var diff = B - A;
                if (diff.LengthSquared() < 1e-12f) continue;

                var fwd   = NVec3.Normalize(diff);
                var right = NVec3.Cross(fwd, NVec3.UnitZ);
                if (right.LengthSquared() < 1e-6f)
                    right = NVec3.Cross(fwd, NVec3.UnitX);
                right = NVec3.Normalize(right);
                var up = NVec3.UnitZ;

                var rHw = right * hw;
                var uHh = up    * hh;

                // 8 box corners (Back/Front × Left/Right × Bottom/Top)
                NVec3 BLB = A - rHw - uHh, BRB = A + rHw - uHh;
                NVec3 BLT = A - rHw + uHh, BRT = A + rHw + uHh;
                NVec3 FLB = B - rHw - uHh, FRB = B + rHw - uHh;
                NVec3 FLT = B - rHw + uHh, FRT = B + rHw + uHh;

                // Averaged corner normals for smooth interpolation across faces
                NVec3 nBLB = NVec3.Normalize(-fwd - right - up);
                NVec3 nBRB = NVec3.Normalize(-fwd + right - up);
                NVec3 nBLT = NVec3.Normalize(-fwd - right + up);
                NVec3 nBRT = NVec3.Normalize(-fwd + right + up);
                NVec3 nFLB = NVec3.Normalize( fwd - right - up);
                NVec3 nFRB = NVec3.Normalize( fwd + right - up);
                NVec3 nFLT = NVec3.Normalize( fwd - right + up);
                NVec3 nFRT = NVec3.Normalize( fwd + right + up);

                // Top
                WV(BLT, nBLT); WV(BRT, nBRT); WV(FRT, nFRT);
                WV(BLT, nBLT); WV(FRT, nFRT); WV(FLT, nFLT);

                // Bottom
                WV(BRB, nBRB); WV(BLB, nBLB); WV(FLB, nFLB);
                WV(BRB, nBRB); WV(FLB, nFLB); WV(FRB, nFRB);

                // Left side
                WV(BLB, nBLB); WV(BLT, nBLT); WV(FLT, nFLT);
                WV(BLB, nBLB); WV(FLT, nFLT); WV(FLB, nFLB);

                // Right side
                WV(BRT, nBRT); WV(BRB, nBRB); WV(FRB, nFRB);
                WV(BRT, nBRT); WV(FRB, nFRB); WV(FRT, nFRT);

                // Back cap
                WV(BRB, nBRB); WV(BRT, nBRT); WV(BLT, nBLT);
                WV(BRB, nBRB); WV(BLT, nBLT); WV(BLB, nBLB);

                // Front cap
                WV(FLB, nFLB); WV(FLT, nFLT); WV(FRT, nFRT);
                WV(FLB, nFLB); WV(FRT, nFRT); WV(FRB, nFRB);
            }
        }

        _beadCount = di / 6;
        if (_beadCount > 0)
        {
            float[] upload = di == data.Length ? data : data[..di];
            (_beadVao, _beadVbo) = BuildVao(upload);
        }
    }

    public void Draw(Matrix4 mvp, bool selected = false,
                     bool showExtrusion = true, bool showTravel = true, bool showSeam = true,
                     bool showBead = false)
    {
        if (_disposed) return;

        _shader.Use();
        _shader.SetMatrix4("uMVP", ref mvp);

        if (!selected)
        {
            if (showExtrusion && _extrudeCount > 0)
            {
                _shader.SetFloat("uOverride", 1f);
                _shader.SetVector3("uOverrideColor", UnselectedGray);
                GL.BindVertexArray(_extrudeVao);
                GL.DrawArrays(PrimitiveType.Lines, 0, _extrudeCount);
            }
        }
        else
        {
            _shader.SetFloat("uOverride", 0f);

            if (showExtrusion && _extrudeCount > 0)
            {
                GL.BindVertexArray(_extrudeVao);
                GL.DrawArrays(PrimitiveType.Lines, 0, _extrudeCount);
            }

            if (showTravel && _travelCount > 0)
            {
                GL.BindVertexArray(_travelVao);
                GL.DrawArrays(PrimitiveType.Lines, 0, _travelCount);
            }

            if (showSeam && _pointCount > 0)
            {
                GL.PointSize(8f);
                GL.BindVertexArray(_ptVao);
                GL.DrawArrays(PrimitiveType.Points, 0, _pointCount);
                GL.PointSize(1f);
            }
        }

        if (showBead && _beadCount > 0)
        {
            _beadShader.Use();
            _beadShader.SetMatrix4("uMVP", ref mvp);
            _beadShader.SetVector3("uColor", _beadMaterialColor);
            GL.Disable(EnableCap.CullFace);
            GL.BindVertexArray(_beadVao);
            GL.DrawArrays(PrimitiveType.Triangles, 0, _beadCount);
            GL.Enable(EnableCap.CullFace);
        }

        GL.BindVertexArray(0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_extrudeVao != 0) { GL.DeleteVertexArray(_extrudeVao); GL.DeleteBuffer(_extrudeVbo); }
        if (_travelVao  != 0) { GL.DeleteVertexArray(_travelVao);  GL.DeleteBuffer(_travelVbo);  }
        if (_ptVao      != 0) { GL.DeleteVertexArray(_ptVao);      GL.DeleteBuffer(_ptVbo);      }
        if (_beadVao    != 0) { GL.DeleteVertexArray(_beadVao);    GL.DeleteBuffer(_beadVbo);    }
        _shader.Dispose();
        _beadShader.Dispose();
    }
}
