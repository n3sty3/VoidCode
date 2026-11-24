using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using VoidCode.Agent;
using VoidCode.Agent.Providers;
using VoidCode.Agent.Tools;
using VoidCode.Services;
using Xunit;

namespace VoidCode.Tests;

[Collection("StorageRoot")]
public class AgentV2Tests
{
    private static string CreateTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"voidcode-v2-{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void UnifiedDiffService_ComputesExactAddedAndRemovedCounts()
    {
        var before = "line1\nline2\nline3\nline4";
        var after = "line1\nline2_mod\nline3\nline3_extra\nline4";

        var diff = UnifiedDiffService.Compute(before, after, "demo.cs");
        Assert.Equal(2, diff.Added);
        Assert.Equal(1, diff.Removed);
        Assert.Contains("@@ ", diff.DiffText);
        Assert.Contains("- line2", diff.DiffText);
        Assert.Contains("+ line2_mod", diff.DiffText);
        Assert.Contains("+ line3_extra", diff.DiffText);
    }

    [Fact]
    public void MultiPatchFileSmart_AppliesAtomicallyAndPreservesFileOnFailure()
    {
        var dir = CreateTempDir("multipatch");
        var file = Path.Combine(dir, "sample.cs");
        try
        {
            var initial = "int a = 1;\nint b = 2;\nint c = 3;\n";
            File.WriteAllText(file, initial);

            // Failing second hunk must leave file completely untouched.
            var fail = FileSystemService.MultiPatchFileSmart(file, new[]
            {
                ("int a = 1;", "int a = 10;"),
                ("DOES_NOT_EXIST", "int z = 99;")
            });
            Assert.False(fail.success);
            Assert.Equal(initial, File.ReadAllText(file));

            // Valid multi-hunk edit updates both hunks with a single checkpoint.
            var ok = FileSystemService.MultiPatchFileSmart(file, new[]
            {
                ("int a = 1;", "int a = 10;"),
                ("int c = 3;", "int c = 30;")
            });
            Assert.True(ok.success);
            Assert.NotNull(ok.checkpoint);
            var updated = File.ReadAllText(file);
            Assert.Contains("int a = 10;", updated);
            Assert.Contains("int c = 30;", updated);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { /* cleanup */ }
        }
    }

