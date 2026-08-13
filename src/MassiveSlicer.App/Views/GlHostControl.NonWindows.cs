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

using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
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

    /// <summary>Optional diagnostic sink (wired to the in-app console in MainWindow) for
    /// one-shot GL lifecycle reporting on macOS/Linux. Invoked from the GL render thread.</summary>
    public static Action<string>? Diag;

    /// <summary>Always writes to stderr (reliable, race-free); also mirrors to the in-app
    /// console when the <see cref="Diag"/> sink is wired.</summary>
    private static void DiagLog(string msg)
    {
        System.Console.Error.WriteLine(msg);
        Diag?.Invoke(msg);
    }

    /// <inheritdoc cref="GlHostControl.InteractionRenderScale"/>
    public float InteractionRenderScale { get; set; } = 1f;

    // -- Screenshot (on-demand FBO readback) -----------------------------------

    private int _screenshotPending;
    private TaskCompletionSource<byte[]?>? _screenshotTcs;

    /// <summary>
    /// Captures the rendered 3D viewport as PNG by reading back the output FBO on the next
    /// frame. Needed on macOS/Linux because Avalonia's OpenGlControlBase surface is not
    /// picked up by window-level RenderTargetBitmap capture (it composites separately), so
    /// a plain window screenshot shows the viewport as black.
    /// </summary>
    public Task<byte[]?> CaptureScreenshotPngAsync(int timeoutMs = 5000)
    {
        var tcs = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _screenshotTcs = tcs;
        Interlocked.Exchange(ref _screenshotPending, 1);
        RequestNextFrameRendering();
        _ = Task.Delay(timeoutMs).ContinueWith(_ => tcs.TrySetResult(null), TaskScheduler.Default);
        return tcs.Task;
    }

    private static byte[]? EncodeRgbaPng(byte[] rgbaBottomUp, int w, int h)
    {
        try
        {
            // OpenGL's origin is bottom-left; image formats are top-left — flip rows.
            int stride = w * 4;
            var topDown = new byte[rgbaBottomUp.Length];
            for (int row = 0; row < h; row++)
                System.Buffer.BlockCopy(rgbaBottomUp, (h - 1 - row) * stride, topDown, row * stride, stride);

            using var wb = new WriteableBitmap(
                new PixelSize(w, h), new Vector(96, 96), PixelFormats.Rgba8888, AlphaFormat.Opaque);
            using (var fb = wb.Lock())
                Marshal.Copy(topDown, 0, fb.Address, topDown.Length);

            using var ms = new System.IO.MemoryStream();
            wb.Save(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }

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
        try
        {
            DiagLog($"[gl] init  version={GL.GetString(StringName.Version)}  " +
                    $"renderer={GL.GetString(StringName.Renderer)}  " +
                    $"glsl={GL.GetString(StringName.ShadingLanguageVersion)}");
        }
        catch (Exception ex) { DiagLog($"[gl] init string query failed: {ex.Message}"); }
        GlInitialized?.Invoke();
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        GlDeinitialized?.Invoke();
        DestroyResources();
    }

    private bool _renderFailed;

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        // If scene shaders cannot compile (e.g. GLSL 3.30 on GLES-only SoCs),
        // keep the app alive and paint a flat clear colour instead of crashing.
        if (_renderFailed)
        {
            try
            {
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, fb);
                GL.Viewport(0, 0, Math.Max(1, (int)Bounds.Width), Math.Max(1, (int)Bounds.Height));
                GL.ClearColor(0.12f, 0.14f, 0.18f, 1f);
                GL.Clear(ClearBufferMask.ColorBufferBit);
            }
            catch { /* ignore */ }
            return;
        }

        try
        {
            OnOpenGlRenderCore(gl, fb);
        }
        catch (Exception ex)
        {
            _renderFailed = true;
            DiagLog($"[gl] render failed (viewport disabled): {ex.Message}");
            try
            {
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, fb);
                GL.ClearColor(0.12f, 0.14f, 0.18f, 1f);
                GL.Clear(ClearBufferMask.ColorBufferBit);
            }
            catch { /* ignore */ }
        }
    }

    private void OnOpenGlRenderCore(GlInterface gl, int fb)
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
        // The scene is rendered at w×h (possibly reduced by InteractionRenderScale during
        // orbit/pan), so we must UPSCALE the source to the full display size of Avalonia's
        // framebuffer — blitting 1:1 would fill only a small corner and leave the rest of
        // the viewport showing stale content (a small, torn window).
        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _outputFbo);
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, fb);
        var blitFilter = (w == displayW && h == displayH)
            ? BlitFramebufferFilter.Nearest
            : BlitFramebufferFilter.Linear;
        GL.BlitFramebuffer(0, 0, w, h, 0, 0, displayW, displayH,
            ClearBufferMask.ColorBufferBit, blitFilter);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, fb);

        // Fulfil a pending screenshot request by reading back the rendered output FBO.
        if (Interlocked.Exchange(ref _screenshotPending, 0) == 1 && _screenshotTcs is { } shot)
        {
            _screenshotTcs = null;
            try
            {
                var raw = new byte[w * h * 4];
                GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _outputFbo);
                GL.ReadPixels(0, 0, w, h, GlPixelFormat.Rgba, PixelType.UnsignedByte, raw);
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, fb);
                int sw = w, sh = h;
                Dispatcher.UIThread.Post(() => shot.TrySetResult(EncodeRgbaPng(raw, sw, sh)));
            }
            catch { shot.TrySetResult(null); }
        }
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
