using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using VoidCode.Agent.Tools;
using VoidCode.Services;

namespace VoidCode.Agent.Mcp;

/// <summary>
/// Lightweight JSON-RPC 2.0 stdio Model Context Protocol (MCP) client.
/// Supports initialize, tools/list, and tools/call over newline-delimited JSON stdio streams,
/// With stream injection for deterministic unit testing and guaranteed child-process teardown.
/// </summary>
public sealed class McpToolInfo
{
    [JsonPropertyName("server")]
    public string Server { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("qualifiedName")]
    public string QualifiedName { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("inputSchema")]
    public JsonObject InputSchema { get; set; } = new() { ["type"] = "object", ["properties"] = new JsonObject() };
}

public sealed class McpServerConfig
{
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public List<string> Args { get; set; } = new();
    public Dictionary<string, string> Env { get; set; } = new();
    public string Url { get; set; } = "";
    public Dictionary<string, string> Headers { get; set; } = new();
    public bool Disabled { get; set; }
    public bool IsHttp => !string.IsNullOrWhiteSpace(Url);
}

public sealed class McpClient : IDisposable
{
    private readonly Process? _process;
    private readonly StreamWriter? _writer;
    private readonly StreamReader? _reader;
    private readonly string? _httpEndpoint;
    private readonly Dictionary<string, string>? _httpHeaders;
    private readonly HttpClient? _httpClient;
    private string? _mcpSessionId;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private int _nextId;
    private bool _disposed;

    public string ServerName { get; }
    public bool IsConnected => !_disposed && (_httpEndpoint != null || _process == null || !_process.HasExited);

    public McpClient(string serverName, Stream serverStdout, Stream serverStdin)
    {
        ServerName = serverName;
        _reader = new StreamReader(serverStdout, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        _writer = new StreamWriter(serverStdin, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        _ = Task.Run(ReadLoopAsync);
    }

    /// <summary>Streamable HTTP / SSE MCP client constructor (JSON-RPC 2.0 over HTTP POST + SSE response parsing).</summary>
    public McpClient(string serverName, string endpointUrl, Dictionary<string, string>? headers = null, HttpClient? http = null)
    {
        ServerName = serverName;
        _httpEndpoint = endpointUrl;
        _httpHeaders = headers;
        _httpClient = http ?? AgentHttp.Client;
    }

    private McpClient(string serverName, Process process)
    {
        ServerName = serverName;
        _process = process;
        _reader = process.StandardOutput;
        _writer = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false), 4096, leaveOpen: false) { AutoFlush = true };
        _ = Task.Run(ReadLoopAsync);
        _ = Task.Run(DrainStderrAsync);
    }

    public static McpClient? Connect(McpServerConfig config, string workspacePath)
    {
        if (config.IsHttp)
        {
            if (!Uri.TryCreate(config.Url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                LoggingService.LogWarning($"[MCP:{config.Name}] Rejected non-HTTP URL '{config.Url}'.", nameof(McpClient));
                return null;
            }
            return new McpClient(config.Name, config.Url, config.Headers);
        }
        return StartProcess(config, workspacePath);
    }

    public static McpClient? StartProcess(McpServerConfig config, string workspacePath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = config.Command,
            WorkingDirectory = Directory.Exists(workspacePath) ? workspacePath : Environment.CurrentDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in config.Args) psi.ArgumentList.Add(arg);
        psi.Environment["PATH"] = LspInstallService.BuildEffectivePath();
        foreach (var kvp in config.Env) psi.Environment[kvp.Key] = kvp.Value;

        var proc = new Process { StartInfo = psi };
        if (!proc.Start())
        {
            proc.Dispose();
            return null;
        }
        return new McpClient(config.Name, proc);
    }

