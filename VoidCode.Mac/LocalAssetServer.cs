using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VoidCode.Services;

namespace VoidCode.Mac;

/// <summary>
/// Lightweight in-process loopback HTTP static server (127.0.0.1:&lt;ephemeral-port&gt;)
/// serving <c>wwwroot/</c> and the active workspace preview (<c>/__workspace__/...</c>)
/// to WKWebView so ES modules, Monaco workers, and CSP report-only headers work natively.
/// </summary>
internal sealed class LocalAssetServer : IDisposable
{
    private static readonly Dictionary<string, string> MimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".htm"] = "text/html; charset=utf-8",
        [".js"] = "application/javascript; charset=utf-8",
        [".mjs"] = "application/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".ico"] = "image/x-icon",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
        [".ttf"] = "font/ttf",
        [".wasm"] = "application/wasm",
        [".map"] = "application/json; charset=utf-8",
        [".txt"] = "text/plain; charset=utf-8",
        [".md"] = "text/markdown; charset=utf-8"
    };

    private readonly string _wwwrootRoot;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private volatile string? _workspaceRoot;

    public int Port { get; }
    public Uri BaseUri { get; }
    public Uri DocumentUri { get; }

    public LocalAssetServer(string wwwrootRoot)
    {
        _wwwrootRoot = Path.GetFullPath(wwwrootRoot);
        Port = PickFreeLoopbackPort();
        BaseUri = new Uri($"http://127.0.0.1:{Port}/");
        DocumentUri = new Uri($"http://127.0.0.1:{Port}/index.html");
        _listener.Prefixes.Add(BaseUri.AbsoluteUri);
    }

    public void SetWorkspaceRoot(string? workspaceRoot)
    {
        _workspaceRoot = string.IsNullOrWhiteSpace(workspaceRoot) ? null : Path.GetFullPath(workspaceRoot);
    }

    public void Start()
    {
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    private static int PickFreeLoopbackPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.Token.IsCancellationRequested && _listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().WaitAsync(_cts.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                LoggingService.Swallowed(ex, "LocalAssetServer.Accept");
                continue;
            }

            _ = Task.Run(() => HandleRequestAsync(ctx));
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx)
    {
        try
        {
            var rawPath = Uri.UnescapeDataString(ctx.Request.Url?.AbsolutePath ?? "/");
            if (rawPath == "/" || string.IsNullOrEmpty(rawPath)) rawPath = "/index.html";

            string baseDir;
            string relative;
            bool isIndex = false;

            const string workspacePrefix = "/__workspace__/";
            if (rawPath.StartsWith(workspacePrefix, StringComparison.OrdinalIgnoreCase))
            {
                var ws = _workspaceRoot;
                if (string.IsNullOrEmpty(ws) || !Directory.Exists(ws))
                {
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                    return;
                }
                baseDir = ws;
                relative = rawPath.Substring(workspacePrefix.Length);
            }
            else
            {
                baseDir = _wwwrootRoot;
                relative = rawPath.TrimStart('/');
                isIndex = string.Equals(relative, "index.html", StringComparison.OrdinalIgnoreCase);
            }

            var candidate = Path.GetFullPath(Path.Combine(baseDir, relative.Replace('/', Path.DirectorySeparatorChar)));
            var normalizedBase = baseDir.EndsWith(Path.DirectorySeparatorChar) ? baseDir : baseDir + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(normalizedBase, StringComparison.Ordinal) || !File.Exists(candidate))
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }

            var ext = Path.GetExtension(candidate);
            ctx.Response.ContentType = MimeTypes.TryGetValue(ext, out var mime) ? mime : "application/octet-stream";
            ctx.Response.Headers["Cache-Control"] = "no-store";
            if (isIndex)
            {
                ctx.Response.Headers["Content-Security-Policy-Report-Only"] = CspPolicy.ReportOnlyValue;
            }

            var bytes = await File.ReadAllBytesAsync(candidate);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
        catch (Exception ex)
        {
            LoggingService.Swallowed(ex, "LocalAssetServer.HandleRequest");
            try { ctx.Response.Abort(); } catch (Exception) { /* already closed */ }
        }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch (Exception) { /* already cancelled */ }
        try { _listener.Stop(); } catch (Exception) { /* already stopped */ }
        try { _listener.Close(); } catch (Exception) { /* already closed */ }
        _cts.Dispose();
    }
}
