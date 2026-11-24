using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using VoidCode.Agent.Mcp;
using VoidCode.Agent.Providers;
using VoidCode.Agent.Tools;
using VoidCode.Services;

namespace VoidCode.Agent;

internal sealed record ApprovalDecision(bool Approved, bool ApproveTurn = false, string? Reason = null);

/// <summary>
/// VoidAgent v2 autonomous turn loop.
/// Streams through <see cref="IChatProvider"/>, executes read-only tool batches concurrently,
/// computes real LCS unified diffs before approval and after edits, enforces authority modes and
/// untrusted-web escalation, guards against stuck retry loops, supports mid-run operator steering,
/// and preserves compact cross-turn tool continuity.
/// </summary>
internal static class AgentLoop
{
    /// <summary>Tool approvals waited on by the loop, keyed by the id the operator is answering.</summary>
    internal static readonly ConcurrentDictionary<string, TaskCompletionSource<ApprovalDecision>> PendingApprovals = new();

    internal static void ResolveToolApproval(string toolId, bool approved, bool approveTurn = false, string? reason = null)
    {
        if (PendingApprovals.TryRemove(toolId, out var tcs))
        {
            tcs.TrySetResult(new ApprovalDecision(approved, approveTurn, reason));
        }
    }

    internal static bool IsParallelizableReadOnlyTool(string toolName, string argsJson, string workspacePath, bool isPlanMode)
    {
        if (McpManager.IsMcpTool(toolName)) return false;
        if (!ToolCatalog.PlanModeTools.Contains(toolName)) return false;
        if (string.Equals(toolName, "delegate_research", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals(toolName, "update_plan", StringComparison.OrdinalIgnoreCase)) return false;
        if (ToolExecutor.AccessesOutsideWorkspace(toolName, argsJson, workspacePath)) return false;
        return true;
    }

    internal static async Task RunAsync(
        string userPrompt,
        List<JsonObject> conversationHistory,
        string workspacePath,
        AppSettings settings,
        Action<string, object> emitEvent,
        CancellationToken cancellationToken = default,
        JsonObject? editorContext = null,
        string? modeOverride = null,
        string? sessionId = null)
    {
        if (string.IsNullOrWhiteSpace(settings.BaseUrl) || string.IsNullOrWhiteSpace(settings.Model))
        {
            emitEvent("agent_error", new
            {
                message = "No model is configured. Open Settings -> Provider, pick a provider, save a key and choose a model. Nothing was sent."
            });
            return;
        }

        AgentSessionMemory.ClearSteers();
        using var sleepGuard = PowerSleepService.Acquire(settings.PreventSleepWhileRunning);

        var activeSettings = settings;
        var provider = ProviderFactory.Create(activeSettings);
        bool usedFallbackModel = false;
        var caps = ProviderCapabilityCache.Shared.Get(activeSettings);
        var turn = TurnTransactionLedger.BeginTurn(workspacePath);
        var isPlanMode = string.Equals(modeOverride ?? settings.AgentMode, "plan", StringComparison.OrdinalIgnoreCase);

        var mcpTools = !isPlanMode
            ? await McpManager.EnsureWorkspaceToolsAsync(workspacePath, cancellationToken)
            : new List<McpToolInfo>();

        var messages = new List<JsonObject>();
        var systemPrompt = Prompts.SystemPrompt(settings, workspacePath, isPlanMode);

        // Keep system prompt byte-stable across turns (only static rules + memories) so
        // Anthropic/OpenAI/Gemini prompt caches hit on follow-up turns!
        var projectRules = ContextBuilder.LoadProjectRules(workspacePath);
        if (!string.IsNullOrEmpty(projectRules))
        {
            systemPrompt.Append("\n\n");
            systemPrompt.Append(projectRules);
        }

        messages.Add(new JsonObject
        {
            ["role"] = "system",
            ["content"] = systemPrompt.ToString()
        });

        // Build clean prior history (excluding the trailing duplicate of userPrompt that the UI
        // pushes into chatHistory right before calling start_agent).
        var priorFrontendHistory = new List<JsonObject>(conversationHistory.Count);
        foreach (var msg in conversationHistory)
        {
            var cloned = JsonNode.Parse(msg.ToJsonString())?.AsObject();
            if (cloned != null) priorFrontendHistory.Add(cloned);
        }
        if (priorFrontendHistory.Count > 0 &&
            string.Equals(priorFrontendHistory[^1]["role"]?.GetValue<string>(), "user", StringComparison.OrdinalIgnoreCase) &&
            priorFrontendHistory[^1]["content"] is JsonValue trailingVal &&
            trailingVal.TryGetValue<string>(out var trailingText) &&
            trailingText == userPrompt)
        {
            priorFrontendHistory.RemoveAt(priorFrontendHistory.Count - 1);
        }

        var sessionKey = AgentSessionMemory.ResolveSessionKey(workspacePath, sessionId);
        bool hasPriorAssistant = priorFrontendHistory.Any(m =>
            string.Equals(m["role"]?.GetValue<string>(), "assistant", StringComparison.OrdinalIgnoreCase));
        bool isFirstTurn = !hasPriorAssistant && priorFrontendHistory.Count == 0;

        if (isFirstTurn)
        {
            // Brand-new chat: never leak turns or tool calls from an earlier session.
            AgentSessionMemory.ClearSession(sessionKey);
        }
        else
        {
            var storedTranscript = AgentSessionMemory.GetOrderedTranscript(sessionKey);
            var frontendUserTurns = AgentSessionMemory.CountUserTurns(priorFrontendHistory);
            var storedUserTurns = AgentSessionMemory.CountUserTurns(storedTranscript);

            // Replay the authoritative chronologically-ordered transcript (including assistant tool_calls,
            // tool results, and preserved reasoning/thinking) whenever it matches the active session's turn count.
            if (storedTranscript.Count > 0 && storedUserTurns == frontendUserTurns)
            {
                messages.AddRange(storedTranscript);
            }
            else
            {
                // Fallback for sessions loaded from disk or rewound in UI.
                messages.AddRange(priorFrontendHistory);
            }
        }

        var imageDataUris = new List<string>();
        var enrichedPrompt = ContextBuilder.EnrichPromptWithReferencedFiles(userPrompt, workspacePath, imageDataUris);

        // Attach Turn-1 Repo Map (now fires reliably on Turn 1 even when UI already pushed the prompt)
        // and per-turn dynamic editor context on the user message to keep the system prompt cache-stable.
        var dynamicPrefix = new StringBuilder();
        if (isFirstTurn)
        {
            var repoMap = RepoMapService.BuildCompactMap(workspacePath);
            if (!string.IsNullOrEmpty(repoMap))
                dynamicPrefix.AppendLine(repoMap).AppendLine();
        }
        var contextBlock = ContextBuilder.BuildEditorContextBlock(editorContext);
        if (!string.IsNullOrEmpty(contextBlock))
            dynamicPrefix.AppendLine(contextBlock).AppendLine();
        if (dynamicPrefix.Length > 0)
            enrichedPrompt = dynamicPrefix + enrichedPrompt;

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

        messages.Add(new JsonObject
        {
            ["role"] = "user",
            ["content"] = userContentNode
        });

        for (int i = 0; i < messages.Count - 1; i++)
        {
            if (messages[i]["content"] is JsonValue histVal &&
                histVal.TryGetValue<string>(out var histStr) &&
                histStr.Contains("[Attached Image: data:image/", StringComparison.Ordinal))
            {
                messages[i]["content"] = ContextBuilder.StripInlineImagePayloads(histStr);
            }
        }

        bool unlimitedTurns = settings.MaxAgentTurns <= 0;
        int maxTurns = unlimitedTurns ? 1000 : Math.Clamp(settings.MaxAgentTurns, 1, 1000);
        int currentTurn = 0;
        var runImages = new ConcurrentBag<(string path, string dataUri, string mime)>();
        bool taintedByUntrustedWeb = false;
        bool turnWideWorkspaceApprove = false;
        var failedCallCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var runStopwatch = System.Diagnostics.Stopwatch.StartNew();
        int promptTokens = 0, completionTokens = 0, cachedInputTokens = 0, cacheCreationTokens = 0, assistantChars = 0, toolCallsExecuted = 0;
        bool usageReported = false;

        Task<string> SummarizeForCompactionAsync(string transcript, CancellationToken ct)
        {
            var useFast = !string.IsNullOrWhiteSpace(settings.FastModel);
            var sumProviderName = useFast && !string.IsNullOrWhiteSpace(settings.FastModelProvider) ? settings.FastModelProvider : activeSettings.Provider;
            var sumBaseUrl = useFast && !string.IsNullOrWhiteSpace(settings.FastModelBaseUrl) ? settings.FastModelBaseUrl : activeSettings.BaseUrl;
            var sumModel = useFast ? settings.FastModel : activeSettings.Model;
            var sumKey = useFast
                ? SettingsService.ResolveApiKey("", sumProviderName, sumBaseUrl)
                : activeSettings.ApiKey;
            var sumProvider = useFast
                ? ProviderFactory.Create(new AppSettings { Provider = sumProviderName, BaseUrl = sumBaseUrl, ApiKey = sumKey, Model = sumModel })
                : provider;

            var req = new ChatRequest
            {
                Provider = sumProviderName,
                BaseUrl = sumBaseUrl,
                ApiKey = sumKey,
                Model = sumModel,
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
            return sumProvider.CompleteAsync(req, ct);
        }

        while (currentTurn < maxTurns && !cancellationToken.IsCancellationRequested)
        {
            currentTurn++;

            // Inject any mid-run operator steering messages before the next model step.
            var steers = AgentSessionMemory.DrainSteers();
            if (steers.Count > 0)
            {
                var combinedSteer = string.Join("\n", steers.Select(s => $"- {s}"));
                messages.Add(new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = $"[Operator Mid-Run Steering Guidance]\n{combinedSteer}"
                });
                emitEvent("agent_status", new { status = "Applying operator guidance..." });
            }

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

            var contentBuilder = new StringBuilder();
            var reasoningBuilder = new StringBuilder();
            var thinkingBlocks = new List<JsonObject>();
            var toolCallsMap = new SortedDictionary<int, StreamedToolCall>();
            var toolCallExtraContent = new Dictionary<int, string>();
            bool startedMessageStream = false;
            bool turnFailed = false;

            for (int attempt = 0; attempt < 2; attempt++)
            {
                var request = new ChatRequest
                {
                    Provider = activeSettings.Provider,
                    BaseUrl = activeSettings.BaseUrl,
                    ApiKey = activeSettings.ApiKey,
                    Model = activeSettings.Model,
                    Messages = messages,
                    Tools = toolDefs,
                    Temperature = activeSettings.Temperature,
                    MaxTokens = activeSettings.MaxTokens,
                    Stream = true,
                    ReasoningEffort = activeSettings.AgentReasoningEffort ?? "medium"
                };

                ChatErrorEvent? pendingError = null;
                await foreach (var evt in provider.StreamAsync(request, cancellationToken))
                {
                    switch (evt)
                    {
                        case ChatStatusEvent statusEvt:
                            emitEvent("agent_status", new { status = statusEvt.Status });
                            break;

                        case ChatErrorEvent errEvt:
                            pendingError = errEvt;
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

                        case ChatThinkingBlockEvent tb:
                            if (tb.Type == "thinking" && !string.IsNullOrEmpty(tb.Signature))
                            {
                                thinkingBlocks.Add(new JsonObject
                                {
                                    ["type"] = "thinking",
                                    ["thinking"] = tb.Thinking ?? "",
                                    ["signature"] = tb.Signature
                                });
                            }
                            else if (tb.Type == "redacted_thinking" && !string.IsNullOrEmpty(tb.Data))
                            {
                                thinkingBlocks.Add(new JsonObject
                                {
                                    ["type"] = "redacted_thinking",
                                    ["data"] = tb.Data
                                });
                            }
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

                    if (pendingError != null) break;
                }

                if (pendingError == null) break;

                // Automatic 429 / 502 / 503 / 529 Fallback: if primary model is rate-limited or overloaded
                // before emitting any tokens, transparently switch to the configured fallback model!
                bool isRetryableFallback = !usedFallbackModel
                    && contentBuilder.Length == 0
                    && toolCallsMap.Count == 0
                    && !string.IsNullOrWhiteSpace(settings.FallbackModel)
                    && (pendingError.StatusCode is 429 or 502 or 503 or 529
                        || pendingError.Message.Contains("429", StringComparison.OrdinalIgnoreCase)
                        || pendingError.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
                        || pendingError.Message.Contains("too many requests", StringComparison.OrdinalIgnoreCase)
                        || pendingError.Message.Contains("overloaded", StringComparison.OrdinalIgnoreCase));

                if (isRetryableFallback)
                {
                    usedFallbackModel = true;
                    var fbProvider = !string.IsNullOrWhiteSpace(settings.FallbackProvider) ? settings.FallbackProvider : settings.Provider;
                    var fbBaseUrl = !string.IsNullOrWhiteSpace(settings.FallbackBaseUrl) ? settings.FallbackBaseUrl : settings.BaseUrl;
                    var fbKey = SettingsService.ResolveApiKey("", fbProvider, fbBaseUrl);
                    activeSettings = new AppSettings
                    {
                        Provider = fbProvider,
                        BaseUrl = fbBaseUrl,
                        ApiKey = fbKey,
                        Model = settings.FallbackModel,
                        Temperature = settings.Temperature,
                        MaxTokens = settings.MaxTokens,
                        AgentReasoningEffort = settings.AgentReasoningEffort,
                        AgentAutoVerify = settings.AgentAutoVerify,
                        AuthorityMode = settings.AuthorityMode,
                        AutoApproveTools = settings.AutoApproveTools
                    };
                    provider = ProviderFactory.Create(activeSettings);
                    caps = ProviderCapabilityCache.Shared.Get(activeSettings);
                    LoggingService.LogWarning($"Primary model '{settings.Model}' hit rate limit/overload; automatically falling back to '{activeSettings.Model}' ({activeSettings.Provider}).", nameof(AgentLoop));
                    emitEvent("agent_status", new { status = $"Rate-limited on {settings.Model} — falling back to {activeSettings.Model}..." });
                    continue;
                }

                emitEvent("agent_error", new { message = pendingError.Message });
                turnFailed = true;
                break;
            }

            if (turnFailed) return;

            if (startedMessageStream)
            {
                emitEvent("agent_stream_end", new
                {
                    fullContent = contentBuilder.ToString(),
                    fullReasoning = reasoningBuilder.ToString(),
                    isIntermediate = toolCallsMap.Count > 0
                });
            }

            assistantChars += contentBuilder.Length;

            var assistantMsg = new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = contentBuilder.ToString()
            };

            // Carry reasoning_content (DeepSeek/Kimi/GLM/MiniMax/Qwen3) and signed thinking blocks (Anthropic)
            // across multi-step tool calls so models retain their exact chain-of-thought and satisfy API validation.
            if (reasoningBuilder.Length > 0)
            {
                assistantMsg["reasoning_content"] = reasoningBuilder.ToString();
            }
            if (thinkingBlocks.Count > 0)
            {
                var tbArray = new JsonArray();
                foreach (var tb in thinkingBlocks) tbArray.Add(tb);
                assistantMsg["_thinking_blocks"] = tbArray;
            }

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
                AgentSessionMemory.SaveOrderedTranscript(sessionKey, messages.Skip(1).ToList());
                EmitDone(finalText);
                break;
            }

            var authorityMode = AgentPolicy.ResolveAuthorityMode(settings);
            var orderedCalls = toolCallsMap.Values.ToList();
            var stepToolResultMsgs = new List<JsonObject>();
            int cursor = 0;

            while (cursor < orderedCalls.Count && !cancellationToken.IsCancellationRequested)
            {
                // Collect a contiguous batch of parallelizable read-only tool calls.
                var parallelBatch = new List<(StreamedToolCall Call, string ArgsRaw)>();
                while (cursor < orderedCalls.Count)
                {
                    var candidate = orderedCalls[cursor];
                    var candidateArgs = string.IsNullOrWhiteSpace(candidate.Arguments.ToString()) ? "{}" : candidate.Arguments.ToString();
                    if (IsParallelizableReadOnlyTool(candidate.Name, candidateArgs, workspacePath, isPlanMode))
                    {
                        parallelBatch.Add((candidate, candidateArgs));
                        cursor++;
                    }
                    else
                    {
                        break;
                    }
                }

                if (parallelBatch.Count > 1)
                {
                    emitEvent("agent_status", new { status = $"Exploring {parallelBatch.Count} targets in parallel..." });
                    foreach (var (call, argsRaw) in parallelBatch)
                    {
                        toolCallsExecuted++;
                        emitEvent("agent_tool_start", new { id = call.Id, tool = call.Name, args = argsRaw });
                    }

                    var batchTasks = parallelBatch.Select(item => Task.Run(async () =>
                    {
                        AgentAuditLog.Record(item.Call.Name, item.ArgsRaw, "auto_approved", "policy", authorityMode, taintedByUntrustedWeb, workspacePath);
                        string output;
                        try
                        {
                            var exec = await ToolExecutor.ExecuteAsync(
                                item.Call.Name,
                                item.ArgsRaw,
                                workspacePath,
                                emitEvent,
                                img => runImages.Add(img),
                                turn,
                                settings,
                                settings.AgentAutoVerify,
                                cancellationToken);
                            output = exec.output;
                        }
                        catch (Exception ex)
                        {
                            output = $"Tool execution error: {ex.Message}";
                        }

                        RecordToolTrace(workspacePath, item.Call.Name, item.ArgsRaw, output);
                        emitEvent("agent_tool_finish", new
                        {
                            id = item.Call.Id,
                            tool = item.Call.Name,
                            output,
                            checkpointId = (string?)null,
                            modifiedFilePath = (string?)null,
                            turnId = turn.TurnId
                        });
                        return (item.Call.Id, Output: output);
                    }, cancellationToken)).ToArray();

                    var batchResults = await Task.WhenAll(batchTasks);
                    foreach (var br in batchResults)
                    {
                        var trMsg = new JsonObject
                        {
                            ["role"] = "tool",
                            ["tool_call_id"] = br.Id,
                            ["content"] = br.Output
                        };
                        messages.Add(trMsg);
                        stepToolResultMsgs.Add(trMsg);
                    }
                    continue;
                }

                // Single read-only call or mutating/approval-gated call: execute cleanly in order.
                StreamedToolCall tc;
                string argumentsRaw;
                if (parallelBatch.Count == 1)
                {
                    tc = parallelBatch[0].Call;
                    argumentsRaw = parallelBatch[0].ArgsRaw;
                }
                else
                {
                    tc = orderedCalls[cursor++];
                    argumentsRaw = string.IsNullOrWhiteSpace(tc.Arguments.ToString()) ? "{}" : tc.Arguments.ToString();
                }

                var toolId = tc.Id;
                var functionName = tc.Name;
                toolCallsExecuted++;

                var diffPreview = UnifiedDiffService.BuildPreviewForTool(functionName, argumentsRaw, workspacePath);

                emitEvent("agent_status", new { status = $"Running {functionName}..." });
                emitEvent("agent_tool_start", new
                {
                    id = toolId,
                    tool = functionName,
                    args = argumentsRaw,
                    diffPreview = diffPreview?.DiffText,
                    addedLines = diffPreview?.Added ?? 0,
                    removedLines = diffPreview?.Removed ?? 0,
                    targetPath = diffPreview?.TargetPath
                });

                string toolResultOutput;
                string? checkpointId = null;
                string? modifiedFilePath = null;

                bool blockedByPlan = isPlanMode && !ToolCatalog.PlanModeTools.Contains(functionName);
                var decision = new ApprovalDecision(true);
                bool askedOperator = false;
                bool requiresApproval = !blockedByPlan &&
                    AgentPolicy.RequiresApproval(functionName, argumentsRaw, workspacePath, settings, taintedByUntrustedWeb);

                if (requiresApproval && turnWideWorkspaceApprove && !taintedByUntrustedWeb && !WorkspaceTrustService.ForcesReview &&
                    functionName is "write_file" or "patch_file" or "multi_patch_file" &&
                    AgentPolicy.AreAllMutationPathsInsideWorkspace(functionName, argumentsRaw, workspacePath))
                {
                    requiresApproval = false;
                }

                if (requiresApproval)
                {
                    askedOperator = true;
                    var tcs = new TaskCompletionSource<ApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
                    PendingApprovals[toolId] = tcs;

                    emitEvent("agent_status", new { status = $"Awaiting approval for {functionName}..." });
                    emitEvent("agent_tool_approval_needed", new
                    {
                        id = toolId,
                        tool = functionName,
                        args = argumentsRaw,
                        diffPreview = diffPreview?.DiffText,
                        addedLines = diffPreview?.Added ?? 0,
                        removedLines = diffPreview?.Removed ?? 0,
                        targetPath = diffPreview?.TargetPath
                    });

                    using var reg = cancellationToken.Register(() => tcs.TrySetCanceled());
                    try
                    {
                        decision = await tcs.Task;
                        if (decision.Approved && decision.ApproveTurn)
                            turnWideWorkspaceApprove = true;
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
                else if (!decision.Approved)
                {
                    AgentAuditLog.Record(functionName, argumentsRaw, "denied", "operator", authorityMode, taintedByUntrustedWeb, workspacePath);
                    toolResultOutput = string.IsNullOrWhiteSpace(decision.Reason)
                        ? "Tool execution denied by operator."
                        : $"Tool execution denied by operator. Operator feedback: {decision.Reason.Trim()}";
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
                            var execResult = await ToolExecutor.ExecuteAsync(
                                functionName,
                                argumentsRaw,
                                workspacePath,
                                emitEvent,
                                img => runImages.Add(img),
                                turn,
                                settings,
                                settings.AgentAutoVerify,
                                cancellationToken,
                                toolId);
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

                // Stuck-loop guard: if the exact same failing call repeats 2+ times, inject a clear recovery hint.
                var isErrorOut = toolResultOutput.StartsWith("Error:", StringComparison.OrdinalIgnoreCase)
                    || toolResultOutput.StartsWith("Tool execution error:", StringComparison.OrdinalIgnoreCase);
                if (isErrorOut)
                {
                    var callSig = $"{functionName}:{argumentsRaw.Length}:{argumentsRaw.GetHashCode(StringComparison.Ordinal)}";
                    failedCallCounts.TryGetValue(callSig, out var fails);
                    fails++;
                    failedCallCounts[callSig] = fails;
                    if (fails >= 2)
                    {
                        toolResultOutput += "\n\n[Loop Guard]: You have attempted this exact failing call multiple times. Stop retrying the same arguments — call read_file or search_code to re-check the real file contents and adjust your strategy.";
                    }
                }

                RecordToolTrace(workspacePath, functionName, argumentsRaw, toolResultOutput);

                emitEvent("agent_tool_finish", new
                {
                    id = toolId,
                    tool = functionName,
                    output = toolResultOutput,
                    checkpointId,
                    modifiedFilePath,
                    turnId = turn.TurnId,
                    diffPreview = diffPreview?.DiffText,
                    addedLines = diffPreview?.Added ?? 0,
                    removedLines = diffPreview?.Removed ?? 0
                });

                var singleToolMsg = new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = toolId,
                    ["content"] = toolResultOutput
                };
                messages.Add(singleToolMsg);
                stepToolResultMsgs.Add(singleToolMsg);
            }

            if (stepToolResultMsgs.Count > 0)
            {
                AgentSessionMemory.SaveOrderedTranscript(sessionKey, messages.Skip(1).ToList());
            }

            if (!runImages.IsEmpty)
            {
                var imageParts = new JsonArray
                {
                    new JsonObject { ["type"] = "text", ["text"] = "Images loaded via view_image (now visible to you):" }
                };
                while (runImages.TryTake(out var img))
                {
                    imageParts.Add(new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JsonObject { ["url"] = img.dataUri, ["detail"] = "auto" }
                    });
                }
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = imageParts });
            }
        }

