using MassiveSlicer.App.Console;

namespace MassiveSlicer.Tests;

/// <summary>A web page must not be able to drive the localhost control bridge.</summary>
public sealed class BridgeRequestGuardTest
{
    static Dictionary<string, string> H(params (string, string)[] kv)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in kv) d[k] = v;
        return d;
    }

    [Fact]
    public void Tooling_without_browser_headers_can_run_commands()
    {
        // curl / Python / MCP shim / PowerShell.
        Assert.Null(BridgeRequestGuard.Check("POST", "/command", H(("Host", "127.0.0.1:8723"))));
        Assert.Null(BridgeRequestGuard.Check("GET", "/status", H(("Host", "localhost:8724"))));
        Assert.Null(BridgeRequestGuard.Check("GET", "/screenshot", H()));
    }

    [Theory]
    [InlineData("POST", "/command")]
    [InlineData("POST", "/materials")]
    [InlineData("GET", "/status")]
    [InlineData("GET", "/console")]
    [InlineData("GET", "/screenshot")]
    public void Browser_cannot_reach_control_routes(string method, string path)
    {
        var fromPage = H(("Host", "127.0.0.1:8723"), ("Origin", "https://evil.example"));
        Assert.NotNull(BridgeRequestGuard.Check(method, path, fromPage));

        var nullOrigin = H(("Host", "127.0.0.1:8723"), ("Origin", "null"));
        Assert.NotNull(BridgeRequestGuard.Check(method, path, nullOrigin));
    }

    [Fact]
    public void Img_tag_get_without_origin_is_caught_by_fetch_metadata()
    {
        var img = H(("Host", "127.0.0.1:8723"), ("Sec-Fetch-Site", "cross-site"));
        Assert.NotNull(BridgeRequestGuard.Check("GET", "/screenshot", img));

        // Typing the URL into the address bar is the user, not a page.
        var typed = H(("Host", "127.0.0.1:8723"), ("Sec-Fetch-Site", "none"));
        Assert.Null(BridgeRequestGuard.Check("GET", "/status", typed));
    }

    [Theory]
    [InlineData("/ping")]
    [InlineData("/reveal")]
    [InlineData("/open")]
    public void Board_graph_routes_still_work_from_a_browser(string path)
    {
        var graph = H(("Host", "127.0.0.1:8723"), ("Origin", "http://192.168.0.10:3000"));
        Assert.Null(BridgeRequestGuard.Check("GET", path, graph));
        Assert.Null(BridgeRequestGuard.Check("POST", path, graph));
    }

    [Theory]
    [InlineData("attacker.example:8723")]
    [InlineData("localhost.attacker.example")]
    [InlineData("127.0.0.1.nip.io:8723")]
    public void Dns_rebinding_host_is_refused(string host)
    {
        Assert.NotNull(BridgeRequestGuard.Check("GET", "/ping", H(("Host", host))));
    }

    [Theory]
    [InlineData("127.0.0.1:8723")]
    [InlineData("localhost")]
    [InlineData("[::1]:8723")]
    [InlineData("192.168.0.69:8723")] // Hermes over the shop LAN
    public void Loopback_and_ip_hosts_are_trusted(string host)
    {
        Assert.True(BridgeRequestGuard.IsTrustedHost(host));
    }
}
