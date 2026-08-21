using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using MassiveSlicer.ViewModels;

namespace MassiveSlicer.App.Console;

/// <summary>
/// Minimal localhost HTTP control bridge so external tooling (curl / an MCP shim) can read the
/// console, send commands, and read robot status without a person relaying. Uses a raw
/// <see cref="TcpListener"/> bound to 127.0.0.1 (no http.sys URL-ACL reservation needed).
///
/// Endpoints:
///   GET  /ping                      -> { ok, app, port }
///   GET  /status                    -> { ok, connected, tool, base, pose:{ x,y,z,a,b,c } }
///   GET  /console?n=N               -> { ok, count, lines:[ { text, error, command } ] }
///   GET  /screenshot                -> { ok, path, bytes }  (full-window PNG under %LOCALAPPDATA%/MassiveSlicer/screenshots/)
///   GET  /screenshot?format=png     -> raw image/png body
///   GET  /materials                 -> PBR shader mode, lighting, layer toggles, active mesh maps
///   GET  /reveal?path=P             -> open Explorer (Windows) / Finder at that MassiveFILES folder
///   POST /reveal                    -> same, JSON or raw path body
///   GET  /open?path=P               -> open a .mass workspace in the running app
///   POST /open                      -> same, JSON or raw path body
///   POST /materials  {..}           -> partial update of PBR/material viewport state
///   POST /command   {"command":".."} -> { ok, ran, output:[ ".." ] }   (or raw body = the command)
///
/// All ViewModel access is marshalled to the UI thread. The chosen port is written to
/// %LOCALAPPDATA%\MassiveSlicer\bridge.port so tooling can discover it.
/// </summary>
public sealed class LocalControlBridge : IDisposable
{
    private readonly MainWindowViewModel _main;
    private readonly int _preferredPort;
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public int Port { get; private set; }

    public LocalControlBridge(MainWindowViewModel main, int preferredPort = 8723)
    {
        _main = main;
        _preferredPort = preferredPort;
    }

