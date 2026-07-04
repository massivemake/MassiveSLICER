using System.IO;
using System.Runtime.InteropServices;

namespace MassiveSlicer.App;

/// <summary>
/// Sets the macOS Dock / Cmd+Tab icon at runtime via NSApplication.applicationIconImage.
/// Works regardless of how the process was launched (app bundle, `dotnet run`, script),
/// which app-bundle metadata alone does not guarantee for exec'd .NET processes.
/// </summary>
internal static class MacDockIcon
{
    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr objc_getClass(string name);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr sel_registerName(string name);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector, IntPtr arg);

    /// <summary>Sets the Dock icon from a PNG beside the executable. No-op off macOS.</summary>
    public static void TrySet(string pngFileName)
    {
        if (!OperatingSystem.IsMacOS()) return;
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, pngFileName);
            if (!File.Exists(path)) return;

            IntPtr nsStringCls = objc_getClass("NSString");
            IntPtr nsImageCls  = objc_getClass("NSImage");
            IntPtr nsAppCls    = objc_getClass("NSApplication");
            if (nsStringCls == IntPtr.Zero || nsImageCls == IntPtr.Zero || nsAppCls == IntPtr.Zero) return;

            IntPtr utf8 = Marshal.StringToHGlobalAnsi(path);
            try
            {
                IntPtr nsPath = objc_msgSend(nsStringCls, sel_registerName("stringWithUTF8String:"), utf8);
                if (nsPath == IntPtr.Zero) return;

                IntPtr image = objc_msgSend(objc_msgSend(nsImageCls, sel_registerName("alloc")),
                                            sel_registerName("initWithContentsOfFile:"), nsPath);
                if (image == IntPtr.Zero) return;

                IntPtr app = objc_msgSend(nsAppCls, sel_registerName("sharedApplication"));
                if (app == IntPtr.Zero) return;

                objc_msgSend(app, sel_registerName("setApplicationIconImage:"), image);
            }
            finally
            {
                Marshal.FreeHGlobal(utf8);
            }
        }
        catch
        {
            // Cosmetic only — never let the Dock icon break startup.
        }
    }
}
