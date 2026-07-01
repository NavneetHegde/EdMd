using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace EdMd;

// A tiny loopback-only HTTP server used by the desktop app's "Open in Browser" action.
// The browser build of the editor needs a *secure context* for the File System Access
// API, and http://localhost qualifies. We serve wwwroot over 127.0.0.1 (a raw
// TcpListener — no admin / URL-ACL reservation, unlike HttpListener) plus one extra
// route, /__session, that hands the currently-open document to the browser editor.
public sealed class LocalWebServer : IDisposable
{
    private readonly string _root;
    private readonly TcpListener _listener;
    private CancellationTokenSource? _cts;

    public int Port { get; }

    // The document handed off on the last "Open in Browser". Read by the /__session route.
    public volatile string SessionName = "untitled.md";
    public volatile string SessionContent = "";
    public volatile string SessionPath = "";
    // A per-launch nonce: /__session only answers a request carrying the matching token,
    // so another local process/tab can't read the in-memory document off the loopback port.
    public volatile string SessionToken = "";

    private static readonly Dictionary<string, string> Mime = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".mjs"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".map"] = "application/json; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".ico"] = "image/x-icon",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
        [".ttf"] = "font/ttf",
        [".md"] = "text/markdown; charset=utf-8",
    };

    public LocalWebServer(string root)
    {
        _root = Path.GetFullPath(root);
        // Port 0 => the OS picks a free ephemeral port, so we never clash with anything.
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _ = AcceptLoopAsync(_cts.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct); }
            catch { break; } // listener stopped / cancelled
            _ = HandleClientAsync(client);
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        using (var stream = client.GetStream())
        {
            try
            {
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                string? requestLine = await reader.ReadLineAsync();
                if (string.IsNullOrEmpty(requestLine)) return;

                var parts = requestLine.Split(' ');
                if (parts.Length < 2 || parts[0] != "GET")
                {
                    await WriteAsync(stream, 405, "Method Not Allowed", "text/plain", Encoding.ASCII.GetBytes("405"));
                    return;
                }

                // Read headers to find Host. We bind to 127.0.0.1, so only local clients can
                // connect — but a website the user visits could still point its own hostname at
                // 127.0.0.1 (DNS rebinding) and script requests to us. Requiring a localhost Host
                // header rejects those: a rebound request carries the attacker's hostname. (The
                // count cap bounds a client that never sends the terminating blank line.)
                string? host = null;
                for (int i = 0; i < 100; i++)
                {
                    string? header = await reader.ReadLineAsync();
                    if (string.IsNullOrEmpty(header)) break; // end of headers
                    if (host == null && header.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
                        host = header["Host:".Length..].Trim();
                }
                if (!IsLocalHost(host))
                {
                    await WriteAsync(stream, 403, "Forbidden", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("403 Forbidden"));
                    return;
                }

                string rawTarget = parts[1];
                int q = rawTarget.IndexOf('?');
                string query = q >= 0 ? rawTarget[(q + 1)..] : "";
                string path = Uri.UnescapeDataString(q >= 0 ? rawTarget[..q] : rawTarget);

                // Hand-off route: the current document, for the browser editor to load.
                // Requires the per-launch token so nothing else on loopback can read it.
                if (path == "/__session")
                {
                    string token = ParseQueryValue(query, "token");
                    if (string.IsNullOrEmpty(SessionToken) || token != SessionToken)
                    {
                        await WriteAsync(stream, 403, "Forbidden", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("403 Forbidden"));
                        return;
                    }
                    string json = JsonSerializer.Serialize(new { name = SessionName, content = SessionContent, path = SessionPath });
                    await WriteAsync(stream, 200, "OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
                    return;
                }

                if (path == "/" || path.Length == 0) path = "/index.html";
                string rel = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                string full = Path.GetFullPath(Path.Combine(_root, rel));

                // Stay inside wwwroot (block ../ traversal) and only serve existing files.
                // Compare against root + separator so a sibling like "wwwroot-x" can't match.
                string rootPrefix = _root.EndsWith(Path.DirectorySeparatorChar) ? _root : _root + Path.DirectorySeparatorChar;
                if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
                {
                    await WriteAsync(stream, 404, "Not Found", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("404 Not Found"));
                    return;
                }

                string ext = Path.GetExtension(full);
                string contentType = Mime.TryGetValue(ext, out var m) ? m : "application/octet-stream";
                byte[] body = await File.ReadAllBytesAsync(full);
                await WriteAsync(stream, 200, "OK", contentType, body);
            }
            catch { /* never let a bad request take down the editor */ }
        }
    }

    // Accept only Host values that name the loopback interface (optionally with a port),
    // so a DNS-rebound request carrying an external hostname is refused.
    private static bool IsLocalHost(string? host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        string h = host;
        if (h.StartsWith('[')) // IPv6 literal, e.g. [::1]:port
        {
            int end = h.IndexOf(']');
            h = end > 0 ? h[1..end] : h;
        }
        else
        {
            int colon = h.IndexOf(':');
            if (colon >= 0) h = h[..colon];
        }
        return h.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || h == "127.0.0.1"
            || h == "::1";
    }

    private static string ParseQueryValue(string query, string key)
    {
        foreach (var pair in query.Split('&'))
        {
            int eq = pair.IndexOf('=');
            if (eq > 0 && Uri.UnescapeDataString(pair[..eq]) == key)
                return Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return "";
    }

    private static async Task WriteAsync(NetworkStream stream, int code, string status, string contentType, byte[] body)
    {
        string head = $"HTTP/1.1 {code} {status}\r\n" +
                      $"Content-Type: {contentType}\r\n" +
                      $"Content-Length: {body.Length}\r\n" +
                      "Cache-Control: no-store\r\n" +
                      "Connection: close\r\n\r\n";
        byte[] headBytes = Encoding.ASCII.GetBytes(head);
        await stream.WriteAsync(headBytes);
        if (body.Length > 0) await stream.WriteAsync(body);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _listener.Stop(); } catch { }
    }
}