    [Fact]
    public void GlobFiles_MatchesPatternsAndSkipsIgnoredDirs()
    {
        var dir = CreateTempDir("glob");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "src", "nested"));
            Directory.CreateDirectory(Path.Combine(dir, "node_modules", "pkg"));
            File.WriteAllText(Path.Combine(dir, "src", "App.cs"), "");
            File.WriteAllText(Path.Combine(dir, "src", "nested", "Helper.cs"), "");
            File.WriteAllText(Path.Combine(dir, "src", "readme.md"), "");
            File.WriteAllText(Path.Combine(dir, "node_modules", "pkg", "Ignored.cs"), "");

            var csFiles = FileSystemService.GlobFiles(dir, "**/*.cs");
            Assert.Equal(2, csFiles.Count);
            Assert.Contains("src/App.cs", csFiles);
            Assert.Contains("src/nested/Helper.cs", csFiles);
            Assert.DoesNotContain(csFiles, f => f.Contains("node_modules"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { /* cleanup */ }
        }
    }

    [Fact]
    public void AgentSessionMemory_TracksTracesAndMidRunSteerQueue()
    {
        var ws = CreateTempDir("session-mem");
        try
        {
            AgentSessionMemory.ClearWorkspace(ws);
            AgentSessionMemory.ClearSteers();

            AgentSessionMemory.RecordAction(ws, "read_file(Program.cs) -> ok");
            AgentSessionMemory.RecordAction(ws, "multi_patch_file(Program.cs) -> ok");

            var block = AgentSessionMemory.FormatRecentWorkForPrompt(ws);
            Assert.Contains("RECENT SESSION TOOL TRACE", block);
            Assert.Contains("Program.cs", block);

            AgentSessionMemory.EnqueueSteer("Use Debug build");
            AgentSessionMemory.EnqueueSteer("Keep zero warnings");
            var steers = AgentSessionMemory.DrainSteers();
            Assert.Equal(2, steers.Count);
            Assert.Equal("Use Debug build", steers[0]);
            Assert.Empty(AgentSessionMemory.DrainSteers());
        }
        finally
        {
            AgentSessionMemory.ClearWorkspace(ws);
            try { Directory.Delete(ws, true); } catch (IOException) { /* cleanup */ }
        }
    }

    [Fact]
    public void AgentLoop_ParallelBatchingAllowsOnlySafeInWorkspaceReadTools()
    {
        var ws = CreateTempDir("par");
        try
        {
            Assert.True(AgentLoop.IsParallelizableReadOnlyTool("read_file", "{\"path\":\"a.cs\"}", ws, false));
            Assert.True(AgentLoop.IsParallelizableReadOnlyTool("glob_files", "{\"pattern\":\"**/*.cs\"}", ws, false));
            Assert.True(AgentLoop.IsParallelizableReadOnlyTool("get_lsp_diagnostics", "{}", ws, false));
            Assert.False(AgentLoop.IsParallelizableReadOnlyTool("patch_file", "{\"path\":\"a.cs\"}", ws, false));
            Assert.False(AgentLoop.IsParallelizableReadOnlyTool("multi_patch_file", "{\"path\":\"a.cs\"}", ws, false));
            Assert.False(AgentLoop.IsParallelizableReadOnlyTool("run_command", "{\"command\":\"ls\"}", ws, false));
            Assert.False(AgentLoop.IsParallelizableReadOnlyTool("delegate_research", "{\"question\":\"hi\"}", ws, false));
            Assert.False(AgentLoop.IsParallelizableReadOnlyTool("update_plan", "{\"steps\":[]}", ws, false));
            Assert.False(AgentLoop.IsParallelizableReadOnlyTool("read_file", "{\"path\":\"../escape.txt\"}", ws, false));
        }
        finally
        {
            try { Directory.Delete(ws, true); } catch (IOException) { /* cleanup */ }
        }
    }

    [Fact]
    public async Task GitPowerService_ValidatesUrlsHashesAndParsesGraph()
    {
        Assert.True(GitPowerService.IsValidCloneUrl("https://github.com/n3sty3/VoidCode.git"));
        Assert.True(GitPowerService.IsValidCloneUrl("git@github.com:n3sty3/VoidCode.git"));
        Assert.False(GitPowerService.IsValidCloneUrl("--upload-pack=evil"));
        Assert.False(GitPowerService.IsValidCloneUrl("file:///etc/passwd"));

        var badShow = await GitPowerService.ShowCommitAsync(Path.GetTempPath(), "not-a-hash;rm -rf");
        Assert.False(badShow.Ok);

        var sampleLog = "*   VCSEPabcdef1234567890\u001fabcdef1\u001fDevid\u001f2 hours ago\u001fHEAD -> main, origin/main\u001ffeat(v2): VoidAgent recode\n|\\  \n| * VCSEP1234567890abcdef\u001f1234567\u001fAda\u001f1 day ago\u001f\u001ffix: parser edge case\n";
        var parsed = GitPowerService.ParseCommitGraphOutput(sampleLog);
        Assert.Equal(2, parsed.Count);
        Assert.Equal("abcdef1", parsed[0].ShortHash);
        Assert.Contains("HEAD -> main", parsed[0].Refs);
        Assert.Equal("feat(v2): VoidAgent recode", parsed[0].Subject);
        Assert.Equal("| *", parsed[1].Graph);
    }

    [Fact]
    public void HostController_ConstructsAndRegistersAllBridgeRoutesWithoutCollisions()
    {
        var dir = CreateTempDir("host-ctrl");
        using var scope = StorageService.UseRoot(dir);
        try
        {
            var hooks = new VoidCode.Bridge.HostController.HostHooks(
                SendRawJson: _ => { },
                ApplyThemeIcon: _ => { },
                OnWorkspaceMapped: _ => { },
                OnMinimize: null,
                OnMaximize: null,
                WindowChrome: new VoidCode.Bridge.Handlers.SessionWindowHandlers.WindowChrome(
                    () => { },
                    () => { },
                    () => { },
                    () => { }));

            var controller = new VoidCode.Bridge.HostController(hooks);
            Assert.NotNull(controller);
            Assert.False(string.IsNullOrWhiteSpace(controller.BridgeNonce));
            controller.Shutdown();
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { /* cleanup */ }
        }
    }

    [Fact]
    public void RepoMapService_BuildsCompactMapWithTopLevelSymbols()
    {
        var dir = CreateTempDir("repomap");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "src"));
            Directory.CreateDirectory(Path.Combine(dir, "node_modules", "ignored"));
            File.WriteAllText(Path.Combine(dir, "src", "Engine.cs"),
                "namespace Demo;\npublic class InferenceEngine\n{\n    public void RunLoop() {}\n}\n");
            File.WriteAllText(Path.Combine(dir, "src", "utils.ts"),
                "export interface TokenBudget { max: number; }\nexport async function computeScore(x: number) { return x; }\n");
            File.WriteAllText(Path.Combine(dir, "node_modules", "ignored", "Noise.js"),
                "export function shouldNotAppear() {}\n");

            var map = RepoMapService.BuildCompactMap(dir);
            Assert.Contains("src/Engine.cs", map);
            Assert.Contains("InferenceEngine", map);
            Assert.Contains("src/utils.ts", map);
            Assert.Contains("TokenBudget", map);
            Assert.Contains("computeScore", map);
            Assert.DoesNotContain("shouldNotAppear", map);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { /* cleanup */ }
        }
    }

    [Fact]
    public void JsonToolArgsRepair_RepairsTrailingCommasFencesAndTruncatedTail()
    {
        var fenced = JsonToolArgsRepair.ParseOrRepair("```json\n{\"path\":\"src/app.js\",}\n```");
        Assert.NotNull(fenced);
        Assert.Equal("src/app.js", fenced!["path"]?.GetValue<string>());

        var truncated = JsonToolArgsRepair.ParseOrRepair("{\"path\":\"foo.cs\",\"content\":\"hello world");
        Assert.NotNull(truncated);
        Assert.Equal("foo.cs", truncated!["path"]?.GetValue<string>());
        Assert.Equal("hello world", truncated!["content"]?.GetValue<string>());

        var empty = JsonToolArgsRepair.ParseOrRepair("   ");
        Assert.NotNull(empty);
        Assert.Empty(empty!.AsObject());
    }

    [Fact]
    public void AgentSessionMemory_SavesAndReplaysOrderedTranscriptWithoutDetachingTools()
    {
        var key = AgentSessionMemory.ResolveSessionKey("ws-test", "sess-ordered-1");
        try
        {
            AgentSessionMemory.ClearSession(key);

            var transcript = new List<JsonObject>
            {
                new() { ["role"] = "user", ["content"] = "Inspect Program.cs" },
                new()
                {
                    ["role"] = "assistant",
                    ["content"] = "",
                    ["reasoning_content"] = "Need to read Program.cs first.",
                    ["tool_calls"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["id"] = "call_1",
                            ["type"] = "function",
                            ["function"] = new JsonObject
                            {
                                ["name"] = "read_file",
                                ["arguments"] = "{\"path\":\"Program.cs\"}"
                            }
                        }
                    }
                },
                new()
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = "call_1",
                    ["content"] = new string('x', 5000)
                },
                new() { ["role"] = "assistant", ["content"] = "Program.cs boots the app." }
            };

            AgentSessionMemory.SaveOrderedTranscript(key, transcript);
            var replay = AgentSessionMemory.GetOrderedTranscript(key);
            Assert.Equal(4, replay.Count);
            Assert.Equal("user", replay[0]["role"]?.GetValue<string>());
            Assert.Equal("assistant", replay[1]["role"]?.GetValue<string>());
            Assert.Equal("Need to read Program.cs first.", replay[1]["reasoning_content"]?.GetValue<string>());
            Assert.Equal("tool", replay[2]["role"]?.GetValue<string>());
            Assert.Equal(5000, replay[2]["content"]?.GetValue<string>()?.Length);
            Assert.Equal("assistant", replay[3]["role"]?.GetValue<string>());
            Assert.Equal(1, AgentSessionMemory.CountUserTurns(replay));
        }
        finally
        {
            AgentSessionMemory.ClearSession(key);
        }
    }

    private sealed class ScriptedChatProvider : IChatProvider
    {
        private readonly Queue<List<ChatEvent>> _turns;
        public List<List<JsonObject>> CapturedRequests { get; } = new();
        public string Name => "scripted";

        public ScriptedChatProvider(params List<ChatEvent>[] turns)
        {
            _turns = new Queue<List<ChatEvent>>(turns);
        }

        public async IAsyncEnumerable<ChatEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            CapturedRequests.Add(request.Messages.Select(m => JsonNode.Parse(m.ToJsonString())!.AsObject()).ToList());
            var events = _turns.Count > 0 ? _turns.Dequeue() : new List<ChatEvent> { new ChatContentDelta("Done.") };
            foreach (var evt in events)
            {
                await Task.Yield();
                yield return evt;
            }
        }

        public Task<string> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult("Summary");

        public Task<(bool success, string message, List<string> models)> TestConnectionAsync(AppSettings settings, CancellationToken cancellationToken = default)
            => Task.FromResult((true, "ok", new List<string> { "mock" }));
    }

    [Fact]
    public async Task AgentLoop_InjectsTurn1RepoMapPreservesReasoningAndReplaysOrderedHistory()
    {
        var ws = CreateTempDir("loop-ordered");
        var sessionId = $"sess_{Guid.NewGuid():N}";
        var previousOverride = ProviderFactory.OverrideForTests;
        try
        {
            Directory.CreateDirectory(Path.Combine(ws, "src"));
            File.WriteAllText(Path.Combine(ws, "src", "Core.cs"), "public class CoreEngine { public int Run() => 42; }\n");

            var fakeProvider = new ScriptedChatProvider(
                // Turn 1 Step 1: think + call read_file
                new List<ChatEvent>
                {
                    new ChatReasoningDelta("Let me inspect Core.cs first."),
                    new ChatThinkingBlockEvent("thinking", Thinking: "Let me inspect Core.cs first.", Signature: "sig_abc_123"),
                    new ChatToolCallDelta(0, "call_read_1", "read_file", "{\"path\":\"src/Core.cs\"}")
                },
                // Turn 1 Step 2: final answer
                new List<ChatEvent>
                {
                    new ChatContentDelta("CoreEngine.Run returns 42.")
                },
                // Turn 2 Step 1: follow-up answer
                new List<ChatEvent>
                {
                    new ChatContentDelta("Confirmed, still 42.")
                }
            );
            ProviderFactory.OverrideForTests = _ => fakeProvider;

            var settings = new AppSettings
            {
                Provider = "anthropic",
                BaseUrl = "https://api.anthropic.com/v1",
                Model = "claude-sonnet-4-6",
                AutoApproveTools = true,
                AuthorityMode = "workspace_auto",
                AgentAutoVerify = false
            };

            // Simulate exact frontend behavior: appendUserMessage ALREADY pushed the Turn-1 prompt into chatHistory!
            var turn1FrontendHistory = new List<JsonObject>
            {
                new() { ["role"] = "user", ["content"] = "What does CoreEngine.Run return?" }
            };
            await AgentLoop.RunAsync(
                "What does CoreEngine.Run return?",
                turn1FrontendHistory,
                ws,
                settings,
                (_, _) => { },
                sessionId: sessionId);

            // Turn 1 Step 1 MUST have injected the Repo Map even though conversationHistory.Count was 1!
            Assert.True(fakeProvider.CapturedRequests.Count >= 2);
            var step1Msgs = fakeProvider.CapturedRequests[0];
            Assert.Equal(2, step1Msgs.Count); // [system, user]
            var turn1UserText = step1Msgs[1]["content"]?.GetValue<string>() ?? "";
            Assert.Contains("CoreEngine", turn1UserText);
            Assert.Contains("What does CoreEngine.Run return?", turn1UserText);

            // Turn 1 Step 2 MUST carry reasoning_content AND signed _thinking_blocks on the assistant tool_calls message!
            var step2Msgs = fakeProvider.CapturedRequests[1];
            Assert.Equal(4, step2Msgs.Count); // [system, user1, assistant(tool_calls), tool]
            Assert.Equal("Let me inspect Core.cs first.", step2Msgs[2]["reasoning_content"]?.GetValue<string>());
            var tbArr = Assert.IsType<JsonArray>(step2Msgs[2]["_thinking_blocks"]);
            Assert.Equal("sig_abc_123", tbArr[0]?["signature"]?.GetValue<string>());

            // Turn 2: follow-up prompt. Frontend sends [user1, assistant_final, user2].
            var turn2FrontendHistory = new List<JsonObject>
            {
                new() { ["role"] = "user", ["content"] = "What does CoreEngine.Run return?" },
                new() { ["role"] = "assistant", ["content"] = "CoreEngine.Run returns 42." },
                new() { ["role"] = "user", ["content"] = "Are you sure?" }
            };
            await AgentLoop.RunAsync(
                "Are you sure?",
                turn2FrontendHistory,
                ws,
                settings,
                (_, _) => { },
                sessionId: sessionId);

            // Turn 2 MUST replay the REAL chronological order:
            // [system, user1, assistant1(tool_calls+reasoning+thinking), tool1, assistant2(final), user2]
            Assert.Equal(3, fakeProvider.CapturedRequests.Count);
            var turn2Msgs = fakeProvider.CapturedRequests[2];
            Assert.Equal(6, turn2Msgs.Count);
            Assert.Equal("system", turn2Msgs[0]["role"]?.GetValue<string>());
            Assert.Equal("user", turn2Msgs[1]["role"]?.GetValue<string>());
            Assert.Equal("assistant", turn2Msgs[2]["role"]?.GetValue<string>());
            Assert.NotNull(turn2Msgs[2]["tool_calls"]);
            Assert.Equal("tool", turn2Msgs[3]["role"]?.GetValue<string>());
            Assert.Contains("42", turn2Msgs[3]["content"]?.GetValue<string>() ?? "");
            Assert.Equal("assistant", turn2Msgs[4]["role"]?.GetValue<string>());
            Assert.Equal("CoreEngine.Run returns 42.", turn2Msgs[4]["content"]?.GetValue<string>());
            Assert.Equal("user", turn2Msgs[5]["role"]?.GetValue<string>());
            Assert.Contains("Are you sure?", turn2Msgs[5]["content"]?.GetValue<string>() ?? "");
        }
        finally
        {
            ProviderFactory.OverrideForTests = previousOverride;
            AgentSessionMemory.ClearSession(AgentSessionMemory.ResolveSessionKey(ws, sessionId));
            try { Directory.Delete(ws, true); } catch (IOException) { /* cleanup */ }
        }
    }

    [Fact]
    public void AnthropicProvider_ReplaysSignedThinkingBlocksAndSupportsMediumEffortOnModernClaude()
    {
        var req = new ChatRequest
        {
            Provider = "anthropic",
            BaseUrl = "https://api.anthropic.com/v1",
            Model = "claude-sonnet-4-6",
            MaxTokens = 8192,
            ReasoningEffort = "medium",
            Messages = new List<JsonObject>
            {
                new() { ["role"] = "user", ["content"] = "Read a.cs" },
                new()
                {
                    ["role"] = "assistant",
                    ["content"] = "",
                    ["_thinking_blocks"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "thinking", ["thinking"] = "Checking a.cs", ["signature"] = "sig_xyz" }
                    },
                    ["tool_calls"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["id"] = "toolu_1",
                            ["type"] = "function",
                            ["function"] = new JsonObject { ["name"] = "read_file", ["arguments"] = "{\"path\":\"a.cs\"}" }
                        }
                    }
                },
                new() { ["role"] = "tool", ["tool_call_id"] = "toolu_1", ["content"] = "class A {}" }
            }
        };

        var payload = AnthropicProvider.BuildMessagesPayload(req);
        Assert.NotNull(payload["thinking"]);
        Assert.Equal("enabled", payload["thinking"]!["type"]?.GetValue<string>());
        Assert.Equal(1, payload["temperature"]?.GetValue<int>());

        var msgs = Assert.IsType<JsonArray>(payload["messages"]);
        var assistantBlocks = Assert.IsType<JsonArray>(msgs[1]?["content"]);
        Assert.Equal("thinking", assistantBlocks[0]?["type"]?.GetValue<string>());
        Assert.Equal("sig_xyz", assistantBlocks[0]?["signature"]?.GetValue<string>());
        Assert.Equal("tool_use", assistantBlocks[1]?["type"]?.GetValue<string>());

        Assert.True(AnthropicProvider.SupportsExtendedThinking("claude-opus-4-6"));
        Assert.True(AnthropicProvider.SupportsExtendedThinking("claude-haiku-4-5-20251001"));
        Assert.True(AnthropicProvider.SupportsExtendedThinking("claude-5-sonnet"));
        Assert.False(AnthropicProvider.SupportsExtendedThinking("claude-3-5-sonnet-20241022"));
    }

    [Fact]
    public void TokenEstimator_WeightsCodeHigherThanProseAndCountsReasoning()
    {
        var prose = new string('a', 400);
        var code = string.Concat(Enumerable.Repeat("if (x[i] != null) { map.Add(k, v); }\n", 11));
        Assert.True(TokenEstimator.EstimateText(code) > TokenEstimator.EstimateText(prose.Substring(0, Math.Min(prose.Length, code.Length))));

        var msgWithoutReasoning = new JsonObject { ["role"] = "assistant", ["content"] = "ok" };
        var msgWithReasoning = new JsonObject { ["role"] = "assistant", ["content"] = "ok", ["reasoning_content"] = new string('r', 400) };
        Assert.True(TokenEstimator.EstimateMessage(msgWithReasoning) >= TokenEstimator.EstimateMessage(msgWithoutReasoning) + 90);
    }

    [Fact]
    public async Task ToolExecutor_UpdatePlan_EmitsNormalizedSteps()
    {
        var ws = CreateTempDir("plan-tool");
        try
        {
            string? capturedEvent = null;
            object? capturedPayload = null;
            var args = "{\"steps\":[{\"title\":\"Inspect parser\",\"status\":\"completed\"},{\"title\":\"Fix edge case\",\"status\":\"in_progress\"},{\"title\":\"Run tests\",\"status\":\"pending\"}]}";

            var (output, _, _) = await ToolExecutor.ExecuteAsync(
                "update_plan",
                args,
                ws,
                (ev, payload) => { capturedEvent = ev; capturedPayload = payload; });

            Assert.Equal("agent_plan_update", capturedEvent);
            Assert.NotNull(capturedPayload);
            Assert.Contains("Updated live plan (3 steps)", output);
        }
        finally
        {
            try { Directory.Delete(ws, true); } catch (IOException) { /* cleanup */ }
        }
    }

    [Fact]
    public void Prompts_LayersCustomOperatorRulesOnTopOfBuiltInVoidAgentCore()
    {
        var ws = CreateTempDir("prompt-core");
        try
        {
            var settings = new AppSettings
            {
                SystemPrompt = "Always prefer immutable record structs."
            };
            var prompt = Prompts.SystemPrompt(settings, ws, isPlanMode: false).ToString();
            Assert.Contains("VoidAgent", prompt);
            Assert.Contains("OPERATOR SPECIALIST PROFILE & EXTRA RULES", prompt);
            Assert.Contains("Always prefer immutable record structs.", prompt);
            Assert.Contains("update_plan", prompt);
        }
        finally
        {
            try { Directory.Delete(ws, true); } catch (IOException) { /* cleanup */ }
        }
    }
}
