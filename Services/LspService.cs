using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace VoidCode.Services;

public class LspDiagnosticItem
{
    public int StartLine { get; set; }
    public int StartCol { get; set; }
    public int EndLine { get; set; }
    public int EndCol { get; set; }
    public int Severity { get; set; } = 1; // 1 = Error, 2 = Warning, 3 = Info, 4 = Hint
    public string Message { get; set; } = "";
    public string? Code { get; set; }
    public string? Source { get; set; }
}

public class LspLocationResult
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";
    [JsonPropertyName("startLine")]
    public int StartLine { get; set; }
    [JsonPropertyName("startColumn")]
    public int StartColumn { get; set; }
    [JsonPropertyName("endLine")]
    public int EndLine { get; set; }
    [JsonPropertyName("endColumn")]
    public int EndColumn { get; set; }
}

public class LspTextEditResult : LspLocationResult
{
    [JsonPropertyName("newText")]
    public string NewText { get; set; } = "";
}

public class LspDocumentSymbolResult : LspLocationResult
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
    [JsonPropertyName("detail")]
    public string Detail { get; set; } = "";
    [JsonPropertyName("containerName")]
    public string ContainerName { get; set; } = "";
    [JsonPropertyName("kind")]
    public int Kind { get; set; }
}

public class LspWorkspaceSymbolResult : LspLocationResult
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("containerName")]
    public string ContainerName { get; set; } = "";

    [JsonPropertyName("kind")]
    public int Kind { get; set; }
}

public class LspInlayHintResult
{
    [JsonPropertyName("line")]
    public int Line { get; set; }

    [JsonPropertyName("column")]
    public int Column { get; set; }

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("kind")]
    public int Kind { get; set; }

    [JsonPropertyName("paddingLeft")]
    public bool PaddingLeft { get; set; }

    [JsonPropertyName("paddingRight")]
    public bool PaddingRight { get; set; }

    [JsonPropertyName("tooltip")]
    public string? Tooltip { get; set; }

    [JsonPropertyName("textEdits")]
    public List<LspTextEditResult> TextEdits { get; set; } = new();
}

public class LspSemanticTokensResult
{
    [JsonPropertyName("data")]
    public List<uint> Data { get; set; } = new();

    [JsonPropertyName("resultId")]
    public string? ResultId { get; set; }
}

public class LspCodeActionResult
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("isPreferred")]
    public bool IsPreferred { get; set; }

    [JsonPropertyName("disabledReason")]
    public string? DisabledReason { get; set; }

    [JsonPropertyName("edits")]
    public List<LspTextEditResult> Edits { get; set; } = new();

    [JsonPropertyName("command")]
    public string? Command { get; set; }

    [JsonPropertyName("arguments")]
    public JsonArray? Arguments { get; set; }

    [JsonPropertyName("raw")]
    public JsonObject? Raw { get; set; }
}

public sealed class LspApplyEditResult
{
    public bool Applied { get; init; }
    public string? FailureReason { get; init; }
}

public sealed class LspCommandExecutionResult
{
    [JsonPropertyName("completed")]
    public bool Completed { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("result")]
    public JsonNode? Result { get; init; }
}

public sealed class LspProtocolResponse
{
    public bool Responded { get; init; }
    public JsonNode? Result { get; init; }
    public string? Error { get; init; }
}

public class LspServerSession : IDisposable
{
    public static readonly string[] SemanticTokenTypes =
    {
        "namespace", "type", "class", "enum", "interface", "struct", "typeParameter",
        "parameter", "variable", "property", "enumMember", "event", "function", "method",
        "macro", "keyword", "modifier", "comment", "string", "number", "regexp", "operator",
        "decorator"
    };

    public static readonly string[] SemanticTokenModifiers =
    {
        "declaration", "definition", "readonly", "static", "deprecated", "abstract",
        "async", "modification", "documentation", "defaultLibrary"
    };

    public string LanguageId { get; }
    public string RootPath { get; }
    public bool IsAlive => _process != null && !_process.HasExited;

    private Process? _process;
    private StreamWriter? _writer;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<LspProtocolResponse>> _pendingRequests = new();
    private readonly ConcurrentDictionary<string, byte> _openDocuments = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _requestIdCounter = 0;
    private readonly CancellationTokenSource _cts = new();
    private readonly Action<string, List<LspDiagnosticItem>> _onDiagnostics;
    private readonly Func<string, string?, JsonObject, Task<LspApplyEditResult>>? _onApplyWorkspaceEdit;
    private List<string> _serverSemanticTokenTypes = new(SemanticTokenTypes);
    private List<string> _serverSemanticTokenModifiers = new(SemanticTokenModifiers);

    public LspServerSession(
        string languageId,
        string rootPath,
        Action<string, List<LspDiagnosticItem>> onDiagnostics,
        Func<string, string?, JsonObject, Task<LspApplyEditResult>>? onApplyWorkspaceEdit = null)
    {
        LanguageId = languageId;
        RootPath = rootPath;
        _onDiagnostics = onDiagnostics;
        _onApplyWorkspaceEdit = onApplyWorkspaceEdit;
    }

