using MassiveSlicer.Viewport.Scene;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Rendering;

/// <summary>
/// Projects nearby geometry onto contact planes as soft multiply shadows (Rhino-style grounding).
/// </summary>
public sealed class ContactShadowRenderer : IDisposable
{
    private readonly Shader _silhouetteShader;
    private readonly Shader _decalShader;
    private int _fbo;
    private int _tex;
    private int _decalVao;
    private int _decalVbo;
    private int _texSize;
    private bool _disposed;

    private const int MaxTexSize = 2048;
    private const float BaseBlurLod = 8.0f;
    private const float BaseUvScale = 0.5f;
    private const float BaseShadowStrength = 1.0f;
    private const float BaseDarkMultiply = 0.32f;
    private const float BaseArcticMultiply = 0.55f;
    private const float BaseArcticStrength = 0.58f;

    /// <summary>When true, shadows are soft gray on white Arctic ground.</summary>
    public bool ArcticPresentation { get; set; }

    /// <summary>Spread multiplier (1 = default tuned size).</summary>
    public float SizeScale { get; set; } = 1f;

    /// <summary>Darkness multiplier (1 = default tuned strength).</summary>
    public float DarknessScale { get; set; } = 1f;

    /// <summary>Blur multiplier (1 = default tuned softness).</summary>
    public float BlurScale { get; set; } = 1f;

    private static readonly string SilhouetteVertSrc = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        uniform mat4 uModel;
        uniform mat4 uFootprint;
        out float vWorldZ;
        void main() {
            vec4 w = vec4(aPos, 1.0) * uModel;
            vWorldZ = w.z;
            gl_Position = vec4(w.x, w.y, 0.0, 1.0) * uFootprint;
        }
        """;

    // Only geometry within the contact band above the floor contributes (pedestal base, bed rim).
    private static readonly string SilhouetteFragSrc = """
        #version 330 core
        in float vWorldZ;
        uniform float uBandTop;
        out vec4 fragColor;
        void main() {
            if (vWorldZ > uBandTop) discard;
            fragColor = vec4(1.0);
        }
        """;

    private static readonly string DecalVertSrc = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec2 aUV;
        uniform mat4 uMVP;
        out vec2 vUV;
        void main() {
            gl_Position = vec4(aPos, 1.0) * uMVP;
            vUV = aUV;
        }
        """;

    private static readonly string DecalFragSrc = """
        #version 330 core
        in vec2 vUV;
        uniform sampler2D uShadowTex;
        uniform float uBlurLod;
        uniform float uUvScale;
        uniform float uDarken;
        uniform float uStrength;
        out vec4 fragColor;
        void main() {
            vec2 uv = (vUV - 0.5) * uUvScale + 0.5;
            float mask = textureLod(uShadowTex, uv, uBlurLod).r;
            mask = pow(mask, 1.15);
            float soft = smoothstep(0.02, 0.98, mask);
            float edge = min(min(uv.x, 1.0 - uv.x), min(uv.y, 1.0 - uv.y));
            soft *= smoothstep(0.0, 0.06, edge);
            float factor = mix(1.0, uDarken, soft * uStrength);
            fragColor = vec4(vec3(factor), 1.0);
        }
        """;

    public ContactShadowRenderer()
    {
        _silhouetteShader = new Shader(SilhouetteVertSrc, SilhouetteFragSrc);
        _decalShader      = new Shader(DecalVertSrc, DecalFragSrc);

        _decalVao = GL.GenVertexArray();
        _decalVbo = GL.GenBuffer();
        GL.BindVertexArray(_decalVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _decalVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, 6 * 5 * sizeof(float), IntPtr.Zero,
            BufferUsageHint.DynamicDraw);
        int stride = 5 * sizeof(float);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
        GL.EnableVertexAttribArray(1);
        GL.BindVertexArray(0);
    }

