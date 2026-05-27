using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using OpenTK;
using OpenTK.Graphics.OpenGL4;
using GlPixelFormat = OpenTK.Graphics.OpenGL4.PixelFormat;

#pragma warning disable CA1416  // Windows-only

namespace MassiveSlicer.App.Views;

// ═══════════════════════════════════════════════════════════════════════════════
// WHY THIS IS WINDOWS-ONLY (WGL P/INVOKE INSTEAD OF OpenGlControlBase)
// ═══════════════════════════════════════════════════════════════════════════════
//
// The natural cross-platform approach for embedding OpenGL in Avalonia is to
// inherit from Avalonia.OpenGL.Controls.OpenGlControlBase. That worked on macOS
// and Linux but CRASHED on Windows — specifically on AMD GPUs (driver atio6axx)
// — because Avalonia's OpenGlControlBase allocates and disposes its own FBO on
// every resize, and AMD's Windows driver enforces a strict rule: you MUST detach
// all texture/renderbuffer attachments from an FBO before calling glDeleteFramebuffer.
// Avalonia's internal FBO teardown does not do this, causing an access violation
// inside atio6axx.dll on the first window resize.
//
// The fix is to NEVER let Avalonia manage a GL framebuffer on Windows. Instead:
//   1. Create our own WGL context on a hidden off-screen 1x1 HWND (no Avalonia
//      framebuffer is ever allocated or touched by the driver during resize).
//   2. Render into our own FBO whose teardown code manually detaches all
//      attachments and calls GL.Finish() both before and after (see DestroyResources).
//   3. Read the finished pixels back to CPU with glReadPixels and blit them into
//      an Avalonia WriteableBitmap displayed by a normal Image control.
//
// This avoids the AMD resize crash entirely and also sidesteps the Win32 airspace
// problem (no native child HWND is visible on screen, so Avalonia overlay controls
// placed above this in the Grid z-order just work).
//
// If OpenGlControlBase is ever fixed upstream to safely handle FBO teardown on AMD,
// or if a GL extension path is found that avoids the crash, this file can be replaced
// with a much simpler OpenGlControlBase subclass. Until then, DO NOT replace this
// implementation on Windows — the crash is real and reproducible.
//
// See also: DestroyResources() below for the AMD-safe detach-before-delete sequence.
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Windows-only OpenGL host. Renders into a private WGL off-screen context and
/// FBO, then presents each frame by copying pixels into an Avalonia
/// <see cref="WriteableBitmap"/>. See the block comment above the class for the
/// full explanation of why the cross-platform <c>OpenGlControlBase</c> cannot be
/// used on Windows.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class GlHostControl : UserControl, IDisposable
{
    // ── Public GL lifecycle events ────────────────────────────────────────────

    public event Action?                        GlInitialized;
    public event Action<TimeSpan, int, int>?    GlRender;
    public event Action?                        GlDeinitialized;

    // ── Display ───────────────────────────────────────────────────────────────

    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private WriteableBitmap? _bitmap;

    // ── WGL context (hidden 1×1 HWND) ─────────────────────────────────────────

    private IntPtr _hwnd, _hdc, _hglrc;

    // ── Output FBO (what SceneRenderer composites into) ───────────────────────

    private int _outputFbo, _outputColorTex, _outputDepthRbo;
    private int _fboW, _fboH;

    // ── CPU pixel buffers ─────────────────────────────────────────────────────
    // _rawPixels receives the bottom-up GL read; _staging holds the Y-flipped
    // result ready for the Avalonia WriteableBitmap.

    private byte[]? _rawPixels;
    private byte[]? _staging;

    // ── GL thread ─────────────────────────────────────────────────────────────

    private Thread? _glThread;
    private volatile bool _running;
    private volatile int _pendingW, _pendingH;
    private readonly System.Threading.ManualResetEventSlim _frameSignal = new(false);

    // ── Timing ────────────────────────────────────────────────────────────────

    private TimeSpan _lastRenderTime;
    private bool _firstFrame = true;

    // ── Construction ──────────────────────────────────────────────────────────

    public GlHostControl()
    {
        Content = _image;

        AttachedToVisualTree   += OnAttached;
        DetachedFromVisualTree += OnDetached;
    }

    /// <summary>Queues one render frame on the GL thread. Safe to call from any thread.</summary>
    public void RequestNextFrameRendering() => _frameSignal.Set();

    // ── Avalonia lifecycle ────────────────────────────────────────────────────

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _running = true;
        _glThread = new Thread(GlThreadProc) { Name = "GL-Offscreen", IsBackground = true };
        _glThread.Start();
    }

    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _running = false;
        _frameSignal.Set(); // unblock GL thread so it can exit cleanly
        _glThread?.Join(3000);
        _glThread = null;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        _pendingW = Math.Max(1, (int)(Bounds.Width  * scale));
        _pendingH = Math.Max(1, (int)(Bounds.Height * scale));
        RequestNextFrameRendering();
    }

    // ── GL thread ─────────────────────────────────────────────────────────────

    private void GlThreadProc()
    {
        try
        {
            CreateContext();
            GL.LoadBindings(new WglBindingsContext());

            GlInitialized?.Invoke();

            while (_running)
            {
                _frameSignal.Wait();
                _frameSignal.Reset();
                if (!_running) break;

                int w = _pendingW, h = _pendingH;
                if (w <= 0 || h <= 0) continue;

                if (w != _fboW || h != _fboH)
                    ResizeResources(w, h);

                // Render into our output FBO.  SceneRenderer reads the currently
                // bound draw FBO via GetInteger(DrawFramebufferBinding) and uses it
                // as its composite target, so we bind it before firing the event.
                var now   = TimeSpan.FromMilliseconds(Environment.TickCount64);
                var delta = _firstFrame ? TimeSpan.Zero : now - _lastRenderTime;
                _lastRenderTime = now;
                _firstFrame     = false;

                GL.BindFramebuffer(FramebufferTarget.Framebuffer, _outputFbo);
                GlRender?.Invoke(delta, w, h);

                // Synchronous pixel readback: stalls ~1–3 ms until the GPU finishes,
                // then the frame is immediately available with zero presentation delay.
                // For an on-demand viewer this is preferable to a one-frame async lag.
                ReadPixelsAndPresent(w, h);
            }

            // Fire deinit on the GL context so SceneRenderer can release GPU resources.
            wglMakeCurrent(_hdc, _hglrc);
            GlDeinitialized?.Invoke();
        }
        finally
        {
            DestroyResources();
            DestroyContext();
        }
    }

    // ── Synchronous readback → WriteableBitmap ───────────────────────────────

    private void ReadPixelsAndPresent(int w, int h)
    {
        int stride  = w * 4;
        int bufSize = stride * h;

        if (_rawPixels is null || _rawPixels.Length != bufSize) _rawPixels = new byte[bufSize];
        if (_staging   is null || _staging.Length   != bufSize) _staging   = new byte[bufSize];

        // Blocking read — GPU stalls until all prior draw calls complete and the
        // pixels are transferred to _rawPixels.  Typically 1–3 ms on modern hardware.
        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _outputFbo);
        GL.ReadPixels(0, 0, w, h, GlPixelFormat.Rgba, PixelType.UnsignedByte, _rawPixels);

        // Flip rows: OpenGL origin is bottom-left; Avalonia bitmap origin is top-left.
        for (int row = 0; row < h; row++)
            System.Buffer.BlockCopy(_rawPixels, (h - 1 - row) * stride, _staging, row * stride, stride);

        var staging = _staging;
        int capturedW = w, capturedH = h;
        Dispatcher.UIThread.InvokeAsync(() => UpdateBitmap(staging, capturedW, capturedH))
                           .GetAwaiter().GetResult();
    }

    private void UpdateBitmap(byte[] staging, int w, int h)
    {
        if (_bitmap is null || _bitmap.PixelSize.Width != w || _bitmap.PixelSize.Height != h)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(
                new PixelSize(w, h),
                new Vector(96, 96),
                PixelFormats.Rgba8888,
                AlphaFormat.Opaque);
            _image.Source = _bitmap;
        }

        using var fb = _bitmap.Lock();
        Marshal.Copy(staging, 0, fb.Address, staging.Length);
        _image.InvalidateVisual();
    }

    // ── Output FBO + PBO lifecycle ────────────────────────────────────────────

    private void ResizeResources(int w, int h)
    {
        DestroyResources();

        // Output FBO: SceneRenderer composites its final frame here.
        // A depth attachment is needed because SceneRenderer's overlay and gizmo
        // passes clear and test depth against this FBO.
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

        // AMD Windows driver (atio6axx) requirement: MUST explicitly detach every
        // texture and renderbuffer from the FBO before calling glDeleteFramebuffer,
        // and MUST call GL.Finish() to drain the GPU pipeline both before and after
        // detaching. Skipping either step causes an access violation in atio6axx.dll.
        // This exact sequence is what makes the WGL approach mandatory on Windows --
        // Avalonia's OpenGlControlBase does not perform this teardown and crashes.
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

    // ── WGL context creation / destruction ────────────────────────────────────

    private void CreateContext()
    {
        EnsureWindowClassRegistered();

        // Hidden 1×1 top-level window. We never display it — it exists only to
        // give us a valid HDC from which WGL can create an OpenGL context.
        _hwnd = CreateWindowExW(
            0, GlWindowClassName, null,
            WS_POPUP,
            0, 0, 1, 1,
            IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException(
                $"CreateWindowExW failed: {Marshal.GetLastWin32Error()}");

        _hdc   = GetDC(_hwnd);
        SetupPixelFormat(_hdc);
        _hglrc = CreateGlContext(_hdc);

        if (_hglrc == IntPtr.Zero)
            throw new InvalidOperationException("WGL context creation failed.");

        wglMakeCurrent(_hdc, _hglrc);
    }

    private void DestroyContext()
    {
        wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
        if (_hglrc != IntPtr.Zero) { wglDeleteContext(_hglrc); _hglrc = IntPtr.Zero; }
        if (_hdc   != IntPtr.Zero && _hwnd != IntPtr.Zero)
        { ReleaseDC(_hwnd, _hdc); _hdc = IntPtr.Zero; }
        if (_hwnd  != IntPtr.Zero) { DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; }
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        _running = false;
        _frameSignal.Set();
        _glThread?.Join(3000);
        _bitmap?.Dispose();
        _bitmap = null;
        _frameSignal.Dispose();
    }

    // ── WGL helpers ───────────────────────────────────────────────────────────

    private static void SetupPixelFormat(IntPtr hdc)
    {
        var pfd = new PIXELFORMATDESCRIPTOR
        {
            nSize      = (ushort)Marshal.SizeOf<PIXELFORMATDESCRIPTOR>(),
            nVersion   = 1,
            dwFlags    = PFD_DRAW_TO_WINDOW | PFD_SUPPORT_OPENGL | PFD_DOUBLEBUFFER,
            iPixelType = PFD_TYPE_RGBA,
            cColorBits = 32,
            cDepthBits = 24,
            iLayerType = PFD_MAIN_PLANE,
        };
        int fmt = ChoosePixelFormat(hdc, ref pfd);
        SetPixelFormat(hdc, fmt, ref pfd);
    }

    private static IntPtr CreateGlContext(IntPtr hdc)
    {
        // Bootstrap a legacy context just long enough to load wglCreateContextAttribsARB.
        var temp = wglCreateContext(hdc);
        wglMakeCurrent(hdc, temp);

        var ptr = wglGetProcAddress("wglCreateContextAttribsARB");
        if (ptr == IntPtr.Zero)
            return temp; // old driver — keep legacy context

        var createAttribs =
            Marshal.GetDelegateForFunctionPointer<WglCreateContextAttribsARB>(ptr);

        int[] attribs =
        [
            WGL_CONTEXT_MAJOR_VERSION_ARB,  3,
            WGL_CONTEXT_MINOR_VERSION_ARB,  3,
            WGL_CONTEXT_PROFILE_MASK_ARB,   WGL_CONTEXT_CORE_PROFILE_BIT_ARB,
            WGL_CONTEXT_FLAGS_ARB,          WGL_CONTEXT_FORWARD_COMPATIBLE_BIT_ARB,
            0,
        ];
        var ctx = createAttribs(hdc, IntPtr.Zero, attribs);
        wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
        wglDeleteContext(temp);
        return ctx;
    }

    // ── Window class registration ─────────────────────────────────────────────

    private const string GlWindowClassName = "MassiveSlicerOffscreenGl";
    private static volatile bool _classRegistered;
    private static readonly object _classLock = new();

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);
    private static IntPtr MinimalWndProc(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp)
        => DefWindowProcW(hwnd, msg, wp, lp);
    private static readonly WndProcDelegate s_wndProc    = MinimalWndProc;
    private static readonly IntPtr          s_wndProcPtr =
        Marshal.GetFunctionPointerForDelegate(s_wndProc);

    private static void EnsureWindowClassRegistered()
    {
        if (_classRegistered) return;
        lock (_classLock)
        {
            if (_classRegistered) return;
            var wc = new WNDCLASSEXW
            {
                cbSize        = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                style         = CS_OWNDC,
                lpfnWndProc   = s_wndProcPtr,
                hInstance     = GetModuleHandleW(null),
                lpszClassName = GlWindowClassName,
            };
            RegisterClassExW(ref wc);
            _classRegistered = true;
        }
    }

    // ── OpenTK bindings context ───────────────────────────────────────────────

    private sealed class WglBindingsContext : IBindingsContext
    {
        public IntPtr GetProcAddress(string name)
        {
            var p = wglGetProcAddress(name);
            if (p != IntPtr.Zero) return p;
            var mod = GetModuleHandleW("opengl32.dll");
            return mod != IntPtr.Zero ? GetProcAddressNative(mod, name) : IntPtr.Zero;
        }
    }

    // ── P/Invoke ──────────────────────────────────────────────────────────────

    private delegate IntPtr WglCreateContextAttribsARB(
        IntPtr hDC, IntPtr hShareContext, int[] attribList);

    private const uint WS_POPUP      = 0x80000000;
    private const uint CS_OWNDC      = 0x0020;

    private const uint PFD_DRAW_TO_WINDOW = 0x00000004;
    private const uint PFD_SUPPORT_OPENGL = 0x00000020;
    private const uint PFD_DOUBLEBUFFER   = 0x00000001;
    private const byte PFD_TYPE_RGBA      = 0;
    private const byte PFD_MAIN_PLANE     = 0;

    private const int WGL_CONTEXT_MAJOR_VERSION_ARB          = 0x2091;
    private const int WGL_CONTEXT_MINOR_VERSION_ARB          = 0x2092;
    private const int WGL_CONTEXT_FLAGS_ARB                  = 0x2094;
    private const int WGL_CONTEXT_PROFILE_MASK_ARB           = 0x9126;
    private const int WGL_CONTEXT_CORE_PROFILE_BIT_ARB       = 0x00000001;
    private const int WGL_CONTEXT_FORWARD_COMPATIBLE_BIT_ARB = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct PIXELFORMATDESCRIPTOR
    {
        public ushort nSize, nVersion;
        public uint   dwFlags;
        public byte   iPixelType, cColorBits, cRedBits,  cRedShift;
        public byte   cGreenBits, cGreenShift, cBlueBits, cBlueShift;
        public byte   cAlphaBits, cAlphaShift, cAccumBits;
        public byte   cAccumRedBits, cAccumGreenBits, cAccumBlueBits, cAccumAlphaBits;
        public byte   cDepthBits, cStencilBits, cAuxBuffers, iLayerType, bReserved;
        public uint   dwLayerMask, dwVisibleMask, dwDamageMask;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint   cbSize, style;
        public IntPtr lpfnWndProc;
        public int    cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string? lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")] private static extern bool   DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int    ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);

    [DllImport("gdi32.dll")] private static extern int  ChoosePixelFormat(IntPtr hdc, ref PIXELFORMATDESCRIPTOR ppfd);
    [DllImport("gdi32.dll")] private static extern bool SetPixelFormat(IntPtr hdc, int fmt, ref PIXELFORMATDESCRIPTOR ppfd);

    [DllImport("opengl32.dll")] private static extern IntPtr wglCreateContext(IntPtr hdc);
    [DllImport("opengl32.dll")] private static extern bool   wglDeleteContext(IntPtr hglrc);
    [DllImport("opengl32.dll")] private static extern bool   wglMakeCurrent(IntPtr hdc, IntPtr hglrc);
    [DllImport("opengl32.dll")] private static extern IntPtr wglGetProcAddress(string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);

    [DllImport("kernel32.dll", EntryPoint = "GetProcAddress", CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddressNative(IntPtr hModule, string procName);
}
