using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using VoidCode.Services;

namespace VoidCode.Bridge.Handlers;

/// <summary>
/// The terminal, the PTY and the "run this" actions - 12 cases.
///
/// <c>pty_init</c>, <c>pty_input</c>, <c>pty_resize</c>, <c>pty_kill</c>, <c>kill_terminal</c>,
/// <c>run_file</c>, <c>run_terminal_command</c>, <c>shell_spawn</c>, <c>shell_kill</c>,
/// <c>list_dir</c>, <c>fs_write</c> and <c>detect_run_config</c>.
///
/// The domain is the one place where the reply stream is a *callback*, not a response: <c>pty_init</c>
/// Hands <see cref="PtyManager"/> two delegates that push <c>pty_output</c> and <c>pty_exit</c> for as
/// Long as the session lives, so those are wired to the send channel here and nothing else has to know.
/// Everything else keeps its shape (<c>list_dir_response</c>, <c>fs_write_response</c>,
/// <c>shell_spawn_response</c>, <c>run_config_detected</c>, <c>terminal_command_done</c>,
/// <c>smart_run_ended</c>, the <c>tree_refreshed</c> after a write, and the "PS cwd>" echo when no PTY is
/// Alive).
/// </summary>
public sealed class TerminalHandlers
{
    private readonly Func<string?> _workspace;
    private readonly Action<string, object> _send;

    public TerminalHandlers(Func<string?> workspace, Action<string, object> send)
    {
        _workspace = workspace;
        _send = send;
    }

    public sealed class PtyInitRequest
    {
        public string? Id { get; init; }
        public int Cols { get; init; }
        public int Rows { get; init; }
        public string? ShellType { get; init; }
    }

    public sealed class PtyInputRequest
    {
        public string? Id { get; init; }
        public string? Data { get; init; }
    }

    public sealed class PtyResizeRequest
    {
        public string? Id { get; init; }
        public int Cols { get; init; }
        public int Rows { get; init; }
    }

    public sealed class PtyIdRequest
    {
        public string? Id { get; init; }
    }

    public sealed class RunFileRequest
    {
        public required string Path { get; init; }
        public string? TermId { get; init; }
    }

    public sealed class RunCommandRequest
    {
        public required string Command { get; init; }
        public string? TermId { get; init; }
    }

    public sealed class ShellSpawnRequest
    {
        public required string File { get; init; }
        public JsonArray? Args { get; init; }
        public string? Cwd { get; init; }
        public string? Stdin { get; init; }
    }

    public sealed class ListDirRequest
    {
        public required string Path { get; init; }
    }

    public sealed class FsWriteRequest
    {
        public required string Path { get; init; }
        public string? Content { get; init; }
    }

    public sealed class DetectRunConfigRequest
    {
        public string? ActivePath { get; init; }
    }

    public sealed class NoPayload
    {
    }

    public sealed class RunTaskRequest
    {
        public required string Label { get; init; }
        public string? ActiveFile { get; init; }
    }

    public void Register(BridgeRouter router)
    {
        router.Register<PtyInitRequest, object?>(TerminalRoutes.PtyInit, PtyInitAsync, envelope: false);
        router.Register<PtyInputRequest, object?>(TerminalRoutes.PtyInput, PtyInputAsync, envelope: false);
        router.Register<PtyResizeRequest, object?>(TerminalRoutes.PtyResize, PtyResizeAsync, envelope: false);
        router.Register<PtyIdRequest, object?>(TerminalRoutes.PtyKill, PtyKillAsync, envelope: false);
        router.Register<RunFileRequest, object?>(TerminalRoutes.RunFile, RunFileAsync, envelope: false);
        router.Register<RunCommandRequest, object?>(TerminalRoutes.RunTerminalCommand, RunTerminalCommandAsync, envelope: false);
        router.Register<ShellSpawnRequest, object?>(TerminalRoutes.ShellSpawn, ShellSpawnAsync, envelope: false);
        router.Register<NoPayload, object?>(TerminalRoutes.ShellKill, ShellKillAsync, envelope: false);
        router.Register<ListDirRequest, object?>(TerminalRoutes.ListDir, ListDirAsync, envelope: false);
        router.Register<FsWriteRequest, object?>(TerminalRoutes.FsWrite, FsWriteAsync, envelope: false);
        router.Register<DetectRunConfigRequest, object?>(TerminalRoutes.DetectRunConfig, DetectRunConfigAsync, envelope: false);
        router.Register<PtyIdRequest, object?>(TerminalRoutes.KillTerminal, KillTerminalAsync, envelope: false);
        router.Register<NoPayload, object?>(TerminalRoutes.ListTasks, ListTasksAsync, envelope: false);
        router.Register<RunTaskRequest, object?>(TerminalRoutes.RunTask, RunTaskAsync, envelope: false);
    }

