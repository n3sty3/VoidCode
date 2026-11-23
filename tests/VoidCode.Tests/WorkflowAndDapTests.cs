using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using VoidCode.Bridge;
using VoidCode.Bridge.Handlers;
using VoidCode.Dap;
using VoidCode.Services;
using Xunit;

namespace VoidCode.Tests;

[Collection("Environment variables")]
public class WorkflowAndDapTests
{
    [Fact]
    public void TaskRunnerService_LoadsJsoncTasks_SubstitutesVars_AndParsesProblemMatchers()
    {
        var temp = Path.Combine(Path.GetTempPath(), "voidcode-tasks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(temp, ".voidcode"));
        Directory.CreateDirectory(Path.Combine(temp, ".vscode"));
        try
        {
            File.WriteAllText(Path.Combine(temp, ".voidcode", "tasks.json"), """
            {
              // JSONC comment and trailing comma
              "version": "2.0.0",
              "tasks": [
                {
                  "label": "build",
                  "type": "shell",
                  "command": "dotnet",
                  "args": ["build", "${workspaceFolder}"],
                  "group": { "kind": "build" },
                  "problemMatcher": "$msCompile",
                }
              ]
            }
            """);

            File.WriteAllText(Path.Combine(temp, ".vscode", "tasks.json"), """
            {
              "version": "2.0.0",
              "tasks": [
                { "label": "build", "command": "old-ignored" },
                { "label": "typecheck", "command": "npx", "args": ["tsc", "--noEmit"], "problemMatcher": ["$tsc"] }
              ]
            }
            """);

            var tasks = TaskRunnerService.LoadTasks(temp);
            Assert.Equal(2, tasks.Count);
            Assert.Equal("dotnet", tasks[0].Command); // .voidcode wins over .vscode for same label
            Assert.Equal("typecheck", tasks[1].Label);

            var sub = TaskRunnerService.SubstituteVariables("${workspaceFolder}/src/${fileBasename}", temp, Path.Combine(temp, "src", "App.cs"));
            Assert.Equal($"{temp}/src/App.cs", sub);

            var compilerOutput = string.Join("\n", new[]
            {
                @"src\Program.cs(18,9): error CS1002: ; expected [Demo.csproj]",
                @"src/app.ts:42:5 - error TS2322: Type 'number' is not assignable to type 'string'.",
                @"native/main.c:9:3: warning: unused variable 'x'"
            });

            var problems = TaskRunnerService.ParseProblems(compilerOutput, temp);
            Assert.Equal(3, problems.Count);
            Assert.Equal(18, problems[0].Line);
            Assert.Equal(9, problems[0].Column);
            Assert.Equal("CS1002", problems[0].Code);
            Assert.Equal(1, problems[0].Severity);

            Assert.Equal(42, problems[1].Line);
            Assert.Equal("TS2322", problems[1].Code);

            Assert.Equal(9, problems[2].Line);
            Assert.Equal(2, problems[2].Severity); // warning
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch (IOException) { /* cleanup */ }
        }
    }

    [Fact]
    public void GitService_ParseBlamePorcelain_HandlesRepeatedCommitsAndUncommittedLines()
    {
        var porcelain = string.Join("\n", new[]
        {
            "abc1234567890abcdef1234567890abcdef12345 1 1 2",
            "author Ada Lovelace",
            "author-time 1700000000",
            "summary Initial engine",
            "filename Program.cs",
            "\tusing System;",
            "abc1234567890abcdef1234567890abcdef12345 2 2",
            "\tclass Program {}",
            "0000000000000000000000000000000000000000 3 3 1",
            "author Not Committed Yet",
            "author-time 0",
            "summary Working tree",
            "filename Program.cs",
            "\t// draft"
        });

        var lines = GitService.ParseBlamePorcelain(porcelain);
        Assert.Equal(3, lines.Count);

        Assert.Equal(1, lines[0].Line);
        Assert.Equal("Ada Lovelace", lines[0].Author);
        Assert.Equal("abc1234", lines[0].ShortHash);
        Assert.Equal("Initial engine", lines[0].Summary);

        // Line 2 reuses cached commit metadata from line 1
        Assert.Equal(2, lines[1].Line);
        Assert.Equal("Ada Lovelace", lines[1].Author);
        Assert.Equal("Initial engine", lines[1].Summary);

        // Line 3 is uncommitted
        Assert.Equal("uncommitted", lines[2].ShortHash);
        Assert.Equal("Not Committed Yet", lines[2].Author);
    }

    [Fact]
    public async Task LaunchConfigService_LoadsJsoncConfigs_AndDispatchesViaBridge()
    {
        var temp = Path.Combine(Path.GetTempPath(), "voidcode-launch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(temp, ".vscode"));
        try
        {
            File.WriteAllText(Path.Combine(temp, ".vscode", "launch.json"), """
            {
              // Standard launch config
              "version": "0.2.0",
              "configurations": [
                {
                  "name": "Debug API",
                  "type": "coreclr",
                  "request": "launch",
                  "program": "${workspaceFolder}/bin/Debug/Api.dll",
                  "args": ["--port", "5000"],
                  "cwd": "${workspaceFolder}",
                  "stopAtEntry": true,
                  "preLaunchTask": "build",
                }
              ]
            }
            """);

            var configs = LaunchConfigService.LoadConfigurations(temp);
            var cfg = Assert.Single(configs);
            Assert.Equal("Debug API", cfg.Name);
            Assert.Equal("coreclr", cfg.Type);
            Assert.Equal($"{temp}/bin/Debug/Api.dll", cfg.Program);
            Assert.Equal(new[] { "--port", "5000" }, cfg.Args);
            Assert.True(cfg.StopOnEntry);
            Assert.Equal("build", cfg.PreLaunchTask);

            var events = new List<(string Action, JsonNode Payload)>();
            var router = new BridgeRouter();
            var handlers = new TestsDebugHandlers(() => temp, (a, p) => events.Add((a, JsonSerializer.SerializeToNode(p)!)));
            handlers.Register(router);

            await router.TryHandleAsync(TestsDebugRoutes.DebugListConfigs, "dbg_1", new JsonObject());
            var resp = Assert.Single(events, e => e.Action == "debug_configs_response");
            Assert.Contains("Debug API", resp.Payload.ToJsonString());
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch (IOException) { /* cleanup */ }
        }
    }

    [Fact]
    public async Task DapClient_AndDebugService_RunFullBreakpointStepVariableEvaluateStopLifecycle()
    {
        using var clientToAdapterRead = new AnonymousPipeServerStream(PipeDirection.In);
        using var clientToAdapterWrite = new AnonymousPipeClientStream(PipeDirection.Out, clientToAdapterRead.GetClientHandleAsString());
        using var adapterToClientRead = new AnonymousPipeServerStream(PipeDirection.In);
        using var adapterToClientWrite = new AnonymousPipeClientStream(PipeDirection.Out, adapterToClientRead.GetClientHandleAsString());

        var receivedCommands = new List<string>();
        var adapterDone = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        async Task WriteDapAsync(JsonObject msg)
        {
            var body = Encoding.UTF8.GetBytes(msg.ToJsonString());
            var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
            await adapterToClientWrite.WriteAsync(header, adapterDone.Token);
            await adapterToClientWrite.WriteAsync(body, adapterDone.Token);
            await adapterToClientWrite.FlushAsync(adapterDone.Token);
        }

        // Mock DAP adapter loop answering initialize, launch, setBreakpoints, configurationDone,
        // StackTrace, scopes, variables, evaluate, next, continue, disconnect
        var serverTask = Task.Run(async () =>
        {
            int outSeq = 1;
            while (!adapterDone.IsCancellationRequested)
            {
                var raw = await DapClient.ReadFramedMessageAsync(clientToAdapterRead, adapterDone.Token);
                if (raw == null) break;
                var req = JsonNode.Parse(raw)!.AsObject();
                var seq = req["seq"]!.GetValue<int>();
                var cmd = req["command"]!.GetValue<string>();
                lock (receivedCommands) receivedCommands.Add(cmd);

                JsonObject? body = null;
                switch (cmd)
                {
                    case "initialize":
                        body = new JsonObject { ["supportsConfigurationDoneRequest"] = true };
                        await WriteDapAsync(new JsonObject { ["seq"] = outSeq++, ["type"] = "response", ["request_seq"] = seq, ["command"] = cmd, ["success"] = true, ["body"] = body });
                        await WriteDapAsync(new JsonObject { ["seq"] = outSeq++, ["type"] = "event", ["event"] = "initialized" });
                        continue;

                    case "stackTrace":
                        body = new JsonObject
                        {
                            ["stackFrames"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["id"] = 101,
                                    ["name"] = "Main",
                                    ["source"] = new JsonObject { ["path"] = @"C:\proj\Program.cs" },
                                    ["line"] = 12,
                                    ["column"] = 5
                                }
                            }
                        };
                        break;

                    case "scopes":
                        body = new JsonObject
                        {
                            ["scopes"] = new JsonArray
                            {
                                new JsonObject { ["name"] = "Locals", ["variablesReference"] = 500 }
                            }
                        };
                        break;

                    case "variables":
                        body = new JsonObject
                        {
                            ["variables"] = new JsonArray
                            {
                                new JsonObject { ["name"] = "count", ["value"] = "42", ["type"] = "int", ["variablesReference"] = 0 }
                            }
                        };
                        break;

                    case "evaluate":
                        body = new JsonObject { ["result"] = "84", ["type"] = "int" };
                        break;
                }

                await WriteDapAsync(new JsonObject
                {
                    ["seq"] = outSeq++,
                    ["type"] = "response",
                    ["request_seq"] = seq,
                    ["command"] = cmd,
                    ["success"] = true,
                    ["body"] = body ?? new JsonObject()
                });

                if (cmd == "configurationDone")
                {
                    // Simulate hitting a breakpoint immediately after configurationDone
                    await WriteDapAsync(new JsonObject
                    {
                        ["seq"] = outSeq++,
                        ["type"] = "event",
                        ["event"] = "stopped",
                        ["body"] = new JsonObject { ["reason"] = "breakpoint", ["threadId"] = 1 }
                    });
                }
                else if (cmd == "disconnect")
                {
                    break;
                }
            }
        });

        var pausedTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var uiEvents = new List<(string Action, string Json)>();
        DebugService.Initialize((action, payload) =>
        {
            var json = JsonSerializer.Serialize(payload);
            lock (uiEvents) uiEvents.Add((action, json));
            if (action == "debug_paused") pausedTcs.TrySetResult(json);
        });

        var prevFactory = DebugService.DapFactoryOverrideForTests;
        DebugService.DapFactoryOverrideForTests = (_, _) => new DapClient(adapterToClientRead, clientToAdapterWrite);
        try
        {
            DebugService.ClearBreakpoints();
            DebugService.ToggleBreakpoint(@"C:\proj\Program.cs", 12, condition: "count > 10", logMessage: "hit {count}");

            await DebugService.StartDebuggingAsync(@"C:\proj\Program.cs", @"C:\proj");

            var pausedJson = await pausedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains("Main", pausedJson);
            Assert.Contains("\"count\"", pausedJson);
            Assert.Contains("42", pausedJson);
            Assert.Equal("paused", DebugService.DebugState);

            var evalObj = await DebugService.EvaluateExpressionAsync("count * 2");
            Assert.Contains("84", JsonSerializer.Serialize(evalObj));

            await DebugService.StepOverAsync();
            await DebugService.ContinueAsync();
            await DebugService.StopDebuggingAsync();

            Assert.Equal("idle", DebugService.DebugState);
            Assert.False(DebugService.IsDebugging);

            await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains("initialize", receivedCommands);
            Assert.Contains("launch", receivedCommands);
            Assert.Contains("setBreakpoints", receivedCommands);
            Assert.Contains("configurationDone", receivedCommands);
            Assert.Contains("stackTrace", receivedCommands);
            Assert.Contains("variables", receivedCommands);
            Assert.Contains("evaluate", receivedCommands);
            Assert.Contains("next", receivedCommands);
            Assert.Contains("continue", receivedCommands);
            Assert.Contains("disconnect", receivedCommands);
        }
        finally
        {
            DebugService.DapFactoryOverrideForTests = prevFactory;
            DebugService.ClearBreakpoints();
            adapterDone.Cancel();
        }
    }
}
