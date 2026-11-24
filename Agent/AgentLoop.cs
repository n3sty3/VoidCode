using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using VoidCode.Agent.Mcp;
using VoidCode.Agent.Providers;
using VoidCode.Agent.Tools;
using VoidCode.Services;

namespace VoidCode.Agent;

/// <summary>
/// The autonomous agent turn loop.
/// Streams through <see cref="IChatProvider"/> (OpenAI-compatible or Anthropic), budgets & compacts
/// Conversation context before every turn, injects project rules, enforces authority modes and
/// Untrusted-web escalation, and records every tool decision to the append-only audit log.
/// </summary>
internal static class AgentLoop
{
    /// <summary>Tool approvals waited on by the loop, keyed by the id the operator is answering.</summary>
    internal static readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> PendingApprovals = new();

    internal static void ResolveToolApproval(string toolId, bool approved)
    {
        if (PendingApprovals.TryRemove(toolId, out var tcs))
        {
            tcs.TrySetResult(approved);
        }
    }

    internal static async Task RunAsync(
        string userPrompt,
        List<JsonObject> conversationHistory,
        string workspacePath,
        AppSettings settings,
        Action<string, object> emitEvent,
        CancellationToken cancellationToken = default,
        JsonObject? editorContext = null,
        string? modeOverride = null)
    {
        if (string.IsNullOrWhiteSpace(settings.BaseUrl) || string.IsNullOrWhiteSpace(settings.Model))
        {
            emitEvent("agent_error", new
            {
                message = "No model is configured. Open Settings -> Provider, pick a provider, save a key and choose a model. Nothing was sent."
            });
            return;
        }

        var provider = ProviderFactory.Create(settings);
        var caps = ProviderCapabilityCache.Shared.Get(settings);
        var turn = TurnTransactionLedger.BeginTurn(workspacePath);
        var isPlanMode = string.Equals(modeOverride ?? settings.AgentMode, "plan", StringComparison.OrdinalIgnoreCase);

        // Discover workspace MCP tools only in trusted workspaces and outside read-only plan mode.
        var mcpTools = !isPlanMode
            ? await McpManager.EnsureWorkspaceToolsAsync(workspacePath, cancellationToken)
            : new List<McpToolInfo>();

        var messages = new List<JsonObject>();

        var systemPrompt = Prompts.SystemPrompt(settings, workspacePath, isPlanMode);

        // Load root AGENTS.md + .voidcode/rules/*.md (capped at 16 KB).
        var projectRules = ContextBuilder.LoadProjectRules(workspacePath);
        if (!string.IsNullOrEmpty(projectRules))
        {
            systemPrompt.Append("\n\n");
            systemPrompt.Append(projectRules);
        }

        var contextBlock = ContextBuilder.BuildEditorContextBlock(editorContext);
        if (!string.IsNullOrEmpty(contextBlock))
        {
            systemPrompt.Append("\n\n");
            systemPrompt.Append(contextBlock);
        }

        messages.Add(new JsonObject
        {
            ["role"] = "system",
            ["content"] = systemPrompt.ToString()
        });

        // Clone history nodes safely
        foreach (var msg in conversationHistory)
        {
            var cloned = JsonNode.Parse(msg.ToJsonString())?.AsObject();
            if (cloned != null) messages.Add(cloned);
        }

        // Automatically inline any [Referenced File: ...] attachments so Turn 1 has immediate context.
        var imageDataUris = new List<string>();
        var enrichedPrompt = ContextBuilder.EnrichPromptWithReferencedFiles(userPrompt, workspacePath, imageDataUris);

        JsonNode userContentNode;
        if (imageDataUris.Count > 0 && settings.ModelSupportsVision)
        {
            var parts = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = enrichedPrompt }
            };
            foreach (var uri in imageDataUris)
            {
                parts.Add(new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject { ["url"] = uri, ["detail"] = "auto" }
                });
            }
            userContentNode = parts;
        }
        else
        {
            userContentNode = JsonValue.Create(enrichedPrompt)!;
        }

        // If the last message in history is already the identical user prompt, replace its content.
        if (messages.Count > 1 &&
            messages[^1]["role"]?.GetValue<string>() == "user" &&
            messages[^1]["content"] is JsonValue lastContent &&
            lastContent.TryGetValue<string>(out var lastUserText) &&
            lastUserText == userPrompt)
        {
            messages[^1]["content"] = userContentNode;
        }
        else
        {
            messages.Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] = userContentNode
            });
        }

        // Strip any inline base64 screenshot markers from prior turns so history stays compact.
        for (int i = 0; i < messages.Count - 1; i++)
        {
            if (messages[i]["content"] is JsonValue histVal &&
                histVal.TryGetValue<string>(out var histStr) &&
                histStr.Contains("[Attached Image: data:image/", StringComparison.Ordinal))
            {
                messages[i]["content"] = ContextBuilder.StripInlineImagePayloads(histStr);
            }
        }

        // MaxAgentTurns <= 0 means "unlimited" (still bounded by a high safety ceiling).
        bool unlimitedTurns = settings.MaxAgentTurns <= 0;
        int maxTurns = unlimitedTurns ? 1000 : Math.Clamp(settings.MaxAgentTurns, 1, 1000);
        int currentTurn = 0;
        var runImages = new List<(string path, string dataUri, string mime)>();
        bool taintedByUntrustedWeb = false;
        var runStopwatch = System.Diagnostics.Stopwatch.StartNew();
        int promptTokens = 0, completionTokens = 0, cachedInputTokens = 0, cacheCreationTokens = 0, assistantChars = 0, toolCallsExecuted = 0;
        bool usageReported = false;

        Task<string> SummarizeForCompactionAsync(string transcript, CancellationToken ct)
        {
            var req = new ChatRequest
            {
                Provider = settings.Provider,
                BaseUrl = settings.BaseUrl,
                ApiKey = settings.ApiKey,
                Model = settings.Model,
                Temperature = 0.1,
                MaxTokens = 800,
                Stream = false,
                Messages = new List<JsonObject>
                {
                    new()
                    {
                        ["role"] = "system",
                        ["content"] = "Summarize this coding-agent conversation excerpt concisely in bullet points. Preserve file paths, changes made, test/build results, errors, and open next steps."
                    },
                    new()
                    {
                        ["role"] = "user",
                        ["content"] = transcript
                    }
                }
            };
            return provider.CompleteAsync(req, ct);
        }

        while (currentTurn < maxTurns && !cancellationToken.IsCancellationRequested)
        {
            currentTurn++;

            // Enforce context-window budgeting & compaction before every turn.
            var compaction = await ContextBudget.CompactAsync(
                messages,
                caps.ContextWindow,
                settings.MaxTokens,
                ContextBudget.DefaultKeepRecentToolResults,
                SummarizeForCompactionAsync,
                cancellationToken);
            if (compaction.Compacted)
            {
                messages = compaction.Messages;
                if (!string.IsNullOrEmpty(compaction.Note))
                {
                    LoggingService.LogInfo(compaction.Note, nameof(AgentLoop));
                    emitEvent("agent_status", new { status = compaction.Note });
                }
            }

            emitEvent("agent_status", new
            {
                status = currentTurn == 1
                    ? "Thinking..."
                    : (unlimitedTurns ? $"Working... (step {currentTurn})" : $"Thinking... (step {currentTurn} of {maxTurns})")
            });

            var toolDefs = ToolCatalog.Definitions(isPlanMode);
            if (mcpTools.Count > 0)
            {
                foreach (var mcpDef in McpManager.ToOpenAiToolDefinitions(mcpTools))
                {
                    if (mcpDef != null) toolDefs.Add(JsonNode.Parse(mcpDef.ToJsonString()));
                }
            }

            var request = new ChatRequest
            {
                Provider = settings.Provider,
                BaseUrl = settings.BaseUrl,
                ApiKey = settings.ApiKey,
                Model = settings.Model,
                Messages = messages,
                Tools = toolDefs,
                Temperature = settings.Temperature,
                MaxTokens = settings.MaxTokens,
                Stream = true
            };

            var contentBuilder = new StringBuilder();
            var reasoningBuilder = new StringBuilder();
            var toolCallsMap = new SortedDictionary<int, StreamedToolCall>();
            var toolCallExtraContent = new Dictionary<int, string>();
            bool startedMessageStream = false;
            bool turnFailed = false;

            await foreach (var evt in provider.StreamAsync(request, cancellationToken))
            {
                switch (evt)
                {
                    case ChatStatusEvent statusEvt:
                        emitEvent("agent_status", new { status = statusEvt.Status });
                        break;

                    case ChatErrorEvent errEvt:
                        emitEvent("agent_error", new { message = errEvt.Message });
                        turnFailed = true;
                        break;

                    case ChatUsageEvent usageEvt:
                        promptTokens += usageEvt.PromptTokens;
                        completionTokens += usageEvt.CompletionTokens;
                        cachedInputTokens += usageEvt.CachedInputTokens;
                        cacheCreationTokens += usageEvt.CacheCreationTokens;
                        usageReported = true;
                        break;

                    case ChatReasoningDelta r:
                        if (!startedMessageStream)
                        {
                            startedMessageStream = true;
                            emitEvent("agent_stream_start", new { });
                        }
                        reasoningBuilder.Append(r.Text);
                        emitEvent("agent_reasoning_delta", new { delta = r.Text });
                        break;

                    case ChatContentDelta c:
                        if (!startedMessageStream)
                        {
                            startedMessageStream = true;
                            emitEvent("agent_stream_start", new { });
                        }
                        contentBuilder.Append(c.Text);
                        emitEvent("agent_stream_delta", new { delta = c.Text });
                        break;

                    case ChatToolCallDelta tc:
                        if (!toolCallsMap.TryGetValue(tc.Index, out var entry))
                        {
                            entry = new StreamedToolCall { Id = $"call_{Guid.NewGuid():N}" };
                            toolCallsMap[tc.Index] = entry;
                        }
                        if (!string.IsNullOrEmpty(tc.Id)) entry.Id = tc.Id;
                        if (!string.IsNullOrEmpty(tc.Name)) entry.Name += tc.Name;
                        if (!string.IsNullOrEmpty(tc.ArgumentsDelta)) entry.Arguments.Append(tc.ArgumentsDelta);
                        if (!string.IsNullOrEmpty(tc.ExtraContentJson)) toolCallExtraContent[tc.Index] = tc.ExtraContentJson;
                        break;
                }

                if (turnFailed) break;
            }

            if (turnFailed) return;

            if (startedMessageStream)
            {
                emitEvent("agent_stream_end", new
                {
                    fullContent = contentBuilder.ToString(),
                    fullReasoning = reasoningBuilder.ToString()
                });
            }

            assistantChars += contentBuilder.Length;

            var assistantMsg = new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = contentBuilder.ToString()
            };

            if (toolCallsMap.Count > 0)
            {
                var tcArray = new JsonArray();
                foreach (var kvp in toolCallsMap)
                {
                    var tcObj = new JsonObject
                    {
                        ["id"] = kvp.Value.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = kvp.Value.Name,
                            ["arguments"] = kvp.Value.Arguments.ToString()
                        }
                    };
                    if (toolCallExtraContent.TryGetValue(kvp.Key, out var extraJson))
                    {
                        try { tcObj["extra_content"] = JsonNode.Parse(extraJson); }
                        catch (Exception ex) { LoggingService.Swallowed(ex, "AgentLoop.ParseToolExtraContent"); }
                    }
                    tcArray.Add(tcObj);
                }
                assistantMsg["tool_calls"] = tcArray;
            }

            messages.Add(assistantMsg);

            if (toolCallsMap.Count == 0)
            {
                var finalText = contentBuilder.ToString();
                if (string.IsNullOrWhiteSpace(finalText) && !startedMessageStream && reasoningBuilder.Length == 0)
                {
                    LoggingService.LogWarning($"Model '{settings.Model}' returned an empty response on turn {currentTurn}.");
                    emitEvent("agent_error", new { message = "The model returned an empty response. Check that the model name is valid, that the provider supports streaming/tools, and that the API key and base URL are correct." });
                    break;
                }
                var completion = usageReported ? completionTokens : Math.Max(completionTokens, assistantChars / 4);
                var cost = CostEstimator.Estimate(settings.Provider, settings.BaseUrl, settings.Model, promptTokens, completion, cachedInputTokens, cacheCreationTokens);
                emitEvent("agent_done", new
                {
                    finalMessage = finalText,
                    steps = currentTurn,
                    toolCalls = toolCallsExecuted,
                    promptTokens,
                    completionTokens = completion,
                    cachedTokens = cachedInputTokens,
                    totalTokens = promptTokens + completion,
                    estimated = !usageReported,
                    estimatedCostUsd = cost.EstimatedCostUsd,
                    cacheSavingsUsd = cost.CacheSavingsUsd,
                    costLabel = cost.FormattedCost,
                    savingsLabel = cost.FormattedSavings,
                    isLocalModel = cost.IsLocal,
                    elapsedMs = runStopwatch.ElapsedMilliseconds,
                    turnId = turn.TurnId,
                    turnSummary = turn.ToSummaryPayload()
                });
                break;
            }

            var authorityMode = AgentPolicy.ResolveAuthorityMode(settings);

            foreach (var kvp in toolCallsMap)
            {
                if (cancellationToken.IsCancellationRequested) break;

                var tc = kvp.Value;
                var toolId = tc.Id;
                var functionName = tc.Name;
                var argumentsRaw = string.IsNullOrWhiteSpace(tc.Arguments.ToString()) ? "{}" : tc.Arguments.ToString();
                toolCallsExecuted++;

                emitEvent("agent_status", new { status = $"Running {functionName}..." });
                emitEvent("agent_tool_start", new { id = toolId, tool = functionName, args = argumentsRaw });

                string toolResultOutput;
                string? checkpointId = null;
                string? modifiedFilePath = null;

                bool blockedByPlan = isPlanMode && !ToolCatalog.PlanModeTools.Contains(functionName);
                bool approved = true;
                bool askedOperator = false;
                bool requiresApproval = !blockedByPlan &&
                    AgentPolicy.RequiresApproval(functionName, argumentsRaw, workspacePath, settings, taintedByUntrustedWeb);

                if (requiresApproval)
                {
                    askedOperator = true;
                    var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    PendingApprovals[toolId] = tcs;

                    emitEvent("agent_status", new { status = $"Awaiting approval for {functionName}..." });
                    emitEvent("agent_tool_approval_needed", new { id = toolId, tool = functionName, args = argumentsRaw });

                    using var reg = cancellationToken.Register(() => tcs.TrySetCanceled());
                    try
                    {
                        approved = await tcs.Task;
                    }
                    catch (OperationCanceledException)
                    {
                        PendingApprovals.TryRemove(toolId, out _);
                        break;
                    }
                }

                if (blockedByPlan)
                {
                    AgentAuditLog.Record(functionName, argumentsRaw, "blocked_plan", "policy", authorityMode, taintedByUntrustedWeb, workspacePath);
                    toolResultOutput = $"Blocked: plan mode is read-only. '{functionName}' is not permitted. Investigate with read-only tools, then present the plan and stop.";
                }
                else if (!approved)
                {
                    AgentAuditLog.Record(functionName, argumentsRaw, "denied", "operator", authorityMode, taintedByUntrustedWeb, workspacePath);
                    toolResultOutput = "Tool execution denied by operator.";
                }
                else
                {
                    AgentAuditLog.Record(
                        functionName,
                        argumentsRaw,
                        askedOperator ? "approved" : "auto_approved",
                        askedOperator ? "operator" : "policy",
                        authorityMode,
                        taintedByUntrustedWeb,
                        workspacePath);

                    try
                    {
                        if (McpManager.IsMcpTool(functionName))
                        {
                            turn.RecordIrreversible(functionName, argumentsRaw);
                            toolResultOutput = await McpManager.ExecuteToolAsync(functionName, argumentsRaw, cancellationToken);
                            taintedByUntrustedWeb = true;
                        }
                        else
                        {
                            var execResult = await ToolExecutor.ExecuteAsync(functionName, argumentsRaw, workspacePath, emitEvent, img => runImages.Add(img), turn);
                            toolResultOutput = execResult.output;
                            checkpointId = execResult.checkpointId;
                            modifiedFilePath = execResult.modifiedFilePath;

                            if (string.Equals(functionName, "web_fetch", StringComparison.OrdinalIgnoreCase))
                            {
                                taintedByUntrustedWeb = true;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        toolResultOutput = $"Tool execution error: {ex.Message}";
                    }
                }

                emitEvent("agent_tool_finish", new
                {
                    id = toolId,
                    tool = functionName,
                    output = toolResultOutput,
                    checkpointId,
                    modifiedFilePath,
                    turnId = turn.TurnId
                });

                messages.Add(new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = toolId,
                    ["content"] = toolResultOutput
                });
            }

            // View_image cannot return pixels through a tool result for most providers, so
            // Attach the collected images as a multimodal user turn for the next step.
            if (runImages.Count > 0)
            {
                var imageParts = new JsonArray
                {
                    new JsonObject { ["type"] = "text", ["text"] = "Images loaded via view_image (now visible to you):" }
                };
                foreach (var img in runImages)
                {
                    imageParts.Add(new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JsonObject { ["url"] = img.dataUri, ["detail"] = "auto" }
                    });
                }
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = imageParts });
                runImages.Clear();
            }
        }

        if (currentTurn >= maxTurns)
        {
            var completion = usageReported ? completionTokens : Math.Max(completionTokens, assistantChars / 4);
            var cost = CostEstimator.Estimate(settings.Provider, settings.BaseUrl, settings.Model, promptTokens, completion, cachedInputTokens, cacheCreationTokens);
            emitEvent("agent_done", new
            {
                finalMessage = unlimitedTurns
                    ? $"Reached the safety limit ({maxTurns} steps)."
                    : $"Reached maximum autonomous turns ({maxTurns}).",
                steps = currentTurn,
                toolCalls = toolCallsExecuted,
                promptTokens,
                completionTokens = completion,
                cachedTokens = cachedInputTokens,
                totalTokens = promptTokens + completion,
                estimated = !usageReported,
                estimatedCostUsd = cost.EstimatedCostUsd,
                cacheSavingsUsd = cost.CacheSavingsUsd,
                costLabel = cost.FormattedCost,
                savingsLabel = cost.FormattedSavings,
                isLocalModel = cost.IsLocal,
                elapsedMs = runStopwatch.ElapsedMilliseconds,
                turnId = turn.TurnId,
                turnSummary = turn.ToSummaryPayload()
            });
        }
    }
}
