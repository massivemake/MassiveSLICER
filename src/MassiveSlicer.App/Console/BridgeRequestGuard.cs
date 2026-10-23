using System.Net;

namespace MassiveSlicer.App.Console;

/// <summary>
/// Decides whether a <see cref="LocalControlBridge"/> request may run. The bridge has no login,
/// so without this any web page the user visits could POST to 127.0.0.1:8723/command and drive
/// the console — including robot motion.
///
/// Two checks:
///   1. Host must be loopback, an IP literal, or this machine's name. A DNS-rebinding page
///      reaches the bridge with Host = its own domain, so a domain name is rejected.
///   2. A request that came from a browser (Origin header, or Sec-Fetch-Site naming another
///      site) may only use the browser-safe routes — /ping, and /reveal + /open, which already
///      restrict paths to MassiveFILES. MassiveBOARD's graph uses those.
///
/// curl, Python, PowerShell, the MCP shim and the second-instance handoff send neither header,
/// so tooling is unaffected.
/// </summary>
public static class BridgeRequestGuard
{
    static readonly HashSet<string> BrowserSafeRoutes = new(StringComparer.Ordinal)
    {
        "/", "/ping", "/reveal", "/open",
    };

    /// <summary>Returns null when allowed, else the reason it was refused.</summary>
    public static string? Check(string method, string path, IReadOnlyDictionary<string, string> headers)
    {
        if (headers.TryGetValue("Host", out var host) && !IsTrustedHost(host))
            return "host not allowed";

        if (method == "OPTIONS") return null;

        if (IsFromBrowser(headers) && !BrowserSafeRoutes.Contains(path))
            return "browser requests may not use this route";

        return null;
    }

    public static bool IsFromBrowser(IReadOnlyDictionary<string, string> headers)
    {
        // Browsers send Origin on every POST and every cross-origin fetch, including "null"
        // from sandboxed frames and file:// pages.
        if (headers.ContainsKey("Origin")) return true;

        // <img>/<script>/navigation GETs carry no Origin, but modern browsers stamp
        // Sec-Fetch-Site. "none" is the user typing the URL themselves.
        if (headers.TryGetValue("Sec-Fetch-Site", out var site))
        {
            site = site.Trim();
            return !site.Equals("none", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    public static bool IsTrustedHost(string hostHeader)
    {
        string h = (hostHeader ?? "").Trim();
        if (h.Length == 0) return true;

        // Strip the port: "[::1]:8723", "127.0.0.1:8723", "localhost:8723".
        if (h.StartsWith('['))
        {
            int close = h.IndexOf(']');
            if (close < 0) return false;
            h = h[1..close];
        }
        else
        {
            int colon = h.LastIndexOf(':');
            if (colon >= 0 && h.IndexOf(':') == colon) h = h[..colon];
        }

        if (h.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        // IP literals: loopback, or a LAN address when the bridge listens on the LAN (Hermes).
        if (IPAddress.TryParse(h, out _)) return true;

        string machine = Environment.MachineName;
        return h.Equals(machine, StringComparison.OrdinalIgnoreCase)
            || h.Equals(machine + ".local", StringComparison.OrdinalIgnoreCase);
    }
}
