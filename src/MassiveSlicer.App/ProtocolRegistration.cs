namespace MassiveSlicer.App;

/// <summary>HKCU URL protocol so the browser can prompt "Open MassiveSLICER".</summary>
public static class ProtocolRegistration
{
    public static void Ensure()
    {
#if WINDOWS
        try
        {
            string exe = Environment.ProcessPath ?? "";
            if (exe.Length == 0 || !File.Exists(exe)) return;
            using var root = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                @"Software\Classes\" + MassiveSlicer.Core.IO.ProtocolUri.Scheme);
            if (root is null) return;
            root.SetValue("", "URL:MassiveSLICER");
            root.SetValue("URL Protocol", "");
            using (var icon = root.CreateSubKey("DefaultIcon"))
                icon?.SetValue("", exe + ",0");
            using var cmd = root.CreateSubKey(@"shell\open\command");
            cmd?.SetValue("", "\"" + exe + "\" \"%1\"");
        }
        catch
        {
            // best-effort — missing HKCU rights should not block launch
        }
#endif
    }
}
