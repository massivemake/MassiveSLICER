using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MassiveSlicer.Viewport.Loading;

/// <summary>
/// The third-party Occt.NET binding (TianTeng) raises a native Win32 MessageBox on
/// first STEP load — a license/machine-id notice with garbled Chinese text under a
/// non-CJK code page (title mojibake, body contains "occt.NET!" + a hex machine id).
/// This suppressor finds those dialogs (and, while active, any #32770 owned by this
/// process) and dismisses them so the import UX stays clean.
/// </summary>
internal static class OcctUiSuppressor
{
    const int  WM_CLOSE     = 0x0010;
    const int  WM_COMMAND   = 0x0111;
    const int  WM_KEYDOWN   = 0x0100;
    const int  WM_KEYUP     = 0x0101;
    const int  BN_CLICKED   = 0;
    const int  IDOK         = 1;
    const int  VK_RETURN    = 0x0D;
    const int  VK_ESCAPE    = 0x1B;
    const uint BM_CLICK     = 0x00F5;
    const uint SMTO_ABORTIFHUNG = 0x0002;

    static int _active;
    static readonly int OurPid = Environment.ProcessId;

    /// <summary>Begin suppressing Occt-related MessageBoxes until disposed.</summary>
    public static IDisposable Begin()
    {
        Interlocked.Increment(ref _active);
        // Immediate pass in case a dialog is already up from a prior attempt.
        try { DismissOcctDialogs(); } catch { /* never fail host */ }

        var cts = new CancellationTokenSource();
        var task = Task.Run(async () =>
        {
            try
            {
                // Poll for a while around load; dialog usually appears on first native call.
                var deadline = DateTime.UtcNow.AddSeconds(45);
                while (!cts.IsCancellationRequested && DateTime.UtcNow < deadline)
                {
                    // Stay active while refcount > 0; after Dispose keep a short grace
                    // so a late dialog that races Dispose still gets closed.
                    DismissOcctDialogs();
                    await Task.Delay(25, cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { /* normal */ }
            catch { /* never let suppressor crash the host */ }
        }, cts.Token);

        return new Scope(cts, task);
    }

    sealed class Scope : IDisposable
    {
        readonly CancellationTokenSource _cts;
        readonly Task _task;
        int _disposed;

        public Scope(CancellationTokenSource cts, Task task)
        {
            _cts = cts;
            _task = task;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Interlocked.Decrement(ref _active);

            // Grace: keep polling ~1.5s after the load scope ends so a dialog that
            // appears at the very end of Configure/Tessellate still gets dismissed.
            _ = Task.Run(async () =>
            {
                try
                {
                    var until = DateTime.UtcNow.AddMilliseconds(1500);
                    while (DateTime.UtcNow < until)
                    {
                        DismissOcctDialogs();
                        await Task.Delay(25).ConfigureAwait(false);
                    }
                }
                catch { /* ignore */ }
                finally
                {
                    try { _cts.Cancel(); } catch { /* ignore */ }
                    try { _cts.Dispose(); } catch { /* ignore */ }
                }
            });

            // Don't block the import thread on the poller.
            try { _task.Wait(50); } catch { /* ignore */ }
        }
    }

    static void DismissOcctDialogs()
    {
        EnumWindows(static (hWnd, _) =>
        {
            try
            {
                if (!IsWindowVisible(hWnd)) return true;

                GetWindowThreadProcessId(hWnd, out var pid);
                if (pid != (uint)OurPid) return true;

                var cls = GetClassName(hWnd);
                if (!string.Equals(cls, "#32770", StringComparison.Ordinal))
                    return true;

                // While we are (or just were) in a suppress window, close Occt-looking
                // dialogs aggressively. Also close any bare MessageBox that matches the
                // known license pattern (machine-id hex / "occt" / garbled CJK + OK).
                var title = GetWindowText(hWnd);
                var body  = GetDialogBodyText(hWnd);

                var active = Volatile.Read(ref _active) > 0;
                if (!LooksLikeOcctDialog(title, body, active))
                    return true;

                ForceDismiss(hWnd);
            }
            catch { /* keep enumerating */ }

            return true;
        }, IntPtr.Zero);
    }

    static void ForceDismiss(IntPtr hWnd)
    {
        // 1) Classic MessageBox OK (control id 1).
        var ok = GetDlgItem(hWnd, IDOK);
        if (ok != IntPtr.Zero)
        {
            // BM_CLICK synthesizes down+up; SendMessage is more reliable than Post
            // while the dialog's modal loop is running on another thread.
            SendMessageTimeout(ok, BM_CLICK, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 100, out _);
            PostMessage(ok, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
        }

        // 2) WM_COMMAND IDOK to the dialog itself.
        var wParam = MakeWParam(IDOK, BN_CLICKED);
        SendMessageTimeout(hWnd, WM_COMMAND, wParam, ok, SMTO_ABORTIFHUNG, 100, out _);
        PostMessage(hWnd, WM_COMMAND, wParam, ok);

        // 3) Enter key (default button).
        PostMessage(hWnd, WM_KEYDOWN, (IntPtr)VK_RETURN, IntPtr.Zero);
        PostMessage(hWnd, WM_KEYUP,   (IntPtr)VK_RETURN, IntPtr.Zero);

        // 4) EndDialog / WM_CLOSE as last resort.
        try { EndDialog(hWnd, IDOK); } catch { /* not every #32770 is a dialog template */ }
        PostMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    static bool LooksLikeOcctDialog(string title, string body, bool suppressActive)
    {
        static bool Has(string s, string token)
            => s.Contains(token, StringComparison.OrdinalIgnoreCase);

        var combined = title + "\n" + body;

        // Explicit Occt.NET markers (ASCII survives the garbled Chinese code page).
        if (Has(combined, "occt") || Has(combined, "OpenCas") || Has(combined, "Open CASCADE"))
            return true;

        // Known machine-id prefix from this package's license dialog (see screenshot).
        if (Has(combined, "92FA0A54") || Has(combined, "92FA"))
            return true;

        // Long hex machine-code blob (32 hex chars, with or without dashes).
        if (HasLongHexToken(combined))
            return true;

        // Garbled CJK license dialogs: mojibake title + short body.
        // Only while our suppress scope is active so we don't close random app dialogs.
        // Avalonia dialogs are not #32770; the file picker is already closed before load,
        // so any MessageBox owned by us during STEP import is almost certainly Occt.NET.
        if (suppressActive && (body.Length is > 0 and < 400 || title.Length is > 0 and < 80))
            return true;

        return false;
    }

    static bool HasLongHexToken(string s)
    {
        int run = 0;
        foreach (var c in s)
        {
            if (c is (>= '0' and <= '9') or (>= 'A' and <= 'F') or (>= 'a' and <= 'f'))
            {
                if (++run >= 24) return true;
            }
            else if (c is '-' or ' ' or '\r' or '\n')
            {
                // allow separators inside machine codes
            }
            else
            {
                run = 0;
            }
        }
        return run >= 24;
    }

    static string GetDialogBodyText(IntPtr hDlg)
    {
        // Static control for MessageBox text is usually ID 0xFFFF.
        var sb = new StringBuilder(1024);
        var staticHwnd = GetDlgItem(hDlg, 0xFFFF);
        if (staticHwnd != IntPtr.Zero)
        {
            GetWindowText(staticHwnd, sb, sb.Capacity);
            if (sb.Length > 0) return sb.ToString();
        }

        // Fallback: concat child window texts (button labels + statics).
        var acc = new StringBuilder();
        EnumChildWindows(hDlg, (h, _) =>
        {
            var t = GetWindowText(h);
            if (t.Length > 0) acc.Append(t).Append(' ');
            return true;
        }, IntPtr.Zero);
        return acc.ToString();
    }

    static string GetClassName(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    static string GetWindowText(IntPtr hWnd)
    {
        var sb = new StringBuilder(512);
        GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    static IntPtr MakeWParam(int lo, int hi) => (IntPtr)((hi << 16) | (lo & 0xFFFF));

    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    static extern IntPtr GetDlgItem(IntPtr hDlg, int nIDDlgItem);

    [DllImport("user32.dll")]
    static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    static extern bool EndDialog(IntPtr hDlg, IntPtr nResult);
}
