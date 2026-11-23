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

// /10: the test runner and the debugger. The routes that launch something
// (debug_start, the session controls) are covered by the UI harness and by hand, because starting a
// Child process from the test host would be slow and machine dependent. What is pinned here is the
// Behaviour that has to stay exact: runner detection, the run identity (started -> completed for the
// Same id, and a cancel that matches it), the breakpoint store the page reads back, and the
// Required-field validation that replaced the old `?? ""` defaults.
public class TestsDebugHandlersTests : IDisposable
{
    private readonly string _root;
    private readonly Recorder _recorder = new();
    private readonly BridgeRouter _router = new();
    private readonly TestsDebugHandlers _handlers;

    public TestsDebugHandlersTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "voidcode-testsdbg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _handlers = new TestsDebugHandlers(() => _root, _recorder.Send);
        _handlers.Register(_router);
    }

    public void Dispose()
    {
        // The breakpoint store is static, so leave nothing behind for the other test classes
        DebugService.ClearBreakpoints(Path.Combine(_root, "a.cs"));
        DebugService.ClearBreakpoints(Path.Combine(_root, "b.cs"));
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private Task<BridgeOutcome> Dispatch(string action, string id, string json = "{}")
        => _router.TryHandleAsync(action, id, JsonNode.Parse(json)!.AsObject());

    [Fact]
    public void FifteenRoutesAreRegistered()
    {
        var routes = _router.Actions.Count(action =>
            action.StartsWith("debug_", StringComparison.Ordinal) ||
            action is TestsDebugRoutes.DetectTestRunner or TestsDebugRoutes.RunTests or TestsDebugRoutes.CancelTests);

        Assert.Equal(16, routes);
        Assert.Contains(TestsDebugRoutes.DebugListConfigs, _router.Actions);
    }

    [Fact]
    public async Task DetectTestRunner_InAnEmptyWorkspace_SaysThereIsNoRunner()
    {
        await Dispatch(TestsDebugRoutes.DetectTestRunner, "req_1");

        var reply = await _recorder.WaitForAsync("test_runner_detected");
        Assert.False(reply.GetProperty("hasRunner").GetBoolean());
        Assert.Equal("None", reply.GetProperty("runner").GetString());
        Assert.Equal("", reply.GetProperty("command").GetString());
    }

    [Fact]
    public async Task DetectTestRunner_FindsTheDotNetRunnerFromACsproj()
    {
        File.WriteAllText(Path.Combine(_root, "App.csproj"), "<Project />");

        await Dispatch(TestsDebugRoutes.DetectTestRunner, "req_2");

        var reply = await _recorder.WaitForAsync("test_runner_detected");
        Assert.True(reply.GetProperty("hasRunner").GetBoolean());
        Assert.Equal("dotnet test", reply.GetProperty("runner").GetString());
        Assert.StartsWith("dotnet test", reply.GetProperty("command").GetString());
    }

    [Fact]
    public async Task RunTests_WithoutARunner_StartsAndCompletesTheSameRun()
    {
        await Dispatch(TestsDebugRoutes.RunTests, "req_3");

        var started = await _recorder.WaitForAsync("test_run_started");
        var runId = started.GetProperty("runId").GetString();
        Assert.StartsWith("test_run_", runId);

        var completed = await _recorder.WaitForAsync("test_run_completed");
        Assert.Equal(runId, completed.GetProperty("runId").GetString());
        Assert.Equal("None", completed.GetProperty("runner").GetString());
    }

    [Fact]
    public async Task CancelTests_WithNothingRunning_StillAnnouncesTheCancel()
    {
        await Dispatch(TestsDebugRoutes.CancelTests, "req_4");

        var reply = await _recorder.WaitForAsync("test_run_cancelled");
        Assert.Equal(JsonValueKind.Null, reply.GetProperty("runId").ValueKind);
    }
[Fact]
    public async Task ToggleBreakpoint_AddsThenRemovesTheLine()
    {
        var file = Path.Combine(_root, "a.cs");
        var payload = JsonSerializer.Serialize(new { file, line = 7 });

        await Dispatch(TestsDebugRoutes.DebugToggleBreakpoint, "req_5", payload);
        var afterAdd = DebugService.GetBreakpoints(file);
        Assert.Single(afterAdd);
        Assert.Equal(7, afterAdd[0].Line);

        await Dispatch(TestsDebugRoutes.DebugToggleBreakpoint, "req_6", payload);
        Assert.Empty(DebugService.GetBreakpoints(file));
    }

    [Fact]
    public async Task SetBreakpoints_ReplacesTheListAndDropsLineZero()
    {
        var file = Path.Combine(_root, "a.cs");
        var payload = JsonSerializer.Serialize(new
        {
            file,
            breakpoints = new object[]
            {
                new { line = 0 },
                new { line = 12, condition = "x > 1", logMessage = (string?)null, enabled = (bool?)null },
            }
        });

        await Dispatch(TestsDebugRoutes.DebugSetBreakpoints, "req_7", payload);

        var stored = DebugService.GetBreakpoints(file);
        Assert.Single(stored);
        Assert.Equal(12, stored[0].Line);
        Assert.Equal("x > 1", stored[0].Condition);
        Assert.True(stored[0].Enabled);
    }

    [Fact]
    public async Task ClearBreakpoints_LeavesTheOtherFilesAlone()
    {
        var first = Path.Combine(_root, "a.cs");
        var second = Path.Combine(_root, "b.cs");
        await Dispatch(TestsDebugRoutes.DebugSetBreakpoints, "req_8",
            JsonSerializer.Serialize(new { file = first, breakpoints = new object[] { new { line = 3 } } }));
        await Dispatch(TestsDebugRoutes.DebugSetBreakpoints, "req_9",
            JsonSerializer.Serialize(new { file = second, breakpoints = new object[] { new { line = 4 } } }));

        await Dispatch(TestsDebugRoutes.DebugClearBreakpoints, "req_10", JsonSerializer.Serialize(new { file = first }));

        Assert.Empty(DebugService.GetBreakpoints(first));
        Assert.Single(DebugService.GetBreakpoints(second));
    }

    [Fact]
    public async Task GetVariables_ForAnUnknownReference_AnswersWithAnEmptyList()
    {
        await Dispatch(TestsDebugRoutes.DebugGetVariables, "req_11",
            JsonSerializer.Serialize(new { variablesReference = 0 }));

        var reply = await _recorder.WaitForAsync("debug_variables_response");
        Assert.Equal(0, reply.GetProperty("variablesReference").GetInt32());
        Assert.Equal(0, reply.GetProperty("variables").GetArrayLength());
    }

    [Theory]
    [InlineData(TestsDebugRoutes.DebugStart)]
    [InlineData(TestsDebugRoutes.DebugToggleBreakpoint)]
    [InlineData(TestsDebugRoutes.DebugSetBreakpoints)]
    [InlineData(TestsDebugRoutes.DebugEvaluate)]
    public async Task RoutesWithARequiredField_RejectAnEmptyPayload(string action)
    {
        var outcome = await Dispatch(action, "req_12");

        Assert.Equal(BridgeRouter.InvalidPayloadError, outcome.Error!.Code);
        Assert.Empty(_recorder.Messages);
    }

    /// <summary>Collects what the domain sent, with a poll for the background work.</summary>
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