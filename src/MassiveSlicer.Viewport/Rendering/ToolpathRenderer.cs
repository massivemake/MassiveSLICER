using MassiveSlicer.Core.Models;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Rendering;

/// <summary>
/// Draws a sliced <see cref="Toolpath"/> as coloured line segments.
///
/// <list type="bullet">
///   <item><description><b>Unselected</b> — extrude moves only, drawn in uniform gray; travel moves and seam points hidden.</description></item>
///   <item><description><b>Selected</b> — all moves with per-vertex colour (extrude=blue, travel=green) plus yellow/red seam points.</description></item>
/// </list>
///
/// Depth test is disabled so lines are never occluded by mesh geometry.
/// </summary>
public sealed class ToolpathRenderer : IDisposable
{
    // Two separate VAOs so we can toggle travel visibility without re-uploading.
    private int  _extrudeVao, _extrudeVbo, _extrudeCount;
    private int  _travelVao,  _travelVbo,  _travelCount;
    private int  _ptVao, _ptVbo;
    private int  _pointCount;
    private bool _disposed;

    private readonly Shader _shader;

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

    private static readonly Vector3 TravelColor    = new(0.18f, 0.42f, 0.18f);  // green
    private static readonly Vector3 ExtrudeColor   = new(0.1f,  0.45f, 0.9f);   // blue
    private static readonly Vector3 SeamStart      = new(1.0f,  0.9f,  0.0f);   // yellow
    private static readonly Vector3 SeamEnd        = new(1.0f,  0.2f,  0.1f);   // red
    private static readonly Vector3 UnselectedGray = new(0.38f, 0.38f, 0.38f);  // neutral gray

    public ToolpathRenderer(Toolpath toolpath, System.Numerics.Vector3 origin = default)
    {
        _shader = new Shader(VertSrc, FragSrc);
        Upload(toolpath, origin);
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
        // ── Count extrude and travel moves separately ────────────────────────
        int extrudeCount = 0, travelCount = 0;
        foreach (var layer in toolpath.Layers)
            foreach (var move in layer.Moves)
            {
                if (move.Kind == MoveKind.Extrude) extrudeCount++;
                else                               travelCount++;
            }

        // ── Pack interleaved [x,y,z, r,g,b] per vertex (2 verts per segment) ─
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

        // ── Seam points: start (yellow) and end (red) per contour ───────────
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

    public void Draw(Matrix4 mvp, bool selected = false)
    {
        if (_extrudeCount == 0 || _disposed) return;

        _shader.Use();
        _shader.SetMatrix4("uMVP", ref mvp);

        if (!selected)
        {
            // ── Unselected: extrude moves only, uniform gray ─────────────────
            _shader.SetFloat("uOverride", 1f);
            _shader.SetVector3("uOverrideColor", UnselectedGray);
            GL.BindVertexArray(_extrudeVao);
            GL.DrawArrays(PrimitiveType.Lines, 0, _extrudeCount);
        }
        else
        {
            // ── Selected: per-vertex colours, travel shown, seam points shown ─
            _shader.SetFloat("uOverride", 0f);

            GL.BindVertexArray(_extrudeVao);
            GL.DrawArrays(PrimitiveType.Lines, 0, _extrudeCount);

            if (_travelCount > 0)
            {
                GL.BindVertexArray(_travelVao);
                GL.DrawArrays(PrimitiveType.Lines, 0, _travelCount);
            }

            if (_pointCount > 0)
            {
                GL.PointSize(8f);
                GL.BindVertexArray(_ptVao);
                GL.DrawArrays(PrimitiveType.Points, 0, _pointCount);
                GL.PointSize(1f);
            }
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
        _shader.Dispose();
    }
}
