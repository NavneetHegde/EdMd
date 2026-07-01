using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using EdMd;
using Xunit;

namespace EdMd.Tests;

public class LocalWebServerTests : IDisposable
{
    private readonly string _baseDir;
    private readonly string _root;
    private readonly LocalWebServer _server;

    public LocalWebServerTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), "EdMdSrv-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_baseDir, "wwwroot");
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "index.html"), "<html><body>INDEX_MARKER</body></html>");
        File.WriteAllText(Path.Combine(_root, "app.css"), "body{color:red}");
        // A secret OUTSIDE the served root, for the traversal test.
        File.WriteAllText(Path.Combine(_baseDir, "secret.txt"), "TOP_SECRET");

        _server = new LocalWebServer(_root);
        _server.Start();
    }

    public void Dispose()
    {
        _server.Dispose();
        try { Directory.Delete(_baseDir, true); } catch { /* best effort */ }
    }

    [Fact]
    public void Serves_index_html_at_root()
    {
        var (status, contentType, body) = Request("GET", "/");
        Assert.Equal(200, status);
        Assert.Contains("text/html", contentType);
        Assert.Contains("INDEX_MARKER", body);
    }

    [Fact]
    public void Serves_css_with_correct_mime()
    {
        var (status, contentType, _) = Request("GET", "/app.css");
        Assert.Equal(200, status);
        Assert.Contains("text/css", contentType);
    }

    [Fact]
    public void Unknown_file_returns_404()
    {
        var (status, _, _) = Request("GET", "/does-not-exist.txt");
        Assert.Equal(404, status);
    }

    [Fact]
    public void Non_get_returns_405()
    {
        var (status, _, _) = Request("POST", "/index.html");
        Assert.Equal(405, status);
    }

    [Fact]
    public void Path_traversal_is_blocked()
    {
        // %2f is an encoded '/', so the server decodes "/../secret.txt" but must not escape root.
        var (status, _, body) = Request("GET", "/..%2fsecret.txt");
        Assert.Equal(404, status);
        Assert.DoesNotContain("TOP_SECRET", body);
    }

    [Fact]
    public void Session_requires_the_matching_token()
    {
        _server.SessionName = "note.md";
        _server.SessionContent = "SESSION_BODY";
        _server.SessionToken = "s3cr3t";

        Assert.Equal(403, Request("GET", "/__session").Status);
        Assert.Equal(403, Request("GET", "/__session?token=wrong").Status);

        var ok = Request("GET", "/__session?token=s3cr3t");
        Assert.Equal(200, ok.Status);
        Assert.Contains("SESSION_BODY", ok.Body);
        Assert.Contains("application/json", ok.ContentType);
    }

    [Fact]
    public void Session_is_forbidden_when_no_token_is_set()
    {
        // Fresh server has an empty SessionToken; even an empty ?token= must not unlock it.
        Assert.Equal(403, Request("GET", "/__session?token=").Status);
    }

    [Fact]
    public void Foreign_host_header_is_rejected()
    {
        // DNS-rebinding shape: a request that reached us but names an external host.
        Assert.Equal(403, Request("GET", "/", host: "evil.example.com").Status);
    }

    [Fact]
    public void Missing_host_header_is_rejected()
    {
        Assert.Equal(403, Request("GET", "/", host: null).Status);
    }

    [Fact]
    public void Loopback_hosts_with_a_port_are_accepted()
    {
        Assert.Equal(200, Request("GET", "/", host: "localhost:12345").Status);
        Assert.Equal(200, Request("GET", "/", host: "127.0.0.1:6789").Status);
    }

    // --- helper: raw HTTP over the loopback socket, so we control the exact request line
    // (HttpClient would canonicalize %2f/.. away and defeat the traversal test). ---
    private (int Status, string ContentType, string Body) Request(string method, string target, string? host = "localhost")
    {
        using var client = new TcpClient();
        client.Connect(IPAddress.Loopback, _server.Port);
        using var stream = client.GetStream();

        string hostHeader = host == null ? "" : $"Host: {host}\r\n";
        byte[] req = Encoding.ASCII.GetBytes($"{method} {target} HTTP/1.1\r\n{hostHeader}Connection: close\r\n\r\n");
        stream.Write(req, 0, req.Length);

        using var ms = new MemoryStream();
        stream.CopyTo(ms); // server sends Connection: close, so this returns at end of response
        string resp = Encoding.UTF8.GetString(ms.ToArray());

        int headerEnd = resp.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        string head = headerEnd >= 0 ? resp[..headerEnd] : resp;
        string body = headerEnd >= 0 ? resp[(headerEnd + 4)..] : "";

        string[] lines = head.Split("\r\n");
        int status = int.Parse(lines[0].Split(' ')[1]); // "HTTP/1.1 <status> <reason>"
        string contentType = "";
        foreach (var line in lines)
            if (line.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase))
                contentType = line["Content-Type:".Length..].Trim();

        return (status, contentType, body);
    }
}
