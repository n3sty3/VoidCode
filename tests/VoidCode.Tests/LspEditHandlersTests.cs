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

// /10: the workspace-edit half of the LSP surface. The valuable test here is the
// Review round-trip, because it is the one thing the window used to own end to end: announce the edit to
// The page, park a TaskCompletionSource under a review id, finish it when the page answers, and give up
// On close. No language server is involved - the edit object is built by hand.
public class LspEditHandlersTests : IDisposable
{
    private readonly string _root;
    private readonly Recorder _recorder = new();
    private readonly BridgeRouter _router = new();
    private readonly LspEditHandlers _handlers;

    public LspEditHandlersTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "voidcode-lspedit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _handlers = new LspEditHandlers(() => _root, _recorder.Send);
        _handlers.Register(_router);
    }

    public void Dispose()
    {
        _handlers.CancelPending();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private Task<BridgeOutcome> Dispatch(string action, string id, string json = "{}")
        => _router.TryHandleAsync(action, id, JsonNode.Parse(json)!.AsObject());

    private string InsideFile => Path.Combine(_root, "Program.cs");

    /// <summary>{"changes": {"file:///…": [{range, newText}]}} - the shape LspService parses.</summary>
    private static JsonObject WorkspaceEditFor(string path) => JsonNode.Parse($$"""
        { "changes": { "{{new Uri(path).AbsoluteUri}}": [ {
            "range": { "start": { "line": 0, "character": 0 }, "end": { "line": 0, "character": 0 } },
            "newText": "x" } ] } }
        """)!.AsObject();

    [Fact]
    public void SevenRoutesAreRegistered()
    {
        var actions = new[]
        {
            LspEditRoutes.Rename, LspEditRoutes.Formatting, LspEditRoutes.CodeActions,
            LspEditRoutes.ResolveCodeAction, LspEditRoutes.ExecuteCommand,
            LspEditRoutes.ResolveWorkspaceEdit, LspEditRoutes.SearchCodebase
        };
        Assert.All(actions, action => Assert.True(_router.Contains(action), $"{action} is not registered"));
    }

    [Theory]
    [InlineData(LspEditRoutes.Rename)]
    [InlineData(LspEditRoutes.Formatting)]
    [InlineData(LspEditRoutes.CodeActions)]
    [InlineData(LspEditRoutes.ResolveCodeAction)]
    [InlineData(LspEditRoutes.ExecuteCommand)]
    [InlineData(LspEditRoutes.SearchCodebase)]
    public async Task Routes_RejectAPayloadWithoutTheirRequiredField(string action)
    {
        var outcome = await Dispatch(action, "req_0");

        Assert.Equal(BridgeRouter.InvalidPayloadError, outcome.Error!.Code);
        Assert.Empty(_recorder.Messages);
    }

    [Fact]
    public async Task ResolveWorkspaceEdit_ForAnUnknownReview_IsQuiet()
    {
        var outcome = await Dispatch(LspEditRoutes.ResolveWorkspaceEdit, "req_1",
            JsonSerializer.Serialize(new { reviewId = "never-existed", applied = true }));

        Assert.True(outcome.Handled);
        Assert.Empty(_recorder.Messages);
    }
[Fact]
    public async Task RequestReview_AnnouncesTheEditAndCompletesWhenThePageAppliesIt()
    {
        var review = _handlers.RequestReviewAsync("csharp", "Rename", WorkspaceEditFor(InsideFile));

        var announced = await _recorder.WaitForAsync("lsp_workspace_edit_review");
        var reviewId = announced.GetProperty("reviewId").GetString()!;
        Assert.Equal("Rename", announced.GetProperty("label").GetString());
        Assert.Equal(1, announced.GetProperty("fileCount").GetInt32());
        Assert.True(announced.GetProperty("edits").GetArrayLength() > 0);

        await Dispatch(LspEditRoutes.ResolveWorkspaceEdit, "req_2",
            JsonSerializer.Serialize(new { reviewId, applied = true }));

        var result = await review.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.Applied);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public async Task RequestReview_ReportsADeclineAsAFailure()
    {
        var review = _handlers.RequestReviewAsync("csharp", "Rename", WorkspaceEditFor(InsideFile));
        var announced = await _recorder.WaitForAsync("lsp_workspace_edit_review");
        var reviewId = announced.GetProperty("reviewId").GetString()!;

        await Dispatch(LspEditRoutes.ResolveWorkspaceEdit, "req_3",
            JsonSerializer.Serialize(new { reviewId, applied = false, failureReason = "nope" }));

        var result = await review.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.Applied);
        Assert.Equal("nope", result.FailureReason);
    }

    [Fact]
    public async Task RequestReview_RefusesAnEditOutsideTheWorkspaceWithoutAskingThePage()
    {
        var outside = Path.Combine(Path.GetTempPath(), "voidcode-outside-" + Guid.NewGuid().ToString("N") + ".cs");

        var result = await _handlers.RequestReviewAsync("csharp", "Rename", WorkspaceEditFor(outside));

        Assert.False(result.Applied);
        Assert.Contains("outside the workspace", result.FailureReason);
        Assert.Empty(_recorder.Messages);
    }

    [Fact]
    public async Task CancelPending_FailsEveryOpenReview()
    {
        var review = _handlers.RequestReviewAsync("csharp", "Rename", WorkspaceEditFor(InsideFile));
        await _recorder.WaitForAsync("lsp_workspace_edit_review");

        _handlers.CancelPending();

        var result = await review.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.Applied);
        Assert.Contains("closed", result.FailureReason);
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
            var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
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