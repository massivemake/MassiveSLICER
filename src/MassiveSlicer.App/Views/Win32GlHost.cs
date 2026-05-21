using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
#pragma warning disable CA1416 // Windows-only
using OpenTK;
using OpenTK.Graphics.OpenGL4;

namespace MassiveSlicer.App.Views;

/// <summary>
/// OpenGL host built on a native Win32 HWND + WGL context instead of
/// Avalonia's <c>OpenGlControlBase</c>. Avalonia's FBO-based control calls
/// <c>glTexImage2D</c> / <c>glRenderbufferStorage</c> when the control grows,
/// which hits a bug in AMD's OpenGL ICD (<c>atio6axx.dll</c>, 0xc0000005 at
/// 0x9372ac). A plain Win32 child window lets the OS resize the surface at the
/// <c>WM_SIZE</c> level — the same path WPF's <c>GLWpfControl</c> used — and
/// AMD's driver does not crash on that code path.
/// </summary>
internal sealed class Win32GlHost : NativeControlHost
{
    private IntPtr _hwnd;
    private IntPtr _hdc;
    private IntPtr _hglrc;
    private bool   _initialized;
    private DispatcherTimer? _renderTimer;
    private TimeSpan _lastRenderTime;
    private bool     _firstFrame = true;

    // Maps native HWND → instance so the static WndProc can call instance methods.
    private static readonly ConcurrentDictionary<IntPtr, Win32GlHost> s_instances = new();

    /// <summary>Fired once on the GL thread after the context is created and OpenTK is loaded.</summary>
    public event Action? GlInitialized;

    /// <summary>Fired each frame on the UI thread. Args: elapsed, physical width px, physical height px.</summary>
    public event Action<TimeSpan, int, int>? GlRender;

    /// <summary>Fired before the GL context is destroyed.</summary>
    public event Action? GlDeinitialized;

    /// <summary>Schedules one render on the next dispatcher frame. Safe to call from the UI thread.</summary>
    public void RequestNextFrameRendering()
    {
        if (_renderTimer is { IsEnabled: true }) return;
        _renderTimer ??= new DispatcherTimer(
            TimeSpan.Zero, DispatcherPriority.Render, OnRenderTick);
        _renderTimer.Start();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_initialized)
            RequestNextFrameRendering();
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        EnsureWindowClassRegistered();

        _hwnd = CreateWindowExW(
            0,
            GlWindowClassName,
            null,
            WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN,
            0, 0, 4, 4,
            parent.Handle,
            IntPtr.Zero,
            GetModuleHandleW(null),
            IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException(
                $"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");

        _hdc   = GetDC(_hwnd);
        SetupPixelFormat(_hdc);
        _hglrc = CreateGlContext(_hdc);

        if (_hglrc == IntPtr.Zero)
            throw new InvalidOperationException("Failed to create WGL context.");

        wglMakeCurrent(_hdc, _hglrc);
        GL.LoadBindings(new WglBindingsContext());

        s_instances[_hwnd] = this;
        _initialized = true;
        GlInitialized?.Invoke();

        RequestNextFrameRendering();
        return new PlatformHandle(_hwnd, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        _renderTimer?.Stop();
        _renderTimer = null;

        if (_hwnd != IntPtr.Zero)
            s_instances.TryRemove(_hwnd, out _);

        if (_initialized)
        {
            _initialized = false;
            wglMakeCurrent(_hdc, _hglrc);
            GlDeinitialized?.Invoke();
        }

        wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);

        if (_hglrc != IntPtr.Zero) { wglDeleteContext(_hglrc); _hglrc = IntPtr.Zero; }
        if (_hdc   != IntPtr.Zero && _hwnd != IntPtr.Zero)
        {
            ReleaseDC(_hwnd, _hdc);
            _hdc = IntPtr.Zero;
        }
        _hwnd = IntPtr.Zero;
    }

    private void OnRenderTick(object? sender, EventArgs e)
    {
        _renderTimer?.Stop();
        if (!_initialized || _hwnd == IntPtr.Zero) return;

        GetClientRect(_hwnd, out var rect);
        int w = Math.Max(1, rect.Right  - rect.Left);
        int h = Math.Max(1, rect.Bottom - rect.Top);

        var now   = TimeSpan.FromMilliseconds(Environment.TickCount64);
        var delta = _firstFrame ? TimeSpan.Zero : now - _lastRenderTime;
        _lastRenderTime = now;
        _firstFrame     = false;

        wglMakeCurrent(_hdc, _hglrc);
        GlRender?.Invoke(delta, w, h);
        SwapBuffers(_hdc);
    }

