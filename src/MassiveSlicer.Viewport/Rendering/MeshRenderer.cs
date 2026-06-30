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
    private readonly Shader _shader = SharedShaders.MeshPhong;
    private int  _vao, _vbo, _ebo;
    private int  _count;
    private bool _indexed;
    private bool _disposed;

    // PBR material texture handles (0 = none); units 4-8 at draw time.
    private int  _baseColorTex, _mrTex, _normalTex, _aoTex, _emissiveTex;
    private bool _hasUv, _hasTangent;

    /// <summary>RGBA material colour applied at draw time. Initialised from <see cref="MeshData.BaseColor"/>.</summary>
    public Vector4 Color { get; set; }

    /// <summary>Specular intensity multiplier (0 = matte, 1 = mirror-like). Default 0.25.</summary>
    public float SpecularStrength { get; set; } = 0.25f;

    /// <summary>Specular exponent controlling highlight tightness. Default 32.</summary>
    public float Shininess { get; set; } = 32f;

    /// <summary>When true, renders world-space normals as RGB instead of Phong shading.</summary>
    public bool NormalsMode { get; set; }

    /// <summary>
    /// When true, renders adaptive layer-height preview. Textured meshes keep PBR and composite
    /// the heatmap overlay; untextured meshes use heatmap-only shading.
    /// </summary>
    public bool LayerPreviewMode { get; set; }

    /// <summary>When true, uses a cheap Lambert pass (cell robot/stands/tools).</summary>
    public bool FastCellMode { get; set; }

    /// <summary>When true, renders the white matte Arctic presentation shader.</summary>
    public bool ArcticMode { get; set; }

    /// <summary>Bed / floor Z (mm) for Arctic ground-contact darkening.</summary>
    public float FloorZ { get; set; }

    /// <summary>Layer height (mm) used to scale the stripe frequency in LayerPreview mode.</summary>
    public float LayerHeight { get; set; } = 3f;

    /// <summary>World-space Z offset of the first layer (typically the bed Z).</summary>
    public float LayerZOffset { get; set; } = 0f;

    /// <summary>World Z of the first layer boundary (bottom of layer 0) for the heatmap texture.</summary>
    public float LayerZMin { get; set; }

    /// <summary>World Z of the last layer boundary (top of the tallest layer) for the heatmap texture.</summary>
    public float LayerZMax { get; set; }

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

    /// <summary>Number of layer boundaries uploaded to <c>uLayerBoundTex</c> (unit 3). 0 = none.</summary>
    public int LayerBoundaryCount { get; set; }

    // -- PBR material factors (uniforms for the metallic-roughness path) --------

    /// <summary>Roughness factor 0..1 (multiplies the MR texture G channel). Default 1.</summary>
    public float RoughnessFactor { get; set; } = 1f;

    /// <summary>Emissive colour factor, linear RGB.</summary>
    public Vector3 EmissiveFactor { get; set; }

    /// <summary>Tangent-space normal map XY scale.</summary>
    public float NormalScale { get; set; } = 1f;

    /// <summary>AO map strength 0..1.</summary>
    public float OcclusionStrength { get; set; } = 1f;

    /// <summary>0 = opaque, 1 = mask (alpha discard), 2 = blend.</summary>
    public int AlphaModeInt { get; set; }

    /// <summary>Alpha cutoff for mask mode.</summary>
    public float AlphaCutoff { get; set; } = 0.5f;

    /// <summary>
    /// Material debug channel: 0 = off (normal shading); 1..7 map to shader modes 4..10
    /// (Base Color, Metalness, Roughness, Normal Map, AO, Emission, UV checker).
    /// </summary>
    public int MaterialChannel { get; set; }

    /// <summary>When true, material textures are not sampled (factor-only) — used by the
    /// flat preset modes (Clay/Metal/Chrome/…) so they don't tint the file's albedo map.</summary>
    public bool SuppressTextures { get; set; }

    public bool UseBaseColorMap { get; set; } = true;
    public bool UseMetallicRoughnessMap { get; set; } = true;
    public bool UseNormalMap { get; set; } = true;
    public bool UseAoMap { get; set; } = true;
    public bool UseEmissiveMap { get; set; } = true;
    public bool LayerOverlayEnabled { get; set; } = true;
    public float LayerOverlayStrength { get; set; } = 0.62f;

    /// <summary>When true, renders flat (faceted) shading plus a wireframe overlay line pass.</summary>
    public bool WireframeMode { get; set; }

    /// <summary>Final-render exposure multiplier (pre-tonemap). 1 = neutral.</summary>
    public float Exposure { get; set; } = 1f;

    /// <summary>Environment / image-based-lighting gain. 1 = neutral.</summary>
    public float IblGain { get; set; } = 1f;

    /// <summary>CPU-side mesh retained for ray-picking after GPU upload.</summary>
    public MeshData PickingData { get; }

    private readonly bool _renderAsPoints;

    // -- GLSL source ----------------------------------------------------------

    internal static readonly string VertSrc = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in vec2 aUv;
        layout(location = 3) in vec4 aTangent;   // xyz + w handedness

        uniform mat4 uMVP;
        uniform mat4 uModel;
        uniform mat3 uNormalMat;

        out vec3 vWorldPos;
        out vec3 vNormal;
        out vec2 vUv;
        out vec3 vTangent;
        out vec3 vBitangent;

        void main() {
            // Row-vector convention: v * M, consistent with OpenTK upload (transpose=true).
            gl_Position = vec4(aPos, 1.0) * uMVP;
            vWorldPos   = (vec4(aPos, 1.0) * uModel).xyz;
            vNormal     = normalize(aNormal * uNormalMat);
            vUv         = aUv;
            vec3 T      = aTangent.xyz * uNormalMat;
            vTangent    = T;
            vBitangent  = cross(vNormal, T) * aTangent.w;
        }
        """;

    internal static readonly string FragSrc = """
        #version 330 core
        in vec3 vWorldPos;
        in vec3 vNormal;
        in vec2 vUv;
        in vec3 vTangent;
        in vec3 vBitangent;

        uniform vec3      uLightDir;       // direction TO the light, world space
        uniform vec3      uViewPos;        // camera position, world space
        uniform vec4      uBaseColor;      // base colour factor, sRGB
        uniform float     uSpecular;       // legacy (unused by PBR path)
        uniform float     uShininess;      // legacy (unused by PBR path)
        uniform float     uMetallic;       // legacy alias (see uMetallicFactor)
        uniform float     uLightIntensity; // scales the directional light
        uniform int       uShadingMode;    // 0=PBR,1=normals,2=layer,3=fastcell,4..10=debug,11=wire,12=arctic
        uniform int       uLayerOverlay;   // 1 = composite layer heatmap over PBR (textured meshes)
        uniform float     uLayerOverlayStrength;
        uniform float     uLayerHeight;
        uniform float     uLayerZOffset;
        uniform float     uLayerZMin;
        uniform float     uLayerZMax;
        uniform sampler1D uLayerColorTex;  // unit 2
        uniform sampler1D uLayerBoundTex;  // unit 3
        uniform int       uLayerBoundCount;
        uniform sampler2D uEnvTex;         // equirectangular HDR backdrop (unit 1)
        uniform int       uHasEnv;

        // -- PBR material (metallic-roughness) --
        uniform float     uMetallicFactor;
        uniform float     uRoughnessFactor;
        uniform vec3      uEmissiveFactor;
        uniform float     uNormalScale;
        uniform float     uOcclusionStrength;
        uniform int       uAlphaMode;      // 0=opaque,1=mask,2=blend
        uniform float     uAlphaCutoff;
        uniform float     uExposure;       // pre-tonemap exposure (1 = neutral)
        uniform float     uIblGain;        // environment/IBL gain (1 = neutral)
        uniform float     uFloorZ;         // bed surface Z for Arctic grounding (mm)
        uniform int       uWireframe;      // 1 during the wireframe line pass
        uniform int       uHasUv;
        uniform int       uHasTangent;
        uniform sampler2D uBaseColorTex;   uniform int uHasBaseColorTex; // unit 4
        uniform sampler2D uMRTex;          uniform int uHasMRTex;        // unit 5
        uniform sampler2D uNormalTex;      uniform int uHasNormalTex;    // unit 6
        uniform sampler2D uAOTex;          uniform int uHasAOTex;        // unit 7
        uniform sampler2D uEmissiveTex;    uniform int uHasEmissiveTex;  // unit 8

        out vec4 fragColor;

        const float PI = 3.14159265;

        vec3 srgbToLin(vec3 c) { return pow(max(c, vec3(0.0)), vec3(2.2)); }

        // Narkowicz ACES filmic tonemap approximation.
        vec3 aces(vec3 x) {
            const float a = 2.51, b = 0.03, c = 2.43, d = 0.59, e = 0.14;
            return clamp((x * (a * x + b)) / (x * (c * x + d) + e), 0.0, 1.0);
        }

        // Karis analytic split-sum environment BRDF (replaces a BRDF LUT for v1 IBL).
        vec2 envBRDFApprox(float rough, float NoV) {
            const vec4 c0 = vec4(-1.0, -0.0275, -0.572, 0.022);
            const vec4 c1 = vec4(1.0, 0.0425, 1.04, -0.04);
            vec4 r = rough * c0 + c1;
            float a004 = min(r.x * r.x, exp2(-9.28 * NoV)) * r.x + r.y;
            return vec2(-1.04, 1.04) * a004 + r.zw;
        }

        // Binary-search the boundary texture for the world-Z distance to the nearest
        // layer boundary. Returns a large sentinel when no boundary data is loaded.
        // 12 iterations handles up to 4096 boundaries without a loop-bound uniform.
        float distToNearestBound(float z) {
            if (uLayerBoundCount < 2) return 1.0e6;
            int lo = 0, hi = uLayerBoundCount;
            for (int i = 0; i < 12; i++) {
                int mid = (lo + hi) >> 1;
                if (texelFetch(uLayerBoundTex, mid, 0).r <= z) lo = mid + 1;
                else                                            hi = mid;
            }
            float bBelow = texelFetch(uLayerBoundTex, max(lo - 1, 0),                    0).r;
            float bAbove = texelFetch(uLayerBoundTex, min(lo,     uLayerBoundCount - 1), 0).r;
            return min(z - bBelow, bAbove - z);
        }

        // Adaptive layer-height preview: heatmap + screen-space seam lines.
        vec3 evalLayerPreview(vec3 worldPos, vec3 N) {
            float range = uLayerZMax - uLayerZMin;
            vec3  col;
            if (range > 0.001) {
                float t = clamp((worldPos.z - uLayerZMin) / range, 0.0, 1.0);
                col     = texture(uLayerColorTex, t).rgb;

                float distB = distToNearestBound(worldPos.z);
                float dz    = fwidth(worldPos.z);
                float seamW = max(dz * 1.5, 1e-4);
                float seam  = 1.0 - smoothstep(0.0, seamW, distB);
                col = mix(col, vec3(0.04), seam * 0.92);
            } else {
                col = vec3(0.45);
            }
            float NdotL = max(dot(N, normalize(uLightDir)), 0.0);
            return col * (0.30 + 0.70 * NdotL);
        }

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

            // Wireframe overlay line pass -- emit a flat dark edge colour.
            if (uWireframe == 1) {
                fragColor = vec4(0.04, 0.05, 0.07, 1.0);
                return;
            }

            // Flat (faceted) shading: derive the true face normal from screen-space
            // derivatives of world position, so smooth meshes still read as polygons.
            if (uShadingMode == 11) {
                vec3 fn = normalize(cross(dFdx(vWorldPos), dFdy(vWorldPos)));
                if (!gl_FrontFacing) fn = -fn;
                float ndl = max(dot(fn, normalize(uLightDir)), 0.0);
                vec3 c = vec3(0.60) * (0.32 + 0.68 * ndl) * uLightIntensity;
                fragColor = vec4(pow(max(c, vec3(0.0)), vec3(1.0 / 2.2)), 1.0);
                return;
            }

            if (uShadingMode == 1) {
                fragColor = vec4(N * 0.5 + 0.5, 1.0);
                return;
            }

            if (uShadingMode == 3) {
                vec3 L = normalize(uLightDir);
                float NdotL = max(dot(N, L), 0.0);
                vec3 baseLinear = pow(max(uBaseColor.rgb, vec3(0.0)), vec3(2.2));
                vec3 lit = baseLinear * (0.32 + 0.68 * NdotL) * uLightIntensity;
                fragColor = vec4(pow(max(lit, vec3(0.0)), vec3(1.0 / 2.2)), uBaseColor.a);
                return;
            }

            // Rhino-style Arctic: white clay, soft top light, crease AO, no specular/IBL.
            if (uShadingMode == 12) {
                vec3 Nm = N;
                if (!gl_FrontFacing) Nm = -Nm;
                vec3 key = normalize(vec3(0.12, 0.08, 1.0));
                float NdotL = max(dot(Nm, key), 0.0);
                float sky = clamp(Nm.z * 0.5 + 0.5, 0.0, 1.0);
                vec3 hemi = mix(vec3(0.72), vec3(0.94), sky);
                vec3 dN = fwidth(Nm);
                float crease = 1.0 - clamp(length(dN) * 9.0, 0.0, 0.50);
                float cavity = 1.0 - clamp((1.0 - NdotL) * 0.35, 0.0, 0.18);
                vec3 base = vec3(0.93, 0.93, 0.95);
                vec3 lit = base * hemi * (0.82 + 0.18 * NdotL * uLightIntensity) * crease * cavity;
                float h = max(vWorldPos.z - uFloorZ, 0.0);
                float vertGround = exp(-h / 28.0);
                lit *= mix(1.0, 0.78, vertGround * 0.55);
                fragColor = vec4(pow(max(lit, vec3(0.0)), vec3(1.0 / 2.2)), 1.0);
                return;
            }

            if (uShadingMode == 2) {
                // Untextured meshes: heatmap-only preview (no PBR maps to show through).
                fragColor = vec4(evalLayerPreview(vWorldPos, N), 1.0);
                return;
            }

            // ---- Material sampling (shared by PBR mode 0 and debug channels 4..10) ----
            vec3 Nm = N;
            if (!gl_FrontFacing) Nm = -Nm;          // double-sided shading
            bool hasUv = (uHasUv == 1);

            // Albedo in linear space. The sRGB sampler auto-decodes the texture; the factor
            // is sRGB so decode it separately -- never decode the sampled value twice (R2).
            vec3 albedo; float alpha;
            if (uHasBaseColorTex == 1 && hasUv) {
                vec4 bc = texture(uBaseColorTex, vUv);
                albedo  = bc.rgb * srgbToLin(uBaseColor.rgb);
                alpha   = bc.a * uBaseColor.a;
            } else {
                albedo = srgbToLin(uBaseColor.rgb);
                alpha  = uBaseColor.a;
            }
            if (uAlphaMode == 1 && alpha < uAlphaCutoff) discard;

            float metal = uMetallicFactor;
            float rough = uRoughnessFactor;
            if (uHasMRTex == 1 && hasUv) {
                vec3 mr = texture(uMRTex, vUv).rgb;
                rough *= mr.g;                       // glTF: roughness in G
                metal *= mr.b;                       // glTF: metallic  in B
            }
            rough = clamp(rough, 0.04, 1.0);
            metal = clamp(metal, 0.0, 1.0);

            float ao = 1.0;
            if (uHasAOTex == 1 && hasUv)
                ao = mix(1.0, texture(uAOTex, vUv).r, uOcclusionStrength);

            vec3 emissive = uEmissiveFactor;
            if (uHasEmissiveTex == 1 && hasUv)
                emissive *= texture(uEmissiveTex, vUv).rgb;

            // Tangent-space normal map -> world.
            if (uHasNormalTex == 1 && uHasTangent == 1 && hasUv) {
                vec3 nt = texture(uNormalTex, vUv).xyz * 2.0 - 1.0;
                nt.xy *= uNormalScale;
                mat3 TBN = mat3(normalize(vTangent), normalize(vBitangent), Nm);
                Nm = normalize(TBN * nt);
            }

            // ---- Debug channel views (inspector) ----
            if (uShadingMode == 4) { fragColor = vec4(pow(max(albedo, vec3(0.0)), vec3(1.0/2.2)), 1.0); return; }
            if (uShadingMode == 5) { fragColor = vec4(vec3(metal), 1.0); return; }
            if (uShadingMode == 6) { fragColor = vec4(vec3(rough), 1.0); return; }
            if (uShadingMode == 7) {
                vec3 nv = (uHasNormalTex == 1 && hasUv) ? texture(uNormalTex, vUv).xyz : (Nm * 0.5 + 0.5);
                fragColor = vec4(nv, 1.0); return;
            }
            if (uShadingMode == 8) { fragColor = vec4(vec3(ao), 1.0); return; }
            if (uShadingMode == 9) { fragColor = vec4(pow(max(emissive, vec3(0.0)), vec3(1.0/2.2)), 1.0); return; }
            if (uShadingMode == 10) {
                if (!hasUv) { fragColor = vec4(1.0, 0.0, 1.0, 1.0); return; }
                vec2 g = floor(vUv * 10.0);
                float c = mod(g.x + g.y, 2.0);
                vec3 col = mix(vec3(0.12), vec3(0.85), c) * (vec3(fract(vUv), 1.0) * 0.5 + 0.5);
                fragColor = vec4(col, 1.0); return;
            }

            // ---- Cook-Torrance metallic-roughness (mode 0, "Final Render") ----
            vec3 V = normalize(uViewPos - vWorldPos);
            vec3 L = normalize(uLightDir);
            vec3 H = normalize(V + L);
            float NdotL = max(dot(Nm, L), 0.0);
            float NdotV = max(dot(Nm, V), 1e-4);
            float NdotH = max(dot(Nm, H), 0.0);
            float VdotH = max(dot(V, H), 0.0);

            vec3 F0 = mix(vec3(0.04), albedo, metal);
            float a  = rough * rough;
            float a2 = a * a;
            float dd = NdotH * NdotH * (a2 - 1.0) + 1.0;
            float D  = a2 / max(PI * dd * dd, 1e-7);
            float kg = (rough + 1.0); kg = kg * kg / 8.0;
            float G  = (NdotV / (NdotV * (1.0 - kg) + kg)) * (NdotL / (NdotL * (1.0 - kg) + kg));
            vec3  F  = F0 + (1.0 - F0) * pow(1.0 - VdotH, 5.0);

            vec3 spec   = (D * G * F) / max(4.0 * NdotV * NdotL, 1e-4);
            vec3 kd     = (1.0 - F) * (1.0 - metal);
            vec3 direct = (kd * albedo / PI + spec) * NdotL * uLightIntensity;

            vec3 ambient;
            if (uHasEnv == 1) {
                vec3 diffIBL     = sampleEnv(Nm, 7.0) * albedo * (1.0 - metal);
                vec3 prefiltered = sampleEnvSmooth(reflect(-V, Nm), rough * 6.0);
                vec2 ab          = envBRDFApprox(rough, NdotV);
                vec3 specIBL     = prefiltered * (F0 * ab.x + ab.y);
                ambient = (diffIBL + specIBL) * ao * uIblGain;
            } else {
                ambient = albedo * 0.15 * ao;
            }

            vec3 color = direct + ambient + emissive;
            if (uLayerOverlay == 1)
                color = mix(color, evalLayerPreview(vWorldPos, Nm), uLayerOverlayStrength);
            color *= uExposure;                      // user exposure (1 = neutral)
            color = aces(color);                     // ACES filmic tonemap
            fragColor = vec4(pow(max(color, vec3(0.0)), vec3(1.0 / 2.2)), alpha);
        }
        """;

    // -- Lifecycle -------------------------------------------------------------

    /// <summary>
    /// Uploads <paramref name="data"/> to the GPU.
    /// Must be called on the OpenGL thread after the context is current.
    /// </summary>
    public MeshRenderer(MeshData data)
    {
        PickingData      = data;
        _renderAsPoints  = data.RenderAsPoints;
        Color            = data.BaseColor;
        Metallic    = data.Metallic;

        float smoothness = 1f - data.Roughness;
        Shininess        = MathF.Pow(2f, smoothness * 5f);
        SpecularStrength = smoothness * (0.25f + data.Metallic * 0.5f);

        RoughnessFactor = data.Roughness;
        _hasUv          = data.Uvs is not null;
        _hasTangent     = data.Tangents is not null;

        // Acquire pooled GPU textures for the PBR material (GL thread — same as Upload).
        if (data.Material is { } mat)
        {
            Color             = mat.BaseColorFactor;
            Metallic          = mat.MetallicFactor;
            RoughnessFactor   = mat.RoughnessFactor;
            EmissiveFactor    = mat.EmissiveFactor;
            NormalScale       = mat.NormalScale;
            OcclusionStrength = mat.OcclusionStrength;
            AlphaModeInt      = (int)mat.AlphaMode;
            AlphaCutoff       = mat.AlphaCutoff;

            if (mat.BaseColor         is { } t0) _baseColorTex = GpuTextureCache.Acquire(t0);
            if (mat.MetallicRoughness is { } t1) _mrTex        = GpuTextureCache.Acquire(t1);
            if (mat.Normal            is { } t2) _normalTex    = GpuTextureCache.Acquire(t2);
            if (mat.Occlusion         is { } t3) _aoTex        = GpuTextureCache.Acquire(t3);
            if (mat.Emissive          is { } t4) _emissiveTex  = GpuTextureCache.Acquire(t4);
        }

        Upload(data);
    }

    // -- Draw ------------------------------------------------------------------

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
        bool hasPbrMaps   = _baseColorTex != 0 || _mrTex != 0;
        bool layerOverlay = LayerPreviewMode && hasPbrMaps && LayerOverlayEnabled;
        int shadingMode = ArcticMode                       ? 12
                        : LayerPreviewMode && !hasPbrMaps ? 2
                        : NormalsMode                     ? 1
                        : FastCellMode                    ? 3
                        : WireframeMode                   ? 11
                        : MaterialChannel > 0 ? 3 + MaterialChannel   // 1..7 -> 4..10
                        : 0;
        _shader.SetInt("uShadingMode",           shadingMode);
        _shader.SetInt("uLayerOverlay",          layerOverlay ? 1 : 0);
        _shader.SetFloat("uLayerOverlayStrength", LayerOverlayStrength);
        _shader.SetInt("uWireframe",             0);

        // PBR material factors.
        _shader.SetFloat("uMetallicFactor",   Metallic);
        _shader.SetFloat("uRoughnessFactor",  RoughnessFactor);
        _shader.SetVector3("uEmissiveFactor", EmissiveFactor);
        _shader.SetFloat("uNormalScale",      NormalScale);
        _shader.SetFloat("uOcclusionStrength", OcclusionStrength);
        _shader.SetInt("uAlphaMode",          AlphaModeInt);
        _shader.SetFloat("uAlphaCutoff",      AlphaCutoff);
        _shader.SetFloat("uExposure",         Exposure);
        _shader.SetFloat("uIblGain",          IblGain);
        _shader.SetFloat("uFloorZ",           FloorZ);
        _shader.SetInt("uHasUv",              _hasUv ? 1 : 0);
        _shader.SetInt("uHasTangent",         _hasTangent ? 1 : 0);

        // Bind material maps to units 4-8 (1=env, 2=heatmap, 3=boundary already used).
        BindMaterialTex(4, "uBaseColorTex", "uHasBaseColorTex", _baseColorTex, UseBaseColorMap);
        BindMaterialTex(5, "uMRTex",        "uHasMRTex",        _mrTex,        UseMetallicRoughnessMap);
        BindMaterialTex(6, "uNormalTex",    "uHasNormalTex",    _normalTex,    UseNormalMap);
        BindMaterialTex(7, "uAOTex",        "uHasAOTex",        _aoTex,        UseAoMap);
        BindMaterialTex(8, "uEmissiveTex",  "uHasEmissiveTex",  _emissiveTex,  UseEmissiveMap);
        GL.ActiveTexture(TextureUnit.Texture0);
        _shader.SetFloat("uLayerHeight",      LayerHeight);
        _shader.SetFloat("uLayerZOffset",     LayerZOffset);
        _shader.SetFloat("uLayerZMin",        LayerZMin);
        _shader.SetFloat("uLayerZMax",        LayerZMax);
        _shader.SetInt("uLayerColorTex",      2);
        _shader.SetInt("uLayerBoundTex",      3);
        _shader.SetInt("uLayerBoundCount",    LayerBoundaryCount);
        _shader.SetInt("uEnvTex",             1);
        _shader.SetInt("uHasEnv",             HasEnvMap ? 1 : 0);

        GL.BindVertexArray(_vao);
        var primitive = _renderAsPoints ? PrimitiveType.Points : PrimitiveType.Triangles;
        if (_renderAsPoints) GL.PointSize(2.5f);
        if (_indexed)
            GL.DrawElements(primitive, _count, DrawElementsType.UnsignedInt, 0);
        else
            GL.DrawArrays(primitive, 0, _count);

        // Wireframe overlay: redraw the triangles as lines, pulled slightly toward the
        // camera so the edges sit on top of the flat fill.
        if (WireframeMode && !_renderAsPoints)
        {
            _shader.SetInt("uWireframe", 1);
            GL.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Line);
            GL.Enable(EnableCap.PolygonOffsetLine);
            GL.PolygonOffset(-1f, -1f);
            if (_indexed)
                GL.DrawElements(PrimitiveType.Triangles, _count, DrawElementsType.UnsignedInt, 0);
            else
                GL.DrawArrays(PrimitiveType.Triangles, 0, _count);
            GL.Disable(EnableCap.PolygonOffsetLine);
            GL.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);
            _shader.SetInt("uWireframe", 0);
        }

        GL.BindVertexArray(0);
    }

    private void BindMaterialTex(int unit, string samplerUniform, string flagUniform, int handle, bool layerEnabled)
    {
        GL.ActiveTexture(TextureUnit.Texture0 + unit);
        GL.BindTexture(TextureTarget.Texture2D, handle);   // 0 unbinds when no map
        _shader.SetInt(samplerUniform, unit);
        _shader.SetInt(flagUniform, (handle != 0 && !SuppressTextures && layerEnabled) ? 1 : 0);
    }

    /// <summary>
    /// Issues the draw call using the currently bound shader -- no uniform setup.
    /// Used by the selection outline pass in <see cref="SceneRenderer"/>.
    /// </summary>
    internal void DrawRaw()
    {
        GL.BindVertexArray(_vao);
        var primitive = _renderAsPoints ? PrimitiveType.Points : PrimitiveType.Triangles;
        if (_renderAsPoints) GL.PointSize(2.5f);
        if (_indexed)
            GL.DrawElements(primitive, _count, DrawElementsType.UnsignedInt, 0);
        else
            GL.DrawArrays(primitive, 0, _count);
        GL.BindVertexArray(0);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        GpuTextureCache.Release(_baseColorTex);
        GpuTextureCache.Release(_mrTex);
        GpuTextureCache.Release(_normalTex);
        GpuTextureCache.Release(_aoTex);
        GpuTextureCache.Release(_emissiveTex);

        GL.DeleteVertexArray(_vao);
        GL.DeleteBuffer(_vbo);
        if (_indexed) GL.DeleteBuffer(_ebo);
    }

    // -- Upload ----------------------------------------------------------------

    private void Upload(MeshData data)
    {
        // Interleaved layout: [pos(3), normal(3), uv(2), tangent(4)] = 12 floats.
        // UV/tangent are zero-filled when absent; sampling is gated by uHasUv/uHasTangent.
        const int floatsPerVertex = 12;
        int vertexCount = data.Positions.Length;
        var verts = new float[vertexCount * floatsPerVertex];
        var uvs = data.Uvs;
        var tangents = data.Tangents;
        for (int i = 0; i < vertexCount; i++)
        {
            int o = i * floatsPerVertex;
            verts[o + 0] = data.Positions[i].X;
            verts[o + 1] = data.Positions[i].Y;
            verts[o + 2] = data.Positions[i].Z;
            verts[o + 3] = data.Normals[i].X;
            verts[o + 4] = data.Normals[i].Y;
            verts[o + 5] = data.Normals[i].Z;
            if (uvs is not null)
            {
                verts[o + 6] = uvs[i].X;
                verts[o + 7] = uvs[i].Y;
            }
            if (tangents is not null)
            {
                verts[o + 8]  = tangents[i].X;
                verts[o + 9]  = tangents[i].Y;
                verts[o + 10] = tangents[i].Z;
                verts[o + 11] = tangents[i].W;
            }
        }

        _vao = GL.GenVertexArray();
        _vbo = GL.GenBuffer();

        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, verts.Length * sizeof(float), verts, BufferUsageHint.StaticDraw);

        int stride = floatsPerVertex * sizeof(float);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, 6 * sizeof(float));
        GL.EnableVertexAttribArray(2);
        GL.VertexAttribPointer(3, 4, VertexAttribPointerType.Float, false, stride, 8 * sizeof(float));
        GL.EnableVertexAttribArray(3);

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
