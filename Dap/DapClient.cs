using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using VoidCode.Services;

namespace VoidCode.Dap;

/// <summary>
/// Reusable Debug Adapter Protocol (DAP) client over stdio.
/// Handles Content-Length framing, seq-correlated request/response dispatch, async event callbacks,
/// Capability tracking, and guaranteed child-process-tree teardown on dispose.
/// </summary>
public sealed class DapResponse
{
    public int RequestSeq { get; init; }
    public string Command { get; init; } = "";
    public bool Success { get; init; }
    public string? Message { get; init; }
    public JsonObject? Body { get; init; }
}

public sealed class DapClient : IDisposable
{
    private readonly Process? _process;
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<DapResponse>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private int _seq;
    private bool _disposed;

    public JsonObject Capabilities { get; private set; } = new();
    public bool IsRunning => !_disposed && (_process == null || !_process.HasExited);

    public event Action<string, JsonObject?>? EventReceived;
    public event Action<int>? Exited;

    /// <summary>Creates a DAP client attached to pre-opened streams (used by unit tests).</summary>
    public DapClient(Stream adapterStdout, Stream adapterStdin)
    {
        _output = adapterStdout ?? throw new ArgumentNullException(nameof(adapterStdout));
        _input = adapterStdin ?? throw new ArgumentNullException(nameof(adapterStdin));
        _ = Task.Run(ReadLoopAsync);
    }