    public void Draw(SceneNode sceneRoot, Matrix4 viewProj)
    {
        float sizeScale = Math.Clamp(SizeScale, 0.25f, 3f);
        float blurScale = Math.Clamp(BlurScale, 0f, 3f);
        float paddingMm = ContactShadowBuilder.DefaultPaddingMm * sizeScale * (1f + blurScale * 0.45f);
        var passes = ContactShadowBuilder.BuildProjections(sceneRoot, paddingMm);
        if (passes.Count == 0)
            return;

        GL.GetInteger(GetPName.DrawFramebufferBinding, out int prevFbo);
        int[] prevViewport = new int[4];
        GL.GetInteger(GetPName.Viewport, prevViewport);

        EnsureFbo();

        foreach (var proj in passes)
        {
            var silhouetteProj = InsetForSilhouette(proj, blurScale, sizeScale);
            RenderSilhouette(sceneRoot, silhouetteProj);

            // Composite the blurred silhouette onto the scene colour buffer (not the off-screen FBO).
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, prevFbo);
            GL.Viewport(prevViewport[0], prevViewport[1], prevViewport[2], prevViewport[3]);
            DrawGroundDecal(proj, viewProj);
        }

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, prevFbo);
        GL.Viewport(prevViewport[0], prevViewport[1], prevViewport[2], prevViewport[3]);
    }

    private void RenderSilhouette(SceneNode sceneRoot, ContactShadowBuilder.ShadowProjection proj)
    {
        var footprint = BuildFootprintMatrix(proj);
        float bandTop = proj.FloorZ + ContactShadowBuilder.ContactBandMm;

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        GL.Viewport(0, 0, _texSize, _texSize);
        GL.ClearColor(0f, 0f, 0f, 0f);
        GL.Clear(ClearBufferMask.ColorBufferBit);

        GL.Disable(EnableCap.DepthTest);
        GL.Disable(EnableCap.Blend);
        GL.Disable(EnableCap.CullFace);

        _silhouetteShader.Use();
        _silhouetteShader.SetMatrix4("uFootprint", ref footprint);
        _silhouetteShader.SetFloat("uBandTop", bandTop);

        foreach (var child in sceneRoot.Children)
        {
            if (!ContactShadowBuilder.ShouldCastSilhouette(child, proj.FloorZ))
                continue;

            DrawSilhouetteSubtree(child);
        }

        ApplyShadowTextureParameters();
        GL.BindTexture(TextureTarget.Texture2D, _tex);
        GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
        GL.BindTexture(TextureTarget.Texture2D, 0);
    }

    private static void ApplyShadowTextureParameters()
    {
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS,
            (int)TextureWrapMode.ClampToBorder);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT,
            (int)TextureWrapMode.ClampToBorder);
        float[] border = [0f, 0f, 0f, 0f];
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureBorderColor, border);
    }

    private void DrawSilhouetteSubtree(SceneNode node)
    {
        if (!node.Visible) return;

        if (node.Mesh is not null)
        {
            var model = node.WorldTransform;
            _silhouetteShader.SetMatrix4("uModel", ref model);
            node.Mesh.DrawRaw();
        }

        foreach (var child in node.Children)
            DrawSilhouetteSubtree(child);
    }

    private void DrawGroundDecal(ContactShadowBuilder.ShadowProjection proj, Matrix4 viewProj)
    {
        float z = proj.FloorZ;
        Span<float> verts = stackalloc float[]
        {
            proj.MinX, proj.MinY, z, 0f, 0f,
            proj.MaxX, proj.MinY, z, 1f, 0f,
            proj.MaxX, proj.MaxY, z, 1f, 1f,
            proj.MinX, proj.MinY, z, 0f, 0f,
            proj.MaxX, proj.MaxY, z, 1f, 1f,
            proj.MinX, proj.MaxY, z, 0f, 1f,
        };

        GL.BindBuffer(BufferTarget.ArrayBuffer, _decalVbo);
        GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, verts.Length * sizeof(float), ref verts[0]);

        GL.Enable(EnableCap.DepthTest);
        GL.DepthFunc(DepthFunction.Lequal);
        GL.DepthMask(false);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.DstColor, BlendingFactor.Zero);
        GL.Enable(EnableCap.PolygonOffsetFill);
        GL.PolygonOffset(-3f, -3f);

        ComputeShadowAppearance(out float blurLod, out float uvScale, out float darken, out float strength);

        _decalShader.Use();
        _decalShader.SetMatrix4("uMVP", ref viewProj);
        _decalShader.SetFloat("uBlurLod", blurLod);
        _decalShader.SetFloat("uUvScale", uvScale);
        _decalShader.SetFloat("uDarken", darken);
        _decalShader.SetFloat("uStrength", strength);
        _decalShader.SetInt("uShadowTex", 0);

        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, _tex);

        GL.BindVertexArray(_decalVao);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 6);
        GL.BindVertexArray(0);

        GL.BindTexture(TextureTarget.Texture2D, 0);
        GL.Disable(EnableCap.PolygonOffsetFill);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.DepthMask(true);
    }

    private void ComputeShadowAppearance(out float blurLod, out float uvScale, out float darken, out float strength)
    {
        float sizeScale     = Math.Clamp(SizeScale, 0.25f, 3f);
        float darknessScale = Math.Clamp(DarknessScale, 0f, 2f);
        float blurScale     = Math.Clamp(BlurScale, 0f, 3f);

        blurLod = BaseBlurLod * blurScale;
        // Size is handled via world-space decal padding only. UV zoom caused hard rectangular clipping.
        uvScale = 1.0f;

        float baseStrength = ArcticPresentation ? BaseArcticStrength : BaseShadowStrength;
        float baseDarken   = ArcticPresentation ? BaseArcticMultiply : BaseDarkMultiply;

        strength = Math.Min(1f, baseStrength * darknessScale);
        if (darknessScale <= 1f)
            darken = 1f + (baseDarken - 1f) * darknessScale;
        else
            darken = Math.Max(0.05f, baseDarken - (darknessScale - 1f) * (1f - baseDarken) * 0.9f);
    }

    /// <summary>
    /// Renders the silhouette into the centre of the shadow texture so blur can bleed
    /// into a black margin instead of hitting ClampToEdge at the texture border.
    /// </summary>
    private static ContactShadowBuilder.ShadowProjection InsetForSilhouette(
        ContactShadowBuilder.ShadowProjection outer, float blurScale, float sizeScale)
    {
        float margin = Math.Clamp(0.10f + blurScale * 0.10f + MathF.Max(sizeScale - 1f, 0f) * 0.04f, 0.10f, 0.32f);
        float cx = (outer.MinX + outer.MaxX) * 0.5f;
        float cy = (outer.MinY + outer.MaxY) * 0.5f;
        float hx = (outer.MaxX - outer.MinX) * 0.5f * (1f - margin);
        float hy = (outer.MaxY - outer.MinY) * 0.5f * (1f - margin);
        return new ContactShadowBuilder.ShadowProjection(
            cx - hx, cy - hy, cx + hx, cy + hy, outer.FloorZ, outer.MaxZ);
    }

    private static Matrix4 BuildFootprintMatrix(ContactShadowBuilder.ShadowProjection proj)
        => Matrix4.CreateOrthographicOffCenter(
            proj.MinX, proj.MaxX,
            proj.MinY, proj.MaxY,
            -1f, 1f);

    private void EnsureFbo()
    {
        if (_fbo != 0)
            return;

        _texSize = MaxTexSize;

        _tex = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, _tex);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.R8,
            _texSize, _texSize, 0, PixelFormat.Red, PixelType.UnsignedByte, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
            (int)TextureMinFilter.LinearMipmapLinear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
            (int)TextureMinFilter.Linear);
        ApplyShadowTextureParameters();

        _fbo = GL.GenFramebuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,
            FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _tex, 0);

        var status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != FramebufferErrorCode.FramebufferComplete)
            throw new InvalidOperationException($"contact shadow FBO incomplete: {status}");
    }

    private void DestroyFbo()
    {
        if (_fbo == 0) return;
        GL.DeleteFramebuffer(_fbo);
        GL.DeleteTexture(_tex);
        _fbo = _tex = 0;
        _texSize = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DestroyFbo();
        _silhouetteShader.Dispose();
        _decalShader.Dispose();
        GL.DeleteVertexArray(_decalVao);
        GL.DeleteBuffer(_decalVbo);
    }
}