    public async Task<bool> StartAsync(string command, string arguments)
    {
        try
        {
            var resolvedCommand = LspService.FindInPath(command) ?? command;
            var psi = new ProcessStartInfo
            {
                FileName = resolvedCommand,
                Arguments = arguments,
                WorkingDirectory = Directory.Exists(RootPath) ? RootPath : Environment.CurrentDirectory,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            psi.Environment["PATH"] = LspInstallService.BuildEffectivePath();

            _process = new Process { StartInfo = psi };
            if (!_process.Start()) return false;

            _writer = new StreamWriter(_process.StandardInput.BaseStream, new UTF8Encoding(false), 4096, leaveOpen: false)
            {
                AutoFlush = true
            };

            _ = Task.Run(ReadOutputLoopAsync);
            _ = Task.Run(ReadErrorLoopAsync);

            // Send initialize request
            var rootUri = new Uri(RootPath).AbsoluteUri;
            var initParams = new JsonObject
            {
                ["processId"] = Environment.ProcessId,
                ["rootUri"] = rootUri,
                ["rootPath"] = RootPath,
                ["capabilities"] = new JsonObject
                {
                    ["workspace"] = new JsonObject
                    {
                        ["configuration"] = true,
                        ["workspaceFolders"] = true,
                        ["applyEdit"] = true,
                        ["symbol"] = new JsonObject
                        {
                            ["dynamicRegistration"] = false
                        },
                        ["workspaceEdit"] = new JsonObject
                        {
                            ["documentChanges"] = true,
                            ["resourceOperations"] = new JsonArray()
                        }
                    },
                    ["textDocument"] = new JsonObject
                    {
                        ["synchronization"] = new JsonObject
                        {
                            ["dynamicRegistration"] = false,
                            ["willSave"] = false,
                            ["willSaveWaitUntil"] = false,
                            ["didSave"] = true
                        },
                        ["completion"] = new JsonObject
                        {
                            ["dynamicRegistration"] = false,
                            ["completionItem"] = new JsonObject { ["snippetSupport"] = true }
                        },
                        ["hover"] = new JsonObject { ["contentFormat"] = new JsonArray("markdown", "plaintext") },
                        ["definition"] = new JsonObject { ["dynamicRegistration"] = false },
                        ["references"] = new JsonObject { ["dynamicRegistration"] = false },
                        ["rename"] = new JsonObject
                        {
                            ["dynamicRegistration"] = false,
                            ["prepareSupport"] = true
                        },
                        ["signatureHelp"] = new JsonObject
                        {
                            ["dynamicRegistration"] = false,
                            ["signatureInformation"] = new JsonObject
                            {
                                ["documentationFormat"] = new JsonArray("markdown", "plaintext"),
                                ["parameterInformation"] = new JsonObject { ["labelOffsetSupport"] = true }
                            }
                        },
                        ["documentSymbol"] = new JsonObject
                        {
                            ["dynamicRegistration"] = false,
                            ["hierarchicalDocumentSymbolSupport"] = true
                        },
                        ["formatting"] = new JsonObject { ["dynamicRegistration"] = false },
                        ["codeAction"] = new JsonObject
                        {
                            ["dynamicRegistration"] = false,
                            ["resolveSupport"] = new JsonObject
                            {
                                ["properties"] = new JsonArray("edit", "command")
                            },
                            ["codeActionLiteralSupport"] = new JsonObject
                            {
                                ["codeActionKind"] = new JsonObject
                                {
                                    ["valueSet"] = new JsonArray(
                                        "", "quickfix", "refactor", "refactor.extract", "refactor.inline",
                                        "refactor.rewrite", "source", "source.organizeImports", "source.fixAll")
                                }
                            }
                        },
                        ["inlayHint"] = new JsonObject
                        {
                            ["dynamicRegistration"] = false,
                            ["resolveSupport"] = new JsonObject
                            {
                                ["properties"] = new JsonArray("tooltip", "textEdits", "label.tooltip", "label.location", "label.command")
                            }
                        },
                        ["semanticTokens"] = new JsonObject
                        {
                            ["dynamicRegistration"] = false,
                            ["requests"] = new JsonObject
                            {
                                ["range"] = false,
                                ["full"] = true
                            },
                            ["tokenTypes"] = new JsonArray(SemanticTokenTypes.Select(type => (JsonNode?)type).ToArray()),
                            ["tokenModifiers"] = new JsonArray(SemanticTokenModifiers.Select(modifier => (JsonNode?)modifier).ToArray()),
                            ["formats"] = new JsonArray("relative"),
                            ["overlappingTokenSupport"] = false,
                            ["multilineTokenSupport"] = false
                        }
                    }
                }
            };

            var initResult = await SendRequestAsync("initialize", initParams, 15000);
            if (initResult != null)
            {
                CaptureSemanticTokenLegend(initResult);
                // Send initialized notification
                await SendNotificationAsync("initialized", new JsonObject());
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LSP] Failed to start {LanguageId} server: {ex.Message}");
            return false;
        }
    }

    private void CaptureSemanticTokenLegend(JsonNode initializeResult)
    {
        if (initializeResult is not JsonObject root ||
            root["capabilities"] is not JsonObject capabilities ||
            capabilities["semanticTokensProvider"] is not JsonObject semanticProvider ||
            semanticProvider["legend"] is not JsonObject legend)
            return;
        if (legend?["tokenTypes"] is JsonArray tokenTypes)
        {
            var parsed = tokenTypes.Select(item => item?.GetValue<string>())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .ToList();
            if (parsed.Count > 0) _serverSemanticTokenTypes = parsed;
        }
        if (legend?["tokenModifiers"] is JsonArray tokenModifiers)
        {
            _serverSemanticTokenModifiers = tokenModifiers.Select(item => item?.GetValue<string>())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .ToList();
        }
    }

    public async Task<JsonNode?> SendRequestAsync(string method, JsonObject? parameters, int timeoutMs = 5000)
    {
        var response = await SendRequestWithStatusAsync(method, parameters, timeoutMs);
        return response.Error == null ? response.Result : null;
    }

    public async Task<LspProtocolResponse> SendRequestWithStatusAsync(
        string method,
        JsonObject? parameters,
        int timeoutMs = 5000)
    {
        if (!IsAlive || _writer == null)
        {
            return new LspProtocolResponse { Error = "The language server is not running." };
        }

        int reqId = Interlocked.Increment(ref _requestIdCounter);
        var tcs = new TaskCompletionSource<LspProtocolResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[reqId] = tcs;

        var req = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = reqId,
            ["method"] = method
        };
        if (parameters != null) req["params"] = parameters;

        await WriteMessageAsync(req.ToJsonString());

        using var cts = new CancellationTokenSource(timeoutMs);
        using var reg = cts.Token.Register(() =>
        {
            if (_pendingRequests.TryRemove(reqId, out var removedTcs))
            {
                // Notify the server so timed-out requests stop consuming server CPU.
                _ = SendCancelRequestAsync(reqId);
                removedTcs.TrySetResult(new LspProtocolResponse
                {
                    Error = $"The language server did not respond within {timeoutMs / 1000.0:0.#} seconds."
                });
            }
        });

        try
        {
            return await tcs.Task;
        }
        catch
        {
            return new LspProtocolResponse { Error = "The language server request failed." };
        }
    }

    public async Task SendNotificationAsync(string method, JsonObject? parameters)
    {
        if (!IsAlive || _writer == null) return;

        var notif = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method
        };
        if (parameters != null) notif["params"] = parameters;