    private DapClient(Process process)
    {
        _process = process;
        _input = process.StandardInput.BaseStream;
        _output = process.StandardOutput.BaseStream;
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            int code = 0;
            try { code = process.ExitCode; } catch (Exception ex) { LoggingService.Swallowed(ex, "DapClient.ExitCode"); }
            FailAllPending("Debug adapter process exited.");
            Exited?.Invoke(code);
        };
        _ = Task.Run(ReadLoopAsync);
        _ = Task.Run(DrainStderrAsync);
    }

    public static DapClient? StartProcess(string fileName, string arguments, string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = !string.IsNullOrEmpty(workingDirectory) && Directory.Exists(workingDirectory)
                ? workingDirectory
                : Environment.CurrentDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.Environment["PATH"] = LspInstallService.BuildEffectivePath();

        var proc = new Process { StartInfo = psi };
        if (!proc.Start())
        {
            proc.Dispose();
            return null;
        }
        return new DapClient(proc);
    }

    public async Task<DapResponse> InitializeAsync(string adapterId, CancellationToken cancellationToken = default)
    {
        var resp = await SendRequestAsync("initialize", new JsonObject
        {
            ["clientID"] = "voidcode",
            ["clientName"] = "VoidCode IDE",
            ["adapterID"] = adapterId,
            ["pathFormat"] = "path",
            ["linesStartAt1"] = true,
            ["columnsStartAt1"] = true,
            ["supportsVariableType"] = true,
            ["supportsVariablePaging"] = true,
            ["supportsRunInTerminalRequest"] = false
        }, 10000, cancellationToken);

        if (resp.Success && resp.Body != null)
        {
            Capabilities = JsonNode.Parse(resp.Body.ToJsonString())?.AsObject() ?? new JsonObject();
        }
        return resp;
    }

    public async Task<DapResponse> SendRequestAsync(
        string command,
        JsonObject? arguments = null,
        int timeoutMs = 10000,
        CancellationToken cancellationToken = default)
    {
        if (!IsRunning)
        {
            return new DapResponse { Command = command, Success = false, Message = "Debug adapter is not running." };
        }

        int seq = Interlocked.Increment(ref _seq);
        var payload = new JsonObject
        {
            ["seq"] = seq,
            ["type"] = "request",
            ["command"] = command
        };
        if (arguments != null) payload["arguments"] = arguments;

        var tcs = new TaskCompletionSource<DapResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[seq] = tcs;

        try
        {
            await WriteFramedJsonAsync(payload.ToJsonString(), cancellationToken);
        }
        catch (Exception ex)
        {
            _pending.TryRemove(seq, out _);
            return new DapResponse { RequestSeq = seq, Command = command, Success = false, Message = ex.Message };
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        linked.CancelAfter(timeoutMs);
        using var reg = linked.Token.Register(() =>
        {
            if (_pending.TryRemove(seq, out var removed))
            {
                removed.TrySetResult(new DapResponse
                {
                    RequestSeq = seq,
                    Command = command,
                    Success = false,
                    Message = $"DAP request '{command}' timed out after {timeoutMs}ms."
                });
            }
        });

        return await tcs.Task;
    }

    private async Task WriteFramedJsonAsync(string json, CancellationToken cancellationToken)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(json);
        var headerBytes = Encoding.ASCII.GetBytes($"Content-Length: {bodyBytes.Length}\r\n\r\n");

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _input.WriteAsync(headerBytes, cancellationToken);
            await _input.WriteAsync(bodyBytes, cancellationToken);
            await _input.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                var messageJson = await ReadFramedMessageAsync(_output, _cts.Token);
                if (messageJson == null) break;
                DispatchMessage(messageJson);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            LoggingService.Swallowed(ex, "DapClient.ReadLoopAsync");
        }
        finally
        {
            FailAllPending("Debug adapter stream closed.");
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
                    LoggingService.LogWarning($"[DAP stderr] {line}", nameof(DapClient));
                }
            }
        }
        catch (Exception ex) { LoggingService.Swallowed(ex, "DapClient.DrainStderrAsync"); }
    }

    internal static async Task<string?> ReadFramedMessageAsync(Stream stream, CancellationToken ct)
    {
        int contentLength = -1;
        var headerLine = new MemoryStream();
        var one = new byte[1];

        while (true)
        {
            headerLine.SetLength(0);
            int prev = -1;
            while (true)
            {
                int read = await stream.ReadAsync(one.AsMemory(0, 1), ct);
                if (read == 0) return null;
                byte b = one[0];
                if (prev == '\r' && b == '\n')
                {
                    // Trim trailing \r
                    var len = (int)headerLine.Length;
                    if (len > 0) headerLine.SetLength(len - 1);
                    break;
                }
                headerLine.WriteByte(b);
                prev = b;
            }

            if (headerLine.Length == 0)
            {
                if (contentLength >= 0) break;
                continue;
            }

            var text = Encoding.ASCII.GetString(headerLine.GetBuffer(), 0, (int)headerLine.Length);
            if (text.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                var val = text.Substring("Content-Length:".Length).Trim();
                int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out contentLength);
            }
        }

        if (contentLength <= 0) return string.Empty;
        var body = new byte[contentLength];
        int offset = 0;
        while (offset < contentLength)
        {
            int n = await stream.ReadAsync(body.AsMemory(offset, contentLength - offset), ct);
            if (n == 0) return null;
            offset += n;
        }
        return Encoding.UTF8.GetString(body);
    }

    private void DispatchMessage(string json)
    {
        try
        {
            var node = JsonNode.Parse(json)?.AsObject();
            if (node == null) return;

            var type = node["type"]?.GetValue<string>() ?? "";
            if (type == "response")
            {
                var reqSeq = node["request_seq"]?.GetValue<int>() ?? 0;
                if (_pending.TryRemove(reqSeq, out var tcs))
                {
                    tcs.TrySetResult(new DapResponse
                    {
                        RequestSeq = reqSeq,
                        Command = node["command"]?.GetValue<string>() ?? "",
                        Success = node["success"]?.GetValue<bool>() ?? false,
                        Message = node["message"]?.GetValue<string>(),
                        Body = node["body"] as JsonObject
                    });
                }
            }
            else if (type == "event")
            {
                var evtName = node["event"]?.GetValue<string>() ?? "";
                var body = node["body"] as JsonObject;
                if (!string.IsNullOrEmpty(evtName))
                {
                    EventReceived?.Invoke(evtName, body);
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Swallowed(ex, "DapClient.DispatchMessage");
        }
    }

    private void FailAllPending(string reason)
    {
        foreach (var kvp in _pending)
        {
            if (_pending.TryRemove(kvp.Key, out var tcs))
            {
                tcs.TrySetResult(new DapResponse
                {
                    RequestSeq = kvp.Key,
                    Success = false,
                    Message = reason
                });
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        FailAllPending("Debug adapter disposed.");

        if (_process != null)
        {
            try
            {
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) { LoggingService.Swallowed(ex, "DapClient.KillProcess"); }
            _process.Dispose();
        }
        _cts.Dispose();
        _writeLock.Dispose();
    }
}
