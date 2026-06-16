using MassiveSlicer.Core.Models;
using MassiveSlicer.Viewport.Camera;
using MassiveSlicer.Viewport.Rendering;
using MassiveSlicer.Viewport.Scene;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport;

/// <summary>
/// Top-level OpenGL renderer. Owns all sub-renderers and the scene camera.
/// Call <see cref="Initialise"/> once after the GL context is created, then
/// <see cref="Render"/> each frame, and <see cref="Dispose"/> when the context is torn down.
/// </summary>
public sealed class SceneRenderer : IDisposable
{
    private GridRenderer?        _grid;
    private AxisRenderer?        _axes;
    private AxisRenderer?        _tcpAxes;
    private AxisRenderer?        _flangeAxes;
    private AxisRenderer?        _sensorAxes;
    private BedBoundaryRenderer? _bedBoundary;
    private GizmoRenderer?       _gizmo;
    private BackdropRenderer?    _backdrop;
    private PlanePreviewRenderer? _planePreview;

    private readonly struct ToolpathEntry
    {
        public ToolpathRenderer             Renderer { get; init; }
        public Toolpath                     Data     { get; init; }
        public System.Numerics.Vector3      Origin   { get; init; }
    }
    private readonly Dictionary<SceneNode, ToolpathEntry> _toolpaths = [];
    private bool _disposed;
    private bool _initialised;

    private Vector3 _toolpathExtrudeColor    = new(0.1f,  0.45f, 0.9f);
    private Vector3 _toolpathTravelColor     = new(0.85f, 0.18f, 0.18f);
    private Vector3 _toolpathSeamColor       = new(1.0f,  0.9f,  0.0f);
    private Vector3 _toolpathUnselectedColor = new(0.38f, 0.38f, 0.38f);

    // -- Off-screen FBOs -------------------------------------------------------
    // Scene is rendered into _sceneFbo so it can be read back as a texture.
    // The selected object is rendered into _maskFbo (white-on-black) sharing the
    // same depth buffer, then Roberts Cross edge detection composites the two
    // into the final output.

    // Layer-preview heatmap texture (1D, RGB8). Uploaded after each slice.
    // _layerColorTexDefault is a 1x1 grey fallback bound before the first slice.
    private int   _layerColorTex;
    private int   _layerColorTexDefault;
    private float _layerColorZMin;
    private float _layerColorZMax;

    // Layer boundary texture (1D, R32F): exact world-Z of each boundary.
    // Used by the fragment shader for screen-space seam line rendering.
    private int _layerBoundaryTex;
    private int _layerBoundaryCount;

    private int _sceneFbo,    _sceneColorTex, _sceneDepthRbo;
    private int _maskFbo,     _maskColorTex;
    private int _fsqVao,      _fsqVbo;          // fullscreen quad
    private Shader? _maskShader;                // flat-white mask renderer
    private Shader? _compositeShader;           // Roberts Cross + scene blend
    private int _fboWidth, _fboHeight;

    // -- Shader sources --------------------------------------------------------

    // Renders the selected mesh as flat white into the mask FBO.
    private static readonly string MaskVertSrc = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        uniform mat4 uMVP;
        void main() { gl_Position = vec4(aPos, 1.0) * uMVP; }
        """;

    private static readonly string MaskFragSrc = """
        #version 330 core
        out vec4 fragColor;
        void main() { fragColor = vec4(1.0); }
        """;

    // Full-screen quad that composites the scene texture and draws a lime-green
    // selection outline. Uses cardinal-direction sampling at 1px and 2px radii
    // (matching Blender's overlay outline detect pass) on a linearly-filtered
    // mask texture so bilinear interpolation provides sub-pixel anti-aliasing.
    // NOTE: All GLSL comment text must be ASCII -- drivers reject non-ASCII bytes.
    private static readonly string CompositeVertSrc = """
        #version 330 core
        layout(location = 0) in vec2 aPos;
        layout(location = 1) in vec2 aUV;
        out vec2 vUV;
        void main() { gl_Position = vec4(aPos, 0.0, 1.0); vUV = aUV; }
        """;

    private static readonly string CompositeFragSrc = """
        #version 330 core
        in vec2 vUV;
        uniform sampler2D uScene;
        uniform sampler2D uMask;
        uniform vec2  uTexelSize;
        uniform float uHasSelection;

        out vec4 fragColor;