        await WriteMessageAsync(notif.ToJsonString());
    }

    private async Task WriteMessageAsync(string json)
    {
        if (_writer == null || _process == null) return;
        bool lockTaken = false;
        try
        {
            await _writeLock.WaitAsync(_cts.Token);
            lockTaken = true;
            // One byte-exact frame (header + UTF-8 payload) instead of a text write per
            // Part, so the length on the wire is always the length of the bytes that follow it.
            var stream = _process.StandardInput.BaseStream;
            await stream.WriteAsync(LspFraming.EncodeFrame(json).AsMemory(), _cts.Token);
            await stream.FlushAsync(_cts.Token);
        }
        catch (OperationCanceledException) { /* send cancelled during shutdown */ }
        catch (Exception) { /* server pipe closed - notification dropped */ }
        finally
        {
            if (lockTaken) _writeLock.Release();
        }
    }

    private async Task ReadOutputLoopAsync()
    {
        if (_process == null) return;
        var stream = _process.StandardOutput.BaseStream;

        try
        {
            while (!_cts.Token.IsCancellationRequested && IsAlive)
            {
                // Framing lives in LspFraming so it can be tested without a server.
                var payload = await LspFraming.ReadMessageAsync(stream, _cts.Token);
                if (payload == null) return; // EOF
                ProcessIncomingMessage(payload);
            }
        }
        catch (OperationCanceledException) { /* reader loop cancelled */ }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LSP Read Error] {ex.Message}");
        }
    }

    public Task SendCancelRequestAsync(int requestId)
        => SendNotificationAsync("$/cancelRequest", new JsonObject { ["id"] = requestId });

    private async Task ReadErrorLoopAsync()
    {
        if (_process == null) return;
        try
        {
            while (!_cts.Token.IsCancellationRequested && IsAlive)
            {
                var line = await _process.StandardError.ReadLineAsync(_cts.Token);
                if (line == null) break;
                if (!string.IsNullOrWhiteSpace(line))
                {
                    LspService.NotifyStderr(LanguageId, line);
                }
            }
        }
        catch (Exception ex) { LoggingService.Swallowed(ex, $"LspServerSession.ReadErrorLoop({LanguageId})"); }
    }

    private void ProcessIncomingMessage(string json)
    {
        try
        {
            var node = JsonNode.Parse(json)?.AsObject();
            if (node == null) return;

            // 1. Request initiated by the language server.
            if (node.ContainsKey("id") && node["id"] != null && node["method"] != null)
            {
                _ = HandleServerRequestAsync(node);
                return;
            }

            // 2. Response to a client request.
            if (node.ContainsKey("id") && node["id"] != null)
            {
                int id = node["id"]!.GetValue<int>();
                if (_pendingRequests.TryRemove(id, out var tcs))
                {
                    if (node["error"] is JsonObject error)
                    {
                        tcs.TrySetResult(new LspProtocolResponse
                        {
                            Responded = true,
                            Error = error["message"]?.GetValue<string>() ?? "The language server returned an error."
                        });
                    }
                    else
                    {
                        tcs.TrySetResult(new LspProtocolResponse
                        {
                            Responded = true,
                            Result = node["result"]?.DeepClone()
                        });
                    }
                }
                return;
            }

            // 3. Notification from server
            var method = node["method"]?.GetValue<string>();
            if (method == "textDocument/publishDiagnostics")
            {
                var paramsNode = node["params"]?.AsObject();
                if (paramsNode != null)
                {
                    var uriStr = paramsNode["uri"]?.GetValue<string>() ?? "";
                    string filePath = Uri.TryCreate(uriStr, UriKind.Absolute, out var fileUri) && fileUri.IsFile
                        ? fileUri.LocalPath
                        : uriStr;

                    var diagnosticsList = new List<LspDiagnosticItem>();
                    var diagArray = paramsNode["diagnostics"]?.AsArray();
                    if (diagArray != null)
                    {
                        foreach (var d in diagArray)
                        {
                            if (d == null) continue;
                            var range = d["range"]?.AsObject();
                            var start = range?["start"]?.AsObject();
                            var end = range?["end"]?.AsObject();

                            int sLine = (start?["line"]?.GetValue<int>() ?? 0) + 1;
                            int sCol = (start?["character"]?.GetValue<int>() ?? 0) + 1;
                            int eLine = (end?["line"]?.GetValue<int>() ?? 0) + 1;
                            int eCol = (end?["character"]?.GetValue<int>() ?? 0) + 1;

                            int severity = d["severity"]?.GetValue<int>() ?? 1;
                            string msg = d["message"]?.GetValue<string>() ?? "";
                            string? code = d["code"]?.ToString();
                            string? source = d["source"]?.GetValue<string>();

                            diagnosticsList.Add(new LspDiagnosticItem
                            {
                                StartLine = sLine,
                                StartCol = sCol,
                                EndLine = eLine,
                                EndCol = eCol,
                                Severity = severity,
                                Message = msg,
                                Code = code,
                                Source = source
                            });
                        }
                    }

                    _onDiagnostics(filePath, diagnosticsList);
                }
            }
        }
        catch (Exception ex) { LoggingService.Swallowed(ex, "LspService.ParseDiagnostics"); }
    }

    private async Task HandleServerRequestAsync(JsonObject request)
    {
        var id = request["id"]?.DeepClone();
        if (id == null) return;
        var method = request["method"]?.GetValue<string>() ?? "";
        JsonNode? result = null;

        switch (method)
        {
            case "workspace/configuration":
            {
                var responseItems = new JsonArray();
                var items = request["params"]?["items"] as JsonArray;
                int count = items?.Count ?? 0;
                for (int i = 0; i < count; i++) responseItems.Add(new JsonObject());
                result = responseItems;
                break;
            }
            case "workspace/workspaceFolders":
                result = new JsonArray
                {
                    new JsonObject
                    {
                        ["uri"] = new Uri(RootPath).AbsoluteUri,
                        ["name"] = Path.GetFileName(RootPath)
                    }
                };
                break;
            case "client/registerCapability":
            case "client/unregisterCapability":
            case "window/workDoneProgress/create":
                result = null;
                break;
            case "workspace/applyEdit":
                {
                    var label = request["params"]?["label"]?.GetValue<string>();
                    var edit = request["params"]?["edit"] as JsonObject;
                    LspApplyEditResult reviewResult;
                    if (edit == null)
                    {
                        reviewResult = new LspApplyEditResult
                        {
                            FailureReason = "The language server did not provide a workspace edit."
                        };
                    }
                    else if (_onApplyWorkspaceEdit == null)
                    {
                        reviewResult = new LspApplyEditResult
                        {
                            FailureReason = "No workspace-edit review handler is available."
                        };
                    }
                    else
                    {
                        reviewResult = await _onApplyWorkspaceEdit(LanguageId, label, edit.DeepClone().AsObject());
                    }

                    result = new JsonObject { ["applied"] = reviewResult.Applied };
                    if (!string.IsNullOrWhiteSpace(reviewResult.FailureReason))
                        result["failureReason"] = reviewResult.FailureReason;
                    break;
                }
        }

        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["result"] = result
        };
        await WriteMessageAsync(response.ToJsonString());
    }

    public LspSemanticTokensResult RemapSemanticTokens(JsonNode? response)
    {
        return RemapSemanticTokens(response, _serverSemanticTokenTypes, _serverSemanticTokenModifiers);
    }

    public static LspSemanticTokensResult RemapSemanticTokens(
        JsonNode? response,
        IReadOnlyList<string> serverTokenTypes,
        IReadOnlyList<string> serverTokenModifiers)
    {
        var result = new LspSemanticTokensResult
        {
            ResultId = response?["resultId"]?.GetValue<string>()
        };
        if (response?["data"] is not JsonArray data || data.Count % 5 != 0) return result;

        int fallbackType = Array.IndexOf(SemanticTokenTypes, "variable");
        for (int index = 0; index < data.Count; index += 5)
        {
            if (!TryGetUInt(data[index], out var deltaLine) ||
                !TryGetUInt(data[index + 1], out var deltaStart) ||
                !TryGetUInt(data[index + 2], out var length) ||
                !TryGetUInt(data[index + 3], out var serverTypeIndex) ||
                !TryGetUInt(data[index + 4], out var serverModifierBits) ||
                serverTypeIndex >= serverTokenTypes.Count)
            {
                result.Data.Clear();
                return result;
            }

            string serverType = serverTokenTypes[(int)serverTypeIndex];
            int mappedType = Array.FindIndex(SemanticTokenTypes,
                candidate => candidate.Equals(serverType, StringComparison.Ordinal));
            if (mappedType < 0) mappedType = fallbackType;

            uint mappedModifiers = 0;
            for (int modifierIndex = 0; modifierIndex < serverTokenModifiers.Count && modifierIndex < 32; modifierIndex++)
            {
                if ((serverModifierBits & (1u << modifierIndex)) == 0) continue;
                int mappedModifier = Array.FindIndex(SemanticTokenModifiers,
                    candidate => candidate.Equals(serverTokenModifiers[modifierIndex], StringComparison.Ordinal));
                if (mappedModifier >= 0) mappedModifiers |= 1u << mappedModifier;
            }

            result.Data.Add(deltaLine);
            result.Data.Add(deltaStart);
            result.Data.Add(length);
            result.Data.Add((uint)mappedType);
            result.Data.Add(mappedModifiers);
        }
        return result;
    }

    private static bool TryGetUInt(JsonNode? node, out uint value)
    {
        value = 0;
        if (node is not JsonValue jsonValue) return false;
        if (jsonValue.TryGetValue<uint>(out value)) return true;
        if (jsonValue.TryGetValue<int>(out var signed) && signed >= 0)
        {
            value = (uint)signed;
            return true;
        }
        return false;
    }

    public void DidOpen(string filePath, string languageId, string content)
    {
        if (!_openDocuments.TryAdd(Path.GetFullPath(filePath), 0)) return;
        var uri = new Uri(filePath).AbsoluteUri;
        _ = SendNotificationAsync("textDocument/didOpen", new JsonObject
        {
            ["textDocument"] = new JsonObject
            {
                ["uri"] = uri,
                ["languageId"] = languageId,
                ["version"] = 1,
                ["text"] = content
            }
        });
    }

    private readonly ConcurrentDictionary<string, int> _documentVersions = new(StringComparer.OrdinalIgnoreCase);

    public void DidChange(string filePath, int version, string content)
    {
        // Document-version guard drops out-of-order stale updates.
        var key = Path.GetFullPath(filePath);
        if (_documentVersions.TryGetValue(key, out var lastVer) && version <= lastVer)
        {
            return;
        }
        _documentVersions[key] = version;

        var uri = new Uri(filePath).AbsoluteUri;
        _ = SendNotificationAsync("textDocument/didChange", new JsonObject
        {
            ["textDocument"] = new JsonObject
            {
                ["uri"] = uri,
                ["version"] = version
            },
            ["contentChanges"] = new JsonArray
            {
                new JsonObject { ["text"] = content }
            }
        });
    }

    public void DidSave(string filePath)
    {
        var uri = new Uri(filePath).AbsoluteUri;
        _ = SendNotificationAsync("textDocument/didSave", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = uri }
        });
    }

    public void DidClose(string filePath)
    {
        var key = Path.GetFullPath(filePath);
        _openDocuments.TryRemove(key, out _);
        _documentVersions.TryRemove(key, out _);
        var uri = new Uri(filePath).AbsoluteUri;
        _ = SendNotificationAsync("textDocument/didClose", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = uri }
        });
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch (Exception) { /* CTS already cancelled or disposed */ }
        foreach (var pending in _pendingRequests)
        {
            if (_pendingRequests.TryRemove(pending.Key, out var request))
            {
                request.TrySetResult(new LspProtocolResponse
                {
                    Error = "The language server stopped before completing the request."
                });
            }
        }
        try
        {
            _ = SendNotificationAsync("exit", new JsonObject());
            if (_process != null && !_process.HasExited)
            {
                _process.Kill(true);
            }
        }
        catch (Exception) { /* language server already exited */ }
        finally
        {
            _process?.Dispose();
            _writer?.Dispose();
        }
    }
}