    // ---------------------------------------------------------------- handlers (moved verbatim)

    private Task<object?> PtyInitAsync(PtyInitRequest request, BridgeContext context)
    {
        var termId = string.IsNullOrEmpty(request.Id) ? "term-1" : request.Id;
        var cols = request.Cols > 0 ? request.Cols : 80;
        var rows = request.Rows > 0 ? request.Rows : 24;
        var shellType = string.IsNullOrEmpty(request.ShellType) ? "powershell" : request.ShellType;

        var session = PtyManager.GetOrCreate(
            termId!,
            _workspace(),
            shellType!,
            cols,
            rows,
            (tId, data) => _send("pty_output", new { id = tId, data }),
            (tId, exitCode) => _send("pty_exit", new { id = tId, exitCode })
        );
        if (!session.IsAlive)
        {
            _send("pty_output", new { id = termId, data = "\r\n[Terminal failed to start. Check the selected shell and logs.]\r\n" });
        }
        return Task.FromResult<object?>(null);
    }

    private Task<object?> PtyInputAsync(PtyInputRequest request, BridgeContext context)
    {
        var termId = string.IsNullOrEmpty(request.Id) ? "term-1" : request.Id;
        PtyManager.Write(termId!, request.Data ?? "");
        return Task.FromResult<object?>(null);
    }

    private Task<object?> PtyResizeAsync(PtyResizeRequest request, BridgeContext context)
    {
        var termId = string.IsNullOrEmpty(request.Id) ? "term-1" : request.Id;
        var cols = request.Cols > 0 ? request.Cols : 80;
        var rows = request.Rows > 0 ? request.Rows : 24;
        PtyManager.Resize(termId!, cols, rows);
        return Task.FromResult<object?>(null);
    }

    private Task<object?> PtyKillAsync(PtyIdRequest request, BridgeContext context)
    {
        var termId = string.IsNullOrEmpty(request.Id) ? "term-1" : request.Id;
        PtyManager.Kill(termId!);
        return Task.FromResult<object?>(null);
    }

    private Task<object?> RunFileAsync(RunFileRequest request, BridgeContext context)
    {
        var filePath = request.Path;
        var termId = string.IsNullOrEmpty(request.TermId) ? "term-1" : request.TermId;
        if (!string.IsNullOrEmpty(filePath))
        {
            RunCommandInTerminal(TerminalService.ResolveRunCommand(filePath, _workspace()), termId!);
        }
        return Task.FromResult<object?>(null);
    }

    private Task<object?> RunTerminalCommandAsync(RunCommandRequest request, BridgeContext context)
    {
        var command = request.Command;
        var termId = string.IsNullOrEmpty(request.TermId) ? "term-1" : request.TermId;
        RunCommandInTerminal(command, termId!);
        return Task.FromResult<object?>(null);
    }

    /// <summary>The shared body of run_file / run_terminal_command: type into the PTY, or fall back.</summary>
    private void RunCommandInTerminal(string resolvedCmd, string termId)
    {
        var workspace = _workspace();
        var pty = PtyManager.Get(termId);
        if (pty != null && pty.IsAlive)
        {
            pty.Write(resolvedCmd + "\r\n");
            return;
        }

        var cwd = TerminalService.GetEffectiveWorkingDir(workspace);
        _send("pty_output", new { id = termId, data = $"\r\nPS {cwd}> {resolvedCmd}\r\n" });
        _ = Task.Run(async () =>
        {
            var result = await TerminalService.ExecuteCommandAsync(
                resolvedCmd,
                workspace,
                (line, isErr) => _send("pty_output", new { id = termId, data = line + "\r\n" })
            );
            _send("terminal_command_done", new { exitCode = result.ExitCode, cwd = result.UpdatedWorkingDir });
        });
    }

    private Task<object?> ShellSpawnAsync(ShellSpawnRequest request, BridgeContext context)
    {
        var reqId = context.Id;
        var file = request.File;
        var args = request.Args == null
            ? Array.Empty<string>()
            : request.Args.Select(a => a == null ? "" : (a.GetValue<string>() ?? "")).ToArray();
        var cwd = request.Cwd;
        var stdin = request.Stdin;

        _ = Task.Run(async () =>
        {
            var result = await TerminalService.SpawnAsync(file, args, cwd, stdin);
            _send("shell_spawn_response", new
            {
                reqId,
                exitCode = result.ExitCode,
                stdout = result.StdOut,
                stderr = result.StdErr
            });
        });
        return Task.FromResult<object?>(null);
    }

