using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace VoidCode.Services;

public sealed class HttpRunRequest
{
    [JsonPropertyName("method")]
    public string Method { get; set; } = "GET";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("headers")]
    public Dictionary<string, string>? Headers { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("timeoutMs")]
    public int TimeoutMs { get; set; } = 15000;
}

public sealed class HttpRunResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("statusCode")]
    public int StatusCode { get; set; }

    [JsonPropertyName("statusText")]
    public string StatusText { get; set; } = "";

    [JsonPropertyName("elapsedMs")]
    public long ElapsedMs { get; set; }

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; }

    [JsonPropertyName("contentType")]
    public string ContentType { get; set; } = "";

    [JsonPropertyName("headers")]
    public Dictionary<string, string> Headers { get; set; } = new();

    [JsonPropertyName("body")]
    public string Body { get; set; } = "";

    [JsonPropertyName("truncated")]
    public bool Truncated { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

/// <summary>
/// Built-in API / HTTP Client runner + .http file parser.
/// </summary>
public static class HttpClientRunnerService
{
    private const int MaxBodyChars = 200_000;
    private static readonly HttpClient SharedClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        AllowAutoRedirect = true
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    public static async Task<HttpRunResponse> SendAsync(HttpRunRequest request, CancellationToken ct = default)
    {
        var url = (request.Url ?? "").Trim();
        if (string.IsNullOrWhiteSpace(url))
            return new HttpRunResponse { Ok = false, Error = "URL is required." };

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return new HttpRunResponse { Ok = false, Error = "URL must start with http:// or https://." };
        }

        var methodStr = string.IsNullOrWhiteSpace(request.Method) ? "GET" : request.Method.Trim().ToUpperInvariant();
        var method = new HttpMethod(methodStr);
        var timeoutMs = Math.Clamp(request.TimeoutMs <= 0 ? 15000 : request.TimeoutMs, 1000, 60000);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);

        var sw = Stopwatch.StartNew();
        try
        {
            using var msg = new HttpRequestMessage(method, uri);
            msg.Headers.UserAgent.ParseAdd("VoidCode-API-Client/1.1");

            string? explicitContentType = null;
            if (request.Headers != null)
            {
                foreach (var kv in request.Headers)
                {
                    if (string.IsNullOrWhiteSpace(kv.Key)) continue;
                    var key = kv.Key.Trim();
                    var val = kv.Value ?? "";
                    if (string.Equals(key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                    {
                        explicitContentType = val;
                        continue;
                    }
                    msg.Headers.TryAddWithoutValidation(key, val);
                }
            }

            if (!string.IsNullOrEmpty(request.Body) && method != HttpMethod.Get && method != HttpMethod.Head)
            {
                var mediaType = string.IsNullOrWhiteSpace(explicitContentType) ? "application/json" : explicitContentType.Split(';')[0].Trim();
                msg.Content = new StringContent(request.Body, Encoding.UTF8, mediaType);
            }

            using var resp = await SharedClient.SendAsync(msg, HttpCompletionOption.ResponseContentRead, cts.Token);
            var rawBytes = await resp.Content.ReadAsByteArrayAsync(cts.Token);
            sw.Stop();

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in resp.Headers)
                headers[h.Key] = string.Join(", ", h.Value);
            foreach (var h in resp.Content.Headers)
                headers[h.Key] = string.Join(", ", h.Value);

            var contentType = resp.Content.Headers.ContentType?.ToString() ?? "";
            var rawText = Encoding.UTF8.GetString(rawBytes);
            var truncated = rawText.Length > MaxBodyChars;
            var bodyText = truncated ? rawText[..MaxBodyChars] : rawText;

            if (!truncated && (contentType.Contains("json", StringComparison.OrdinalIgnoreCase) || LooksLikeJson(bodyText)))
            {
                bodyText = TryPrettyJson(bodyText);
            }

            return new HttpRunResponse
            {
                Ok = true,
                StatusCode = (int)resp.StatusCode,
                StatusText = resp.ReasonPhrase ?? resp.StatusCode.ToString(),
                ElapsedMs = sw.ElapsedMilliseconds,
                SizeBytes = rawBytes.LongLength,
                ContentType = contentType,
                Headers = headers,
                Body = bodyText,
                Truncated = truncated
            };
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return new HttpRunResponse { Ok = false, ElapsedMs = sw.ElapsedMilliseconds, Error = $"Request timed out after {timeoutMs}ms." };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new HttpRunResponse { Ok = false, ElapsedMs = sw.ElapsedMilliseconds, Error = ex.Message };
        }
    }

    /// <summary>
    /// Parses the first request block from standard .http / .rest syntax (METHOD URL, headers, blank line, body).
    /// </summary>
    public static HttpRunRequest? ParseDotHttp(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var req = new HttpRunRequest { Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) };
        bool foundStart = false;
        bool inBody = false;
        var bodyLines = new List<string>();

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            if (!foundStart)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#') || trimmed.StartsWith("//", StringComparison.Ordinal))
                    continue;
                var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    req.Method = parts[0].ToUpperInvariant();
                    req.Url = parts[1];
                    foundStart = true;
                }
                continue;
            }

            if (line.Trim().StartsWith("###", StringComparison.Ordinal))
                break;

            if (!inBody)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    inBody = true;
                    continue;
                }
                var colon = line.IndexOf(':');
                if (colon > 0)
                {
                    var key = line[..colon].Trim();
                    var val = line[(colon + 1)..].Trim();
                    req.Headers![key] = val;
                }
            }
            else
            {
                bodyLines.Add(rawLine);
            }
        }

        if (!foundStart) return null;
        if (bodyLines.Count > 0) req.Body = string.Join("\n", bodyLines).Trim();
        return req;
    }

    private static bool LooksLikeJson(string text)
    {
        var t = text.TrimStart();
        return t.StartsWith('{') || t.StartsWith('[');
    }

    private static string TryPrettyJson(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            // Keep original payload if response isn't strictly valid JSON.
            return text;
        }
    }
}
