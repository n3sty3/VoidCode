using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using VoidCode.Dap;
using VoidCode.Services;

namespace VoidCode.Bridge.Handlers;

/// <summary>
/// The test runner and the debugger - 15 cases.
///
/// <c>detect_test_runner</c>, <c>run_tests</c>, <c>cancel_tests</c>, the seven session controls
/// (<c>debug_start</c>, <c>debug_stop</c>, <c>debug_pause</c>, <c>debug_continue</c>,
/// <c>debug_step_over</c>, <c>debug_step_into</c>, <c>debug_step_out</c>), the three breakpoint actions
/// (<c>debug_toggle_breakpoint</c>, <c>debug_set_breakpoints</c>, <c>debug_clear_breakpoints</c>) and the
/// Two reads (<c>debug_get_variables</c>, <c>debug_evaluate</c>).
///
/// None of them answers a request: the page listens for named events (<c>test_runner_detected</c>,
/// <c>test_run_started</c>, <c>test_output_line</c>, <c>test_run_completed</c>,
/// <c>test_run_cancelled</c>, <c>debug_variables_response</c>, <c>debug_evaluate_response</c>) and the
/// Rest of the debugger stream is <see cref="DebugService"/>'s own callback, wired once in the window.
/// Every route is therefore registered with <c>envelope: false</c>.
///
/// The domain also takes over the test-run identity managed for this domain
/// (<c>_testRunSequence</c> and <c>_activeTestRunId</c>): each run gets a fresh id, and a completion is
/// Only published while that run is still the active one - which is what makes "cancel, then start
/// Another run" safe.
/// </summary>
public sealed class TestsDebugHandlers
{
    private readonly Func<string?> _workspace;
    private readonly Action<string, object> _send;
    private long _testRunSequence;
    private string? _activeTestRunId;

    public TestsDebugHandlers(Func<string?> workspace, Action<string, object> send)
    {
        _workspace = workspace;
        _send = send;
    }

    /// <summary>An action that carries nothing the host needs (detect/cancel tests, debug stop and steps).</summary>
    public sealed class NoPayload
    {
    }

    public sealed class RunTestsRequest
    {
        /// <summary>Optional: a dotnet test filter (the page sends the failed test names on re-run).</summary>
        public string? Filter { get; init; }
    }

    public sealed class DebugStartRequest
    {
        /// <summary>Required: the file the page wants to run under the debugger.</summary>
        public required string File { get; init; }

        public bool StopOnEntry { get; init; }

        /// <summary>Optional: extra program arguments.</summary>
        public string? Args { get; init; }
    }

    public sealed class ToggleBreakpointRequest
    {
        /// <summary>Required: a breakpoint without a file has nowhere to live.</summary>
        public required string File { get; init; }

        public int Line { get; init; }
        public string? Condition { get; init; }
        public string? LogMessage { get; init; }
    }

    /// <summary>One entry of a <c>debug_set_breakpoints</c> payload.</summary>
    public sealed class BreakpointEntry
    {
        public int Line { get; init; }
        public string? Condition { get; init; }
        public string? LogMessage { get; init; }
        public bool? Enabled { get; init; }
    }

    public sealed class SetBreakpointsRequest
    {
        /// <summary>Required: every breakpoint in the payload belongs to this file.</summary>
        public required string File { get; init; }

        public List<BreakpointEntry>? Breakpoints { get; init; }
    }

    public sealed class ClearBreakpointsRequest
    {
        /// <summary>Optional: null or empty clears every file's breakpoints.</summary>
        public string? File { get; init; }
    }

    public sealed class GetVariablesRequest
    {
        public int VariablesReference { get; init; }
    }

    public sealed class EvaluateRequest
    {
        /// <summary>Required: an empty expression is a page bug, not an evaluation.</summary>
        public required string Expression { get; init; }

        public string? FrameId { get; init; }
    }

