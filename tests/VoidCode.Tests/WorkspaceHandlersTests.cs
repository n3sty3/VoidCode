using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using VoidCode.Bridge;
using VoidCode.Bridge.Handlers;
using VoidCode.Services;
using Xunit;

namespace VoidCode.Tests;

// /10: the workspace routes. Seven MainWindow cases that used to reach into
// Window state now take it as arguments - the settings object, the current folder, a "switch to this
// Folder" callback and the two dialogs - which is what makes them testable without a window.
public class WorkspaceHandlersTests : IDisposable
{
    private readonly string _root;
    private readonly Recorder _recorder = new();
    private readonly List<string> _activated = new();
    private readonly Queue<string?> _folders = new();
    private readonly Queue<string[]> _filePicks = new();
    private readonly AppSettings _settings = new();
    private readonly BridgeRouter _router = new();
    private readonly WorkspaceHandlers _handlers;

    public WorkspaceHandlersTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "voidcode-ws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "Program.cs"), "class P { }");
        File.WriteAllText(Path.Combine(_root, "notes.md"), "# notes");

        _handlers = new WorkspaceHandlers(
            () => _settings,
            () => _root,
            _recorder.Send,
            folder => _activated.Add(folder),
            promptFolder: () => _folders.Count > 0 ? _folders.Dequeue() : null,
            promptFiles: _ => _filePicks.Count > 0 ? _filePicks.Dequeue() : Array.Empty<string>());
        _handlers.Register(_router);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private Task<BridgeOutcome> Dispatch(string action, string id, string json)
        => _router.TryHandleAsync(action, id, JsonNode.Parse(json)!.AsObject());

    [Fact]
    public async Task GetInitState_AnswersWithSettingsWorkspaceTreeSessionsAndRecovery()
    {
        await Dispatch(WorkspaceRoutes.GetInitState, "req_1", "{}");

        var reply = await _recorder.WaitForAsync("init_state_response");
        Assert.Equal("req_1", reply.GetProperty("reqId").GetString());
        Assert.Equal(_root, reply.GetProperty("workspace").GetString());
        Assert.True(reply.GetProperty("tree").GetProperty("isDirectory").GetBoolean());
        foreach (var field in new[] { "settings", "sessions", "checkpoints", "recovery" })
        {
            Assert.True(reply.TryGetProperty(field, out _), $"init_state_response is missing {field}");
        }
        // The bootstrap also remembers the folder it booted into
        Assert.Equal(_root, _settings.RecentWorkspaces.First());
    }

    [Fact]
    public async Task RefreshWorkspace_SendsTheTree()
    {
        await Dispatch(WorkspaceRoutes.RefreshWorkspace, "req_2", "{}");

        var reply = await _recorder.WaitForAsync("tree_refreshed");
        Assert.True(reply.GetProperty("tree").GetProperty("isDirectory").GetBoolean());
    }

    [Fact]
    public async Task SelectWorkspacePath_ActivatesAnExistingFolder()
    {
        var other = Path.Combine(_root, "second");
        Directory.CreateDirectory(other);

        await Dispatch(WorkspaceRoutes.SelectWorkspacePath, "req_3", JsonSerializer.Serialize(new { path = other }));

        Assert.Equal(other, Assert.Single(_activated));
        Assert.DoesNotContain(_recorder.Messages, m => m.Action == "toast");
    }

    [Fact]
    public async Task SelectWorkspacePath_RefusesAMissingFolderWithAToast()
    {
        var missing = Path.Combine(_root, "nope");

        await Dispatch(WorkspaceRoutes.SelectWorkspacePath, "req_4", JsonSerializer.Serialize(new { path = missing }));

        Assert.Empty(_activated);
        var toast = await _recorder.WaitForAsync("toast");
        Assert.Equal("error", toast.GetProperty("type").GetString());
        Assert.Contains("does not exist", toast.GetProperty("message").GetString());
    }

    [Fact]
    public async Task SelectWorkspacePath_WithoutAPath_IsRejectedBeforeAnythingRuns()
    {
        var outcome = await Dispatch(WorkspaceRoutes.SelectWorkspacePath, "req_5", "{}");

        Assert.Equal(BridgeRouter.InvalidPayloadError, outcome.Error!.Code);
        Assert.Empty(_activated);
        Assert.Empty(_recorder.Messages);
    }
[Fact]
    public async Task OpenWorkspaceDialog_ActivatesTheFolderThePickerReturned()
    {
        var picked = Path.Combine(_root, "picked");
        Directory.CreateDirectory(picked);
        _folders.Enqueue(picked);

        await Dispatch(WorkspaceRoutes.OpenWorkspaceDialog, "req_6", "{}");

        Assert.Equal(picked, Assert.Single(_activated));
    }

    [Fact]
    public async Task OpenWorkspaceDialog_DoesNothingWhenThePickerIsCancelled()
    {
        _folders.Enqueue(null);

        await Dispatch(WorkspaceRoutes.OpenWorkspaceDialog, "req_7", "{}");

        Assert.Empty(_activated);
    }

    [Fact]
    public async Task CreateProjectFolder_MakesTheFolderAndActivatesIt()
    {
        _folders.Enqueue(_root);

        await Dispatch(WorkspaceRoutes.CreateProjectFolder, "req_8", JsonSerializer.Serialize(new { name = "Fresh" }));

        var created = Path.Combine(_root, "Fresh");
        Assert.True(Directory.Exists(created));
        Assert.Equal(created, Assert.Single(_activated));
    }

    [Fact]
    public async Task OpenFileDialog_ReportsThePickedFilesUnderTheirTarget()
    {
        var file = Path.Combine(_root, "Program.cs");
        _filePicks.Enqueue(new[] { file });

        await Dispatch(WorkspaceRoutes.OpenFileDialog, "req_9", JsonSerializer.Serialize(new { target = "chat" }));

        var reply = await _recorder.WaitForAsync("files_selected");
        Assert.Equal("chat", reply.GetProperty("target").GetString());
        Assert.Equal(file, reply.GetProperty("files")[0].GetString());
    }

    [Fact]
    public async Task OpenFileDialog_StaysQuietWhenNothingIsPicked()
    {
        _filePicks.Enqueue(Array.Empty<string>());

        await Dispatch(WorkspaceRoutes.OpenFileDialog, "req_10", "{}");

        Assert.DoesNotContain(_recorder.Messages, m => m.Action == "files_selected");
    }

    [Fact]
    public async Task RevealInExplorer_WithAnEmptyPathDoesNothing()
    {
        // A real path would launch Explorer, so this test only covers the guard
        await Dispatch(WorkspaceRoutes.RevealInExplorer, "req_11", JsonSerializer.Serialize(new { path = "" }));

        Assert.Empty(_recorder.Messages);
    }

    /// <summary>Collects what the domain sent, with a poll for anything asynchronous.</summary>
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
            var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
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