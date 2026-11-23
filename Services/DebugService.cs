using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using VoidCode.Dap;

namespace VoidCode.Services
{
    public class BreakpointInfo
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string FilePath { get; set; } = "";
        public int Line { get; set; }
        public string? Condition { get; set; }
        public string? LogMessage { get; set; }
        public bool Enabled { get; set; } = true;
        public bool Verified { get; set; } = true;
    }

    public class StackFrameInfo
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string FilePath { get; set; } = "";
        public int Line { get; set; }
        public int Column { get; set; }
        public string? InternalFrameId { get; set; }
    }

    public class VariableInfo
    {
        public string Name { get; set; } = "";
        public string Value { get; set; } = "";
        public string Type { get; set; } = "";
        public int VariablesReference { get; set; }
        public string? ObjectId { get; set; }
    }

    public class DebugService
    {
        private static readonly ConcurrentDictionary<string, List<BreakpointInfo>> _breakpoints = new(StringComparer.OrdinalIgnoreCase);
        private static Process? _activeProcess;
        private static ClientWebSocket? _v8Socket;
        private static CancellationTokenSource? _debugCts;
        private static int _v8MessageId = 0;
        private static readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode>> _pendingV8Requests = new();
        private static Action<string, object>? _sendToJs;
        private static string? _currentDebugFile;
        private static string? _currentTopFrameId;
        private static readonly ConcurrentDictionary<int, string> _objectIdByVarRef = new();
        private static int _nextVarRef = 1;
        private static DapClient? _dapClient;
        private static int _dapStoppedThreadId = 1;
        private static int _dapTopFrameId;

        /// <summary>Optional test seam for injecting a fake DAP adapter without launching external binaries.</summary>
        internal static Func<string, string, DapClient?>? DapFactoryOverrideForTests { get; set; }

        public static bool IsDebugging => (_activeProcess != null && !_activeProcess.HasExited) || (_dapClient != null && _dapClient.IsRunning);
        public static string DebugState { get; private set; } = "idle"; // idle, running, paused

        public static void Initialize(Action<string, object> sendToJs)
        {
            _sendToJs = sendToJs;
        }

        public static IReadOnlyList<BreakpointInfo> GetBreakpoints(string filePath)
        {
            var key = Path.GetFullPath(filePath);
            return _breakpoints.TryGetValue(key, out var list) ? list : Array.Empty<BreakpointInfo>();
        }

        public static List<BreakpointInfo> GetAllBreakpoints()
        {
            var all = new List<BreakpointInfo>();
            foreach (var kvp in _breakpoints)
            {
                all.AddRange(kvp.Value);
            }
            return all;
        }

        public static void SetBreakpoints(string filePath, IEnumerable<BreakpointInfo> bps)
        {
            var key = Path.GetFullPath(filePath);
            var list = new List<BreakpointInfo>(bps);
            _breakpoints[key] = list;

            _sendToJs?.Invoke("debug_breakpoints_updated", new
            {
                file = key,
                breakpoints = list
            });

            // If actively debugging in V8 or DAP, update breakpoints dynamically
            if (IsDebugging && _v8Socket != null && _v8Socket.State == WebSocketState.Open)
            {
                _ = SyncV8BreakpointsForFileAsync(key, list);
            }
            if (IsDebugging && _dapClient != null && _dapClient.IsRunning)
            {
                _ = SyncDapBreakpointsForFileAsync(key, list);
            }
        }

        public static void ToggleBreakpoint(string filePath, int line, string? condition = null, string? logMessage = null)
        {
            var key = Path.GetFullPath(filePath);
            var list = _breakpoints.GetOrAdd(key, _ => new List<BreakpointInfo>());
            lock (list)
            {
                var existing = list.Find(b => b.Line == line);
                if (existing != null)
                {
                    list.Remove(existing);
                }
                else
                {
                    list.Add(new BreakpointInfo
                    {
                        FilePath = key,
                        Line = line,
                        Condition = condition,
                        LogMessage = logMessage,
                        Enabled = true,
                        Verified = true
                    });
                }
            }

            SetBreakpoints(key, list);
        }

        public static void ClearBreakpoints(string? filePath = null)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                _breakpoints.Clear();
                _sendToJs?.Invoke("debug_breakpoints_updated", new { file = "", breakpoints = Array.Empty<BreakpointInfo>() });
            }
            else
            {
                var key = Path.GetFullPath(filePath);
                _breakpoints.TryRemove(key, out _);
                _sendToJs?.Invoke("debug_breakpoints_updated", new { file = key, breakpoints = Array.Empty<BreakpointInfo>() });
            }
        }

        public static async Task StartDebuggingAsync(string filePath, string workspaceRoot, bool stopOnEntry = false, string? args = null)
        {
            if (IsDebugging)
            {
                await StopDebuggingAsync();
            }

            _debugCts = new CancellationTokenSource();
            _currentDebugFile = Path.GetFullPath(filePath);
            DebugState = "running";
            _objectIdByVarRef.Clear();
            _nextVarRef = 1;

            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext == ".js" || ext == ".mjs" || ext == ".cjs" || ext == ".ts")
            {
                await StartNodeDebuggerAsync(filePath, workspaceRoot, stopOnEntry, args, _debugCts.Token);
            }
            else if (ext == ".cs")
            {
                await StartDotNetDebuggerAsync(filePath, workspaceRoot, stopOnEntry, args, _debugCts.Token);
            }
            else if (ext == ".py")
            {
                await StartPythonDebuggerAsync(filePath, workspaceRoot, stopOnEntry, args, _debugCts.Token);
            }
            else
            {
                // Fallback to Node if generic script
                await StartNodeDebuggerAsync(filePath, workspaceRoot, stopOnEntry, args, _debugCts.Token);
            }
        }

        private static async Task StartNodeDebuggerAsync(string filePath, string workspaceRoot, bool stopOnEntry, string? args, CancellationToken ct)
        {
            try
            {
                var isTs = filePath.EndsWith(".ts", StringComparison.OrdinalIgnoreCase);
                var nodeExe = "node";
                var inspectArg = stopOnEntry ? "--inspect-brk=0" : "--inspect=0";
                var arguments = isTs
                    ? $"{inspectArg} --loader ts-node/esm \"{filePath}\" {args ?? ""}"
                    : $"{inspectArg} \"{filePath}\" {args ?? ""}";

                var psi = new ProcessStartInfo
                {
                    FileName = nodeExe,
                    Arguments = arguments,
                    WorkingDirectory = string.IsNullOrEmpty(workspaceRoot) ? Path.GetDirectoryName(filePath)! : workspaceRoot,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                _activeProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };

                var wsUrlTcs = new TaskCompletionSource<string>();

                _activeProcess.ErrorDataReceived += (s, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data)) return;

                    var match = Regex.Match(e.Data, @"Debugger listening on (ws://[^\s]+)");
                    if (match.Success && !wsUrlTcs.Task.IsCompleted)
                    {
                        wsUrlTcs.TrySetResult(match.Groups[1].Value);
                    }
                    else
                    {
                        _sendToJs?.Invoke("debug_output", new { category = "stderr", text = e.Data + "\n" });
                    }
                };

                _activeProcess.OutputDataReceived += (s, e) =>
                {
                    if (e.Data != null)
                    {
                        _sendToJs?.Invoke("debug_output", new { category = "stdout", text = e.Data + "\n" });
                    }
                };

                _activeProcess.Exited += (s, e) =>
                {
                    DebugState = "idle";
                    _sendToJs?.Invoke("debug_stopped", new { exitCode = _activeProcess?.ExitCode ?? 0 });
                    _sendToJs?.Invoke("debug_output", new { category = "console", text = $"\n[Process terminated with exit code {_activeProcess?.ExitCode ?? 0}]\n" });
                };

                _activeProcess.Start();
                _activeProcess.BeginOutputReadLine();
                _activeProcess.BeginErrorReadLine();

                _sendToJs?.Invoke("debug_started", new
                {
                    target = Path.GetFileName(filePath),
                    file = filePath,
                    type = "node"
                });

                var completedTask = await Task.WhenAny(wsUrlTcs.Task, Task.Delay(5000, ct));
                if (completedTask != wsUrlTcs.Task)
                {
                    _sendToJs?.Invoke("toast", new { type = "error", message = "Debugger connection timed out." });
                    return;
                }

                var wsUrl = await wsUrlTcs.Task;
                _v8Socket = new ClientWebSocket();
                await _v8Socket.ConnectAsync(new Uri(wsUrl), ct);

                _ = ListenV8MessagesAsync(_v8Socket, ct);

                // Initialize V8 debug session
                await SendV8CommandAsync("Debugger.enable");
                await SendV8CommandAsync("Runtime.enable");
                await SendV8CommandAsync("Debugger.setPauseOnExceptions", new { state = "uncaught" });

                // Register all breakpoints
                foreach (var kvp in _breakpoints)
                {
                    await SyncV8BreakpointsForFileAsync(kvp.Key, kvp.Value);
                }

                if (!stopOnEntry)
                {
                    await SendV8CommandAsync("Debugger.resume");
                }
            }
            catch (Exception ex)
            {
                _sendToJs?.Invoke("toast", new { type = "error", message = $"Failed to start Node debugger: {ex.Message}" });
                DebugState = "idle";
                _sendToJs?.Invoke("debug_stopped", new { exitCode = -1 });
            }
        }

        private static async Task StartDotNetDebuggerAsync(string filePath, string workspaceRoot, bool stopOnEntry, string? args, CancellationToken ct)
        {
            var netcoredbg = LspService.FindInPath("netcoredbg.exe") ?? LspService.FindInPath("netcoredbg");
            if (netcoredbg == null && DapFactoryOverrideForTests == null)
            {
                EmitDebugUnsupported("dotnet", ".NET debugger (netcoredbg) is not installed on PATH or in VoidCode tools.");
                return;
            }

            var configs = LaunchConfigService.LoadConfigurations(workspaceRoot, filePath);
            var cfg = configs.Find(c => c.Type.Equals("coreclr", StringComparison.OrdinalIgnoreCase) || c.Type.Equals("dotnet", StringComparison.OrdinalIgnoreCase));
            var program = !string.IsNullOrWhiteSpace(cfg?.Program) ? cfg!.Program : ResolveDotNetProgramDll(filePath, workspaceRoot);
            var cwd = !string.IsNullOrWhiteSpace(cfg?.Cwd) ? cfg!.Cwd : workspaceRoot;
            var launchArgs = new JsonObject
            {
                ["program"] = program,
                ["cwd"] = cwd,
                ["stopAtEntry"] = stopOnEntry || (cfg?.StopOnEntry == true),
                ["args"] = BuildArgsArray(cfg?.Args, args)
            };

            await StartDapSessionAsync(
                runtime: "dotnet",
                adapterId: "coreclr",
                command: netcoredbg ?? "netcoredbg",
                commandArgs: "--interpreter=vscode",
                targetLabel: Path.GetFileName(program),
                filePath: filePath,
                workspaceRoot: workspaceRoot,
                launchArgs: launchArgs,
                ct: ct);
        }

        private static async Task StartPythonDebuggerAsync(string filePath, string workspaceRoot, bool stopOnEntry, string? args, CancellationToken ct)
        {
            var pythonExe = LspService.FindInPath("python.exe") ?? LspService.FindInPath("python3.exe") ?? LspService.FindInPath("python");
            if ((pythonExe == null || !IsPythonDebugpyInstalled(pythonExe)) && DapFactoryOverrideForTests == null)
            {
                EmitDebugUnsupported("python", "Python debugger (debugpy) is not installed. Run: python -m pip install debugpy");
                return;
            }

            var configs = LaunchConfigService.LoadConfigurations(workspaceRoot, filePath);
            var cfg = configs.Find(c => c.Type.Equals("python", StringComparison.OrdinalIgnoreCase) || c.Type.Equals("debugpy", StringComparison.OrdinalIgnoreCase));
            var program = !string.IsNullOrWhiteSpace(cfg?.Program) ? cfg!.Program : filePath;
            var cwd = !string.IsNullOrWhiteSpace(cfg?.Cwd) ? cfg!.Cwd : workspaceRoot;
            var launchArgs = new JsonObject
            {
                ["program"] = program,
                ["cwd"] = cwd,
                ["stopOnEntry"] = stopOnEntry || (cfg?.StopOnEntry == true),
                ["args"] = BuildArgsArray(cfg?.Args, args),
                ["console"] = "internalConsole",
                ["justMyCode"] = true
            };

            await StartDapSessionAsync(
                runtime: "python",
                adapterId: "debugpy",
                command: pythonExe ?? "python",
                commandArgs: "-m debugpy.adapter",
                targetLabel: Path.GetFileName(program),
                filePath: filePath,
                workspaceRoot: workspaceRoot,
                launchArgs: launchArgs,
                ct: ct);
        }

        private static async Task StartDapSessionAsync(
            string runtime,
            string adapterId,
            string command,
            string commandArgs,
            string targetLabel,
            string filePath,
            string workspaceRoot,
            JsonObject launchArgs,
            CancellationToken ct)
        {
            try
            {
                var client = DapFactoryOverrideForTests != null
                    ? DapFactoryOverrideForTests(runtime, commandArgs)
                    : DapClient.StartProcess(command, commandArgs, workspaceRoot);

                if (client == null)
                {
                    EmitDebugUnsupported(runtime, $"Failed to start {runtime} debug adapter ({command}).");
                    return;
                }

                _dapClient = client;
                var initializedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                client.EventReceived += (evtName, body) =>
                {
                    if (evtName == "initialized")
                    {
                        initializedTcs.TrySetResult(true);
                    }
                    else
                    {
                        _ = HandleDapEventAsync(evtName, body);
                    }
                };
                client.Exited += exitCode =>
                {
                    DebugState = "idle";
                    _sendToJs?.Invoke("debug_stopped", new { exitCode });
                };

                var initResp = await client.InitializeAsync(adapterId, ct);
                if (!initResp.Success)
                {
                    client.Dispose();
                    _dapClient = null;
                    EmitDebugUnsupported(runtime, initResp.Message ?? $"Failed to initialize {runtime} debugger.");
                    return;
                }

                _sendToJs?.Invoke("debug_started", new { target = targetLabel, file = filePath, type = runtime });

                // Kick off launch and wait briefly for `initialized` so breakpoints can be registered before execution.
                var launchTask = client.SendRequestAsync("launch", launchArgs, 15000, ct);
                await Task.WhenAny(initializedTcs.Task, Task.Delay(1500, ct));

                foreach (var kvp in _breakpoints)
                {
                    await SyncDapBreakpointsForFileAsync(kvp.Key, kvp.Value);
                }

                await client.SendRequestAsync("setExceptionBreakpoints", new JsonObject
                {
                    ["filters"] = new JsonArray("uncaught")
                }, 5000, ct);

                await client.SendRequestAsync("configurationDone", new JsonObject(), 5000, ct);
                var launchResp = await launchTask;
                if (!launchResp.Success)
                {
                    _sendToJs?.Invoke("debug_output", new { category = "stderr", text = $"Launch error: {launchResp.Message}\n" });
                }
            }
            catch (Exception ex)
            {
                LoggingService.LogError($"DAP start error ({runtime}): {ex.Message}", ex, nameof(DebugService));
                _dapClient?.Dispose();
                _dapClient = null;
                DebugState = "idle";
                _sendToJs?.Invoke("debug_stopped", new { exitCode = -1 });
            }
        }

        private static async Task SyncDapBreakpointsForFileAsync(string filePath, List<BreakpointInfo> bps)
        {
            if (_dapClient == null || !_dapClient.IsRunning) return;
            var arr = new JsonArray();
            foreach (var bp in bps)
            {
                if (!bp.Enabled) continue;
                var obj = new JsonObject { ["line"] = bp.Line };
                if (!string.IsNullOrWhiteSpace(bp.Condition)) obj["condition"] = bp.Condition;
                if (!string.IsNullOrWhiteSpace(bp.LogMessage)) obj["logMessage"] = bp.LogMessage;
                arr.Add(obj);
            }
            await _dapClient.SendRequestAsync("setBreakpoints", new JsonObject
            {
                ["source"] = new JsonObject { ["path"] = filePath },
                ["breakpoints"] = arr
            });
        }

        private static async Task HandleDapEventAsync(string evtName, JsonObject? body)
        {
            if (_dapClient == null) return;

            switch (evtName)
            {
                case "stopped":
                {
                    DebugState = "paused";
                    _dapStoppedThreadId = body?["threadId"]?.GetValue<int>() ?? 1;
                    var reason = body?["reason"]?.GetValue<string>() ?? "breakpoint";

                    var stackResp = await _dapClient.SendRequestAsync("stackTrace", new JsonObject
                    {
                        ["threadId"] = _dapStoppedThreadId,
                        ["startFrame"] = 0,
                        ["levels"] = 20
                    });

                    var frames = new List<StackFrameInfo>();
                    if (stackResp.Success && stackResp.Body?["stackFrames"] is JsonArray sfArr)
                    {
                        foreach (var sf in sfArr)
                        {
                            if (sf == null) continue;
                            frames.Add(new StackFrameInfo
                            {
                                Id = sf["id"]?.GetValue<int>() ?? 0,
                                Name = sf["name"]?.GetValue<string>() ?? "(frame)",
                                FilePath = sf["source"]?["path"]?.GetValue<string>() ?? _currentDebugFile ?? "",
                                Line = sf["line"]?.GetValue<int>() ?? 1,
                                Column = sf["column"]?.GetValue<int>() ?? 1,
                                InternalFrameId = (sf["id"]?.GetValue<int>() ?? 0).ToString()
                            });
                        }
                    }

                    var localVars = new List<VariableInfo>();
                    if (frames.Count > 0)
                    {
                        _dapTopFrameId = frames[0].Id;
                        _currentTopFrameId = frames[0].InternalFrameId;
                        var scopesResp = await _dapClient.SendRequestAsync("scopes", new JsonObject { ["frameId"] = _dapTopFrameId });
                        if (scopesResp.Success && scopesResp.Body?["scopes"] is JsonArray scopesArr && scopesArr.Count > 0)
                        {
                            var varRef = scopesArr[0]?["variablesReference"]?.GetValue<int>() ?? 0;
                            if (varRef > 0)
                            {
                                localVars = await FetchDapVariablesAsync(varRef);
                            }
                        }
                    }

                    _sendToJs?.Invoke("debug_paused", new
                    {
                        reason,
                        file = frames.Count > 0 ? frames[0].FilePath : (_currentDebugFile ?? ""),
                        line = frames.Count > 0 ? frames[0].Line : 1,
                        callStack = frames,
                        variables = localVars
                    });
                    break;
                }
                case "continued":
                    DebugState = "running";
                    _sendToJs?.Invoke("debug_resumed", new { });
                    break;
                case "output":
                {
                    var text = body?["output"]?.GetValue<string>() ?? "";
                    var category = body?["category"]?.GetValue<string>() ?? "console";
                    if (!string.IsNullOrEmpty(text))
                    {
                        _sendToJs?.Invoke("debug_output", new { category, text });
                    }
                    break;
                }
                case "terminated":
                case "exited":
                {
                    var exitCode = body?["exitCode"]?.GetValue<int>() ?? 0;
                    DebugState = "idle";
                    _sendToJs?.Invoke("debug_stopped", new { exitCode });
                    break;
                }
            }
        }

        private static async Task<List<VariableInfo>> FetchDapVariablesAsync(int variablesReference, int start = 0, int count = 100)
        {
            var list = new List<VariableInfo>();
            if (_dapClient == null || !_dapClient.IsRunning || variablesReference <= 0) return list;
            var resp = await _dapClient.SendRequestAsync("variables", new JsonObject
            {
                ["variablesReference"] = variablesReference,
                ["start"] = start,
                ["count"] = count
            });
            if (resp.Success && resp.Body?["variables"] is JsonArray arr)
            {
                foreach (var v in arr)
                {
                    if (v == null) continue;
                    list.Add(new VariableInfo
                    {
                        Name = v["name"]?.GetValue<string>() ?? "",
                        Value = v["value"]?.GetValue<string>() ?? "",
                        Type = v["type"]?.GetValue<string>() ?? "",
                        VariablesReference = v["variablesReference"]?.GetValue<int>() ?? 0
                    });
                }
            }
            return list;
        }

        private static JsonArray BuildArgsArray(List<string>? configArgs, string? rawArgs)
        {
            var arr = new JsonArray();
            if (configArgs != null && configArgs.Count > 0)
            {
                foreach (var a in configArgs) arr.Add(a);
                return arr;
            }
            if (!string.IsNullOrWhiteSpace(rawArgs))
            {
                foreach (var part in rawArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    arr.Add(part);
                }
            }
            return arr;
        }

        private static string ResolveDotNetProgramDll(string filePath, string workspaceRoot)
        {
            if (filePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) return filePath;
            try
            {
                var binDebug = Path.Combine(workspaceRoot, "bin", "Debug");
                if (Directory.Exists(binDebug))
                {
                    var projectName = Path.GetFileName(workspaceRoot);
                    var dlls = Directory.GetFiles(binDebug, "*.dll", SearchOption.AllDirectories);
                    foreach (var dll in dlls)
                    {
                        if (Path.GetFileNameWithoutExtension(dll).Equals(projectName, StringComparison.OrdinalIgnoreCase))
                            return dll;
                    }
                    if (dlls.Length > 0) return dlls[0];
                }
            }
            catch (Exception ex) { LoggingService.Swallowed(ex, "DebugService.ResolveDotNetProgramDll"); }
            return filePath;
        }

        internal static bool IsPythonDebugpyInstalled(string pythonExe)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = pythonExe,
                    Arguments = "-c \"import debugpy\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) return false;
                if (!proc.WaitForExit(3000))
                {
                    try { proc.Kill(true); } catch (Exception ex) { LoggingService.Swallowed(ex, "DebugService.DebugpyProbeKill"); }
                    return false;
                }
                return proc.ExitCode == 0;
            }
            catch (Exception ex)
            {
                LoggingService.Swallowed(ex, "DebugService.IsPythonDebugpyInstalled");
                return false;
            }
        }

        // The single honest "we cannot debug this yet" path for unimplemented runtimes.
        private static void EmitDebugUnsupported(string runtime, string reason)
        {
            DebugState = "idle";
            _activeProcess = null;
            _sendToJs?.Invoke("debug_unsupported", new { runtime, reason });
        }

        private static async Task SyncV8BreakpointsForFileAsync(string filePath, List<BreakpointInfo> bps)
        {
            if (_v8Socket == null || _v8Socket.State != WebSocketState.Open) return;

            var fileName = Path.GetFileName(filePath);
            var regex = ".*" + Regex.Escape(fileName);

            foreach (var bp in bps)
            {
                if (bp.Enabled)
                {
                    var @params = new JsonObject
                    {
                        ["urlRegex"] = regex,
                        ["lineNumber"] = bp.Line - 1,
                        ["columnNumber"] = 0
                    };
                    if (!string.IsNullOrEmpty(bp.Condition))
                    {
                        @params["condition"] = bp.Condition;
                    }
                    await SendV8CommandAsync("Debugger.setBreakpointByUrl", @params);
                }
            }
        }

        private static async Task ListenV8MessagesAsync(ClientWebSocket socket, CancellationToken ct)
        {
            var buffer = new byte[64 * 1024];
            var ms = new MemoryStream();

            try
            {
                while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    ms.SetLength(0);
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                            return;
                        }
                        ms.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    var text = Encoding.UTF8.GetString(ms.ToArray());
                    var json = JsonNode.Parse(text);
                    if (json == null) continue;

                    if (json["id"] != null)
                    {
                        var reqId = json["id"]!.GetValue<int>();
                        if (_pendingV8Requests.TryRemove(reqId, out var tcs))
                        {
                            tcs.TrySetResult(json);
                        }
                    }
                    else if (json["method"] != null)
                    {
                        var method = json["method"]!.GetValue<string>();
                        var @params = json["params"];
                        await HandleV8EventAsync(method, @params);
                    }
                }
            }
            catch (OperationCanceledException) { /* V8 listener loop cancelled */ }
            catch (Exception ex)
            {
                _sendToJs?.Invoke("debug_output", new { category = "stderr", text = $"[Debugger Socket Error]: {ex.Message}\n" });
            }
        }

        private static async Task HandleV8EventAsync(string method, JsonNode? @params)
        {
            switch (method)
            {
                case "Debugger.paused":
                {
                    DebugState = "paused";
                    var reason = @params?["reason"]?.GetValue<string>() ?? "breakpoint";
                    var hitBps = @params?["hitBreakpoints"]?.AsArray();
                    var callFrames = @params?["callFrames"]?.AsArray();

                    var frames = new List<StackFrameInfo>();
                    var variables = new List<VariableInfo>();

                    if (callFrames != null && callFrames.Count > 0)
                    {
                        var topFrame = callFrames[0];
                        _currentTopFrameId = topFrame?["callFrameId"]?.GetValue<string>();

                        int frameIdx = 0;
                        foreach (var f in callFrames)
                        {
                            if (f == null) continue;
                            var fnName = f["functionName"]?.GetValue<string>();
                            if (string.IsNullOrEmpty(fnName)) fnName = "(anonymous)";
                            var url = f["url"]?.GetValue<string>() ?? "";
                            var line = (f["location"]?["lineNumber"]?.GetValue<int>() ?? 0) + 1;
                            var col = (f["location"]?["columnNumber"]?.GetValue<int>() ?? 0) + 1;

                            // Clean file path from URL
                            var resolvedPath = url.StartsWith("file:///") ? new Uri(url).LocalPath : url;

                            frames.Add(new StackFrameInfo
                            {
                                Id = frameIdx++,
                                Name = fnName,
                                FilePath = resolvedPath,
                                Line = line,
                                Column = col,
                                InternalFrameId = f["callFrameId"]?.GetValue<string>()
                            });
                        }

                        // Extract local scope variables from top frame
                        var scopeChain = topFrame?["scopeChain"]?.AsArray();
                        if (scopeChain != null)
                        {
                            foreach (var s in scopeChain)
                            {
                                var scopeType = s?["type"]?.GetValue<string>();
                                var obj = s?["object"];
                                var objectId = obj?["objectId"]?.GetValue<string>();
                                if (!string.IsNullOrEmpty(objectId) && (scopeType == "local" || scopeType == "block" || scopeType == "catch"))
                                {
                                    var props = await FetchV8PropertiesAsync(objectId);
                                    variables.AddRange(props);
                                }
                            }
                        }

                        // Add "this" object if available
                        var thisObj = topFrame?["this"];
                        if (thisObj != null)
                        {
                            var thisType = thisObj["type"]?.GetValue<string>() ?? "object";
                            var thisVal = thisObj["description"]?.GetValue<string>() ?? thisObj["value"]?.ToString() ?? "Object";
                            var thisId = thisObj["objectId"]?.GetValue<string>();
                            var varRef = 0;
                            if (!string.IsNullOrEmpty(thisId))
                            {
                                varRef = _nextVarRef++;
                                _objectIdByVarRef[varRef] = thisId;
                            }
                            variables.Add(new VariableInfo
                            {
                                Name = "this",
                                Value = thisVal,
                                Type = thisType,
                                VariablesReference = varRef,
                                ObjectId = thisId
                            });
                        }
                    }

                    _sendToJs?.Invoke("debug_paused", new
                    {
                        reason,
                        frames,
                        variables
                    });
                    break;
                }

                case "Debugger.resumed":
                {
                    DebugState = "running";
                    _currentTopFrameId = null;
                    _sendToJs?.Invoke("debug_resumed", new { });
                    break;
                }

                case "Runtime.consoleAPICalled":
                {
                    var type = @params?["type"]?.GetValue<string>() ?? "log";
                    var args = @params?["args"]?.AsArray();
                    var sb = new StringBuilder();
                    if (args != null)
                    {
                        foreach (var a in args)
                        {
                            var val = a?["value"]?.ToString() ?? a?["description"]?.ToString() ?? "";
                            sb.Append(val).Append(' ');
                        }
                    }
                    _sendToJs?.Invoke("debug_output", new
                    {
                        category = "console",
                        level = type,
                        text = sb.ToString().TrimEnd() + "\n"
                    });
                    break;
                }
            }
        }

        private static async Task<List<VariableInfo>> FetchV8PropertiesAsync(string objectId)
        {
            var list = new List<VariableInfo>();
            try
            {
                var resp = await SendV8CommandAsync("Runtime.getProperties", new
                {
                    objectId,
                    ownProperties = true,
                    generatePreview = true
                });

                var result = resp?["result"]?["result"]?.AsArray();
                if (result != null)
                {
                    foreach (var p in result)
                    {
                        var name = p?["name"]?.GetValue<string>() ?? "";
                        if (name.StartsWith("__")) continue; // hide internal proto

                        var valNode = p?["value"];
                        var valType = valNode?["type"]?.GetValue<string>() ?? "unknown";
                        var desc = valNode?["description"]?.GetValue<string>() ?? valNode?["value"]?.ToString() ?? "undefined";
                        var propObjectId = valNode?["objectId"]?.GetValue<string>();

                        var varRef = 0;
                        if (!string.IsNullOrEmpty(propObjectId) && valType == "object")
                        {
                            varRef = _nextVarRef++;
                            _objectIdByVarRef[varRef] = propObjectId;
                        }

                        list.Add(new VariableInfo
                        {
                            Name = name,
                            Value = desc,
                            Type = valType,
                            VariablesReference = varRef,
                            ObjectId = propObjectId
                        });
                    }
                }
            }
            catch (Exception ex) { LoggingService.Swallowed(ex, "DebugService.GetVariables"); }
            return list;
        }

        public static async Task<List<VariableInfo>> GetVariablesByReferenceAsync(int variablesReference)
        {
            if (_dapClient != null && _dapClient.IsRunning)
            {
                return await FetchDapVariablesAsync(variablesReference);
            }
            if (_objectIdByVarRef.TryGetValue(variablesReference, out var objectId))
            {
                return await FetchV8PropertiesAsync(objectId);
            }
            return new List<VariableInfo>();
        }

        public static async Task<JsonNode?> SendV8CommandAsync(string method, object? @params = null)
        {
            if (_v8Socket == null || _v8Socket.State != WebSocketState.Open) return null;

            var id = Interlocked.Increment(ref _v8MessageId);
            var req = new JsonObject
            {
                ["id"] = id,
                ["method"] = method
            };
            if (@params != null)
            {
                req["params"] = JsonSerializer.SerializeToNode(@params);
            }

            var tcs = new TaskCompletionSource<JsonNode>();
            _pendingV8Requests[id] = tcs;

            var bytes = Encoding.UTF8.GetBytes(req.ToJsonString());
            await _v8Socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);

            var timeoutTask = Task.Delay(5000);
            var done = await Task.WhenAny(tcs.Task, timeoutTask);
            if (done == tcs.Task)
            {
                return await tcs.Task;
            }
            _pendingV8Requests.TryRemove(id, out _);
            return null;
        }

        public static async Task ContinueAsync()
        {
            if (_dapClient != null && _dapClient.IsRunning)
            {
                await _dapClient.SendRequestAsync("continue", new JsonObject { ["threadId"] = _dapStoppedThreadId });
                DebugState = "running";
                _sendToJs?.Invoke("debug_resumed", new { });
                return;
            }
            if (_v8Socket != null && _v8Socket.State == WebSocketState.Open)
            {
                await SendV8CommandAsync("Debugger.resume");
            }
        }

        public static async Task PauseAsync()
        {
            if (_dapClient != null && _dapClient.IsRunning)
            {
                await _dapClient.SendRequestAsync("pause", new JsonObject { ["threadId"] = _dapStoppedThreadId });
                return;
            }
            if (_v8Socket != null && _v8Socket.State == WebSocketState.Open)
            {
                await SendV8CommandAsync("Debugger.pause");
            }
        }

        public static async Task StepOverAsync()
        {
            if (_dapClient != null && _dapClient.IsRunning)
            {
                await _dapClient.SendRequestAsync("next", new JsonObject { ["threadId"] = _dapStoppedThreadId });
                return;
            }
            if (_v8Socket != null && _v8Socket.State == WebSocketState.Open)
            {
                await SendV8CommandAsync("Debugger.stepOver");
            }
        }

        public static async Task StepIntoAsync()
        {
            if (_dapClient != null && _dapClient.IsRunning)
            {
                await _dapClient.SendRequestAsync("stepIn", new JsonObject { ["threadId"] = _dapStoppedThreadId });
                return;
            }
            if (_v8Socket != null && _v8Socket.State == WebSocketState.Open)
            {
                await SendV8CommandAsync("Debugger.stepInto");
            }
        }

        public static async Task StepOutAsync()
        {
            if (_dapClient != null && _dapClient.IsRunning)
            {
                await _dapClient.SendRequestAsync("stepOut", new JsonObject { ["threadId"] = _dapStoppedThreadId });
                return;
            }
            if (_v8Socket != null && _v8Socket.State == WebSocketState.Open)
            {
                await SendV8CommandAsync("Debugger.stepOut");
            }
        }

        public static async Task<object> EvaluateExpressionAsync(string expression, string? frameId = null)
        {
            if (_dapClient != null && _dapClient.IsRunning)
            {
                var evalArgs = new JsonObject
                {
                    ["expression"] = expression,
                    ["context"] = "watch"
                };
                if (int.TryParse(frameId, out var parsedFrame)) evalArgs["frameId"] = parsedFrame;
                else if (_dapTopFrameId > 0) evalArgs["frameId"] = _dapTopFrameId;

                var dapResp = await _dapClient.SendRequestAsync("evaluate", evalArgs);
                if (dapResp.Success && dapResp.Body != null)
                {
                    return new
                    {
                        success = true,
                        result = dapResp.Body["result"]?.GetValue<string>() ?? "",
                        type = dapResp.Body["type"]?.GetValue<string>() ?? "value"
                    };
                }
                return new { success = false, error = dapResp.Message ?? "Evaluation failed." };
            }

            if (_v8Socket == null || _v8Socket.State != WebSocketState.Open)
            {
                return new { success = false, error = "Debugger is not active." };
            }

            var targetFrameId = frameId ?? _currentTopFrameId;
            JsonNode? resp;

            if (!string.IsNullOrEmpty(targetFrameId))
            {
                resp = await SendV8CommandAsync("Debugger.evaluateOnCallFrame", new
                {
                    callFrameId = targetFrameId,
                    expression,
                    returnByValue = false,
                    generatePreview = true
                });
            }
            else
            {
                resp = await SendV8CommandAsync("Runtime.evaluate", new
                {
                    expression,
                    returnByValue = false,
                    generatePreview = true
                });
            }

            var resultNode = resp?["result"]?["result"];
            if (resultNode != null)
            {
                var valType = resultNode["type"]?.GetValue<string>() ?? "undefined";
                var desc = resultNode["description"]?.GetValue<string>() ?? resultNode["value"]?.ToString() ?? "undefined";
                return new { success = true, result = desc, type = valType };
            }

            var exceptionNode = resp?["result"]?["exceptionDetails"];
            if (exceptionNode != null)
            {
                var errMsg = exceptionNode["text"]?.GetValue<string>() ?? "Evaluation error";
                return new { success = false, error = errMsg };
            }

            return new { success = false, error = "No response from debugger" };
        }

        public static async Task StopDebuggingAsync()
        {
            _debugCts?.Cancel();

            if (_dapClient != null)
            {
                try
                {
                    if (_dapClient.IsRunning)
                    {
                        await _dapClient.SendRequestAsync("disconnect", new JsonObject { ["terminateDebuggee"] = true }, 1500);
                    }
                }
                catch (Exception ex) { LoggingService.Swallowed(ex, "DebugService.DapDisconnect"); }
                _dapClient.Dispose();
                _dapClient = null;
                _dapTopFrameId = 0;
            }

            if (_v8Socket != null)
            {
                try
                {
                    if (_v8Socket.State == WebSocketState.Open)
                    {
                        await _v8Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Debugging ended", CancellationToken.None);
                    }
                }
                catch (Exception) { /* socket already closed */ }
                _v8Socket.Dispose();
                _v8Socket = null;
            }

            if (_activeProcess != null && !_activeProcess.HasExited)
            {
                try
                {
                    _activeProcess.Kill(true);
                }
                catch (Exception) { /* process already exited */ }
                _activeProcess.Dispose();
                _activeProcess = null;
            }

            DebugState = "idle";
            _currentDebugFile = null;
            _currentTopFrameId = null;
            _objectIdByVarRef.Clear();

            _sendToJs?.Invoke("debug_stopped", new { exitCode = 0 });
        }

        private static string? FindExecutableInPath(string exeName)
        {
            var pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(pathEnv)) return null;

            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var fullPath = Path.Combine(dir.Trim(), exeName);
                    if (File.Exists(fullPath)) return fullPath;
                }
                catch (Exception) { /* unusable PATH entry */ }
            }
            return null;
        }
    }
}