    // ── WGL setup ─────────────────────────────────────────────────────────────

    private static void SetupPixelFormat(IntPtr hdc)
    {
        var pfd = new PIXELFORMATDESCRIPTOR
        {
            nSize        = (ushort)Marshal.SizeOf<PIXELFORMATDESCRIPTOR>(),
            nVersion     = 1,
            dwFlags      = PFD_DRAW_TO_WINDOW | PFD_SUPPORT_OPENGL | PFD_DOUBLEBUFFER,
            iPixelType   = PFD_TYPE_RGBA,
            cColorBits   = 32,
            cDepthBits   = 24,
            cStencilBits = 8,
            iLayerType   = PFD_MAIN_PLANE,
        };
        int fmt = ChoosePixelFormat(hdc, ref pfd);
        SetPixelFormat(hdc, fmt, ref pfd);
    }

    private static IntPtr CreateGlContext(IntPtr hdc)
    {
        // Bootstrap a legacy context to load WGL_ARB_create_context
        var temp = wglCreateContext(hdc);
        wglMakeCurrent(hdc, temp);

        var ptr = wglGetProcAddress("wglCreateContextAttribsARB");
        if (ptr == IntPtr.Zero)
            return temp; // driver too old; fall back to legacy context

        var createAttribs =
            Marshal.GetDelegateForFunctionPointer<WglCreateContextAttribsARB>(ptr);

        int[] attribs =
        [
            WGL_CONTEXT_MAJOR_VERSION_ARB,   3,
            WGL_CONTEXT_MINOR_VERSION_ARB,   3,
            WGL_CONTEXT_PROFILE_MASK_ARB,    WGL_CONTEXT_CORE_PROFILE_BIT_ARB,
            WGL_CONTEXT_FLAGS_ARB,           WGL_CONTEXT_FORWARD_COMPATIBLE_BIT_ARB,
            0,
        ];
        var ctx = createAttribs(hdc, IntPtr.Zero, attribs);
        wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
        wglDeleteContext(temp);
        return ctx;
    }

    // ── Win32 window class ─────────────────────────────────────────────────────

    private const string GlWindowClassName   = "MassiveSlicerGlHost";
    private static volatile bool _classRegistered;
    private static readonly object _classLock = new();