    public void Register(BridgeRouter router)
    {
        router.Register<NoPayload, object?>(TestsDebugRoutes.DetectTestRunner, DetectTestRunnerAsync, envelope: false);
        router.Register<RunTestsRequest, object?>(TestsDebugRoutes.RunTests, RunTestsAsync, envelope: false);
        router.Register<NoPayload, object?>(TestsDebugRoutes.CancelTests, CancelTestsAsync, envelope: false);

        router.Register<DebugStartRequest, object?>(TestsDebugRoutes.DebugStart, DebugStartAsync, envelope: false);
        router.Register<NoPayload, object?>(TestsDebugRoutes.DebugStop,
            (_, _) => RunInBackground(DebugService.StopDebuggingAsync), envelope: false);
        router.Register<NoPayload, object?>(TestsDebugRoutes.DebugPause,
            (_, _) => RunInBackground(DebugService.PauseAsync), envelope: false);
        router.Register<NoPayload, object?>(TestsDebugRoutes.DebugContinue,
            (_, _) => RunInBackground(DebugService.ContinueAsync), envelope: false);
        router.Register<NoPayload, object?>(TestsDebugRoutes.DebugStepOver,
            (_, _) => RunInBackground(DebugService.StepOverAsync), envelope: false);
        router.Register<NoPayload, object?>(TestsDebugRoutes.DebugStepInto,
            (_, _) => RunInBackground(DebugService.StepIntoAsync), envelope: false);
        router.Register<NoPayload, object?>(TestsDebugRoutes.DebugStepOut,
            (_, _) => RunInBackground(DebugService.StepOutAsync), envelope: false);

        router.Register<ToggleBreakpointRequest, object?>(TestsDebugRoutes.DebugToggleBreakpoint, ToggleBreakpointAsync, envelope: false);
        router.Register<SetBreakpointsRequest, object?>(TestsDebugRoutes.DebugSetBreakpoints, SetBreakpointsAsync, envelope: false);
        router.Register<ClearBreakpointsRequest, object?>(TestsDebugRoutes.DebugClearBreakpoints, ClearBreakpointsAsync, envelope: false);
        router.Register<GetVariablesRequest, object?>(TestsDebugRoutes.DebugGetVariables, GetVariablesAsync, envelope: false);
        router.Register<EvaluateRequest, object?>(TestsDebugRoutes.DebugEvaluate, EvaluateAsync, envelope: false);
        router.Register<ActiveFileRequest, object?>(TestsDebugRoutes.DebugListConfigs, ListDebugConfigsAsync, envelope: false);
    }

    public sealed class ActiveFileRequest
    {
        public string? ActiveFile { get; init; }
    }

    /// <summary>
    /// The debugger controls are fire-and-forget: the page never waits, and a failure there is already
    /// Reported on the debugger's own event stream.
    /// </summary>
    private static Task<object?> RunInBackground(Func<Task> work)
    {
        _ = Task.Run(work);
        return Task.FromResult<object?>(null);
    }

    private Task<object?> DetectTestRunnerAsync(NoPayload request, BridgeContext context)
    {
        var detected = TestService.DetectTestRunner(_workspace() ?? "");
        _send("test_runner_detected", new
        {
            hasRunner = detected != null,
            runner = detected?.Runner ?? "None",
            command = detected != null ? $"{detected.Value.Command} {detected.Value.Args}" : ""
        });
        return Task.FromResult<object?>(null);
    }

    private Task<object?> RunTestsAsync(RunTestsRequest request, BridgeContext context)
    {
        var filter = request.Filter;
        var workspace = _workspace() ?? "";
        var runId = $"test_run_{Interlocked.Increment(ref _testRunSequence)}";
        Interlocked.Exchange(ref _activeTestRunId, runId);

        _ = Task.Run(async () =>
        {
            _send("test_run_started", new { runId, filter });
            var result = await TestService.RunTestsAsync(
                workspace,
                filter,
                line => _send("test_output_line", new { runId, line })
            );
            result.RunId = runId;

            // Only the run the page still believes in gets a completion: a cancelled run, or one that a
            // Newer run replaced, has already had its `test_run_cancelled`.
            if (string.Equals(Volatile.Read(ref _activeTestRunId), runId, StringComparison.Ordinal))
            {
                Interlocked.CompareExchange(ref _activeTestRunId, null, runId);
                _send("test_run_completed", result);
            }
        });

        return Task.FromResult<object?>(null);
    }