    /// <summary>Binds the first free port in [preferred, preferred+5] on 127.0.0.1. Returns the port, or 0 on failure.</summary>
    public int Start()
    {
        for (int p = _preferredPort; p <= _preferredPort + 5; p++)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, p);
                listener.Start();
                _listener = listener;
                Port = p;
                _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
                WritePortFile(p);
                return p;
            }
            catch { /* port busy — try the next */ }
        }
        return 0;
    }

    private static void WritePortFile(int port)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MassiveSlicer");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "bridge.port"), port.ToString());
        }
        catch { /* best-effort */ }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var listener = _listener!;
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch { break; }
            _ = Task.Run(() => HandleAsync(client, ct), ct);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using var c = client;
        try
        {
            c.NoDelay = true;
            using var stream = c.GetStream();
            var req = await ReadRequestAsync(stream, ct);
            if (req is null) return;
            var (method, path, body) = req.Value;
            int code = 200;
            try
            {
                string json = await RouteAsync(method, path, body);
                if (json.StartsWith("__raw_png__:", StringComparison.Ordinal))
                {
                    string filePath = json["__raw_png__:".Length..];
                    byte[] png = await File.ReadAllBytesAsync(filePath, ct);
                    await WriteBinaryAsync(stream, 200, "image/png", png, ct);
                    return;
                }
                await WriteAsync(stream, code, json, ct);
            }
            catch (Exception ex)
            {
                code = 500;
                string json = JsonSerializer.Serialize(new { ok = false, error = ex.Message }, Json);
                await WriteAsync(stream, code, json, ct);
            }
        }
        catch { /* per-connection failure: ignore */ }
    }

    private static async Task<(string Method, string Path, string Body)?> ReadRequestAsync(NetworkStream s, CancellationToken ct)
    {
        var buf = new byte[8192];
        var acc = new List<byte>();
        int headerEnd = -1;
        while (headerEnd < 0)
        {
            int n = await s.ReadAsync(buf, ct);
            if (n == 0) return null;
            for (int i = 0; i < n; i++) acc.Add(buf[i]);
            headerEnd = IndexOfHeaderEnd(acc);
            if (acc.Count > 1_000_000) return null;
        }
        string headerText = Encoding.ASCII.GetString(acc.ToArray(), 0, headerEnd);
        string[] lines = headerText.Split("\r\n");
        string[] reqLine = lines[0].Split(' ');
        if (reqLine.Length < 2) return null;

        int contentLength = 0;
        foreach (string h in lines.Skip(1))
        {
            int colon = h.IndexOf(':');
            if (colon > 0 && h.AsSpan(0, colon).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                int.TryParse(h.AsSpan(colon + 1).Trim(), out contentLength);
        }

        int bodyStart = headerEnd + 4;
        var bodyBytes = new List<byte>();
        for (int i = bodyStart; i < acc.Count; i++) bodyBytes.Add(acc[i]);
        while (bodyBytes.Count < contentLength)
        {
            int n = await s.ReadAsync(buf, ct);
            if (n == 0) break;
            for (int i = 0; i < n; i++) bodyBytes.Add(buf[i]);
        }
        string body = contentLength > 0
            ? Encoding.UTF8.GetString(bodyBytes.ToArray(), 0, Math.Min(bodyBytes.Count, contentLength))
            : "";
        return (reqLine[0], reqLine[1], body);
    }

    private static int IndexOfHeaderEnd(List<byte> d)
    {
        for (int i = 0; i + 3 < d.Count; i++)
            if (d[i] == 13 && d[i + 1] == 10 && d[i + 2] == 13 && d[i + 3] == 10) return i;
        return -1;
    }

    private async Task<string> RouteAsync(string method, string rawPath, string body)
    {
        string path = rawPath;
        string query = "";
        int qi = rawPath.IndexOf('?');
        if (qi >= 0) { path = rawPath[..qi]; query = rawPath[(qi + 1)..]; }

        if (method == "OPTIONS")
            return JsonSerializer.Serialize(new { ok = true }, Json);

        if ((method == "GET" || method == "POST") && path == "/reveal")
            return RevealPath(method == "GET" ? QueryValue(query, "path") : ParsePathBody(body));

        if ((method == "GET" || method == "POST") && path == "/open")
            return await OpenWorkspacePathAsync(method == "GET" ? QueryValue(query, "path") : ParsePathBody(body));

        if (method == "GET" && (path == "/ping" || path == "/"))
            return JsonSerializer.Serialize(new { ok = true, app = "MassiveSlicer", port = Port }, Json);

        if (method == "GET" && path == "/status")
            return await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var r = _main.RightPanel.Settings.Robot;
                return JsonSerializer.Serialize(new
                {
                    ok = true,
                    connected = r.IsConnected,
                    tool = r.KrlToolIndex,
                    @base = r.KrlBaseIndex,
                    pose = new { x = r.TcpX, y = r.TcpY, z = r.TcpZ, a = r.TcpA, b = r.TcpB, c = r.TcpC },
                }, Json);
            });

        if (method == "GET" && path == "/console")
        {
            int n = 50;
            foreach (string kv in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] kvp = kv.Split('=', 2);
                if (kvp.Length == 2 && kvp[0] == "n") int.TryParse(kvp[1], out n);
            }
            return await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var lines = _main.Console.SnapshotHistory(n)
                    .Select(h => new { text = h.DisplayLine, error = h.IsError, command = h.IsCommand })
                    .ToList();
                return JsonSerializer.Serialize(new { ok = true, count = lines.Count, lines }, Json);
            });
        }

        if (method == "GET" && path == "/screenshot")
        {
            bool rawPng = query.Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Any(p => p.Equals("format=png", StringComparison.OrdinalIgnoreCase));
            string saved = await Dispatcher.UIThread.InvokeAsync(async () => await _main.SaveViewportScreenshotAsync());
            if (saved.StartsWith("App screenshot", StringComparison.Ordinal))
                return JsonSerializer.Serialize(new { ok = false, error = saved }, Json);
            if (rawPng && File.Exists(saved))
                return "__raw_png__:" + saved;
            return JsonSerializer.Serialize(new
            {
                ok = true,
                path = saved,
                bytes = File.Exists(saved) ? new FileInfo(saved).Length : 0,
            }, Json);
        }

        if (method == "GET" && path == "/materials")
            return await Dispatcher.UIThread.InvokeAsync(() =>
                JsonSerializer.Serialize(PbrMaterialBridge.BuildStatus(_main), Json));

        if (method == "POST" && path == "/materials")
        {
            if (string.IsNullOrWhiteSpace(body))
                return JsonSerializer.Serialize(new { ok = false, error = "missing JSON body" }, Json);
            return await Dispatcher.UIThread.InvokeAsync(() =>
            {
                using var doc = JsonDocument.Parse(body);
                PbrMaterialBridge.ApplyPatch(_main, doc.RootElement);
                return JsonSerializer.Serialize(PbrMaterialBridge.BuildStatus(_main), Json);
            });
        }

        if (method == "POST" && path == "/command")
        {
            string cmd = ParseCommand(body);
            if (string.IsNullOrWhiteSpace(cmd))
                return JsonSerializer.Serialize(new { ok = false, error = "missing 'command'" }, Json);
            return await Dispatcher.UIThread.InvokeAsync(() =>
            {
                int before = _main.Console.History.Count;
                _main.Console.ExecuteLine(cmd);
                var output = _main.Console.History.Skip(before).Select(h => h.DisplayLine).ToList();
                return JsonSerializer.Serialize(new { ok = true, ran = cmd, output }, Json);
            });
        }

        return JsonSerializer.Serialize(new { ok = false, error = $"unknown route {method} {path}" }, Json);
    }

    private static string ParseCommand(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("command", out var c))
                return c.GetString() ?? "";
        }
        catch { /* not JSON — treat the raw body as the command */ }
        return body.Trim();
    }

    private static string QueryValue(string query, string key)
    {
        foreach (string kv in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] p = kv.Split('=', 2);
            if (p.Length == 2 && p[0].Equals(key, StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(p[1].Replace('+', ' '));
        }
        return "";
    }

    private static string ParsePathBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("path", out var p))
                    return p.GetString() ?? "";
                if (doc.RootElement.TryGetProperty("folder", out var f))
                    return f.GetString() ?? "";
            }
        }
        catch { /* raw path */ }
        return body.Trim().Trim('"');
    }

    private static string RevealPath(string raw)
    {
        string requested = (raw ?? "").Trim();
        if (requested.Length == 0)
            return JsonSerializer.Serialize(new { ok = false, error = "path required" });

        string local = ToLocalOsPath(requested);
        if (!AllowedRevealPath(local) && !AllowedRevealPath(requested))
            return JsonSerializer.Serialize(new { ok = false, error = "path not on MassiveFILES" });

        string target = local;
        try
        {
            if (File.Exists(target))
                target = Path.GetDirectoryName(target) ?? target;
            else if (!Directory.Exists(target))
            {
                string parent = Path.GetDirectoryName(target) ?? "";
                if (Directory.Exists(parent)) target = parent;
            }
        }
        catch { /* use as-is */ }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "\"" + target + "\"",
                    UseShellExecute = true,
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "open",
                    Arguments = target,
                    UseShellExecute = false,
                });
            }
            return JsonSerializer.Serialize(new { ok = true, folder = target, path = local });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { ok = false, error = ex.Message, folder = target });
        }
    }

    private async Task<string> OpenWorkspacePathAsync(string raw)
    {
        string requested = MassiveSlicer.Core.IO.ProtocolUri.ResolveWorkspacePath(raw) ?? (raw ?? "").Trim().Trim('"');
        if (requested.Length == 0)
            return JsonSerializer.Serialize(new { ok = false, error = "path required" });
        if (!requested.EndsWith(".mass", StringComparison.OrdinalIgnoreCase))
            return JsonSerializer.Serialize(new { ok = false, error = "not a .mass file" });

        string local = ToLocalOsPath(requested);
        if (!AllowedRevealPath(local) && !AllowedRevealPath(requested))
            return JsonSerializer.Serialize(new { ok = false, error = "path not on MassiveFILES" });

        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => _main.OpenWorkspace(local));
            return JsonSerializer.Serialize(new { ok = true, opened = local });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { ok = false, error = ex.Message, path = local });
        }
    }

    private static string ToLocalOsPath(string p)
    {
        string n = p.Replace('/', Path.DirectorySeparatorChar);
        if (OperatingSystem.IsWindows())
        {
            const string macRoot = "/Volumes/MassiveFILES/";
            if (p.StartsWith(macRoot, StringComparison.OrdinalIgnoreCase))
                return "Z:\\" + p[macRoot.Length..].Replace('/', '\\');
            if (p.StartsWith("/Volumes/MassiveFILES", StringComparison.OrdinalIgnoreCase))
                return "Z:\\";
        }
        return n;
    }

    private static bool AllowedRevealPath(string p)
    {
        if (string.IsNullOrWhiteSpace(p)) return false;
        string n = p.Replace('\\', '/');
        if (n.Contains("/MassiveFILES/", StringComparison.OrdinalIgnoreCase)) return true;
        if (n.StartsWith("/Volumes/MassiveFILES", StringComparison.OrdinalIgnoreCase)) return true;
        if (n.Length >= 3 && (n[0] is 'Z' or 'z') && n[1] == ':' &&
            (n.StartsWith("Z:/Projects/", StringComparison.OrdinalIgnoreCase)
             || n.StartsWith("Z:/Research/", StringComparison.OrdinalIgnoreCase)
             || n.Equals("Z:/Projects", StringComparison.OrdinalIgnoreCase)
             || n.Equals("Z:/Research", StringComparison.OrdinalIgnoreCase)))
            return true;
        return false;
    }

    private static async Task WriteBinaryAsync(NetworkStream s, int code, string contentType, byte[] body, CancellationToken ct)
    {
        string reason = code == 200 ? "OK" : "Error";
        string header = $"HTTP/1.1 {code} {reason}\r\n" +
                        $"Content-Type: {contentType}\r\n" +
                        $"Content-Length: {body.Length}\r\n" +
                        "Access-Control-Allow-Origin: *\r\n" +
                        "Connection: close\r\n\r\n";
        await s.WriteAsync(Encoding.ASCII.GetBytes(header), ct);
        await s.WriteAsync(body, ct);
        await s.FlushAsync(ct);
    }

    private static async Task WriteAsync(NetworkStream s, int code, string json, CancellationToken ct)
    {
        byte[] bodyBytes = Encoding.UTF8.GetBytes(json);
        string reason = code == 200 ? "OK" : code == 500 ? "Internal Server Error" : "Error";
        string header = $"HTTP/1.1 {code} {reason}\r\n" +
                        "Content-Type: application/json\r\n" +
                        $"Content-Length: {bodyBytes.Length}\r\n" +
                        "Access-Control-Allow-Origin: *\r\n" +
                        "Connection: close\r\n\r\n";
        await s.WriteAsync(Encoding.ASCII.GetBytes(header), ct);
        await s.WriteAsync(bodyBytes, ct);
        await s.FlushAsync(ct);
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { /* ignore */ }
        try { _listener?.Stop(); } catch { /* ignore */ }
        _cts.Dispose();
    }
}
