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

// The file-system domain. These cases used to be eight branches of the MainWindow
// Switch, reading node["path"] by hand; they are routes now, so the payload is validated and the
// Replies keep the shapes the page listens for (read_file_response, save_file_response, ...).
// Every test runs against a real temp directory - no file-system mock, because being testable outside
// A window is the whole point of the migration.
public class FileSystemHandlersTests : IDisposable
{
    private readonly string _root;
    private readonly Recorder _recorder = new();
    private readonly BridgeRouter _router = new();
    private readonly FileSystemHandlers _handlers;

    public FileSystemHandlersTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "voidcode-fs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _handlers = new FileSystemHandlers(() => _root, _recorder.Send);
        _handlers.Register(_router);
    }

    public void Dispose()
    {
        _handlers.CancelPending();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    private Task<BridgeOutcome> Dispatch(string action, string id, string json)
        => _router.TryHandleAsync(action, id, JsonNode.Parse(json)!.AsObject());

    [Fact]
    public async Task ReadFile_AnswersWithTheTextAndMetadata()
    {
        var path = WriteFile("notes.md", "# hello\n");

        var outcome = await Dispatch(FileSystemRoutes.ReadFile, "req_1", JsonSerializer.Serialize(new { path }));

        Assert.True(outcome.Handled);
        Assert.True(outcome.Ok);
        Assert.False(outcome.ExpectsEnvelope); // the route answers in the legacy shape itself
        var reply = await _recorder.WaitForAsync("read_file_response");
        Assert.Equal("req_1", reply.GetProperty("reqId").GetString());
        Assert.Equal("# hello\n", reply.GetProperty("content").GetString());
        Assert.True(reply.TryGetProperty("metadata", out _));
    }

    [Fact]
    public async Task ReadFile_OnAMissingFile_AnswersWithAnErrorInsteadOfThrowing()
    {
        var missing = Path.Combine(_root, "nope.txt");

        await Dispatch(FileSystemRoutes.ReadFile, "req_2", JsonSerializer.Serialize(new { path = missing }));

        var reply = await _recorder.WaitForAsync("read_file_response");
        Assert.False(string.IsNullOrEmpty(reply.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task ReadFile_WithoutAPath_IsRejectedBeforeTheFileSystemIsTouched()
    {
        var outcome = await Dispatch(FileSystemRoutes.ReadFile, "req_3", "{}");

        Assert.True(outcome.Handled);
        Assert.False(outcome.Ok);
        Assert.Equal(BridgeRouter.InvalidPayloadError, outcome.Error!.Code);
        Assert.Empty(_recorder.Messages); // nothing was read, nothing was sent
    }

    [Fact]
    public async Task SaveFile_WritesTheContentAndAnswersSuccessfully()
    {
        var path = Path.Combine(_root, "saved.txt");

        await Dispatch(FileSystemRoutes.SaveFile, "req_4", JsonSerializer.Serialize(new { path, content = "hello" }));

        var reply = await _recorder.WaitForAsync("save_file_response");
        Assert.True(reply.GetProperty("success").GetBoolean());
        Assert.Equal("hello", File.ReadAllText(path));
    }
[Fact]
    public async Task CreateEntry_MakesTheFileAndRefreshesTheTree()
    {
        var path = Path.Combine(_root, "folder", "new.cs");

        await Dispatch(FileSystemRoutes.CreateEntry, "req_5", JsonSerializer.Serialize(new { path, isDir = false }));

        Assert.True(File.Exists(path));
        var created = await _recorder.WaitForAsync("create_entry_response");
        Assert.Equal(JsonValueKind.Null, created.GetProperty("error").ValueKind);
        await _recorder.WaitForAsync("tree_refreshed");
    }

    [Fact]
    public async Task RenameAndDelete_AnswerWithTreeAndTheEntryEvents()
    {
        var original = WriteFile("old.txt", "x");
        var renamed = Path.Combine(_root, "new.txt");

        await Dispatch(FileSystemRoutes.RenameEntry, "req_6", JsonSerializer.Serialize(new { path = original, newName = "new.txt" }));
        Assert.True(File.Exists(renamed));
        await _recorder.WaitForAsync("tree_refreshed");

        await Dispatch(FileSystemRoutes.DeleteEntry, "req_7", JsonSerializer.Serialize(new { path = renamed }));
        Assert.False(File.Exists(renamed));
        var deleted = await _recorder.WaitForAsync("entry_deleted");
        Assert.Equal(renamed, deleted.GetProperty("path").GetString());
        await _recorder.WaitForAsync("delete_entry_response");
    }

    [Fact]
    public async Task DirectoryChildren_ArePagedThroughTheWorkspace()
    {
        WriteFile("a.txt", "a");
        WriteFile("b.txt", "b");

        await Dispatch(FileSystemRoutes.GetDirectoryChildren, "req_8", JsonSerializer.Serialize(new { path = _root, offset = 0 }));

        var reply = await _recorder.WaitForAsync("directory_children_response");
        Assert.Equal(_root, reply.GetProperty("path").GetString());
        Assert.True(reply.GetProperty("children").GetArrayLength() >= 2);
    }

    [Fact]
    public async Task GetAllFiles_EnumeratesTheWorkspace()
    {
        WriteFile("one.txt", "1");
        WriteFile("two.txt", "2");

        await Dispatch(FileSystemRoutes.GetAllFiles, "req_9", "{}");

        var reply = await _recorder.WaitForAsync("all_files_response");
        Assert.Equal("req_9", reply.GetProperty("reqId").GetString());
        Assert.True(reply.GetProperty("files").GetArrayLength() >= 2);
    }

    /// <summary>Collects what the domain sent, in order, with a poll for the async handlers.</summary>
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