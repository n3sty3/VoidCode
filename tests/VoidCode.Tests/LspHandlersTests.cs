using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using VoidCode.Bridge;
using VoidCode.Bridge.Handlers;
using Xunit;

namespace VoidCode.Tests;

// /10 (first half): the LSP requests, the document-sync notifications and the
// Restart. No language server exists in this environment, and these tests do not need one: an unmapped
// Extension (.zzz) and an empty workspace make LspService return immediately, which is exactly the
// "no server" path the page already handles.
public class LspHandlersTests
{
    private readonly Recorder _recorder = new();
    private readonly BridgeRouter _router = new();
    private readonly LspHandlers _handlers;

    public LspHandlersTests()
    {
        _handlers = new LspHandlers(() => "", _recorder.Send);
        _handlers.Register(_router);
    }

    private const string DummyFile = @"C:\nowhere\file.zzz";

    private Task<BridgeOutcome> Dispatch(string action, string id, string json)
        => _router.TryHandleAsync(action, id, JsonNode.Parse(json)!.AsObject());

    private static string Position(string path = DummyFile) => JsonSerializer.Serialize(new { path, line = 3, col = 5 });

    [Fact]
    public void ThirteenRoutesAreRegistered()
    {
        var lspActions = _router.Actions.Where(action => action.StartsWith("lsp_", StringComparison.Ordinal)).ToArray();

        Assert.Equal(13, lspActions.Length);
        Assert.Contains(LspRoutes.Hover, lspActions);
        Assert.Contains(LspRoutes.Restart, lspActions);
    }

    [Theory]
    [InlineData(LspRoutes.Hover)]
    [InlineData(LspRoutes.Definition)]
    [InlineData(LspRoutes.Completion)]
    [InlineData(LspRoutes.References)]
    [InlineData(LspRoutes.SignatureHelp)]
    [InlineData(LspRoutes.DocumentSymbols)]
    [InlineData(LspRoutes.SemanticTokens)]
    public async Task RequestRoutes_RejectAPayloadWithoutAPath(string action)
    {
        var outcome = await Dispatch(action, "req_0", "{}");

        Assert.Equal(BridgeRouter.InvalidPayloadError, outcome.Error!.Code);
        Assert.Empty(_recorder.Messages);
    }

    [Fact]
    public async Task Hover_AnswersWithTheRequestIdEvenWithoutAServer()
    {
        await Dispatch(LspRoutes.Hover, "req_1", Position());

        var reply = await _recorder.WaitForAsync("lsp_hover_response");
        Assert.Equal("req_1", reply.GetProperty("reqId").GetString());
        Assert.Equal(JsonValueKind.Null, reply.GetProperty("result").ValueKind);
    }

    [Fact]
    public async Task Completion_AnswersWithTheRequestIdAndAnItemsField()
    {
        await Dispatch(LspRoutes.Completion, "req_2", Position());

        var reply = await _recorder.WaitForAsync("lsp_completion_response");
        Assert.Equal("req_2", reply.GetProperty("reqId").GetString());
        Assert.True(reply.TryGetProperty("items", out _));
    }

    [Fact]
    public async Task DocumentSymbols_AnswersForAFileWithoutAServer()
    {
        await Dispatch(LspRoutes.DocumentSymbols, "req_3", JsonSerializer.Serialize(new { path = DummyFile }));

        var reply = await _recorder.WaitForAsync("lsp_document_symbols_response");
        Assert.Equal("req_3", reply.GetProperty("reqId").GetString());
    }

    [Fact]
    public async Task DidOpenAndDidClose_AreNotificationsAndSendNothing()
    {
        var open = await Dispatch(LspRoutes.DidOpen, "req_4",
            JsonSerializer.Serialize(new { path = DummyFile, languageId = "zzz", content = "x" }));
        var close = await Dispatch(LspRoutes.DidClose, "req_5", JsonSerializer.Serialize(new { path = DummyFile }));

        Assert.True(open.Handled);
        Assert.True(close.Handled);
        Assert.False(open.ExpectsEnvelope); // a notification never gets an envelope
        Assert.Empty(_recorder.Messages);
    }

    [Fact]
    public async Task Restart_ShutsTheServersDownAndAnnouncesIt()
    {
        await Dispatch(LspRoutes.Restart, "req_6", "{}");

        await _recorder.WaitForAsync("lsp_restarted");
    }

    /// <summary>Collects what the domain sent, with a poll for the background LSP calls.</summary>
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