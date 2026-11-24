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

// The session domain's checkpoint routes now go through IFileSystem too, so the
// Restore path can be driven without a real checkpoint store - which was the last thing in that domain
// That needed a disk (and %APPDATA%, via FileSystemService's checkpoint persistence).
public class SessionCheckpointSeamTests
{
    private static readonly string Workspace = Path.Combine(Path.GetTempPath(), "fake-workspace");
    private static string WsFile(string name) => Path.Combine(Workspace, name);
    private readonly Recorder _recorder = new();
    private readonly FakeFileSystem _files = new();
    private readonly BridgeRouter _router = new();

    public SessionCheckpointSeamTests()
    {
        var handlers = new SessionWindowHandlers(
            () => Workspace,
            () => new AppSettings(),
            _ => { },
            new SessionWindowHandlers.WindowChrome(() => { }, () => { }, () => { }, () => { }),
            _recorder.Send,
            _ => { },
            _files);
        handlers.Register(_router);
    }

    private Task<BridgeOutcome> Dispatch(string action, string id, object payload)
        => _router.TryHandleAsync(action, id, JsonNode.Parse(JsonSerializer.Serialize(payload))!.AsObject());

    [Fact]
    public async Task GetCheckpointDiff_UsesTheSeamForTheBeforeAndAfter()
    {
        _files.Checkpoint = new FileCheckpoint
        {
            Id = "cp_1",
            FilePath = @"C:\fake\after.cs",
            PreviousContent = "class Before { }"
        };
        _files.Exists = true;
        _files.RestoredContent = "class After { }";

        await Dispatch(SessionWindowRoutes.GetCheckpointDiff, "req_1", new { checkpointId = "cp_1" });

        var reply = await _recorder.WaitForAsync("checkpoint_diff_response");
        Assert.Equal("cp_1", reply.GetProperty("checkpointId").GetString());
        Assert.Equal(@"C:\fake\after.cs", reply.GetProperty("path").GetString());
        Assert.Equal("class Before { }", reply.GetProperty("beforeContent").GetString());
        Assert.Equal("class After { }", reply.GetProperty("afterContent").GetString());
        Assert.Contains("get-checkpoint:cp_1", _files.Calls);
    }

    [Fact]
    public async Task GetCheckpointDiff_ForAFileThatIsGone_SendsAnEmptyAfterContent()
    {
        _files.Checkpoint = new FileCheckpoint { Id = "cp_2", FilePath = @"C:\fake\gone.cs", PreviousContent = "old" };
        _files.Exists = false;

        await Dispatch(SessionWindowRoutes.GetCheckpointDiff, "req_2", new { checkpointId = "cp_2" });

        var reply = await _recorder.WaitForAsync("checkpoint_diff_response");
        Assert.Equal("old", reply.GetProperty("beforeContent").GetString());
        Assert.Equal("", reply.GetProperty("afterContent").GetString());
    }

    [Fact]
    public async Task RestoreCheckpoint_WhenTheSeamRestores_SendsTheTreeAndTheContent()
    {
        _files.Checkpoint = new FileCheckpoint { Id = "cp_3", FilePath = @"C:\fake\workspace\a.cs", PreviousContent = "old" };
        _files.Tree = new FileNode { Name = "restored-tree", IsDirectory = true };
        _files.Exists = true;
        _files.RestoredContent = "old";

        await Dispatch(SessionWindowRoutes.RestoreCheckpoint, "req_3", new { checkpointId = "cp_3" });

        var restored = await _recorder.WaitForAsync("checkpoint_restored");
        Assert.True(restored.GetProperty("exists").GetBoolean());
        Assert.Equal("old", restored.GetProperty("content").GetString());
        var tree = await _recorder.WaitForAsync("tree_refreshed");
        Assert.Equal("restored-tree", tree.GetProperty("tree").GetProperty("name").GetString());
        Assert.Contains("restore-checkpoint:cp_3", _files.Calls);
    }

    [Fact]
    public async Task RestoreCheckpoint_WhenTheSeamCannotRestore_ToastsAndRefreshesNothing()
    {
        _files.Checkpoint = null;

        await Dispatch(SessionWindowRoutes.RestoreCheckpoint, "req_4", new { checkpointId = "cp_gone" });

        var toast = await _recorder.WaitForAsync("toast");
        Assert.Equal("error", toast.GetProperty("type").GetString());
        Assert.Contains("Checkpoint no longer available", toast.GetProperty("message").GetString());
        Assert.DoesNotContain(_recorder.Messages, m => m.Action == "checkpoint_restored");
        Assert.DoesNotContain(_recorder.Messages, m => m.Action == "tree_refreshed");
    }

    [Fact]
    public async Task InspectFile_UsesTheSeamForTheBinaryReport()
    {
        var appExe = WsFile("app.exe");
        await Dispatch(SessionWindowRoutes.InspectFile, "req_5", new { path = appExe });

        var reply = await _recorder.WaitForAsync("inspect_file_response");
        Assert.Equal("binary report", reply.GetProperty("report").GetString());
        Assert.Equal(appExe, reply.GetProperty("path").GetString());
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