    private Task<object?> CancelTestsAsync(NoPayload request, BridgeContext context)
    {
        var runId = Interlocked.Exchange(ref _activeTestRunId, null);
        TestService.CancelActiveRun();
        _send("test_run_cancelled", new { runId });
        return Task.FromResult<object?>(null);
    }

    private Task<object?> DebugStartAsync(DebugStartRequest request, BridgeContext context)
    {
        var file = request.File;
        var workspace = _workspace() ?? "";
        var stopOnEntry = request.StopOnEntry;
        var args = request.Args;
        return RunInBackground(() => DebugService.StartDebuggingAsync(file, workspace, stopOnEntry, args));
    }

    private Task<object?> ToggleBreakpointAsync(ToggleBreakpointRequest request, BridgeContext context)
    {
        var file = request.File;
        if (!string.IsNullOrEmpty(file) && request.Line > 0)
        {
            DebugService.ToggleBreakpoint(file, request.Line, request.Condition, request.LogMessage);
        }
        return Task.FromResult<object?>(null);
    }

    private Task<object?> SetBreakpointsAsync(SetBreakpointsRequest request, BridgeContext context)
    {
        var file = request.File;

        // The list is replaced wholesale, which is what the editor sends after every breakpoint change;
        // An entry without a line is dropped rather than stored at line 0.
        var list = new List<BreakpointInfo>();
        foreach (var entry in request.Breakpoints ?? new List<BreakpointEntry>())
        {
            if (entry == null || entry.Line <= 0) continue;
            list.Add(new BreakpointInfo
            {
                FilePath = file,
                Line = entry.Line,
                Condition = entry.Condition,
                LogMessage = entry.LogMessage,
                Enabled = entry.Enabled ?? true
            });
        }

        DebugService.SetBreakpoints(file, list);
        return Task.FromResult<object?>(null);
    }

    private Task<object?> ClearBreakpointsAsync(ClearBreakpointsRequest request, BridgeContext context)
    {
        DebugService.ClearBreakpoints(request.File);
        return Task.FromResult<object?>(null);
    }

    private Task<object?> GetVariablesAsync(GetVariablesRequest request, BridgeContext context)
    {
        var variablesReference = request.VariablesReference;
        _ = Task.Run(async () =>
        {
            var variables = await DebugService.GetVariablesByReferenceAsync(variablesReference);
            _send("debug_variables_response", new { variablesReference, variables });
        });
        return Task.FromResult<object?>(null);
    }

    private Task<object?> EvaluateAsync(EvaluateRequest request, BridgeContext context)
    {
        var expression = request.Expression;
        var frameId = request.FrameId;
        _ = Task.Run(async () =>
        {
            var result = await DebugService.EvaluateExpressionAsync(expression, frameId);
            _send("debug_evaluate_response", new { expression, result });
        });
        return Task.FromResult<object?>(null);
    }

    private Task<object?> ListDebugConfigsAsync(ActiveFileRequest request, BridgeContext context)
    {
        var configs = LaunchConfigService.LoadConfigurations(_workspace() ?? "", request.ActiveFile);
        _send("debug_configs_response", new { configs });
        return Task.FromResult<object?>(null);
    }
}

/// <summary>The action names of the test-runner and debugger domain.</summary>
public static class TestsDebugRoutes
{
    public const string DetectTestRunner = "detect_test_runner";
    public const string RunTests = "run_tests";
    public const string CancelTests = "cancel_tests";
    public const string DebugStart = "debug_start";
    public const string DebugStop = "debug_stop";
    public const string DebugPause = "debug_pause";
    public const string DebugContinue = "debug_continue";
    public const string DebugStepOver = "debug_step_over";
    public const string DebugStepInto = "debug_step_into";
    public const string DebugStepOut = "debug_step_out";
    public const string DebugToggleBreakpoint = "debug_toggle_breakpoint";
    public const string DebugSetBreakpoints = "debug_set_breakpoints";
    public const string DebugClearBreakpoints = "debug_clear_breakpoints";
    public const string DebugGetVariables = "debug_get_variables";
    public const string DebugEvaluate = "debug_evaluate";
    public const string DebugListConfigs = "debug_list_configs";
}