        void main() {
            vec4 scene = texture(uScene, vUV);

            if (uHasSelection < 0.5) {
                fragColor = scene;
                return;
            }

            // Cardinal-direction edge detection on the selection mask.
            // The mask uses linear filtering so bilinear interpolation at
            // sub-pixel offsets provides anti-aliasing for free.
            // Sampling at 1px and 2px radii keeps the line ~2px wide at all angles.
            vec2 ts = uTexelSize;
            float c  = texture(uMask, vUV).r;

            float r1 = texture(uMask, vUV + vec2( ts.x,  0.0 )).r;
            float l1 = texture(uMask, vUV + vec2(-ts.x,  0.0 )).r;
            float u1 = texture(uMask, vUV + vec2( 0.0,   ts.y)).r;
            float d1 = texture(uMask, vUV + vec2( 0.0,  -ts.y)).r;

            float r2 = texture(uMask, vUV + vec2( 2.0*ts.x,  0.0 )).r;
            float l2 = texture(uMask, vUV + vec2(-2.0*ts.x,  0.0 )).r;
            float u2 = texture(uMask, vUV + vec2( 0.0,   2.0*ts.y)).r;
            float d2 = texture(uMask, vUV + vec2( 0.0,  -2.0*ts.y)).r;

            float e1 = max(max(abs(c - r1), abs(c - l1)), max(abs(c - u1), abs(c - d1)));
            float e2 = max(max(abs(c - r2), abs(c - l2)), max(abs(c - u2), abs(c - d2)));
            float edge = smoothstep(0.05, 0.95, max(e1, e2));

            vec3 outlineColor = vec3(0.35, 1.0, 0.05); // lime green (was bed grid colour)
            fragColor = vec4(mix(scene.rgb, outlineColor, edge), 1.0);
        }
        """;

    // -- World light -----------------------------------------------------------

    /// <summary>Horizontal rotation of the key light around the Z axis, in degrees.</summary>
    public float LightAzimuth   { get; set; } = 45f;

    /// <summary>Vertical angle of the key light above the XY plane, in degrees (0-90).</summary>
    public float LightElevation { get; set; } = 45f;

    /// <summary>Directional light multiplier applied to diffuse and specular (1 = default).</summary>
    public float LightIntensity { get; set; } = 1f;

    private Vector3 ComputeLightDir()
    {
        float az = MathHelper.DegreesToRadians(LightAzimuth);
        float el = MathHelper.DegreesToRadians(LightElevation);
        float cosEl = MathF.Cos(el);
        return Vector3.Normalize(new Vector3(
            cosEl * MathF.Cos(az),
            cosEl * MathF.Sin(az),
            MathF.Sin(el)));
    }

    // -- Public state ----------------------------------------------------------

    /// <summary>The orbit camera that all renderers share.</summary>
    public OrbitCamera Camera { get; } = new();

    /// <summary>Root of the scene graph. Add child nodes to populate the scene.</summary>
    public SceneNode SceneRoot { get; } = new() { Name = "Root" };

    /// <summary>Currently selected node (direct child of <see cref="SceneRoot"/>), or <c>null</c>.</summary>
    public SceneNode? SelectedNode { get; private set; }

    /// <summary>Returns <c>true</c> if <paramref name="node"/> is a registered toolpath node.</summary>
    public bool IsToolpathNode(SceneNode node) => _toolpaths.ContainsKey(node);

    /// <summary>
    /// The gizmo axis currently being dragged, or <see cref="GizmoAxis.None"/>.
    /// Set by the viewport when a drag begins and cleared when it ends.
    /// </summary>
    public GizmoAxis ActiveDragAxis { get; set; } = GizmoAxis.None;

    /// <summary>Active gizmo mode: translate, scale, or rotate.</summary>
    public GizmoMode GizmoMode { get; set; } = GizmoMode.None;

    /// <summary>When false the gizmo is not rendered and handle hit-testing is skipped.</summary>
    public bool GizmoEnabled { get; set; } = true;

    /// <summary>When false the ground-plane grid is not rendered.</summary>
    public bool ShowGrid { get; set; } = true;

    /// <summary>When false the world-origin X/Y/Z axis lines are not rendered.</summary>
    public bool ShowAxes { get; set; } = false;

    /// <summary>When false the print-bed boundary grid overlay is not rendered.</summary>
    public bool ShowBedGrid { get; set; } = true;

    public bool ShowExtrusionMoves { get; set; } = true;
    public bool ShowTravelMoves    { get; set; } = true;
    public bool ShowSeam           { get; set; } = true;
    public bool ShowBead           { get; set; } = false;
    public bool ShowBeadOverhang        { get; set; } = false;
    public bool ShowOrientationPreview  { get; set; } = false;

    /// <summary>
    /// Scrubber move index applied to the selected toolpath renderer.
    /// int.MaxValue (default) means render the full toolpath.
    /// </summary>
    public int ToolpathActiveScrubIndex { get; set; } = int.MaxValue;

    /// <summary>Active shader/material mode applied to all mesh renderers each frame.</summary>
    public ShaderMode ShaderMode { get; set; } = ShaderMode.Standard;

    /// <summary>Layer height (mm) used when <see cref="ShaderMode"/> is <see cref="ShaderMode.LayerPreview"/>.</summary>
    public float LayerPreviewHeight { get; set; } = 3f;

    /// <summary>Path of the currently loaded backdrop image, or <c>null</c> for none.</summary>
    public string? BackdropPath { get; private set; }

    /// <summary>Mipmap LOD level for backdrop blur. 0 = sharp, 7 = maximum blur.</summary>
    public float BackdropBlur { get; set; } = 2.5f;

    /// <summary>
    /// Updates toolpath line colours for all registered renderers. Must be called on the GL thread.
    /// No-op when the values are unchanged to avoid unnecessary VBO rebuilds.
    /// </summary>
    public void SetToolpathColors(Vector3 extrude, Vector3 travel, Vector3 seam, Vector3 unselected)
    {
        if (_toolpathExtrudeColor    == extrude   &&
            _toolpathTravelColor     == travel    &&
            _toolpathSeamColor       == seam      &&
            _toolpathUnselectedColor == unselected)
            return;

        _toolpathExtrudeColor    = extrude;
        _toolpathTravelColor     = travel;
        _toolpathSeamColor       = seam;
        _toolpathUnselectedColor = unselected;

        foreach (var entry in _toolpaths.Values)
            entry.Renderer.UpdateColors(extrude, travel, seam, unselected);
    }

    /// <summary>
    /// Uploads a toolpath to the GPU and registers it in the scene.
    /// Must be called on the GL thread after <see cref="Initialise"/>.
    /// </summary>
    public void AddToolpath(Toolpath toolpath, SceneNode node,
        float beadWidth = 6f, float layerHeight = 3f,
        System.Numerics.Vector3 materialColor = default)
    {
        var centroid = ComputeToolpathCentroid(toolpath);
        var renderer = new ToolpathRenderer(toolpath, centroid, beadWidth, layerHeight, materialColor);
        renderer.UpdateColors(_toolpathExtrudeColor, _toolpathTravelColor, _toolpathSeamColor, _toolpathUnselectedColor);
        node.LocalTransform = Matrix4.CreateTranslation(centroid.X, centroid.Y, centroid.Z);
        _toolpaths[node]    = new ToolpathEntry { Renderer = renderer, Data = toolpath, Origin = centroid };
        SceneRoot.AddChild(node);
    }

    /// <summary>
    /// Updates the extrude-move colours for a registered toolpath to show reachability.
    /// <paramref name="reachable"/>[i] == false turns move i red. Must be called on the GL thread.
    /// </summary>
    public void UpdateToolpathReachability(SceneNode node, bool[] reachable)
    {
        bool found = _toolpaths.TryGetValue(node, out var entry);
        System.Diagnostics.Debug.WriteLine($"[Renderer] UpdateReachability: nodeFound={found}, unreachable={reachable.Count(x => !x)}/{reachable.Length}");
        if (found)
            entry.Renderer.UpdateReachability(reachable);
    }

    /// <summary>
    /// Builds or rebuilds the singularity-point VBO for a registered toolpath.
    /// Must be called on the GL thread.
    /// </summary>
    public void UpdateToolpathSingularityPoints(SceneNode node, bool[] singularity)
    {
        if (_toolpaths.TryGetValue(node, out var entry))
            entry.Renderer.UpdateSingularityPoints(singularity);
    }

    /// <summary>
    /// Uploads per-move overhang scores to the bead-overhang VAO for the given toolpath node.
    /// Must be called on the GL thread.
    /// </summary>
    public void UpdateToolpathBeadOverhang(SceneNode node, float[] overhangPerFlatMove)
    {
        if (_toolpaths.TryGetValue(node, out var entry))
            entry.Renderer.UpdateBeadOverhang(overhangPerFlatMove);
    }

    public void UpdateToolpathBeadOrientation(SceneNode node, float[] orientationRatePerFlatMove)
    {
        if (_toolpaths.TryGetValue(node, out var entry))
            entry.Renderer.UpdateBeadOrientation(orientationRatePerFlatMove);
    }

    /// <summary>
    /// Disposes the toolpath renderer for <paramref name="node"/> if one is registered.
    /// Call before removing the node from the scene. Safe to call on non-toolpath nodes.
    /// </summary>
    public void RemoveToolpathIfExists(SceneNode node)
    {
        if (_toolpaths.TryGetValue(node, out var entry))
        {
            entry.Renderer.Dispose();
            _toolpaths.Remove(node);
        }
    }

    /// <summary>
    /// When set, draws a set of X/Y/Z axis lines at the TCP position in the overlay pass.
    /// </summary>
    public Matrix4? TcpFrameMatrix { get; set; }

    /// <summary>
    /// When set, draws a set of X/Y/Z axis lines at the flange position in the overlay pass.
    /// </summary>
    public Matrix4? FlangeFrameMatrix { get; set; }

    /// <summary>
    /// When set, draws a second (orange/lime/cyan) axis gizmo for the sensor optical origin
    /// (e.g. camera optical centre, distinct from the TCP focal point).
    /// </summary>
    public Matrix4? SensorOriginFrameMatrix { get; set; }

    // -- Public methods --------------------------------------------------------

    /// <summary>
    /// Configures (or replaces) the print-bed boundary overlay. Safe to call at any time
    /// on the GL thread -- disposes the previous renderer if one exists.
    /// </summary>
    /// <summary>World-space Z coordinate of the build plate surface.</summary>
    public float BedZ { get; private set; }

    public void SetBedBoundary(Vector3 origin, float width, float depth, Vector3 datum, float diameter = 0f)
    {
        BedZ         = origin.Z;
        _bedBoundary?.Dispose();
        _bedBoundary = new BedBoundaryRenderer(origin, width, depth, datum, diameter);
    }

    /// <summary>
    /// Model matrix applied to the print-bed boundary/grid overlay so it can rotate
    /// with the rotary bed (E1). Identity = static. Set by the viewport per E1 change.
    /// </summary>
    public Matrix4 BedBoundaryModel { get; set; } = Matrix4.Identity;

    /// <summary>
    /// Updates (or clears) the angled-slice plane preview quad.
    /// Pass <c>null</c> center to hide it. Must be called on the GL thread.
    /// </summary>
    public void SetPlanePreview(Vector3? center, Vector3? normal, float size = 100f)
    {
        if (center is null || normal is null)
        {
            _planePreview?.Dispose();
            _planePreview = null;
            return;
        }
        _planePreview ??= new PlanePreviewRenderer();
        _planePreview.Update(center.Value, normal.Value, size);
    }

    /// <summary>
    /// Builds and uploads a 1-D heatmap texture from slice layer data so the layer-preview
    /// shader can colour each fragment by the thickness of the layer it falls in.
    /// Blue = thinnest layer, red = thickest. Must be called on the GL thread.
    /// </summary>
    /// <param name="zBoundaries">Sorted layer boundary Z positions (length = numLayers + 1).</param>
    /// <param name="heights">Per-layer thickness in mm (length = numLayers).</param>
    public void SetLayerPreview(float[] zBoundaries, float[] heights)
    {
        if (zBoundaries.Length < 2 || heights.Length == 0) return;

        float zMin = zBoundaries[0];
        float zMax = zBoundaries[^1];
        float range = Math.Max(zMax - zMin, 0.001f);

        float minH = heights.Min();
        float maxH = heights.Max();
        bool  uniform = maxH - minH < 0.001f;

        const int TexWidth = 2048;
        var pixels = new byte[TexWidth * 3];

        for (int i = 0; i < TexWidth; i++)
        {
            float z = zMin + range * (i + 0.5f) / TexWidth;

            // Binary search for layer containing z.
            int layerIdx = Array.BinarySearch(zBoundaries, z);
            if (layerIdx < 0) layerIdx = ~layerIdx - 1;
            layerIdx = Math.Clamp(layerIdx, 0, heights.Length - 1);

            float h = heights[layerIdx];
            float t = uniform ? 0.5f : (h - minH) / (maxH - minH);

            // Pure flat heatmap colour — seams are drawn entirely in the shader via
            // screen-space derivatives so they are always a consistent width on screen.
            var (r, g, b) = HeatmapColor(t);
            pixels[i * 3 + 0] = (byte)(r * 255);
            pixels[i * 3 + 1] = (byte)(g * 255);
            pixels[i * 3 + 2] = (byte)(b * 255);
        }

        if (_layerColorTex != 0) GL.DeleteTexture(_layerColorTex);
        _layerColorTex = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture1D, _layerColorTex);
        GL.TexImage1D(TextureTarget.Texture1D, 0, PixelInternalFormat.Rgb8,
                      TexWidth, 0, PixelFormat.Rgb, PixelType.UnsignedByte, pixels);
        GL.TexParameter(TextureTarget.Texture1D, TextureParameterName.TextureMinFilter,
                        (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture1D, TextureParameterName.TextureMagFilter,
                        (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture1D, TextureParameterName.TextureWrapS,
                        (int)TextureWrapMode.ClampToEdge);
        GL.BindTexture(TextureTarget.Texture1D, 0);

        _layerColorZMin = zMin;
        _layerColorZMax = zMax;

        // Upload exact boundary Z positions (R32F) for the shader binary search.
        if (_layerBoundaryTex != 0) GL.DeleteTexture(_layerBoundaryTex);
        _layerBoundaryTex = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture1D, _layerBoundaryTex);
        GL.TexImage1D(TextureTarget.Texture1D, 0, PixelInternalFormat.R32f,
                      zBoundaries.Length, 0, PixelFormat.Red, PixelType.Float, zBoundaries);
        GL.TexParameter(TextureTarget.Texture1D, TextureParameterName.TextureMinFilter,
                        (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture1D, TextureParameterName.TextureMagFilter,
                        (int)TextureMagFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture1D, TextureParameterName.TextureWrapS,
                        (int)TextureWrapMode.ClampToEdge);
        GL.BindTexture(TextureTarget.Texture1D, 0);
        _layerBoundaryCount = zBoundaries.Length;
    }

    private static (float r, float g, float b) HeatmapColor(float t)
    {
        // Green(thin=0) -> Yellow(0.5) -> Red(thick=1), matching OrcaSlicer convention.
        t = Math.Clamp(t, 0f, 1f);
        if (t < 0.5f) { float s = t * 2f;           return (s,  1f,       0f); }
        {               float s = (t - 0.5f) * 2f;  return (1f, 1f - s,   0f); }
    }

    /// <summary>
    /// Loads (or clears) the backdrop image. Pass <c>null</c> to remove the backdrop.
    /// Must be called on the GL thread. Safe to call at any time after initialisation.
    /// </summary>
    public void SetBackdrop(string? path)
    {
        _backdrop?.Dispose();
        _backdrop    = null;
        BackdropPath = path;
        if (path is null) return;

        try   { _backdrop = new BackdropRenderer(path); }
        catch { BackdropPath = null; }   // unsupported or corrupt image -- silently clear
    }

    /// <summary>
    /// Initialises all GPU resources. Must be called on the GL thread
    /// after the context is current and before the first <see cref="Render"/> call.
    /// </summary>
    public void Initialise()
    {
        if (_initialised) return;
        _initialised = true;

        GL.Enable(EnableCap.DepthTest);
        GL.Enable(EnableCap.CullFace);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        // Smooth filtering across cubemap face boundaries (GL 3.2+).
        GL.Enable(EnableCap.TextureCubeMapSeamless);

        _grid             = new GridRenderer();
        _axes             = new AxisRenderer();
        _tcpAxes          = new AxisRenderer();
        _flangeAxes       = new AxisRenderer();
        // Sensor origin gizmo: orange X / lime Y / sky-blue Z, 150mm to distinguish from TCP.
        _sensorAxes       = new AxisRenderer(0.95f, 0.50f, 0.15f, 0.30f, 0.90f, 0.45f, 0.15f, 0.65f, 0.95f, 150f);
        _gizmo            = new GizmoRenderer();
        _maskShader       = new Shader(MaskVertSrc, MaskFragSrc);
        _compositeShader  = new Shader(CompositeVertSrc, CompositeFragSrc);

        // Default 1x1 grey layer texture used before any slice has been run.
        _layerColorTexDefault = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture1D, _layerColorTexDefault);
        byte[] grey = [128, 128, 128];
        GL.TexImage1D(TextureTarget.Texture1D, 0, PixelInternalFormat.Rgb8, 1, 0,
                      PixelFormat.Rgb, PixelType.UnsignedByte, grey);
        GL.TexParameter(TextureTarget.Texture1D, TextureParameterName.TextureMinFilter,
                        (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture1D, TextureParameterName.TextureMagFilter,
                        (int)TextureMagFilter.Nearest);
        GL.BindTexture(TextureTarget.Texture1D, 0);

        BuildFsq();
    }

    /// <summary>
    /// Renders one frame.
    /// </summary>
    public void Render(int viewportWidth, int viewportHeight)
    {
        if (!_initialised || viewportWidth <= 0 || viewportHeight <= 0)
            return;

        // Capture the currently bound FBO before we touch anything.
        // With OpenGlControlBase this is Avalonia's internal FBO; with Win32GlHost it's 0.
        GL.GetInteger(GetPName.DrawFramebufferBinding, out int outputFbo);

        EnsureFbos(viewportWidth, viewportHeight);

        // Set viewport immediately after any potential FBO resize.
        // AMD is sensitive to mismatched viewport / FBO dimensions.
        GL.Viewport(0, 0, viewportWidth, viewportHeight);

        float aspect = viewportWidth / (float)viewportHeight;
        var view = Camera.GetViewMatrix();
        var proj = Camera.GetProjectionMatrix(aspect);
        var mvp  = view * proj;
        Matrix4.Invert(mvp, out var invVP);

        // -- Scene pass --------------------------------------------------------
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _sceneFbo);
        GL.ClearColor(0.027f, 0.035f, 0.059f, 1f);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        GL.Viewport(0, 0, viewportWidth, viewportHeight);

        // Draw backdrop before any 3-D content, with depth writes and testing off
        // so it never interferes with the scene depth buffer.
        if (_backdrop is not null)
        {
            // Strip camera translation from the view matrix before inverting.
            // The backdrop is at infinity -- only rotation and FOV matter.
            // Using the full translation causes float precision loss when the
            // camera target is far from the world origin, producing jitter.
            var viewRot = view;
            viewRot.Row3 = Vector4.UnitW;
            Matrix4.Invert(viewRot * proj, out var invVPRot);

            GL.Disable(EnableCap.DepthTest);
            GL.Disable(EnableCap.CullFace);
            GL.DepthMask(false);
            _backdrop.Draw(invVPRot, BackdropBlur);
            GL.Enable(EnableCap.DepthTest);
            GL.Enable(EnableCap.CullFace);
            GL.DepthMask(true);
        }

        if (ShowGrid)    _grid?.Draw(mvp);
        if (ShowAxes)    _axes?.Draw(mvp);
        if (ShowBedGrid) _bedBoundary?.Draw(BedBoundaryModel * mvp);

        // Bind backdrop HDR to unit 1 for env reflections in the mesh shader.
        // Unit 0 is left for other samplers; unit 1 stays bound for all mesh draws.
        if (_backdrop is not null)
        {
            GL.ActiveTexture(TextureUnit.Texture1);
            GL.BindTexture(TextureTarget.Texture2D, _backdrop.TextureId);
            GL.ActiveTexture(TextureUnit.Texture0);
        }

        // Bind layer-preview heatmap to unit 2 and boundary texture to unit 3.
        int activeLayerTex = _layerColorTex != 0 ? _layerColorTex : _layerColorTexDefault;
        GL.ActiveTexture(TextureUnit.Texture2);
        GL.BindTexture(TextureTarget.Texture1D, activeLayerTex);
        GL.ActiveTexture(TextureUnit.Texture3);
        GL.BindTexture(TextureTarget.Texture1D, _layerBoundaryTex);  // 0 = nothing bound; safe when uLayerBoundCount=0
        GL.ActiveTexture(TextureUnit.Texture0);

        GL.Enable(EnableCap.PolygonOffsetFill);
        GL.PolygonOffset(1f, 1f);
        foreach (var child in SceneRoot.Children)
        {
            if (child.Overlay) continue; // drawn in overlay pass instead
            ApplyShaderModeToSubtree(child);
            if (!child.CullFaces) GL.Disable(EnableCap.CullFace);
            child.Draw(mvp, Camera.Eye, ComputeLightDir(), LightIntensity);
            if (!child.CullFaces) GL.Enable(EnableCap.CullFace);
        }
        GL.Disable(EnableCap.PolygonOffsetFill);

        // Draw toolpaths after meshes so the polygon-offset pass has written depth first.
        foreach (var (tpNode, entry) in _toolpaths)
        {
            if (!tpNode.Visible) continue;
            var toolpathMvp = tpNode.LocalTransform * mvp;
            bool isSelected = tpNode == SelectedNode;
            entry.Renderer.Draw(toolpathMvp, selected: isSelected,
                showExtrusion: ShowExtrusionMoves, showTravel: ShowTravelMoves,
                showSeam: ShowSeam, showBead: ShowBead, showBeadOverhang: ShowBeadOverhang,
                showOrientationPreview: ShowOrientationPreview,
                scrubIndex: isSelected ? ToolpathActiveScrubIndex : int.MaxValue);
        }

        // Draw the angled-slice plane preview (only present when Angled method is active).
        _planePreview?.Draw(mvp);

        // -- Selection mask pass -----------------------------------------------
        // Render the selected object as flat white into the mask FBO. The mask FBO
        // shares the scene depth buffer so hidden surfaces are correctly excluded.
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _maskFbo);
        GL.ClearColor(0f, 0f, 0f, 0f);
        GL.Clear(ClearBufferMask.ColorBufferBit);
        // Do NOT clear depth -- it holds the scene values and gives us occlusion for free.

        if (SelectedNode is not null && _maskShader is not null)
        {
            GL.DepthMask(false); // read scene depth for occlusion, never overwrite it
            _maskShader.Use();
            foreach (var n in SelectedNode.SelfAndDescendants())
            {
                if (n.Mesh is null) continue;
                var nodeMvp = n.WorldTransform * mvp;
                _maskShader.SetMatrix4("uMVP", ref nodeMvp);
                n.Mesh.DrawRaw();
            }
            GL.DepthMask(true);
        }

        // -- Composite pass ----------------------------------------------------
        // Blit to the output FBO using a fullscreen quad. Roberts Cross on the mask
        // texture detects the selection silhouette edge and blends lime green there.
        GL.Viewport(0, 0, viewportWidth, viewportHeight);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, outputFbo);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        GL.Disable(EnableCap.DepthTest);
        GL.Disable(EnableCap.CullFace);

        if (_compositeShader is not null)
        {
            _compositeShader.Use();

            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, _sceneColorTex);
            _compositeShader.SetInt("uScene", 0);

            GL.ActiveTexture(TextureUnit.Texture1);
            GL.BindTexture(TextureTarget.Texture2D, _maskColorTex);
            _compositeShader.SetInt("uMask", 1);

            _compositeShader.SetVector2("uTexelSize",
                new Vector2(1f / viewportWidth, 1f / viewportHeight));
            _compositeShader.SetFloat("uHasSelection",
                SelectedNode is not null ? 1f : 0f);

            GL.BindVertexArray(_fsqVao);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 6);
            GL.BindVertexArray(0);
        }

        // Unbind all texture units so Avalonia's subsequent compositor pass
        // starts with a clean texture state (AMD is sensitive to stale bindings).
        GL.ActiveTexture(TextureUnit.Texture3);
        GL.BindTexture(TextureTarget.Texture1D, 0);
        GL.ActiveTexture(TextureUnit.Texture2);
        GL.BindTexture(TextureTarget.Texture1D, 0);
        GL.ActiveTexture(TextureUnit.Texture1);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        GL.Enable(EnableCap.DepthTest);
        GL.Enable(EnableCap.CullFace);

        // -- Overlay pass ------------------------------------------------------
        // Nodes flagged Overlay=true and the TCP frame axes are drawn after the
        // composite with depth cleared so they always appear on top.
        bool hasOverlay = TcpFrameMatrix is not null || FlangeFrameMatrix is not null || SensorOriginFrameMatrix is not null;
        foreach (var child in SceneRoot.Children)
            if (child.Overlay) { hasOverlay = true; break; }

        if (hasOverlay)
        {
            GL.Clear(ClearBufferMask.DepthBufferBit);
            GL.Disable(EnableCap.CullFace);
            foreach (var child in SceneRoot.Children)
            {
                if (!child.Overlay) continue;
                child.Draw(mvp, Camera.Eye, ComputeLightDir(), LightIntensity);
            }
            if (TcpFrameMatrix          is { } tcpModel    && _tcpAxes    is not null)
                _tcpAxes.Draw(tcpModel * mvp);
            if (FlangeFrameMatrix       is { } flangeModel && _flangeAxes is not null)
                _flangeAxes.Draw(flangeModel * mvp);
            if (SensorOriginFrameMatrix is { } sensorModel && _sensorAxes is not null)
                _sensorAxes.Draw(sensorModel * mvp);
            GL.Enable(EnableCap.CullFace);
        }

        // -- Gizmo pass --------------------------------------------------------
        // Drawn directly into the output FBO after the composite so it always
        // appears on top of the selection outline.
        // Axis highlight renders whenever a drag/keyboard transform has an active axis
        // (even when gizmo handles are hidden). Handles only render when GizmoEnabled.
        if (SelectedNode is { } sel && _gizmo is not null)
        {
            var nodePos = sel.WorldTransform.Row3.Xyz;
            float dist  = (Camera.Eye - nodePos).Length;
            float scale = MathF.Max(dist * 0.12f, 1f);

            GL.Clear(ClearBufferMask.DepthBufferBit);
            GL.Disable(EnableCap.CullFace);

            if (ActiveDragAxis != GizmoAxis.None)
                _gizmo.DrawAxisHighlight(nodePos, ActiveDragAxis, Camera.FarClip * 0.9f, mvp);

            if (GizmoEnabled && GizmoMode != GizmoMode.None)
            {
                _gizmo.Draw(nodePos, scale, mvp, GizmoMode);
                if (GizmoMode == GizmoMode.Rotate && ActiveDragAxis != GizmoAxis.None)
                    _gizmo.DrawRingHighlight(nodePos, ActiveDragAxis, scale, mvp);
            }

            GL.Enable(EnableCap.CullFace);
        }
    }

    // -- Selection -------------------------------------------------------------

    /// <summary>Selects <paramref name="node"/>, or clears selection if <c>null</c>.</summary>
    public void Select(SceneNode? node) => SelectedNode = node;

    /// <summary>
    /// Casts <paramref name="worldRay"/> against all pickable nodes and returns
    /// the selectable root (direct child of <see cref="SceneRoot"/>), or <c>null</c>.
    /// </summary>
    public SceneNode? Pick(Ray worldRay)
    {
        var hit = Picker.Pick(worldRay, SceneRoot, out _);
        return hit is null ? null : Picker.FindSelectableRoot(hit, SceneRoot);
    }

    /// <summary>
    /// Picks the closest triangle under <paramref name="worldRay"/> and returns the
    /// selectable root node plus the face normal in world space (camera-facing).
    /// Node is <c>null</c> if nothing was hit.
    /// </summary>
    public (SceneNode? node, Vector3 faceNormal) PickFace(Ray worldRay)
    {
        var hit  = Picker.PickFace(worldRay, SceneRoot, out var normal, out _);
        var root = hit is null ? null : Picker.FindSelectableRoot(hit, SceneRoot);
        return (root, root is null ? Vector3.Zero : normal);
    }

    /// <summary>
    /// Tests whether screen position (<paramref name="mx"/>, <paramref name="my"/>) is
    /// within 8 px of any extrude segment in the toolpath. Returns the toolpath node, or
    /// <c>null</c> if no hit (or toolpath is hidden / absent).
    /// </summary>
    public SceneNode? PickToolpath(float mx, float my, float vpW, float vpH)
    {
        if (vpW <= 0 || vpH <= 0 || _toolpaths.Count == 0) return null;

        float aspect = vpW / vpH;
        var   click  = new Vector2(mx, my);
        var   viewProj = Camera.GetViewMatrix() * Camera.GetProjectionMatrix(aspect);

        foreach (var (node, entry) in _toolpaths)
        {
            if (!node.Visible) continue;
            var mvp    = node.LocalTransform * viewProj;
            var origin = entry.Origin;

            foreach (var layer in entry.Data.Layers)
                foreach (var move in layer.Moves)
                {
                    if (move.Kind != MoveKind.Extrude) continue;
                    var a = WorldToScreen(new Vector3(move.From.X - origin.X, move.From.Y - origin.Y, move.From.Z - origin.Z), mvp, vpW, vpH);
                    var b = WorldToScreen(new Vector3(move.To.X   - origin.X, move.To.Y   - origin.Y, move.To.Z   - origin.Z), mvp, vpW, vpH);
                    if (float.IsNaN(a.X) || float.IsNaN(b.X)) continue;
                    if (SegmentPointDist2D(click, a, b) < 8f) return node;
                }
        }
        return null;
    }

    private static Vector2 WorldToScreen(Vector3 world, Matrix4 mvp, float vpW, float vpH)
    {
        var clip = new Vector4(world, 1f) * mvp;
        if (clip.W <= 0f) return new Vector2(float.NaN);
        float invW = 1f / clip.W;
        return new Vector2(
            (clip.X * invW * 0.5f + 0.5f) * vpW,
            (1f - (clip.Y * invW * 0.5f + 0.5f)) * vpH);
    }

    private static System.Numerics.Vector3 ComputeToolpathCentroid(Toolpath toolpath)
    {
        var sum = System.Numerics.Vector3.Zero;
        int count = 0;
        foreach (var layer in toolpath.Layers)
            foreach (var move in layer.Moves)
                if (move.Kind == MoveKind.Extrude)
                {
                    sum += move.From + move.To;
                    count += 2;
                }
        return count > 0 ? sum / count : System.Numerics.Vector3.Zero;
    }

    private static float SegmentPointDist2D(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float len2 = ab.LengthSquared;
        if (len2 < 0.01f) return (p - a).Length;
        float t = MathF.Max(0f, MathF.Min(1f, Vector2.Dot(p - a, ab) / len2));
        return (p - (a + t * ab)).Length;
    }

    /// <summary>
    /// Returns which gizmo axis is under the given screen position,
    /// or <see cref="GizmoAxis.None"/> if no axis is hit or nothing is selected.
    /// </summary>
    public GizmoAxis HitTestGizmo(float mx, float my, float vpW, float vpH)
    {
        if (SelectedNode is null || _gizmo is null || vpW <= 0 || vpH <= 0 || GizmoMode == GizmoMode.None)
            return GizmoAxis.None;

        var nodePos = SelectedNode.WorldTransform.Row3.Xyz;
        float dist  = (Camera.Eye - nodePos).Length;
        float scale = MathF.Max(dist * 0.12f, 1f);

        float aspect = vpW / vpH;
        var viewProj = Camera.GetViewMatrix() * Camera.GetProjectionMatrix(aspect);

        return GizmoMode == GizmoMode.Rotate
            ? GizmoRenderer.HitTestRings(nodePos, scale, mx, my, vpW, vpH, viewProj)
            : GizmoRenderer.HitTestAxes (nodePos, scale, mx, my, vpW, vpH, viewProj, GizmoMode);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _grid?.Dispose();
        _axes?.Dispose();
        _tcpAxes?.Dispose();
        _flangeAxes?.Dispose();
        _sensorAxes?.Dispose();
        _bedBoundary?.Dispose();
        _gizmo?.Dispose();
        _backdrop?.Dispose();
        _planePreview?.Dispose();
        foreach (var entry in _toolpaths.Values) entry.Renderer.Dispose();
        _toolpaths.Clear();
        _maskShader?.Dispose();
        _compositeShader?.Dispose();
        if (_layerColorTex != 0)        GL.DeleteTexture(_layerColorTex);
        if (_layerColorTexDefault != 0) GL.DeleteTexture(_layerColorTexDefault);
        if (_layerBoundaryTex != 0)     GL.DeleteTexture(_layerBoundaryTex);
        DestroyFbos();
        if (_fsqVao != 0) { GL.DeleteVertexArray(_fsqVao); GL.DeleteBuffer(_fsqVbo); }
    }

    // -- Shader mode -----------------------------------------------------------

    private static readonly OpenTK.Mathematics.Vector4 ClayColor      = new(1.00f, 0.55f, 0.30f, 1f);
    private static readonly OpenTK.Mathematics.Vector4 MetalColor     = new(0.60f, 0.60f, 0.65f, 1f);
    private static readonly OpenTK.Mathematics.Vector4 ChromeColor    = new(1.00f, 1.00f, 1.00f, 1f);
    private static readonly OpenTK.Mathematics.Vector4 MatteBlackColor = new(0.07f, 0.07f, 0.07f, 1f);
    private static readonly OpenTK.Mathematics.Vector4 PurpleColor    = new(0.53f, 0.25f, 0.80f, 1f);

    private void ApplyShaderModeToSubtree(SceneNode root)
    {
        bool hasEnv          = _backdrop is not null;
        bool forceLayerPreview = root.LayerPreview;
        foreach (var n in root.SelfAndDescendants())
        {
            if (n.Mesh is not { } mesh) continue;
            mesh.HasEnvMap        = hasEnv;
            mesh.LayerPreviewMode = false;

            if (forceLayerPreview)
            {
                mesh.NormalsMode      = false;
                mesh.LayerPreviewMode = true;
                mesh.LayerHeight      = LayerPreviewHeight;
                mesh.LayerZOffset     = BedZ;
                mesh.LayerZMin           = _layerColorZMin;
                mesh.LayerZMax           = _layerColorZMax;
                mesh.LayerBoundaryCount  = _layerBoundaryCount;
                mesh.Metallic            = 0f;
                continue;
            }

            switch (ShaderMode)
            {
                case ShaderMode.Standard:
                {
                    var pd = mesh.PickingData;
                    float smoothness = 1f - pd.Roughness;
                    mesh.NormalsMode      = false;
                    mesh.Color            = pd.BaseColor;
                    mesh.Metallic         = pd.Metallic;
                    mesh.Shininess        = MathF.Pow(2f, smoothness * 5f);
                    mesh.SpecularStrength = smoothness * (0.25f + pd.Metallic * 0.5f);
                    break;
                }
                case ShaderMode.Clay:
                    mesh.NormalsMode      = false;
                    mesh.Metallic         = 0f;
                    mesh.Color            = ClayColor;
                    mesh.SpecularStrength = 0f;
                    mesh.Shininess        = 1f;
                    break;
                case ShaderMode.Metal:
                    mesh.NormalsMode      = false;
                    mesh.Metallic         = 0.85f;
                    mesh.Color            = MetalColor;
                    mesh.SpecularStrength = 0.35f;
                    mesh.Shininess        = 18f;
                    break;
                case ShaderMode.Chrome:
                    mesh.NormalsMode      = false;
                    mesh.Metallic         = 1f;
                    mesh.Color            = ChromeColor;
                    mesh.SpecularStrength = 1.20f;
                    mesh.Shininess        = 256f;
                    break;
                case ShaderMode.MatteBlack:
                    mesh.NormalsMode      = false;
                    mesh.Metallic         = 0f;
                    mesh.Color            = MatteBlackColor;
                    mesh.SpecularStrength = 0f;
                    mesh.Shininess        = 1f;
                    break;
                case ShaderMode.Purple:
                    mesh.NormalsMode      = false;
                    mesh.Metallic         = 0.1f;
                    mesh.Color            = PurpleColor;
                    mesh.SpecularStrength = 0.15f;
                    mesh.Shininess        = 16f;
                    break;
                case ShaderMode.Normals:
                    mesh.NormalsMode = true;
                    mesh.Metallic    = 0f;
                    break;
            }
        }
    }

    // -- FBO management --------------------------------------------------------

    private void EnsureFbos(int w, int h)
    {
        if (w == _fboWidth && h == _fboHeight) return;

        if (_fboWidth == 0)
        {
            // -- First-time creation --------------------------------------------

            // Scene FBO: colour texture + depth renderbuffer
            _sceneFbo = GL.GenFramebuffer();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _sceneFbo);

            _sceneColorTex = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, _sceneColorTex);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8,
                          w, h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
                            (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
                            (int)TextureMagFilter.Nearest);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,
                                    FramebufferAttachment.ColorAttachment0,
                                    TextureTarget.Texture2D, _sceneColorTex, 0);

            _sceneDepthRbo = GL.GenRenderbuffer();
            GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _sceneDepthRbo);
            GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer,
                                   RenderbufferStorage.Depth24Stencil8, w, h);
            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer,
                                       FramebufferAttachment.DepthStencilAttachment,
                                       RenderbufferTarget.Renderbuffer, _sceneDepthRbo);
            ValidateFbo("sceneFbo(create)");

            // Mask FBO: colour texture + shared scene depth.
            // Sharing the depth buffer means the mask pass respects scene occlusion
            // without a blit -- selected surfaces behind other objects stay masked out.
            _maskFbo = GL.GenFramebuffer();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _maskFbo);

            _maskColorTex = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, _maskColorTex);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8,
                          w, h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
            // Linear filtering for sub-pixel anti-aliasing in the composite edge pass.
            // ClampToEdge stops the 2px cardinal samples wrapping at the viewport border.
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
                            (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
                            (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS,
                            (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT,
                            (int)TextureWrapMode.ClampToEdge);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,
                                    FramebufferAttachment.ColorAttachment0,
                                    TextureTarget.Texture2D, _maskColorTex, 0);
            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer,
                                       FramebufferAttachment.DepthStencilAttachment,
                                       RenderbufferTarget.Renderbuffer, _sceneDepthRbo);
            ValidateFbo("maskFbo(create)");
        }
        else
        {
            // -- Resize: detach -> delete -> create new -> re-attach -------------
            // AMD's driver (atio6axx.dll) faults when TexImage2D or
            // RenderbufferStorage is called on an existing object to make it
            // LARGER, regardless of whether the object is currently attached to
            // an FBO. The only safe path is to delete the old objects and create
            // new ones. The FBO objects themselves are kept alive to avoid the
            // rapid GenFramebuffer/DeleteFramebuffer churn that caused a separate
            // crash in an earlier fix attempt.
            GL.Finish();

            // Detach from both FBOs before deleting their attachments
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _sceneFbo);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,
                                    FramebufferAttachment.ColorAttachment0,
                                    TextureTarget.Texture2D, 0, 0);
            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer,
                                       FramebufferAttachment.DepthStencilAttachment,
                                       RenderbufferTarget.Renderbuffer, 0);

            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _maskFbo);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,
                                    FramebufferAttachment.ColorAttachment0,
                                    TextureTarget.Texture2D, 0, 0);
            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer,
                                       FramebufferAttachment.DepthStencilAttachment,
                                       RenderbufferTarget.Renderbuffer, 0);

            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            GL.Finish();

            // Delete old attachments
            GL.DeleteTexture(_sceneColorTex);
            GL.DeleteRenderbuffer(_sceneDepthRbo);
            GL.DeleteTexture(_maskColorTex);
            GL.Finish();

            // Create new attachments
            _sceneColorTex = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, _sceneColorTex);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8,
                          w, h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
                            (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
                            (int)TextureMagFilter.Nearest);

            _sceneDepthRbo = GL.GenRenderbuffer();
            GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _sceneDepthRbo);
            GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer,
                                   RenderbufferStorage.Depth24Stencil8, w, h);

            _maskColorTex = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, _maskColorTex);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8,
                          w, h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
                            (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
                            (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS,
                            (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT,
                            (int)TextureWrapMode.ClampToEdge);

            // Re-attach new objects to the existing FBOs
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _sceneFbo);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,
                                    FramebufferAttachment.ColorAttachment0,
                                    TextureTarget.Texture2D, _sceneColorTex, 0);
            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer,
                                       FramebufferAttachment.DepthStencilAttachment,
                                       RenderbufferTarget.Renderbuffer, _sceneDepthRbo);
            ValidateFbo("sceneFbo(resize)");

            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _maskFbo);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,
                                    FramebufferAttachment.ColorAttachment0,
                                    TextureTarget.Texture2D, _maskColorTex, 0);
            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer,
                                       FramebufferAttachment.DepthStencilAttachment,
                                       RenderbufferTarget.Renderbuffer, _sceneDepthRbo);
            ValidateFbo("maskFbo(resize)");
        }

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, 0);

        _fboWidth  = w;
        _fboHeight = h;
    }

    private static void ValidateFbo(string name)
    {
        var status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != FramebufferErrorCode.FramebufferComplete)
            throw new InvalidOperationException($"FBO '{name}' incomplete: {status}");
    }

    private void DestroyFbos()
    {
        if (_fboWidth == 0) return;
        GL.Finish();
        GL.DeleteFramebuffer(_sceneFbo);
        GL.DeleteFramebuffer(_maskFbo);
        GL.DeleteTexture(_sceneColorTex);
        GL.DeleteTexture(_maskColorTex);
        GL.DeleteRenderbuffer(_sceneDepthRbo);
        _fboWidth = _fboHeight = 0;
    }

    // -- Fullscreen quad -------------------------------------------------------

    private void BuildFsq()
    {
        float[] verts =
        [
            // aPos (NDC)   aUV
            -1f, -1f,      0f, 0f,
             1f, -1f,      1f, 0f,
             1f,  1f,      1f, 1f,
            -1f, -1f,      0f, 0f,
             1f,  1f,      1f, 1f,
            -1f,  1f,      0f, 1f,
        ];
        _fsqVao = GL.GenVertexArray();
        _fsqVbo = GL.GenBuffer();
        GL.BindVertexArray(_fsqVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _fsqVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, verts.Length * sizeof(float), verts,
                      BufferUsageHint.StaticDraw);
        int stride = 4 * sizeof(float);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride,
                               2 * sizeof(float));
        GL.EnableVertexAttribArray(1);
        GL.BindVertexArray(0);
    }
}
