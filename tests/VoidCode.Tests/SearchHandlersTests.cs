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

// /10: the three search routes. The interesting part is the cancellation
// Hand-over - the token source used to be a MainWindow field, so after this move the window must not
// Be able to leave a disposed token behind; the tests below search twice in a row and replace after a
// Cancel to prove that.
public class SearchHandlersTests : IDisposable
{
    private readonly string _root;
    private readonly Recorder _recorder = new();
    private readonly BridgeRouter _router = new();
    private readonly SearchHandlers _handlers;

    public SearchHandlersTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "voidcode-search-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "Program.cs"), "class Alpha { }\n// Alpha again\n");
        File.WriteAllText(Path.Combine(_root, "notes.md"), "# alpha in lower case\n");

        _handlers = new SearchHandlers(() => _root, _recorder.Send);
        _handlers.Register(_router);
    }

    public void Dispose()
    {
        _handlers.CancelPending();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private Task<BridgeOutcome> Dispatch(string action, string id, string json)
        => _router.TryHandleAsync(action, id, JsonNode.Parse(json)!.AsObject());

    [Fact]
    public async Task SearchWorkspace_AnswersWithTheFilesAndTheRequestId()
    {
        await Dispatch(SearchRoutes.SearchWorkspace, "req_1", JsonSerializer.Serialize(new { query = "Alpha" }));

        var reply = await _recorder.WaitForAsync("search_workspace_response");
        Assert.Equal("req_1", reply.GetProperty("searchId").GetString());
        Assert.Equal(2, reply.GetProperty("files").GetArrayLength());
    }

    [Fact]
    public async Task SearchWorkspace_UsesThePagesOwnSearchIdWhenItSendsOne()
    {
        await Dispatch(SearchRoutes.SearchWorkspace, "req_2", JsonSerializer.Serialize(new { query = "Alpha", searchId = "panel-7" }));

        var reply = await _recorder.WaitForAsync("search_workspace_response");
        Assert.Equal("panel-7", reply.GetProperty("searchId").GetString());
    }

    [Fact]
    public async Task SearchWorkspace_HonoursMatchCase()
    {
        await Dispatch(SearchRoutes.SearchWorkspace, "req_3", JsonSerializer.Serialize(new { query = "Alpha", matchCase = true }));

        var reply = await _recorder.WaitForAsync("search_workspace_response");
        var files = reply.GetProperty("files");
        Assert.Equal(1, files.GetArrayLength());
        Assert.EndsWith("Program.cs", files[0].GetProperty("fullPath").GetString());
    }

    [Fact]
    public async Task SearchWorkspace_WithoutAQuery_IsRejectedBeforeTheDiskIsTouched()
    {
        var outcome = await Dispatch(SearchRoutes.SearchWorkspace, "req_4", "{}");

        Assert.Equal(BridgeRouter.InvalidPayloadError, outcome.Error!.Code);
        Assert.Empty(_recorder.Messages);
    }
[Fact]
    public async Task CancelSearch_ThenAnotherSearch_StillAnswers()
    {
        await Dispatch(SearchRoutes.CancelSearch, "req_5", "{}");   // nothing running: still handled
        _handlers.CancelPending();                                  // and the window's close path

        await Dispatch(SearchRoutes.SearchWorkspace, "req_6", JsonSerializer.Serialize(new { query = "Alpha" }));

        var reply = await _recorder.WaitForAsync("search_workspace_response");
        Assert.Equal("req_6", reply.GetProperty("searchId").GetString());
        Assert.Equal(2, reply.GetProperty("files").GetArrayLength());
    }

    [Fact]
    public async Task ReplaceWorkspace_RewritesTheFilesAndRefreshesTheTree()
    {
        await Dispatch(SearchRoutes.ReplaceWorkspace, "req_7", JsonSerializer.Serialize(new { query = "Alpha", replaceText = "Beta" }));

        var reply = await _recorder.WaitForAsync("replace_workspace_response");
        Assert.True(reply.GetProperty("replacedCount").GetInt32() >= 2);
        Assert.Contains("Beta", File.ReadAllText(Path.Combine(_root, "Program.cs")));
        Assert.DoesNotContain("Alpha", File.ReadAllText(Path.Combine(_root, "Program.cs")));
        await _recorder.WaitForAsync("tree_refreshed");
    }

    [Fact]
    public async Task ReplaceWorkspace_WithoutAQuery_IsRejected()
    {
        var outcome = await Dispatch(SearchRoutes.ReplaceWorkspace, "req_8", JsonSerializer.Serialize(new { replaceText = "x" }));

        Assert.Equal(BridgeRouter.InvalidPayloadError, outcome.Error!.Code);
        Assert.Empty(_recorder.Messages);
    }

    /// <summary>Collects what the domain sent, with a poll for the background searches.</summary>
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