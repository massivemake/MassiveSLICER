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
    public event Action?                     GlInitialized;
    public event Action<TimeSpan, int, int>? GlRender;
    public event Action?                     GlDeinitialized;

    private int _outputFbo, _outputColorTex, _outputDepthRbo;
    private int _fboW, _fboH;
    private TimeSpan _lastRenderTime;
    private bool _firstFrame = true;

    protected override void OnOpenGlInit(GlInterface gl)
    {
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
        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        int w = Math.Max(1, (int)(Bounds.Width  * scale));
        int h = Math.Max(1, (int)(Bounds.Height * scale));

        if (w != _fboW || h != _fboH)
            ResizeResources(w, h);

        var now   = TimeSpan.FromMilliseconds(Environment.TickCount64);
        var delta = _firstFrame ? TimeSpan.Zero : now - _lastRenderTime;
        _lastRenderTime = now;
        _firstFrame     = false;

        // Render scene into our depth-backed FBO.
        // SceneRenderer reads the bound draw FBO via GetInteger(DrawFramebufferBinding).
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _outputFbo);
        GlRender?.Invoke(delta, w, h);
        GL.Finish();

        // Blit colour result to Avalonia's target framebuffer.
        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _outputFbo);
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, fb);
        GL.BlitFramebuffer(0, 0, w, h, 0, 0, w, h,
            ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, fb);
    }

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

    public void Dispose() { } // cleanup handled by OnOpenGlDeinit

    private sealed class AvaloniaBindingsContext : IBindingsContext
    {
        private readonly GlInterface _gl;
        public AvaloniaBindingsContext(GlInterface gl) => _gl = gl;
        public IntPtr GetProcAddress(string name) => _gl.GetProcAddress(name);
    }
}
