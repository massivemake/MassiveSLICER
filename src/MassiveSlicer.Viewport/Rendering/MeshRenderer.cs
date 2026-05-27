using MassiveSlicer.Viewport.Scene;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Rendering;

/// <summary>
/// Renders a triangle mesh with a Phong-shaded GLSL shader.
/// Interleaves positions and normals in a single VBO; supports both indexed
/// and non-indexed geometry. Must be created and disposed on the GL thread.
/// </summary>
public sealed class MeshRenderer : IDisposable
{
    private readonly Shader _shader;
    private int  _vao, _vbo, _ebo;
    private int  _count;
    private bool _indexed;
    private bool _disposed;

    /// <summary>RGBA material colour applied at draw time. Initialised from <see cref="MeshData.BaseColor"/>.</summary>
    public Vector4 Color { get; set; }

    /// <summary>Specular intensity multiplier (0 = matte, 1 = mirror-like). Default 0.25.</summary>
    public float SpecularStrength { get; set; } = 0.25f;

    /// <summary>Specular exponent controlling highlight tightness. Default 32.</summary>
    public float Shininess { get; set; } = 32f;

    /// <summary>When true, renders world-space normals as RGB instead of Phong shading.</summary>
    public bool NormalsMode { get; set; }

    /// <summary>
    /// 0 = dielectric (Phong with white specular), 1 = metallic (tinted specular + Fresnel rim).
    /// Intermediate values blend between the two.
    /// </summary>
    public float Metallic { get; set; }

    /// <summary>
    /// When true the fragment shader samples the backdrop equirectangular texture (bound to
    /// unit 1 by SceneRenderer) for environment reflections and diffuse IBL.
    /// </summary>
    public bool HasEnvMap { get; set; }

    /// <summary>CPU-side mesh retained for ray-picking after GPU upload.</summary>
    public MeshData PickingData { get; }

    // ── GLSL source ──────────────────────────────────────────────────────────

    private static readonly string VertSrc = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNormal;

        uniform mat4 uMVP;
        uniform mat4 uModel;
        uniform mat3 uNormalMat;

        out vec3 vWorldPos;
        out vec3 vNormal;

        void main() {
            // Row-vector convention: v * M, consistent with OpenTK upload (transpose=true).
            gl_Position = vec4(aPos, 1.0) * uMVP;
            vWorldPos   = (vec4(aPos, 1.0) * uModel).xyz;
            vNormal     = normalize(aNormal * uNormalMat);
        }
        """;

    private static readonly string FragSrc = """
        #version 330 core
        in vec3 vWorldPos;
        in vec3 vNormal;

        uniform vec3      uLightDir;       // direction TO the light, world space
        uniform vec3      uViewPos;        // camera position, world space
        uniform vec4      uBaseColor;
        uniform float     uSpecular;
        uniform float     uShininess;
        uniform float     uMetallic;       // 0=dielectric, 1=metallic
        uniform float     uLightIntensity; // scales directional light (diffuse + specular)
        uniform int       uShadingMode;    // 0=shaded, 1=normals
        uniform sampler2D uEnvTex;         // equirectangular HDR backdrop (unit 1)
        uniform int       uHasEnv;         // 1 when uEnvTex is valid

        out vec4 fragColor;

        const float PI = 3.14159265;

        vec3 sampleEnv(vec3 dir, float lod) {
            float u = atan(dir.y, dir.x) / (2.0 * PI) + 0.5;
            float v = 0.5 - asin(clamp(dir.z, -1.0, 1.0)) / PI;
            return max(textureLod(uEnvTex, vec2(u, v), lod).rgb, vec3(0.0));
        }

        // 5-tap cross sample to smooth blocky mip boundaries at high LOD values.
        // At LOD 5+ the texture is only 32-64px wide, so a single tap shows visible
        // pixel blocks. The 4 offset taps straddle adjacent texels; spread grows with
        // LOD so sharp reflections (LOD 0-1) are unaffected.
        vec3 sampleEnvSmooth(vec3 dir, float lod) {
            vec3 t = abs(dir.z) < 0.9 ? vec3(0.0, 0.0, 1.0) : vec3(0.0, 1.0, 0.0);
            vec3 r = normalize(cross(t, dir));
            vec3 u = cross(dir, r);
            float s = max(lod - 1.0, 0.0) * 0.07;
            vec3 c = sampleEnv(dir, lod);
            c += sampleEnv(normalize(dir + r * s), lod);
            c += sampleEnv(normalize(dir - r * s), lod);
            c += sampleEnv(normalize(dir + u * s), lod);
            c += sampleEnv(normalize(dir - u * s), lod);
            return c * 0.2;
        }

