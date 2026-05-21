using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using OpenTK;
using OpenTK.Graphics.OpenGL4;

namespace MassiveSlicer.App.Views;

/// <summary>
/// Thin OpenGlControlBase wrapper that bridges Avalonia's GL lifecycle to the
/// SceneRenderer. Loads OpenTK function pointers from Avalonia's GlInterface so
/// the Viewport project can continue using OpenTK GL calls unchanged.
/// </summary>
internal sealed class OpenGlCanvas : OpenGlControlBase
{
    private bool _initialized;

    /// <summary>Fired on the GL thread once the context is ready and OpenTK bindings are loaded.</summary>
    public event Action? GlInitialized;

    /// <summary>Fired on the GL thread each frame. Args: (elapsed since last frame, physical width px, physical height px).</summary>
    public event Action<TimeSpan, int, int>? GlRender;

    /// <summary>Fired on the GL thread when the context is being torn down.</summary>
    public event Action? GlDeinitialized;

    protected override void OnOpenGlInit(GlInterface gl)
    {
        // Load OpenTK function pointers from Avalonia's context so the
        // Viewport rendering code can make raw GL4 calls as before.
        GL.LoadBindings(new AvaloniaBindingsContext(gl));
        _initialized = true;
        GlInitialized?.Invoke();
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _initialized = false;
        GlDeinitialized?.Invoke();
    }

    private TimeSpan _lastRenderTime = TimeSpan.Zero;
    private bool _firstFrame = true;

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (!_initialized)
        {
            RequestNextFrameRendering();
            return;
        }

        // Compute elapsed time since last frame.
        var now = TimeSpan.FromMilliseconds(Environment.TickCount64);
        var delta = _firstFrame ? TimeSpan.Zero : (now - _lastRenderTime);
        _lastRenderTime = now;
        _firstFrame = false;

        // Bind Avalonia's target FBO. SceneRenderer reads the currently-bound
        // FBO via GL.GetInteger(FramebufferBinding) and composites into it.
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, fb);

        // Physical framebuffer dimensions (DPI-scaled).
        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        int w = Math.Max(1, (int)(Bounds.Width  * scale));
        int h = Math.Max(1, (int)(Bounds.Height * scale));

        GlRender?.Invoke(delta, w, h);

        // Continuous rendering — queue the next frame immediately.
        RequestNextFrameRendering();
    }

    // ── OpenTK bindings adapter ───────────────────────────────────────────────

    private sealed class AvaloniaBindingsContext : IBindingsContext
    {
        private readonly GlInterface _gl;
        public AvaloniaBindingsContext(GlInterface gl) => _gl = gl;
        public IntPtr GetProcAddress(string procName) => _gl.GetProcAddress(procName);
    }
}