    public async Task<bool> InitializeAsync(CancellationToken cancellationToken = default)
    {
        var resp = await SendRequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = "2024-11-05",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject
            {
                ["name"] = "VoidCode",
                ["version"] = "0.2.0"
            }
        }, 10000, cancellationToken);

        if (resp == null || resp["error"] != null) return false;

        await SendNotificationAsync("notifications/initialized", new JsonObject(), cancellationToken);
        return true;
    }

    public async Task<List<McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        var list = new List<McpToolInfo>();
        var resp = await SendRequestAsync("tools/list", new JsonObject(), 10000, cancellationToken);
        var toolsArr = resp?["result"]?["tools"]?.AsArray();
        if (toolsArr == null) return list;

        foreach (var node in toolsArr)
        {
            var name = node?["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name)) continue;

            var schema = node?["inputSchema"] is JsonObject s
                ? JsonNode.Parse(s.ToJsonString())!.AsObject()
                : new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };

            list.Add(new McpToolInfo
            {
                Server = ServerName,
                Name = name,
                QualifiedName = BuildQualifiedName(ServerName, name),
                Description = node?["description"]?.GetValue<string>() ?? $"MCP tool {name} from {ServerName}",
                InputSchema = schema
            });
        }
        return list;
    }

    public async Task<(bool success, string output)> CallToolAsync(
        string toolName,
        JsonObject? arguments,
        CancellationToken cancellationToken = default)
    {
        var resp = await SendRequestAsync("tools/call", new JsonObject
        {
            ["name"] = toolName,
            ["arguments"] = arguments != null ? JsonNode.Parse(arguments.ToJsonString()) : new JsonObject()
        }, 30000, cancellationToken);

        if (resp == null)
            return (false, $"MCP server '{ServerName}' did not respond to '{toolName}'.");

        if (resp["error"] is JsonObject err)
        {
            return (false, $"MCP error ({err["code"]}): {err["message"]?.GetValue<string>() ?? err.ToJsonString()}");
        }

        var result = resp["result"];
        var isError = result?["isError"]?.GetValue<bool>() == true;
        var sb = new StringBuilder();
        if (result?["content"] is JsonArray contentArr)
        {
            foreach (var part in contentArr)
            {
                var text = part?["text"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(text))
                {
                    if (sb.Length > 0) sb.AppendLine();
                    sb.Append(text);
                }
            }
        }
        else if (result != null)
        {
            sb.Append(result.ToJsonString());
        }

        var output = ToolOutputPolicy.Truncate(sb.ToString(), ToolOutputPolicy.CommandMaxChars, "mcp output truncated");
        return (!isError, ToolOutputPolicy.WrapUntrusted($"mcp:{ServerName}/{toolName}", output));
    }

    internal static string BuildQualifiedName(string server, string tool)
    {
        var s = SanitizeIdentifier(server);
        var t = SanitizeIdentifier(tool);
        return $"mcp__{s}__{t}";
    }

    private static string SanitizeIdentifier(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            sb.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
        }
        return sb.Length == 0 ? "tool" : sb.ToString();
    }

    private async Task<JsonObject?> SendRequestAsync(
        string method,
        JsonObject parameters,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        if (!IsConnected) return null;
        int id = Interlocked.Increment(ref _nextId);
        var req = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters
        };

        if (_httpEndpoint != null)
        {
            return await SendHttpRpcAsync(req, timeoutMs, cancellationToken);
        }

        if (_writer == null) return null;
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        try
        {
            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                await _writer.WriteLineAsync(req.ToJsonString().AsMemory(), cancellationToken);
                await _writer.FlushAsync(cancellationToken);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch (Exception ex)
        {
            _pending.TryRemove(id, out _);
            LoggingService.Swallowed(ex, $"McpClient.SendRequest({ServerName}.{method})");
            return null;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        linked.CancelAfter(timeoutMs);
        using var reg = linked.Token.Register(() =>
        {
            if (_pending.TryRemove(id, out var removed)) removed.TrySetCanceled();
        });

        try
        {
            return await tcs.Task;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<JsonObject?> SendHttpRpcAsync(JsonObject rpcBody, int timeoutMs, CancellationToken ct)
    {
        if (_httpEndpoint == null || _httpClient == null) return null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        linked.CancelAfter(timeoutMs);
        try
        {
            using var msg = new HttpRequestMessage(HttpMethod.Post, _httpEndpoint)
            {
                Content = new StringContent(rpcBody.ToJsonString(), Encoding.UTF8, "application/json")
            };
            msg.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
            if (!string.IsNullOrEmpty(_mcpSessionId))
            {
                msg.Headers.TryAddWithoutValidation("Mcp-Session-Id", _mcpSessionId);
            }
            if (_httpHeaders != null)
            {
                foreach (var kv in _httpHeaders)
                {
                    var expanded = Regex.Replace(kv.Value ?? "", @"\$\{([A-Za-z0-9_]+)\}", m =>
                        Environment.GetEnvironmentVariable(m.Groups[1].Value) ?? "");
                    if (!string.IsNullOrWhiteSpace(expanded))
                    {
                        msg.Headers.TryAddWithoutValidation(kv.Key, expanded);
                    }
                }
            }

            using var resp = await _httpClient.SendAsync(msg, linked.Token);
            if (resp.Headers.TryGetValues("Mcp-Session-Id", out var sessionVals))
            {
                _mcpSessionId = sessionVals.FirstOrDefault() ?? _mcpSessionId;
            }
            if (!resp.IsSuccessStatusCode) return null;

            var bodyText = await resp.Content.ReadAsStringAsync(linked.Token);
            if (string.IsNullOrWhiteSpace(bodyText)) return null;

            // Support both direct JSON-RPC response and text/event-stream (SSE) frames.
            var trimmed = bodyText.TrimStart();
            if (trimmed.StartsWith("{", StringComparison.Ordinal))
            {
                return JsonNode.Parse(trimmed) as JsonObject;
            }

            JsonObject? lastObj = null;
            foreach (var rawLine in bodyText.Split('\n'))
            {
                var line = rawLine.Trim();
                if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
                var data = line.Substring(5).Trim();
                if (string.IsNullOrEmpty(data) || data == "[DONE]") continue;
                try
                {
                    if (JsonNode.Parse(data) is JsonObject obj) lastObj = obj;
                }
                catch (Exception) { /* skip non-JSON SSE event line */ }
            }
            return lastObj;
        }
        catch (Exception ex)
        {
            LoggingService.Swallowed(ex, $"McpClient.SendHttpRpc({ServerName})");
            return null;
        }
    }

    private async Task SendNotificationAsync(string method, JsonObject parameters, CancellationToken cancellationToken)
    {
        if (!IsConnected) return;
        var notif = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = parameters
        };
        if (_httpEndpoint != null)
        {
            await SendHttpRpcAsync(notif, 5000, cancellationToken);
            return;
        }
        if (_writer == null) return;
        try
        {
            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                await _writer.WriteLineAsync(notif.ToJsonString().AsMemory(), cancellationToken);
                await _writer.FlushAsync(cancellationToken);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch (Exception ex) { LoggingService.Swallowed(ex, $"McpClient.SendNotification({ServerName}.{method})"); }
    }

    private async Task ReadLoopAsync()
    {
        if (_reader == null) return;
        try
        {
            string? line;
            while (!_cts.Token.IsCancellationRequested && (line = await _reader.ReadLineAsync(_cts.Token)) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var node = JsonNode.Parse(line)?.AsObject();
                    if (node == null) continue;
                    if (node["id"] is JsonValue idVal && idVal.TryGetValue<int>(out var id))
                    {
                        if (_pending.TryRemove(id, out var tcs))
                        {
                            tcs.TrySetResult(node);
                        }
                    }
                }
                catch (Exception ex) { LoggingService.Swallowed(ex, $"McpClient.ParseLine({ServerName})"); }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex) { LoggingService.Swallowed(ex, $"McpClient.ReadLoop({ServerName})"); }
        finally
        {
            foreach (var kvp in _pending)
            {
                if (_pending.TryRemove(kvp.Key, out var tcs)) tcs.TrySetCanceled();
            }
        }
    }

    private async Task DrainStderrAsync()
    {
        if (_process == null) return;
        try
        {
            string? line;
            while (!_cts.Token.IsCancellationRequested && (line = await _process.StandardError.ReadLineAsync(_cts.Token)) != null)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    LoggingService.LogWarning($"[MCP:{ServerName}] {line}", nameof(McpClient));
                }
            }
        }
        catch (Exception ex) { LoggingService.Swallowed(ex, $"McpClient.Stderr({ServerName})"); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        if (_process != null)
        {
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
            catch (Exception ex) { LoggingService.Swallowed(ex, $"McpClient.Kill({ServerName})"); }
            _process.Dispose();
        }
        _cts.Dispose();
        _writeLock.Dispose();
    }
}

public static class McpManager
{
    private static readonly JsonDocumentOptions JsoncOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly object Gate = new();
    private static string? _activeWorkspace;
    private static readonly Dictionary<string, McpClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, McpToolInfo> _toolsByQualifiedName = new(StringComparer.OrdinalIgnoreCase);

    internal static Func<McpServerConfig, string, McpClient?>? ClientFactoryOverrideForTests { get; set; }

    public static bool IsMcpTool(string? toolName)
        => !string.IsNullOrEmpty(toolName) && toolName.StartsWith("mcp__", StringComparison.OrdinalIgnoreCase);

    public static List<McpServerConfig> LoadConfig(string workspacePath)
    {
        var configs = new List<McpServerConfig>();
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
            return configs;

        var file = Path.Combine(workspacePath, ".voidcode", "mcp.json");
        if (!File.Exists(file) || !PathPolicy.IsInsideWorkspace(workspacePath, file))
            return configs;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file, Encoding.UTF8), JsoncOptions);
            if (!doc.RootElement.TryGetProperty("mcpServers", out var servers) || servers.ValueKind != JsonValueKind.Object)
                return configs;

            foreach (var prop in servers.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                var cmd = prop.Value.TryGetProperty("command", out var c) ? c.GetString() : null;
                var url = prop.Value.TryGetProperty("url", out var u) ? u.GetString() : null;
                if (string.IsNullOrWhiteSpace(cmd) && string.IsNullOrWhiteSpace(url)) continue;

                var cfg = new McpServerConfig
                {
                    Name = prop.Name,
                    Command = string.IsNullOrWhiteSpace(cmd) ? "" : TaskRunnerService.SubstituteVariables(cmd, workspacePath),
                    Url = string.IsNullOrWhiteSpace(url) ? "" : url.Trim(),
                    Disabled = prop.Value.TryGetProperty("disabled", out var d) && d.ValueKind == JsonValueKind.True
                };

                if (prop.Value.TryGetProperty("args", out var argsEl) && argsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in argsEl.EnumerateArray())
                    {
                        if (a.ValueKind == JsonValueKind.String)
                            cfg.Args.Add(TaskRunnerService.SubstituteVariables(a.GetString() ?? "", workspacePath));
                    }
                }

                if (prop.Value.TryGetProperty("env", out var envEl) && envEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var envProp in envEl.EnumerateObject())
                    {
                        if (envProp.Value.ValueKind == JsonValueKind.String)
                            cfg.Env[envProp.Name] = envProp.Value.GetString() ?? "";
                    }
                }

                if (prop.Value.TryGetProperty("headers", out var hdrEl) && hdrEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var hdrProp in hdrEl.EnumerateObject())
                    {
                        if (hdrProp.Value.ValueKind == JsonValueKind.String)
                            cfg.Headers[hdrProp.Name] = hdrProp.Value.GetString() ?? "";
                    }
                }

                configs.Add(cfg);
            }
        }
        catch (Exception ex)
        {
            LoggingService.Swallowed(ex, "McpManager.LoadConfig");
        }

        return configs;
    }

    public static async Task<List<McpToolInfo>> EnsureWorkspaceToolsAsync(string workspacePath, CancellationToken cancellationToken = default)
    {
        // Never spawn workspace MCP servers in an untrusted folder.
        if (string.IsNullOrWhiteSpace(workspacePath) || !WorkspaceTrustService.IsTrusted(workspacePath))
        {
            return new List<McpToolInfo>();
        }

        var canonical = Path.GetFullPath(workspacePath);
        lock (Gate)
        {
            if (!string.Equals(_activeWorkspace, canonical, StringComparison.OrdinalIgnoreCase))
            {
                ShutdownAllLocked();
                _activeWorkspace = canonical;
            }
        }

        var configs = LoadConfig(canonical).Where(c => !c.Disabled).ToList();
        if (configs.Count == 0) return new List<McpToolInfo>();

        var discovered = new List<McpToolInfo>();
        foreach (var cfg in configs)
        {
            McpClient? client;
            bool newlyStarted = false;
            lock (Gate)
            {
                if (!_clients.TryGetValue(cfg.Name, out client) || !client.IsConnected)
                {
                    client?.Dispose();
                    client = ClientFactoryOverrideForTests != null
                        ? ClientFactoryOverrideForTests(cfg, canonical)
                        : McpClient.Connect(cfg, canonical);
                    if (client != null)
                    {
                        _clients[cfg.Name] = client;
                        newlyStarted = true;
                    }
                }
            }

            if (client == null) continue;
            if (newlyStarted)
            {
                var ok = await client.InitializeAsync(cancellationToken);
                if (!ok)
                {
                    lock (Gate)
                    {
                        client.Dispose();
                        _clients.Remove(cfg.Name);
                    }
                    continue;
                }
            }

            var tools = await client.ListToolsAsync(cancellationToken);
            lock (Gate)
            {
                foreach (var t in tools)
                {
                    _toolsByQualifiedName[t.QualifiedName] = t;
                    discovered.Add(t);
                }
            }
        }

        return discovered;
    }

    public static JsonArray ToOpenAiToolDefinitions(IEnumerable<McpToolInfo> tools)
    {
        var arr = new JsonArray();
        foreach (var t in tools)
        {
            arr.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = t.QualifiedName,
                    ["description"] = $"[MCP:{t.Server}] {t.Description}",
                    ["parameters"] = JsonNode.Parse(t.InputSchema.ToJsonString())
                }
            });
        }
        return arr;
    }

    public static async Task<string> ExecuteToolAsync(string qualifiedName, string argsJson, CancellationToken cancellationToken = default)
    {
        McpToolInfo? tool;
        McpClient? client;
        lock (Gate)
        {
            if (!_toolsByQualifiedName.TryGetValue(qualifiedName, out tool) ||
                !_clients.TryGetValue(tool.Server, out client) ||
                !client.IsConnected)
            {
                return $"Error: MCP tool '{qualifiedName}' is not available.";
            }
        }

        JsonObject? argsObj = null;
        try
        {
            argsObj = JsonNode.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson) as JsonObject;
        }
        catch (Exception ex)
        {
            LoggingService.Swallowed(ex, "McpManager.ParseArgs");
        }

        var (_, output) = await client.CallToolAsync(tool.Name, argsObj, cancellationToken);
        return output;
    }

    public static void ShutdownAll()
    {
        lock (Gate)
        {
            ShutdownAllLocked();
        }
    }

    private static void ShutdownAllLocked()
    {
        foreach (var c in _clients.Values) c.Dispose();
        _clients.Clear();
        _toolsByQualifiedName.Clear();
        _activeWorkspace = null;
    }
}
