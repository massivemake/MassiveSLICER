// -- Non-Windows GL host (macOS / Linux) --------------------------------------
//
// This file is compiled on macOS and Linux only (see MassiveSlicer.App.csproj).
// The Windows implementation is in GlHostControl.Windows.cs -- see that file for
// a full explanation of why Windows requires a different approach (AMD GPU crash
// inside atio6axx.dll caused by Avalonia's FBO teardown sequence).
//
// On macOS / Linux the AMD constraint does not apply: we can safely use Avalonia's
// cross-platform OpenGlControlBase. The approach here is:
//   1. Avalonia manages the GL context via CGL (macOS) or EGL/GLX (Linux).
//   2. We create our own depth-backed FBO so SceneRenderer has a stencil-capable
//      target, identical to the Windows approach.
//   3. After rendering, we blit our FBO's colour plane to the Avalonia-provided
//      framebuffer (fb parameter of OnOpenGlRender). Avalonia composites that into
//      the window.
//
// RequestNextFrameRendering() is inherited from OpenGlControlBase -- all callers
// in ViewportView.axaml.cs can use it without changes.

using Avalonia;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using OpenTK;
using OpenTK.Graphics.OpenGL4;
using GlPixelFormat = OpenTK.Graphics.OpenGL4.PixelFormat;

namespace MassiveSlicer.App.Views;

internal sealed class GlHostControl : OpenGlControlBase, IDisposable
{
    // -- Public GL lifecycle events --------------------------------------------

    public event Action?                     GlInitialized;
    public event Action<TimeSpan, int, int>? GlRender;
    public event Action?                     GlDeinitialized;

    /// <inheritdoc cref="GlHostControl.InteractionRenderScale"/>
    public float InteractionRenderScale { get; set; } = 1f;

    /// <inheritdoc cref="GlHostControl.CaptureScreenshotPngAsync"/>
    public Task<byte[]?> CaptureScreenshotPngAsync(int timeoutMs = 5000) => Task.FromResult<byte[]?>(null);

    // -- Output FBO (what SceneRenderer composites into) -----------------------

    private int _outputFbo, _outputColorTex, _outputDepthRbo;
    private int _fboW, _fboH;

    // -- Timing ----------------------------------------------------------------

    private TimeSpan _lastRenderTime;
    private bool _firstFrame = true;

    // -- OpenGlControlBase lifecycle -------------------------------------------

    protected override void OnOpenGlInit(GlInterface gl)
    {
        // Load OpenTK bindings through Avalonia's GL interface so we can use
        // the same GL.* calls as the rest of SceneRenderer / MeshRenderer.
        GL.LoadBindings(new AvaloniaBindingsContext(gl));
        GlInitialized?.Invoke();
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        GlDeinitialized?.Invoke();
        DestroyResources();
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        double dpi = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        int displayW = Math.Max(1, (int)(Bounds.Width  * dpi));
        int displayH = Math.Max(1, (int)(Bounds.Height * dpi));
        float interaction = Math.Clamp(InteractionRenderScale, 0.25f, 1f);
        int w = Math.Max(1, (int)(displayW * interaction));
        int h = Math.Max(1, (int)(displayH * interaction));

        if (w != _fboW || h != _fboH)
            ResizeResources(w, h);

        var now   = TimeSpan.FromMilliseconds(Environment.TickCount64);
        var delta = _firstFrame ? TimeSpan.Zero : now - _lastRenderTime;
        _lastRenderTime = now;
        _firstFrame     = false;

        // Render scene into our depth-backed FBO.
        // SceneRenderer queries the bound draw FBO via GetInteger(DrawFramebufferBinding)
        // and composites into it, so we bind it before firing the render event.
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _outputFbo);
        GlRender?.Invoke(delta, w, h);
        GL.Finish();

        // Blit colour result to the framebuffer Avalonia provided for this frame.
        // Avalonia composites that into the window on its own render pass.
        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _outputFbo);
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, fb);
        GL.BlitFramebuffer(0, 0, w, h, 0, 0, w, h,
            ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, fb);
    }

    // -- FBO lifecycle ---------------------------------------------------------

    private void ResizeResources(int w, int h)
    {
        DestroyResources();

        _outputFbo = GL.GenFramebuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _outputFbo);

        _outputColorTex = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, _outputColorTex);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8,
                      w, h, 0, GlPixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
                        (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
                        (int)TextureMagFilter.Nearest);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,
                                FramebufferAttachment.ColorAttachment0,
                                TextureTarget.Texture2D, _outputColorTex, 0);

        _outputDepthRbo = GL.GenRenderbuffer();
        GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _outputDepthRbo);
        GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer,
                               RenderbufferStorage.Depth24Stencil8, w, h);
        GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer,
                                   FramebufferAttachment.DepthStencilAttachment,
                                   RenderbufferTarget.Renderbuffer, _outputDepthRbo);

        var status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != FramebufferErrorCode.FramebufferComplete)
            throw new InvalidOperationException($"Output FBO incomplete: {status}");

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, 0);

        _fboW = w;
        _fboH = h;
    }

    private void DestroyResources()
    {
        if (_outputFbo == 0) return;

        GL.Finish();

        // Detach before deletion -- mirrors the AMD-safe sequence in the Windows
        // implementation (not strictly required here, but good practice everywhere).
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _outputFbo);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,
                                FramebufferAttachment.ColorAttachment0,
                                TextureTarget.Texture2D, 0, 0);
        GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer,
                                   FramebufferAttachment.DepthStencilAttachment,
                                   RenderbufferTarget.Renderbuffer, 0);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        GL.Finish();

        GL.DeleteFramebuffer(_outputFbo);       _outputFbo      = 0;
        GL.DeleteTexture(_outputColorTex);      _outputColorTex = 0;
        GL.DeleteRenderbuffer(_outputDepthRbo); _outputDepthRbo = 0;

        _fboW = _fboH = 0;
    }

    /// <inheritdoc/>
    public void Dispose() { } // GPU resources released in OnOpenGlDeinit

    // -- OpenTK bindings context -----------------------------------------------

    /// <summary>
    /// Bridges Avalonia's <see cref="GlInterface"/> to OpenTK's
    /// <see cref="IBindingsContext"/> so <c>GL.LoadBindings</c> can resolve
    /// function pointers via the platform GL context rather than opengl32.dll.
    /// </summary>
    private sealed class AvaloniaBindingsContext : IBindingsContext
    {
        private readonly GlInterface _gl;
        public AvaloniaBindingsContext(GlInterface gl) => _gl = gl;
        public IntPtr GetProcAddress(string name) => _gl.GetProcAddress(name);
    }
}