public static class LspService
{
    private static readonly ConcurrentDictionary<string, LspServerSession> _sessions = new();
    private static readonly ConcurrentDictionary<string, (string Status, string Detail)> _unavailableLanguages = new();
    private static readonly ConcurrentDictionary<string, List<LspDiagnosticItem>> _latestDiagnostics = new(StringComparer.OrdinalIgnoreCase);
    private static Action<string, List<LspDiagnosticItem>>? _diagnosticsCallback;
    private static Action<string, string, string?>? _statusCallback;
    private static Func<string, string?, JsonObject, Task<LspApplyEditResult>>? _workspaceEditCallback;

    public static void Initialize(
        Action<string, List<LspDiagnosticItem>> onDiagnostics,
        Action<string, string, string?>? onStatus = null,
        Func<string, string?, JsonObject, Task<LspApplyEditResult>>? onWorkspaceEdit = null)
    {
        _diagnosticsCallback = onDiagnostics;
        _statusCallback = onStatus;
        _workspaceEditCallback = onWorkspaceEdit;
    }

    public static string NormalizeLanguage(string extensionOrLang)
    {
        var ext = extensionOrLang.TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "py" or "python" => "python",
            "cs" or "csharp" => "csharp",
            "rs" or "rust" => "rust",
            "go" or "golang" => "go",
            "c" or "cpp" or "h" or "hpp" => "cpp",
            "ts" or "typescript" => "typescript",
            "js" or "javascript" => "javascript",
            "html" => "html",
            "css" => "css",
            "json" => "json",
            _ => ext
        };
    }

    private static readonly ConcurrentDictionary<string, (int Count, DateTimeOffset NextAllowedUtc)> _crashBackoff = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> _documentContents = new(StringComparer.OrdinalIgnoreCase);
    private static Action<string, string>? _stderrCallback;
    internal const int MaxCrashRestarts = 3;

    public static void SetStderrCallback(Action<string, string>? callback) => _stderrCallback = callback;

    internal static void NotifyStderr(string languageId, string line)
    {
        LoggingService.LogWarning($"[LSP:{languageId}] {line}", nameof(LspService));
        _stderrCallback?.Invoke(languageId, line);
    }

    public static void ClearUnavailable(string? languageId = null)
    {
        if (string.IsNullOrWhiteSpace(languageId))
        {
            _unavailableLanguages.Clear();
            _crashBackoff.Clear();
            return;
        }
        var norm = NormalizeLanguage(languageId);
        _unavailableLanguages.TryRemove(norm, out _);
        _crashBackoff.TryRemove(norm, out _);
    }

    public static async Task<LspServerSession?> GetOrStartServerAsync(string languageId, string? workspacePath)
    {
        var lang = NormalizeLanguage(languageId);
        if (_sessions.TryGetValue(lang, out var existing))
        {
            if (existing.IsAlive)
            {
                _statusCallback?.Invoke(lang, "ready", null);
                return existing;
            }

            // Server crashed — allow up to MaxCrashRestarts restarts with backoff.
            _sessions.TryRemove(lang, out _);
            existing.Dispose();
            var prev = _crashBackoff.GetOrAdd(lang, _ => (0, DateTimeOffset.MinValue));
            var nextCount = prev.Count + 1;
            if (nextCount > MaxCrashRestarts)
            {
                var exhaustedMsg = $"Language server for {lang} crashed {MaxCrashRestarts} times; restart manually when ready.";
                _unavailableLanguages[lang] = ("failed", exhaustedMsg);
                _statusCallback?.Invoke(lang, "failed", exhaustedMsg);
                return null;
            }
            var waitSecs = (int)Math.Pow(2, nextCount - 1);
            _crashBackoff[lang] = (nextCount, DateTimeOffset.UtcNow.AddSeconds(waitSecs));
            _statusCallback?.Invoke(lang, "starting", $"Restarting {lang} server (attempt {nextCount}/{MaxCrashRestarts})...");
        }
        if (_unavailableLanguages.TryGetValue(lang, out var unavailable))
        {
            _statusCallback?.Invoke(lang, unavailable.Status, unavailable.Detail);
            return null;
        }
        if (_crashBackoff.TryGetValue(lang, out var backoff) && DateTimeOffset.UtcNow < backoff.NextAllowedUtc)
        {
            return null;
        }

        var root = !string.IsNullOrEmpty(workspacePath) && Directory.Exists(workspacePath)
            ? workspacePath
            : Environment.CurrentDirectory;

        // Restricted mode never starts a language server for the workspace.
        if (!WorkspaceTrustService.AllowsWorkspaceLanguageServers(root))
        {
            _statusCallback?.Invoke(lang, "restricted", "This folder is not trusted - language features stay off.");
            return null;
        }

        var serverConfig = DetectLanguageServer(lang);
        if (serverConfig == null)
        {
            var hint = GetInstallHint(lang) ?? "No language server was detected.";
            _unavailableLanguages[lang] = ("missing", hint);
            _statusCallback?.Invoke(lang, "missing", hint);
            return null;
        }

        _statusCallback?.Invoke(lang, "starting", $"Starting {serverConfig.Value.Command}");

        var session = new LspServerSession(
            lang,
            root,
            (path, diags) =>
            {
                if (!string.IsNullOrWhiteSpace(path))
                {
                    var key = Path.GetFullPath(path);
                    if (diags == null || diags.Count == 0) _latestDiagnostics.TryRemove(key, out _);
                    else _latestDiagnostics[key] = new List<LspDiagnosticItem>(diags);
                }
                _diagnosticsCallback?.Invoke(path, diags ?? new List<LspDiagnosticItem>());
            },
            (language, label, edit) => _workspaceEditCallback != null
                ? _workspaceEditCallback(language, label, edit)
                : Task.FromResult(new LspApplyEditResult
                {
                    FailureReason = "No workspace-edit review handler is available."
                }));

        bool started = await session.StartAsync(serverConfig.Value.Command, serverConfig.Value.Arguments);
        if (started)
        {
            _sessions[lang] = session;
            _statusCallback?.Invoke(lang, "ready", serverConfig.Value.Command);
            return session;
        }

        session.Dispose();
        var failure = $"Could not start {serverConfig.Value.Command}";
        _unavailableLanguages[lang] = ("failed", failure);
        _statusCallback?.Invoke(lang, "failed", failure);
        return null;
    }

    private static (string Command, string Arguments)? DetectLanguageServer(string lang)
    {
        switch (lang)
        {
            case "python":
                if (FindInPath("pyright-langserver.cmd") != null || FindInPath("pyright-langserver") != null)
                    return ("pyright-langserver.cmd", "--stdio");
                if (FindInPath("pylsp.exe") != null || FindInPath("pylsp") != null)
                    return ("pylsp", "");
                if (FindInPath("ruff.exe") != null || FindInPath("ruff") != null)
                    return ("ruff", "server");
                break;

            case "csharp":
                if (FindInPath("csharp-ls.exe") != null || FindInPath("csharp-ls") != null)
                    return ("csharp-ls", "");
                if (FindInPath("OmniSharp.exe") != null || FindInPath("omnisharp") != null)
                    return ("omnisharp", "-lsp");
                break;

            case "rust":
                if (FindInPath("rust-analyzer.exe") != null || FindInPath("rust-analyzer") != null)
                    return ("rust-analyzer", "");
                break;

            case "go":
                if (FindInPath("gopls.exe") != null || FindInPath("gopls") != null)
                    return ("gopls", "serve");
                break;

            case "cpp":
                if (FindInPath("clangd.exe") != null || FindInPath("clangd") != null)
                    return ("clangd", "--background-index");
                break;

            case "typescript":
            case "javascript":
                if (FindInPath("typescript-language-server.cmd") != null || FindInPath("typescript-language-server") != null)
                    return ("typescript-language-server.cmd", "--stdio");
                break;
        }

        return null;
    }

    private static string? GetInstallHint(string lang) => lang switch
    {
        "python" => "Install pyright: npm install -g pyright",
        "csharp" => "Install csharp-ls: dotnet tool install --global csharp-ls",
        "rust" => "Install rust-analyzer with rustup or your package manager",
        "go" => "Install gopls: go install golang.org/x/tools/gopls@latest",
        "cpp" => "Install LLVM clangd and add it to PATH",
        "typescript" or "javascript" => "Install the server: npm install -g typescript typescript-language-server",
        _ => null
    };

    public static string? FindInPath(string exeName)
    {
        // Check VoidCode's managed tools directories before system PATH.
        foreach (var managedDir in LspInstallService.ManagedBinDirectories)
        {
            try
            {
                var candidate = Path.Combine(managedDir, exeName);
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception ex) { LoggingService.Swallowed(ex, "LspService.FindInPath.Managed"); }
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), exeName);
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception) { /* unusable PATH entry */ }
        }
        return null;
    }

    public static async Task<object?> HandleHoverAsync(string filePath, int line, int col, string? workspace)
    {
        var lang = NormalizeLanguage(Path.GetExtension(filePath));
        var session = await GetOrStartServerAsync(lang, workspace);
        if (session == null) return null;

        var uri = new Uri(filePath).AbsoluteUri;
        var res = await session.SendRequestAsync("textDocument/hover", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = uri },
            ["position"] = new JsonObject
            {
                ["line"] = Math.Max(0, line - 1),
                ["character"] = Math.Max(0, col - 1)
            }
        });

        if (res == null) return null;

        var contents = res["contents"];
        if (contents == null) return null;

        if (contents is JsonValue val) return val.GetValue<string>();
        if (contents is JsonObject obj && obj.ContainsKey("value")) return obj["value"]?.GetValue<string>();
        if (contents is JsonArray arr)
        {
            var sb = new StringBuilder();
            foreach (var item in arr)
            {
                if (item is JsonObject o && o.ContainsKey("value")) sb.AppendLine(o["value"]?.GetValue<string>());
                else if (item is JsonValue v) sb.AppendLine(v.GetValue<string>());
            }
            return sb.ToString();
        }

        return contents.ToJsonString();
    }

    public static async Task<object?> HandleDefinitionAsync(string filePath, int line, int col, string? workspace)
    {
        var lang = NormalizeLanguage(Path.GetExtension(filePath));
        var session = await GetOrStartServerAsync(lang, workspace);
        if (session == null) return null;

        var uri = new Uri(filePath).AbsoluteUri;
        var res = await session.SendRequestAsync("textDocument/definition", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = uri },
            ["position"] = new JsonObject
            {
                ["line"] = Math.Max(0, line - 1),
                ["character"] = Math.Max(0, col - 1)
            }
        });

        if (res == null) return null;

        JsonObject? locObj = null;
        if (res is JsonArray arr && arr.Count > 0) locObj = arr[0]?.AsObject();
        else if (res is JsonObject obj) locObj = obj;

        if (locObj == null) return null;

        var targetUri = locObj["uri"]?.GetValue<string>() ?? locObj["targetUri"]?.GetValue<string>();
        var targetRange = locObj["range"]?.AsObject() ?? locObj["targetRange"]?.AsObject();

        if (string.IsNullOrEmpty(targetUri)) return null;

        string targetPath = Uri.TryCreate(targetUri, UriKind.Absolute, out var u) && u.IsFile
            ? u.LocalPath
            : targetUri;

        int tLine = (targetRange?["start"]?["line"]?.GetValue<int>() ?? 0) + 1;
        int tCol = (targetRange?["start"]?["character"]?.GetValue<int>() ?? 0) + 1;

        return new
        {
            path = targetPath,
            line = tLine,
            column = tCol
        };
    }

    public static async Task<object?> HandleCompletionAsync(string filePath, int line, int col, string? workspace)
    {
        var lang = NormalizeLanguage(Path.GetExtension(filePath));
        var session = await GetOrStartServerAsync(lang, workspace);
        if (session == null) return null;

        var uri = new Uri(filePath).AbsoluteUri;
        var res = await session.SendRequestAsync("textDocument/completion", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = uri },
            ["position"] = new JsonObject
            {
                ["line"] = Math.Max(0, line - 1),
                ["character"] = Math.Max(0, col - 1)
            }
        });

        if (res == null) return null;

        JsonArray? itemsArr = null;
        if (res is JsonArray arr) itemsArr = arr;
        else if (res is JsonObject obj && obj.ContainsKey("items")) itemsArr = obj["items"]?.AsArray();

        if (itemsArr == null) return null;

        var list = new List<object>();
        foreach (var item in itemsArr)
        {
            if (item is not JsonObject o) continue;
            list.Add(new
            {
                label = o["label"]?.GetValue<string>() ?? "",
                kind = o["kind"]?.GetValue<int>() ?? 1,
                detail = o["detail"]?.GetValue<string>() ?? "",
                insertText = o["insertText"]?.GetValue<string>() ?? o["label"]?.GetValue<string>() ?? ""
            });
            if (list.Count >= 50) break; // Limit batch size for fast Monaco rendering
        }

        return list;
    }

    public static async Task<List<LspLocationResult>> HandleReferencesAsync(
        string filePath,
        int line,
        int col,
        string? workspace)
    {
        var session = await GetOrStartServerAsync(NormalizeLanguage(Path.GetExtension(filePath)), workspace);
        if (session == null) return new List<LspLocationResult>();

        var parameters = CreatePositionParams(filePath, line, col);
        parameters["context"] = new JsonObject { ["includeDeclaration"] = true };
        var response = await session.SendRequestAsync("textDocument/references", parameters, 10000);
        return ParseLocations(response);
    }

    public static List<LspLocationResult> ParseLocations(JsonNode? response)
    {
        var results = new List<LspLocationResult>();
        if (response is not JsonArray locations) return results;

        foreach (var location in locations)
        {
            if (location is JsonObject locationObject && TryParseLocation(locationObject, out var parsed))
                results.Add(parsed);
            if (results.Count >= 500) break;
        }
        return results;
    }

    public static async Task<JsonNode?> HandleSignatureHelpAsync(
        string filePath,
        int line,
        int col,
        string? workspace)
    {
        var session = await GetOrStartServerAsync(NormalizeLanguage(Path.GetExtension(filePath)), workspace);
        if (session == null) return null;
        var response = await session.SendRequestAsync(
            "textDocument/signatureHelp",
            CreatePositionParams(filePath, line, col),
            5000);
        return response?.DeepClone();
    }

    public static async Task<List<LspDocumentSymbolResult>> HandleDocumentSymbolsAsync(
        string filePath,
        string? workspace)
    {
        var session = await GetOrStartServerAsync(NormalizeLanguage(Path.GetExtension(filePath)), workspace);
        if (session == null) return new List<LspDocumentSymbolResult>();

        var response = await session.SendRequestAsync("textDocument/documentSymbol", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = new Uri(filePath).AbsoluteUri }
        }, 10000);

        return ParseDocumentSymbols(response, filePath);
    }

    public static List<LspDocumentSymbolResult> ParseDocumentSymbols(JsonNode? response, string defaultPath)
    {
        var results = new List<LspDocumentSymbolResult>();
        if (response is JsonArray symbols)
        {
            foreach (var symbol in symbols)
            {
                FlattenDocumentSymbol(symbol, defaultPath, "", results);
                if (results.Count >= 1000) break;
            }
        }
        return results;
    }

    public static async Task<List<LspWorkspaceSymbolResult>> HandleWorkspaceSymbolsAsync(
        string filePath,
        string query,
        string? workspace)
    {
        if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(query))
            return new List<LspWorkspaceSymbolResult>();
        var session = await GetOrStartServerAsync(NormalizeLanguage(Path.GetExtension(filePath)), workspace);
        if (session == null) return new List<LspWorkspaceSymbolResult>();
        var response = await session.SendRequestAsync("workspace/symbol", new JsonObject
        {
            ["query"] = query
        }, 10000);
        return ParseWorkspaceSymbols(response);
    }

    public static List<LspWorkspaceSymbolResult> ParseWorkspaceSymbols(JsonNode? response)
    {
        var results = new List<LspWorkspaceSymbolResult>();
        if (response is not JsonArray symbols) return results;

        foreach (var symbol in symbols)
        {
            if (symbol is not JsonObject item || item["location"] is not JsonObject location) continue;
            var uri = location["uri"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(uri)) continue;
            var path = Uri.TryCreate(uri, UriKind.Absolute, out var parsedUri) && parsedUri.IsFile
                ? parsedUri.LocalPath
                : uri;
            var result = new LspWorkspaceSymbolResult
            {
                Name = item["name"]?.GetValue<string>() ?? "(anonymous)",
                ContainerName = item["containerName"]?.GetValue<string>() ?? "",
                Kind = item["kind"]?.GetValue<int>() ?? 13,
                Path = path,
                StartLine = 1,
                StartColumn = 1,
                EndLine = 1,
                EndColumn = 1
            };
            if (location["range"] is JsonObject range) ApplyRange(result, range);
            results.Add(result);
            if (results.Count >= 500) break;
        }
        return results;
    }

    public static async Task<List<LspInlayHintResult>> HandleInlayHintsAsync(
        string filePath,
        int startLine,
        int startColumn,
        int endLine,
        int endColumn,
        string? workspace)
    {
        var session = await GetOrStartServerAsync(NormalizeLanguage(Path.GetExtension(filePath)), workspace);
        if (session == null) return new List<LspInlayHintResult>();
        var uri = new Uri(filePath).AbsoluteUri;
        var response = await session.SendRequestAsync("textDocument/inlayHint", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = uri },
            ["range"] = new JsonObject
            {
                ["start"] = new JsonObject
                {
                    ["line"] = Math.Max(0, startLine - 1),
                    ["character"] = Math.Max(0, startColumn - 1)
                },
                ["end"] = new JsonObject
                {
                    ["line"] = Math.Max(0, endLine - 1),
                    ["character"] = Math.Max(0, endColumn - 1)
                }
            }
        }, 10000);
        return ParseInlayHints(response, uri);
    }

    public static List<LspInlayHintResult> ParseInlayHints(JsonNode? response, string documentUri)
    {
        var results = new List<LspInlayHintResult>();
        if (response is not JsonArray hints) return results;
        foreach (var hint in hints)
        {
            if (hint is not JsonObject item || item["position"] is not JsonObject position) continue;
            string label = item["label"] switch
            {
                JsonValue value when value.TryGetValue<string>(out var text) => text,
                JsonArray parts => string.Concat(parts.OfType<JsonObject>()
                    .Select(part => part["value"]?.GetValue<string>() ?? "")),
                _ => ""
            };
            if (string.IsNullOrEmpty(label)) continue;

            string? tooltip = item["tooltip"] switch
            {
                JsonValue value when value.TryGetValue<string>(out var text) => text,
                JsonObject markup => markup["value"]?.GetValue<string>(),
                _ => null
            };
            results.Add(new LspInlayHintResult
            {
                Line = (position["line"]?.GetValue<int>() ?? 0) + 1,
                Column = (position["character"]?.GetValue<int>() ?? 0) + 1,
                Label = label,
                Kind = item["kind"]?.GetValue<int>() ?? 0,
                PaddingLeft = item["paddingLeft"]?.GetValue<bool>() ?? false,
                PaddingRight = item["paddingRight"]?.GetValue<bool>() ?? false,
                Tooltip = tooltip,
                TextEdits = ParseTextEdits(documentUri, item["textEdits"])
            });
            if (results.Count >= 1000) break;
        }
        return results;
    }

    public static async Task<LspSemanticTokensResult> HandleSemanticTokensAsync(
        string filePath,
        string? workspace)
    {
        var session = await GetOrStartServerAsync(NormalizeLanguage(Path.GetExtension(filePath)), workspace);
        if (session == null) return new LspSemanticTokensResult();
        var response = await session.SendRequestAsync("textDocument/semanticTokens/full", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = new Uri(filePath).AbsoluteUri }
        }, 15000);
        return session.RemapSemanticTokens(response);
    }

    public static async Task<List<LspTextEditResult>> HandleRenameAsync(
        string filePath,
        int line,
        int col,
        string newName,
        string? workspace)
    {
        var session = await GetOrStartServerAsync(NormalizeLanguage(Path.GetExtension(filePath)), workspace);
        if (session == null || string.IsNullOrWhiteSpace(newName)) return new List<LspTextEditResult>();

        var parameters = CreatePositionParams(filePath, line, col);
        parameters["newName"] = newName;
        var response = await session.SendRequestAsync("textDocument/rename", parameters, 15000);
        return ParseWorkspaceEdit(response);
    }

    public static List<LspTextEditResult> ParseWorkspaceEdit(JsonNode? response)
    {
        var results = new List<LspTextEditResult>();
        if (response is not JsonObject workspaceEdit) return results;
        if (HasUnsupportedResourceOperations(workspaceEdit)) return results;

        if (workspaceEdit["changes"] is JsonObject changes)
        {
            foreach (var change in changes)
            {
                AddTextEdits(change.Key, change.Value, results);
            }
        }

        if (workspaceEdit["documentChanges"] is JsonArray documentChanges)
        {
            foreach (var change in documentChanges)
            {
                var uri = change?["textDocument"]?["uri"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(uri)) AddTextEdits(uri, change?["edits"], results);
            }
        }
        return results;
    }

    public static bool HasUnsupportedResourceOperations(JsonNode? response)
    {
        if (response is not JsonObject workspaceEdit ||
            workspaceEdit["documentChanges"] is not JsonArray documentChanges)
            return false;

        foreach (var change in documentChanges)
        {
            if (change is not JsonObject item) return true;
            if (item["kind"] != null || item["textDocument"] is not JsonObject || item["edits"] is not JsonArray)
                return true;
        }
        return false;
    }

    public static async Task<List<LspTextEditResult>> HandleFormattingAsync(
        string filePath,
        int tabSize,
        bool insertSpaces,
        string? workspace)
    {
        var session = await GetOrStartServerAsync(NormalizeLanguage(Path.GetExtension(filePath)), workspace);
        if (session != null)
        {
            var uri = new Uri(filePath).AbsoluteUri;
            var response = await session.SendRequestAsync("textDocument/formatting", new JsonObject
            {
                ["textDocument"] = new JsonObject { ["uri"] = uri },
                ["options"] = new JsonObject
                {
                    ["tabSize"] = Math.Clamp(tabSize, 1, 16),
                    ["insertSpaces"] = insertSpaces,
                    ["trimTrailingWhitespace"] = true,
                    ["insertFinalNewline"] = false,
                    ["trimFinalNewlines"] = false
                }
            }, 15000);

            var edits = ParseTextEdits(uri, response);
            if (edits.Count > 0) return edits;
        }

        return await TryCliFormatAsync(filePath, workspace);
    }

    internal static async Task<List<LspTextEditResult>> TryCliFormatAsync(string filePath, string? workspace)
    {
        var results = new List<LspTextEditResult>();
        if (string.IsNullOrWhiteSpace(filePath)) return results;

        var root = !string.IsNullOrEmpty(workspace) && Directory.Exists(workspace)
            ? workspace
            : Environment.CurrentDirectory;
        if (!WorkspaceTrustService.AllowsWorkspaceLanguageServers(root)) return results;

        string? content = null;
        var fullKey = Path.GetFullPath(filePath);
        if (!_documentContents.TryGetValue(fullKey, out content) && File.Exists(fullKey))
        {
            try { content = await File.ReadAllTextAsync(fullKey); }
            catch (Exception ex) { LoggingService.Swallowed(ex, "LspService.TryCliFormat.ReadFile"); }
        }
        if (string.IsNullOrEmpty(content)) return results;

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        var (exe, args) = ext switch
        {
            ".py" => ("ruff", $"format --stdin-filename \"{filePath}\" -"),
            ".go" => ("gofmt", ""),
            ".rs" => ("rustfmt", "--edition 2021"),
            ".js" or ".ts" or ".jsx" or ".tsx" or ".css" or ".html" or ".json" or ".md" => ("prettier", $"--stdin-filepath \"{filePath}\""),
            ".cs" => ("dotnet", "csharpier"),
            _ => ("", "")
        };
        if (string.IsNullOrEmpty(exe)) return results;

        var resolvedExe = CapabilitiesService.DefaultFindExecutable(exe + ".cmd")
            ?? CapabilitiesService.DefaultFindExecutable(exe + ".exe")
            ?? CapabilitiesService.DefaultFindExecutable(exe);
        if (string.IsNullOrEmpty(resolvedExe)) return results;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = resolvedExe,
                Arguments = args,
                WorkingDirectory = root,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            using var proc = Process.Start(psi);
            if (proc == null) return results;

            await proc.StandardInput.WriteAsync(content);
            proc.StandardInput.Close();

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var completed = await Task.WhenAny(stdoutTask, Task.Delay(5000));
            if (completed != stdoutTask)
            {
                try { proc.Kill(true); } catch (Exception) { /* already exited */ }
                return results;
            }

            var formatted = await stdoutTask;
            await proc.WaitForExitAsync();
            if (proc.ExitCode == 0 && !string.IsNullOrEmpty(formatted) && formatted != content)
            {
                var lines = content.Split('\n');
                results.Add(new LspTextEditResult
                {
                    Path = fullKey,
                    StartLine = 1,
                    StartColumn = 1,
                    EndLine = Math.Max(1, lines.Length),
                    EndColumn = (lines.LastOrDefault()?.Length ?? 0) + 1,
                    NewText = formatted
                });
            }
        }
        catch (Exception ex) { LoggingService.Swallowed(ex, "LspService.TryCliFormat"); }
        return results;
    }

    public static async Task<List<LspCodeActionResult>> HandleCodeActionsAsync(
        string filePath,
        int startLine,
        int startColumn,
        int endLine,
        int endColumn,
        JsonArray? diagnostics,
        string? workspace)
    {
        var session = await GetOrStartServerAsync(NormalizeLanguage(Path.GetExtension(filePath)), workspace);
        if (session == null) return new List<LspCodeActionResult>();
        var response = await session.SendRequestAsync("textDocument/codeAction", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = new Uri(filePath).AbsoluteUri },
            ["range"] = new JsonObject
            {
                ["start"] = new JsonObject
                {
                    ["line"] = Math.Max(0, startLine - 1),
                    ["character"] = Math.Max(0, startColumn - 1)
                },
                ["end"] = new JsonObject
                {
                    ["line"] = Math.Max(0, endLine - 1),
                    ["character"] = Math.Max(0, endColumn - 1)
                }
            },
            ["context"] = new JsonObject
            {
                ["diagnostics"] = diagnostics?.DeepClone() ?? new JsonArray()
            }
        }, 10000);

        return ParseCodeActions(response);
    }

    public static async Task<LspCodeActionResult?> HandleCodeActionResolveAsync(
        string filePath,
        JsonObject action,
        string? workspace)
    {
        var session = await GetOrStartServerAsync(NormalizeLanguage(Path.GetExtension(filePath)), workspace);
        if (session == null) return null;
        var response = await session.SendRequestAsync("codeAction/resolve", action.DeepClone().AsObject(), 10000);
        return response is JsonObject resolved ? ParseCodeAction(resolved) : null;
    }

    public static async Task<LspCommandExecutionResult> HandleExecuteCommandAsync(
        string filePath,
        string command,
        JsonArray? arguments,
        string? workspace)
    {
        var session = await GetOrStartServerAsync(NormalizeLanguage(Path.GetExtension(filePath)), workspace);
        if (session == null || string.IsNullOrWhiteSpace(command))
        {
            return new LspCommandExecutionResult
            {
                Error = string.IsNullOrWhiteSpace(command)
                    ? "The language action did not specify a command."
                    : "The language server is not available."
            };
        }

        var response = await session.SendRequestWithStatusAsync("workspace/executeCommand", new JsonObject
        {
            ["command"] = command,
            ["arguments"] = arguments?.DeepClone() ?? new JsonArray()
        }, 100000);
        return new LspCommandExecutionResult
        {
            Completed = response.Responded && response.Error == null,
            Error = response.Error,
            Result = response.Result
        };
    }

    public static List<LspCodeActionResult> ParseCodeActions(JsonNode? response)
    {
        var results = new List<LspCodeActionResult>();
        if (response is not JsonArray actions) return results;
        foreach (var action in actions)
        {
            if (action is JsonObject item)
            {
                var parsed = ParseCodeAction(item);
                if (parsed != null) results.Add(parsed);
            }
            if (results.Count >= 200) break;
        }
        return results;
    }

    public static LspCodeActionResult? ParseCodeAction(JsonObject item)
    {
        string title = item["title"]?.GetValue<string>() ?? "";
        if (string.IsNullOrWhiteSpace(title)) return null;
        var result = new LspCodeActionResult
        {
            Title = title,
            Kind = item["kind"]?.GetValue<string>() ?? "",
            IsPreferred = item["isPreferred"]?.GetValue<bool>() ?? false,
            DisabledReason = item["disabled"]?["reason"]?.GetValue<string>(),
            Raw = item.DeepClone().AsObject()
        };

        if (HasUnsupportedResourceOperations(item["edit"]))
        {
            result.DisabledReason ??= "This action includes file create, rename, or delete operations that require a separate review flow.";
        }
        else
        {
            result.Edits = ParseWorkspaceEdit(item["edit"]);
        }
        JsonObject? commandObject = item["command"] as JsonObject;
        if (commandObject != null)
        {
            result.Command = commandObject["command"]?.GetValue<string>();
            result.Arguments = commandObject["arguments"]?.DeepClone() as JsonArray;
        }
        else if (item["command"] is JsonValue commandValue)
        {
            result.Command = commandValue.GetValue<string>();
            result.Arguments = item["arguments"]?.DeepClone() as JsonArray;
        }
        return result;
    }

    public static List<LspTextEditResult> ParseTextEdits(string uri, JsonNode? response)
    {
        var results = new List<LspTextEditResult>();
        AddTextEdits(uri, response, results);
        return results;
    }

    private static JsonObject CreatePositionParams(string filePath, int line, int col)
    {
        return new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = new Uri(filePath).AbsoluteUri },
            ["position"] = new JsonObject
            {
                ["line"] = Math.Max(0, line - 1),
                ["character"] = Math.Max(0, col - 1)
            }
        };
    }

    private static bool TryParseLocation(JsonObject location, out LspLocationResult result)
    {
        result = new LspLocationResult();
        try
        {
            var uri = location["uri"]?.GetValue<string>() ?? location["targetUri"]?.GetValue<string>();
            var range = location["range"] as JsonObject ??
                        location["targetSelectionRange"] as JsonObject ??
                        location["targetRange"] as JsonObject;
            if (string.IsNullOrWhiteSpace(uri) || range == null) return false;

            result.Path = Uri.TryCreate(uri, UriKind.Absolute, out var parsedUri) && parsedUri.IsFile
                ? parsedUri.LocalPath
                : uri;
            ApplyRange(result, range);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void ApplyRange(LspLocationResult result, JsonObject range)
    {
        result.StartLine = (range["start"]?["line"]?.GetValue<int>() ?? 0) + 1;
        result.StartColumn = (range["start"]?["character"]?.GetValue<int>() ?? 0) + 1;
        result.EndLine = (range["end"]?["line"]?.GetValue<int>() ?? result.StartLine - 1) + 1;
        result.EndColumn = (range["end"]?["character"]?.GetValue<int>() ?? result.StartColumn - 1) + 1;
    }

    private static void AddTextEdits(string uri, JsonNode? editsNode, List<LspTextEditResult> results)
    {
        if (editsNode is not JsonArray edits) return;
        var path = Uri.TryCreate(uri, UriKind.Absolute, out var parsedUri) && parsedUri.IsFile
            ? parsedUri.LocalPath
            : uri;
        foreach (var edit in edits)
        {
            if (edit?["range"] is not JsonObject range) continue;
            var result = new LspTextEditResult
            {
                Path = path,
                NewText = edit["newText"]?.GetValue<string>() ?? ""
            };
            ApplyRange(result, range);
            results.Add(result);
            if (results.Count >= 5000) return;
        }
    }

    private static void FlattenDocumentSymbol(
        JsonNode? symbol,
        string defaultPath,
        string parentName,
        List<LspDocumentSymbolResult> results)
    {
        if (symbol is not JsonObject item) return;
        string name = item["name"]?.GetValue<string>() ?? "(anonymous)";
        string path = defaultPath;
        JsonObject? range = item["selectionRange"] as JsonObject ?? item["range"] as JsonObject;

        if (item["location"] is JsonObject location)
        {
            var uri = location["uri"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(uri) && Uri.TryCreate(uri, UriKind.Absolute, out var parsedUri) && parsedUri.IsFile)
                path = parsedUri.LocalPath;
            range = location["range"] as JsonObject ?? range;
        }
        if (range == null) return;

        var result = new LspDocumentSymbolResult
        {
            Path = path,
            Name = name,
            Detail = item["detail"]?.GetValue<string>() ?? "",
            ContainerName = item["containerName"]?.GetValue<string>() ?? parentName,
            Kind = item["kind"]?.GetValue<int>() ?? 13
        };
        ApplyRange(result, range);
        results.Add(result);

        if (item["children"] is JsonArray children)
        {
            foreach (var child in children)
            {
                FlattenDocumentSymbol(child, defaultPath, name, results);
                if (results.Count >= 1000) return;
            }
        }
    }

    public static void NotifyDidOpen(string filePath, string languageId, string content, string? workspace)
    {
        var safeContent = content ?? "";
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            _documentContents[Path.GetFullPath(filePath)] = safeContent;
        }
        var lang = NormalizeLanguage(languageId);
        _ = Task.Run(async () =>
        {
            var session = await GetOrStartServerAsync(lang, workspace);
            session?.DidOpen(filePath, lang, safeContent);
        });
    }

    public static void NotifyDidChange(string filePath, int version, string content, string? workspace)
    {
        var safeContent = content ?? "";
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            _documentContents[Path.GetFullPath(filePath)] = safeContent;
        }
        var lang = NormalizeLanguage(Path.GetExtension(filePath));
        if (_sessions.TryGetValue(lang, out var session) && session.IsAlive)
        {
            session.DidChange(filePath, version, safeContent);
        }
    }

    public static void NotifyDidSave(string filePath)
    {
        var lang = NormalizeLanguage(Path.GetExtension(filePath));
        if (_sessions.TryGetValue(lang, out var session) && session.IsAlive)
        {
            session.DidSave(filePath);
        }
    }

    public static void NotifyDidClose(string filePath)
    {
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            _documentContents.TryRemove(Path.GetFullPath(filePath), out _);
        }
        var lang = NormalizeLanguage(Path.GetExtension(filePath));
        if (_sessions.TryGetValue(lang, out var session) && session.IsAlive)
        {
            session.DidClose(filePath);
        }
    }

    public static Dictionary<string, List<LspDiagnosticItem>> GetCachedDiagnostics(string? filePath = null)
    {
        var result = new Dictionary<string, List<LspDiagnosticItem>>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            try
            {
                var key = Path.GetFullPath(filePath);
                if (_latestDiagnostics.TryGetValue(key, out var list) && list.Count > 0)
                    result[key] = new List<LspDiagnosticItem>(list);
            }
            catch (Exception ex) { LoggingService.Swallowed(ex, "LspService.GetCachedDiagnostics"); }
            return result;
        }

        foreach (var kv in _latestDiagnostics)
        {
            if (kv.Value.Count > 0)
                result[kv.Key] = new List<LspDiagnosticItem>(kv.Value);
        }
        return result;
    }

    public static string FormatCachedDiagnostics(string? filePath = null, string? workspacePath = null, int maxItems = 40)
    {
        var map = GetCachedDiagnostics(filePath);
        if (map.Count == 0)
            return string.IsNullOrWhiteSpace(filePath)
                ? "No live LSP diagnostics reported across open files."
                : $"No live LSP diagnostics for '{Path.GetFileName(filePath)}'.";

        var sb = new StringBuilder();
        int count = 0;
        foreach (var kv in map.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            var rel = !string.IsNullOrWhiteSpace(workspacePath)
                ? Path.GetRelativePath(workspacePath, kv.Key)
                : kv.Key;
            foreach (var d in kv.Value)
            {
                if (count >= maxItems) break;
                var sev = d.Severity switch
                {
                    1 => "ERROR",
                    2 => "WARN",
                    3 => "INFO",
                    _ => "HINT"
                };
                sb.AppendLine($"[{sev}] {rel}:{d.StartLine}:{d.StartCol} — {d.Message}");
                count++;
            }
            if (count >= maxItems) break;
        }
        return sb.ToString().TrimEnd();
    }

    public static void ShutdownAll()
    {
        _latestDiagnostics.Clear();
        foreach (var kvp in _sessions)
        {
            kvp.Value.Dispose();
        }
        _sessions.Clear();
        _unavailableLanguages.Clear();
        _crashBackoff.Clear();
    }
}
