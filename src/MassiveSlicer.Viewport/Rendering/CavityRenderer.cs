using MassiveSlicer.Viewport.Scene;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Rendering;

/// <summary>
/// Renders a world-normal prepass and supplies cavity settings for the composite shader.
/// Blender Workbench-style ridge/valley accentuation.
/// </summary>
public sealed class CavityRenderer : IDisposable
{
    private readonly Shader _normalShader;
    private bool _disposed;

    private static readonly string NormalVertSrc = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNormal;
        uniform mat4 uMVP;
        uniform mat4 uModel;
        uniform mat3 uNormalMat;
        out vec3 vNormal;
        void main() {
            gl_Position = vec4(aPos, 1.0) * uMVP;
            vNormal = normalize(aNormal * uNormalMat);
        }
        """;

    private static readonly string NormalFragSrc = """
        #version 330 core
        in vec3 vNormal;
        uniform float uMaskAlpha;      // 1 = cavity applies here, 0 = excluded category
        out vec4 fragColor;
        void main() {
            vec3 n = vNormal;
            if (!gl_FrontFacing) n = -n;
            fragColor = vec4(n * 0.5 + 0.5, uMaskAlpha);
        }
        """;

    public bool Enabled { get; set; }

    public CavityMode Mode { get; set; } = CavityMode.Both;

    public float ScreenRidge  { get; set; } = 1f;
    public float ScreenValley { get; set; } = 1f;
    public float WorldRidge   { get; set; } = 1f;
    public float WorldValley  { get; set; } = 1f;

    /// <summary>World-space sampling radius in mm (Blender Distance).</summary>
    public float WorldDistance { get; set; } = 5f;

    /// <summary>When false, user-imported meshes are excluded from cavity shading
    /// (cell geometry — bed, robot — always shades).</summary>
    public bool ShadeUserMeshes { get; set; } = true;

    public bool NeedsNormalPrepass => Enabled;

    public CavityRenderer()
    {
        _normalShader = new Shader(NormalVertSrc, NormalFragSrc);
    }

    /// <summary>
    /// Writes encoded world normals into <paramref name="normalTex"/> using the shared depth buffer.
    /// </summary>
    public void RenderNormalPrepass(SceneNode sceneRoot, Matrix4 mvp, int normalFbo)
    {
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, normalFbo);
        GL.ClearColor(0f, 0f, 0f, 0f);
        GL.Clear(ClearBufferMask.ColorBufferBit);

        GL.Enable(EnableCap.DepthTest);
        GL.DepthFunc(DepthFunction.Less);
        GL.DepthMask(false);
        GL.ColorMask(true, true, true, true);

        _normalShader.Use();

        foreach (var child in sceneRoot.Children)
        {
            if (child.Overlay) continue;
            DrawNormalSubtree(child, mvp, userContent: false);
        }

        GL.DepthMask(true);
    }

    private void DrawNormalSubtree(SceneNode node, Matrix4 parentMvp, bool userContent)
    {
        if (!node.Visible || node.TranslucentPass) return;
        userContent |= node.Selectable;

        if (node.Mesh is not null)
        {
            var model = node.WorldTransform;
            var nodeMvp = model * parentMvp;
            var normalMat = new Matrix3(model);

            _normalShader.SetMatrix4("uMVP", ref nodeMvp);
            _normalShader.SetMatrix4("uModel", ref model);
            _normalShader.SetMatrix3("uNormalMat", ref normalMat);
            _normalShader.SetFloat("uMaskAlpha", userContent && !ShadeUserMeshes ? 0f : 1f);
            node.Mesh.DrawRaw();
        }

        foreach (var child in node.Children)
            DrawNormalSubtree(child, parentMvp, userContent);
    }

    public void BindCompositeUniforms(Shader composite, int viewportWidth, int viewportHeight)
    {
        composite.SetFloat("uCavityEnabled", Enabled ? 1f : 0f);
        composite.SetInt("uCavityType", (int)Mode);
        composite.SetFloat("uScreenRidge",  ScreenRidge);
        composite.SetFloat("uScreenValley", ScreenValley);
        composite.SetFloat("uWorldRidge",   WorldRidge);
        composite.SetFloat("uWorldValley",  WorldValley);
        composite.SetFloat("uWorldDistance", WorldDistance);
        composite.SetVector2("uViewportSize",
            new Vector2(viewportWidth, viewportHeight));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _normalShader.Dispose();
    }
}