    // Delegate instance held statically to prevent GC collection while the WNDCLASS is registered.
    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);
    private static IntPtr CustomWndProc(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp)
    {
        // Snap / maximize / restore resize: WM_SIZE fires reliably for all of these
        // whereas Avalonia's OnSizeChanged only fires during drag resize. Request a
        // new frame so the viewport redraws immediately at the new size.
        if (msg == WM_SIZE && s_instances.TryGetValue(hwnd, out var host))
            host.RequestNextFrameRendering();

        // WM_MOUSEWHEEL reaches Avalonia via the focus chain and works without help.
        // All other mouse messages are sent to the window under the cursor (this native
        // HWND). Forward them to the Avalonia root window with converted coordinates so
        // Avalonia's input pipeline raises PointerPressed / PointerMoved / etc.
        if (msg >= WM_MOUSEFIRST && msg <= WM_MOUSELAST
            && msg != WM_MOUSEWHEEL && msg != WM_MOUSEHWHEEL)
        {
            var root = GetAncestor(hwnd, GA_ROOT);
            if (root != IntPtr.Zero && root != hwnd)
            {
                var pt = new POINT
                {
                    X = (short)(lp.ToInt32() & 0xFFFF),
                    Y = (short)((lp.ToInt32() >> 16) & 0xFFFF),
                };
                MapWindowPoints(hwnd, root, ref pt, 1);
                var newLp = new IntPtr(
                    (int)(((uint)(pt.Y & 0xFFFF) << 16) | (uint)(pt.X & 0xFFFF)));
                PostMessageW(root, msg, wp, newLp);
            }
            return IntPtr.Zero;
        }
        return DefWindowProcW(hwnd, msg, wp, lp);
    }
    private static readonly WndProcDelegate s_wndProc = CustomWndProc;
    private static readonly IntPtr s_wndProcPtr =
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
                hCursor       = LoadCursorW(IntPtr.Zero, IDC_ARROW),
            };
            RegisterClassExW(ref wc);
            _classRegistered = true;
        }
    }

    // ── OpenTK bindings context ────────────────────────────────────────────────

    private sealed class WglBindingsContext : IBindingsContext
    {
        public IntPtr GetProcAddress(string name)
        {
            var p = wglGetProcAddress(name);
            if (p != IntPtr.Zero) return p;
            // Core functions live in opengl32.dll, not returned by wglGetProcAddress
            var mod = GetModuleHandleW("opengl32.dll");
            return mod != IntPtr.Zero ? GetProcAddressNative(mod, name) : IntPtr.Zero;
        }
    }

    // ── P/Invoke ──────────────────────────────────────────────────────────────

    private delegate IntPtr WglCreateContextAttribsARB(
        IntPtr hDC, IntPtr hShareContext, int[] attribList);

    private const uint WM_SIZE        = 0x0005;
    private const uint WM_MOUSEFIRST  = 0x0200;
    private const uint WM_MOUSELAST   = 0x020E;
    private const uint WM_MOUSEWHEEL  = 0x020A;
    private const uint WM_MOUSEHWHEEL = 0x020E;
    private const uint GA_ROOT        = 2;
    private const uint WS_CHILD        = 0x40000000;
    private const uint WS_VISIBLE      = 0x10000000;
    private const uint WS_CLIPSIBLINGS = 0x04000000;
    private const uint WS_CLIPCHILDREN = 0x02000000;
    private const uint CS_OWNDC        = 0x0020;
    private const uint PFD_DRAW_TO_WINDOW = 0x00000004;
    private const uint PFD_SUPPORT_OPENGL = 0x00000020;
    private const uint PFD_DOUBLEBUFFER   = 0x00000001;
    private const byte PFD_TYPE_RGBA      = 0;
    private const byte PFD_MAIN_PLANE     = 0;
    private const int  WGL_CONTEXT_MAJOR_VERSION_ARB           = 0x2091;
    private const int  WGL_CONTEXT_MINOR_VERSION_ARB           = 0x2092;
    private const int  WGL_CONTEXT_FLAGS_ARB                   = 0x2094;
    private const int  WGL_CONTEXT_PROFILE_MASK_ARB            = 0x9126;
    private const int  WGL_CONTEXT_CORE_PROFILE_BIT_ARB        = 0x00000001;
    private const int  WGL_CONTEXT_FORWARD_COMPATIBLE_BIT_ARB  = 0x00000002;
    private static readonly IntPtr IDC_ARROW = new(32512);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT  { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PIXELFORMATDESCRIPTOR
    {
        public ushort nSize, nVersion;
        public uint   dwFlags;
        public byte   iPixelType, cColorBits, cRedBits,     cRedShift;
        public byte   cGreenBits, cGreenShift, cBlueBits,   cBlueShift;
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

    [DllImport("user32.dll")] private static extern bool   GetClientRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursorW(IntPtr hInst, IntPtr name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int    ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);
    [DllImport("user32.dll")] private static extern int    MapWindowPoints(IntPtr hFrom, IntPtr hTo, ref POINT pt, int cPoints);
    [DllImport("user32.dll")] private static extern bool   PostMessageW(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);

    [DllImport("gdi32.dll")] private static extern int  ChoosePixelFormat(IntPtr hdc, ref PIXELFORMATDESCRIPTOR ppfd);
    [DllImport("gdi32.dll")] private static extern bool SetPixelFormat(IntPtr hdc, int fmt, ref PIXELFORMATDESCRIPTOR ppfd);
    [DllImport("gdi32.dll")] private static extern bool SwapBuffers(IntPtr hdc);

    [DllImport("opengl32.dll")] private static extern IntPtr wglCreateContext(IntPtr hdc);
    [DllImport("opengl32.dll")] private static extern bool   wglDeleteContext(IntPtr hglrc);
    [DllImport("opengl32.dll")] private static extern bool   wglMakeCurrent(IntPtr hdc, IntPtr hglrc);
    [DllImport("opengl32.dll")] private static extern IntPtr wglGetProcAddress(string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);

    [DllImport("kernel32.dll", EntryPoint = "GetProcAddress", CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddressNative(IntPtr hModule, string procName);
}