        if (currentTurn >= maxTurns)
        {
            AgentSessionMemory.SaveOrderedTranscript(sessionKey, messages.Skip(1).ToList());
            EmitDone($"Reached the {maxTurns}-step safety limit. Review the completed work above or raise Max Agent Steps in Settings (set to 0 for unlimited) to continue.");
        }

        void EmitDone(string finalMessage)
        {
            var completion = usageReported ? completionTokens : Math.Max(completionTokens, assistantChars / 4);
            var cost = CostEstimator.Estimate(settings.Provider, settings.BaseUrl, settings.Model, promptTokens, completion, cachedInputTokens, cacheCreationTokens);
            emitEvent("agent_done", new
            {
                finalMessage,
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

    private static void RecordToolTrace(string workspacePath, string toolName, string argsRaw, string output)
    {
        try
        {
            var args = JsonNode.Parse(argsRaw);
            var target = args?["path"]?.GetValue<string>()
                ?? args?["pattern"]?.GetValue<string>()
                ?? args?["command"]?.GetValue<string>()
                ?? args?["question"]?.GetValue<string>()
                ?? args?["url"]?.GetValue<string>()
                ?? "";
            if (target.Length > 80) target = target[..80] + "…";
            var ok = !output.StartsWith("Error:", StringComparison.OrdinalIgnoreCase)
                && !output.StartsWith("Tool execution error:", StringComparison.OrdinalIgnoreCase);
            AgentSessionMemory.RecordAction(workspacePath, $"{toolName}({target}) -> {(ok ? "ok" : "error")}");
        }
        catch (Exception ex)
        {
            LoggingService.Swallowed(ex, "AgentLoop.RecordToolTrace");
        }
    }
}
