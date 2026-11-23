using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using VoidCode.Agent;
using Xunit;

namespace VoidCode.Tests;

public class ContextBudgetTests
{
    private static List<JsonObject> BuildSynthetic300kHistory()
    {
        var messages = new List<JsonObject>
        {
            new() { ["role"] = "system", ["content"] = "SYSTEM PROMPT: VoidCode engineering assistant." },
            new() { ["role"] = "user", ["content"] = "ORIGINAL TASK: Refactor the payment ledger without breaking invoices." }
        };

        // ~300,000 tokens (~1.2M characters) spread across 60 tool-augmented turns
        var bigPayload = new string('A', 18000);
        for (int i = 0; i < 60; i++)
        {
            var callId = $"call_{i}";
            messages.Add(new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = $"Inspecting file {i}.",
                ["tool_calls"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = callId,
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = "read_file",
                            ["arguments"] = $"{{\"path\":\"src/File{i}.cs\"}}"
                        }
                    }
                }
            });
            messages.Add(new JsonObject
            {
                ["role"] = "tool",
                ["tool_call_id"] = callId,
                ["content"] = $"// File {i}\n" + bigPayload
            });
        }

        messages.Add(new JsonObject
        {
            ["role"] = "user",
            ["content"] = "LATEST REQUEST: Now write the unit test for invoice rounding."
        });
        return messages;
    }

    [Theory]
    [InlineData(8192, 2048)]
    [InlineData(32768, 4096)]
    [InlineData(128000, 8192)]
    public async Task CompactAsync_Fits300kSyntheticHistoryIntoTargetWindow_AndKeepsOriginalTask(int contextWindow, int maxOutputTokens)
    {
        var history = BuildSynthetic300kHistory();
        Assert.True(TokenEstimator.EstimateMessages(history) > 250_000);

        bool summarizerCalled = false;
        var result = await ContextBudget.CompactAsync(
            history,
            contextWindow,
            maxOutputTokens,
            keepRecentToolResults: 4,
            summarizeAsync: (transcript, _) =>
            {
                summarizerCalled = true;
                return Task.FromResult("- Inspected src/File0.cs through src/File59.cs.\n- Ready for rounding tests.");
            });

        var budget = ContextBudget.ComputeInputBudget(contextWindow, maxOutputTokens);
        Assert.True(result.Compacted);
        Assert.True(result.FinalTokens <= budget, $"Expected <= {budget} tokens, got {result.FinalTokens}");

        var allText = string.Join("\n", result.Messages.Select(m => m["content"]?.ToString() ?? ""));
        Assert.Contains("SYSTEM PROMPT", allText);
        Assert.Contains("ORIGINAL TASK: Refactor the payment ledger", allText);
        Assert.Contains("LATEST REQUEST: Now write the unit test for invoice rounding.", allText);

        // Verify no orphaned tool messages exist after compaction
        var declaredCallIds = new HashSet<string>();
        foreach (var msg in result.Messages)
        {
            if (msg["tool_calls"] is JsonArray tcs)
            {
                foreach (var tc in tcs)
                {
                    var id = tc?["id"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(id)) declaredCallIds.Add(id);
                }
            }
            if (msg["role"]?.GetValue<string>() == "tool")
            {
                var tid = msg["tool_call_id"]?.GetValue<string>() ?? "";
                Assert.Contains(tid, declaredCallIds);
            }
        }

        if (contextWindow <= 8192)
        {
            Assert.True(summarizerCalled);
        }
    }

    [Fact]
    public async Task CompactAsync_SummarizesMiddleConversation_WhenProseExceeds80PercentBudget()
    {
        var messages = new List<JsonObject>
        {
            new() { ["role"] = "system", ["content"] = "SYSTEM PROMPT" },
            new() { ["role"] = "user", ["content"] = "ORIGINAL TASK: build the sync engine" }
        };
        for (int i = 0; i < 30; i++)
        {
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = $"Step {i}: " + new string('m', 1200) });
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = $"Follow-up {i}: " + new string('u', 1200) });
        }
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = "LATEST QUESTION: what is left?" });

        bool summarizerCalled = false;
        var result = await ContextBudget.CompactAsync(
            messages,
            contextWindow: 16384,
            maxOutputTokens: 2048,
            summarizeAsync: (_, _) =>
            {
                summarizerCalled = true;
                return Task.FromResult("- Completed steps 0..29.\n- Sync engine core ready.");
            });

        Assert.True(result.Compacted);
        Assert.True(summarizerCalled);
        Assert.True(result.FinalTokens <= ContextBudget.ComputeInputBudget(16384, 2048));
        var joined = string.Join("\n", result.Messages.Select(m => m["content"]?.ToString() ?? ""));
        Assert.Contains("ORIGINAL TASK: build the sync engine", joined);
        Assert.Contains("Sync engine core ready.", joined);
        Assert.Contains("LATEST QUESTION: what is left?", joined);
    }

    [Fact]
    public async Task CompactAsync_StubsOlderToolOutputs_WhileKeepingRecentOnesInFull()
    {
        var messages = new List<JsonObject>
        {
            new() { ["role"] = "system", ["content"] = "sys" },
            new() { ["role"] = "user", ["content"] = "task" }
        };

        for (int i = 0; i < 10; i++)
        {
            var id = $"call_{i}";
            messages.Add(new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = "",
                ["tool_calls"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = id,
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = "read_file",
                            ["arguments"] = $"{{\"path\":\"src/Mod{i}.cs\"}}"
                        }
                    }
                }
            });
            messages.Add(new JsonObject
            {
                ["role"] = "tool",
                ["tool_call_id"] = id,
                ["content"] = $"UNIQUE_CONTENT_{i}_" + new string('z', 4000)
            });
        }

        // Choose a window where stubbing older tools alone drops under threshold without needing Pass 2.
        var res = await ContextBudget.CompactAsync(messages, contextWindow: 12000, maxOutputTokens: 2000, keepRecentToolResults: 2);

        Assert.True(res.Compacted);
        var toolMsgs = res.Messages.Where(m => m["role"]?.GetValue<string>() == "tool").ToList();
        Assert.Equal(10, toolMsgs.Count);
        Assert.Contains("omitted", toolMsgs[0]["content"]?.GetValue<string>() ?? "");
        Assert.Contains("read_file src/Mod0.cs", toolMsgs[0]["content"]?.GetValue<string>() ?? "");
        Assert.Contains("UNIQUE_CONTENT_9_", toolMsgs[^1]["content"]?.GetValue<string>() ?? "");
        Assert.Contains("UNIQUE_CONTENT_8_", toolMsgs[^2]["content"]?.GetValue<string>() ?? "");
    }

    [Fact]
    public void TokenEstimator_CountsMultimodalImagesAsFixedCost_NotRawBase64Length()
    {
        var msg = new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = "Describe this image." },
                new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64," + new string('B', 400_000) }
                }
            }
        };

        var est = TokenEstimator.EstimateMessage(msg);
        Assert.InRange(est, 900, 1200);
    }
}