        void main() {
            vec3 N = normalize(vNormal);

            if (uShadingMode == 1) {
                fragColor = vec4(N * 0.5 + 0.5, 1.0);
                return;
            }

            // sRGB to linear: glTF baseColorFactor is stored in sRGB. Decode it so all
            // lighting is done in linear space and the result is gamma-encoded at output.
            vec3 baseLinear = pow(max(uBaseColor.rgb, vec3(0.0)), vec3(2.2));

            vec3 L    = normalize(uLightDir);
            vec3 V    = normalize(uViewPos - vWorldPos);
            vec3 Rdir = reflect(-L, N);
            vec3 Renv = reflect(-V, N);
            float NdotL = max(dot(N, L), 0.0);
            float NdotV = max(dot(N, V), 0.0);

            float specWeight = mix(uSpecular, 1.0, uMetallic);

            // Keep flat ambient at 0.15 for fully matte materials even when an env map is
            // loaded -- env IBL is skipped for them, so they would otherwise go dark.
            // Shiny/metallic materials use 0.04 since env IBL supplements the ambient.
            float ambient = (uHasEnv == 1 && specWeight > 0.01) ? 0.04 : 0.15;
            float diffuse = NdotL * mix(0.75, 0.08, uMetallic) * uLightIntensity;

            float specRaw  = pow(max(dot(Rdir, V), 0.0), uShininess) * uSpecular * uLightIntensity;
            vec3 specColor = mix(vec3(1.0), baseLinear, uMetallic) * specRaw;

            float fresnel = pow(1.0 - NdotV, 4.0) * uMetallic * 0.75 * uLightIntensity;

            vec3 color = baseLinear * (ambient + diffuse) + specColor + fresnel;

            // Environment IBL -- only for shiny/metallic materials.
            // Matte objects (specWeight <= 0.01) use flat ambient only; the directional
            // variation from envDiff sampling reads as sheen on otherwise flat surfaces.
            if (uHasEnv == 1 && specWeight > 0.01) {
                float roughness = 1.0 - clamp(log2(max(uShininess, 1.0)) / 5.0, 0.0, 1.0);
                vec3 F0 = mix(vec3(0.04), baseLinear, uMetallic);

                // Dielectrics (metallic=0) always start at LOD 5 so they never show
                // a sharp reflection. Pure metals (metallic=1) start at LOD 0 and can
                // be mirror-like. Roughness adds further blur on top of that baseline.
                float minLod  = mix(5.0, 0.0, uMetallic);
                float specLod = minLod + roughness * (6.0 - minLod);
                vec3 envSpec  = sampleEnvSmooth(Renv, specLod);
                color += envSpec * F0 * specWeight;

                vec3 envDiff = sampleEnv(N, 7.0);
                color += envDiff * baseLinear * (1.0 - uMetallic) * 0.15;
            }

            // Linear to sRGB: gamma-encode for display.
            fragColor = vec4(pow(max(color, vec3(0.0)), vec3(1.0 / 2.2)), uBaseColor.a);
        }
        """;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Uploads <paramref name="data"/> to the GPU.
    /// Must be called on the OpenGL thread after the context is current.
    /// </summary>
    public MeshRenderer(MeshData data)
    {
        PickingData = data;
        Color       = data.BaseColor;
        Metallic    = data.Metallic;

        float smoothness = 1f - data.Roughness;
        Shininess        = MathF.Pow(2f, smoothness * 5f);
        SpecularStrength = smoothness * (0.25f + data.Metallic * 0.5f);

        _shader = new Shader(VertSrc, FragSrc);
        Upload(data);
    }

    // ── Draw ──────────────────────────────────────────────────────────────────

    /// <summary>Renders the mesh using the supplied matrices and scene lighting.</summary>
    /// <param name="model">Model-to-world transform.</param>
    /// <param name="mvp">Combined model-view-projection transform.</param>
    /// <param name="viewPos">Camera world position (for specular).</param>
    /// <param name="lightDir">Direction toward the light source, world space.</param>
    /// <param name="lightIntensity">Directional light multiplier (1 = default).</param>
    public void Draw(Matrix4 model, Matrix4 mvp, Vector3 viewPos, Vector3 lightDir, float lightIntensity)
    {
        _shader.Use();
        _shader.SetMatrix4("uMVP",   ref mvp);
        _shader.SetMatrix4("uModel", ref model);

        // Normal matrix: upper-left 3×3 of the model matrix.
        // Correct for rigid-body (rotation + translation) transforms.
        // For non-uniform scaling, this would need to be the inverse-transpose.
        var normalMat = new Matrix3(model);
        _shader.SetMatrix3("uNormalMat", ref normalMat);

        _shader.SetVector3("uLightDir",       lightDir);
        _shader.SetVector3("uViewPos",        viewPos);
        _shader.SetVector4("uBaseColor",      Color);
        _shader.SetFloat("uSpecular",         SpecularStrength);
        _shader.SetFloat("uShininess",        Shininess);
        _shader.SetFloat("uMetallic",         Metallic);
        _shader.SetFloat("uLightIntensity",   lightIntensity);
        _shader.SetInt("uShadingMode",        NormalsMode ? 1 : 0);
        _shader.SetInt("uEnvTex",             1);
        _shader.SetInt("uHasEnv",             HasEnvMap ? 1 : 0);

        GL.BindVertexArray(_vao);
        if (_indexed)
            GL.DrawElements(PrimitiveType.Triangles, _count, DrawElementsType.UnsignedInt, 0);
        else
            GL.DrawArrays(PrimitiveType.Triangles, 0, _count);
        GL.BindVertexArray(0);
    }

    /// <summary>
    /// Issues the draw call using the currently bound shader — no uniform setup.
    /// Used by the selection outline pass in <see cref="SceneRenderer"/>.
    /// </summary>
    internal void DrawRaw()
    {
        GL.BindVertexArray(_vao);
        if (_indexed)
            GL.DrawElements(PrimitiveType.Triangles, _count, DrawElementsType.UnsignedInt, 0);
        else
            GL.DrawArrays(PrimitiveType.Triangles, 0, _count);
        GL.BindVertexArray(0);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _shader.Dispose();
        GL.DeleteVertexArray(_vao);
        GL.DeleteBuffer(_vbo);
        if (_indexed) GL.DeleteBuffer(_ebo);
    }

    // ── Upload ────────────────────────────────────────────────────────────────

    private void Upload(MeshData data)
    {
        // Interleaved layout: [pos.x, pos.y, pos.z, nrm.x, nrm.y, nrm.z, ...]
        int vertexCount = data.Positions.Length;
        var verts = new float[vertexCount * 6];
        for (int i = 0; i < vertexCount; i++)
        {
            verts[i * 6 + 0] = data.Positions[i].X;
            verts[i * 6 + 1] = data.Positions[i].Y;
            verts[i * 6 + 2] = data.Positions[i].Z;
            verts[i * 6 + 3] = data.Normals[i].X;
            verts[i * 6 + 4] = data.Normals[i].Y;
            verts[i * 6 + 5] = data.Normals[i].Z;
        }

        _vao = GL.GenVertexArray();
        _vbo = GL.GenBuffer();

        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, verts.Length * sizeof(float), verts, BufferUsageHint.StaticDraw);

        int stride = 6 * sizeof(float);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
        GL.EnableVertexAttribArray(1);

        if (data.Indices != null)
        {
            _ebo = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, _ebo);
            GL.BufferData(BufferTarget.ElementArrayBuffer, data.Indices.Length * sizeof(uint),
                          data.Indices, BufferUsageHint.StaticDraw);
            _count   = data.Indices.Length;
            _indexed = true;
        }
        else
        {
            _count = vertexCount;
        }

        GL.BindVertexArray(0);
    }
}
