using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using VoidCode.Bridge;
using VoidCode.Bridge.Handlers;
using Xunit;

namespace VoidCode.Tests;

// /10: the terminal, PTY and run routes. The routes that spawn a process
// (pty_init, run_file, run_terminal_command, shell_spawn) are covered by the UI harness and by hand;
// These tests assert what needs no shell - the answers that are pure file work, the validation, and that
// A route aimed at a session that does not exist is a no-op instead of a crash.
public class TerminalHandlersTests : IDisposable
{
    private readonly string _root;
    private readonly Recorder _recorder = new();
    private readonly BridgeRouter _router = new();
    private readonly TerminalHandlers _handlers;

    public TerminalHandlersTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "voidcode-term-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "Program.cs"), "class P { }");
        _handlers = new TerminalHandlers(() => _root, _recorder.Send);
        _handlers.Register(_router);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private Task<BridgeOutcome> Dispatch(string action, string id, string json = "{}")
        => _router.TryHandleAsync(action, id, JsonNode.Parse(json)!.AsObject());

    [Fact]
    public void TwelveRoutesAreRegistered()
    {
        var routes = _router.Actions.Count(action =>
            action.StartsWith("pty_", StringComparison.Ordinal) ||
            action.StartsWith("shell_", StringComparison.Ordinal) ||
            action is TerminalRoutes.RunFile or TerminalRoutes.RunTerminalCommand or TerminalRoutes.ListDir
                or TerminalRoutes.FsWrite or TerminalRoutes.DetectRunConfig or TerminalRoutes.KillTerminal);

        Assert.Equal(12, routes);
    }

    [Fact]
    public async Task ListDir_AnswersWithTheEntries()
    {
        await Dispatch(TerminalRoutes.ListDir, "req_1", JsonSerializer.Serialize(new { path = _root }));

        var reply = await _recorder.WaitForAsync("list_dir_response");
        Assert.Equal("req_1", reply.GetProperty("reqId").GetString());
        Assert.Equal(1, reply.GetProperty("entries").GetArrayLength());
        Assert.Equal("Program.cs", reply.GetProperty("entries")[0].GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, reply.GetProperty("error").ValueKind);
    }

    [Fact]
    public async Task ListDir_ReportsAMissingDirectoryAsAnError()
    {
        await Dispatch(TerminalRoutes.ListDir, "req_2",
            JsonSerializer.Serialize(new { path = Path.Combine(_root, "nope") }));

        var reply = await _recorder.WaitForAsync("list_dir_response");
        Assert.False(string.IsNullOrEmpty(reply.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task FsWrite_WritesTheFileAndRefreshesTheTree()
    {
        var target = Path.Combine(_root, "nested", "saved.txt");

        await Dispatch(TerminalRoutes.FsWrite, "req_3", JsonSerializer.Serialize(new { path = target, content = "hello" }));

        Assert.Equal("hello", File.ReadAllText(target));
        var reply = await _recorder.WaitForAsync("fs_write_response");
        Assert.Equal(JsonValueKind.Null, reply.GetProperty("error").ValueKind);
        await _recorder.WaitForAsync("tree_refreshed");
    }

    [Theory]
    [InlineData(TerminalRoutes.ListDir)]
    [InlineData(TerminalRoutes.FsWrite)]
    public async Task PathRoutes_RejectAPayloadWithoutAPath(string action)
    {
        var outcome = await Dispatch(action, "req_4");

        Assert.Equal(BridgeRouter.InvalidPayloadError, outcome.Error!.Code);
        Assert.Empty(_recorder.Messages);
    }
[Fact]
    public async Task DetectRunConfig_AnswersWithAConfigForTheWorkspace()
    {
        await Dispatch(TerminalRoutes.DetectRunConfig, "req_5",
            JsonSerializer.Serialize(new { activePath = Path.Combine(_root, "Program.cs") }));

        var reply = await _recorder.WaitForAsync("run_config_detected");
        Assert.Equal(JsonValueKind.Object, reply.GetProperty("config").ValueKind);
    }

    [Fact]
    public async Task PtyRoutes_ForASessionThatNeverExisted_AreQuiet()
    {
        // No pty_init in this test, so every one of these must survive an unknown id instead of throwing
        await Dispatch(TerminalRoutes.PtyInput, "req_6", JsonSerializer.Serialize(new { id = "term-9", data = "x" }));
        await Dispatch(TerminalRoutes.PtyResize, "req_7", JsonSerializer.Serialize(new { id = "term-9", cols = 80, rows = 24 }));
        await Dispatch(TerminalRoutes.PtyKill, "req_8", JsonSerializer.Serialize(new { id = "term-9" }));
        await Dispatch(TerminalRoutes.ShellKill, "req_9");

        Assert.Empty(_recorder.Messages);
    }

    [Fact]
    public async Task KillTerminal_StopsTheRunAndAnnouncesIt()
    {
        await Dispatch(TerminalRoutes.KillTerminal, "req_10", JsonSerializer.Serialize(new { id = "term-9" }));

        var reply = await _recorder.WaitForAsync("smart_run_ended");
        Assert.Equal(-1, reply.GetProperty("exitCode").GetInt32());
    }

    /// <summary>Collects what the domain sent, with a poll for the asynchronous routes.</summary>
    private sealed class Recorder
    {
        private readonly List<(string Action, JsonElement Payload)> _messages = new();
        private readonly object _gate = new();

        public void Send(string action, object payload)
        {
            var element = JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement.Clone();
            lock (_gate) { _messages.Add((action, element)); }
        }

        public IReadOnlyList<(string Action, JsonElement Payload)> Messages
        {
            get { lock (_gate) { return _messages.ToArray(); } }
        }

        public async Task<JsonElement> WaitForAsync(string action, TimeSpan? timeout = null)
        {
            var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
            while (DateTime.UtcNow < deadline)
            {
                var hit = Messages.LastOrDefault(m => m.Action == action);
                if (hit.Payload.ValueKind != JsonValueKind.Undefined) return hit.Payload;
                await Task.Delay(20);
            }
            throw new TimeoutException($"no '{action}' message was sent");
        }
    }
}