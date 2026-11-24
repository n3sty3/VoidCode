using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using VoidCode.Agent.Providers;
using VoidCode.Agent.Tools;
using VoidCode.Services;

namespace VoidCode.Agent;

/// <summary>
/// Focused read-only research subagent invoked by the <c>delegate_research</c> tool.
/// Runs up to 6 read-only steps in an isolated context window so broad codebase exploration
/// never bloats the main conversation transcript.
/// </summary>
internal static class ResearchSubagent
{
    private const int MaxSteps = 6;

    internal static async Task<string> RunAsync(
        string question,
        string workspacePath,
        AppSettings settings,
        Action<string, object> emitEvent,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(question))
            return "Error: 'question' is required for delegate_research.";

        var provider = ProviderFactory.Create(settings);
        var readOnlyTools = ToolCatalog.Definitions(planOnly: true);

        // Remove delegate_research and update_plan from subagent tools to prevent
        // recursive nesting or clobbering the main agent's live plan checklist.
        var subTools = new JsonArray();
        foreach (var t in readOnlyTools)
        {
            var n = t?["function"]?["name"]?.GetValue<string>();
            if (!string.Equals(n, "delegate_research", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(n, "update_plan", StringComparison.OrdinalIgnoreCase))
                subTools.Add(t?.DeepClone());
        }

        var messages = new List<JsonObject>
        {
            new()
            {
                ["role"] = "system",
                ["content"] =
                    "You are a focused read-only codebase research subagent inside VoidCode IDE.\n" +
                    $"Workspace: {workspacePath}\n" +
                    "Use glob_files, search_code, search_codebase, list_dir, and read_file to answer the research question accurately.\n" +
                    "Return a concise, factual summary citing exact relative file paths and line numbers. Never speculate about files you did not read."
            },
            new()
            {
                ["role"] = "user",
                ["content"] = question
            }
        };

        for (int step = 1; step <= MaxSteps && !ct.IsCancellationRequested; step++)
        {
            emitEvent("agent_status", new { status = $"Research subagent (step {step}/{MaxSteps})..." });
            var req = new ChatRequest
            {
                Provider = settings.Provider,
                BaseUrl = settings.BaseUrl,
                ApiKey = settings.ApiKey,
                Model = settings.Model,
                Messages = messages,
                Tools = subTools,
                Temperature = 0.1,
                MaxTokens = Math.Min(Math.Max(settings.MaxTokens, 2048), 4096),
                Stream = true,
                ReasoningEffort = "low"
            };

            var content = new StringBuilder();
            var toolCalls = new SortedDictionary<int, (string Id, string Name, StringBuilder Args)>();
            string? streamErr = null;

            await foreach (var evt in provider.StreamAsync(req, ct))
            {
                switch (evt)
                {
                    case ChatContentDelta c:
                        content.Append(c.Text);
                        break;
                    case ChatErrorEvent err:
                        streamErr = err.Message;
                        break;
                    case ChatToolCallDelta tc:
                        if (!toolCalls.TryGetValue(tc.Index, out var entry))
                            entry = ($"sub_{Guid.NewGuid():N}", "", new StringBuilder());
                        if (!string.IsNullOrEmpty(tc.Id)) entry.Id = tc.Id;
                        if (!string.IsNullOrEmpty(tc.Name)) entry.Name += tc.Name;
                        if (!string.IsNullOrEmpty(tc.ArgumentsDelta)) entry.Args.Append(tc.ArgumentsDelta);
                        toolCalls[tc.Index] = entry;
                        break;
                }
                if (streamErr != null) break;
            }

            if (streamErr != null)
                return $"Research subagent error: {streamErr}";

            var assistantMsg = new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = content.ToString()
            };

            if (toolCalls.Count == 0)
            {
                var answer = content.ToString().Trim();
                return string.IsNullOrEmpty(answer) ? "Research subagent returned no findings." : answer;
            }

            var tcArr = new JsonArray();
            foreach (var kv in toolCalls)
            {
                tcArr.Add(new JsonObject
                {
                    ["id"] = kv.Value.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = kv.Value.Name,
                        ["arguments"] = kv.Value.Args.ToString()
                    }
                });
            }
            assistantMsg["tool_calls"] = tcArr;
            messages.Add(assistantMsg);

            // Run read-only subagent tool calls in parallel for speed.
            var tasks = new List<Task<(string Id, string Output)>>();
            foreach (var kv in toolCalls)
            {
                var call = kv.Value;
                tasks.Add(Task.Run(async () =>
                {
                    if (!ToolCatalog.PlanModeTools.Contains(call.Name) ||
                        string.Equals(call.Name, "delegate_research", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(call.Name, "update_plan", StringComparison.OrdinalIgnoreCase))
                    {
                        return (call.Id, $"Blocked: '{call.Name}' is not permitted in read-only research subagent.");
                    }
                    var argsJson = call.Args.Length == 0 ? "{}" : call.Args.ToString();
                    if (ToolExecutor.AccessesOutsideWorkspace(call.Name, argsJson, workspacePath))
                        return (call.Id, "Blocked: research subagent cannot access files outside the workspace.");
                    var res = await ToolExecutor.ExecuteAsync(call.Name, argsJson, workspacePath, (_, _) => { }, null, null, null, false, ct);
                    return (call.Id, res.output);
                }, ct));
            }

            var results = await Task.WhenAll(tasks);
            foreach (var r in results)
            {
                messages.Add(new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = r.Id,
                    ["content"] = r.Output
                });
            }
        }

        return "Research subagent reached its step limit without a final summary. Narrow the question or inspect directly.";
    }
}
