using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using VoidCode.Bridge;
using Xunit;

namespace VoidCode.Tests;

// The typed bridge router. Three things must hold before the switch can shrink:
// A payload is validated instead of read by hand, an action nobody owns produces an error envelope
// Instead of silence, and a request can be cancelled while it runs.
public class BridgeRouterTests
{
    // Request types as routes see them: `required` is the validation contract.
    private sealed class Echo
    {
        public required string Text { get; init; }
    }

    private sealed class Empty
    {
    }

    private sealed class Result
    {
        public string Upper { get; init; } = "";
    }

    private static JsonObject Payload(string json) => JsonNode.Parse(json)!.AsObject();

    private static string Json(object envelope) => JsonSerializer.Serialize(envelope);

    [Fact]
    public async Task Register_DeserializesThePayloadAndReturnsData()
    {
        var router = new BridgeRouter();
        router.Register<Echo, Result>("echo", (request, _) =>
            Task.FromResult(new Result { Upper = request.Text.ToUpperInvariant() }));

        var outcome = await router.TryHandleAsync("echo", "req_1", Payload("{\"text\":\"hi\",\"extra\":1}"));

        Assert.True(outcome.Handled);
        Assert.True(outcome.Ok);
        Assert.Equal("HI", Assert.IsType<Result>(outcome.Data).Upper);
    }

    [Fact]
    public async Task BuildEnvelope_IsIdOkDataForSuccess()
    {
        var outcome = await DispatchEcho("hi");

        using var document = JsonDocument.Parse(Json(BridgeRouter.BuildEnvelope("req_1", outcome)));
        var root = document.RootElement;

        Assert.Equal("req_1", root.GetProperty("id").GetString());
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal("HI", root.GetProperty("data").GetProperty("Upper").GetString());
        Assert.False(root.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task MissingRequiredField_IsInvalidPayloadNotAnEmptyString()
    {
        var router = new BridgeRouter();
        router.Register<Echo, Result>("echo", (request, _) => Task.FromResult(new Result { Upper = request.Text }));

        var outcome = await router.TryHandleAsync("echo", "req_2", Payload("{\"other\":1}"));

        Assert.True(outcome.Handled);
        Assert.False(outcome.Ok);
        Assert.Equal(BridgeRouter.InvalidPayloadError, outcome.Error!.Code);

        // And the envelope carries the code, so the page can branch on it
        using var document = JsonDocument.Parse(Json(BridgeRouter.BuildEnvelope("req_2", outcome)));
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("invalid_payload", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task HandlerThatThrows_IsAHandlerFailedError()
    {
        var router = new BridgeRouter();
        router.Register<Empty, Result>("boom", (_, _) => throw new InvalidOperationException("kaput"));

        var outcome = await router.TryHandleAsync("boom", "req_3", Payload("{}"));

        Assert.True(outcome.Handled);
        Assert.False(outcome.Ok);
        Assert.Equal(BridgeRouter.HandlerFailedError, outcome.Error!.Code);
        Assert.Contains("kaput", outcome.Error.Message);
    }

    [Fact]
    public async Task UnknownAction_IsSkippedAndHasAnErrorEnvelopeWhenItWasARequest()
    {
        var router = new BridgeRouter();
        router.Register<Empty, Result>("known", (_, _) => Task.FromResult(new Result()));

        var outcome = await router.TryHandleAsync("not_registered", "req_4", Payload("{}"));
        Assert.False(outcome.Handled); // the legacy switch still gets its turn

        var unknown = BridgeRouter.UnknownActionOutcome("not_registered");
        using var document = JsonDocument.Parse(Json(BridgeRouter.BuildEnvelope("req_4", unknown)));
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("unknown_action", document.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("not_registered", document.RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    private static async Task<BridgeOutcome> DispatchEcho(string text)
    {
        var router = new BridgeRouter();
        router.Register<Echo, Result>("echo", (request, _) =>
            Task.FromResult(new Result { Upper = request.Text.ToUpperInvariant() }));
        return await router.TryHandleAsync("echo", "req_1", Payload($"{{\"text\":\"{text}\"}}"));
    }

    [Fact]
    public async Task CancelRequest_StopsTheRunningRouteAndReportsIt()
    {
        var router = new BridgeRouter();
        var started = new TaskCompletionSource();
        router.Register<Empty, Result>("slow", async (_, context) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, context.Cancellation);
            return new Result();
        });

        var running = router.TryHandleAsync("slow", "req_slow", Payload("{}"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var cancel = await router.TryHandleAsync(BridgeRouter.CancelAction, "req_cancel", Payload("{\"id\":\"req_slow\"}"));
        var outcome = await running.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(cancel.Ok);
        Assert.False(outcome.Ok);
        Assert.Equal(BridgeRouter.CancelledError, outcome.Error!.Code);
    }

    [Fact]
    public async Task CancelRequest_ForAFinishedOrUnknownId_ReportsFalseInsteadOfFailing()
    {
        var router = new BridgeRouter();
        router.Register<Empty, Result>("quick", (_, _) => Task.FromResult(new Result()));
        await router.TryHandleAsync("quick", "req_done", Payload("{}"));

        Assert.False(router.Cancel("req_done"));
        var cancelled = await router.TryHandleAsync(BridgeRouter.CancelAction, "req_c2", Payload("{\"id\":\"never\"}"));
        Assert.True(cancelled.Ok);

        var missingId = await router.TryHandleAsync(BridgeRouter.CancelAction, "req_c3", Payload("{}"));
        Assert.False(missingId.Ok);
        Assert.Equal(BridgeRouter.InvalidPayloadError, missingId.Error!.Code);
    }

    [Fact]
    public void Router_OwnsCancelAndRefusesDuplicateRegistrations()
    {
        var router = new BridgeRouter();
        Assert.Contains(BridgeRouter.CancelAction, router.Actions);
        Assert.True(router.Contains(BridgeRouter.CancelAction));

        router.RegisterNotification<Empty>("once", (_, _) => Task.CompletedTask);
        Assert.Contains("once", router.Actions);

        var duplicate = Assert.Throws<InvalidOperationException>(() =>
            router.RegisterNotification<Empty>("once", (_, _) => Task.CompletedTask));
        Assert.Contains("already registered", duplicate.Message);
        Assert.False(router.Contains("never-registered"));
    }

    [Fact]
    public async Task BridgeActions_RegisterTheThreeDiagnosticsNotifications()
    {
        var router = new BridgeRouter();
        BridgeActions.Register(router);

        Assert.Contains("js_log", router.Actions);
        Assert.Contains("js_error", router.Actions);
        Assert.Contains("open_external_url", router.Actions);

        // A notification with no id is handled and produces no envelope
        var log = await router.TryHandleAsync("js_log", "", Payload("{\"msg\":\"hello\",\"context\":\"test\"}"));
        Assert.True(log.Handled);
        Assert.True(log.Ok);

        // Validation: msg and url are required, so a payload that omits them is a coded error
        var missingMsg = await router.TryHandleAsync("js_log", "req_5", Payload("{\"context\":\"csp\"}"));
        Assert.Equal(BridgeRouter.InvalidPayloadError, missingMsg.Error!.Code);

        var missingUrl = await router.TryHandleAsync("open_external_url", "req_6", Payload("{}"));
        Assert.Equal(BridgeRouter.InvalidPayloadError, missingUrl.Error!.Code);
    }
}