    private Task<object?> ShellKillAsync(NoPayload request, BridgeContext context)
    {
        TerminalService.KillActiveProcess();
        return Task.FromResult<object?>(null);
    }

    private Task<object?> ListDirAsync(ListDirRequest request, BridgeContext context)
    {
        var dir = request.Path;
        var entries = new List<object>();
        string? error = null;
        try
        {
            foreach (var entry in Directory.GetFileSystemEntries(dir))
            {
                var isDir = Directory.Exists(entry);
                long size = 0;
                if (!isDir)
                {
                    try { size = new FileInfo(entry).Length; } catch (Exception) { /* file vanished or is locked - leave size 0 */ }
                }
                entries.Add(new { name = Path.GetFileName(entry), path = entry, isDir, size });
            }
        }
        catch (Exception ex) { error = ex.Message; }

        _send("list_dir_response", new { reqId = context.Id, path = dir, entries, error });
        return Task.FromResult<object?>(null);
    }

    private Task<object?> FsWriteAsync(FsWriteRequest request, BridgeContext context)
    {
        var path = request.Path;
        var content = request.Content ?? "";
        string? error = null;
        try
        {
            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent)) Directory.CreateDirectory(parent!);
            File.WriteAllText(path, content, new System.Text.UTF8Encoding(false));
            var workspace = _workspace();
            if (!string.IsNullOrEmpty(workspace))
            {
                var tree = FileSystemService.GetDirectoryTree(workspace!);
                _send("tree_refreshed", new { tree });
            }
        }
        catch (Exception ex) { error = ex.Message; }

        _send("fs_write_response", new { reqId = context.Id, path, error });
        return Task.FromResult<object?>(null);
    }

    private Task<object?> DetectRunConfigAsync(DetectRunConfigRequest request, BridgeContext context)
    {
        var config = TerminalService.DetectProjectRunConfig(_workspace(), request.ActivePath);
        _send("run_config_detected", new { config });
        return Task.FromResult<object?>(null);
    }

    private Task<object?> KillTerminalAsync(PtyIdRequest request, BridgeContext context)
    {
        var termId = string.IsNullOrEmpty(request.Id) ? "term-1" : request.Id;
        var pty = PtyManager.Get(termId!);
        if (pty != null && pty.IsAlive)
        {
            pty.Write("\x03"); // Send SIGINT / Ctrl+C
        }
        TerminalService.KillActiveProcess();
        _send("smart_run_ended", new { exitCode = -1 });
        return Task.FromResult<object?>(null);
    }

    private Task<object?> ListTasksAsync(NoPayload request, BridgeContext context)
    {
        var workspace = _workspace() ?? "";
        var tasks = TaskRunnerService.LoadTasks(workspace);
        _send("tasks_list_response", new { workspace, tasks });
        return Task.FromResult<object?>(null);
    }

    private Task<object?> RunTaskAsync(RunTaskRequest request, BridgeContext context)
    {
        var workspace = _workspace() ?? "";
        var tasks = TaskRunnerService.LoadTasks(workspace);
        var task = tasks.FirstOrDefault(t => string.Equals(t.Label, request.Label, StringComparison.OrdinalIgnoreCase));
        if (task == null)
        {
            _send("toast", new { type = "error", message = $"Task '{request.Label}' was not found." });
            return Task.FromResult<object?>(null);
        }

        _ = Task.Run(async () =>
        {
            var result = await TaskRunnerService.RunTaskAsync(
                workspace,
                task,
                request.ActiveFile,
                (text, isErr) => _send("terminal_stream", new { text, isError = isErr }));

            if (!string.IsNullOrEmpty(result.Error))
            {
                _send("toast", new { type = "error", message = result.Error });
            }
            if (result.Problems.Count > 0)
            {
                _send("task_problems", new { label = result.Label, problems = result.Problems });
            }
            _send("task_run_completed", result);
        });
        return Task.FromResult<object?>(null);
    }
}

/// <summary>The action names of the terminal/run domain.</summary>
public static class TerminalRoutes
{
    public const string PtyInit = "pty_init";
    public const string PtyInput = "pty_input";
    public const string PtyResize = "pty_resize";
    public const string PtyKill = "pty_kill";
    public const string RunFile = "run_file";
    public const string RunTerminalCommand = "run_terminal_command";
    public const string ShellSpawn = "shell_spawn";
    public const string ShellKill = "shell_kill";
    public const string ListDir = "list_dir";
    public const string FsWrite = "fs_write";
    public const string DetectRunConfig = "detect_run_config";
    public const string KillTerminal = "kill_terminal";
    public const string ListTasks = "list_tasks";
    public const string RunTask = "run_task";
}