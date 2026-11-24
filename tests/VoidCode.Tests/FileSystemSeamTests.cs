using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using VoidCode.Bridge;
using VoidCode.Bridge.Handlers;
using VoidCode.Services;
using Xunit;

namespace VoidCode.Tests;

// The filesystem seam exists to exercise what a real disk will not do on demand - a save
// That hits a locked file, a rename that collides, a tree that cannot be read. Nothing here touches the
// Disk: the workspace path is fake and every call is answered by the fake below.
public class FileSystemSeamTests
{
    private static readonly string Workspace = Path.Combine(Path.GetTempPath(), "fake-workspace");
    private static string WsFile(string name) => Path.Combine(Workspace, name);
    private readonly Recorder _recorder = new();
    private readonly FakeFileSystem _files = new();
    private readonly BridgeRouter _router = new();

    public FileSystemSeamTests()
    {
        var handlers = new FileSystemHandlers(() => Workspace, _recorder.Send, _files);
        handlers.Register(_router);
    }

    private Task<BridgeOutcome> Dispatch(string action, string id, object payload)
        => _router.TryHandleAsync(action, id, JsonNode.Parse(JsonSerializer.Serialize(payload))!.AsObject());

    [Fact]
    public async Task Save_WhenTheDiskRefuses_RepliesWithTheErrorAndNoCheckpoint()
    {
        _files.Exists = true;
        _files.Text = "same content";
        _files.WriteThrows = new IOException("The process cannot access the file.");

        await Dispatch(FileSystemRoutes.SaveFile, "req_1", new { path = WsFile("a.cs"), content = "same content" });

        var reply = await _recorder.WaitForAsync("save_file_response");
        Assert.Contains("cannot access the file", reply.GetProperty("error").GetString());
        Assert.False(reply.TryGetProperty("success", out _));
        Assert.Equal(0, _files.Checkpoints);           // identical content: no checkpoint was needed
    }

    [Fact]
    public async Task Save_WhenContentChanged_TakesACheckpointBeforeWriting()
    {
        _files.Exists = true;
        _files.Text = "old";

        await Dispatch(FileSystemRoutes.SaveFile, "req_2", new { path = WsFile("a.cs"), content = "new" });

        var reply = await _recorder.WaitForAsync("save_file_response");
        Assert.True(reply.GetProperty("success").GetBoolean());
        Assert.Equal("cp_1", reply.GetProperty("checkpointId").GetString());
        Assert.Equal(new[] { "checkpoint:Saved", "write:a.cs:new" }, _files.Calls.ToArray());
    }

    [Fact]
    public async Task Rename_WhenTheTargetExists_ToastsTheFailureAndRefreshesNothing()
    {
        _files.RenameThrows = new IOException("Cannot create a file when that file already exists.");

        await Dispatch(FileSystemRoutes.RenameEntry, "req_3",
            new { path = WsFile("a.cs"), newName = "b.cs" });

        var toast = await _recorder.WaitForAsync("toast");
        Assert.Equal("error", toast.GetProperty("type").GetString());
        Assert.Contains("Rename failed", toast.GetProperty("message").GetString());
        Assert.DoesNotContain(_recorder.Messages, m => m.Action == "tree_refreshed");
    }

    [Fact]
    public async Task CreateEntry_ForAFile_SendsTheTreeFromTheSeam()
    {
        _files.Tree = new FileNode { Name = "workspace", Path = Workspace, IsDirectory = true };

        await Dispatch(FileSystemRoutes.CreateEntry, "req_4", new { path = WsFile("b.cs") });

        var tree = await _recorder.WaitForAsync("tree_refreshed");
        Assert.Equal("workspace", tree.GetProperty("tree").GetProperty("name").GetString());
        var created = await _recorder.WaitForAsync("create_entry_response");
        Assert.Equal(JsonValueKind.Null, created.GetProperty("error").ValueKind);
        Assert.Contains("create:b.cs", _files.Calls);
    }

    [Fact]
    public async Task ReadFile_ForAnImage_UsesTheSeamDataUriInsteadOfTheDisk()
    {
        _files.ImageFile = true;
        _files.DataUri = "data:image/png;base64,AAAA";
        _files.ImageBytes = 4;

        await Dispatch(FileSystemRoutes.ReadFile, "req_5", new { path = WsFile("logo.png") });

        var reply = await _recorder.WaitForAsync("image_preview_response");
        Assert.Equal("data:image/png;base64,AAAA", reply.GetProperty("dataUri").GetString());
        Assert.Equal(4, reply.GetProperty("sizeBytes").GetInt64());
        Assert.Empty(_files.Calls);   // decoding an image is the seam's job, not a write path
    }

    [Fact]
    public async Task ReadFile_WhenTheReadThrows_SendsTheErrorInTheLegacyShape()
    {
        _files.ReadThrows = new UnauthorizedAccessException("Access to the path is denied.");

        await Dispatch(FileSystemRoutes.ReadFile, "req_6", new { path = WsFile("secret.txt") });

        var reply = await _recorder.WaitForAsync("read_file_response");
        Assert.Contains("denied", reply.GetProperty("error").GetString());
        Assert.False(reply.TryGetProperty("content", out _));
    }

    [Fact]
    public void FileSystemRoutes_AreRegisteredThroughTheSeam()
    {
        Assert.Equal(8, _router.Actions.Count(a => a is FileSystemRoutes.ReadFile
            or FileSystemRoutes.ReadTextFile or FileSystemRoutes.SaveFile or FileSystemRoutes.CreateEntry
            or FileSystemRoutes.RenameEntry or FileSystemRoutes.DeleteEntry or FileSystemRoutes.GetAllFiles
            or FileSystemRoutes.GetDirectoryChildren));
    }

    /// <summary>Collects what the domain sent, same shape as the other handler tests.</summary>
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